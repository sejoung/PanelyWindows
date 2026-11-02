namespace Panely.Core.Translation;

/// <summary>
/// 모델 세션을 쓰는 동안 잡는 잠금. 닫으면 쓰는 중인 호출이 끝나기를 잠깐 기다렸다가 세션을 풀고, 그 뒤의 호출은 모델을 다시 올리지 않고 실패한다.
/// 말풍선 추가의 OCR과 PrepareAsync는 스케줄러 밖에서 도므로, 종료할 때 쓰는 중인 네이티브 세션을 풀면 프로세스가 죽을 수 있다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim은 Wait 계열만 사용해 대기 핸들을 만들지 않는다. 닫은 뒤 늦게 끝나는 호출의 Release와 경합하지 않도록 해제하지 않는다.")]
internal sealed class ModelGate(TimeSpan? closeTimeout = null)
{
    /// <summary>앱 종료는 전체 5초만 기다리고(MainWindow.OnClosing), 공급자는 모두 합쳐 네 개다.</summary>
    private static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _closeTimeout = closeTimeout ?? DefaultCloseTimeout;
    private volatile bool _closed;

    /// <exception cref="ObjectDisposedException">이미 닫혔다.</exception>
    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_closed) return;
        _gate.Release();
        throw new ObjectDisposedException(nameof(ModelGate));
    }

    public void Exit() => _gate.Release();

    /// <summary>
    /// 닫고 잠금을 얻어 release를 부른다. 시간 안에 얻지 못하면 쓰는 중인 세션을 풀지 않는다(프로세스 종료가 회수한다).
    /// 호출 스레드를 막으므로 UI 스레드에서 부르지 않는다.
    /// </summary>
    /// <returns>release를 불렀는지.</returns>
    public bool Close(Action release)
    {
        _closed = true;
        if (!_gate.Wait(_closeTimeout)) return false;
        try
        {
            release();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
