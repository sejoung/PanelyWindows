using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Model;

namespace Panely.App.Translation;

/// <summary>
/// 설정 창 "실험" 탭: 번역 보조, 기본 언어, 번역 모델(설치·실행 장치), 언어팩·OCR 상태, GPU 가속,
/// 수정 쪽 수·전체 수정 삭제, 번역 캐시 용량·삭제.
/// </summary>
internal sealed class ExperimentalSettingsView : UserControl
{
    private readonly TranslationFeature _f;
    private readonly StackPanel _root = new() { MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _queued;

    public ExperimentalSettingsView(TranslationFeature feature)
    {
        _f = feature;
        Content = _root;
        Padding = new Thickness(4);
        EventHandler<StateChange> onState = (_, c) => { if (c.HasFlag(StateChange.Settings)) Queue(); };
        EventHandler onModels = (_, _) => Queue();
        EventHandler onLang = (_, _) => Queue();
        EventHandler<string> onEdits = (_, _) => Queue();
        Loaded += (_, _) =>
        {
            feature.StateStore.Changed += onState;
            feature.Service.Models.Changed += onModels;
            feature.Service.Edits.Changed += onEdits;
            Loc.LanguageChanged += onLang;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            feature.StateStore.Changed -= onState;
            feature.Service.Models.Changed -= onModels;
            feature.Service.Edits.Changed -= onEdits;
            Loc.LanguageChanged -= onLang;
        };
    }

    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _queued = false; Rebuild(); });
    }

    private static FrameworkElement Gap(FrameworkElement e, double top = 8) { e.Margin = new Thickness(0, top, 0, 0); return e; }

    private void Rebuild()
    {
        var state = _f.StateStore;
        var settings = state.Settings;
        var service = _f.Service;
        _root.Children.Clear();

        _root.Children.Add(Ui.Row(Ui.Text(Loc.T("Translation Assist"), "Text.Title", wrap: false), Ui.Badge(Loc.T("Experimental"))));
        _root.Children.Add(Gap(Ui.Caption(Loc.T("Reads the text in speech balloons and shows an on-device machine translation over the page. It's meant to help you follow the story, not to replace a real translation."))));
        _root.Children.Add(Gap(Ui.Toggle(Loc.T("Translation Assist"), settings.TranslationEnabled, on => state.UpdateSettings(s => s.TranslationEnabled = on), switchStyle: true), 10));
        if (service.Providers.IsDeveloperTestProvider)
            _root.Children.Add(Gap(Ui.Caption(DeveloperProviders.Label + " — PANELY_DEV_PSEUDO_TRANSLATION=1"), 6));

        // 기본 언어
        _root.Children.Add(Gap(Ui.Section(Loc.T("Default Language")), 18));
        var system = TranslationLanguages.DefaultTarget(Loc.Language);
        var options = new List<(string, string?)> { (Loc.T("System Default (%@)", Loc.LanguageName(system)), null) };
        options.AddRange(TranslationLanguages.All.Select(l => (Loc.LanguageName(l), (string?)l)));
        _root.Children.Add(Gap(Ui.Combo(options, settings.TranslationTargetLanguage, v => state.UpdateSettings(s => s.TranslationTargetLanguage = v)), 4));
        _root.Children.Add(Gap(Ui.Caption(Loc.T("The default language is what new series are shown in. Each series can be shown in another language, and each book's own language is detected the first time it's translated — both from the translation button in the toolbar, remembered per series.")), 4));

        // 번역 모델(Gemma 4 E2B 하나, ADR 0008)
        _root.Children.Add(Gap(Ui.Section(Loc.T("Translation Model")), 18));
        _root.Children.Add(Gap(Ui.Text(TranslatorStatus(), "Text.Body"), 4));
        _root.Children.Add(Gap(Ui.Caption(Loc.T("Gemma 4 E2B translates a few balloons at a time together with the lines before them, so it keeps each speaker's speech level and names. It uses the GPU when one is available; on a PC without a usable GPU it runs on the CPU and is several times slower.")), 4));

        // 언어팩
        _root.Children.Add(Gap(Ui.Section(Loc.T("Language Packs")), 18));
        foreach (var m in service.Models.Manifests)
            _root.Children.Add(PackRow(m));
        _root.Children.Add(Gap(Ui.Section(Loc.T("Text recognition")), 14));
        var models = service.Providers.Recognizer as ModelTextRecognizer;
        foreach (var lang in TranslationLanguages.All)
        {
            var cap = service.Providers.Recognizer.GetCapability(lang);
            var engine = lang == TranslationLanguages.Japanese ? "manga-ocr" : "PP-OCRv5";
            var engineState = models?.IsModelReady(lang) == true ? $"{engine} · {Loc.T("Installed")}"
                : cap.IsReady ? Loc.T("Windows OCR (fallback)")
                : cap.Reason ?? Loc.T("Language pack not installed");
            _root.Children.Add(Gap(Ui.Caption($"{Loc.LanguageName(lang)} — {engineState}"), 4));
        }
        // 가속: 말풍선 탐지는 DirectML, 번역은 Vulkan(없으면 CPU). int8 글자 인식은 CPU가 더 빨랐다(ADR 0003)
        if (OnnxSessions.GpuDisabledByUser)
            _root.Children.Add(Gap(Ui.Caption(Loc.T("GPU acceleration is off (PANELY_ORT_DEVICE=cpu): balloon detection, text recognition and translation run on the CPU.")), 8));
        else
        {
            var gpu = OnnxSessions.GpuFailure is { } failure ? Loc.T("unavailable: %@", failure) : "DirectML";
            _root.Children.Add(Gap(Ui.Caption(Loc.T("GPU acceleration: balloon detection uses the GPU (%@) and translation uses it through Vulkan when available. Text recognition runs on the CPU, which measured faster for these quantized models.", gpu)), 8));
        }

        // 수정
        _root.Children.Add(Gap(Ui.Section(Loc.T("Your Edits")), 18));
        var editedPages = service.Edits.EditedPageCount;
        _root.Children.Add(Gap(Ui.Caption(Loc.T("Translations you rewrote, originals you corrected and balloons you hid. They're kept apart from the cache, so clearing it or translating again keeps them.")), 4));
        var editsRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var deleteEdits = Ui.Button(Loc.T("Delete All Edits…"), () =>
        {
            if (SheetWindow.Confirm(Window.GetWindow(this), Loc.T("Delete your edits in every book?"),
                    Loc.T("Rewritten translations, corrected originals and hidden balloons go back to the machine translation in every book. This can't be undone."), Loc.T("Delete All Edits")))
                service.Edits.DeleteAll();
        });
        deleteEdits.IsEnabled = editedPages > 0;
        DockPanel.SetDock(deleteEdits, Dock.Right);
        editsRow.Children.Add(deleteEdits);
        editsRow.Children.Add(Ui.Text($"{Loc.T("Edited pages")}: {editedPages}", "Text.Body"));
        _root.Children.Add(editsRow);

        // 캐시
        _root.Children.Add(Gap(Ui.Section(Loc.T("Translation Cache")), 18));
        var cacheRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var sizeText = Ui.Text($"{Loc.T("Cached translations")}: …", "Text.Body");
        var clear = Ui.Button(Loc.T("Clear Translation Cache"), () =>
        {
            service.Scheduler.StopAll();
            service.Cache.Clear();
            Queue();
        });
        DockPanel.SetDock(clear, Dock.Right);
        cacheRow.Children.Add(clear);
        cacheRow.Children.Add(sizeText);
        _root.Children.Add(cacheRow);
        _ = Task.Run(() => service.Cache.SizeBytes()).ContinueWith(t =>
        {
            if (!t.IsFaulted) sizeText.Text = $"{Loc.T("Cached translations")}: {JobsWindow.FormatBytes(t.Result)}";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private string TranslatorStatus()
    {
        var service = _f.Service;
        var m = service.Models.Find(ModelCatalog.TranslatorId);
        if (service.Providers.IsDeveloperTestProvider) return DeveloperProviders.Label;
        if (m is null) return Loc.T("Not Supported");
        if (service.Models.IsInstalled(m)) return Loc.T("Installed") + " · " + service.Providers.Translator.Name;
        return m.IsVerified ? Loc.T("Language pack not installed") : Loc.T("Not available in this build: the model isn't bundled or verified yet.");
    }

    /// <summary>팩 ID 대신 쓰임새를 보여 준다(번역 패널·진행 표시도 쓴다).</summary>
    internal static string PackTitle(string id) => id switch
    {
        ModelCatalog.DetectorId => Loc.T("Balloon detection (RT-DETR)"),
        ModelCatalog.MangaOcrId => Loc.T("Japanese text recognition (manga-ocr)"),
        ModelCatalog.PpOcrId => Loc.T("Chinese, English and Korean text recognition, language detection (PP-OCRv5)"),
        ModelCatalog.TranslatorId => Loc.T("Translation (Gemma 4 E2B)"),
        _ => id,
    };

    private FrameworkElement PackRow(ModelManifest m)
    {
        var service = _f.Service;
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        string status;
        Button? action = null;
        if (service.Models.IsInstalled(m)) status = Loc.T("Installed");
        else if (service.Models.DownloadProgress(m.Id) is { } p) status = $"{Loc.T("Downloading…")} {p:P0}";
        else if (!m.IsVerified) status = Loc.T("Not available in this build: the model isn't bundled or verified yet.");
        else
        {
            status = $"{Loc.T("Language pack not installed")} · {JobsWindow.FormatBytes(m.TotalBytes)}";
            action = Ui.Button(Loc.T("Download…"), () => _ = InstallAsync(m.Id));
        }
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); row.Children.Add(action); }
        var text = new StackPanel();
        text.Children.Add(Ui.Text(PackTitle(m.Id), "Text.Body", wrap: false));
        text.Children.Add(Ui.Caption($"{m.Id} · {status}"));
        row.Children.Add(text);
        return row;
    }

    private async Task InstallAsync(string id)
    {
        try
        {
            await _f.Service.Models.InstallAsync(id, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SheetWindow.Inform(Window.GetWindow(this), Loc.T("Language Pack"), ex.Message);
        }
        Queue();
    }
}
