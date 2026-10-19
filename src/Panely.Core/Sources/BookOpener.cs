using System.Diagnostics;
using Panely.Core.Abstractions;
using Panely.Core.Archives;
using Panely.Core.Cache;
using Panely.Core.Localization;
using Panely.Core.State;
using Panely.Domain.Model;

namespace Panely.Core.Sources;

/// <summary>
/// 소스 해석·형식 판별·폴더/겉포장/시리즈·중첩 추출·쪽 목록(04-files.md).
/// 평평한 아카이브는 entry 단위로 읽고, 중첩 아카이브나 solid RAR은 해제 캐시에 한 번 풀어서 읽는다.
/// 실패하면 연 reader/lease를 모두 닫아 부분 상태를 남기지 않는다.
/// </summary>
public sealed class BookOpener : IBookOpener
{
    private readonly IStateStore _state;
    private readonly ExtractionCache _cache;
    private readonly IDiagnostics _diagnostics;
    private readonly SafeExtractor _extractor;

    public BookOpener(IStateStore state, ExtractionCache cache, IDiagnostics diagnostics)
        : this(state, cache, diagnostics, ExtractionLimits.Default) { }

    public BookOpener(IStateStore state, ExtractionCache cache, IDiagnostics diagnostics, ExtractionLimits limits)
    {
        _state = state;
        _cache = cache;
        _diagnostics = diagnostics;
        _extractor = new SafeExtractor(limits, m => diagnostics.Log(LogLevel.Warning, LogCategory.Load, m));
    }

    public async Task<OpenedBook> OpenAsync(OpenRequest request, IProgress<LoadStage>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var fileName = Path.GetFileName(request.Path.TrimEnd('\\', '/'));
        var ext = SupportedFormats.ExtensionOf(request.Path);
        var kind = Directory.Exists(request.Path) ? "folder" : request.Intent.ToString();
        try
        {
            var book = await Task.Run(() => OpenCoreAsync(request, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            _diagnostics.RecordLoad(new LoadEvent(DateTimeOffset.UtcNow, kind, fileName, ext, "success", sw.Elapsed));
            return book;
        }
        catch (OperationCanceledException)
        {
            _diagnostics.RecordLoad(new LoadEvent(DateTimeOffset.UtcNow, kind, fileName, ext, "cancelled", sw.Elapsed));
            throw;
        }
        catch (BookOpenException ex)
        {
            _diagnostics.RecordLoad(new LoadEvent(DateTimeOffset.UtcNow, kind, fileName, ext, ex.Kind.ToString(), sw.Elapsed));
            throw;
        }
        catch (Exception ex)
        {
            var mapped = Map(ex, request.Path);
            _diagnostics.RecordLoad(new LoadEvent(DateTimeOffset.UtcNow, kind, fileName, ext, mapped.Kind.ToString(), sw.Elapsed));
            _diagnostics.Log(LogLevel.Error, LogCategory.Load, $"Open failed: {ex.GetType().Name}: {ex.Message}");
            throw mapped;
        }
    }

    private static BookOpenException Map(Exception ex, string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return ex switch
        {
            UnauthorizedAccessException => new BookOpenException(OpenErrorKind.AccessDenied, Loc.T("Panely doesn't have permission to read “%@”.", name), ex),
            FileNotFoundException or DirectoryNotFoundException => new BookOpenException(ClassifyMissing(path), MissingMessage(ClassifyMissing(path), OpenIntent.Normal), ex),
            IOException io when IsTransient(io) => new BookOpenException(OpenErrorKind.TemporarilyUnavailable, Loc.T("This book is currently unavailable. Reconnect its drive or restore the file."), ex),
            IOException => new BookOpenException(OpenErrorKind.Unknown, Loc.T("Failed to load %@", name), ex),
            _ => new BookOpenException(OpenErrorKind.Unknown, Loc.T("Failed to load %@", name), ex),
        };
    }

    private static bool IsTransient(IOException io)
    {
        var code = io.HResult & 0xFFFF;
        // ERROR_NOT_READY(21), ERROR_BAD_NETPATH(53), ERROR_NETNAME_DELETED(64), ERROR_BAD_NET_NAME(67), ERROR_DEV_NOT_EXIST(55), ERROR_UNEXP_NET_ERR(59)
        return code is 21 or 53 or 55 or 59 or 64 or 67;
    }

    /// <summary>경로가 없을 때: 드라이브/네트워크 root가 없으면 일시 불가, 아니면 없음.</summary>
    public static OpenErrorKind ClassifyMissing(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return OpenErrorKind.NotFound;
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return Directory.Exists(root) ? OpenErrorKind.NotFound : OpenErrorKind.TemporarilyUnavailable;
            var drive = new DriveInfo(root);
            return drive.IsReady ? OpenErrorKind.NotFound : OpenErrorKind.TemporarilyUnavailable;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return OpenErrorKind.TemporarilyUnavailable;
        }
    }

    private static string MissingMessage(OpenErrorKind kind, OpenIntent intent)
    {
        if (kind == OpenErrorKind.TemporarilyUnavailable)
            return Loc.T("This book is currently unavailable. Reconnect its drive or restore the file.");
        return intent switch
        {
            OpenIntent.Recent => Loc.T("This recent book can no longer be opened."),
            OpenIntent.Bookmark => Loc.T("This bookmarked book can no longer be opened."),
            OpenIntent.Continue => Loc.T("The saved book is no longer available in this source."),
            _ => Loc.T("This file or folder can't be found."),
        };
    }

    private static bool StrictInnerPath(OpenIntent intent) => intent is OpenIntent.Continue or OpenIntent.Bookmark;

    private static BookOpenException InnerMissing()
        => new(OpenErrorKind.InnerPathMissing, Loc.T("The saved book is no longer available in this source."));

    private async Task<OpenedBook> OpenCoreAsync(OpenRequest request, IProgress<LoadStage>? progress, CancellationToken ct)
    {
        progress?.Report(LoadStage.Opening);
        var path = StateStore.NormalizePath(request.Path);
        ct.ThrowIfCancellationRequested();

        if (Directory.Exists(path)) return await OpenFolderAsync(path, request, progress, ct).ConfigureAwait(false);
        if (File.Exists(path))
        {
            if (SupportedFormats.IsImage(path))
            {
                // 이미지 파일을 직접 열면 그 폴더를 책으로 연다
                var folder = Path.GetDirectoryName(path)!;
                return await OpenFolderAsync(folder, request with { Path = folder, InnerPath = null }, progress, ct).ConfigureAwait(false);
            }
            var format = ArchiveSniffer.DetectFile(path);
            if (format == ArchiveFormat.Unknown)
                throw new BookOpenException(OpenErrorKind.Unsupported, Loc.T("Unsupported file type: %@", Path.GetFileName(path)));
            return await OpenArchiveAsync(path, format, request, null, progress, ct).ConfigureAwait(false);
        }

        var missing = ClassifyMissing(path);
        throw new BookOpenException(missing, MissingMessage(missing, request.Intent));
    }

    // ───────────────────────── 폴더 ─────────────────────────

    private async Task<OpenedBook> OpenFolderAsync(string path, OpenRequest request, IProgress<LoadStage>? progress, CancellationToken ct)
    {
        var start = path;
        if (BookKeys.NormalizeInnerPath(request.InnerPath) is { } inner)
        {
            var safe = EntryPathGuard.Sanitize(inner);
            var target = safe is null ? null : EntryPathGuard.ResolveDestination(path, safe);
            if (target is not null && (Directory.Exists(target) || File.Exists(target))) start = target;
            else if (StrictInnerPath(request.Intent)) throw InnerMissing();
        }

        progress?.Report(LoadStage.Loading);
        if (File.Exists(start))
        {
            var fmt = ArchiveSniffer.DetectFile(start);
            return await OpenArchiveAsync(start, fmt, request with { Path = start, InnerPath = null }, null, progress, ct).ConfigureAwait(false);
        }

        var resolution = FolderAnalyzer.Resolve(start, cancellationToken: ct);
        ct.ThrowIfCancellationRequested();

        VolumeContext volumes = resolution.SeriesEntries is { } series
            ? new VolumeContext(series, resolution.SeriesDirectory!, resolution.SeriesEntry!)
            : VolumeContext.FromSiblings(start);

        if (resolution.IsArchive)
        {
            var fmt = ArchiveSniffer.DetectFile(resolution.BookPath);
            if (fmt == ArchiveFormat.Unknown)
                throw new BookOpenException(OpenErrorKind.Unsupported, Loc.T("Unsupported file type: %@", Path.GetFileName(resolution.BookPath)));
            return await OpenArchiveAsync(resolution.BookPath, fmt, request with { Path = resolution.BookPath, InnerPath = null }, volumes, progress, ct)
                .ConfigureAwait(false);
        }

        return BuildFolderBook(resolution.BookPath, resolution.BookAnalysis, volumes, progress, ct);
    }

    private OpenedBook BuildFolderBook(string bookFolder, FolderAnalysis? analysis, VolumeContext volumes, IProgress<LoadStage>? progress, CancellationToken ct)
    {
        analysis ??= FolderAnalyzer.Analyze(bookFolder);
        if (!analysis.HasDirectImages) throw new BookOpenException(OpenErrorKind.Empty, Loc.T("Folder is empty or has no supported content"));
        var title = Path.GetFileName(bookFolder.TrimEnd('\\'));
        if (string.IsNullOrEmpty(title)) title = bookFolder;
        var source = _state.GetOrCreateSource(bookFolder, SourceKind.Folder, title);
        var bookRef = new BookRef(source.Id, null);
        var pages = new List<PageEntry>(analysis.Images.Count);
        foreach (var info in analysis.Images)
        {
            ct.ThrowIfCancellationRequested();
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            var rel = info.Name;
            pages.Add(new PageEntry
            {
                Index = pages.Count,
                RelativePath = rel,
                FileName = rel,
                Extension = SupportedFormats.ExtensionOf(rel),
                PageId = PageIds.Make(bookRef.Key, rel, $"{info.Length:x}-{mtime:x}"),
                Length = info.Length,
            });
        }
        progress?.Report(LoadStage.Building);
        var seriesDir = volumes.Entries.Count >= 2 ? volumes.Directory : Path.GetDirectoryName(bookFolder) ?? bookFolder;
        var (list, index) = volumes.ToVolumeEntries();
        return new OpenedBook
        {
            Source = source,
            Book = bookRef,
            SeriesKey = SeriesKeyForDirectory(seriesDir, source.Id),
            Title = title,
            Pages = pages,
            Data = new FolderPageSource(bookFolder),
            Volumes = list,
            VolumeIndex = index,
            IsNestedCacheBook = false,
            WatchPath = bookFolder,
            DisplayPath = title,
            LibraryPath = bookFolder,
        };
    }

    private string SeriesKeyForDirectory(string? directory, Guid fallback)
    {
        if (string.IsNullOrEmpty(directory)) return BookKeys.SeriesKey(fallback.ToString("D"));
        var name = Path.GetFileName(directory.TrimEnd('\\'));
        var series = _state.GetOrCreateSource(directory, SourceKind.Folder, string.IsNullOrEmpty(name) ? directory : name);
        return BookKeys.SeriesKey(series.Id.ToString("D"));
    }

    // ───────────────────────── 아카이브 ─────────────────────────

    private async Task<OpenedBook> OpenArchiveAsync(string archivePath, ArchiveFormat format, OpenRequest request, VolumeContext? volumes,
        IProgress<LoadStage>? progress, CancellationToken ct)
    {
        progress?.Report(LoadStage.Analyzing);
        if (format == ArchiveFormat.Unknown)
            throw new BookOpenException(OpenErrorKind.Unsupported, Loc.T("Unsupported file type: %@", Path.GetFileName(archivePath)));

        IArchiveReader reader;
        try
        {
            reader = ArchiveReaderFactory.Open(archivePath, format);
        }
        catch (InvalidDataException ex)
        {
            throw new BookOpenException(OpenErrorKind.Corrupt, Loc.T("The archive could not be opened."), ex);
        }

        var transferred = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            var visible = reader.Entries.Where(e => !SupportedFormats.IsHiddenRelativePath(e.Path)).ToList();
            var images = visible.Where(e => SupportedFormats.IsImage(e.Path)).ToList();
            var nested = visible.Where(e => SupportedFormats.IsArchive(e.Path)).ToList();
            if (images.Count > 0 && images.All(e => e.IsEncrypted) || (images.Count == 0 && nested.Count == 0 && visible.Count > 0 && visible.All(e => e.IsEncrypted)))
                throw new BookOpenException(OpenErrorKind.Encrypted, Loc.T("Password-protected archives are not supported."));

            var info = new FileInfo(archivePath);
            var outerSig = $"{info.Length:x}-{new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds():x}";
            var title = Path.GetFileNameWithoutExtension(archivePath);
            var source = _state.GetOrCreateSource(archivePath, SourceKind.Archive, title);
            volumes ??= VolumeContext.FromSiblings(archivePath);

            if (nested.Count > 0)
            {
                transferred = true; // reader는 아래에서 닫는다
                return await OpenNestedAsync(reader, archivePath, source, outerSig, request, volumes, progress, ct).ConfigureAwait(false);
            }

            if (images.Count == 0)
                throw new BookOpenException(OpenErrorKind.Empty, Loc.T("No images found"));

            // innerPath가 평평한 아카이브 안의 폴더를 가리키면 그 폴더의 이미지만
            string? bookInner = null;
            var selected = images;
            if (BookKeys.NormalizeInnerPath(request.InnerPath) is { } inner)
            {
                var prefix = inner + "/";
                var subset = images.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (subset.Count > 0) { selected = subset; bookInner = inner; }
                else if (StrictInnerPath(request.Intent)) throw InnerMissing();
            }

            var cmp = FolderAnalyzer.Comparer;
            selected.Sort((a, b) => cmp.Compare(a.Path, b.Path));
            var bookRef = new BookRef(source.Id, bookInner);

            IPageSource data;
            IDisposable? lease = null;
            if (reader.IsSolid)
            {
                // solid RAR: 임의 접근이 O(n)이라 캐시에 한 번 전체 해제해 디스크에서 읽는다
                progress?.Report(LoadStage.Extracting);
                var key = ExtractionCache.ComputeKey(archivePath);
                var cacheLease = await _cache.GetOrCreateAsync(key, (content, token) => _extractor.ExtractAll(reader, content, expandNested: false, token), ct)
                    .ConfigureAwait(false);
                reader.Dispose();
                transferred = true;
                lease = cacheLease;
                data = new FolderPageSource(cacheLease.ContentPath);
                selected = selected.Where(e => EntryPathGuard.Sanitize(e.Path) is { } safe && File.Exists(Path.Combine(cacheLease.ContentPath, safe))).ToList();
                if (selected.Count == 0)
                {
                    cacheLease.Dispose();
                    throw new BookOpenException(OpenErrorKind.Empty, Loc.T("No images found"));
                }
            }
            else
            {
                data = new ArchivePageSource(reader);
                transferred = true;
            }

            progress?.Report(LoadStage.Loading);
            var pages = new List<PageEntry>(selected.Count);
            foreach (var e in selected)
            {
                var mtime = e.Modified?.ToUnixTimeMilliseconds() ?? 0;
                var rel = reader.IsSolid ? EntryPathGuard.Sanitize(e.Path)! : e.Path;
                pages.Add(new PageEntry
                {
                    Index = pages.Count,
                    RelativePath = rel,
                    FileName = Path.GetFileName(e.Path),
                    Extension = SupportedFormats.ExtensionOf(e.Path),
                    PageId = PageIds.Make(bookRef.Key, e.Path, $"{e.Size:x}-{mtime:x}-{outerSig}"),
                    Length = e.Size,
                });
            }

            progress?.Report(LoadStage.Building);
            var seriesDir = volumes.Entries.Count >= 2 ? volumes.Directory : Path.GetDirectoryName(archivePath);
            var (list, index) = volumes.ToVolumeEntries();
            var bookTitle = bookInner is null ? title : LastSegment(bookInner);
            return new OpenedBook
            {
                Source = source,
                Book = bookRef,
                SeriesKey = SeriesKeyForDirectory(seriesDir, source.Id),
                Title = bookTitle,
                Pages = pages,
                Data = data,
                Volumes = list,
                VolumeIndex = index,
                IsNestedCacheBook = false,
                WatchPath = archivePath,
                DisplayPath = bookInner is null ? Path.GetFileName(archivePath) : $"{Path.GetFileName(archivePath)} › {bookInner}",
                LibraryPath = archivePath,
                Lease = lease,
            };
        }
        catch
        {
            if (!transferred) reader.Dispose();
            throw;
        }
    }

    private async Task<OpenedBook> OpenNestedAsync(IArchiveReader reader, string archivePath, SourceRef source, string outerSig, OpenRequest request,
        VolumeContext outerVolumes, IProgress<LoadStage>? progress, CancellationToken ct)
    {
        CacheLease lease;
        try
        {
            progress?.Report(LoadStage.Extracting);
            var key = ExtractionCache.ComputeKey(archivePath);
            lease = await _cache.GetOrCreateAsync(key, (content, token) => _extractor.ExtractAll(reader, content, expandNested: true, token), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            reader.Dispose();
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var root = lease.ContentPath;
            var start = root;
            var innerRequested = false;
            if (BookKeys.NormalizeInnerPath(request.InnerPath) is { } inner)
            {
                var safe = EntryPathGuard.Sanitize(inner);
                var target = safe is null ? null : EntryPathGuard.ResolveDestination(root, safe);
                if (target is not null && Directory.Exists(target)) { start = target; innerRequested = true; }
                else if (StrictInnerPath(request.Intent)) throw InnerMissing();
            }

            progress?.Report(LoadStage.Loading);
            var resolution = FolderAnalyzer.Resolve(start, cancellationToken: ct);
            if (resolution.IsArchive)
                throw new BookOpenException(OpenErrorKind.Empty, Loc.T("Folder is empty or has no supported content"));
            var bookFolder = resolution.BookPath;
            var bookInner = RelativeInner(root, bookFolder);

            // 권 목록: 내려가며 찾은 다권 레벨 → 대상에서 root까지 올라가며 가장 가까운 다권 레벨 → 바깥 목록
            VolumeContext volumes;
            var nestedVolumes = true;
            if (resolution.SeriesEntries is { } series)
            {
                volumes = new VolumeContext(series, resolution.SeriesDirectory!, resolution.SeriesEntry!);
            }
            else
            {
                var climbFrom = innerRequested ? start : bookFolder;
                var sib = !string.Equals(climbFrom.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    ? FolderAnalyzer.Siblings(climbFrom, stopAt: root)
                    : default;
                if (sib.Entries is { Count: >= 2 })
                {
                    volumes = new VolumeContext(sib.Entries, sib.Directory, sib.Entry);
                }
                else
                {
                    volumes = outerVolumes;
                    nestedVolumes = false;
                }
            }

            var bookRef = new BookRef(source.Id, bookInner);
            var analysis = resolution.BookAnalysis ?? FolderAnalyzer.Analyze(bookFolder);
            var pages = new List<PageEntry>(analysis.Images.Count);
            foreach (var fi in analysis.Images)
            {
                ct.ThrowIfCancellationRequested();
                var rel = fi.Name;
                var entryPath = bookInner is null ? rel : $"{bookInner}/{rel}";
                pages.Add(new PageEntry
                {
                    Index = pages.Count,
                    RelativePath = rel,
                    FileName = rel,
                    Extension = SupportedFormats.ExtensionOf(rel),
                    // 재해제에도 유지되도록 해제 시각 대신 크기와 바깥 아카이브 signature 사용
                    PageId = PageIds.Make(bookRef.Key, entryPath, $"{fi.Length:x}-{outerSig}"),
                    Length = fi.Length,
                });
            }
            if (pages.Count == 0) throw new BookOpenException(OpenErrorKind.Empty, Loc.T("No images found"));

            progress?.Report(LoadStage.Building);
            List<VolumeEntry> list;
            int index;
            if (nestedVolumes)
            {
                list = volumes.Entries.Select(e => new VolumeEntry
                {
                    DisplayName = DisplayName(e),
                    Path = archivePath,
                    InnerPath = RelativeInner(root, e),
                    IsArchive = SupportedFormats.IsArchive(e),
                    IsNested = true,
                }).ToList();
                index = IndexOf(volumes.Entries, volumes.Entry);
            }
            else
            {
                (list, index) = volumes.ToVolumeEntries();
            }

            var title = bookInner is null ? Path.GetFileNameWithoutExtension(archivePath) : DisplayName(bookFolder);
            return new OpenedBook
            {
                Source = source,
                Book = bookRef,
                SeriesKey = BookKeys.SeriesKey(source.Id.ToString("D")),
                Title = title,
                Pages = pages,
                Data = new FolderPageSource(bookFolder),
                Volumes = list,
                VolumeIndex = index,
                IsNestedCacheBook = true,
                WatchPath = archivePath,
                DisplayPath = bookInner is null ? Path.GetFileName(archivePath) : $"{Path.GetFileName(archivePath)} › {bookInner}",
                LibraryPath = archivePath,
                Lease = lease,
            };
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static string? RelativeInner(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
        return rel is "." or "" ? null : BookKeys.NormalizeInnerPath(rel);
    }

    private static string LastSegment(string innerPath)
    {
        var i = innerPath.LastIndexOf('/');
        return i >= 0 ? innerPath[(i + 1)..] : innerPath;
    }

    internal static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return SupportedFormats.IsArchive(name) ? Path.GetFileNameWithoutExtension(name) : name;
    }

    private static int IndexOf(IReadOnlyList<string> entries, string entry)
    {
        for (var i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].TrimEnd('\\'), entry.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>권 목록 후보(실제 경로들)와 이 책이 속한 항목.</summary>
    private sealed record VolumeContext(IReadOnlyList<string> Entries, string Directory, string Entry)
    {
        public static VolumeContext FromSiblings(string path)
        {
            try
            {
                var (entries, dir, entry) = FolderAnalyzer.Siblings(path);
                return new VolumeContext(entries, dir, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new VolumeContext([path], Path.GetDirectoryName(path) ?? path, path);
            }
        }

        public (List<VolumeEntry> List, int Index) ToVolumeEntries()
        {
            var list = Entries.Select(e => new VolumeEntry
            {
                DisplayName = DisplayName(e),
                Path = e,
                InnerPath = null,
                IsArchive = File.Exists(e),
                IsNested = false,
            }).ToList();
            return (list, IndexOf(Entries, Entry));
        }
    }
}
