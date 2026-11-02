using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Panely.App.Shell;

/// <summary>
/// 사용자 세션별 single instance(07). 명명 Mutex로 첫 인스턴스를 정하고, 후속 실행은 named pipe로
/// 열기 요청(파일/폴더 경로만)을 전달한 뒤 종료한다. IPC 입력은 경로 목록만 허용하며 임의 명령을 실행하지 않는다.
/// 빈 경로 목록은 "실행 중인 창을 앞으로"라는 뜻이다.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxQueuedRequests = 32;
    private static readonly TimeSpan DefaultSendBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly object _requestGate = new();
    private Queue<IReadOnlyList<string>>? _pendingRequests = new();
    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private int _serverFaults;

    public SingleInstance(string appId = "Panely")
    {
        (_mutexName, _pipeName) = NamesFor(appId, Environment.GetEnvironmentVariable("PANELY_DATA_ROOT"));
    }

    /// <summary>열기 요청 수신(백그라운드 스레드). <see cref="MarkReady"/> 전에 온 요청은 그때 차례로 전달한다.</summary>
    public event Action<IReadOnlyList<string>>? OpenRequested;

    /// <summary>연결한 클라이언트가 메시지를 끝까지 보내야 하는 시간. 넘기면 끊고 다음 연결을 받는다.</summary>
    internal TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal string PipeName => _pipeName;

    /// <summary>시작 후 서버 루프가 예외로 재시도한 횟수(테스트·진단).</summary>
    internal int ServerFaults => Volatile.Read(ref _serverFaults);

    internal static (string MutexName, string PipeName) NamesFor(string appId, string? dataRoot)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
        if (!string.IsNullOrEmpty(dataRoot))
            suffix += Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot)))[..8];
        return ($"Local\\{appId}-{suffix}", $"{appId}-{suffix}");
    }

    /// <summary>첫 인스턴스면 true.</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var created);
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return created;
    }

    /// <summary>
    /// 실행 중인 인스턴스에 경로를 전달한다. 빈 목록이면 창만 앞으로 가져오게 한다.
    /// 첫 인스턴스가 아직 서버를 열기 전일 수 있으므로 제한 시간(기본 10초) 안에서 다시 시도한다.
    /// </summary>
    public bool SendToPrimary(IReadOnlyList<string> paths, TimeSpan? budget = null)
    {
        var limit = budget ?? DefaultSendBudget;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new IpcMessage { Paths = paths.ToArray() });
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect((int)Math.Max(1, (limit - clock.Elapsed).TotalMilliseconds));
                // 포그라운드 권한은 사용자가 방금 실행한 이쪽에 있다. 넘겨주지 않으면 첫 인스턴스의 Activate가 작업 표시줄 깜빡임으로 끝난다.
                if (NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var primary))
                    _ = NativeMethods.AllowSetForegroundWindow(primary);
                client.Write(payload);
                client.Flush();
                return true;
            }
            catch (Exception)
            {
                // 시간 초과·서버 교체 중(busy)·연결 끊김: 남은 시간 안에서 다시 시도한다
            }
            if (clock.Elapsed >= limit) return false;
            Thread.Sleep(SendRetryDelay);
        }
    }

    public void StartServer()
    {
        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;
        _ = Task.Run(() => ServeAsync(token), token);
    }

    /// <summary>창이 요청을 받을 수 있게 되면 호출한다. 그때까지 쌓인 요청을 온 순서대로 전달한다.</summary>
    public void MarkReady()
    {
        // 처리기는 Dispatcher에 넘기기만 하므로 lock 안에서 호출해 뒤이어 도착한 요청과 순서가 바뀌지 않게 한다
        lock (_requestGate)
        {
            var pending = _pendingRequests;
            _pendingRequests = null;
            while (pending is { Count: > 0 }) OpenRequested?.Invoke(pending.Dequeue());
        }
    }

    internal void Deliver(IReadOnlyList<string> paths)
    {
        lock (_requestGate)
        {
            if (_pendingRequests is { } pending)
            {
                // 마지막 요청이 화면을 차지하므로 넘치면 오래된 것부터 버린다
                if (pending.Count >= MaxQueuedRequests) pending.Dequeue();
                pending.Enqueue(paths);
                return;
            }
            OpenRequested?.Invoke(paths);
        }
    }

    private async Task ServeAsync(CancellationToken token)
    {
        var consecutiveFaults = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                consecutiveFaults = 0;
                var payload = await ReadMessageAsync(server, token).ConfigureAwait(false);
                if (payload is not null && TryParseRequest(payload, out var paths)) Deliver(paths);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // 읽기 시간 초과: 메시지를 보내지 않는 클라이언트를 끊고 다음 연결을 기다린다
            }
            catch (Exception)
            {
                // 서버 생성 실패가 계속되어도 코어를 점유하지 않게 간격을 늘려 다시 시도한다
                Interlocked.Increment(ref _serverFaults);
                try { await Task.Delay(FaultDelay(++consecutiveFaults), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<byte[]?> ReadMessageAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await server.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxMessageBytes) return null;
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>연속 실패 횟수별 재시도 간격: 100ms부터 두 배씩, 최대 5초.</summary>
    internal static TimeSpan FaultDelay(int consecutiveFaults)
        => TimeSpan.FromMilliseconds(Math.Min(5000, 100 * Math.Pow(2, Math.Max(0, consecutiveFaults - 1))));

    /// <summary>
    /// 메시지를 요청으로 해석한다. 빈 목록은 활성화 요청이고, 경로가 있었는데 모두 걸러졌거나 형식이 틀리면 무시한다.
    /// </summary>
    internal static bool TryParseRequest(byte[] payload, out IReadOnlyList<string> paths)
    {
        paths = [];
        IpcMessage? message;
        try { message = JsonSerializer.Deserialize<IpcMessage>(payload); }
        catch (JsonException) { return false; }
        if (message?.Paths is not { } raw) return false;
        paths = Sanitize(raw);
        return paths.Count > 0 || raw.Length == 0;
    }

    /// <summary>경로만 허용: 절대 경로, 존재 여부는 열기 파이프라인이 판단.</summary>
    internal static IReadOnlyList<string> Sanitize(string[]? paths)
    {
        if (paths is null) return [];
        var result = new List<string>();
        foreach (var p in paths.Take(32))
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length > 4096) continue;
            if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            if (!Path.IsPathFullyQualified(p)) continue;
            result.Add(p);
        }
        return result;
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        try { _mutex?.ReleaseMutex(); } catch (Exception) { }
        _mutex?.Dispose();
    }

    private sealed class IpcMessage
    {
        public string[]? Paths { get; set; }
    }
}
