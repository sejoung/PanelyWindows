using Panely.Domain.Reading;

namespace Panely.Core.Reader;

/// <summary>
/// Windows 휠 입력 정규화(03: 보정 계수는 입력 어댑터 한 곳). WHEEL_DELTA=120을 그대로 zoom/scroll에 넣지 않는다.
/// - 120의 배수만 오면 일반 휠(Discrete). 120의 배수가 아닌 delta를 최근에 받았으면 정밀 장치(phase 없음).
/// - 일반 WM_MOUSEWHEEL을 무조건 터치패드로 추정하지 않는다.
/// - WPF Delta &gt; 0 은 위로 굴림 = 논리 backward.
/// </summary>
public sealed class WheelInputAdapter
{
    public const int WheelDelta = 120;
    /// <summary>한 notch 상당 스크롤 거리(DIP). 정밀 delta도 같은 비율로 DIP 상당값으로 바꾼다.</summary>
    public const double DipPerNotch = 48;
    public static readonly TimeSpan PreciseMemory = TimeSpan.FromMilliseconds(600);

    private TimeSpan? _lastPreciseSeen;

    public WheelDevice Classify(int rawDelta, TimeSpan timestamp)
    {
        if (rawDelta % WheelDelta != 0)
        {
            _lastPreciseSeen = timestamp;
            return WheelDevice.PrecisePhaseless;
        }
        if (_lastPreciseSeen is { } last && timestamp - last <= PreciseMemory)
        {
            _lastPreciseSeen = timestamp;
            return WheelDevice.PrecisePhaseless;
        }
        return WheelDevice.Discrete;
    }

    /// <summary>논리 forward 방향 DIP 상당 delta(아래로 굴리면 양수).</summary>
    public static double ForwardDip(int rawDelta) => -rawDelta / (double)WheelDelta * DipPerNotch;

    /// <summary>스크롤(pan) 거리 DIP. 위로 굴리면 음수(위로 이동).</summary>
    public static double ScrollDip(int rawDelta) => -rawDelta / (double)WheelDelta * DipPerNotch;

    /// <summary>Ctrl+wheel 확대 delta: notch 당 10(약 10%), 정밀은 비례.</summary>
    public static double ZoomDelta(int rawDelta) => rawDelta / (double)WheelDelta * ZoomPolicy.DiscreteDeltaPerNotch;

    public WheelInput ToEngineInput(int rawDelta, TimeSpan timestamp, bool atForwardEdge, bool atBackwardEdge)
        => new(Classify(rawDelta, timestamp), WheelPhase.None, ForwardDip(rawDelta), atForwardEdge, atBackwardEdge, timestamp);

    public void Reset() => _lastPreciseSeen = null;
}
