using System.Reflection;
using System.Runtime.InteropServices;

namespace Panely.App.Shell;

public static class AppInfo
{
    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (info is not null)
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static string Build
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var plus = info?.IndexOf('+') ?? -1;
            var commit = plus > 0 ? info![(plus + 1)..] : null;
            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
#if DEBUG
            const string config = "debug";
#else
            const string config = "release";
#endif
            return commit is null ? $"{config}-{arch}" : $"{config}-{arch}-{commit[..Math.Min(8, commit.Length)]}";
        }
    }

    public static string OsVersion => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
}
