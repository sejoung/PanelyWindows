using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.Core.Reader;

/// <summary>리더 화면 문구 조합(카운터·오류 설명).</summary>
public static class ReaderText
{
    /// <summary>오류 종류별 원인 설명. 공급자 메시지가 있으면 덧붙인다.</summary>
    public static string DescribeError(OpenErrorKind kind, string? detail, OpenIntent intent)
    {
        var text = kind switch
        {
            OpenErrorKind.NotFound when intent == OpenIntent.Recent => Loc.T("This recent book can no longer be opened."),
            OpenErrorKind.NotFound when intent == OpenIntent.Bookmark => Loc.T("This bookmarked book can no longer be opened."),
            OpenErrorKind.NotFound => Loc.T("The file or folder could not be found."),
            OpenErrorKind.TemporarilyUnavailable => Loc.T("This book is currently unavailable. Reconnect its drive or restore the file."),
            OpenErrorKind.AccessDenied => Loc.T("Panely doesn't have permission to read this file or folder."),
            OpenErrorKind.Unsupported => Loc.T("This file type isn't supported. Open a folder, CBZ/CBR, or ZIP/RAR."),
            OpenErrorKind.Empty => Loc.T("Folder is empty or has no supported content"),
            OpenErrorKind.Encrypted => Loc.T("Password-protected archives are not supported."),
            OpenErrorKind.Corrupt => Loc.T("The archive could not be opened."),
            OpenErrorKind.ExtractionLimit => Loc.T("Archive expanded past the safety limit (%lld MB).", 5L * 1024),
            OpenErrorKind.InnerPathMissing => Loc.T("The saved book is no longer available in this source."),
            _ => Loc.T("Failed to load"),
        };
        if (!string.IsNullOrWhiteSpace(detail) && kind is OpenErrorKind.Unknown or OpenErrorKind.Corrupt && detail != text)
            text = $"{text} {detail}";
        return text;
    }

    public static string StageText(LoadStage? stage) => stage switch
    {
        LoadStage.Analyzing => Loc.T("Analyzing archive…"),
        LoadStage.Extracting => Loc.T("Extracting archive…"),
        LoadStage.Loading => Loc.T("Loading pages…"),
        LoadStage.Building => Loc.T("Building vertical strip…"),
        _ => Loc.T("Opening…"),
    };

    /// <summary>`Vol 1 / 3 · 5-6 / 24` 형식의 오른쪽 부분(쪽 범위 문자열).</summary>
    public static string PageRangeText(PageRange spread)
        => spread.Length <= 1 ? (spread.Start + 1).ToString() : $"{spread.Start + 1}-{spread.End}";

    public static string VolumePrefix(int volumeIndex, int volumeCount)
        => volumeIndex >= 0 && volumeCount > 1 ? Loc.T("Vol %lld / %lld", volumeIndex + 1, volumeCount) + " · " : string.Empty;

    /// <summary>입력창 폭: max(32, digits(total)×9+10).</summary>
    public static double JumpInputWidth(int total) => Math.Max(32, Math.Max(1, total).ToString().Length * 9 + 10);

    /// <summary>quick jump 입력 해석: 유효 정수만, 범위 밖 clamp, 잘못되면 null(원래 쪽 유지).</summary>
    public static int? ParsePageNumber(string? text, int total)
    {
        if (total <= 0 || string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        // 전각 숫자 허용
        var normalized = new string(t.Select(c => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c).ToArray());
        if (!long.TryParse(normalized, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return null;
        return (int)Math.Clamp(n, 1, total);
    }
}
