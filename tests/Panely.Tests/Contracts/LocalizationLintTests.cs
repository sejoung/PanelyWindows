using System.Text.Json;
using System.Text.RegularExpressions;
using Panely.Core.Commands;
using Panely.Core.Localization;

namespace Panely.Tests.Contracts;

/// <summary>
/// 문구 린트(08-commands-and-copy.md): 코드에서 쓰는 문구 key가 원본 catalog나 Windows 문구 파일에 있는지,
/// Windows 문구의 en/ko 치환자가 맞는지, macOS 표현이 화면에 남지 않는지 검사한다.
/// key가 없으면 한국어 UI에 영어 원문이 그대로 보인다.
/// </summary>
public partial class LocalizationLintTests
{
    // Loc.T("key" …), Loc.TIn(lang, "key" …), MenuCommand("key", …) 의 첫 문자열 리터럴
    [GeneratedRegex("""(?:Loc\.T\(\s*|Loc\.TIn\(\s*[^,()]+,\s*|MenuCommand\(\s*)"((?:[^"\\]|\\.)*)"(?=\s*[,)])""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"%(?:(\d+)\$)?(?:l{0,2}|h|q|z)?[@dixufgs]")]
    private static partial Regex Placeholder();

    private static IEnumerable<(string File, int Line, string Key)> UsedKeys()
    {
        foreach (var file in RepoPaths.SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in KeyLiteral().Matches(lines[i]))
                    yield return (Path.GetRelativePath(RepoPaths.Root, file), i + 1, Regex.Unescape(m.Groups[1].Value));
            }
        }
    }

    [Fact]
    public void Every_key_used_in_code_has_a_string()
    {
        var used = UsedKeys().ToList();
        Assert.True(used.Count > 300, $"문구 사용처를 너무 적게 찾았습니다({used.Count}). 정규식이 깨졌는지 확인하세요.");
        var missing = used.Where(u => !Loc.Has(u.Key)).Select(u => $"{u.File}:{u.Line}  \"{u.Key}\"").Distinct().ToList();
        Assert.True(missing.Count == 0, "문구 파일에 없는 key:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Every_key_used_in_code_has_korean()
    {
        var english = UsedKeys().Select(u => u.Key).Distinct()
            .Where(k => Loc.Has(k) && Loc.TIn("ko", k) == Loc.TIn("en", k) && HasLetters(k))
            .Where(k => !AllowedSameInKorean.Contains(k))
            .ToList();
        Assert.True(english.Count == 0, "한국어 번역이 영어와 같은 key(번역 누락 가능):\n" + string.Join('\n', english));
    }

    /// <summary>한국어에서도 그대로 쓰는 고유명사·형식 이름.</summary>
    private static readonly HashSet<string> AllowedSameInKorean = new(StringComparer.Ordinal)
    {
        "CBZ", "ZIP", "Panely", "OK", "%@", "%lld", ".%@", "%@ · ", "%@ max, reports include %@",
    };

    private static bool HasLetters(string s) => s.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    [Fact]
    public void Command_labels_have_strings()
    {
        var missing = CommandRegistry.All.Where(c => !Loc.Has(c.LabelKey)).Select(c => $"{c.Id}: {c.LabelKey}").ToList();
        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    public static IEnumerable<object[]> StringFiles()
        => Directory.EnumerateFiles(RepoPaths.Strings, "*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(StringFiles))]
    public void Windows_string_file_is_valid(string fileName)
    {
        var path = Path.Combine(RepoPaths.Strings, fileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (!seen.Add(entry.Name)) problems.Add($"중복 key: {entry.Name}");
            if (!entry.Value.TryGetProperty("en", out var en) || string.IsNullOrWhiteSpace(en.GetString())) problems.Add($"en 없음: {entry.Name}");
            if (!entry.Value.TryGetProperty("ko", out var ko) || string.IsNullOrWhiteSpace(ko.GetString())) { problems.Add($"ko 없음: {entry.Name}"); continue; }
            if (Signature(en.GetString()!) != Signature(ko.GetString()!))
                problems.Add($"치환자 불일치: {entry.Name}\n    en: {en.GetString()}\n    ko: {ko.GetString()}");
            foreach (var mac in MacWording)
            {
                if (en.GetString()!.Contains(mac, StringComparison.Ordinal) || ko.GetString()!.Contains(mac, StringComparison.Ordinal))
                    problems.Add($"macOS 표현 '{mac}': {entry.Name}");
            }
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void Same_key_is_not_defined_differently_in_two_files()
    {
        var map = new Dictionary<string, (string File, string Ko)>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Strings, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var ko = entry.Value.TryGetProperty("ko", out var k) ? k.GetString() ?? "" : "";
                if (map.TryGetValue(entry.Name, out var prev) && prev.Ko != ko)
                    conflicts.Add($"{entry.Name}: {prev.File}=\"{prev.Ko}\" vs {Path.GetFileName(file)}=\"{ko}\"");
                else map[entry.Name] = (Path.GetFileName(file), ko);
            }
        }
        Assert.True(conflicts.Count == 0, string.Join('\n', conflicts));
    }

    /// <summary>
    /// 화면 문구에 macOS 단축키 기호·용어가 남지 않아야 한다(08 문서 변환표).
    /// 코드가 실제로 쓰는 key의 현재 언어 결과를 en/ko 모두 검사한다.
    /// </summary>
    [Fact]
    public void Used_strings_contain_no_mac_wording()
    {
        var problems = new List<string>();
        foreach (var key in UsedKeys().Select(u => u.Key).Distinct())
        {
            foreach (var lang in new[] { "en", "ko" })
            {
                var text = Loc.TIn(lang, key);
                foreach (var mac in MacWording)
                    if (text.Contains(mac, StringComparison.Ordinal)) problems.Add($"[{lang}] '{mac}' in \"{key}\" → \"{text}\"");
            }
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private static readonly string[] MacWording = ["⌘", "⌥", "⌃", "⇧", "Finder", "macOS", "Apple Intelligence", "System Settings"];

    /// <summary>치환자 서명: 위치 인덱스별 종류(@/숫자). 비위치 치환자는 순서대로 번호를 매긴다.</summary>
    private static string Signature(string template)
    {
        var parts = new SortedDictionary<int, char>();
        var seq = 0;
        foreach (Match m in Placeholder().Matches(template.Replace("%%", "")))
        {
            var index = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1 : seq++;
            var kind = m.Value[^1] == '@' ? '@' : '#';
            parts[index] = kind;
        }
        return string.Join(",", parts.Select(p => $"{p.Key}{p.Value}"));
    }
}
