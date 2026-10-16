using System.Text;

namespace Panely.Domain.Model;

/// <summary>실제 파일/폴더를 가리키는 소스.</summary>
public sealed record SourceRef
{
    public required Guid Id { get; init; }
    public required string Path { get; init; }
    public required SourceKind Kind { get; init; }
    public required string Title { get; init; }
    public string? VolumeId { get; init; }
    public string? FileId { get; init; }
}

/// <summary>소스 안의 한 책. InnerPath는 바깥 컨테이너 기준 정규화 상대 경로(임시 해제 root 아님).</summary>
public readonly record struct BookRef(Guid SourceId, string? InnerPath)
{
    public string Key => BookKeys.Make(SourceId, InnerPath);
}

public static class BookKeys
{
    /// <summary>bookKey 형식 버전. escaping/대소문자 규칙을 바꾸면 올린다.</summary>
    public const int FormatVersion = 1;

    public static string Make(Guid sourceId, string? innerPath)
    {
        var normalized = NormalizeInnerPath(innerPath);
        return string.IsNullOrEmpty(normalized)
            ? $"source:{sourceId:D}"
            : $"source:{sourceId:D}#{normalized}";
    }

    public static string SeriesKey(string seriesIdentity) => $"series:{seriesIdentity}";

    /// <summary>구분자는 '/', 빈 구성요소와 '.' 제거. 대소문자는 보존한다.</summary>
    public static string? NormalizeInnerPath(string? innerPath)
    {
        if (string.IsNullOrWhiteSpace(innerPath)) return null;
        var parts = innerPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".");
        var joined = string.Join('/', parts);
        return joined.Length == 0 ? null : joined;
    }

    public static bool TryParse(string key, out Guid sourceId, out string? innerPath)
    {
        sourceId = Guid.Empty;
        innerPath = null;
        if (!key.StartsWith("source:", StringComparison.Ordinal)) return false;
        var rest = key.AsSpan(7);
        var hash = rest.IndexOf('#');
        var idPart = hash >= 0 ? rest[..hash] : rest;
        if (!Guid.TryParse(idPart, out sourceId)) return false;
        if (hash >= 0) innerPath = rest[(hash + 1)..].ToString();
        return true;
    }
}

public static class PageIds
{
    /// <summary>pageId = bookKey|정규화 상대경로|contentSignature.</summary>
    public static string Make(string bookKey, string relativePath, string contentSignature)
    {
        var sb = new StringBuilder(bookKey.Length + relativePath.Length + contentSignature.Length + 2);
        sb.Append(bookKey).Append('|').Append(relativePath.Replace('\\', '/')).Append('|').Append(contentSignature);
        return sb.ToString();
    }
}
