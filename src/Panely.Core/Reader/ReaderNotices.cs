namespace Panely.Core.Reader;

/// <summary>알림 범주. 값이 작을수록 우선순위가 높다(01: 소스 변경 → 리더 오류 → 권 이동 안내 → 번역 안내).</summary>
public enum NoticeCategory { SourceChanged = 0, ReaderError = 1, Volume = 2, Translation = 3 }

public enum NoticeAction { None, Reload, Dismiss, Retry, Open, RemoveFromList, AllowAccess }

/// <summary>상단 배너 하나. Duration이 있으면 그 시간 뒤 자동 해제.</summary>
public sealed record Notice(NoticeCategory Category, string Message, IReadOnlyList<NoticeAction> Actions, TimeSpan? Duration = null)
{
    public long Id { get; init; }
}

/// <summary>범주별 최신 알림을 보관하고 가장 우선순위가 높은 것 하나를 노출한다.</summary>
public sealed class NotificationCenter
{
    private readonly Notice?[] _slots = new Notice?[4];
    private long _nextId;

    public event EventHandler? Changed;

    public Notice? Current
    {
        get
        {
            foreach (var n in _slots)
                if (n is not null) return n;
            return null;
        }
    }

    public Notice? Get(NoticeCategory category) => _slots[(int)category];

    public Notice Post(Notice notice)
    {
        var stored = notice with { Id = Interlocked.Increment(ref _nextId) };
        _slots[(int)notice.Category] = stored;
        Changed?.Invoke(this, EventArgs.Empty);
        return stored;
    }

    public void Clear(NoticeCategory category)
    {
        if (_slots[(int)category] is null) return;
        _slots[(int)category] = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Id가 같을 때만 해제(자동 만료 타이머가 새 알림을 지우지 않게).</summary>
    public void Dismiss(long id)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i]?.Id == id)
            {
                _slots[i] = null;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
    }
}
