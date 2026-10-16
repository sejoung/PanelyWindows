using Panely.Domain.Model;

namespace Panely.Domain.Reading;

public static class FitCalculator
{
    public static double Fit(SizeD document, SizeD viewport, FitMode mode)
    {
        if (document.Width <= 0 || document.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return 1;
        return mode switch
        {
            FitMode.FitWidth => viewport.Width / document.Width,
            FitMode.FitHeight => viewport.Height / document.Height,
            _ => Math.Min(viewport.Width / document.Width, viewport.Height / document.Height),
        };
    }
}

/// <summary>확대/축소 규칙. 허용 범위 .1..10, 배수 1.25, 더블클릭 baseline↔baseline×2.</summary>
public static class ZoomPolicy
{
    public const double Min = 0.1;
    public const double Max = 10;
    public const double Step = 1.25;
    public const double FitTolerance = 0.001;
    public const double DoubleClickTolerance = 0.01;
    public const double ContinuousSensitivity = 0.01;
    /// <summary>Windows 결정: 일반 휠 한 notch = zoom delta 10(약 10%).</summary>
    public const double DiscreteDeltaPerNotch = 10;

    public static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, Min, Max) : 1;

    public static double ZoomIn(double current) => Clamp(current * Step);

    public static double ZoomOut(double current) => Clamp(current / Step);

    public static double DoubleClick(double baseline, double current)
        => Math.Abs(current - baseline) <= DoubleClickTolerance ? Clamp(baseline * 2) : Clamp(baseline);

    /// <summary>연속 delta(DIP 상당) 기반 배수: 1 + delta*.01.</summary>
    public static double ApplyContinuousDelta(double current, double delta)
        => Clamp(current * (1 + delta * ContinuousSensitivity));

    public static bool IsAtFit(FitMode current, FitMode target, double zoom, double baseline)
        => current == target && Math.Abs(zoom - baseline) < FitTolerance;

    /// <summary>geometry 변경 시 zoom을 baseline으로 재설정할지 결정한다(03 재맞춤 규칙).</summary>
    public static bool ShouldReset(bool force, bool initialFitApplied, bool sizeLocked, bool userHasZoomedAway)
    {
        if (force || !initialFitApplied) return true;
        if (sizeLocked) return false;
        if (userHasZoomedAway) return false;
        return true;
    }
}
