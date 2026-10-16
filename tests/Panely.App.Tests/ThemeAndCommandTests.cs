using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Panely.Core.Commands;

namespace Panely.App.Tests;

public class ThemeAndCommandTests
{
    private static ResourceDictionary LoadTheme()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack; // pack:// 스킴 등록
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        return new ResourceDictionary { Source = new Uri("pack://application:,,,/Panely;component/Themes/Theme.xaml", UriKind.Absolute) };
    }

    /// <summary>번역 UI 등이 DynamicResource로 참조하는 테마 계약 key(02-design.md 토큰 포함).</summary>
    public static readonly string[] RequiredKeys =
    [
        "Brush.BgPrimary", "Brush.BgSecondary", "Brush.BgTertiary", "Brush.TextPrimary", "Brush.TextSecondary",
        "Brush.BorderSubtle", "Brush.Accent", "Brush.AccentText",
        "Panely.Button", "Panely.AccentButton", "Panely.IconButton", "Panely.TextBox", "Panely.CheckBox", "Panely.RadioButton",
        "Panely.ComboBox", "Panely.ToggleSwitch", "Panely.ProgressBar", "Panely.Window", "Panely.Card", "Panely.ScrollViewer",
        "Text.Title", "Text.Body", "Text.Caption", "Text.Section",
    ];

    [Fact]
    public void Theme_defines_every_contract_key() => Sta.Run(() =>
    {
        var theme = LoadTheme();
        var missing = RequiredKeys.Where(k => !theme.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Theme.xaml에 없는 key: " + string.Join(", ", missing));
    });

    [Theory]
    [InlineData("Brush.BgPrimary", "#FF0F1115")]
    [InlineData("Brush.BgSecondary", "#FF1C1F26")]
    [InlineData("Brush.BgTertiary", "#FF2A2E36")]
    [InlineData("Brush.TextPrimary", "#FFE6EAF2")]
    [InlineData("Brush.TextSecondary", "#FFAAB2C0")]
    [InlineData("Brush.Accent", "#FF0A84FF")]
    public void Theme_colors_match_design_tokens(string key, string argb) => Sta.Run(() =>
    {
        var brush = Assert.IsType<SolidColorBrush>(LoadTheme()[key]);
        Assert.Equal(argb, brush.Color.ToString());
    });

    [Fact]
    public void Border_subtle_is_white_at_8_percent() => Sta.Run(() =>
    {
        var brush = Assert.IsType<SolidColorBrush>(LoadTheme()["Brush.BorderSubtle"]);
        var alpha = brush.Color.A / 255.0 * brush.Opacity;
        Assert.Equal((byte)255, brush.Color.R);
        Assert.InRange(alpha, 0.07, 0.09);
    });

    public static IEnumerable<object[]> AllGestures()
        => CommandRegistry.All.SelectMany(c => c.Gestures.Select(g => new object[] { c.Id, g }));

    [Theory]
    [MemberData(nameof(AllGestures))]
    public void Every_registered_gesture_parses_to_a_wpf_key(string commandId, string gesture)
    {
        Assert.True(MainWindow.TryParseGesture(gesture, out var mods, out var key), $"{commandId}: '{gesture}'을(를) 해석하지 못했습니다.");
        Assert.NotEqual(Key.None, key);
        // OS 예약 조합을 가로채지 않는다(08 문서): Alt+F4는 종료 명령으로만, Win 키 조합 없음
        if (commandId != CommandIds.AppExit) Assert.False(mods == ModifierKeys.Alt && key == Key.F4);
        Assert.False(mods.HasFlag(ModifierKeys.Windows));
    }

    [Fact]
    public void Gestures_are_unique_after_parsing()
    {
        var parsed = CommandRegistry.All
            .SelectMany(c => c.Gestures.Select(g => (c.Id, Ok: MainWindow.TryParseGesture(g, out var m, out var k), m, k)))
            .Where(x => x.Ok)
            .GroupBy(x => (x.m, x.k))
            .Where(g => g.Select(x => x.Id).Distinct().Count() > 1)
            .Select(g => $"{g.Key.m}+{g.Key.k}: {string.Join(", ", g.Select(x => x.Id))}")
            .ToList();
        Assert.True(parsed.Count == 0, "같은 키 조합을 쓰는 명령: " + string.Join("; ", parsed));
    }
}
