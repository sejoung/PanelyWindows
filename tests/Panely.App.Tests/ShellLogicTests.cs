using Panely.App.Shell;
using Panely.Core.Abstractions;

namespace Panely.App.Tests;

public class ShellLogicTests
{
    [Theory]
    [InlineData(@"Z:\Comics\a.cbz", @"Z:\", true)]
    [InlineData(@"Z:\a.cbz", @"Z:\", true)]
    [InlineData(@"\\srv\share\Comics\a.cbz", @"\\srv\share\", true)]
    [InlineData(@"\\srv\share\a.cbz", @"\\srv\share", true)]
    [InlineData(@"D:\Comics\a.cbz", @"D:\Comics", true)]
    [InlineData(@"D:\Comics\a.cbz", @"D:\Comics\", true)]
    [InlineData(@"d:\comics\A.cbz", @"D:\Comics", true)]
    [InlineData(@"D:\Comics", @"D:\Comics", false)]
    [InlineData(@"D:\Comics\", @"D:\Comics", false)]
    [InlineData(@"Z:\", @"Z:\", false)]
    [InlineData(@"D:\Comics2\a.cbz", @"D:\Comics", false)]
    [InlineData(@"E:\a.cbz", @"Z:\", false)]
    [InlineData(@"\\srv\share2\a.cbz", @"\\srv\share", false)]
    public void IsUnder_handles_drive_and_share_roots(string path, string root, bool expected)
        => Assert.Equal(expected, PathScope.IsUnder(path, root));

    private static readonly PageEntry[] Pages =
    [
        new() { Index = 0, RelativePath = "001.png", FileName = "001.png", Extension = "png", PageId = "a" },
        new() { Index = 1, RelativePath = "002.png", FileName = "002.png", Extension = "png", PageId = "b" },
    ];

    [Fact]
    public void Image_open_jumps_only_when_this_request_opened_that_folder()
    {
        Assert.Equal(1, MainWindow.ImagePageToJump(true, @"D:\Book", Pages, @"D:\book\", "002.PNG"));
        // 취소·실패·대체: 이전 책의 같은 이름 쪽으로 가면 안 된다
        Assert.Null(MainWindow.ImagePageToJump(false, @"D:\Book", Pages, @"D:\Book", "002.png"));
        // 열렸지만 지금 책이 그 폴더가 아니다
        Assert.Null(MainWindow.ImagePageToJump(true, @"D:\Other", Pages, @"D:\Book", "002.png"));
        Assert.Null(MainWindow.ImagePageToJump(true, null, null, @"D:\Book", "002.png"));
        Assert.Null(MainWindow.ImagePageToJump(true, @"D:\Book", Pages, @"D:\Book", "missing.png"));
    }

    [Theory]
    [InlineData(".cbz", null, null, true)]
    [InlineData(".cbr", "", "", true)]
    [InlineData(".cbz", "Other.Reader", null, false)]
    [InlineData(".cbz", null, "MachineOnly.Reader", false)] // HKLM에만 있어도 HKCU 기본값으로 가리지 않는다
    [InlineData(".zip", null, null, false)]
    [InlineData(".rar", null, null, false)]
    public void Default_association_is_claimed_only_when_nobody_owns_it(string extension, string? user, string? machine, bool expected)
        => Assert.Equal(expected, FileAssociation.ShouldClaimDefault(extension, user, machine));
}
