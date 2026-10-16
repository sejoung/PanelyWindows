using System.Text;

namespace Panely.Domain.Export;

public static class ExportNaming
{
    /// <summary>번호 폭 = max(3, digits(pageCount)), 1부터.</summary>
    public static string PageFileName(int pageCount, int index, string extension)
    {
        var width = Math.Max(3, Math.Max(1, pageCount).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0') + "." + ext;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Windows 금지 문자·reserved name·끝 공백/마침표 정리.</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = "Untitled";
        var stem = s.Split('.')[0];
        if (Reserved.Contains(stem)) s = "_" + s;
        if (s.Length > 180) s = s[..180].TrimEnd('.', ' ');
        return s;
    }

    /// <summary>`Title [언어명]`.</summary>
    public static string OutputBaseName(string title, string languageDisplayName)
        => SanitizeFileName($"{title} [{languageDisplayName}]");

    /// <summary>충돌하면 " 2", " 3"…을 붙인다. 덮어쓰지 않는다.</summary>
    public static string UniqueName(string baseName, string extensionWithDotOrEmpty, Func<string, bool> exists)
    {
        var candidate = baseName + extensionWithDotOrEmpty;
        for (var n = 2; exists(candidate); n++) candidate = $"{baseName} {n}{extensionWithDotOrEmpty}";
        return candidate;
    }
}
