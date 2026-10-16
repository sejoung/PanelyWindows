using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Panely.App.Shell;

/// <summary>
/// 사용자 세션별 single instance(07). 명명 Mutex로 첫 인스턴스를 정하고, 후속 실행은 named pipe로
/// 열기 요청(파일/폴더 경로만)을 전달한 뒤 종료한다. IPC 입력은 경로 목록만 허용하며 임의 명령을 실행하지 않는다.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;

    public SingleInstance(string appId = "Panely")
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
        var dataRoot = Environment.GetEnvironmentVariable("PANELY_DATA_ROOT");
        if (!string.IsNullOrEmpty(dataRoot))
            suffix += Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot)))[..8];
        _mutexName = $"Local\\{appId}-{suffix}";
        _pipeName = $"{appId}-{suffix}";
    }

    /// <summary>열기 요청 수신(백그라운드 스레드).</summary>
    public event Action<IReadOnlyList<string>>? OpenRequested;

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

    /// <summary>실행 중인 인스턴스에 경로를 전달(최대 3초).</summary>
    public bool SendToPrimary(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new IpcMessage { Paths = paths.ToArray() });
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void StartServer()
    {
        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[4096];
                    int read;
                    while ((read = await server.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                    {
                        buffer.Write(chunk, 0, read);
                        if (buffer.Length > MaxMessageBytes) break;
                    }
                    if (buffer.Length is 0 or > MaxMessageBytes) continue;
                    var message = JsonSerializer.Deserialize<IpcMessage>(buffer.ToArray());
                    var paths = Sanitize(message?.Paths);
                    if (paths.Count > 0) OpenRequested?.Invoke(paths);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // 잘못된 메시지는 무시하고 다음 연결을 기다린다
                }
            }
        }, token);
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
