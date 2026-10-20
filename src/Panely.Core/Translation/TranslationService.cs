using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>
/// 번역 계층 composition: 공급자·캐시·수정 저장·스케줄러·내보내기. 앱은 IPagePixelSource와 렌더러만 제공한다.
/// </summary>
public sealed class TranslationService : IAsyncDisposable
{
    public TranslationService(AppPaths paths, IBookOpener opener, IStateStore state, IDiagnostics? diagnostics,
        IPagePixelSource pixels, ITranslatedPageRenderer renderer, TranslationProviders? providers = null)
    {
        Paths = paths;
        State = state;
        Models = new ModelStore(paths.Models);
        Models.CleanupStaging();
        // 이전 버전의 M2M100 팩(최대 약 2.9GB)은 쓰지 않는다. 지우는 데 시간이 걸릴 수 있어 백그라운드에서 지운다.
        _ = Task.Run(() => Models.RemoveObsolete(ModelCatalog.ObsoleteIds));
        Providers = providers ?? CreateDefaultProviders(Models);
        Cache = new TranslationCache(paths.TranslationCache);
        Edits = new PageEditsStore(paths.Edits);
        var books = new OpenerBookAccess(opener);
        Scheduler = new TranslationScheduler(Providers, Cache, pixels, books, new TranslationOptions(), diagnostics);
        Exports = new ExportScheduler(Scheduler, Edits, books, renderer, state);
    }

    public AppPaths Paths { get; }
    public IStateStore State { get; }
    public ModelStore Models { get; }
    public TranslationProviders Providers { get; }
    public TranslationCache Cache { get; }
    public PageEditsStore Edits { get; }
    public TranslationScheduler Scheduler { get; }
    public ExportScheduler Exports { get; }

    public static TranslationProviders CreateDefaultProviders(ModelStore models)
    {
        var windowsOcr = new WindowsOcrRecognizer();
        var dev = DeveloperProviders.Enabled;
        ITranslator translator = dev ? new PseudoTranslator() : new GemmaTranslator(models);
        // 만화 OCR 모델(manga-ocr/PP-OCRv5)이 기본, Windows OCR은 모델 manifest가 없을 때의 대체
        ITextRecognizer recognizer = dev ? new PseudoRecognizer(windowsOcr) : new ModelTextRecognizer(models, windowsOcr);
        return new TranslationProviders
        {
            Recognizer = recognizer,
            Detector = new OnnxBalloonDetector(models),
            Translator = translator,
            Packs = new LanguagePackManager(models, recognizer, translator),
            IsDeveloperTestProvider = dev,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Exports.CancelAllAsync().ConfigureAwait(false);
        await Scheduler.DisposeAsync().ConfigureAwait(false);
        (Providers.Translator as IDisposable)?.Dispose();
        (Providers.Detector as IDisposable)?.Dispose();
        (Providers.Recognizer as IDisposable)?.Dispose();
    }
}

/// <summary>AI 채팅 프롬프트(원본 catalog 문구를 그대로 사용).</summary>
public static class AiChatPrompt
{
    public const string PromptKey =
        "The attached file “%@” has %lld speech balloons from a comic, in reading order. Each line is: [id] original (%@) → current machine translation (%@). The machine translated a few balloons at a time, seeing only the lines just before them.\n\nImprove the translations using the whole text:\n- Keep each balloon's meaning. Don't add, drop or soften anything.\n- Work out who is speaking, and keep each speaker's tone and politeness consistent.\n- Keep names and terms the same throughout; fix mistranslated slang and wordplay.\n- Keep each translation about as long as it is now, so it fits its balloon.\n\nReply with a plain-text file listing every balloon — also the ones that are already right — one per line, in exactly this form, keeping each id:\n[id] translation\nIf you can't make a file, put the same lines in a single code block, and nothing else.";

    public static string Build(AiChatSnapshot snapshot, string fileName) =>
        Loc.T(PromptKey, fileName, snapshot.Entries.Count, Loc.LanguageName(snapshot.SourceLanguage), Loc.LanguageName(snapshot.TargetLanguage));

    /// <summary>파일 이름: "Title - Japanese to Korean.txt"(Windows 금지 문자 정리).</summary>
    public static string FileName(AiChatSnapshot snapshot) =>
        Domain.Export.ExportNaming.SanitizeFileName($"{snapshot.Title} - {Loc.LanguageNameIn("en", snapshot.SourceLanguage)} to {Loc.LanguageNameIn("en", snapshot.TargetLanguage)}") + ".txt";
}
