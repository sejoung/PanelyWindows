using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Panely.App.Controls;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.App.Translation;

/// <summary>
/// AI 채팅 파일 교환 시트(폭 520). 앱은 AI 서비스에 직접 요청하지 않는다.
/// 전체 책 기본, Choose Pages로 좁힘 → 텍스트 파일 저장(UTF-8) → 프롬프트 복사 → 응답 .txt 열기/붙여넣기 → 바뀐 것만 사용자 수정으로 적용 → 일괄 Undo.
/// </summary>
internal sealed class AiChatSheet : SheetWindow
{
    private readonly TranslationFeature _f;
    private readonly OpenedBook _book;
    private readonly string _source;
    private readonly string _target;
    private readonly TranslationScope _scope;
    private readonly TextBox _prompt;
    private readonly TextBlock _status;
    private readonly Border _result;
    private readonly IconGlyph _resultIcon;
    private readonly TextBlock _resultTitle;
    private readonly TextBlock _resultDetail;
    private readonly Button _undo;
    private readonly TextBox _from;
    private readonly TextBox _to;
    private readonly TextBlock _count;
    private bool _wholeBook = true;
    /// <summary>이 범위로 저장한 파일의 snapshot. 저장한 적이 없으면 null이고 답을 적용하지 않는다.</summary>
    private AiChatSnapshot? _snapshot;
    private string? _fileName;
    private IReadOnlyDictionary<(string PageId, TranslationScope Scope), PageEdits?>? _undoSnapshot;

    private AiChatSheet(Window? owner, TranslationFeature feature, OpenedBook book, string source, string target)
        : base(owner, Loc.T("Improve with an AI Chat"), 520)
    {
        _f = feature;
        _book = book;
        _source = source;
        _target = target;
        _scope = feature.Scope;

        var body = new StackPanel();
        body.Children.Add(Ui.Text(Loc.T("Improve with an AI Chat"), "Text.Title"));
        body.Children.Add(Gap(Ui.Caption(Loc.T("A chat that reads the whole book can fix what translating balloon by balloon misses: who's speaking, how they speak, names. Use the one you already have — ChatGPT, Claude, Gemini or another."))));

        body.Children.Add(Gap(Ui.Section(Loc.T("Pages")), 14));
        // 다시 열면 마지막으로 저장한 범위를 고른다. 그 파일의 답을 붙여넣으러 온 것이다.
        var saved = feature.LastAiChatRange(book.BookKey, source, target, _scope) ?? (0, book.Pages.Count - 1);
        _wholeBook = saved.From == 0 && saved.To == book.Pages.Count - 1;
        _from = Ui.TextBox((saved.From + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _to = Ui.TextBox((saved.To + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _from.Width = _to.Width = 64;
        _from.IsEnabled = _to.IsEnabled = !_wholeBook;
        body.Children.Add(Gap(Ui.Radio(Loc.T("Whole Book"), "ai-scope", _wholeBook, () => { _wholeBook = true; _from.IsEnabled = _to.IsEnabled = false; RangeChanged(); }), 4));
        var choose = Ui.Radio(Loc.T("Choose Pages"), "ai-scope", !_wholeBook, () => { _wholeBook = false; _from.IsEnabled = _to.IsEnabled = true; RangeChanged(); });
        var range = Ui.Row(choose, Spaced(Ui.Caption(Loc.T("From page")), 12), Spaced(_from, 6), Spaced(Ui.Caption(Loc.T("To page")), 12), Spaced(_to, 6));
        _from.TextChanged += (_, _) => RangeChanged();
        _to.TextChanged += (_, _) => RangeChanged();
        body.Children.Add(Gap(range, 4));
        _count = Ui.Text(string.Empty, "Text.Caption");
        body.Children.Add(Gap(_count, 6));

        var save = Ui.Button(Loc.T("Save as Text File…"), SaveFile, accent: true);
        body.Children.Add(Gap(save, 14));
        body.Children.Add(Gap(Ui.Caption(Loc.T("The balloons, a line each: [id] original → translation.")), 2));

        body.Children.Add(Gap(Ui.Section(Loc.T("Copy Prompt")), 14));
        body.Children.Add(Gap(Ui.Caption(Loc.T("Paste this into the chat along with the file. Change it if you like.")), 2));
        _prompt = Ui.TextBox(string.Empty, multiline: true);
        _prompt.Height = 140;
        body.Children.Add(Gap(_prompt, 6));
        body.Children.Add(Gap(Ui.Button(Loc.T("Copy Prompt"), CopyPrompt), 6));

        body.Children.Add(Gap(Ui.Section(Loc.T("Open the Reply…").TrimEnd('…')), 14));
        body.Children.Add(Gap(Ui.Caption(Loc.T("Open the text file the chat sent back, or paste its reply if it answered in the chat. Changed translations replace the current ones.")), 2));
        var replyRow = Ui.Row(Ui.Button(Loc.T("Open the Reply…"), OpenReply), Spaced(Ui.Button(Loc.T("Paste the Reply"), PasteReply), 8));
        body.Children.Add(Gap(replyRow, 6));
        _status = Ui.Text(string.Empty, "Text.Caption");
        body.Children.Add(Gap(_status, 8));

        // 답을 적용한 결과 카드: 끝났는지(✓)·빠진 게 있는지(⚠)를 한눈에, 되돌리기는 카드 안의 링크로
        _resultIcon = new IconGlyph { Glyph = Icons.Check, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        _resultTitle = Ui.Text(string.Empty, "Text.Body");
        _resultTitle.FontWeight = FontWeights.SemiBold;
        _resultDetail = Ui.Caption(string.Empty);
        _undo = Ui.LinkButton(Loc.T("Undo This Reply"), Undo);
        _undo.ToolTip = Loc.T("Put back the translations this reply changed");
        _undo.HorizontalAlignment = HorizontalAlignment.Right;
        var resultText = new StackPanel();
        resultText.Children.Add(_resultTitle);
        resultText.Children.Add(Gap(_resultDetail, 2));
        resultText.Children.Add(Gap(_undo, 4));
        var resultRow = new DockPanel();
        DockPanel.SetDock(_resultIcon, Dock.Left);
        resultRow.Children.Add(_resultIcon);
        resultRow.Children.Add(resultText);
        _result = new Border { Child = resultRow, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        _result.SetResourceReference(Border.BackgroundProperty, "Brush.BgTertiary");
        _result.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
        body.Children.Add(Gap(_result, 8));

        body.Children.Add(Gap(Ui.Caption(Loc.T("Your text leaves your Mac only when you give it to a chat. Changed balloons count as your edits: each can go back to the machine translation.")), 14));
        var close = Ui.Button(Loc.T("Done"), Close, accent: true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(Gap(close, 12));
        SetBody(body, save);
        RangeChanged();
        _prompt.Text = AiChatPrompt.Build(_snapshot ?? new AiChatSnapshot(book.BookKey, book.Title, source, target, []), FileNameFor());
    }

    private TranslationFeature.AiChatSaveKey SaveKey()
    {
        var (from, to) = PageRange();
        return new TranslationFeature.AiChatSaveKey(_book.BookKey, _source, _target, _scope, from, to);
    }

    /// <summary>범위가 바뀌면 파일에 담길 말풍선 수를 다시 센다. 응답은 저장할 때 만든 snapshot에 맞추므로 그 범위로 저장한 snapshot으로 바꾼다.</summary>
    private void RangeChanged()
    {
        _snapshot = _f.SavedAiChat(SaveKey());
        _count.Text = $"{Loc.T("Balloons")}: {BuildSnapshot().Entries.Count}";
    }

    public static void Show(Window? owner, TranslationFeature feature)
    {
        if (feature.Book is not { } book || feature.SourceLanguage is not { } source || feature.VisibleLanguage is not { } target) return;
        new AiChatSheet(owner, feature, book, source, target).ShowDialog();
    }

    private static FrameworkElement Gap(FrameworkElement e, double top = 8) { e.Margin = new Thickness(0, top, 0, 0); return e; }

    private static FrameworkElement Spaced(FrameworkElement e, double left) { e.Margin = new Thickness(left, 0, 0, 0); e.VerticalAlignment = VerticalAlignment.Center; return e; }

    private string FileNameFor() => _fileName ?? AiChatPrompt.FileName(new AiChatSnapshot(_book.BookKey, _book.Title, _source, _target, []));

    private (int From, int To) PageRange()
    {
        if (_wholeBook) return (0, _book.Pages.Count - 1);
        var from = int.TryParse(_from.Text, out var a) ? Math.Clamp(a, 1, _book.Pages.Count) : 1;
        var to = int.TryParse(_to.Text, out var b) ? Math.Clamp(b, 1, _book.Pages.Count) : _book.Pages.Count;
        if (to < from) (from, to) = (to, from);
        return (from - 1, to - 1);
    }

    /// <summary>지금 상태로 snapshot을 만든다. 응답은 파일을 저장할 때 만든 snapshot에 매핑한다.</summary>
    private AiChatSnapshot BuildSnapshot()
    {
        var (from, to) = PageRange();
        var pages = new List<(int, IReadOnlyList<DisplayBubble>)>();
        for (var i = from; i <= to; i++)
        {
            var entry = _book.Pages[i];
            var cached = _f.Service.Scheduler.LoadCached(entry.PageId, _source, _target, _scope);
            var edits = _f.Service.Edits.Get(entry.PageId, _scope);
            if (cached is null && edits is null) continue;
            pages.Add((i, EditResolver.Resolve(cached?.Bubbles ?? [], edits, _target)));
        }
        return AiChatExchange.BuildSnapshot(_book.BookKey, _book.Title, _source, _target, pages);
    }

    private void SaveFile()
    {
        // 저장을 끝낸 뒤에야 이전 snapshot을 바꾼다. 대화 상자를 취소하면 이전에 저장한 파일의 답을 그대로 받을 수 있어야 한다.
        var snapshot = BuildSnapshot();
        if (snapshot.Entries.Count == 0)
        {
            _status.Text = Loc.T("Nothing in this book is translated into this language yet.");
            return;
        }
        var dialog = new SaveFileDialog { FileName = AiChatPrompt.FileName(snapshot), Filter = "Text (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, AiChatExchange.BuildFile(snapshot, Loc.LanguageNameIn("en", _source), Loc.LanguageNameIn("en", _target)), new UTF8Encoding(false));
            _snapshot = snapshot;
            _f.RememberAiChatSave(SaveKey(), snapshot);
            _fileName = System.IO.Path.GetFileName(dialog.FileName);
            _prompt.Text = AiChatPrompt.Build(snapshot, _fileName);
            _status.Text = Loc.T("Saved “%@”. Attach it to a new chat.", _fileName);
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("Couldn't save the file: %@", ex.Message);
        }
    }

    private void CopyPrompt()
    {
        try
        {
            Clipboard.SetText(_prompt.Text);
            _status.Text = Loc.T("Copied. Paste it into the chat with the file attached.");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void OpenReply()
    {
        var dialog = new OpenFileDialog { Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        string text;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            text = DecodeReply(bytes);
        }
        catch (Exception)
        {
            _status.Text = Loc.T("Couldn't read that file as text.");
            return;
        }
        Apply(text);
    }

    /// <summary>UTF-8(BOM 허용)를 기본으로 하고, UTF-16 BOM은 명시적으로 처리한다. 잘못된 UTF-8은 거부한다.</summary>
    internal static string DecodeReply(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
    }

    private void PasteReply()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; } catch (Exception) { text = string.Empty; }
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = Loc.T("The clipboard has no text. Copy the chat's reply first."); return; }
        Apply(text);
    }

    private void Apply(string reply)
    {
        var parsed = AiChatExchange.Parse(reply);
        if (parsed.Count == 0)
        {
            _result.Visibility = Visibility.Collapsed;
            _status.Text = Loc.T("No balloon lines like “[p12-3] …” in it. Use the chat's whole reply.");
            return;
        }
        // id는 저장한 파일에서만 뜻이 있다. 지금 상태로 snapshot을 다시 만들면 숨긴 풍선만큼 번호가 밀려 다른 풍선에 적용된다.
        var changes = _snapshot is null ? null : AiChatExchange.ComputeChanges(_snapshot, parsed);
        if (_snapshot is null || changes is null || !CanApply(_snapshot, changes))
        {
            _result.Visibility = Visibility.Collapsed;
            _status.Text = Loc.T("This reply can't be matched to the balloons: they changed after the file was saved, or the file wasn't saved since Panely started. Save the text file again and give it to the chat.");
            return;
        }
        // 적용 전 snapshot(일괄 Undo)
        var affected = changes.Updates.Keys.Select(id => _snapshot.Find(id)!).Select(e => (_book.Pages[e.PageIndex].PageId, _scope)).Distinct().ToList();
        _undoSnapshot = _f.Service.Edits.Snapshot(affected);
        foreach (var (id, text) in changes.Updates)
        {
            var entry = _snapshot.Find(id)!;
            var pageId = _book.Pages[entry.PageIndex].PageId;
            var cached = _f.Service.Scheduler.LoadCached(pageId, _source, _target, _scope);
            var edits = _f.Service.Edits.Get(pageId, _scope) ?? PageEditsEditor.Empty(pageId, _scope);
            var bubble = AiChatExchange.FindBubble(entry, EditResolver.Resolve(cached?.Bubbles ?? [], edits, _target));
            if (bubble is null) continue;
            int index;
            if (bubble.IsAdded && bubble.EditIndex is { } ai) index = ai;
            else (edits, index) = PageEditsEditor.Ensure(edits, bubble.RecognizedSource, bubble.Rect);
            _f.Service.Edits.Save(PageEditsEditor.SetTranslation(edits, index, _target, text));
        }
        var returned = changes.Returned;
        var missing = changes.Missing.Count > 0;
        var detail = missing
            ? Loc.T("%lld balloons came back: %lld changed, %lld already right. Missing %lld: %@ — ask the chat for those, or choose fewer pages.",
                returned, changes.Updates.Count, changes.Unchanged, changes.Missing.Count, string.Join(", ", changes.Missing.Take(12)) + (changes.Missing.Count > 12 ? "…" : string.Empty))
            : Loc.T("All %lld balloons came back: %lld changed, %lld already right.", returned, changes.Updates.Count, changes.Unchanged);
        if (changes.Unknown > 0) detail += " " + Loc.T("Ignored %lld lines with ids that weren't in the file.", changes.Unknown);
        var title = missing ? Loc.T("Applied, but some balloons didn't come back")
            : changes.Updates.Count > 0 ? Loc.T("Applied the reply")
            : Loc.T("Checked the reply — no translation needed changing");
        ShowResult(missing ? Icons.Warning : Icons.Check, missing ? "Brush.Danger" : "Brush.Accent", title, detail, canUndo: changes.Updates.Count > 0);
        _f.Host?.InvalidateOverlay();
    }

    /// <summary>바꿀 줄이 모두 저장할 때의 그 말풍선을 아직 가리키는가. 하나라도 아니면 어느 답도 적용하지 않는다.</summary>
    private bool CanApply(AiChatSnapshot snapshot, AiChatChanges changes)
    {
        foreach (var page in changes.Updates.Keys.Select(id => snapshot.Find(id)!).GroupBy(e => e.PageIndex))
        {
            if (page.Key >= _book.Pages.Count) return false;
            var pageId = _book.Pages[page.Key].PageId;
            var cached = _f.Service.Scheduler.LoadCached(pageId, _source, _target, _scope);
            var bubbles = EditResolver.Resolve(cached?.Bubbles ?? [], _f.Service.Edits.Get(pageId, _scope), _target);
            if (page.Any(e => AiChatExchange.FindBubble(e, bubbles) is null)) return false;
        }
        return true;
    }

    private void ShowResult(Geometry icon, string iconBrush, string title, string detail, bool canUndo)
    {
        _status.Text = string.Empty;
        _resultIcon.Glyph = icon;
        _resultIcon.SetResourceReference(IconGlyph.ForegroundProperty, iconBrush);
        _resultTitle.Text = title;
        _resultDetail.Text = detail;
        _resultDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _undo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        _result.Visibility = Visibility.Visible;
    }

    private void Undo()
    {
        if (_undoSnapshot is null) return;
        _f.Service.Edits.Restore(_undoSnapshot);
        _undoSnapshot = null;
        ShowResult(Icons.Refresh, "Brush.TextSecondary", Loc.T("Undid this reply. The translations it changed are back."), string.Empty, canUndo: false);
        _f.Host?.InvalidateOverlay();
    }
}
