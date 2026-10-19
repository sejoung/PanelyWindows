using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Panely.Core.Localization;
using Panely.Domain.Translation;

namespace Panely.App.Translation;

/// <summary>
/// 말풍선 팝오버: 원문(선택 가능)·보이는 언어 번역·다른 언어 번역, Edit Translation / Fix Original / Hide / Revert.
/// 편집: Enter 저장, Shift+Enter(또는 Ctrl/Alt+Enter) 줄바꿈, Escape 취소. IME 조합 중 Enter(Key.ImeProcessed)는 저장하지 않는다.
/// </summary>
internal sealed class BalloonPopover : Border
{
    public enum Mode { View, EditTranslation, FixOriginal }

    private readonly TranslationFeature _feature;
    private readonly int _page;
    private readonly DisplayBubble _bubble;
    private readonly List<(string Language, string Text)> _others;
    private Control? _initialFocus;

    public BalloonPopover(TranslationFeature feature, int page, DisplayBubble bubble, List<(string Language, string Text)> others)
    {
        _feature = feature;
        _page = page;
        _bubble = bubble;
        _others = others;
        Width = 340;
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(6);
        SetResourceReference(BackgroundProperty, "Brush.BgSecondary");
        SetResourceReference(BorderBrushProperty, "Brush.BorderSubtle");
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35 };
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Child is StackPanel { Tag: Mode.View }) { _feature.ClosePopover(); e.Handled = true; }
        };
        Enter(Mode.View);
    }

    public void FocusInitial() => Dispatcher.BeginInvoke(() => _initialFocus?.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    public void Enter(Mode mode)
    {
        Child = mode == Mode.View ? BuildView() : BuildEditor(mode);
        FocusInitial();
    }

    private static TextBox ReadOnlyText(string text)
    {
        var t = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = 14,
        };
        t.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        return t;
    }

    private string StateCaption() => _bubble.State switch
    {
        BubbleTextState.UserTranslation => Loc.T("Your translation"),
        BubbleTextState.CorrectedRetranslated when _bubble.IsAdded => Loc.T("Balloon you added — machine translation of its text"),
        BubbleTextState.CorrectedRetranslated => Loc.T("Machine translation of your corrected original"),
        BubbleTextState.CorrectedNeedsRetranslation or BubbleTextState.CorrectedPending => Loc.T("Original corrected — not translated again yet"),
        BubbleTextState.AddedNoText => Loc.T("Balloon you added — no text was read; type it with Fix Original"),
        BubbleTextState.AddedNotTranslated => Loc.T("Balloon you added — not translated into this language yet"),
        BubbleTextState.Pending => Loc.T("Translating…"),
        BubbleTextState.Untranslated => Loc.T("Couldn't translate this balloon, so the original is shown. You can type one with Edit Translation."),
        _ => Loc.T("Machine translation"),
    };

    private StackPanel BuildView()
    {
        var panel = new StackPanel { Tag = Mode.View };
        panel.Children.Add(Ui.Section(Loc.T("Original")));
        panel.Children.Add(ReadOnlyText(_bubble.EffectiveSource.Length > 0 ? _bubble.EffectiveSource : "—"));
        if (_bubble.EffectiveSource != _bubble.RecognizedSource && _bubble.RecognizedSource.Length > 0)
            panel.Children.Add(Ui.Caption(Loc.T("Recognized as: %@", _bubble.RecognizedSource)));

        if (_feature.VisibleLanguage is { } lang)
        {
            var header = Ui.Section(Loc.LanguageName(lang));
            header.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(header);
            panel.Children.Add(ReadOnlyText(_bubble.Text ?? "…"));
            panel.Children.Add(Ui.Caption(StateCaption()));
        }
        foreach (var (language, text) in _others)
        {
            var h = Ui.Section(Loc.LanguageName(language));
            h.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(h);
            panel.Children.Add(ReadOnlyText(text));
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button Add(string text, Action a, string? tip = null)
        {
            var b = Ui.Button(text, a, tooltip: tip);
            b.Margin = new Thickness(0, 0, 6, 6);
            actions.Children.Add(b);
            return b;
        }
        var edit = Add(Loc.T("Edit Translation…"), () => Enter(Mode.EditTranslation), Loc.T("Rewrite this translation"));
        edit.IsEnabled = _feature.VisibleLanguage is not null;
        Add(Loc.T("Fix Original…"), () => Enter(Mode.FixOriginal), Loc.T("Correct the recognized text and translate it again"));
        if (_bubble.IsAdded)
            Add(Loc.T("Remove"), () => { _feature.RemoveAdded(_page, _bubble); _feature.ClosePopover(); }, Loc.T("Remove this balloon you added"));
        else if (_bubble.IsHidden)
            Add(Loc.T("Show This Balloon"), () => { _feature.SetHidden(_page, _bubble, false); _feature.ClosePopover(); });
        else
            Add(Loc.T("Hide"), () => { _feature.SetHidden(_page, _bubble, true); _feature.ClosePopover(); },
                Loc.T("Hide this balloon — for text that isn't really there. Edit Balloons (%@) shows hidden ones again.", TranslationFeature.GestureHint(Core.Commands.CommandIds.TranslationEdit)));
        if (_bubble.IsEdited && !_bubble.IsAdded)
        {
            Button? revert = null;
            revert = Add(Loc.T("Revert"), () =>
            {
                var menu = new ContextMenu { PlacementTarget = revert, Placement = PlacementMode.Bottom };
                menu.Items.Add(Ui.MenuItem(Loc.T("Use Machine Translation"), () => Done(() => _feature.Revert(_page, _bubble, RevertKind.Translation)), tooltip: Loc.T("Use the machine translation again")));
                menu.Items.Add(Ui.MenuItem(Loc.T("Restore Original"), () => Done(() => _feature.Revert(_page, _bubble, RevertKind.Original)), tooltip: Loc.T("Restore the recognized original and its machine translation")));
                menu.Items.Add(Ui.MenuItem(Loc.T("Revert Both"), () => Done(() => _feature.Revert(_page, _bubble, RevertKind.Both))));
                menu.IsOpen = true;
            }, Loc.T("Go back to the machine translation, the recognized original, or both"));
        }
        panel.Children.Add(actions);
        _initialFocus = edit.IsEnabled ? edit : actions.Children.OfType<Control>().FirstOrDefault();
        return panel;
    }

    private void Done(Action action)
    {
        action();
        _feature.ClosePopover();
    }

    private StackPanel BuildEditor(Mode mode)
    {
        var panel = new StackPanel { Tag = mode };
        var fixing = mode == Mode.FixOriginal;
        panel.Children.Add(Ui.Section(fixing ? Loc.T("Fix Original") : Loc.T("Edit Translation…").TrimEnd('…')));
        var initial = fixing ? _bubble.EffectiveSource : _bubble.Text ?? string.Empty;
        var box = Ui.TextBox(initial, multiline: false);
        box.AcceptsReturn = false;
        box.TextWrapping = TextWrapping.Wrap;
        box.MinHeight = 64;
        box.Margin = new Thickness(0, 6, 0, 0);
        box.SelectAll();
        panel.Children.Add(box);
        panel.Children.Add(Ui.Caption(Loc.T("Shift+Enter for a new line")));

        void Save()
        {
            var text = box.Text.Replace("\r\n", "\n");
            if (fixing) _feature.FixOriginal(_page, _bubble, text);
            else _feature.SaveTranslation(_page, _bubble, text);
            _feature.ClosePopover();
        }

        box.PreviewKeyDown += (_, e) =>
        {
            // IME 조합 중 Enter는 e.Key == Key.ImeProcessed 로 들어오므로 여기서 걸리지 않는다
            if (e.Key == Key.Enter)
            {
                var mods = Keyboard.Modifiers;
                if (mods.HasFlag(ModifierKeys.Shift) || mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt))
                {
                    var caret = box.CaretIndex;
                    box.Text = box.Text.Insert(caret, "\n");
                    box.CaretIndex = caret + 1;
                }
                else Save();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Enter(Mode.View);
                e.Handled = true;
            }
        };

        var buttons = Ui.Row(Ui.Button(Loc.T("Cancel"), () => Enter(Mode.View)), Ui.Button(Loc.T("Save"), Save, accent: true));
        ((FrameworkElement)buttons.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(buttons);
        _initialFocus = box;
        return panel;
    }
}

/// <summary>편집 모드 바(뷰어 하단 +76): 안내·숨김 풍선·책 되돌리기·완료.</summary>
internal sealed class EditBar : Border
{
    private readonly TranslationFeature _feature;

    public EditBar(TranslationFeature feature)
    {
        _feature = feature;
        Padding = new Thickness(12, 8, 12, 8);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 0, 0, 8);
        MaxWidth = 720;
        SetResourceReference(BackgroundProperty, "Brush.CardBackground");
        SetResourceReference(BorderBrushProperty, "Brush.BorderSubtle");
    }

    public void Refresh()
    {
        var dock = new DockPanel { LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_feature.HasHiddenBalloons)
        {
            var show = Ui.Button(Loc.T("Show All Hidden Balloons"), _feature.ShowHiddenBalloons, tooltip: Loc.T("Go to a hidden balloon, or show them all again"));
            show.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(show);
        }
        var revert = Ui.Button(Loc.T("Revert This Book…"), _feature.RevertBook);
        revert.Margin = new Thickness(8, 0, 0, 0);
        revert.IsEnabled = _feature.Book is not null && _feature.Service.Edits.EditedBubbleCount(_feature.Book.BookKey) > 0;
        right.Children.Add(revert);
        var done = Ui.Button(Loc.T("Done"), _feature.ToggleEditMode, accent: true, tooltip: Loc.T("Back to reading (esc)").Replace("esc", "Esc"));
        done.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(done);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Ui.Text(Loc.T("Editing Balloons"), "Text.Body", wrap: false));
        left.Children.Add(Ui.Caption(Loc.T("Click a balloon to correct it — drag over text that was missed to add one")));
        dock.Children.Add(left);
        Child = dock;
    }
}
