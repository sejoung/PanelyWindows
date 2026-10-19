using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Library;

namespace Panely.Core.Sources;

/// <summary>
/// 폴더 분석 결과: 직접 이미지 존재 여부와 자연정렬한 하위 폴더+아카이브.
/// Images는 열거할 때 받은 FileInfo라 크기·수정 시각을 파일마다 다시 조회하지 않는다.
/// </summary>
public sealed record FolderAnalysis(string Path, IReadOnlyList<FileInfo> Images, IReadOnlyList<string> Children)
{
    public bool HasDirectImages => Images.Count > 0;
}

/// <summary>폴더/겉포장/시리즈 결정(04-files.md).</summary>
public static class FolderAnalyzer
{
    public static NaturalComparer Comparer => new(Loc.Culture);

    /// <summary>숨김·시스템 속성, 점 이름, __MACOSX, ._* 제외.</summary>
    public static bool IsHidden(FileSystemInfo info)
    {
        var name = info.Name;
        if (name.StartsWith('.') || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (IOException) { return true; }
    }

    public static FolderAnalysis Analyze(string folder)
    {
        var dir = new DirectoryInfo(folder);
        var images = new List<FileInfo>();
        var children = new List<string>();
        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            if (IsHidden(info)) continue;
            if (info is DirectoryInfo)
            {
                if (info.Name.StartsWith(".panely-", StringComparison.Ordinal)) continue;
                children.Add(info.FullName);
            }
            else if (SupportedFormats.IsImage(info.Name))
            {
                images.Add((FileInfo)info);
            }
            else if (SupportedFormats.IsArchive(info.Name))
            {
                children.Add(info.FullName);
            }
        }
        var cmp = Comparer;
        images.Sort((a, b) => cmp.Compare(a.Name, b.Name));
        children.Sort((a, b) => cmp.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        return new FolderAnalysis(folder, images, children);
    }

    /// <summary>링크를 따라간 실제 경로(루프 방지 방문 집합용).</summary>
    public static string CanonicalDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) return Path.GetFullPath(target.FullName).TrimEnd('\\');
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Path.GetFullPath(path).TrimEnd('\\');
    }

    public sealed record Resolution(
        string BookPath,
        bool IsArchive,
        IReadOnlyList<string>? SeriesEntries,
        string? SeriesDirectory,
        string? SeriesEntry)
    {
        /// <summary>폴더 책이면 그 폴더의 분석 결과(다시 열거하지 않게).</summary>
        public FolderAnalysis? BookAnalysis { get; init; }
    }

    /// <summary>
    /// 직접 이미지가 있으면 그 폴더가 책. 없으면 첫 하위 항목으로 내려가며 반복.
    /// 하위 항목 2개 이상은 시리즈 레벨(더 안쪽 다권 목록이 더 구체적), 1개는 겉포장.
    /// </summary>
    public static Resolution Resolve(string start, HashSet<string>? visited = null, CancellationToken cancellationToken = default)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string>? series = null;
        string? seriesDir = null;
        string? seriesEntry = null;
        var current = start;
        for (var guard = 0; guard < 64; guard++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(CanonicalDirectory(current)))
                throw new BookOpenException(OpenErrorKind.Empty, Loc.T("Folder is empty or has no supported content"));
            var analysis = Analyze(current);
            if (analysis.HasDirectImages) return new Resolution(current, false, series, seriesDir, seriesEntry) { BookAnalysis = analysis };
            if (analysis.Children.Count == 0)
                throw new BookOpenException(OpenErrorKind.Empty, Loc.T("Folder is empty or has no supported content"));
            var first = analysis.Children[0];
            if (analysis.Children.Count >= 2)
            {
                series = analysis.Children;
                seriesDir = current;
                seriesEntry = first;
            }
            if (File.Exists(first)) return new Resolution(first, true, series, seriesDir, seriesEntry);
            current = first;
        }
        throw new BookOpenException(OpenErrorKind.Empty, Loc.T("Folder is empty or has no supported content"));
    }

    /// <summary>
    /// 직접 연 책(또는 권 항목)의 sibling 목록: 부모의 하위 폴더+아카이브.
    /// 부모가 겉포장(자식 1개)이면 최대 2단계까지 올라가 가장 가까운 다권 레벨을 쓴다.
    /// </summary>
    public static (IReadOnlyList<string> Entries, string Directory, string Entry) Siblings(string bookOrEntryPath, string? stopAt = null)
    {
        var entry = bookOrEntryPath.TrimEnd('\\');
        var parent = Path.GetDirectoryName(entry);
        IReadOnlyList<string> fallback = [entry];
        string fallbackDir = parent ?? entry;
        for (var level = 0; level < 3 && parent is not null; level++)
        {
            if (stopAt is not null && !IsUnderOrEqual(parent, stopAt)) break;
            FolderAnalysis analysis;
            try { analysis = Analyze(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
            if (analysis.Children.Count >= 2)
            {
                // 바로 위 부모는 그대로 sibling. 겉포장을 지나 올라간 단계는 같은 시리즈 이름(stem)의 형제가
                // 있을 때만 권 목록으로 인정한다. (Webtoon/Ep 1 처럼 단일 권 폴더가 라이브러리 루트의
                // 무관한 폴더들을 권으로 잡지 않게 한다. Series/Vol01/chapter 같은 겉포장은 Vol01·Vol02 stem이 같다.)
                if (level == 0 || HasSameSeriesSibling(analysis.Children, entry))
                    return (analysis.Children, parent, entry);
                break;
            }
            if (level == 0) { fallback = analysis.Children.Count > 0 ? analysis.Children : [entry]; fallbackDir = parent; }
            if (analysis.HasDirectImages) break; // 부모가 자체로 책이면 더 올라가지 않는다
            if (stopAt is not null && string.Equals(parent.TrimEnd('\\'), stopAt.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
            entry = parent;
            parent = Path.GetDirectoryName(parent);
        }
        var original = bookOrEntryPath.TrimEnd('\\');
        return (fallback, fallbackDir, fallback.FirstOrDefault(p => string.Equals(p, original, StringComparison.OrdinalIgnoreCase)) ?? original);
    }

    private static bool HasSameSeriesSibling(IReadOnlyList<string> children, string entry)
    {
        var stem = Panely.Domain.Library.SeriesStem.Compute(Path.GetFileName(entry.TrimEnd('\\')));
        return children.Any(c =>
            !string.Equals(c.TrimEnd('\\'), entry.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && Panely.Domain.Library.SeriesStem.Compute(Path.GetFileName(c.TrimEnd('\\'))) == stem);
    }

    public static bool IsUnderOrEqual(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
