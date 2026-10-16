using Panely.Core.Abstractions;
using Panely.Domain.Model;

namespace Panely.Core.Library;

/// <summary>이어보기 제안(05-data.md).</summary>
public static class ContinueReading
{
    /// <summary>
    /// 사용 가능하고 total&gt;0인 unfinished 진행 중 최근 것부터(현재 열린 책 포함).
    /// 동률은 최근 소스 목록 순서를 따른다.
    /// </summary>
    public static IReadOnlyList<string> SuggestBookKeys(IStateStore state, Func<string, bool> isAvailable)
    {
        var recentOrder = new Dictionary<Guid, int>();
        var recents = state.Recents;
        for (var i = 0; i < recents.Count; i++) recentOrder.TryAdd(recents[i].SourceId, i);

        int RecentRank(string key) => BookKeys.TryParse(key, out var id, out _) && recentOrder.TryGetValue(id, out var r) ? r : int.MaxValue;

        return state.AllProgress
            .Where(p => !p.Value.Finished && p.Value.Total > 0)
            .OrderByDescending(p => p.Value.UpdatedAt)
            .ThenBy(p => RecentRank(p.Key))
            .Select(p => p.Key)
            .Where(isAvailable)
            .ToList();
    }

    /// <summary>bookKey → (SourceRef, innerPath).</summary>
    public static bool TryResolve(IStateStore state, string bookKey, out SourceRef? source, out string? innerPath)
    {
        source = null;
        innerPath = null;
        if (!BookKeys.TryParse(bookKey, out var id, out innerPath)) return false;
        source = state.FindSource(id);
        return source is not null;
    }

    /// <summary>bookKey로 열기 요청을 만든다.</summary>
    public static OpenRequest? ToRequest(IStateStore state, string bookKey, OpenIntent intent, int? pageIndex = null)
        => TryResolve(state, bookKey, out var source, out var inner) ? new OpenRequest(source!.Path, inner, pageIndex, intent) : null;
}
