using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Panely.App.Integration;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.App.Shell;

/// <summary>번역 기능이 없는 구성(또는 번역 모듈 미연결)에서 쓰는 빈 구현. IsEnabled=false.</summary>
public sealed class NullTranslationFeature : ITranslationFeature
{
    private static readonly IReadOnlySet<int> Empty = new HashSet<int>();

    public bool IsEnabled => false;
    public event EventHandler? StateChanged { add { } remove { } }
    public void Attach(IReaderHost host) { }
    public void RenderPageOverlay(DrawingContext dc, int pageIndex, SizeD pagePixelSize) { }
    public bool IsOverlayActive => false;
    public bool OnPointerPressed(Point viewPoint, MouseButton button, int clickCount) => false;
    public bool OnPointerMoved(Point viewPoint, bool leftButtonDown) => false;
    public bool OnPointerReleased(Point viewPoint, MouseButton button) => false;
    public bool OnKeyDown(KeyEventArgs e) => false;
    public IReadOnlySet<int> TranslatedPageIndices => Empty;
    public string VisibleLanguageLabel => Loc.T("Original");
    public bool HasResultsOrJob => false;
    public void OnToolbarButtonClicked() { }
    public void ShowPanel() { }
    public IReadOnlyList<Control> BuildLanguageMenuItems() => [];
    public IReadOnlyList<Control> BuildPageContextMenuItems(Point viewPoint) => [];
    public IReadOnlyList<Control> BuildLibraryContextMenuItems(string path, bool isFolder) => [];
    public bool CanToggleOriginal => false;
    public void ToggleOriginal() { }
    public bool CanRunOrPause => false;
    public bool IsRunningCurrentBook => false;
    public void RunOrPause() { }
    public bool CanEdit => false;
    public bool IsEditing => false;
    public void ToggleEditMode() { }
    public bool HasHiddenBalloons => false;
    public void ShowHiddenBalloons() { }
    public bool CanExport => false;
    public void ShowExport() { }
    public void ShowJobs() { }

    public FrameworkElement CreateExperimentalSettingsView() => new TextBlock
    {
        Text = Loc.T("Translation isn't available in this build."),
        Margin = new Thickness(16),
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.FindResource("Brush.TextSecondary"),
    };

    public string? ExitWarning => null;
    public Task ShutdownAsync() => Task.CompletedTask;
}
