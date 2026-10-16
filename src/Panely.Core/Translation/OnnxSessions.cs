using Microsoft.ML.OnnxRuntime;

namespace Panely.Core.Translation;

public enum InferenceDevice { Cpu, Gpu }

/// <summary>
/// ONNX Runtime 세션 생성. GPU(DirectML, DirectX 12 GPU면 제조사 무관)를 먼저 시도하고 실패하면 CPU로 돌아간다.
/// 07 문서대로 GPU 경로를 고정하지 않고 이 한 곳에서 고른다.
/// PANELY_ORT_DEVICE=cpu 로 GPU를 끌 수 있다(문제 확인용).
/// </summary>
public static class OnnxSessions
{
    private static readonly object Gate = new();
    private static string? _gpuFailure;

    /// <summary>환경 변수로 GPU를 껐는지.</summary>
    public static bool GpuDisabledByUser =>
        string.Equals(Environment.GetEnvironmentVariable("PANELY_ORT_DEVICE"), "cpu", StringComparison.OrdinalIgnoreCase);

    /// <summary>마지막으로 GPU 세션 생성에 실패한 이유(사용자 안내·진단용).</summary>
    public static string? GpuFailure { get { lock (Gate) return _gpuFailure; } }

    /// <summary>가장 최근에 만든 세션이 실제로 쓰는 장치.</summary>
    public static InferenceDevice? LastDevice { get; private set; }

    /// <summary>
    /// 모델 세션을 만든다. preferGpu=false면 CPU만 쓴다(int8 양자화 모델은 GPU 이득이 작거나 연산자가 CPU로 쪼개져 더 느릴 수 있음).
    /// </summary>
    public static (InferenceSession Session, InferenceDevice Device) Create(string modelPath, bool preferGpu = true)
    {
        if (preferGpu && !GpuDisabledByUser)
        {
            try
            {
                // DirectML 실행 공급자 요구 사항: 메모리 패턴 끄기, 순차 실행
                var gpu = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };
                gpu.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(modelPath, gpu);
                LastDevice = InferenceDevice.Gpu;
                return (session, InferenceDevice.Gpu);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
            {
                lock (Gate) _gpuFailure = ex.Message;
            }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
        };
        var cpuSession = new InferenceSession(modelPath, cpu);
        LastDevice = InferenceDevice.Cpu;
        return (cpuSession, InferenceDevice.Cpu);
    }
}
