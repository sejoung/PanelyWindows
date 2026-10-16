using System.Diagnostics;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.Core.Translation;

/// <summary>
/// 번역 작업 스케줄러(06 작업 스케줄러와 시리즈).
/// 앱 전체 활성 작업 1개. 현재 화면의 책/대상 언어 우선, 그다음 대기순. 사용자 pause는 queued와 구별해 자동 재개하지 않는다.
/// 중첩 캐시 책은 떠나면 일시정지하고 돌아오면 재개한다. 쪽 사이마다 우선순위를 다시 평가한다.
/// 이벤트는 worker 스레드에서 발생한다(UI는 dispatcher로 옮겨 처리).
/// </summary>
public sealed class TranslationScheduler : IAsyncDisposable
{
    private readonly TranslationProviders _providers;
    private readonly TranslationCache _cache;
    private readonly IPagePixelSource _pixels;
    private readonly IBookAccess _books;
    private readonly TranslationOptions _options;
    private readonly IDiagnostics? _diagnostics;
    private readonly PageAnalyzer _analyzer;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private long _seq;
    private string? _currentBook;
    private string? _currentTarget;
    private int _currentPage;
    private Job? _active;

    public TranslationScheduler(TranslationProviders providers, TranslationCache cache, IPagePixelSource pixels, IBookAccess books,
        TranslationOptions? options = null, IDiagnostics? diagnostics = null)
    {
        _providers = providers;
        _cache = cache;
        _pixels = pixels;
        _books = books;
        _options = options ?? new TranslationOptions();
        _diagnostics = diagnostics;
        _analyzer = new PageAnalyzer(providers.Recognizer, providers.Detector);
        _cache.Cleared += (_, _) => StopAll();
        _loop = Task.Run(RunLoopAsync);
    }

    public event EventHandler<TranslationJobInfo>? JobChanged;
    public event EventHandler<string>? JobRemoved;
    public event EventHandler<TranslationPageUpdate>? PageUpdated;

    public TranslationProviders Providers => _providers;
    public TranslationCache Cache => _cache;
    public PageAnalyzer Analyzer => _analyzer;

    public static string JobKey(string bookKey, string target) => $"{bookKey}\u001f{target}";

    /// <summary>캐시 identity의 provider/model revision.</summary>
    public string ProviderRevision(TranslationMode mode) =>
        $"{_providers.Translator.Revision(mode)}+det:{_providers.Detector.Revision}+ocr:{_providers.Recognizer.Name}";

    public TranslationCacheKey CacheKey(string pageId, string source, string target, TranslationScope scope, TranslationMode mode)
        => new(pageId, source, target, scope, mode, PageAnalyzer.AnalysisVersion, ProviderRevision(mode));

    // ───────────────────────── 조회 ─────────────────────────

    public IReadOnlyList<TranslationJobInfo> Jobs
    {
        get { lock (_gate) return _jobs.Values.OrderBy(j => j.Seq).Select(Snapshot).ToList(); }
    }

    public TranslationJobInfo? Get(string bookKey, string target)
    {
        lock (_gate) return _jobs.TryGetValue(JobKey(bookKey, target), out var j) ? Snapshot(j) : null;
    }

    public IReadOnlyList<TranslationJobInfo> JobsForBook(string bookKey)
    {
        lock (_gate) return _jobs.Values.Where(j => j.Book.BookKey == bookKey).Select(Snapshot).ToList();
    }

    public TranslatedPage? LoadCached(string pageId, string source, string target, TranslationScope scope, TranslationMode mode)
        => _cache.TryLoad(CacheKey(pageId, source, target, scope, mode));

    /// <summary>완료 캐시가 있는 쪽 index.</summary>
    public HashSet<int> CachedPages(IReadOnlyList<PageEntry> pages, string source, string target, TranslationScope scope, TranslationMode mode)
    {
        var set = new HashSet<int>();
        for (var i = 0; i < pages.Count; i++)
            if (_cache.Contains(CacheKey(pages[i].PageId, source, target, scope, mode))) set.Add(i);
        return set;
    }

    // ───────────────────────── 제어 ─────────────────────────

    /// <summary>번역 시작/재개. 이미 있으면 사용자 pause를 풀고 대기열에 넣는다.</summary>
    public TranslationJobInfo Start(BookTarget book, string target, string? source, TranslationScope scope, int focusPage = 0, IEnumerable<int>? onlyPages = null)
    {
        TranslationJobInfo info;
        lock (_gate)
        {
            var key = JobKey(book.BookKey, target);
            if (!_jobs.TryGetValue(key, out var job) || job.Scope != scope || job.Mode != _options.Mode())
            {
                if (job is not null) CancelJob(job);
                job = new Job(book, target, _options.Mode(), scope) { Seq = ++_seq, Enqueued = DateTimeOffset.UtcNow };
                _jobs[key] = job;
            }
            if (source is not null && job.Source != source)
            {
                job.Source = source;
                job.Completed.Clear();
                job.Plan = null;
                job.StripBubbles.Clear();
                job.CompletedBands.Clear();
            }
            job.UserPaused = false;
            job.Attempted.Clear();
            job.AttemptedBands.Clear();
            job.FocusPage = Math.Max(0, focusPage);
            if (onlyPages is not null) { job.Only ??= []; job.Only.UnionWith(onlyPages); }
            else job.Only = null;
            if (job.Phase is TranslationPhase.Completed or TranslationPhase.Failed or TranslationPhase.Paused or TranslationPhase.Idle)
            {
                job.Phase = TranslationPhase.Queued;
                job.Reason = TranslationStopReason.None;
                job.ReasonMessage = null;
            }
            job.AutoPaused = book.IsNestedCacheBook && _currentBook != book.BookKey;
            if (job.AutoPaused) job.Phase = TranslationPhase.Paused;
            info = Snapshot(job);
        }
        Raise(info);
        _signal.Release();
        return info;
    }

    /// <summary>쪽 재번역(현재 방식으로 다시 읽기). 사용자 수정은 유지된다.</summary>
    public TranslationJobInfo Retranslate(BookTarget book, string target, string? source, TranslationScope scope, IReadOnlyCollection<int>? pages, int focusPage)
    {
        bool existed;
        lock (_gate) existed = _jobs.ContainsKey(JobKey(book.BookKey, target));
        var info = Start(book, target, source, scope, focusPage, existed || pages is null ? null : pages);
        lock (_gate)
        {
            if (_jobs.TryGetValue(JobKey(book.BookKey, target), out var job))
            {
                job.ForceAll = pages is null;
                if (pages is not null) { job.Forced.UnionWith(pages); job.Completed.ExceptWith(pages); }
                else job.Completed.Clear();
                job.Plan = null;
                job.StripBubbles.Clear();
                job.CompletedBands.Clear();
                info = Snapshot(job);
            }
        }
        Raise(info);
        _signal.Release();
        return info;
    }

    /// <summary>사용자 일시정지(자동 재개하지 않음).</summary>
    public void Pause(string bookKey, string target)
    {
        TranslationJobInfo? info = null;
        lock (_gate)
        {
            if (_jobs.TryGetValue(JobKey(bookKey, target), out var job) && job.Phase != TranslationPhase.Completed)
            {
                job.UserPaused = true;
                job.Phase = TranslationPhase.Paused;
                job.Cts?.Cancel();
                info = Snapshot(job);
            }
        }
        if (info is not null) Raise(info);
    }

    /// <summary>작업 중지·제거. 완료 캐시는 남는다.</summary>
    public void Stop(string bookKey, string target)
    {
        Job? removed;
        lock (_gate)
        {
            if (!_jobs.Remove(JobKey(bookKey, target), out removed)) return;
            CancelJob(removed);
        }
        JobRemoved?.Invoke(this, JobKey(bookKey, target));
    }

    /// <summary>실패/완료 항목 닫기(Dismiss).</summary>
    public void Dismiss(string bookKey, string target) => Stop(bookKey, target);

    public void StopAll()
    {
        List<string> keys;
        lock (_gate)
        {
            keys = _jobs.Keys.ToList();
            foreach (var j in _jobs.Values) CancelJob(j);
            _jobs.Clear();
        }
        foreach (var k in keys) JobRemoved?.Invoke(this, k);
    }

    /// <summary>
    /// 현재 화면의 책/보이는(또는 만드는) 대상 언어/쪽. 우선순위와 중첩 책 일시정지를 갱신한다.
    /// </summary>
    public void SetCurrent(string? bookKey, string? target, int currentPage)
    {
        var changed = new List<TranslationJobInfo>();
        lock (_gate)
        {
            _currentBook = bookKey;
            _currentTarget = target;
            _currentPage = currentPage;
            foreach (var job in _jobs.Values)
            {
                var isCurrent = job.Book.BookKey == bookKey;
                if (isCurrent && job.FocusPage != currentPage) job.FocusPage = currentPage;
                if (!job.Book.IsNestedCacheBook) { if (isCurrent != job.WasCurrent) { job.WasCurrent = isCurrent; changed.Add(Snapshot(job)); } continue; }
                if (!isCurrent && !job.AutoPaused && job.Phase is not (TranslationPhase.Completed or TranslationPhase.Failed))
                {
                    job.AutoPaused = true;
                    if (!job.UserPaused) job.Phase = TranslationPhase.Paused;
                    job.Cts?.Cancel();
                    changed.Add(Snapshot(job));
                }
                else if (isCurrent && job.AutoPaused)
                {
                    job.AutoPaused = false;
                    if (!job.UserPaused) job.Phase = TranslationPhase.Queued;
                    changed.Add(Snapshot(job));
                }
                job.WasCurrent = isCurrent;
            }
        }
        foreach (var c in changed) Raise(c);
        _signal.Release();
    }

    /// <summary>원문 수정·추가 풍선의 짧은 번역(작업 큐 밖, 번역기 직렬화는 공급자가 담당).</summary>
    public async Task<IReadOnlyDictionary<string, string>> TranslateTextsAsync(IReadOnlyList<TranslationRequestItem> items, string source, string target, CancellationToken cancellationToken)
    {
        var mode = _options.Mode();
        var cap = _providers.Translator.GetCapability(source, target, mode);
        if (!cap.IsReady) throw new InvalidOperationException(cap.Reason);
        var results = await _providers.Translator.TranslateAsync(items, source, target, mode, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, string>();
        var sourceById = items.ToDictionary(i => i.Id, i => i.Text);
        foreach (var r in results)
            if (sourceById.TryGetValue(r.Id, out var src) && IsValidTranslation(src, r.Text, target)) map[r.Id] = r.Text!.Trim();
        return map;
    }

    /// <summary>빈 결과·원문 그대로인 다른 언어 결과·오류는 성공으로 취급하지 않는다.</summary>
    public static bool IsValidTranslation(string source, string? result, string target)
    {
        if (string.IsNullOrWhiteSpace(result)) return false;
        if (result.Trim() == source.Trim() && !LanguageDetector.LooksLikeLanguage(result, target)) return false;
        return true;
    }

    // ───────────────────────── worker ─────────────────────────

    private async Task RunLoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            Job? job;
            CancellationTokenSource cts;
            lock (_gate)
            {
                job = PickNext();
                if (job is not null)
                {
                    _active = job;
                    job.Cts = cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                }
                else cts = null!;
            }
            if (job is null)
            {
                try { await _signal.WaitAsync(token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await RunJobAsync(job, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // pause/stop/shutdown: 상태는 요청한 쪽에서 이미 정했다
            }
            catch (Exception ex)
            {
                // 원문/번역 본문은 남기지 않고 예외 종류와 축약된 스택만 기록한다.
                _diagnostics?.Log(LogLevel.Error, LogCategory.Translation,
                    $"Translation job failed: {ex.GetType().Name}{Environment.NewLine}{Panely.Core.Diagnostics.PathRedactor.Redact(ex.StackTrace)}");
                Update(job, j => { j.Phase = TranslationPhase.Failed; j.Reason = TranslationStopReason.Failed; j.ReasonMessage = ex.Message; });
            }
            finally
            {
                lock (_gate)
                {
                    if (_active == job) _active = null;
                    job.Cts = null;
                    if (!_jobs.ContainsValue(job) || job.Phase is TranslationPhase.Completed or TranslationPhase.Failed || job.UserPaused || job.AutoPaused)
                        ReleaseHandle(job);
                }
                cts.Dispose();
            }
        }
    }

    private Job? PickNext()
    {
        bool Runnable(Job j) => !j.UserPaused && !j.AutoPaused && j.Phase is TranslationPhase.Queued or TranslationPhase.Translating or TranslationPhase.Preparing or TranslationPhase.DetectingLanguage;
        var candidates = _jobs.Values.Where(Runnable).ToList();
        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(j => j.Book.BookKey == _currentBook && j.Target == _currentTarget)
            .ThenByDescending(j => j.Book.BookKey == _currentBook)
            .ThenBy(j => j.Seq)
            .First();
    }

    private bool ShouldYield(Job job)
    {
        lock (_gate)
        {
            var next = PickNext();
            return next is not null && next != job;
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken ct)
    {
        if (job.Handle is null)
        {
            Update(job, j => j.Phase = TranslationPhase.Preparing);
            var handle = await _books.OpenAsync(job.Book, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_jobs.ContainsValue(job)) { handle.Dispose(); return; }
                job.Handle = handle;
                job.Total = job.Only?.Count ?? handle.Pages.Count;
            }
        }
        var book = job.Handle!;

        if (job.Source is null)
        {
            Update(job, j => j.Phase = TranslationPhase.DetectingLanguage);
            var (detected, readable) = await DetectSourceAsync(book, job.FocusPage, ct).ConfigureAwait(false);
            if (detected is null)
            {
                Update(job, j =>
                {
                    j.Phase = TranslationPhase.Failed;
                    j.Reason = TranslationStopReason.CouldNotDetectLanguage;
                    j.ReasonMessage = DetectionFailureMessage(readable);
                });
                return;
            }
            Update(job, j => j.Source = detected);
        }

        var source = job.Source!;
        if (source == job.Target)
        {
            // 원문이 번역할 언어와 같다: 감지가 틀렸을 수도 있으니 원어를 확인하게 안내한다
            Update(job, j =>
            {
                j.Phase = TranslationPhase.Failed;
                j.Reason = TranslationStopReason.UnsupportedPair;
                j.ReasonMessage = Loc.T("This book's language is set to %@, the same language it would be translated into. If the book is in another language, choose it under Book's language at the top of the translation panel; otherwise translate into another language.", Loc.LanguageName(source));
            });
            return;
        }
        var capability = _providers.Packs.GetCapability(source, job.Target, job.Mode);
        if (!capability.IsReady)
        {
            Update(job, j =>
            {
                if (capability.Status is CapabilityStatus.Supported or CapabilityStatus.Downloading)
                {
                    j.Phase = TranslationPhase.Paused;
                    j.Reason = TranslationStopReason.NeedsLanguageDownload;
                }
                else
                {
                    j.Phase = TranslationPhase.Failed;
                    j.Reason = source == j.Target || M2M100Translator.ModelLanguage(source) == M2M100Translator.ModelLanguage(j.Target)
                        ? TranslationStopReason.UnsupportedPair : TranslationStopReason.Failed;
                }
                j.ReasonMessage = capability.Reason;
            });
            return;
        }

        // 이미 완료된 캐시를 먼저 반영
        var revision = ProviderRevision(job.Mode);
        if (job.Completed.Count == 0)
        {
            var cached = CachedPages(book.Pages, source, job.Target, job.Scope, job.Mode);
            if (!job.ForceAll) cached.ExceptWith(job.Forced); else cached.Clear();
            Update(job, j => j.Completed.UnionWith(cached));
        }

        job.FirstResult = false;
        Update(job, j => j.Phase = TranslationPhase.Preparing);
        if (job.Scope == TranslationScope.Strip) await RunStripAsync(job, book, source, ct).ConfigureAwait(false);
        else await RunPagesAsync(job, book, source, ct).ConfigureAwait(false);
        _ = revision;
    }

    private IEnumerable<int> RemainingPages(Job job, int count)
    {
        foreach (var p in TranslationOrder.Pages(count, job.FocusPage))
        {
            if (job.Only is not null && !job.Only.Contains(p)) continue;
            if (job.Completed.Contains(p) || job.Attempted.Contains(p)) continue;
            yield return p;
        }
    }

    private async Task RunPagesAsync(Job job, OpenedBook book, string source, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int page;
            lock (_gate)
            {
                var next = RemainingPages(job, book.Pages.Count).Cast<int?>().FirstOrDefault();
                if (next is null) break;
                page = next.Value;
            }
            var sw = Stopwatch.StartNew();
            await TranslatePageAsync(job, book, page, source, ct).ConfigureAwait(false);
            sw.Stop();
            Update(job, j =>
            {
                j.Attempted.Add(page);
                j.Processed++;
                j.ProcessingTime += sw.Elapsed;
            });
            if (ShouldYield(job))
            {
                Update(job, j => j.Phase = TranslationPhase.Queued);
                return;
            }
        }
        FinishJob(job, book.Pages.Count);
    }

    private void FinishJob(Job job, int pageCount)
    {
        Update(job, j =>
        {
            var all = j.Only ?? Enumerable.Range(0, pageCount).ToHashSet();
            var missing = all.Count(p => !j.Completed.Contains(p));
            j.Forced.Clear();
            j.ForceAll = false;
            if (missing == 0)
            {
                j.Phase = TranslationPhase.Completed;
                j.Reason = TranslationStopReason.None;
                j.ReasonMessage = null;
            }
            else
            {
                j.Phase = TranslationPhase.Failed;
                j.Reason = TranslationStopReason.Failed;
                j.ReasonMessage = Loc.T("Translation was stopped before the book was finished.");
            }
        });
    }

    private async Task TranslatePageAsync(Job job, OpenedBook book, int pageIndex, string source, CancellationToken ct)
    {
        var entry = book.Pages[pageIndex];
        var key = CacheKey(entry.PageId, source, job.Target, TranslationScope.Page, job.Mode);
        if (!job.ForceAll && !job.Forced.Contains(pageIndex) && _cache.TryLoad(key) is { } cached)
        {
            Update(job, j => j.Completed.Add(pageIndex));
            PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, pageIndex, cached));
            return;
        }
        var generation = _cache.Generation;
        var pixels = await _pixels.GetPixelsAsync(book, entry, ct).ConfigureAwait(false);
        var bubbles = await _analyzer.AnalyzeAsync(pixels, source, ct).ConfigureAwait(false);
        var page = new TranslatedPage
        {
            PageId = entry.PageId,
            SourceLanguage = source,
            TargetLanguage = job.Target,
            Scope = TranslationScope.Page,
            Mode = job.Mode,
            ProviderRevision = key.ProviderRevision,
            AnalysisVersion = PageAnalyzer.AnalysisVersion,
            ImageWidth = pixels.Width,
            ImageHeight = pixels.Height,
            IsComplete = false,
            Bubbles = bubbles,
        };
        PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, pageIndex, page));
        page = await TranslateBubblesAsync(job, book.BookKey, pageIndex, page, source, ct).ConfigureAwait(false);
        if (page.IsComplete && _cache.Store(key, page, generation))
        {
            Update(job, j => { j.Completed.Add(pageIndex); j.Forced.Remove(pageIndex); });
        }
        PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, pageIndex, page));
    }

    /// <summary>풍선을 묶음 단위로 번역해 도착하는 대로 갱신한다. 결과는 ID로 맞춘다.</summary>
    private async Task<TranslatedPage> TranslateBubblesAsync(Job job, string bookKey, int pageIndex, TranslatedPage page, string source, CancellationToken ct)
    {
        var bubbles = page.Bubbles.ToList();
        // 대기 목록을 먼저 고정한다. 루프 안에서 bubbles[index]를 바꾸므로 지연 열거하면 List 버전이 바뀌어 예외가 난다.
        var pending = bubbles.Where(b => b.Translation is null).ToList();
        foreach (var chunk in pending.Chunk(Math.Max(1, _options.TranslateChunk)))
        {
            ct.ThrowIfCancellationRequested();
            var request = chunk.Select(b => new TranslationRequestItem(b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), b.Source)).ToList();
            var results = await _providers.Translator.TranslateAsync(request, source, job.Target, job.Mode, ct).ConfigureAwait(false);
            foreach (var r in results)
            {
                if (!int.TryParse(r.Id, out var id)) continue;
                var index = bubbles.FindIndex(b => b.Id == id);
                if (index < 0 || !IsValidTranslation(bubbles[index].Source, r.Text, job.Target)) continue;
                bubbles[index] = bubbles[index] with { Translation = r.Text!.Trim() };
            }
            if (!job.FirstResult)
            {
                job.FirstResult = true;
                Update(job, j => j.Phase = TranslationPhase.Translating);
            }
            page = page with { Bubbles = bubbles.ToList() };
            PageUpdated?.Invoke(this, new TranslationPageUpdate(bookKey, job.Target, pageIndex, page));
        }
        if (!job.FirstResult)
        {
            job.FirstResult = true;
            Update(job, j => j.Phase = TranslationPhase.Translating);
        }

        bool? speechChecked = null;
        if (_options.MatchSpeechLevel() && IsSpeechPair(source, job.Target) && _providers.SpeechLevel.GetCapability(source, job.Target).IsReady)
        {
            for (var i = 0; i < bubbles.Count; i++)
            {
                if (bubbles[i].Translation is not { } tr) continue;
                var corrected = await _providers.SpeechLevel.CorrectAsync(bubbles[i].Source, tr, source, job.Target, ct).ConfigureAwait(false);
                if (corrected.Outcome == SpeechLevelOutcome.Candidate && IsValidTranslation(bubbles[i].Source, corrected.Text, job.Target))
                    bubbles[i] = bubbles[i] with { Translation = corrected.Text };
            }
            speechChecked = true;
        }
        return page with { Bubbles = bubbles, IsComplete = bubbles.All(b => b.Translation is not null), SpeechLevelChecked = speechChecked };
    }

    public static bool IsSpeechPair(string source, string target) =>
        (source, target) is (TranslationLanguages.Japanese, TranslationLanguages.Korean) or (TranslationLanguages.Korean, TranslationLanguages.Japanese);

    private async Task RunStripAsync(Job job, OpenedBook book, string source, CancellationToken ct)
    {
        if (job.Plan is null)
        {
            var sizes = new List<SizeD>(book.Pages.Count);
            foreach (var p in book.Pages) sizes.Add(await _pixels.GetSizeAsync(book, p, ct).ConfigureAwait(false));
            job.Plan = StripPlan.Create(sizes);
        }
        var plan = job.Plan;
        var revision = ProviderRevision(job.Mode);

        // 이미 완료된 쪽의 캐시를 게시
        foreach (var p in job.Completed.ToList())
        {
            if (_cache.TryLoad(CacheKey(book.Pages[p].PageId, source, job.Target, TranslationScope.Strip, job.Mode)) is { } c)
                PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, p, c));
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            StripBand? band;
            lock (_gate)
            {
                var focusBand = plan.Bands.FirstOrDefault(b => b.PageIndices.Contains(Math.Clamp(job.FocusPage, 0, Math.Max(0, book.Pages.Count - 1))))?.Index ?? 0;
                band = plan.Bands.Skip(focusBand).Concat(plan.Bands.Take(focusBand))
                    .FirstOrDefault(b => !job.CompletedBands.Contains(b.Index) && !job.AttemptedBands.Contains(b.Index)
                        && b.PageIndices.Any(p => !job.Completed.Contains(p) && (job.Only is null || job.Only.Contains(p))));
            }
            if (band is null) break;
            var sw = Stopwatch.StartNew();
            var generation = _cache.Generation;
            await TranslateBandAsync(job, book, plan, band, source, revision, generation, ct).ConfigureAwait(false);
            sw.Stop();
            Update(job, j => { j.AttemptedBands.Add(band.Index); j.Processed += Math.Max(1, band.PageIndices.Count); j.ProcessingTime += sw.Elapsed; });
            if (ShouldYield(job))
            {
                Update(job, j => j.Phase = TranslationPhase.Queued);
                return;
            }
        }
        FinishJob(job, book.Pages.Count);
    }

    private async Task TranslateBandAsync(Job job, OpenedBook book, StripPlan plan, StripBand band, string source, string revision, int generation, CancellationToken ct)
    {
        // 읽기 화면과 같은 maxWidth·수평 중앙·연속 y 공간으로 band를 합성한다
        var canvas = PagePixels.Blank(plan.Width, band.Height);
        foreach (var p in plan.PagesIntersecting(band))
        {
            var pixels = await GetPixelsCachedAsync(job, book, p, ct).ConfigureAwait(false);
            var rect = plan.PageRects[p];
            canvas.Blit(pixels, (int)Math.Round(rect.X), (int)Math.Round(rect.Y - band.Top));
        }
        var bubbles = await _analyzer.AnalyzeAsync(canvas, source, ct).ConfigureAwait(false);
        var touched = new HashSet<int>();
        foreach (var b in bubbles)
        {
            var centerY = band.Top + b.Rect.CenterY;
            if (!band.Owns(centerY)) continue; // 중심 Y를 소유하는 band에서 한 번만 채택
            var page = plan.PageAtY(centerY);
            var origin = plan.PageRects[page];
            var dx = -origin.X; var dy = band.Top - origin.Y;
            var local = b with
            {
                Rect = b.Rect.Offset(dx, dy),
                BalloonRect = b.BalloonRect?.Offset(dx, dy),
            };
            if (!job.StripBubbles.TryGetValue(page, out var list)) job.StripBubbles[page] = list = [];
            list.Add(local);
            touched.Add(page);
        }

        // 부분 결과 게시 후 band 풍선 번역
        foreach (var p in touched) PublishStripPage(job, book, p, source, revision, complete: false);
        foreach (var p in touched.Order())
        {
            var partial = BuildStripPage(job, book, p, source, revision, complete: false);
            var translated = await TranslateBubblesAsync(job, book.BookKey, p, partial, source, ct).ConfigureAwait(false);
            job.StripBubbles[p] = translated.Bubbles.ToList();
        }

        Update(job, j => j.CompletedBands.Add(band.Index));
        foreach (var p in plan.CompletedPages(job.CompletedBands))
        {
            if (job.Completed.Contains(p)) continue;
            var page = BuildStripPage(job, book, p, source, revision, complete: true);
            if (!page.IsComplete) { PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, p, page)); continue; }
            var key = CacheKey(book.Pages[p].PageId, source, job.Target, TranslationScope.Strip, job.Mode);
            if (_cache.Store(key, page, generation)) Update(job, j => { j.Completed.Add(p); j.Forced.Remove(p); });
            PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, p, page));
        }
    }

    private void PublishStripPage(Job job, OpenedBook book, int page, string source, string revision, bool complete)
        => PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, page, BuildStripPage(job, book, page, source, revision, complete)));

    private TranslatedPage BuildStripPage(Job job, OpenedBook book, int page, string source, string revision, bool complete)
    {
        var rect = job.Plan!.PageRects[page];
        var list = job.StripBubbles.TryGetValue(page, out var l) ? l : [];
        // 페이지 로컬 ID 재부여(위→아래, 같은 높이면 오른쪽 먼저) — 번역 중에는 기존 ID 유지
        var bubbles = complete
            ? list.OrderBy(b => b.Rect.Top).ThenByDescending(b => b.Rect.CenterX).Select((b, i) => b with { Id = i }).ToList()
            : list.ToList();
        return new TranslatedPage
        {
            PageId = book.Pages[page].PageId,
            SourceLanguage = source,
            TargetLanguage = job.Target,
            Scope = TranslationScope.Strip,
            Mode = job.Mode,
            ProviderRevision = revision,
            AnalysisVersion = PageAnalyzer.AnalysisVersion,
            ImageWidth = Math.Max(1, rect.Width),
            ImageHeight = Math.Max(1, rect.Height),
            IsComplete = complete && bubbles.All(b => b.Translation is not null),
            Bubbles = bubbles,
        };
    }

    private async Task<PagePixels> GetPixelsCachedAsync(Job job, OpenedBook book, int page, CancellationToken ct)
    {
        if (job.PixelCache.TryGetValue(page, out var cached)) return cached;
        var pixels = await _pixels.GetPixelsAsync(book, book.Pages[page], ct).ConfigureAwait(false);
        job.PixelCache[page] = pixels;
        job.PixelOrder.Enqueue(page);
        while (job.PixelOrder.Count > _options.PixelCacheSize && job.PixelOrder.TryDequeue(out var old)) job.PixelCache.Remove(old);
        return pixels;
    }

    /// <summary>감지할 때 읽어 보는 최대 쪽 수. 웹툰은 앞쪽 조각에 글자가 없는 경우가 많아 3쪽으로는 부족하다.</summary>
    public const int MaxDetectionPages = 15;

    /// <summary>
    /// 감지 실패 안내. Windows OCR은 설치된 언어만 읽을 수 있으므로, 읽을 수 있는 언어를 밝혀
    /// "일본어 책인데 일본어 글자 인식이 없음" 같은 실제 원인을 알 수 있게 한다.
    /// </summary>
    public static string DetectionFailureMessage(IReadOnlyCollection<string> readableLanguages)
    {
        if (readableLanguages.Count == 0)
            return Loc.T("No text recognition language is installed on this PC, so the book can't be read. Add a language with its optical character recognition feature in Windows Settings › Time & language › Language & region.");
        if (readableLanguages.Count < TranslationLanguages.All.Count)
        {
            var names = string.Join(", ", readableLanguages.Select(Loc.LanguageName));
            return Loc.T("Couldn't tell which language this book is in. This PC can only read %@ text — if the book is in another language such as Japanese or Chinese, install that language's text recognition in Windows. You can also choose the book's language at the top of the translation panel.", names);
        }
        return Loc.T("Couldn't tell which language this book is in. Choose the book's language at the top of the translation panel.");
    }

    /// <summary>
    /// 원문 언어 감지: 설치된 OCR 언어로 최대 <see cref="MaxDetectionPages"/>쪽을 읽어 자체 문자 수가 가장 많은 언어를 고른다.
    /// 근거가 충분하면(자체 문자 12개 이상) 일찍 멈춘다. Readable은 이 PC가 읽을 수 있는 언어(실패 안내용).
    /// </summary>
    private async Task<(string? Language, IReadOnlyList<string> Readable)> DetectSourceAsync(OpenedBook book, int focusPage, CancellationToken ct)
    {
        if (_providers.Recognizer is ILanguageIdentifier { CanIdentify: true } identifier)
            return (await IdentifyWithModelAsync(identifier, book, focusPage, ct).ConfigureAwait(false), TranslationLanguages.All);

        var candidates = TranslationLanguages.All.Where(l => _providers.Recognizer.GetCapability(l).IsReady).ToList();
        if (candidates.Count == 0) return (null, candidates);
        var pages = TranslationOrder.Pages(book.Pages.Count, focusPage).Take(MaxDetectionPages).ToList();
        var evidence = new Dictionary<string, DetectionEvidence>();
        var texts = new List<string>();
        foreach (var p in pages)
        {
            var pixels = await _pixels.GetPixelsAsync(book, book.Pages[p], ct).ConfigureAwait(false);
            foreach (var lang in candidates.Where(l => l != TranslationLanguages.ChineseTraditional || !candidates.Contains(TranslationLanguages.ChineseSimplified)))
            {
                var regions = await _providers.Recognizer.RecognizeAsync(pixels, lang, null, ct).ConfigureAwait(false);
                var e = evidence.GetValueOrDefault(lang);
                foreach (var r in regions) e = e.Add(DetectionEvidence.Of(r.Text, lang));
                evidence[lang] = e;
                texts.AddRange(regions.Select(r => r.Text));
            }
            if (PickDetectedLanguage(evidence) is { } sure && evidence[sure].Own >= DetectionEvidence.EarlyStopOwn) break;
        }
        var best = PickDetectedLanguage(evidence);
        if (best is TranslationLanguages.ChineseSimplified or TranslationLanguages.ChineseTraditional)
        {
            var refined = LanguageDetector.Detect(texts);
            if (refined is TranslationLanguages.ChineseSimplified or TranslationLanguages.ChineseTraditional && candidates.Contains(refined)) best = refined;
        }
        return (best, candidates);
    }

    /// <summary>
    /// 만화 OCR 모델로 감지: 탐지한 글자 상자(없으면 페이지 전체)를 다국어/한국어 모델로 읽어 근거를 모은다.
    /// 중국어로 보이면 간체/번체 전용 글자로 다시 나눈다.
    /// </summary>
    private async Task<string?> IdentifyWithModelAsync(ILanguageIdentifier identifier, OpenedBook book, int focusPage, CancellationToken ct)
    {
        var evidence = new Dictionary<string, DetectionEvidence>();
        foreach (var p in TranslationOrder.Pages(book.Pages.Count, focusPage).Take(MaxDetectionPages))
        {
            var pixels = await _pixels.GetPixelsAsync(book, book.Pages[p], ct).ConfigureAwait(false);
            IReadOnlyList<RectD>? boxes = null;
            if (_providers.Detector.Capability.IsReady)
                boxes = (await _providers.Detector.DetectAsync(pixels, ct).ConfigureAwait(false))?.Where(d => d.IsTextBox).Select(d => d.Rect).ToList();
            if (boxes is { Count: 0 }) continue; // 글자 상자가 없는 쪽(그림만 있는 웹툰 조각)은 건너뛴다
            foreach (var (lang, e) in await identifier.IdentifyAsync(pixels, boxes, ct).ConfigureAwait(false))
                evidence[lang] = evidence.GetValueOrDefault(lang).Add(e);
            if (PickDetectedLanguage(evidence) is { } sure && evidence[sure].Own >= DetectionEvidence.EarlyStopOwn) break;
        }
        return PickDetectedLanguage(evidence);
    }

    /// <summary>
    /// 언어별 근거 중 믿을 만한 것을 고른다. 다른 언어의 글자나 그림 무늬를 OCR이 억지로 읽은 잡음
    /// (예: 한국어 OCR이 건물 무늬를 "1닿11*들흁원『『"로 읽음)을 걸러내기 위해,
    /// 자체 문자가 <see cref="DetectionEvidence.MinOwn"/>개 이상이고 인식된 글자 중 자체 문자 비율이
    /// <see cref="DetectionEvidence.MinShare"/> 이상인 언어만 후보로 본다. 영어 OCR은 어떤 글자든 라틴으로 읽으려 하므로 순위 가중치를 낮춘다.
    /// </summary>
    public static string? PickDetectedLanguage(IReadOnlyDictionary<string, DetectionEvidence> evidence)
        => evidence
            .Where(kv => kv.Value.IsConvincing)
            .OrderByDescending(kv => kv.Key == TranslationLanguages.English ? kv.Value.Own / 3.0 : kv.Value.Own)
            .Select(kv => kv.Key)
            .FirstOrDefault();

    // ───────────────────────── 내부 상태 ─────────────────────────

    private void Update(Job job, Action<Job> change)
    {
        TranslationJobInfo info;
        lock (_gate)
        {
            change(job);
            if (!_jobs.ContainsValue(job)) return;
            info = Snapshot(job);
        }
        Raise(info);
    }

    private void Raise(TranslationJobInfo info) => JobChanged?.Invoke(this, info);

    private void CancelJob(Job job)
    {
        job.Cts?.Cancel();
        if (_active != job) ReleaseHandle(job);
    }

    private static void ReleaseHandle(Job job)
    {
        job.Handle?.Dispose();
        job.Handle = null;
        job.PixelCache.Clear();
        job.PixelOrder.Clear();
    }

    private TranslationJobInfo Snapshot(Job j)
    {
        var total = j.Only?.Count ?? j.Total;
        var completed = j.Only is null ? j.Completed.Count : j.Completed.Count(j.Only.Contains);
        TimeSpan? remaining = null;
        if (j.Processed >= 2 && total > completed)
            remaining = TimeSpan.FromTicks(j.ProcessingTime.Ticks / j.Processed * (total - completed));
        return new TranslationJobInfo
        {
            Book = j.Book,
            Target = j.Target,
            Source = j.Source,
            Mode = j.Mode,
            Scope = j.Scope,
            Phase = j.Phase,
            Reason = j.Reason,
            ReasonMessage = j.ReasonMessage,
            CompletedPages = completed,
            TotalPages = total,
            UserPaused = j.UserPaused,
            AutoPaused = j.AutoPaused,
            IsCurrentBook = j.Book.BookKey == _currentBook,
            Remaining = remaining,
            EnqueuedAt = j.Enqueued,
            IsActive = _active == j,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        StopAll();
        _signal.Release();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private sealed class Job(BookTarget book, string target, TranslationMode mode, TranslationScope scope)
    {
        public BookTarget Book { get; } = book;
        public string Target { get; } = target;
        public TranslationMode Mode { get; } = mode;
        public TranslationScope Scope { get; } = scope;
        public string? Source { get; set; }
        public TranslationPhase Phase { get; set; } = TranslationPhase.Queued;
        public TranslationStopReason Reason { get; set; }
        public string? ReasonMessage { get; set; }
        public HashSet<int> Completed { get; } = [];
        public HashSet<int> Attempted { get; } = [];
        public HashSet<int> Forced { get; } = [];
        public bool ForceAll { get; set; }
        public HashSet<int>? Only { get; set; }
        public int Total { get; set; }
        public bool UserPaused { get; set; }
        public bool AutoPaused { get; set; }
        public bool WasCurrent { get; set; }
        public int FocusPage { get; set; }
        public long Seq { get; init; }
        public DateTimeOffset Enqueued { get; init; }
        public OpenedBook? Handle { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public int Processed { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public bool FirstResult { get; set; }
        public StripPlan? Plan { get; set; }
        public Dictionary<int, List<TranslatedBubble>> StripBubbles { get; } = [];
        public HashSet<int> CompletedBands { get; } = [];
        public HashSet<int> AttemptedBands { get; } = [];
        public Dictionary<int, PagePixels> PixelCache { get; } = [];
        public Queue<int> PixelOrder { get; } = new();
    }
}
