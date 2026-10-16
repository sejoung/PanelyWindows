using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Panely.Core.Localization;
using Panely.Core.Settings;
using Panely.Core.Translation;
using Panely.Domain.Model;

namespace Panely.App.Translation;

/// <summary>
/// 번역본 내보내기 시트(폭 460, padding 16): 언어 / CBZ·ZIP·폴더 / 미번역 정책 / 저장 위치.
/// 선택을 기억하고 목적지 폴더는 한 번 고른 뒤 재사용한다.
/// </summary>
internal sealed class ExportSheet : SheetWindow
{
    private readonly TranslationFeature _f;
    private readonly IReadOnlyList<BookTarget> _books;
    private readonly string? _source;
    private readonly TranslationScope _scope;
    private string _target;
    private ExportFormat _format;
    private UntranslatedPolicy _policy;
    private string? _destination;
    private readonly TextBlock _destinationText;
    private readonly TextBlock _error;

    private ExportSheet(Window? owner, TranslationFeature feature, IReadOnlyList<BookTarget> books, string target, string? source, TranslationScope scope)
        : base(owner, books.Count > 1 ? Loc.T("Export Translated Series") : Loc.T("Export Translated Book"), 460)
    {
        _f = feature;
        _books = books;
        _source = source;
        _scope = scope;
        var state = feature.StateStore;
        var remembered = state.ExportOptions;
        _target = target;
        _format = remembered?.Format ?? ExportFormat.Cbz;
        _policy = remembered?.Untranslated ?? UntranslatedPolicy.TranslateFirst;
        _destination = state.ExportDestination;

        var body = new StackPanel();
        body.Children.Add(Ui.Text(Title, "Text.Title"));
        if (books.Count > 1)
            body.Children.Add(Gap(Ui.Caption(Loc.T("%lld volumes: %@", books.Count, string.Join(", ", books.Select(b => b.Title).Take(6)) + (books.Count > 6 ? "…" : string.Empty)))));

        body.Children.Add(Gap(Ui.Section(Loc.T("Language")), 14));
        var languages = TranslationLanguages.All.Where(l => l != source).Select(l => (Loc.LanguageName(l), l)).ToList();
        body.Children.Add(Gap(Ui.Combo(languages, _target, v => { _target = v; Validate(); }), 4));

        body.Children.Add(Gap(Ui.Section(Loc.T("Format")), 14));
        body.Children.Add(Gap(Ui.Radio(Loc.T("CBZ"), "fmt", _format == ExportFormat.Cbz, () => _format = ExportFormat.Cbz), 4));
        body.Children.Add(Gap(Ui.Radio(Loc.T("ZIP"), "fmt", _format == ExportFormat.Zip, () => _format = ExportFormat.Zip), 4));
        body.Children.Add(Gap(Ui.Radio(Loc.T("Folder of Images"), "fmt", _format == ExportFormat.Folder, () => _format = ExportFormat.Folder), 4));

        body.Children.Add(Gap(Ui.Section(Loc.T("Pages Not Translated Yet")), 14));
        body.Children.Add(Gap(Ui.Radio(Loc.T("Translate Them First"), "policy", _policy == UntranslatedPolicy.TranslateFirst, () => { _policy = UntranslatedPolicy.TranslateFirst; Validate(); }), 4));
        body.Children.Add(Gap(Ui.Radio(Loc.T("Export As They Are"), "policy", _policy == UntranslatedPolicy.ExportAsIs, () => { _policy = UntranslatedPolicy.ExportAsIs; Validate(); }), 4));

        body.Children.Add(Gap(Ui.Section(Loc.T("Save To")), 14));
        var destRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var choose = Ui.Button(Loc.T("Choose…"), ChooseDestination);
        DockPanel.SetDock(choose, Dock.Right);
        destRow.Children.Add(choose);
        _destinationText = Ui.Text(_destination ?? Loc.T("Not Chosen"), "Text.Body", wrap: false);
        _destinationText.VerticalAlignment = VerticalAlignment.Center;
        _destinationText.ToolTip = _destination;
        destRow.Children.Add(_destinationText);
        body.Children.Add(destRow);

        body.Children.Add(Gap(Ui.Caption(Loc.T("Your edits are included and hidden balloons are left out. The original files aren't changed.")), 14));
        body.Children.Add(Gap(Ui.Caption(_policy == UntranslatedPolicy.TranslateFirst
            ? Loc.T("Runs in the background — keep reading. Untranslated pages are translated first, which can take a while. Follow it in Window › Jobs (%@).", TranslationFeature.GestureHint(Core.Commands.CommandIds.JobsShow))
            : Loc.T("Runs in the background — keep reading. Follow it in Window › Jobs (%@).", TranslationFeature.GestureHint(Core.Commands.CommandIds.JobsShow))), 4));
        _error = Ui.Text(string.Empty, "Text.Caption");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        body.Children.Add(Gap(_error, 8));

        var export = Ui.Button(Loc.T("Export"), Export, accent: true);
        var buttons = Ui.Row(Ui.Button(Loc.T("Cancel"), Close), export);
        export.Margin = new Thickness(8, 0, 0, 0);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(Gap(buttons, 16));
        SetBody(body, export);
        Validate();
    }

    public static void Show(Window? owner, TranslationFeature feature, IReadOnlyList<BookTarget> books, string target, string? source, TranslationScope scope)
    {
        var sheet = new ExportSheet(owner, feature, books, target, source, scope);
        sheet.ShowDialog();
    }

    private static FrameworkElement Gap(FrameworkElement e, double top = 8)
    {
        e.Margin = new Thickness(0, top, 0, 0);
        return e;
    }

    private bool Validate()
    {
        _error.Text = string.Empty;
        if (_policy == UntranslatedPolicy.ExportAsIs && _source is not null && _books.Count == 1
            && _f.Book?.BookKey == _books[0].BookKey && _f.CompletedPages(_target) == 0)
        {
            _error.Text = Loc.T("Nothing in this book is translated into this language yet.");
            return false;
        }
        return true;
    }

    private void ChooseDestination()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Choose where translated books are saved."), InitialDirectory = _destination ?? string.Empty };
        if (dialog.ShowDialog(this) != true) return;
        _destination = dialog.FolderName;
        _destinationText.Text = _destination;
        _destinationText.ToolTip = _destination;
    }

    private void Export()
    {
        if (_destination is null)
        {
            ChooseDestination();
            if (_destination is null) { _error.Text = Loc.T("Choose a folder to save to."); return; }
        }
        if (!Validate()) return;
        var state = _f.StateStore;
        state.ExportOptions = new ExportOptions { Target = _target, Format = _format, Untranslated = _policy };
        state.ExportDestination = _destination;
        foreach (var book in _books)
        {
            var source = book.BookKey == _f.Book?.BookKey ? _source : null;
            _f.Service.Exports.Enqueue(new ExportRequest(book, _target, source, _format, _policy, _destination, book.BookKey == _f.Book?.BookKey ? _scope : TranslationScope.Page));
        }
        Close();
        _f.ShowJobs();
    }
}
