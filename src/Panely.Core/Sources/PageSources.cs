using Panely.Core.Abstractions;
using Panely.Core.Archives;
using Panely.Core.Localization;

namespace Panely.Core.Sources;

/// <summary>디스크 파일(일반 폴더 책, 해제 캐시 안의 책)에서 쪽을 읽는다.</summary>
public sealed class FolderPageSource : IPageSource
{
    private readonly string _root;
    private readonly IDisposable? _owned;

    public FolderPageSource(string root, IDisposable? owned = null)
    {
        _root = root;
        _owned = owned;
    }

    public string Root => _root;

    public string? TryGetFilePath(PageEntry page) => Path.Combine(_root, page.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public async Task<byte[]> ReadAsync(PageEntry page, CancellationToken cancellationToken)
    {
        var path = TryGetFilePath(page)!;
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex) { throw new BookOpenException(OpenErrorKind.NotFound, Loc.T("Couldn't read this page."), ex); }
        catch (DirectoryNotFoundException ex) { throw new BookOpenException(OpenErrorKind.NotFound, Loc.T("Couldn't read this page."), ex); }
        catch (UnauthorizedAccessException ex) { throw new BookOpenException(OpenErrorKind.AccessDenied, Loc.T("Couldn't read this page."), ex); }
    }

    public async Task<byte[]> ReadPrefixAsync(PageEntry page, int maxBytes, CancellationToken cancellationToken)
    {
        var path = TryGetFilePath(page)!;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var buffer = new byte[(int)Math.Min(maxBytes, Math.Max(0, fs.Length))];
            var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (FileNotFoundException ex) { throw new BookOpenException(OpenErrorKind.NotFound, Loc.T("Couldn't read this page."), ex); }
        catch (DirectoryNotFoundException ex) { throw new BookOpenException(OpenErrorKind.NotFound, Loc.T("Couldn't read this page."), ex); }
        catch (UnauthorizedAccessException ex) { throw new BookOpenException(OpenErrorKind.AccessDenied, Loc.T("Couldn't read this page."), ex); }
    }

    public void Dispose() => _owned?.Dispose();
}

/// <summary>평평한 아카이브에서 entry 단위로 쪽을 읽는다(전체 추출 없음).</summary>
public sealed class ArchivePageSource(IArchiveReader reader) : IPageSource
{
    public IArchiveReader Reader => reader;

    public string? TryGetFilePath(PageEntry page) => null;

    public Task<byte[]> ReadAsync(PageEntry page, CancellationToken cancellationToken)
        => reader.ReadEntryAsync(page.RelativePath, cancellationToken);

    public Task<byte[]> ReadPrefixAsync(PageEntry page, int maxBytes, CancellationToken cancellationToken)
        => reader.ReadPrefixAsync(page.RelativePath, maxBytes, cancellationToken);

    public void Dispose() => reader.Dispose();
}
