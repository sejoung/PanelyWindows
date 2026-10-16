using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Model;

namespace Panely.App.Translation;

/// <summary>
/// 설정 창 "실험" 탭: 번역 보조, 기본 언어, Fast/Accurate(실제 능력), 말투 맞춤(사용 불가 사유), 언어팩·OCR 상태,
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

        // 모드
        _root.Children.Add(Gap(Ui.Section(Loc.T("Translation Mode")), 18));
        foreach (var mode in new[] { TranslationMode.Fast, TranslationMode.Accurate })
        {
            var label = mode == TranslationMode.Fast ? Loc.T("Fast") : Loc.T("Accurate");
            _root.Children.Add(Gap(Ui.Radio(label, "tr-mode", settings.TranslationMode == mode, () => state.UpdateSettings(s => s.TranslationMode = mode)), 4));
            _root.Children.Add(Ui.Caption("   " + ModeCapability(mode)));
        }
        _root.Children.Add(Gap(Ui.Caption(Loc.T("Fast uses a smaller, quantized model; Accurate uses the full-precision model with a wider search and takes longer. Each mode downloads its own language pack.")), 4));

        // 말투
        var speech = service.Providers.SpeechLevel.GetCapability(TranslationLanguages.Japanese, TranslationLanguages.Korean);
        var speechToggle = Ui.Toggle(Loc.T("Match Speech Level"), speech.IsReady && settings.TranslationMatchesSpeechLevel,
            on => state.UpdateSettings(s => s.TranslationMatchesSpeechLevel = on), switchStyle: true);
        speechToggle.IsEnabled = speech.IsReady;
        _root.Children.Add(Gap(speechToggle, 18));
        _root.Children.Add(Gap(Ui.Caption(speech.IsReady ? Loc.T("Japanese ↔ Korean: endings are rewritten to match the original's speech level; a rewrite is used only if it keeps the same words.") : speech.Reason ?? string.Empty), 4));

        // 언어팩
        _root.Children.Add(Gap(Ui.Section(Loc.T("Language Packs")), 18));
        foreach (var m in service.Models.Manifests)
            _root.Children.Add(PackRow(m));
        _root.Children.Add(Gap(Ui.Section(Loc.T("Text recognition (Windows OCR)")), 14));
        foreach (var lang in TranslationLanguages.All)
        {
            var cap = service.Providers.Recognizer.GetCapability(lang);
            _root.Children.Add(Gap(Ui.Caption($"{Loc.LanguageName(lang)} — {(cap.IsReady ? Loc.T("Installed") : cap.Reason)}"), 4));
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

    private string ModeCapability(TranslationMode mode)
    {
        var service = _f.Service;
        var id = LanguagePackManager.TranslatorPackFor(mode);
        var m = service.Models.Find(id);
        if (service.Providers.IsDeveloperTestProvider) return DeveloperProviders.Label;
        if (m is null) return Loc.T("Not Supported");
        if (service.Models.IsInstalled(m)) return Loc.T("Installed") + " · " + service.Providers.Translator.Name;
        return m.IsVerified ? Loc.T("Language pack not installed") : Loc.T("Not available in this build: the model isn't bundled or verified yet.");
    }

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
        text.Children.Add(Ui.Text(m.Id, "Text.Body", wrap: false));
        text.Children.Add(Ui.Caption($"{m.Mode} · {status}"));
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
