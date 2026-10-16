using System.Globalization;

namespace Panely.Domain.Library;

/// <summary>
/// 자연 정렬: 숫자 run은 숫자처럼(1,2,10), 나머지는 UI locale의 case-insensitive 비교.
/// 동률은 ordinal 전체 문자열로 결정한다(Windows 결정). 전각 숫자도 숫자로 취급한다.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    private readonly CompareInfo _compareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    public static NaturalComparer Invariant { get; } = new(CultureInfo.InvariantCulture);

    public NaturalComparer(CultureInfo culture) => _compareInfo = culture.CompareInfo;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var result = CompareNatural(x, y);
        return result != 0 ? result : string.CompareOrdinal(x, y);
    }

    private int CompareNatural(string x, string y)
    {
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var dx = DigitValue(x[i]);
            var dy = DigitValue(y[j]);
            if (dx >= 0 && dy >= 0)
            {
                var si = i; var sj = j;
                while (i < x.Length && DigitValue(x[i]) >= 0) i++;
                while (j < y.Length && DigitValue(y[j]) >= 0) j++;
                var c = CompareDigitRuns(x, si, i, y, sj, j);
                if (c != 0) return c;
                continue;
            }

            if (dx >= 0 != dy >= 0)
            {
                // 숫자가 문자보다 앞선다
                return dx >= 0 ? -1 : 1;
            }

            var ti = i; var tj = j;
            while (i < x.Length && DigitValue(x[i]) < 0) i++;
            while (j < y.Length && DigitValue(y[j]) < 0) j++;
            var textCompare = _compareInfo.Compare(x, ti, i - ti, y, tj, j - tj, Options);
            if (textCompare != 0) return Math.Sign(textCompare);
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }

    private static int CompareDigitRuns(string x, int xs, int xe, string y, int ys, int ye)
    {
        // 앞의 0 무시
        while (xs < xe - 1 && DigitValue(x[xs]) == 0) xs++;
        while (ys < ye - 1 && DigitValue(y[ys]) == 0) ys++;
        var lx = xe - xs;
        var ly = ye - ys;
        if (lx != ly) return lx.CompareTo(ly);
        for (int a = xs, b = ys; a < xe; a++, b++)
        {
            var c = DigitValue(x[a]).CompareTo(DigitValue(y[b]));
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>ASCII 및 전각 숫자만 숫자로 취급(0..9), 아니면 -1.</summary>
    internal static int DigitValue(char c)
    {
        if (c is >= '0' and <= '9') return c - '0';
        if (c is >= '０' and <= '９') return c - '０';
        return -1;
    }
}
