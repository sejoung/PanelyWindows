using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>
/// RT-DETR 출력 해석: scores [1,Q,3](sigmoid 적용 완료), boxes [1,Q,4](normalized cx,cy,w,h).
/// 한 query가 여러 class를 통과할 수 있다. score 내림차순.
/// </summary>
public static class DetectorDecoder
{
    public const double DefaultThreshold = 0.4;
    public const int ClassCount = 3;

    public static List<DetectedRegion> Decode(ReadOnlySpan<float> scores, ReadOnlySpan<float> boxes, int queries,
        double imageWidth, double imageHeight, double threshold = DefaultThreshold)
    {
        var result = new List<DetectedRegion>();
        for (var q = 0; q < queries; q++)
        {
            if ((q + 1) * 4 > boxes.Length || (q + 1) * ClassCount > scores.Length) break;
            var cx = boxes[q * 4];
            var cy = boxes[q * 4 + 1];
            var w = boxes[q * 4 + 2];
            var h = boxes[q * 4 + 3];
            for (var c = 0; c < ClassCount; c++)
            {
                var s = scores[q * ClassCount + c];
                if (!(s >= threshold)) continue;
                result.Add(new DetectedRegion(ToRect(cx, cy, w, h, imageWidth, imageHeight), (DetectionKind)c, s));
            }
        }
        result.Sort((a, b) => b.Score.CompareTo(a.Score));
        return result;
    }

    /// <summary>
    /// 후처리가 포함된 RT-DETR deploy export(실측: ogkalu/comic-text-and-bubble-detector detector.onnx):
    /// labels [1,Q] int64, scores [1,Q] 확률, boxes [1,Q,4] = orig_target_sizes 좌표계의 절대 x1,y1,x2,y2.
    /// query당 class 하나. targetWidth/Height는 모델에 넣은 orig_target_sizes, 결과는 imageWidth/Height로 환산한다.
    /// </summary>
    public static List<DetectedRegion> DecodePostprocessed(ReadOnlySpan<long> labels, ReadOnlySpan<float> scores, ReadOnlySpan<float> boxes,
        double targetWidth, double targetHeight, double imageWidth, double imageHeight, double threshold = DefaultThreshold)
    {
        var result = new List<DetectedRegion>();
        var queries = Math.Min(labels.Length, Math.Min(scores.Length, boxes.Length / 4));
        var sx = targetWidth > 0 ? imageWidth / targetWidth : 1;
        var sy = targetHeight > 0 ? imageHeight / targetHeight : 1;
        for (var q = 0; q < queries; q++)
        {
            var s = scores[q];
            var label = labels[q];
            if (!(s >= threshold) || label < 0 || label >= ClassCount) continue;
            var x1 = Math.Clamp(boxes[q * 4] * sx, 0, imageWidth);
            var y1 = Math.Clamp(boxes[q * 4 + 1] * sy, 0, imageHeight);
            var x2 = Math.Clamp(boxes[q * 4 + 2] * sx, 0, imageWidth);
            var y2 = Math.Clamp(boxes[q * 4 + 3] * sy, 0, imageHeight);
            if (x2 <= x1 || y2 <= y1) continue;
            result.Add(new DetectedRegion(RectD.FromEdges(x1, y1, x2, y2), (DetectionKind)label, s));
        }
        result.Sort((a, b) => b.Score.CompareTo(a.Score));
        return result;
    }

    public static RectD ToRect(double cx, double cy, double w, double h, double imageWidth, double imageHeight)
        => new((cx - w / 2) * imageWidth, (cy - h / 2) * imageHeight, w * imageWidth, h * imageHeight);
}
