namespace Panely.Domain.Reading;

public enum WheelDevice
{
    /// <summary>일반 마우스 휠(WHEEL_DELTA notch).</summary>
    Discrete,
    /// <summary>began/changed/ended/momentum phase를 신뢰할 수 있는 정밀 장치.</summary>
    PrecisePhased,
    /// <summary>phase 없는 정밀 장치(대부분의 Windows 정밀 터치패드 WM_MOUSEWHEEL).</summary>
    PrecisePhaseless,
}

public enum WheelPhase { None, Began, Changed, Ended, Momentum, MomentumEnded }

public enum WheelOutcome { Pan, Swallow, TurnForward, TurnBackward }

/// <summary>
/// 입력 어댑터에서 정규화된 휠 입력. ForwardDelta &gt; 0 이면 논리 forward(아래).
/// 부호/자연 스크롤 정규화는 어댑터에서 한 번만 한다. 단위는 DIP 상당값.
/// </summary>
public readonly record struct WheelInput(
    WheelDevice Device,
    WheelPhase Phase,
    double ForwardDelta,
    bool AtForwardEdge,
    bool AtBackwardEdge,
    TimeSpan Timestamp);

/// <summary>
/// 단일/두 장 레이아웃의 휠 페이지 넘김 상태 머신(03-reader.md).
/// Ctrl-zoom·핀치·vertical·옵션 off는 호출 전에 걸러낸다.
/// </summary>
public sealed class WheelPageTurnEngine
{
    public const double PreciseThreshold = 60;
    public static readonly TimeSpan DiscreteCooldown = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan PhaselessReset = TimeSpan.FromMilliseconds(250);

    private enum Eligibility { Undetermined, Eligible, Ineligible }

    // discrete
    private TimeSpan? _lastDiscreteTurn;

    // phased gesture
    private Eligibility _gesture = Eligibility.Undetermined;
    private int _gestureDirection;
    private double _accum;
    private bool _gestureTurned;

    // phaseless
    private TimeSpan? _lastPhaselessInput;
    private bool _phaselessLatched;
    private double _phaselessAccum;
    private int _phaselessDirection;

    public void Reset()
    {
        _lastDiscreteTurn = null;
        ResetGesture();
        _lastPhaselessInput = null;
        _phaselessLatched = false;
        _phaselessAccum = 0;
        _phaselessDirection = 0;
    }

    private void ResetGesture()
    {
        _gesture = Eligibility.Undetermined;
        _gestureDirection = 0;
        _accum = 0;
        _gestureTurned = false;
    }

    public WheelOutcome Process(WheelInput input) => input.Device switch
    {
        WheelDevice.Discrete => ProcessDiscrete(input),
        WheelDevice.PrecisePhased => ProcessPhased(input),
        _ => ProcessPhaseless(input),
    };

    private static bool AtEdge(WheelInput input, int direction)
        => direction > 0 ? input.AtForwardEdge : direction < 0 && input.AtBackwardEdge;

    private static WheelOutcome Turn(int direction) => direction > 0 ? WheelOutcome.TurnForward : WheelOutcome.TurnBackward;

    private WheelOutcome ProcessDiscrete(WheelInput input)
    {
        var direction = Math.Sign(input.ForwardDelta);
        if (direction == 0) return WheelOutcome.Swallow;
        if (_lastDiscreteTurn is { } last && input.Timestamp - last < DiscreteCooldown) return WheelOutcome.Swallow;
        if (!AtEdge(input, direction)) return WheelOutcome.Pan;
        _lastDiscreteTurn = input.Timestamp;
        return Turn(direction);
    }

    private WheelOutcome ProcessPhased(WheelInput input)
    {
        switch (input.Phase)
        {
            case WheelPhase.Began:
                ResetGesture();
                break;
            case WheelPhase.Ended:
            case WheelPhase.MomentumEnded:
                return _gestureTurned || _gesture == Eligibility.Eligible ? WheelOutcome.Swallow : WheelOutcome.Pan;
        }

        if (_gestureTurned) return WheelOutcome.Swallow;
        var delta = input.ForwardDelta;
        if (delta == 0) return _gesture == Eligibility.Ineligible ? WheelOutcome.Pan : WheelOutcome.Swallow;

        if (_gesture == Eligibility.Undetermined)
        {
            if (input.Phase == WheelPhase.Momentum)
            {
                // 관성만 남은 새 입력은 경계 판단 없이 pan
                _gesture = Eligibility.Ineligible;
                return WheelOutcome.Pan;
            }
            var direction = Math.Sign(delta);
            if (AtEdge(input, direction))
            {
                _gesture = Eligibility.Eligible;
                _gestureDirection = direction;
            }
            else
            {
                _gesture = Eligibility.Ineligible;
            }
        }

        if (_gesture == Eligibility.Ineligible) return WheelOutcome.Pan;

        _accum += delta * _gestureDirection;
        if (_accum < 0)
        {
            // 방향을 되돌린 제스처: 자격 해제
            _gesture = Eligibility.Ineligible;
            return WheelOutcome.Pan;
        }
        if (_accum >= PreciseThreshold)
        {
            _gestureTurned = true;
            return Turn(_gestureDirection);
        }
        return WheelOutcome.Swallow;
    }

    private WheelOutcome ProcessPhaseless(WheelInput input)
    {
        if (_lastPhaselessInput is { } last && input.Timestamp - last > PhaselessReset)
        {
            _phaselessLatched = false;
            _phaselessAccum = 0;
            _phaselessDirection = 0;
        }
        _lastPhaselessInput = input.Timestamp;

        if (_phaselessLatched) return WheelOutcome.Swallow;
        var direction = Math.Sign(input.ForwardDelta);
        if (direction == 0) return WheelOutcome.Swallow;

        if (!AtEdge(input, direction))
        {
            _phaselessAccum = 0;
            _phaselessDirection = 0;
            return WheelOutcome.Pan;
        }

        if (_phaselessDirection != direction)
        {
            _phaselessDirection = direction;
            _phaselessAccum = 0;
        }
        _phaselessAccum += Math.Abs(input.ForwardDelta);
        if (_phaselessAccum >= PreciseThreshold)
        {
            _phaselessLatched = true;
            _phaselessAccum = 0;
            return Turn(direction);
        }
        return WheelOutcome.Swallow;
    }
}
