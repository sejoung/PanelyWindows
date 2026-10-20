using System.Diagnostics;
using System.Text.Json;
using Panely.Core.Translation;

namespace Panely.App.Tests;

/// <summary>
/// 실제 번역 모델 확인(선택): PANELY_TEST_LLM_MODEL=gemma-4-E2B_q4_0-it.gguf 경로를 지정하면 실행한다.
/// 앱과 같은 네이티브(LLamaSharp CPU·Vulkan)로 모델을 올려 일본어 → 한국어를 번역한다. 모델 파일은 복사하지 않고 같은 드라이브에 하드 링크로 설치 폴더를 만든다.
/// </summary>
public class GemmaTranslatorIntegrationTests
{
    [Fact]
    public async Task Real_model_translates_japanese_into_korean_when_available()
    {
        var model = Environment.GetEnvironmentVariable("PANELY_TEST_LLM_MODEL");
        if (string.IsNullOrEmpty(model) || !File.Exists(model)) return;

        var manifest = ModelCatalog.BuiltIn.Single(m => m.Id == ModelCatalog.TranslatorId);
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(model))!, "panely-llm-test-" + Guid.NewGuid().ToString("N"));
        var store = new ModelStore(root, manifests: ModelCatalog.BuiltIn);
        var installDir = store.InstallDirectory(manifest);
        Directory.CreateDirectory(installDir);
        try
        {
            var link = Path.Combine(installDir, manifest.InputString("model")!);
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{Path.GetFullPath(model)}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                await mklink.WaitForExitAsync();
            File.WriteAllText(Path.Combine(installDir, "installed.json"), JsonSerializer.Serialize(new { manifest.Id, manifest.Revision }));
            Assert.True(store.IsInstalled(manifest), "model file size must match the pinned manifest");

            using var translator = new GemmaTranslator(store);
            Assert.True(translator.GetCapability("ja", "ko").IsReady);
            var results = await translator.TranslateAsync(
                [new TranslationRequestItem("a", "こんにちは"), new TranslationRequestItem("b", "ありがとうございます")],
                [new TranslationContextLine("おはよう", "좋은 아침")], "ja", "ko", CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.All(results, r =>
            {
                Assert.Null(r.Error);
                Assert.False(string.IsNullOrWhiteSpace(r.Text));
                Assert.Contains(r.Text!, c => c is >= '가' and <= '힣');
            });
            Assert.Contains("Gemma 4 E2B", translator.Name, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
