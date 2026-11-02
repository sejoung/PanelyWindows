namespace Panely.App.Shell;

/// <summary>경로 포함 관계. 드라이브·공유 루트("Z:\", "\\srv\share\")처럼 구분자로 끝나는 루트도 같게 다룬다.</summary>
internal static class PathScope
{
    /// <summary>path가 root의 엄격한 하위면 true(같은 경로는 false).</summary>
    public static bool IsUnder(string path, string root)
    {
        try
        {
            var r = Normalize(root);
            if (!Path.EndsInDirectorySeparator(r)) r += Path.DirectorySeparatorChar;
            var p = Normalize(path);
            return p.Length > r.Length && p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsSame(string a, string b)
    {
        try
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 루트는 TrimEndingDirectorySeparator가 구분자를 남긴다
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
