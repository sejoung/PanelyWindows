using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Panely.App.Integration;
using Panely.Core.Abstractions;
using Panely.Core.Commands;
using Panely.Core.Localization;
using Panely.Core.Translation;
using Panely.Domain.Library;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.App.Translation;

/// <summary>
/// 번역 기능 전체(06-translation.md). 리더는 ITranslationFeature로만 호출한다.
/// 상태 원칙: 책을 보이는 언어(_visible)와 백그라운드에서 만드는 언어(작업)를 분리한다.
/// 원본/번역·편집 선택은 다른 책의 작업을 취소하지 않는다.
/// </summary>
public sealed partial class TranslationFeature : ITranslationFeature, IDisposable
{
    private readonly AppServices _services;
    private readonly WpfTranslationRenderer _renderer;
    private readonly Dispatcher _ui;
    private IReaderHost? _host;

    private OpenedBook? _book;
    private BookTarget? _bookTarget;
    private string? _source;
    private string? _visible;
    private readonly Dictionary<int, TranslatedPage> _pages = new();
    private readonly HashSet<int> _translated = new();
    private readonly Dictionary<string, HashSet<int>> _completedSets = new();
    private readonly HashSet<(string PageId, int EditIndex, string Language)> _pendingRetranslations = new();
    private int _bookEpoch;
    private bool _editing;
    private bool _stateQueued;
    private TranslationScope _loadedScope;

    public TranslationFeature(AppServices services)
    {
        _services = services;
        _ui = Dispatcher.CurrentDispatcher;
        _renderer = new WpfTranslationRenderer(services.Decoder);
        Service = new TranslationService(services.Paths, services.Opener, services.State, services.Diagnostics,
            new WpfPagePixelSource(services.Decoder), _renderer);
        Service.Scheduler.PageUpdated += (_, u) => _ui.BeginInvoke(() => OnPageUpdated(u));
        Service.Scheduler.JobChanged += (_, j) => _ui.BeginInvoke(() => OnJobChanged(j));
        Service.Scheduler.JobRemoved += (_, _) => _ui.BeginInvoke(QueueStateChanged);
        Service.Exports.JobChanged += (_, _) => _ui.BeginInvoke(QueueStateChanged);
        Service.Exports.JobRemoved += (_, _) => _ui.BeginInvoke(QueueStateChanged);
        Service.Edits.Changed += (_, _) => _ui.BeginInvoke(() => { _host?.InvalidateOverlay(); QueueStateChanged(); });
        Service.Models.Changed += (_, _) => _ui.BeginInvoke(QueueStateChanged);
        Service.Cache.Cleared += (_, _) => _ui.BeginInvoke(() => { ReloadBookState(); });
        services.State.Changed += (_, change) =>
        {
            if (change.HasFlag(StateChange.Settings)) _ui.BeginInvoke(OnSettingsChanged);
        };
        Loc.LanguageChanged += (_, _) => _ui.BeginInvoke(() => { RebuildOpenUi(); QueueStateChanged(); });
        _wasEnabled = IsEnabled;
    }

    /// <summary>번역 계층(테스트·진단용 접근).</summary>
    public TranslationService Service { get; }

    public bool IsEnabled => _services.State.Settings.TranslationEnabled;

    public event EventHandler? StateChanged;

    private IStateStore State => _services.State;

    private TranslationMode Mode => State.Settings.TranslationMode;

    private TranslationScope CurrentScope => _host?.Layout == ReadingLayout.Vertical ? TranslationScope.Strip : TranslationScope.Page;

    private bool ShowingTranslation => State.Settings.TranslationOverlayVisible && _visible is not null;

    public bool IsOverlayActive => IsEnabled && _book is not null && _visible is not null && (ShowingTranslation || _editing);

    public void Attach(IReaderHost host)
    {
        _host = host;
        host.BookChanged += (_, _) => OnBookChanged();
        host.VisiblePagesChanged += (_, _) => OnVisiblePagesChanged();
        OnBookChanged();
    }

    // ───────────────────────── 언어 결정 ─────────────────────────

    /// <summary>시리즈 선호 → 기본 언어 설정 → UI 언어(미지원이면 en). 원어와 같으면 다른 언어.</summary>
    private string PreferredTarget()
    {
        var series = _book is null ? null : State.GetSeries(_book.SeriesKey);
        var target = series?.TranslationTarget ?? State.Settings.TranslationTargetLanguage ?? TranslationLanguages.DefaultTarget(Loc.Language);
        if (!TranslationLanguages.IsSupported(target)) target = TranslationLanguages.English;
        if (target == _source) target = _source == TranslationLanguages.English ? TranslationLanguages.Korean : TranslationLanguages.English;
        return target;
    }

    /// <summary>완료 쪽이 있거나, 실행/대기 중이거나, 사용자가 일시정지한 작업이 있으면 그 언어에 "결과"가 있다.</summary>
    private bool HasResults(string language) =>
        CompletedPages(language) > 0
        || (_book is not null && Service.Scheduler.Get(_book.BookKey, language) is { } j && (j.IsRunningOrQueued || j.UserPaused));

    // ───────────────────────── 책/쪽 변경 ─────────────────────────

    private void OnBookChanged()
    {
        CloseTransientUi();
        _editing = false;
        _bookEpoch++;
        _pages.Clear();
        _translated.Clear();
        _completedSets.Clear();
        _book = _host?.CurrentBook;
        _bookTarget = _book is null ? null : BookTarget.From(_book);
        _source = null;
        _visible = null;
        if (_book is null || !IsEnabled)
        {
            Service.Scheduler.SetCurrent(_book?.BookKey, null, 0);
            QueueStateChanged();
            _host?.InvalidateOverlay();
            return;
        }

        var series = State.GetSeries(_book.SeriesKey);
        _source = series?.TranslationSource ?? Service.Scheduler.JobsForBook(_book.BookKey).Select(j => j.Source).FirstOrDefault(s => s is not null);
        var preferred = PreferredTarget();
        if (HasResults(preferred)) _visible = preferred;
        Service.Scheduler.SetCurrent(_book.BookKey, preferred, _host!.CurrentPageIndex);
        ReloadBookState();
        QueueStateChanged();
    }

    /// <summary>캐시에서 언어별 완료 수·원어를 백그라운드로 다시 계산한다.</summary>
    private void ReloadBookState()
    {
        var book = _book;
        if (book is null || !IsEnabled) return;
        var epoch = _bookEpoch;
        var scope = CurrentScope;
        var mode = Mode;
        var knownSource = _source;
        _ = Task.Run(() =>
        {
            var source = knownSource ?? DiscoverSourceFromCache(book, scope, mode);
            var counts = new Dictionary<string, HashSet<int>>();
            if (source is not null)
                foreach (var lang in TranslationLanguages.All.Where(l => l != source))
                    counts[lang] = Service.Scheduler.CachedPages(book.Pages, source, lang, scope, mode);
            return (source, counts);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || epoch != _bookEpoch) return;
            var (source, counts) = t.Result;
            if (_source is null && source is not null) _source = source;
            _loadedScope = scope;
            _completedSets.Clear();
            foreach (var (lang, set) in counts) _completedSets[lang] = set;
            var preferred = PreferredTarget();
            if (_visible is null && HasResults(preferred)) _visible = preferred;
            _translated.Clear();
            if (_visible is not null && counts.TryGetValue(_visible, out var visibleSet)) _translated.UnionWith(visibleSet);
            MaybeAutoTranslate();
            LoadVisiblePages();
            QueueStateChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private string? DiscoverSourceFromCache(OpenedBook book, TranslationScope scope, TranslationMode mode)
    {
        var probe = book.Pages.Take(3).ToList();
        foreach (var source in TranslationLanguages.All)
            foreach (var target in TranslationLanguages.All.Where(t => t != source))
                if (Service.Scheduler.CachedPages(probe, source, target, scope, mode).Count > 0) return source;
        return null;
    }

    private void MaybeAutoTranslate()
    {
        if (_book is null || _bookTarget is null) return;
        var series = State.GetSeries(_book.SeriesKey);
        if (series?.AutoTranslate != true) return;
        var target = PreferredTarget();
        if (CompletedPages(target) >= _book.Pages.Count) return;
        if (Service.Scheduler.Get(_book.BookKey, target) is not null) return;
        _visible = target;
        Service.Scheduler.Start(_bookTarget, target, _source, CurrentScope, _host?.CurrentPageIndex ?? 0);
    }

    private void OnVisiblePagesChanged()
    {
        if (_book is null || _host is null || !IsEnabled) return;
        if (_loadedScope != CurrentScope) { _pages.Clear(); ReloadBookState(); }
        Service.Scheduler.SetCurrent(_book.BookKey, _visible ?? PreferredTarget(), _host.CurrentPageIndex);
        CloseTransientUi();
        LoadVisiblePages();
    }

    /// <summary>보이는 범위 ±2의 캐시를 비동기로 읽는다(디스크 I/O는 worker).</summary>
    private void LoadVisiblePages()
    {
        if (_host is null || _book is null || _visible is null || _source is null) return;
        var range = _host.VisibleRange;
        var book = _book;
        var target = _visible;
        var source = _source;
        var scope = CurrentScope;
        var mode = Mode;
        var epoch = _bookEpoch;
        var wanted = Enumerable.Range(Math.Max(0, range.Start - 2), Math.Min(book.Pages.Count, range.End + 2) - Math.Max(0, range.Start - 2))
            .Where(i => !_pages.ContainsKey(i) && (_translated.Contains(i) || scope == TranslationScope.Strip)).ToList();
        // 먼 페이지 정리
        foreach (var far in _pages.Keys.Where(k => k < range.Start - 20 || k > range.End + 20).ToList()) _pages.Remove(far);
        if (wanted.Count == 0) { _host.InvalidateOverlay(); return; }
        _ = Task.Run(() => wanted.Select(i => (i, Service.Scheduler.LoadCached(book.Pages[i].PageId, source, target, scope, mode))).ToList())
            .ContinueWith(t =>
            {
                if (t.IsFaulted || epoch != _bookEpoch || target != _visible) return;
                foreach (var (i, page) in t.Result) if (page is not null && !_pages.ContainsKey(i)) _pages[i] = page;
                _host?.InvalidateOverlay();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnPageUpdated(TranslationPageUpdate u)
    {
        if (_book is null || u.BookKey != _book.BookKey) { QueueStateChanged(); return; }
        if (u.Page.IsComplete)
        {
            // 언어별 완료량
            if (!_completedSets.TryGetValue(u.Target, out var set)) _completedSets[u.Target] = set = [];
            set.Add(u.PageIndex);
            if (u.Target == _visible) _translated.Add(u.PageIndex);
        }
        if (_visible is null && u.Target == PreferredTarget() && State.Settings.TranslationOverlayVisible) _visible = u.Target;
        if (u.Target == _visible)
        {
            _pages[u.PageIndex] = u.Page;
            _host?.InvalidateOverlay();
        }
        QueueStateChanged();
    }

    private readonly HashSet<(string BookKey, string Target)> _languageProblemShown = [];

    private void OnJobChanged(TranslationJobInfo info)
    {
        // 원어를 알 수 없거나 번역할 언어와 같아 멈췄으면, 원문 언어 칸이 강조된 패널을 한 번 열어 바로 고를 수 있게 한다
        if (_book is not null && info.Book.BookKey == _book.BookKey && info.Phase == TranslationPhase.Failed
            && (info.Reason == TranslationStopReason.CouldNotDetectLanguage || (info.Reason == TranslationStopReason.UnsupportedPair && info.Source == info.Target))
            && _languageProblemShown.Add((info.Book.BookKey, info.Target)))
        {
            _ui.BeginInvoke(() => { if (_panelPopup?.IsOpen != true && _host?.OwnerWindow.IsActive == true) ShowPanel(); });
        }
        else if (info.Phase is not TranslationPhase.Failed)
        {
            _languageProblemShown.Remove((info.Book.BookKey, info.Target));
        }

        if (_book is not null && info.Book.BookKey == _book.BookKey && info.Source is { } src && _source != src)
        {
            _source = src;
            State.UpdateSeries(_book.SeriesKey, s => { s.TranslationSource = src; s.UpdatedAt = DateTimeOffset.UtcNow; });
            if (info.Phase == TranslationPhase.Completed) ReloadBookState();
        }
        QueueStateChanged();
    }

    private bool _wasEnabled;

    private void OnSettingsChanged()
    {
        var enabled = IsEnabled;
        if (enabled != _wasEnabled)
        {
            _wasEnabled = enabled;
            if (!enabled) { Service.Scheduler.StopAll(); CloseTransientUi(); _editing = false; }
            OnBookChanged();
        }
        _host?.InvalidateOverlay();
        QueueStateChanged();
    }

    /// <summary>StateChanged를 UI 스레드에서 한 번으로 모아 보낸다.</summary>
    private void QueueStateChanged()
    {
        if (_stateQueued) return;
        _stateQueued = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _stateQueued = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            RefreshProgressPills();
            RefreshOpenPanel();
        });
    }

    // ───────────────────────── 툴바/명령 ─────────────────────────

    public IReadOnlySet<int> TranslatedPageIndices => IsEnabled && _visible is not null ? _translated : EmptySet;
    private static readonly IReadOnlySet<int> EmptySet = new HashSet<int>();

    public string VisibleLanguageLabel => ShowingTranslation && _visible is not null ? Loc.LanguageName(_visible) : Loc.T("Original");

    public bool HasResultsOrJob => _book is not null && TranslationLanguages.All.Any(HasResults);

    public void OnToolbarButtonClicked()
    {
        if (!IsEnabled || _book is null) return;
        if (HasResultsOrJob) ToggleOriginal();
        else ShowPanel();
    }

    public bool CanToggleOriginal => IsEnabled && _book is not null && HasResultsOrJob;

    public void ToggleOriginal()
    {
        if (!CanToggleOriginal) return;
        if (_visible is null) { _visible = TranslationLanguages.All.FirstOrDefault(HasResults) ?? PreferredTarget(); State.UpdateSettings(s => s.TranslationOverlayVisible = true); }
        else State.UpdateSettings(s => s.TranslationOverlayVisible = !s.TranslationOverlayVisible);
        _pages.Clear();
        ReloadBookState();
        _host?.InvalidateOverlay();
        QueueStateChanged();
    }

    public bool CanRunOrPause => IsEnabled && _book is not null;

    public bool IsRunningCurrentBook =>
        _book is not null && Service.Scheduler.JobsForBook(_book.BookKey).Any(j => j.IsRunningOrQueued);

    public void RunOrPause()
    {
        if (!CanRunOrPause || _book is null || _bookTarget is null) return;
        var target = _visible ?? PreferredTarget();
        var job = Service.Scheduler.Get(_book.BookKey, target);
        if (job is { IsRunningOrQueued: true }) Service.Scheduler.Pause(_book.BookKey, target);
        else StartTranslation(target, switchVisible: true);
    }

    /// <summary>번역 시작(대상 언어). switchVisible이면 그 언어를 보이게 하고 시리즈에 기억한다.</summary>
    private void StartTranslation(string target, bool switchVisible)
    {
        if (_book is null || _bookTarget is null || _host is null) return;
        if (switchVisible)
        {
            SelectVisibleLanguage(target);
        }
        Service.Scheduler.Start(_bookTarget, target, _source, CurrentScope, _host.CurrentPageIndex);
        QueueStateChanged();
    }

    private void SelectVisibleLanguage(string? target)
    {
        if (_book is null) return;
        if (target is null)
        {
            State.UpdateSettings(s => s.TranslationOverlayVisible = false);
        }
        else
        {
            _visible = target;
            State.UpdateSeries(_book.SeriesKey, s => { s.TranslationTarget = target; s.UpdatedAt = DateTimeOffset.UtcNow; });
            State.UpdateSettings(s => s.TranslationOverlayVisible = true);
            Service.Scheduler.SetCurrent(_book.BookKey, target, _host?.CurrentPageIndex ?? 0);
        }
        _pages.Clear();
        ReloadBookState();
        _host?.InvalidateOverlay();
        QueueStateChanged();
    }

    public bool CanEdit => IsEnabled && _book is not null && _visible is not null && (_translated.Count > 0 || _pages.Count > 0);

    public bool IsEditing => _editing;

    public void ToggleEditMode()
    {
        if (_editing) { SetEditing(false); return; }
        if (!CanEdit) return;
        SetEditing(true);
    }

    private void SetEditing(bool on)
    {
        _editing = on;
        CloseTransientUi();
        _dragStart = null;
        _dragRect = null;
        ShowEditBar(on);
        _host?.InvalidateOverlay();
        if (!on) _host?.FocusReader();
        QueueStateChanged();
    }

    public bool HasHiddenBalloons => _book is not null && Service.Edits.ForBook(_book.BookKey).Any(e => e.Bubbles.Any(b => b.IsHidden));

    public void ShowHiddenBalloons()
    {
        if (_book is null) return;
        foreach (var e in Service.Edits.ForBook(_book.BookKey))
            if (e.Bubbles.Any(b => b.IsHidden)) Service.Edits.Save(PageEditsEditor.UnhideAll(e));
        _host?.InvalidateOverlay();
    }

    public bool CanExport => IsEnabled && _book is not null;

    public void ShowExport()
    {
        if (!CanExport || _book is null || _bookTarget is null) return;
        ExportSheet.Show(_host?.OwnerWindow, this, [_bookTarget], _visible ?? PreferredTarget(), _source, CurrentScope);
    }

    public void ShowJobs() => JobsWindow.ShowSingle(_host?.OwnerWindow, this);

    public FrameworkElement CreateExperimentalSettingsView() => new ExperimentalSettingsView(this);

    public async Task ShutdownAsync()
    {
        CloseTransientUi();
        try { await Service.DisposeAsync().ConfigureAwait(true); } catch (Exception) { }
        _renderer.Dispose();
    }

    public void Dispose() => _renderer.Dispose();

    // ───────────────────────── 내부 접근(창들이 사용) ─────────────────────────

    internal IReaderHost? Host => _host;
    internal OpenedBook? Book => _book;
    internal BookTarget? CurrentBookTarget => _bookTarget;
    internal string? SourceLanguage => _source;
    internal string? VisibleLanguage => _visible;
    internal IStateStore StateStore => State;
    internal int CompletedPages(string language) => _completedSets.TryGetValue(language, out var s) ? s.Count : 0;
    internal TranslationScope Scope => CurrentScope;
    internal string Preferred => PreferredTarget();

    /// <summary>
    /// Windows에서 OCR 언어를 새로 설치했을 수 있으니 엔진 목록을 다시 읽는다(패널을 열 때·"다시 확인").
    /// </summary>
    internal void RecheckTextRecognition()
    {
        (Service.Providers.Recognizer as WindowsOcrRecognizer)?.Refresh();
        RequestStateRefresh();
    }

    internal void SetBookLanguage(string? language)
    {
        if (_book is null) return;
        _source = language;
        State.UpdateSeries(_book.SeriesKey, s => { s.TranslationSource = language; s.UpdatedAt = DateTimeOffset.UtcNow; });
        // 원어가 바뀌면 진행 중 작업을 새 원어로 다시 시작
        foreach (var job in Service.Scheduler.JobsForBook(_book.BookKey))
        {
            Service.Scheduler.Stop(_book.BookKey, job.Target);
            if (language is not null && job.Target != language && _bookTarget is not null)
                Service.Scheduler.Start(_bookTarget, job.Target, language, CurrentScope, _host?.CurrentPageIndex ?? 0);
        }
        _pages.Clear();
        ReloadBookState();
    }

    internal void SetAutoTranslate(bool on)
    {
        if (_book is null) return;
        State.UpdateSeries(_book.SeriesKey, s => { s.AutoTranslate = on; s.UpdatedAt = DateTimeOffset.UtcNow; });
        if (on) MaybeAutoTranslate();
        QueueStateChanged();
    }

    internal void StartLanguage(string target, bool switchVisible) => StartTranslation(target, switchVisible);

    internal void PauseLanguage(string target) { if (_book is not null) Service.Scheduler.Pause(_book.BookKey, target); }

    internal void StopLanguage(string target) { if (_book is not null) Service.Scheduler.Stop(_book.BookKey, target); }

    internal void ChooseView(string? target) => SelectVisibleLanguage(target);

    /// <summary>같은 제목 stem의 권(일반 권만; 중첩 권은 열려 있을 때만 보장).</summary>
    internal (List<BookTarget> Queue, List<string> SkippedNested) SeriesVolumes(bool filterByStem)
    {
        var queue = new List<BookTarget>();
        var skipped = new List<string>();
        if (_book is null) return (queue, skipped);
        var stem = SeriesStem.Compute(_book.Title);
        foreach (var v in _book.Volumes)
        {
            if (filterByStem && SeriesStem.Compute(v.DisplayName) != stem) continue;
            var isCurrent = v.Path == _book.Source.Path && v.InnerPath == _book.Book.InnerPath || v.DisplayName == _book.Title && v.InnerPath == _book.Book.InnerPath;
            if (isCurrent) { queue.Insert(0, _bookTarget!); continue; }
            if (v.IsNested) { skipped.Add(v.DisplayName); continue; }
            var source = State.GetOrCreateSource(v.Path, v.IsArchive ? SourceKind.Archive : SourceKind.Folder, System.IO.Path.GetFileNameWithoutExtension(v.Path));
            queue.Add(new BookTarget(BookKeys.Make(source.Id, v.InnerPath), v.Path, v.InnerPath, v.DisplayName, _book.SeriesKey, v.IsNested));
        }
        if (queue.Count == 0 && _bookTarget is not null) queue.Add(_bookTarget);
        return (queue, skipped);
    }

    internal void TranslateSeries()
    {
        var (queue, skipped) = SeriesVolumes(filterByStem: true);
        var target = _visible ?? PreferredTarget();
        SelectVisibleLanguage(target);
        foreach (var book in queue)
            Service.Scheduler.Start(book, target, _source, CurrentScope, book.BookKey == _book?.BookKey ? _host?.CurrentPageIndex ?? 0 : 0);
        if (skipped.Count > 0)
            _host?.ShowNotice(Loc.T("Skipped %lld volumes that are archives inside archives — open each one to translate it: %@", skipped.Count, string.Join(", ", skipped)));
    }

    internal void RetranslateBook()
    {
        if (_book is null || _bookTarget is null) return;
        var target = _visible ?? PreferredTarget();
        Service.Scheduler.Retranslate(_bookTarget, target, _source, CurrentScope, null, _host?.CurrentPageIndex ?? 0);
        _pages.Clear();
        _translated.Clear();
        _completedSets.Remove(target);
        _host?.InvalidateOverlay();
    }

    internal void RetranslatePage(int pageIndex)
    {
        if (_book is null || _bookTarget is null) return;
        var target = _visible ?? PreferredTarget();
        if (_visible is null) SelectVisibleLanguage(target);
        Service.Scheduler.Retranslate(_bookTarget, target, _source, CurrentScope, [pageIndex], pageIndex);
        _pages.Remove(pageIndex);
        _translated.Remove(pageIndex);
        _host?.InvalidateOverlay();
    }

    internal void RequestStateRefresh() => QueueStateChanged();

    internal void DismissTip() => State.UpdateSettings(s => s.TranslationBalloonTipDismissed = true);

    internal static string GestureHint(string commandId) => CommandRegistry.GestureText(commandId) ?? string.Empty;

    private void RebuildOpenUi()
    {
        RefreshOpenPanel(force: true);
        RefreshProgressPills();
        _editBar?.Refresh();
    }

    private void CloseTransientUi()
    {
        ClosePopover();
        _contextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
    }
}
