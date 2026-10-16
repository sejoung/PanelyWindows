using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Panely.Core.Localization;

namespace Panely.App.Translation;

/// <summary>번역 UI를 코드로 구성하는 작은 도우미. 테마 리소스(D의 Theme.xaml)를 DynamicResource로 참조한다.</summary>
internal static class Ui
{
    public static TextBlock Text(string text, string style = "Text.Body", bool wrap = true)
    {
        var t = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    public static TextBlock Caption(string text) => Text(text, "Text.Caption");

    public static TextBlock Section(string text) => Text(text, "Text.Section", wrap: false);

    public static Button Button(string text, Action onClick, bool accent = false, string? tooltip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), ToolTip = tooltip };
        b.SetResourceReference(FrameworkElement.StyleProperty, accent ? "Panely.AccentButton" : "Panely.Button");
        b.Click += (_, _) => onClick();
        AutomationName(b, text);
        return b;
    }

    public static Button LinkButton(string text, Action onClick)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Panely.LinkButton");
        b.Click += (_, _) => onClick();
        AutomationName(b, text);
        return b;
    }

    public static Button IconButton(Geometry icon, string tooltip, Action onClick)
    {
        var path = new System.Windows.Shapes.Path { Data = icon, Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4 };
        path.SetResourceReference(Shape.StrokeProperty, "Brush.TextPrimary");
        var b = new Button { Content = path, ToolTip = tooltip, Width = 32, Height = 32 };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Panely.IconButton");
        b.Click += (_, _) => onClick();
        AutomationName(b, tooltip);
        return b;
    }

    public static CheckBox Toggle(string text, bool isChecked, Action<bool> changed, bool switchStyle = false)
    {
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = isChecked };
        c.SetResourceReference(FrameworkElement.StyleProperty, switchStyle ? "Panely.ToggleSwitch" : "Panely.CheckBox");
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        AutomationName(c, text);
        return c;
    }

    public static RadioButton Radio(string text, string group, bool isChecked, Action onChecked)
    {
        var r = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked };
        r.SetResourceReference(FrameworkElement.StyleProperty, "Panely.RadioButton");
        r.Checked += (_, _) => onChecked();
        AutomationName(r, text);
        return r;
    }

    public static TextBox TextBox(string text = "", bool multiline = false)
    {
        var t = new TextBox { Text = text, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Panely.TextBox");
        if (multiline) t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        return t;
    }

    public static ComboBox Combo<T>(IEnumerable<(string Label, T Value)> items, T selected, Action<T> changed)
    {
        var c = new ComboBox { MinWidth = 160 };
        c.SetResourceReference(FrameworkElement.StyleProperty, "Panely.ComboBox");
        var list = items.ToList();
        foreach (var (label, _) in list) c.Items.Add(new ComboBoxItem { Content = label });
        c.SelectedIndex = Math.Max(0, list.FindIndex(i => Equals(i.Value, selected)));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(list[c.SelectedIndex].Value); };
        return c;
    }

    public static ProgressBar Progress(double fraction, bool indeterminate = false)
    {
        var p = new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, IsIndeterminate = indeterminate, Height = 4, Margin = new Thickness(0, 4, 0, 4) };
        p.SetResourceReference(FrameworkElement.StyleProperty, "Panely.ProgressBar");
        return p;
    }

    public static Border Separator() =>
        WithBrush(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) }, Border.BackgroundProperty, "Brush.BorderSubtle");

    public static Border Badge(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var b = new Border { Child = t, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.BgTertiary");
        return b;
    }

    public static T WithBrush<T>(T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Column(double spacing = 8, params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && s.Children.Count > 0 && fe.Margin == default) fe.Margin = new Thickness(0, spacing, 0, 0);
            s.Children.Add(c);
        }
        return s;
    }

    public static void AutomationName(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);

    public static MenuItem MenuItem(string header, Action onClick, bool enabled = true, bool isChecked = false, string? gesture = null, string? tooltip = null)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked, InputGestureText = gesture ?? string.Empty, ToolTip = tooltip };
        if (!enabled && tooltip is not null) ToolTipService.SetShowOnDisabled(m, true);
        m.Click += (_, _) => onClick();
        return m;
    }

    // 작은 vector 아이콘(14 DIP 윤곽선 계열)
    public static readonly Geometry GearIcon = Geometry.Parse("M7,4.5 A2.5,2.5 0 1 1 6.99,4.5 Z M7,0.5 L8,2 L10,1.5 L10.5,3.5 L12.5,4 L12,6 L13.5,7 L12,8 L12.5,10 L10.5,10.5 L10,12.5 L8,12 L7,13.5 L6,12 L4,12.5 L3.5,10.5 L1.5,10 L2,8 L0.5,7 L2,6 L1.5,4 L3.5,3.5 L4,1.5 L6,2 Z");
    public static readonly Geometry CloseIcon = Geometry.Parse("M2,2 L12,12 M12,2 L2,12");
    public static readonly Geometry MoreIcon = Geometry.Parse("M2,7 A0.8,0.8 0 1 1 2.01,7 M7,7 A0.8,0.8 0 1 1 7.01,7 M12,7 A0.8,0.8 0 1 1 12.01,7");
    public static readonly Geometry PencilIcon = Geometry.Parse("M2,12 L3,9 L10,2 L12,4 L5,11 Z M9,3 L11,5");
    public static readonly Geometry BulbIcon = Geometry.Parse("M7,1 A4,4 0 0 1 9.5,8.2 L9.5,10 L4.5,10 L4.5,8.2 A4,4 0 0 1 7,1 Z M5,12 L9,12 M5.5,13.5 L8.5,13.5");
}

/// <summary>
/// 모달 시트 공통: owner 중앙, 초기 포커스, Escape 닫기, Tab 순환, 닫힌 뒤 원래 포커스 복귀.
/// </summary>
internal class SheetWindow : Window
{
    private readonly IInputElement? _previousFocus;

    public SheetWindow(Window? owner, string title, double width)
    {
        _previousFocus = Keyboard.FocusedElement;
        Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = owner is not null ? Math.Max(320, owner.ActualHeight - 40) : 800;
        WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "Panely.Window");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Closed += (_, _) =>
        {
            if (_previousFocus is UIElement el && el.IsVisible) el.Focus();
            owner?.Activate();
        };
    }

    protected void SetBody(UIElement body, UIElement? initialFocus = null)
    {
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) };
        scroll.SetResourceReference(StyleProperty, "Panely.ScrollViewer");
        Content = scroll;
        if (initialFocus is not null) Loaded += (_, _) => initialFocus.Focus();
    }

    public static bool Confirm(Window? owner, string title, string message, string confirm)
    {
        var w = new SheetWindow(owner, title, 420);
        var result = false;
        var buttons = Ui.Row(Ui.Button(Loc.T("Cancel"), () => w.Close()), Ui.Button(confirm, () => { result = true; w.Close(); }, accent: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        ((FrameworkElement)buttons.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        var body = Ui.Column(12, Ui.Text(title, "Text.Title"), Ui.Text(message), buttons);
        w.SetBody(body, buttons.Children[0]);
        w.ShowDialog();
        return result;
    }

    public static void Inform(Window? owner, string title, string message)
    {
        var w = new SheetWindow(owner, title, 420);
        var ok = Ui.Button(Loc.T("OK"), () => w.Close(), accent: true);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        w.SetBody(Ui.Column(12, Ui.Text(title, "Text.Title"), Ui.Text(message), ok), ok);
        w.ShowDialog();
    }
}
