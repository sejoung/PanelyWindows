using Panely.Domain.Model;
using Panely.Domain.Reading;

namespace Panely.Core.Reader;

/// <summary>
/// baseline/현재 배율과 재맞춤 정책(03 재맞춤·잠금·시리즈 줌). UI와 무관한 상태만 가진다.
/// </summary>
public sealed class ZoomController
{
    public double Baseline { get; private set; } = 1;
    public double Zoom { get; private set; } = 1;
    public FitMode FitMode { get; private set; } = FitMode.FitScreen;
    public bool InitialFitApplied { get; private set; }

    /// <summary>사용자가 이전 baseline에서 벗어나도록 확대했는가.</summary>
    public bool UserHasZoomedAway => InitialFitApplied && Math.Abs(Zoom - Baseline) >= ZoomPolicy.FitTolerance;

    public bool IsAtFit(FitMode target) => ZoomPolicy.IsAtFit(FitMode, target, Zoom, Baseline);

    /// <summary>수동 배수(zoom/baseline). 시리즈 세션 기억용.</summary>
    public double ManualFactor => Baseline > 0 ? Zoom / Baseline : 1;

    /// <summary>새 책: 초기 맞춤을 다시 적용하도록 표시.</summary>
    public void ResetForNewDocument(FitMode mode)
    {
        FitMode = mode;
        InitialFitApplied = false;
    }

    /// <summary>
    /// geometry 변경 시 새 baseline 적용. force: 명시적 맞춤/Reset/시리즈 기억 없는 새 책.
    /// 반환값: zoom이 baseline으로 재설정되었는지.
    /// </summary>
    public bool ApplyBaseline(double newBaseline, bool force, bool sizeLocked, double? sessionFactor = null)
    {
        if (newBaseline <= 0 || !double.IsFinite(newBaseline)) newBaseline = 1;
        var reset = ZoomPolicy.ShouldReset(force, InitialFitApplied, sizeLocked, UserHasZoomedAway);
        var firstApply = !InitialFitApplied;
        Baseline = newBaseline;
        if (reset)
        {
            Zoom = firstApply && sessionFactor is { } f && !force
                ? ZoomPolicy.Clamp(newBaseline * f)
                : ZoomPolicy.Clamp(newBaseline);
        }
        InitialFitApplied = true;
        return reset;
    }

    /// <summary>맞춤 모드 선택(같은 모드 재선택도 baseline 복귀).</summary>
    public void SelectFit(FitMode mode, double baselineForMode)
    {
        FitMode = mode;
        Baseline = baselineForMode > 0 ? baselineForMode : 1;
        Zoom = ZoomPolicy.Clamp(Baseline);
        InitialFitApplied = true;
    }

    public void ResetZoom() => Zoom = ZoomPolicy.Clamp(Baseline);

    public void SetZoom(double zoom) => Zoom = ZoomPolicy.Clamp(zoom);

    public void ZoomIn() => Zoom = ZoomPolicy.ZoomIn(Zoom);

    public void ZoomOut() => Zoom = ZoomPolicy.ZoomOut(Zoom);

    public void DoubleClick() => Zoom = ZoomPolicy.DoubleClick(Baseline, Zoom);
}

/// <summary>시리즈별 수동 배수(zoom/oldBaseline). 앱 세션 메모리만, 영속하지 않는다.</summary>
public sealed class SessionZoomMemory
{
    private readonly Dictionary<string, double> _factors = new(StringComparer.Ordinal);

    public void Remember(string seriesKey, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        _factors[seriesKey] = factor;
    }

    public double? Recall(string seriesKey) => _factors.TryGetValue(seriesKey, out var f) ? f : null;

    public void Clear() => _factors.Clear();
}
