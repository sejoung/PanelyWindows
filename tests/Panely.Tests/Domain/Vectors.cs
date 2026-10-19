using System.Text.Json;

namespace Panely.Tests.Domain;

/// <summary>Vectors/test-vectors.json(계산 기대값) 로더.</summary>
internal static class Vectors
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", "test-vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    });

    public static JsonElement Root => Doc.Value.RootElement;

    public static IEnumerable<object[]> Cases(string section)
        => Root.GetProperty(section).EnumerateArray().Select((e, i) => new object[] { i });

    public static JsonElement Case(string section, int index) => Root.GetProperty(section)[index];

    public static double[] Doubles(this JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    public static int? NullableInt(this JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();
}
