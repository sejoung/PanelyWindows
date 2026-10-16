namespace Panely.Core.Commands;

/// <summary>
/// 명령 정의(08-commands-and-copy.md). 메뉴·tooltip·번역 패널 힌트가 같은 registry에서 단축키 문구를 만든다.
/// Gestures는 "Ctrl+Shift+1" 형식(WPF KeyGestureConverter 호환 키 이름). 첫 항목이 표시용.
/// ReaderOnly=true면 텍스트 입력/모달 포커스에서 처리하지 않는다(bare key).
/// </summary>
public sealed record CommandSpec(string Id, string LabelKey, IReadOnlyList<string> Gestures, bool ReaderOnly = false)
{
    public string? DisplayGesture => Gestures.Count > 0 ? Gestures[0] : null;
}

public static class CommandIds
{
    public const string FileOpen = "file.open";
    public const string FileReload = "file.reload";
    public const string FileRecent = "file.recent";
    public const string AppSettings = "app.settings";
    public const string FileExport = "file.export";
    public const string DiagnosticsExport = "diagnostics.export";
    public const string DiagnosticsClear = "diagnostics.clear";
    public const string DiagnosticsFolder = "diagnostics.folder";
    public const string CacheClear = "cache.clear";
    public const string LibraryPin = "library.pin";
    public const string ToolbarPin = "toolbar.pin";
    public const string ThumbnailsToggle = "thumbnails.toggle";
    public const string LayoutSingle = "layout.single";
    public const string LayoutDouble = "layout.double";
    public const string LayoutVertical = "layout.vertical";
    public const string ReadingDirection = "reading.direction";
    public const string SpreadCover = "spread.cover";
    public const string FitScreen = "fit.screen";
    public const string FitWidth = "fit.width";
    public const string FitHeight = "fit.height";
    public const string ZoomIn = "zoom.in";
    public const string ZoomOut = "zoom.out";
    public const string ZoomReset = "zoom.reset";
    public const string ViewLock = "view.lock";
    public const string WheelToggle = "wheel.toggle";
    public const string PageForward = "page.forward";
    public const string PageBackward = "page.backward";
    public const string PageJump = "page.jump";
    public const string BookmarkToggle = "bookmark.toggle";
    public const string BookmarkPrevious = "bookmark.previous";
    public const string BookmarkNext = "bookmark.next";
    public const string FavoriteToggle = "favorite.toggle";
    public const string VolumePrevious = "volume.previous";
    public const string VolumeNext = "volume.next";
    public const string TranslationToggle = "translation.toggle";
    public const string TranslationRun = "translation.run";
    public const string TranslationEdit = "translation.edit";
    public const string JobsShow = "jobs.show";
    public const string WindowFullscreen = "window.fullscreen";
    public const string AppExit = "app.exit";
}

public static class CommandRegistry
{
    public static readonly IReadOnlyList<CommandSpec> All =
    [
        new(CommandIds.FileOpen, "Open…", ["Ctrl+O"]),
        new(CommandIds.FileReload, "Reload Book", ["Ctrl+R"]),
        new(CommandIds.FileRecent, "Open Recent", []),
        new(CommandIds.AppSettings, "Settings…", ["Ctrl+OemComma"]),
        new(CommandIds.FileExport, "Export Translated Book…", []),
        new(CommandIds.DiagnosticsExport, "Export Diagnostic Report…", []),
        new(CommandIds.DiagnosticsClear, "Clear Diagnostic Logs", []),
        new(CommandIds.DiagnosticsFolder, "Open Diagnostic Logs Folder", []),
        new(CommandIds.CacheClear, "Clear Extraction Cache", []),
        new(CommandIds.LibraryPin, "Pin Library", ["Ctrl+Alt+S"]),
        new(CommandIds.ToolbarPin, "Pin Toolbar", ["Ctrl+Alt+T"]),
        new(CommandIds.ThumbnailsToggle, "Show Thumbnails", ["Ctrl+Alt+P"]),
        new(CommandIds.LayoutSingle, "Single Page", ["Ctrl+Shift+D1"]),
        new(CommandIds.LayoutDouble, "Double Page", ["Ctrl+Shift+D2"]),
        new(CommandIds.LayoutVertical, "Vertical Scroll", ["Ctrl+Shift+D3"]),
        new(CommandIds.ReadingDirection, "Reading Direction", []),
        new(CommandIds.SpreadCover, "Standalone Cover", []),
        new(CommandIds.FitScreen, "Fit to Screen", ["Ctrl+D1", "Ctrl+NumPad1"]),
        new(CommandIds.FitWidth, "Fit to Width", ["Ctrl+D2", "Ctrl+NumPad2"]),
        new(CommandIds.FitHeight, "Fit to Height", ["Ctrl+D3", "Ctrl+NumPad3"]),
        new(CommandIds.ZoomIn, "Zoom In", ["Ctrl+OemPlus", "Ctrl+Add", "Ctrl+Shift+OemPlus"]),
        new(CommandIds.ZoomOut, "Zoom Out", ["Ctrl+OemMinus", "Ctrl+Subtract"]),
        new(CommandIds.ZoomReset, "Reset Zoom", ["Ctrl+D0", "Ctrl+NumPad0"]),
        new(CommandIds.ViewLock, "Lock View Size", ["Ctrl+L"]),
        new(CommandIds.WheelToggle, "Scroll Page Turning", []),
        new(CommandIds.PageForward, "Forward", ["Space"], ReaderOnly: true),
        new(CommandIds.PageBackward, "Backward", [], ReaderOnly: true),
        new(CommandIds.PageJump, "Go to Page…", ["Ctrl+G"]),
        new(CommandIds.BookmarkToggle, "Add Page Bookmark", ["Ctrl+D"]),
        new(CommandIds.BookmarkPrevious, "Previous Bookmark", ["Ctrl+Shift+OemOpenBrackets"]),
        new(CommandIds.BookmarkNext, "Next Bookmark", ["Ctrl+Shift+OemCloseBrackets"]),
        new(CommandIds.FavoriteToggle, "Add to Favorites", ["Ctrl+Shift+D"]),
        new(CommandIds.VolumePrevious, "Previous Volume", ["OemOpenBrackets", "Ctrl+OemOpenBrackets"], ReaderOnly: true),
        new(CommandIds.VolumeNext, "Next Volume", ["OemCloseBrackets", "Ctrl+OemCloseBrackets"], ReaderOnly: true),
        new(CommandIds.TranslationToggle, "Show Translation", ["Ctrl+Shift+T"]),
        new(CommandIds.TranslationRun, "Translate This Book", ["Ctrl+Alt+Y"]),
        new(CommandIds.TranslationEdit, "Edit Balloons", ["Ctrl+Shift+E"]),
        new(CommandIds.JobsShow, "Jobs", ["Ctrl+Alt+J"]),
        new(CommandIds.WindowFullscreen, "Full Screen", ["F11"]),
        new(CommandIds.AppExit, "Exit", ["Alt+F4"]),
    ];

    private static readonly Dictionary<string, CommandSpec> ById = All.ToDictionary(c => c.Id);

    public static CommandSpec Get(string id) => ById[id];

    /// <summary>사용자에게 보이는 단축키 문구(예: "Ctrl+Shift+1", "Ctrl+,", "[").</summary>
    public static string? GestureText(string id)
    {
        var g = Get(id).DisplayGesture;
        return g is null ? null : Humanize(g);
    }

    /// <summary>"라벨 (단축키)" 형식의 tooltip.</summary>
    public static string Tooltip(string label, string id)
    {
        var g = GestureText(id);
        return g is null ? label : $"{label} ({g})";
    }

    public static string Humanize(string gesture)
    {
        var parts = gesture.Split('+');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i] switch
            {
                "OemComma" => ",",
                "OemPlus" => "+",
                "OemMinus" => "-",
                "Add" => "Num +",
                "Subtract" => "Num -",
                "OemOpenBrackets" => "[",
                "OemCloseBrackets" => "]",
                var p when p.Length == 2 && p[0] == 'D' && char.IsAsciiDigit(p[1]) => p[1..],
                var p when p.StartsWith("NumPad", StringComparison.Ordinal) => "Num " + p[6..],
                var p => p,
            };
        }
        return string.Join('+', parts);
    }
}
