using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Panely.Core.Settings;
using Panely.Domain.Model;

namespace Panely.Core.State;

/// <summary>state.json 직렬화 형식(domain.schema.json #/$defs/State).</summary>
internal sealed class StateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<SourceDto> Sources { get; set; } = [];
    public Dictionary<string, PositionDto> Positions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProgressDto> Progress { get; set; } = new(StringComparer.Ordinal);
    public List<RecentDto> Recents { get; set; } = [];
    public List<FavoriteDto> Favorites { get; set; } = [];
    public Dictionary<string, BookBookmarksDto> Bookmarks { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SeriesPreferences> SeriesPreferences { get; set; } = new(StringComparer.Ordinal);
    public List<ExportRecordDto> ExportHistory { get; set; } = [];
    public ExportOptions? ExportOptions { get; set; }
    public string? ExportDestination { get; set; }
    public Guid? LastLibraryRoot { get; set; }
}

internal sealed class SourceDto
{
    public Guid Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public SourceKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? VolumeId { get; set; }
    public string? FileId { get; set; }

    public SourceRef ToRef() => new() { Id = Id, Path = Path, Kind = Kind, Title = Title, VolumeId = VolumeId, FileId = FileId };
}

internal sealed class PositionDto
{
    public int PageIndex { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ProgressDto
{
    public int Page { get; set; }
    public int Total { get; set; }
    public bool Finished { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RecentDto
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset OpenedAt { get; set; }
}

internal sealed class BookRefDto
{
    public Guid SourceId { get; set; }
    public string? InnerPath { get; set; }

    public BookRef ToRef() => new(SourceId, InnerPath);
    public static BookRefDto From(BookRef r) => new() { SourceId = r.SourceId, InnerPath = BookKeys.NormalizeInnerPath(r.InnerPath) };
}

internal sealed class FavoriteDto
{
    public Guid Id { get; set; }
    public BookRefDto BookRef { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; }
}

internal sealed class BookmarkDto
{
    public Guid Id { get; set; }
    public int PageIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class BookBookmarksDto
{
    public BookRefDto BookRef { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public List<BookmarkDto> Bookmarks { get; set; } = [];
}

internal sealed class ExportRecordDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Target { get; set; } = "en";
    public ExportFormat Format { get; set; }
    public string Path { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset Date { get; set; }
}

internal static class StateJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.Strict,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        o.Converters.Add(new UtcDateTimeOffsetConverter());
        return o;
    }
}

/// <summary>UTC ISO8601("2026-10-16T00:00:00Z") 형식으로 쓴다.</summary>
internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return DateTimeOffset.Parse(s!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}
