using Panely.Domain.Model;

namespace Panely.Core.Settings;

/// <summary>영속 전역 설정. 기본값은 design-tokens.json defaults와 같다.</summary>
public sealed class AppSettings
{
    public ReadingLayout Layout { get; set; } = ReadingLayout.Single;
    public ReadingDirection Direction { get; set; } = ReadingDirection.LeftToRight;
    public FitMode FitMode { get; set; } = FitMode.FitScreen;
    public AppLanguage AppLanguage { get; set; } = AppLanguage.System;
    public TranslationMode TranslationMode { get; set; } = TranslationMode.Fast;
    public string? TranslationTargetLanguage { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public bool SidebarPinned { get; set; } = true;
    public bool ToolbarPinned { get; set; }
    public bool ThumbnailSidebarVisible { get; set; }
    public bool AutoFitOnResize { get; set; } = true;
    public bool DoublePageCoverAlone { get; set; }
    public bool WheelPageTurn { get; set; } = true;
    public bool ReopenLastFolderOnLaunch { get; set; } = true;
    public bool TranslationEnabled { get; set; }
    public bool TranslationMatchesSpeechLevel { get; set; } = true;
    public bool TranslationOverlayVisible { get; set; } = true;
    public bool TranslationBalloonTipDismissed { get; set; }
    public bool LibraryAutoRefreshEnabled { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>시리즈별 override. null이면 전역 기본값 사용(override ?? global).</summary>
public sealed class SeriesPreferences
{
    public ReadingDirection? Direction { get; set; }
    public ReadingLayout? Layout { get; set; }
    public FitMode? FitMode { get; set; }
    public string? TranslationSource { get; set; }
    public string? TranslationTarget { get; set; }
    public bool? AutoTranslate { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SeriesPreferences Clone() => (SeriesPreferences)MemberwiseClone();
}

public sealed class ExportOptions
{
    public string Target { get; set; } = "en";
    public ExportFormat Format { get; set; } = ExportFormat.Cbz;
    public UntranslatedPolicy Untranslated { get; set; } = UntranslatedPolicy.TranslateFirst;
}
