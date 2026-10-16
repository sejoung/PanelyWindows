using System.Globalization;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Domain.Export;
using Panely.Domain.Library;
using Panely.Domain.Model;

namespace Panely.Tests.Domain;

public class LibraryAndTextTests
{
    [Fact]
    public void Series_stems_match_vectors()
    {
        foreach (var c in Vectors.Root.GetProperty("seriesStems").EnumerateArray())
            Assert.Equal(c.GetProperty("stem").GetString(), SeriesStem.Compute(c.GetProperty("name").GetString()!));
    }

    [Theory]
    [InlineData("Story [Group] Vol.3.cbz", "story")]
    [InlineData("第3巻 物語.zip", "物語")]
    [InlineData("제2화 이야기.cbz", "이야기")]
    [InlineData("Story ch12.cbz", "story")]
    [InlineData("Story Episode 4.cbz", "story")]
    [InlineData("Other Story v01.cbz", "otherstory")]
    public void Series_stem_strips_markers(string name, string stem) => Assert.Equal(stem, SeriesStem.Compute(name));

    [Fact]
    public void Export_names_match_vectors()
    {
        foreach (var c in Vectors.Root.GetProperty("exportNames").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("expected").GetString(), ExportNaming.PageFileName(
                c.GetProperty("pageCount").GetInt32(), c.GetProperty("index").GetInt32(), c.GetProperty("extension").GetString()!));
        }
    }

    [Theory]
    [InlineData("a:b*c?", "a_b_c_")]
    [InlineData("CON", "_CON")]
    [InlineData("name. . ", "name")]
    [InlineData("  ", "Untitled")]
    [InlineData("com1.txt", "_com1.txt")]
    public void Sanitize_file_names(string input, string expected) => Assert.Equal(expected, ExportNaming.SanitizeFileName(input));

    [Fact]
    public void Unique_name_appends_counter()
    {
        var existing = new HashSet<string> { "Book [Korean].cbz", "Book [Korean] 2.cbz" };
        Assert.Equal("Book [Korean] 3.cbz", ExportNaming.UniqueName("Book [Korean]", ".cbz", existing.Contains));
    }

    [Fact]
    public void Natural_sort_orders_numbers_and_ties()
    {
        var names = new[] { "10.jpg", "2.jpg", "1.jpg", "01.jpg", "Ａ.jpg", "a.jpg", "B.jpg", "한글.jpg", "page１１.jpg", "page3.jpg", "Page3.jpg" };
        var sorted = names.Order(new NaturalComparer(CultureInfo.GetCultureInfo("en-US"))).ToArray();
        // 숫자 run 비교: 01 == 1 숫자값 → ordinal 동률 처리로 "01" < "1"
        Assert.Equal(["01.jpg", "1.jpg", "2.jpg", "10.jpg"], sorted[..4]);
        Assert.True(Array.IndexOf(sorted, "page3.jpg") < Array.IndexOf(sorted, "page１１.jpg"));
        // 대소문자 동률은 ordinal로 고정(결정적)
        Assert.True(Array.IndexOf(sorted, "Page3.jpg") < Array.IndexOf(sorted, "page3.jpg"));
        // 같은 입력이면 항상 같은 순서
        var again = names.Reverse().Order(new NaturalComparer(CultureInfo.GetCultureInfo("en-US"))).ToArray();
        Assert.Equal(sorted, again);
    }

    [Fact]
    public void Natural_sort_full_paths()
    {
        var paths = new[] { "ch10/001.jpg", "ch2/010.jpg", "ch2/002.jpg", "ch1/1.jpg" };
        var sorted = paths.Order(NaturalComparer.Invariant).ToArray();
        Assert.Equal(["ch1/1.jpg", "ch2/002.jpg", "ch2/010.jpg", "ch10/001.jpg"], sorted);
    }

    [Fact]
    public void Progress_fraction_and_sticky_finished()
    {
        Assert.Equal(0, ReadingProgressMath.Fraction(0, 0));
        Assert.Equal(0.5, ReadingProgressMath.Fraction(11, 24));
        Assert.Equal(1, ReadingProgressMath.Fraction(99, 24));

        var finished = ReadingProgressMath.Update(null, 4, 6, isLastSpread: true);
        Assert.True(finished.Finished);
        var browsedBack = ReadingProgressMath.Update(finished, 1, 6, isLastSpread: false);
        Assert.True(browsedBack.Finished);
        var totalChanged = ReadingProgressMath.Update(finished, 1, 8, isLastSpread: false);
        Assert.False(totalChanged.Finished);
    }

    [Fact]
    public void Bookmark_navigation_skips_visible_spread_and_does_not_wrap()
    {
        int[] marks = [3, 4, 9];
        // double 펼침 (2,3)을 보는 중: 3은 화면에 있으므로 다음은 4
        Assert.Equal(4, BookmarkNavigator.Next(marks, 2, 4));
        Assert.Equal(9, BookmarkNavigator.Next(marks, 4, 6));
        Assert.Null(BookmarkNavigator.Next(marks, 9, 10));
        Assert.Equal(4, BookmarkNavigator.Previous(marks, 9));
        Assert.Null(BookmarkNavigator.Previous(marks, 3));
    }

    [Fact]
    public void Book_keys_round_trip()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        Assert.Equal("source:11111111-1111-4111-8111-111111111111", BookKeys.Make(id, null));
        Assert.Equal("source:11111111-1111-4111-8111-111111111111#Volume 01/Sub", BookKeys.Make(id, "\\Volume 01\\./Sub/"));
        Assert.True(BookKeys.TryParse("source:11111111-1111-4111-8111-111111111111#Volume 01", out var parsed, out var inner));
        Assert.Equal(id, parsed);
        Assert.Equal("Volume 01", inner);
        Assert.False(BookKeys.TryParse("series:x", out _, out _));
    }

    [Fact]
    public void Loc_converts_positional_placeholders()
    {
        var ko = Loc.TIn("ko", "%lld of %lld pages translated", 3, 24);
        Assert.Equal("24페이지 중 3페이지 번역됨", ko);
        Assert.Equal("3 of 24 pages translated", Loc.TIn("en", "%lld of %lld pages translated", 3, 24));
        Assert.Equal("50% read", Loc.TIn("en", "%lld%% read", 50));
    }

    [Fact]
    public void Loc_does_not_interpret_arguments_as_format()
    {
        Assert.Equal("Failed to load {0} %@ 100%", Loc.TIn("en", "Failed to load %@", "{0} %@ 100%"));
    }

    [Fact]
    public void Loc_falls_back_to_source_key()
    {
        Assert.Equal("Some unknown key", Loc.TIn("ko", "Some unknown key"));
    }

    [Fact]
    public void Loc_windows_override_replaces_mac_wording()
    {
        Assert.Equal("Show in Explorer", Loc.TIn("en", "Reveal in Finder"));
        Assert.Equal("탐색기에서 보기", Loc.TIn("ko", "Show in Finder"));
    }

    [Fact]
    public void Command_gestures_are_unique_and_humanized()
    {
        var gestures = CommandRegistry.All.SelectMany(c => c.Gestures).ToList();
        Assert.Equal(gestures.Count, gestures.Distinct().Count());
        Assert.Equal("Ctrl+Shift+1", CommandRegistry.GestureText(CommandIds.LayoutSingle));
        Assert.Equal("Ctrl+,", CommandRegistry.GestureText(CommandIds.AppSettings));
        Assert.Equal("[", CommandRegistry.GestureText(CommandIds.VolumePrevious));
        Assert.Equal("Ctrl+Alt+Y", CommandRegistry.GestureText(CommandIds.TranslationRun));
        Assert.Equal("Fit to Screen (Ctrl+1)", CommandRegistry.Tooltip("Fit to Screen", CommandIds.FitScreen));
    }
}
