using System.Text;
using System.Text.RegularExpressions;

namespace Panely.Domain.Translation;

/// <summary>export snapshot의 한 줄. Id는 `p{표시 쪽}-{그 쪽 export 순서}`(둘 다 1-based).</summary>
public sealed record AiChatEntry(string Id, int PageIndex, int BubbleId, string Original, string Translation);

/// <summary>한 번 만든 export snapshot. 편집 이후에도 응답을 이 snapshot에 매핑하며 다른 책에 적용하지 않는다.</summary>
public sealed record AiChatSnapshot(
    string BookKey,
    string Title,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<AiChatEntry> Entries)
{
    public AiChatEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
}

public sealed record AiChatChanges(
    IReadOnlyDictionary<string, string> Updates,
    int Returned,
    int Unchanged,
    int Unknown,
    IReadOnlyList<string> Missing);

/// <summary>AI 채팅 파일 교환(06 AI 채팅 파일 교환). 앱은 AI 서비스에 직접 요청하지 않는다.</summary>
public static partial class AiChatExchange
{
    /// <summary>
    /// snapshot 생성: hidden/빈 번역 제외, 쪽 오름차순·풍선 ID순.
    /// pages: (논리 쪽 index, 그 쪽의 표시 풍선) 목록.
    /// </summary>
    public static AiChatSnapshot BuildSnapshot(string bookKey, string title, string source, string target,
        IEnumerable<(int PageIndex, IReadOnlyList<DisplayBubble> Bubbles)> pages)
    {
        var entries = new List<AiChatEntry>();
        foreach (var (pageIndex, bubbles) in pages.OrderBy(p => p.PageIndex))
        {
            var n = 0;
            foreach (var b in bubbles.OrderBy(b => b.Id))
            {
                if (b.IsHidden || string.IsNullOrWhiteSpace(b.Text)) continue;
                n++;
                entries.Add(new AiChatEntry($"p{pageIndex + 1}-{n}", pageIndex, b.Id, b.EffectiveSource, b.Text!));
            }
        }
        return new AiChatSnapshot(bookKey, title, source, target, entries);
    }

    /// <summary>
    /// 파일 내용. 예:
    /// # Panely — "Sample" — Japanese → Korean — 2 balloons, pages 1–2
    /// # Each line: [id] original → current translation
    /// [p1-1] こんにちは → 안녕하세요
    /// </summary>
    public static string BuildFile(AiChatSnapshot snapshot, string sourceLanguageName, string targetLanguageName, string newline = "\r\n")
    {
        var sb = new StringBuilder();
        sb.Append("# Panely — \"").Append(snapshot.Title).Append("\" — ")
          .Append(sourceLanguageName).Append(" → ").Append(targetLanguageName).Append(" — ")
          .Append(snapshot.Entries.Count).Append(snapshot.Entries.Count == 1 ? " balloon, " : " balloons, ")
          .Append(DescribePages(snapshot.Entries.Select(e => e.PageIndex + 1).Distinct().Order().ToList()))
          .Append(newline);
        sb.Append("# Each line: [id] original → current translation").Append(newline);
        foreach (var e in snapshot.Entries)
            sb.Append('[').Append(e.Id).Append("] ").Append(Flatten(e.Original)).Append(" → ").Append(Flatten(e.Translation)).Append(newline);
        return sb.ToString();
    }

    public static string DescribePages(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0) return "no pages";
        if (pages.Count == 1) return $"page {pages[0]}";
        var contiguous = pages[^1] - pages[0] + 1 == pages.Count;
        return contiguous ? $"pages {pages[0]}–{pages[^1]}" : "pages " + string.Join(", ", pages);
    }

    /// <summary>줄바꿈은 ` / `로 펼친다(응답의 ` / `는 다시 줄바꿈).</summary>
    public static string Flatten(string text) =>
        string.Join(" / ", text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    [GeneratedRegex(@"^[\s>*\-•`]*\[(p\d+-\d+)\]\s*[:：.\-]?\s*(.*)$")]
    private static partial Regex LinePattern();

    private static readonly (string Open, string Close)[] QuotePairs = [("\"", "\""), ("“", "”"), ("「", "」")];

    /// <summary>
    /// 응답 parser: 줄 앞 공백/`&gt;`/`*`/`-`/`•`/backtick 허용, `[p숫자-숫자]` 뒤 `:：.-` 허용.
    /// 같은 ID는 마지막 줄 우선, 마지막 `→` 뒤만 채택, 양끝 한 쌍의 따옴표 제거, ` / `는 줄바꿈, 빈 내용 무시.
    /// </summary>
    public static Dictionary<string, string> Parse(string reply)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(reply)) return result;
        if (reply[0] == '﻿') reply = reply[1..];
        foreach (var raw in reply.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var m = LinePattern().Match(raw);
            if (!m.Success) continue;
            var id = m.Groups[1].Value;
            var content = m.Groups[2].Value;
            var arrow = content.LastIndexOf('→');
            if (arrow >= 0) content = content[(arrow + 1)..];
            content = content.Trim();
            foreach (var (open, close) in QuotePairs)
            {
                if (content.Length >= open.Length + close.Length && content.StartsWith(open, StringComparison.Ordinal) && content.EndsWith(close, StringComparison.Ordinal))
                {
                    content = content[open.Length..^close.Length].Trim();
                    break;
                }
            }
            content = content.Replace(" / ", "\n").Trim();
            if (content.Length == 0) continue;
            result[id] = content;
        }
        return result;
    }

    /// <summary>바뀐 것만 updates. unchanged/unknown 개수와 export에 있었는데 답에 없는 ID(순서대로).</summary>
    public static AiChatChanges ComputeChanges(IReadOnlyList<(string Id, string Translation)> sent, IReadOnlyDictionary<string, string> reply)
    {
        var sentMap = sent.ToDictionary(s => s.Id, s => s.Translation, StringComparer.Ordinal);
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        int unchanged = 0, unknown = 0, returned = 0;
        foreach (var (id, text) in reply)
        {
            if (!sentMap.TryGetValue(id, out var current)) { unknown++; continue; }
            returned++;
            if (Normalize(current) == Normalize(text)) unchanged++;
            else updates[id] = text;
        }
        var missing = sent.Where(s => !reply.ContainsKey(s.Id)).Select(s => s.Id).ToList();
        return new AiChatChanges(updates, returned, unchanged, unknown, missing);
    }

    public static AiChatChanges ComputeChanges(AiChatSnapshot snapshot, IReadOnlyDictionary<string, string> reply)
        => ComputeChanges(snapshot.Entries.Select(e => (e.Id, e.Translation)).ToList(), reply);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}

/// <summary>번역 순서: 현재 쪽부터 뒤쪽, 그다음 남은 앞쪽.</summary>
public static class TranslationOrder
{
    public static List<int> Pages(int pageCount, int currentIndex)
    {
        var list = new List<int>(Math.Max(0, pageCount));
        if (pageCount <= 0) return list;
        var start = Math.Clamp(currentIndex, 0, pageCount - 1);
        for (var i = start; i < pageCount; i++) list.Add(i);
        for (var i = 0; i < start; i++) list.Add(i);
        return list;
    }
}
