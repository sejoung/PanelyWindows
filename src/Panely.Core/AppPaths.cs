namespace Panely.Core;

/// <summary>
/// 사용자별 저장 위치(05-data.md, Windows 결정): %LOCALAPPDATA%/Panely/
/// Data/state.json, Data/edits/, Cache/extraction/, Cache/translation/, Cache/thumbnails/, Diagnostics/, Models/.
/// cache Clear는 Data/Models를 건드리지 않는다.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Data = Path.Combine(root, "Data");
        StateFile = Path.Combine(Data, "state.json");
        Edits = Path.Combine(Data, "edits");
        Cache = Path.Combine(root, "Cache");
        ExtractionCache = Path.Combine(Cache, "extraction");
        TranslationCache = Path.Combine(Cache, "translation");
        ThumbnailCache = Path.Combine(Cache, "thumbnails");
        Diagnostics = Path.Combine(root, "Diagnostics");
        Models = Path.Combine(root, "Models");
    }

    /// <summary>PANELY_DATA_ROOT 환경 변수로 테스트/포터블 위치를 지정할 수 있다.</summary>
    public static AppPaths Default { get; } = new(
        Environment.GetEnvironmentVariable("PANELY_DATA_ROOT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Panely"));

    public string Root { get; }
    public string Data { get; }
    public string StateFile { get; }
    public string Edits { get; }
    public string Cache { get; }
    public string ExtractionCache { get; }
    public string TranslationCache { get; }
    public string ThumbnailCache { get; }
    public string Diagnostics { get; }
    public string Models { get; }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Data, Edits, ExtractionCache, TranslationCache, ThumbnailCache, Diagnostics, Models })
            Directory.CreateDirectory(dir);
    }
}
