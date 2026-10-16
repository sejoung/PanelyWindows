namespace Panely.Tests.Contracts;

/// <summary>저장소 루트(Panely.sln이 있는 폴더)와 명세 폴더 위치.</summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Panely.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Panely.sln을 찾지 못했습니다.");
    });

    public static string Root => RootLazy.Value;
    public static string Spec => Path.Combine(Root, "docs", "windows");
    public static string Src => Path.Combine(Root, "src");
    public static string Strings => Path.Combine(Src, "Panely.Core", "Localization", "Strings");

    /// <summary>bin/obj를 제외한 src 아래 C# 파일.</summary>
    public static IEnumerable<string> SourceFiles(string pattern = "*.cs")
        => Directory.EnumerateFiles(Src, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
}
