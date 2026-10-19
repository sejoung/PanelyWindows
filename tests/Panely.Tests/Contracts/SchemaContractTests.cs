using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Panely.Core;
using Panely.Core.Diagnostics;
using Panely.Core.State;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Tests.Contracts;

/// <summary>
/// 저장 형식 계약(Vectors/domain.schema.json). 앱이 실제로 쓰는 state.json·번역 캐시·수정 파일이 스키마를 통과해야 한다.
/// 형식을 바꾸면 기존 사용자 데이터를 읽을 수 있는지 함께 확인하고 스키마를 갱신한다.
/// </summary>
public class SchemaContractTests
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

    /// <summary>domain.schema.json의 루트($ref State) 또는 지정한 $defs 항목으로 검증하는 스키마.</summary>
    private static JsonSchema SchemaFor(string? definition)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Vectors, "domain.schema.json")))!.AsObject();
        var id = $"urn:panely:test:{definition ?? "State"}:{Guid.NewGuid():N}";
        root["$id"] = id;
        if (definition is not null) root["$ref"] = $"#/$defs/{definition}";
        return JsonSchema.FromText(root.ToJsonString());
    }

    private static void AssertValid(JsonSchema schema, string json, string what)
    {
        using var doc = JsonDocument.Parse(json);
        var result = schema.Evaluate(doc.RootElement, Options);
        if (result.IsValid) return;
        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .Distinct()
            .Take(20);
        Assert.Fail($"{what}이(가) domain.schema.json을 통과하지 못했습니다:\n" + string.Join('\n', errors));
    }

    [Fact]
    public void Examples_validate()
    {
        var examples = Path.Combine(RepoPaths.Vectors, "examples");
        AssertValid(SchemaFor(null), File.ReadAllText(Path.Combine(examples, "state.json")), "examples/state.json");
        AssertValid(SchemaFor("TranslatedPage"), File.ReadAllText(Path.Combine(examples, "translation-page.json")), "examples/translation-page.json");
        AssertValid(SchemaFor("PageEdits"), File.ReadAllText(Path.Combine(examples, "page-edits.json")), "examples/page-edits.json");
    }

    [Fact]
    public void State_written_by_the_app_validates()
    {
        var root = Path.Combine(Path.GetTempPath(), "panely-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            using (var diagnostics = new DiagnosticsService(paths))
            using (var state = new StateStore(paths, diagnostics))
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, "Comics", "Series A")).FullName;
                var source = state.GetOrCreateSource(folder, SourceKind.Folder, "Series A");
                var book = new BookRef(source.Id, "Volume 01");
                state.UpdateSettings(s => { s.Layout = ReadingLayout.Double; s.Direction = ReadingDirection.RightToLeft; s.TranslationTargetLanguage = "ko"; s.TranslationEnabled = true; });
                state.SetPosition(book.Key, 3);
                state.SetProgress(book.Key, 3, 24, false);
                state.TouchRecent(source);
                state.ToggleFavorite(book, "Volume 01");
                state.ToggleBookmark(book, "Volume 01", 4);
                state.UpdateSeries(BookKeys.SeriesKey(source.Id.ToString("D")), p => { p.Direction = ReadingDirection.RightToLeft; p.TranslationSource = "ja"; p.TranslationTarget = "ko"; p.AutoTranslate = true; });
                state.AddExport(new Panely.Core.Abstractions.ExportRecord(Guid.NewGuid(), "Volume 01", "ko", ExportFormat.Cbz, Path.Combine(root, "out.cbz"), 24, 1234, DateTimeOffset.UtcNow));
                state.ExportOptions = new Panely.Core.Settings.ExportOptions { Target = "ko", Format = ExportFormat.Zip, Untranslated = UntranslatedPolicy.ExportAsIs };
                state.ExportDestination = root;
                state.SetLastLibraryRoot(source.Id);
                state.Flush();
            }
            AssertValid(SchemaFor(null), File.ReadAllText(paths.StateFile), "StateStore가 쓴 state.json");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Default_state_validates()
    {
        var root = Path.Combine(Path.GetTempPath(), "panely-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            using (var diagnostics = new DiagnosticsService(paths))
            using (var state = new StateStore(paths, diagnostics))
            {
                state.UpdateSettings(_ => { });
                state.Flush();
            }
            AssertValid(SchemaFor(null), File.ReadAllText(paths.StateFile), "기본 state.json");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Translation_cache_and_edits_written_by_the_app_validate()
    {
        var page = new TranslatedPage
        {
            PageId = "source:11111111-1111-4111-8111-111111111111#Volume 01|001.png|sig",
            SourceLanguage = "ja",
            TargetLanguage = "ko",
            Scope = TranslationScope.Strip,
            Mode = TranslationMode.Accurate,
            ProviderRevision = "rev",
            AnalysisVersion = PageAnalyzer.AnalysisVersion,
            ImageWidth = 720,
            ImageHeight = 1200,
            IsComplete = false,
            SpeechLevelChecked = null,
            Bubbles =
            [
                new TranslatedBubble { Id = 0, Rect = new RectD(100, 120, 140, 260), GlyphSize = 24, IsVertical = true, Source = "こんにちは", Translation = "안녕하세요", Background = BubbleBackground.White, BalloonRect = new RectD(70, 90, 220, 320) },
                new TranslatedBubble { Id = 1, Rect = new RectD(10, 10, 50, 20), GlyphSize = 12, IsVertical = false, Source = "…", Translation = null, Background = new BubbleBackground(0, 0, 0, false) },
            ],
        };
        AssertValid(SchemaFor("TranslatedPage"), TranslationJson.Serialize(page), "번역 캐시 JSON");

        var edits = new PageEdits
        {
            PageId = page.PageId,
            Scope = TranslationScope.Page,
            Bubbles =
            [
                new BubbleEdit { RecognizedSource = "こんにちは", Rect = new RectD(100, 120, 140, 260), Translations = new Dictionary<string, string> { ["ko"] = "안녕" } },
                new BubbleEdit { RecognizedSource = "", Rect = new RectD(300, 300, 80, 40), IsHidden = false, CorrectedSource = "추가", Retranslations = new Dictionary<string, string> { ["en"] = "Added" }, Added = new AddedBalloon(18, false, BubbleBackground.White) },
            ],
        };
        AssertValid(SchemaFor("PageEdits"), TranslationJson.Serialize(edits), "말풍선 수정 JSON");
    }
}
