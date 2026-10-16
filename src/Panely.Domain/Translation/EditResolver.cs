using Panely.Domain.Model;

namespace Panely.Domain.Translation;

public enum BubbleTextState
{
    /// <summary>기계 번역.</summary>
    Machine,
    /// <summary>기계 번역 대기(OCR 직후 `…`).</summary>
    Pending,
    /// <summary>해당 언어 사용자 직접 번역.</summary>
    UserTranslation,
    /// <summary>수정한 원문의 기계 재번역.</summary>
    CorrectedRetranslated,
    /// <summary>수정한 원문을 재번역 중.</summary>
    CorrectedPending,
    /// <summary>원문을 고쳤지만 아직 재번역 안 됨(기존 번역 표시).</summary>
    CorrectedNeedsRetranslation,
    /// <summary>사용자가 추가한 풍선, 글자를 못 읽음.</summary>
    AddedNoText,
    /// <summary>사용자가 추가한 풍선, 이 언어로 아직 번역 안 됨.</summary>
    AddedNotTranslated,
}

/// <summary>화면/내보내기에 쓰는 런타임 풍선(캐시 원본+수정 조합).</summary>
public sealed record DisplayBubble
{
    /// <summary>런타임 ID: 기계 풍선은 원래 ID, 추가 풍선은 1,000,000+수정 배열 index.</summary>
    public required int Id { get; init; }
    public required RectD Rect { get; init; }
    public required double GlyphSize { get; init; }
    public required bool IsVertical { get; init; }
    public required BubbleBackground Background { get; init; }
    public RectD? BalloonRect { get; init; }
    /// <summary>OCR이 읽은 원문.</summary>
    public required string RecognizedSource { get; init; }
    /// <summary>수정 원문이 있으면 그것, 없으면 인식 원문.</summary>
    public required string EffectiveSource { get; init; }
    /// <summary>표시할 번역(없으면 null → `…` 또는 빈 풍선).</summary>
    public string? Text { get; init; }
    public required BubbleTextState State { get; init; }
    public bool IsHidden { get; init; }
    public bool IsAdded { get; init; }
    /// <summary>PageEdits.Bubbles 안의 index(수정이 있을 때).</summary>
    public int? EditIndex { get; init; }
    /// <summary>캐시 원본 풍선 ID(추가 풍선이면 null).</summary>
    public int? MachineId { get; init; }

    public bool IsEdited => EditIndex is not null;
    public bool HasVisibleText => !IsHidden && !string.IsNullOrEmpty(Text);
}

/// <summary>
/// 수정 우선순위(06 사용자 수정):
/// 1 숨김 → 읽기/내보내기 제외(편집 모드 점선) · 2 해당 target 사용자 번역 · 3 correctedSource 재번역 결과
/// · 4 재번역 중이면 pending, 미처리면 기존 번역+재번역 필요 · 5 기계 번역.
/// </summary>
public static class EditResolver
{
    public const int AddedIdBase = 1_000_000;

    /// <summary>수정 매칭: recognizedSource 동일 + rect 교집합(최대 교집합 우선). 숫자 ID로 매칭하지 않는다.</summary>
    public static int FindEditIndex(PageEdits? edits, string recognizedSource, RectD rect)
    {
        if (edits is null) return -1;
        var best = -1;
        double bestArea = 0;
        for (var i = 0; i < edits.Bubbles.Count; i++)
        {
            var e = edits.Bubbles[i];
            if (e.Added is not null || e.RecognizedSource != recognizedSource) continue;
            var area = e.Rect.Intersect(rect).Area;
            if (area > bestArea) { best = i; bestArea = area; }
        }
        return best;
    }

    public static List<DisplayBubble> Resolve(
        IReadOnlyList<TranslatedBubble> machine,
        PageEdits? edits,
        string target,
        Func<int, bool>? isRetranslationPending = null)
    {
        var result = new List<DisplayBubble>(machine.Count + (edits?.Bubbles.Count ?? 0));
        foreach (var b in machine)
        {
            var index = FindEditIndex(edits, b.Source, b.Rect);
            var edit = index >= 0 ? edits!.Bubbles[index] : null;
            if (edit is null)
            {
                result.Add(new DisplayBubble
                {
                    Id = b.Id, Rect = b.Rect, GlyphSize = b.GlyphSize, IsVertical = b.IsVertical, Background = b.Background,
                    BalloonRect = b.BalloonRect, RecognizedSource = b.Source, EffectiveSource = b.Source,
                    Text = b.Translation, State = b.Translation is null ? BubbleTextState.Pending : BubbleTextState.Machine,
                    MachineId = b.Id,
                });
                continue;
            }

            var (text, state) = ResolveText(edit, target, b.Translation, index, isRetranslationPending);
            result.Add(new DisplayBubble
            {
                Id = b.Id, Rect = b.Rect, GlyphSize = b.GlyphSize, IsVertical = b.IsVertical, Background = b.Background,
                BalloonRect = b.BalloonRect, RecognizedSource = b.Source, EffectiveSource = edit.CorrectedSource ?? b.Source,
                Text = text, State = state, IsHidden = edit.IsHidden, EditIndex = index, MachineId = b.Id,
            });
        }

        if (edits is not null)
        {
            for (var i = 0; i < edits.Bubbles.Count; i++)
            {
                var e = edits.Bubbles[i];
                if (e.Added is not { } added) continue;
                var source = e.CorrectedSource ?? e.RecognizedSource;
                string? text;
                BubbleTextState state;
                if (e.Translations.TryGetValue(target, out var user) && user.Length > 0) { text = user; state = BubbleTextState.UserTranslation; }
                else if (e.Retranslations.TryGetValue(target, out var machineText) && machineText.Length > 0) { text = machineText; state = BubbleTextState.CorrectedRetranslated; }
                else if (string.IsNullOrWhiteSpace(source)) { text = null; state = BubbleTextState.AddedNoText; }
                else if (isRetranslationPending?.Invoke(i) == true) { text = null; state = BubbleTextState.CorrectedPending; }
                else { text = null; state = BubbleTextState.AddedNotTranslated; }
                result.Add(new DisplayBubble
                {
                    Id = AddedIdBase + i, Rect = e.Rect, GlyphSize = added.GlyphSize, IsVertical = added.IsVertical,
                    Background = added.Background, RecognizedSource = e.RecognizedSource, EffectiveSource = source,
                    Text = text, State = state, IsHidden = e.IsHidden, IsAdded = true, EditIndex = i,
                });
            }
        }
        return result;
    }

    private static (string? Text, BubbleTextState State) ResolveText(BubbleEdit edit, string target, string? machine, int index, Func<int, bool>? pending)
    {
        if (edit.Translations.TryGetValue(target, out var user) && user.Length > 0) return (user, BubbleTextState.UserTranslation);
        if (edit.CorrectedSource is not null)
        {
            if (edit.Retranslations.TryGetValue(target, out var re) && re.Length > 0) return (re, BubbleTextState.CorrectedRetranslated);
            return pending?.Invoke(index) == true
                ? (machine, BubbleTextState.CorrectedPending)
                : (machine, BubbleTextState.CorrectedNeedsRetranslation);
        }
        return (machine, machine is null ? BubbleTextState.Pending : BubbleTextState.Machine);
    }

    /// <summary>읽기/내보내기용: 숨김 제외, 글자가 있는 풍선만 patch 원본으로.</summary>
    public static List<PatchSource> ToPatchSources(IEnumerable<DisplayBubble> bubbles, bool includePending, string pendingText = "…")
    {
        var list = new List<PatchSource>();
        foreach (var b in bubbles)
        {
            if (b.IsHidden) continue;
            var text = b.Text;
            if (string.IsNullOrEmpty(text))
            {
                if (!includePending || b.State is BubbleTextState.AddedNoText) continue;
                text = pendingText;
            }
            list.Add(new PatchSource(b.Id, b.Rect, b.GlyphSize, b.IsVertical, b.Background, b.BalloonRect, text));
        }
        return list;
    }
}

public enum RevertKind { Translation, Original, Both }

/// <summary>PageEdits 불변 갱신 도우미. 모든 메서드는 새 PageEdits를 돌려준다.</summary>
public static class PageEditsEditor
{
    public static PageEdits Empty(string pageId, TranslationScope scope) => new() { PageId = pageId, Scope = scope, Bubbles = [] };

    /// <summary>기계 풍선의 수정 항목 index를 찾거나 새로 만든다.</summary>
    public static (PageEdits Edits, int Index) Ensure(PageEdits edits, string recognizedSource, RectD rect)
    {
        var index = EditResolver.FindEditIndex(edits, recognizedSource, rect);
        if (index >= 0) return (edits, index);
        var list = edits.Bubbles.ToList();
        list.Add(new BubbleEdit { RecognizedSource = recognizedSource, Rect = rect });
        return (edits with { Bubbles = list }, list.Count - 1);
    }

    public static PageEdits Update(PageEdits edits, int index, Func<BubbleEdit, BubbleEdit?> update)
    {
        var list = edits.Bubbles.ToList();
        var updated = update(list[index]);
        if (updated is null || IsEmpty(updated)) list.RemoveAt(index);
        else list[index] = updated;
        return edits with { Bubbles = list };
    }

    public static bool IsEmpty(BubbleEdit e) =>
        e.Added is null && !e.IsHidden && e.CorrectedSource is null && e.Translations.Count == 0 && e.Retranslations.Count == 0;

    /// <summary>해당 언어 직접 번역. 빈 값이면 제거.</summary>
    public static PageEdits SetTranslation(PageEdits edits, int index, string target, string? text) =>
        Update(edits, index, e => e with { Translations = With(e.Translations, target, string.IsNullOrWhiteSpace(text) ? null : text) });

    /// <summary>원문 수정(모든 언어에 적용). 이전 재번역은 무효화한다. 사용자 번역은 유지.</summary>
    public static PageEdits SetCorrectedSource(PageEdits edits, int index, string? source) =>
        Update(edits, index, e =>
        {
            var normalized = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
            if (e.Added is null && normalized == e.RecognizedSource) normalized = null;
            return e with { CorrectedSource = normalized, Retranslations = new Dictionary<string, string>() };
        });

    public static PageEdits SetRetranslation(PageEdits edits, int index, string target, string text) =>
        Update(edits, index, e => e with { Retranslations = With(e.Retranslations, target, text) });

    public static PageEdits SetHidden(PageEdits edits, int index, bool hidden) =>
        Update(edits, index, e => e with { IsHidden = hidden });

    public static PageEdits UnhideAll(PageEdits edits)
    {
        var copy = edits;
        for (var i = copy.Bubbles.Count - 1; i >= 0; i--)
            if (copy.Bubbles[i].IsHidden) copy = SetHidden(copy, i, false);
        return copy;
    }

    public static PageEdits Revert(PageEdits edits, int index, RevertKind kind, string target) =>
        Update(edits, index, e =>
        {
            if (e.Added is not null && kind != RevertKind.Translation)
                return e with { CorrectedSource = null, Retranslations = new Dictionary<string, string>(), Translations = kind == RevertKind.Both ? new Dictionary<string, string>() : e.Translations };
            return kind switch
            {
                RevertKind.Translation => e with { Translations = With(e.Translations, target, null) },
                RevertKind.Original => e with { CorrectedSource = null, Retranslations = new Dictionary<string, string>() },
                _ => e with { CorrectedSource = null, Retranslations = new Dictionary<string, string>(), Translations = new Dictionary<string, string>(), IsHidden = false },
            };
        });

    /// <summary>드래그로 추가한 풍선. recognized는 crop OCR 결과(없으면 빈 문자열).</summary>
    public static (PageEdits Edits, int Index) AddBalloon(PageEdits edits, RectD rect, string recognized, AddedBalloon added)
    {
        var list = edits.Bubbles.ToList();
        list.Add(new BubbleEdit { RecognizedSource = recognized, Rect = rect, Added = added });
        return (edits with { Bubbles = list }, list.Count - 1);
    }

    public static PageEdits RemoveAt(PageEdits edits, int index)
    {
        var list = edits.Bubbles.ToList();
        list.RemoveAt(index);
        return edits with { Bubbles = list };
    }

    private static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> map, string key, string? value)
    {
        var copy = new Dictionary<string, string>(map);
        if (value is null) copy.Remove(key); else copy[key] = value;
        return copy;
    }
}
