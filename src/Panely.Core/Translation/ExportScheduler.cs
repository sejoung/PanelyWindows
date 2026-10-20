using System.IO.Compression;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Export;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>번역 patch를 원본 해상도로 그려 인코딩한다(앱이 뷰어와 같은 painter로 구현).</summary>
public interface ITranslatedPageRenderer
{
    /// <summary>원본이 PNG면 PNG, 그 외 JPEG quality .92. 반환 extension은 점 없이.</summary>
    Task<(byte[] Bytes, string Extension)> RenderAsync(byte[] original, string extension, IReadOnlyList<PatchLayout> patches, CancellationToken cancellationToken);
}

public enum ExportPhase { Preparing, Translating, Waiting, Exporting, Finished, Failed, Cancelled }

public sealed record ExportRequest(BookTarget Book, string Target, string? Source, ExportFormat Format, UntranslatedPolicy Policy, string Destination, TranslationScope Scope);

public sealed record ExportJobInfo
{
    public required Guid Id { get; init; }
    public required ExportRequest Request { get; init; }
    public required ExportPhase Phase { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsTerminal => Phase is ExportPhase.Finished or ExportPhase.Failed or ExportPhase.Cancelled;
}

/// <summary>
/// 번역본 내보내기(06 번역본 내보내기). 책 하나당 job: preparing → translating(공유 큐) → waiting → exporting → finished/failed/cancelled.
/// 렌더링/쓰기는 한 책씩. `.partial` staging 후 원자적 rename, rename 시점에도 충돌을 확인해 덮어쓰지 않는다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim은 WaitAsync만 사용해 대기 핸들을 만들지 않는다. 종료 중 남은 작업의 Release와 경합하지 않도록 해제하지 않는다.")]
public sealed class ExportScheduler
{
    private readonly TranslationScheduler _translation;
    private readonly PageEditsStore _edits;
    private readonly IBookAccess _books;
    private readonly ITranslatedPageRenderer _renderer;
    private readonly IStateStore? _state;
    private readonly Func<string, string> _languageName;
    private readonly SemaphoreSlim _renderSlot = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _jobs = new();

    public ExportScheduler(TranslationScheduler translation, PageEditsStore edits, IBookAccess books, ITranslatedPageRenderer renderer,
        IStateStore? state, Func<string, string>? languageName = null)
    {
        _translation = translation;
        _edits = edits;
        _books = books;
        _renderer = renderer;
        _state = state;
        _languageName = languageName ?? (code => Loc.LanguageName(code));
    }

    public event EventHandler<ExportJobInfo>? JobChanged;
    public event EventHandler<Guid>? JobRemoved;

    public IReadOnlyList<ExportJobInfo> Jobs
    {
        get { lock (_gate) return _jobs.Values.OrderBy(e => e.Info.CreatedAt).Select(e => e.Info).ToList(); }
    }

    public ExportJobInfo Enqueue(ExportRequest request)
    {
        var entry = new Entry
        {
            Info = new ExportJobInfo { Id = Guid.NewGuid(), Request = request, Phase = ExportPhase.Preparing, CreatedAt = DateTimeOffset.UtcNow },
        };
        lock (_gate) _jobs[entry.Info.Id] = entry;
        JobChanged?.Invoke(this, entry.Info);
        entry.Task = Task.Run(() => RunAsync(entry));
        return entry.Info;
    }

    public void Cancel(Guid id)
    {
        Entry? e;
        lock (_gate) _jobs.TryGetValue(id, out e);
        if (e is null || e.Info.IsTerminal) return;
        e.Cancelled = true;
        e.Cts.Cancel();
        Set(e, i => i with { Phase = ExportPhase.Cancelled });
    }

    public ExportJobInfo? Retry(Guid id)
    {
        Entry? e;
        lock (_gate) _jobs.TryGetValue(id, out e);
        if (e is null || e.Info.Phase is not (ExportPhase.Failed or ExportPhase.Cancelled)) return null;
        Dismiss(id);
        return Enqueue(e.Info.Request);
    }

    public void Dismiss(Guid id)
    {
        Entry? e;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out e) || !e.Info.IsTerminal) return;
            _jobs.Remove(id);
        }
        JobRemoved?.Invoke(this, id);
    }

    public async Task CancelAllAsync()
    {
        List<Entry> entries;
        lock (_gate) entries = _jobs.Values.ToList();
        foreach (var e in entries) Cancel(e.Info.Id);
        foreach (var e in entries)
        {
            try { if (e.Task is not null) await e.Task.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private void Set(Entry e, Func<ExportJobInfo, ExportJobInfo> change)
    {
        ExportJobInfo info;
        lock (_gate)
        {
            // cancelled는 늦은 task 결과가 다른 상태로 바꾸지 못한다
            if (e.Info.Phase == ExportPhase.Cancelled) return;
            info = e.Info = change(e.Info);
        }
        JobChanged?.Invoke(this, info);
    }

    private async Task RunAsync(Entry e)
    {
        var ct = e.Cts.Token;
        var request = e.Info.Request;
        string? staging = null;
        try
        {
            using var book = await _books.OpenAsync(request.Book, ct).ConfigureAwait(false);
            Set(e, i => i with { Total = book.Pages.Count });

            var source = request.Source ?? _translation.Get(request.Book.BookKey, request.Target)?.Source;
            var cached = source is null ? new HashSet<int>() : _translation.CachedPages(book.Pages, source, request.Target, request.Scope);

            if (request.Policy == UntranslatedPolicy.TranslateFirst && cached.Count < book.Pages.Count)
            {
                Set(e, i => i with { Phase = ExportPhase.Translating });
                source = await WaitForTranslationAsync(request, source, ct).ConfigureAwait(false);
                cached = _translation.CachedPages(book.Pages, source, request.Target, request.Scope);
            }
            else if (request.Policy == UntranslatedPolicy.ExportAsIs && cached.Count == 0)
            {
                throw new ExportFailure(Loc.T("Nothing in this book is translated into this language yet."));
            }

            Set(e, i => i with { Phase = ExportPhase.Waiting });
            await _renderSlot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Set(e, i => i with { Phase = ExportPhase.Exporting, Done = 0 });
                var baseName = ExportNaming.OutputBaseName(request.Book.Title, _languageName(request.Target));
                Directory.CreateDirectory(request.Destination);
                var extension = request.Format == ExportFormat.Folder ? string.Empty : request.Format == ExportFormat.Zip ? ".zip" : ".cbz";
                staging = Path.Combine(request.Destination, $".{baseName}.{Guid.NewGuid():N}.partial");
                await WriteAsync(e, book, source, cached, request, staging, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var final = PublishStaging(staging, request.Destination, baseName, extension, request.Format == ExportFormat.Folder);
                staging = null;
                var bytes = request.Format == ExportFormat.Folder
                    ? Directory.EnumerateFiles(final).Sum(f => new FileInfo(f).Length)
                    : new FileInfo(final).Length;
                if (e.Cancelled) { TryDelete(final); return; }
                _state?.AddExport(new ExportRecord(Guid.NewGuid(), request.Book.Title, request.Target, request.Format, final, book.Pages.Count, bytes, DateTimeOffset.UtcNow));
                Set(e, i => i with { Phase = ExportPhase.Finished, OutputPath = final, Bytes = bytes, Done = book.Pages.Count });
            }
            finally
            {
                _renderSlot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Set(e, i => i with { Phase = ExportPhase.Cancelled });
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                ExportFailure f => f.Message,
                IOException io when IsDiskFull(io) => Loc.T("Export failed: %@", Loc.T("The disk is full.")),
                UnauthorizedAccessException => Loc.T("Export failed: %@", Loc.T("Panely can't write to that folder.")),
                _ => Loc.T("Export failed: %@", ex.Message),
            };
            Set(e, i => i with { Phase = ExportPhase.Failed, Error = message });
        }
        finally
        {
            if (staging is not null) TryDelete(staging); // 취소/오류는 partial만 정리
        }
    }

    private async Task<string> WaitForTranslationAsync(ExportRequest request, string? source, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, TranslationJobInfo info)
        {
            if (info.Book.BookKey != request.Book.BookKey || info.Target != request.Target) return;
            if (info.Phase == TranslationPhase.Completed && info.Source is { } src) done.TrySetResult(src);
            else if (info.Phase == TranslationPhase.Failed || (info.Phase == TranslationPhase.Paused && info.Reason == TranslationStopReason.NeedsLanguageDownload))
                done.TrySetException(new ExportFailure(info.ReasonMessage ?? Loc.T("Translation was stopped before the book was finished.")));
            else if (info.Phase == TranslationPhase.Paused && info.UserPaused)
                done.TrySetException(new ExportFailure(Loc.T("Translation was stopped before the book was finished.")));
        }
        void OnRemoved(object? s, string key)
        {
            if (key == TranslationScheduler.JobKey(request.Book.BookKey, request.Target))
                done.TrySetException(new ExportFailure(Loc.T("Translation was stopped before the book was finished.")));
        }
        _translation.JobChanged += OnChanged;
        _translation.JobRemoved += OnRemoved;
        try
        {
            var existing = _translation.Get(request.Book.BookKey, request.Target);
            if (existing is null || existing.Phase is TranslationPhase.Completed or TranslationPhase.Failed or TranslationPhase.Paused)
                _translation.Start(request.Book, request.Target, source ?? existing?.Source, request.Scope);
            using var reg = ct.Register(() => done.TrySetCanceled(ct));
            return await done.Task.ConfigureAwait(false);
        }
        finally
        {
            _translation.JobChanged -= OnChanged;
            _translation.JobRemoved -= OnRemoved;
        }
    }

    private async Task WriteAsync(Entry e, OpenedBook book, string? source, HashSet<int> cached, ExportRequest request, string staging, CancellationToken ct)
    {
        ZipArchive? zip = null;
        FileStream? zipStream = null;
        if (request.Format == ExportFormat.Folder) Directory.CreateDirectory(staging);
        else
        {
            zipStream = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);
        }
        try
        {
            var count = book.Pages.Count;
            List<PatchLayout> carry = [];
            double prevW = 0, prevH = 0;
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = book.Pages[i];
                var bytes = await book.Data.ReadAsync(entry, ct).ConfigureAwait(false);
                var ext = entry.Extension;
                List<PatchLayout> patches = [];
                double width = 0, height = 0;
                if (source is not null && cached.Contains(i) && _translation.LoadCached(entry.PageId, source, request.Target, request.Scope) is { } page)
                {
                    width = page.ImageWidth; height = page.ImageHeight;
                    var edits = _edits.Get(entry.PageId, request.Scope);
                    var display = EditResolver.Resolve(page.Bubbles, edits, request.Target);
                    patches = PatchGeometry.Layout(EditResolver.ToPatchSources(display, includePending: false), page.ImageWidth);
                }
                // strip 풍선이 이전 slice에서 넘친 부분
                var overflow = request.Scope == TranslationScope.Strip && carry.Count > 0 && width > 0
                    ? carry.Select(p => PatchGeometry.ShiftToNextSlice(p, prevW, prevH, width)).ToList()
                    : [];
                var all = patches.Concat(overflow).ToList();

                byte[] output;
                string outExt;
                if (all.Count == 0) { output = bytes; outExt = ext; } // 원본 bytes 그대로
                else (output, outExt) = await _renderer.RenderAsync(bytes, ext, all, ct).ConfigureAwait(false);

                var name = ExportNaming.PageFileName(count, i, outExt);
                if (zip is not null)
                {
                    var level = outExt is "jpg" or "jpeg" or "png" or "webp" or "gif" or "heic" or "heif" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var zipEntry = zip.CreateEntry(name, level);
                    await using var s = zipEntry.Open();
                    await s.WriteAsync(output, ct).ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllBytesAsync(Path.Combine(staging, name), output, ct).ConfigureAwait(false);
                }

                carry = height > 0 ? patches.Where(p => p.Rect.Bottom > height).ToList() : [];
                if (width > 0) { prevW = width; prevH = height; }
                Set(e, info => info with { Done = i + 1 });
            }
        }
        finally
        {
            zip?.Dispose();
            zipStream?.Dispose();
        }
    }

    /// <summary>최종 이름을 rename 시점에 정해 기존 파일/폴더를 덮어쓰지 않는다.</summary>
    internal static string PublishStaging(string staging, string destination, string baseName, string extension, bool isFolder)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var name = ExportNaming.UniqueName(baseName, extension, n => File.Exists(Path.Combine(destination, n)) || Directory.Exists(Path.Combine(destination, n)));
            var final = Path.Combine(destination, name);
            try
            {
                if (isFolder) Directory.Move(staging, final);
                else File.Move(staging, final, overwrite: false);
                return final;
            }
            catch (IOException) when (File.Exists(final) || Directory.Exists(final))
            {
                // 경합: 다른 프로세스가 같은 이름을 만들었다 → 다음 번호
            }
        }
        throw new IOException("Could not choose an output name.");
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27;

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ExportFailure(string message) : Exception(message);

    private sealed class Entry
    {
        public required ExportJobInfo Info { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public bool Cancelled { get; set; }
        public Task? Task { get; set; }
    }
}
