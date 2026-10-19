using System.Text;
using System.Text.RegularExpressions;
using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>
/// manga-ocr(kha-white/manga-ocr-base, ONNX) 토큰 해석. comic-translate의 MangaOCRONNX와 같은 규칙:
/// [CLS]=2로 시작, [SEP]=3에서 끝, id&lt;5(특수 토큰) 무시, 공백 제거, … → ..., 반각 → 전각.
/// </summary>
public static partial class MangaOcrText
{
    public const int StartToken = 2;
    public const int EndToken = 3;
    public const int FirstRealToken = 5;
    public const int MaxTokens = 300;

    public static string Decode(IEnumerable<long> tokens, IReadOnlyList<string> vocab)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            if (t < FirstRealToken) continue;
            if (t < vocab.Count) sb.Append(vocab[(int)t]);
        }
        return Postprocess(sb.ToString());
    }

    public static string Postprocess(string text)
    {
        var s = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        s = s.Replace("…", "...", StringComparison.Ordinal);
        s = DotRun().Replace(s, m => new string('.', m.Length));
        return HalfToFullWidth(s);
    }

    /// <summary>jaconv.h2z(ascii=True, digit=True): ASCII 문자·숫자를 전각으로.</summary>
    public static string HalfToFullWidth(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= '!' and <= '~' ? (char)(c + 0xFEE0) : c);
        return sb.ToString();
    }

    [GeneratedRegex("[・.]{2,}")]
    private static partial Regex DotRun();
}

/// <summary>PaddleOCR식 CTC greedy 해석. 0번은 blank, 사전 뒤에 공백 문자가 붙을 수 있다.</summary>
public static class CtcDecoder
{
    /// <summary>logits: [T, C] 한 줄. 결과 텍스트와 평균 확률.</summary>
    public static (string Text, double Confidence) Decode(ReadOnlySpan<float> logits, int steps, int classes, IReadOnlyList<string> dictionary)
    {
        // 클래스 수에 맞춰 사전을 정렬: blank + dict (+ space)
        string Char(int i)
        {
            if (i <= 0) return string.Empty;
            if (i <= dictionary.Count) return dictionary[i - 1];
            return i == dictionary.Count + 1 ? " " : string.Empty;
        }

        var softmax = NeedsSoftmax(logits);
        var sb = new StringBuilder();
        double sum = 0;
        var count = 0;
        var last = -1;
        for (var t = 0; t < steps; t++)
        {
            var row = logits.Slice(t * classes, classes);
            var best = 0;
            var bestValue = row[0];
            for (var c = 1; c < classes; c++)
                if (row[c] > bestValue) { bestValue = row[c]; best = c; }
            if (best != 0 && best != last)
            {
                var ch = Char(best);
                if (ch.Length > 0 && !ch.Any(char.IsControl))
                {
                    sb.Append(ch);
                    sum += softmax ? Probability(row, best) : bestValue;
                    count++;
                }
            }
            last = best;
        }
        return (sb.ToString().Trim(), count > 0 ? sum / count : 0);
    }

    private static bool NeedsSoftmax(ReadOnlySpan<float> logits)
    {
        foreach (var v in logits) if (v > 1f || v < 0f) return true;
        return false;
    }

    private static double Probability(ReadOnlySpan<float> row, int index)
    {
        var max = float.MinValue;
        foreach (var v in row) if (v > max) max = v;
        double total = 0;
        foreach (var v in row) total += Math.Exp(v - max);
        return Math.Exp(row[index] - max) / total;
    }
}

/// <summary>
/// PP-OCR DB 탐지 확률 지도 → 글자 줄 사각형. 원 구현(다각형 최소 외접 사각형 + 다각형 확장) 대신
/// 만화 대사처럼 기울지 않은 글자를 가정해 축 정렬 사각형으로 단순화했다.
/// thresh .3 이진화 → 4-연결 성분 → 성분 평균 확률 ≥ boxThresh → unclip(거리 = 면적×ratio/둘레)만큼 확장.
/// </summary>
public static class DbPostProcess
{
    public static List<RectD> Boxes(ReadOnlySpan<float> probability, int width, int height,
        double thresh = 0.3, double boxThresh = 0.5, double unclipRatio = 2.0, int minSide = 3)
    {
        var mask = new bool[width * height];
        for (var i = 0; i < mask.Length; i++) mask[i] = probability[i] > thresh;
        var seen = new bool[width * height];
        var boxes = new List<RectD>();
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            int minX = width, minY = height, maxX = -1, maxY = -1, pixels = 0;
            double score = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int x = p % width, y = p / width;
                pixels++;
                score += probability[p];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (x > 0) Visit(p - 1);
                if (x < width - 1) Visit(p + 1);
                if (y > 0) Visit(p - width);
                if (y < height - 1) Visit(p + width);
            }
            var w = maxX - minX + 1;
            var h = maxY - minY + 1;
            if (Math.Min(w, h) < minSide || score / pixels < boxThresh) continue;
            var distance = w * h * unclipRatio / (2.0 * (w + h));
            boxes.Add(RectD.FromEdges(
                Math.Max(0, minX - distance), Math.Max(0, minY - distance),
                Math.Min(width, maxX + 1 + distance), Math.Min(height, maxY + 1 + distance)));
        }
        return boxes;

        void Visit(int q)
        {
            if (!mask[q] || seen[q]) return;
            seen[q] = true;
            stack.Push(q);
        }
    }

    public const int DefaultLimitSide = 960;

    /// <summary>DB 입력 크기: 짧은 변이 limit보다 작으면 키우고, 32의 배수로 맞춘다(PP-OCR limit_type=min).</summary>
    public static (int Width, int Height) InputSize(int width, int height, int limitSideLength = DefaultLimitSide, int maxSide = 2400)
    {
        var ratio = Math.Min(width, height) < limitSideLength ? limitSideLength / (double)Math.Min(width, height) : 1.0;
        // 아주 긴 웹툰 조각에서 입력이 지나치게 커지지 않게 긴 변을 제한한다
        if (Math.Max(width, height) * ratio > maxSide) ratio = maxSide / (double)Math.Max(width, height);
        static int Snap(double v) => Math.Max(32, (int)Math.Round(v / 32, MidpointRounding.AwayFromZero) * 32);
        return (Snap(width * ratio), Snap(height * ratio));
    }
}
