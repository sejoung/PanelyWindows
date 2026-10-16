using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Panely.Core.Localization;
using Panely.Core.Reader;

namespace Panely.App.Shell;

/// <summary>다크 테마 모달의 공통 바탕: owner 중심, 초기 포커스, Escape 취소, 닫힌 뒤 원래 포커스 복귀.</summary>
public abstract class PanelyDialog : Window
{
    private IInputElement? _returnFocus;

    protected PanelyDialog(Window? owner)
    {
        Style = (Style)Application.Current.FindResource("Panely.Window");
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        _returnFocus = Keyboard.FocusedElement;
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkCaption(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        Closed += (_, _) => _returnFocus?.Focus();
    }

    protected static Button MakeButton(string text, bool accent, bool destructive = false)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource(accent ? "Panely.AccentButton" : "Panely.Button"),
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 6, 14, 6),
        };
        if (destructive && accent) b.Background = new SolidColorBrush(Color.FromRgb(0xD9, 0x3F, 0x3F));
        return b;
    }
}

/// <summary>대상과 영향을 명시하는 확인 대화상자(05: 일괄 삭제·되돌리기·로그 삭제).</summary>
public sealed class ConfirmDialog : PanelyDialog
{
    private ConfirmDialog(Window? owner, string title, string message, string? confirm, bool destructive) : base(owner)
    {
        Title = title;
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Text.Base"), FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(message))
            panel.Children.Add(new TextBlock { Text = message, Style = (Style)FindResource("Text.Body"), Foreground = (Brush)FindResource("Brush.TextSecondary"), Margin = new Thickness(0, 8, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        if (confirm is not null)
        {
            var cancel = MakeButton(Loc.T("Cancel"), false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => DialogResult = false;
            var ok = MakeButton(confirm, true, destructive);
            ok.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            // 파괴적 동작은 취소에 초기 포커스
            Loaded += (_, _) => (destructive ? cancel : ok).Focus();
        }
        else
        {
            var ok = MakeButton(Loc.T("OK"), true);
            ok.IsDefault = true;
            ok.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(ok);
            Loaded += (_, _) => ok.Focus();
        }
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static bool Show(Window? owner, string title, string message, string confirm, bool destructive = false)
        => new ConfirmDialog(owner, title, message, confirm, destructive).ShowDialog() == true;

    public static void Info(Window? owner, string title, string message)
        => new ConfirmDialog(owner, title, message, null, false).ShowDialog();
}

/// <summary>페이지로 이동(Ctrl+G): Enter 확정, Escape 취소, 범위 밖 clamp, 잘못된 값은 원래 쪽 유지.</summary>
public sealed class GoToPageDialog : PanelyDialog
{
    private readonly TextBox _input;
    private readonly int _total;
    public int? Result { get; private set; }

    private GoToPageDialog(Window owner, int total, int current) : base(owner)
    {
        _total = total;
        Title = Loc.T("Go to Page");
        var panel = new StackPanel { Margin = new Thickness(20), Width = 300 };
        panel.Children.Add(new TextBlock { Text = Loc.T("Enter a page number (1 – %lld):", (long)total), Style = (Style)FindResource("Text.Body") });
        _input = new TextBox { Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture), Margin = new Thickness(0, 10, 0, 0) };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        panel.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = MakeButton(Loc.T("Cancel"), false);
        cancel.Click += (_, _) => DialogResult = false;
        var go = MakeButton(Loc.T("Go"), true);
        go.Click += (_, _) => Commit();
        buttons.Children.Add(cancel);
        buttons.Children.Add(go);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    private void Commit()
    {
        Result = ReaderText.ParsePageNumber(_input.Text, _total);
        DialogResult = true;
    }

    public static int? Ask(Window owner, int total, int current)
    {
        var d = new GoToPageDialog(owner, total, current);
        return d.ShowDialog() == true ? d.Result : null;
    }
}

/// <summary>About: 버전·빌드·출처·라이선스.</summary>
public sealed class AboutWindow : PanelyDialog
{
    public AboutWindow() : base(null)
    {
        Title = Loc.T("About Panely");
        var panel = new StackPanel { Margin = new Thickness(24), Width = 380 };
        panel.Children.Add(new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/panely-256.png")),
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock { Text = "Panely", Style = (Style)FindResource("Text.Title"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(new TextBlock { Text = $"{Loc.T("App version")} {AppInfo.Version} · {Loc.T("Build")} {AppInfo.Build}", Style = (Style)FindResource("Text.Caption"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 12) });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("A local comic and image reader for Windows. Original design and assets: Copyright 2026 beni kim, licensed under the Apache License 2.0. Third-party components: SharpCompress (MIT), ONNX Runtime (MIT), Microsoft.ML.Tokenizers (MIT), Magick.NET (Apache-2.0)."),
            Style = (Style)FindResource("Text.Body"),
            Foreground = (Brush)FindResource("Brush.TextSecondary"),
            TextAlignment = TextAlignment.Center,
        });
        var ok = MakeButton(Loc.T("OK"), true);
        ok.HorizontalAlignment = HorizontalAlignment.Center;
        ok.Margin = new Thickness(0, 16, 0, 0);
        ok.IsDefault = true;
        ok.Click += (_, _) => DialogResult = true;
        panel.Children.Add(ok);
        Content = panel;
        Loaded += (_, _) => ok.Focus();
    }
}
