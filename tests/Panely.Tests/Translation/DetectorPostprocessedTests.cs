using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Panely.Domain.Translation;

namespace Panely.Tests.Translation;

public class DetectorPostprocessedTests
{
    [Fact]
    public void Decodes_absolute_xyxy_and_scales_to_image()
    {
        long[] labels = [1, 0, 2, 0];
        float[] scores = [0.9f, 0.95f, 0.3f, 0.5f];
        float[] boxes =
        [
            64, 64, 128, 192,     // text in balloon
            0, 0, 320, 320,       // balloon
            10, 10, 20, 20,       // below threshold
            600, 600, 700, 700,   // clamped to 640 → still valid area
        ];
        var result = DetectorDecoder.DecodePostprocessed(labels, scores, boxes, 640, 640, 1280, 1920);

        Assert.Equal(3, result.Count);
        Assert.Equal(DetectionKind.Balloon, result[0].Kind);
        Assert.Equal(0.95, result[0].Score, 3);
        Assert.Equal(new Panely.Domain.Model.RectD(0, 0, 640, 960), result[0].Rect);
        Assert.Equal((DetectionKind)1, result[1].Kind);
        Assert.Equal(128, result[1].Rect.X, 6);
        Assert.Equal(192, result[1].Rect.Y, 6);
        Assert.Equal(128, result[1].Rect.Width, 6);
        Assert.Equal(384, result[1].Rect.Height, 6);
        // 화면 밖 좌표는 이미지 경계로 clamp
        Assert.True(result[2].Rect.Right <= 1280 && result[2].Rect.Bottom <= 1920);
    }

    [Fact]
    public void Skips_unknown_labels_and_degenerate_boxes()
    {
        long[] labels = [7, 0];
        float[] scores = [0.99f, 0.99f];
        float[] boxes = [0, 0, 10, 10, 50, 50, 50, 80];
        Assert.Empty(DetectorDecoder.DecodePostprocessed(labels, scores, boxes, 640, 640, 640, 640));
    }

    /// <summary>
    /// 실제 모델 확인(선택): PANELY_TEST_DETECTOR_MODEL=detector.onnx 경로를 지정하면 실행한다.
    /// 문서의 번역 화면 스크린샷에서 말풍선/텍스트를 찾는지 본다.
    /// </summary>
    [Fact]
    public void Real_detector_finds_balloons_when_model_available()
    {
        var model = Environment.GetEnvironmentVariable("PANELY_TEST_DETECTOR_MODEL");
        if (string.IsNullOrEmpty(model) || !File.Exists(model)) return;
        var image = FindRepoFile("docs/windows/assets/screenshots/18-translation-before-after.png");
        if (image is null) return;

        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(image), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(bgra, new System.Windows.Media.ScaleTransform(640.0 / bgra.PixelWidth, 640.0 / bgra.PixelHeight));
        var px = new byte[640 * 640 * 4];
        scaled.CopyPixels(px, 640 * 4, 0);
        var tensor = new DenseTensor<float>([1, 3, 640, 640]);
        for (var y = 0; y < 640; y++)
            for (var x = 0; x < 640; x++)
            {
                var i = (y * 640 + x) * 4;
                tensor[0, 0, y, x] = px[i + 2] / 255f;
                tensor[0, 1, y, x] = px[i + 1] / 255f;
                tensor[0, 2, y, x] = px[i] / 255f;
            }

        using var session = new InferenceSession(model);
        Assert.Contains("orig_target_sizes", session.InputMetadata.Keys);
        Assert.Contains("labels", session.OutputMetadata.Keys);
        using var outputs = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("images", tensor),
            NamedOnnxValue.CreateFromTensor("orig_target_sizes", new DenseTensor<long>(new long[] { 640, 640 }, [1, 2])),
        ]);
        var labels = outputs.First(o => o.Name == "labels").AsEnumerable<long>().ToArray();
        var scores = outputs.First(o => o.Name == "scores").AsEnumerable<float>().ToArray();
        var boxes = outputs.First(o => o.Name == "boxes").AsEnumerable<float>().ToArray();
        Assert.All(scores, s => Assert.InRange(s, 0f, 1f)); // 이미 확률: sigmoid를 다시 적용하지 않는다

        var regions = DetectorDecoder.DecodePostprocessed(labels, scores, boxes, 640, 640, bgra.PixelWidth, bgra.PixelHeight);
        Assert.True(regions.Count(r => r.Kind == DetectionKind.Balloon) >= 4);
        Assert.Contains(regions, r => r.Kind != DetectionKind.Balloon);
    }

    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
