namespace Panely.Domain.Model;

public enum ReadingLayout { Single, Double, Vertical }

public enum ReadingDirection { LeftToRight, RightToLeft }

public enum FitMode { FitScreen, FitWidth, FitHeight }

public enum AppLanguage { System, En, Ko }

public enum ExportFormat { Cbz, Zip, Folder }

public enum UntranslatedPolicy { TranslateFirst, ExportAsIs }

public enum LogLevel { Trace, Debug, Info, Notice, Warning, Error, Critical }

public enum SourceKind { Folder, Archive }

public enum TranslationScope { Page, Strip }

public static class LayoutExtensions
{
    public static int Step(this ReadingLayout layout) => layout == ReadingLayout.Double ? 2 : 1;

    /// <summary>vertical에서는 방향을 LTR로 고정한다. 저장된 선택은 지우지 않는다.</summary>
    public static ReadingDirection EffectiveDirection(this ReadingLayout layout, ReadingDirection stored)
        => layout == ReadingLayout.Vertical ? ReadingDirection.LeftToRight : stored;
}
