using System.Text;
using System.Text.RegularExpressions;

namespace Panely.Domain.Library;

/// <summary>
/// 전체 시리즈 번역에서 같은 제목 권만 고르는 stem(06-translation.md).
/// 확장자 제거 → 소문자 → 괄호 tag 제거 → 권/화 표기와 숫자 제거 → 영숫자만 남김.
/// </summary>
public static partial class SeriesStem
{
    private static readonly string[] KnownExtensions = [".cbz", ".cbr", ".zip", ".rar"];

    public static string Compute(string name)
    {
        var s = name;
        foreach (var ext in KnownExtensions)
        {
            if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { s = s[..^ext.Length]; break; }
        }
        s = s.ToLowerInvariant();
        s = Brackets().Replace(s, " ");
        s = PrefixedNumber().Replace(s, " ");
        s = SuffixedNumber().Replace(s, " ");
        s = VolumeMarker().Replace(s, " ");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsDigit(c) || c is >= '０' and <= '９') continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool SameSeries(string a, string b) => Compute(a) == Compute(b);

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}|【[^】]*】|（[^）]*）")]
    private static partial Regex Brackets();

    // 제/第 + 숫자 (+ 권/화 단위가 붙은 "第3巻", "제2화" 포함)
    [GeneratedRegex(@"[제第]\s*[0-9０-９]+\s*[권화巻話卷集]?")]
    private static partial Regex PrefixedNumber();

    // 숫자 + 권/화/巻/話/卷/集
    [GeneratedRegex(@"[0-9０-９]+\s*[권화巻話卷集]")]
    private static partial Regex SuffixedNumber();

    // volume/vol/v/ch/chapter/ep/episode/# + 숫자
    [GeneratedRegex(@"(?<![a-z])(?:volume|vol|chapter|ch|episode|ep|v|#)\.?\s*[0-9０-９]+")]
    private static partial Regex VolumeMarker();
}
