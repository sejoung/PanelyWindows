using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
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
        // 언어팩을 받으면(번역 패널·설정 어디서든) 팩이 없어 멈춘 작업을 다시 돌린다. 재시작하지 않아도 번역이 이어진다.
        providers.Packs.PacksChanged += (_, _) => ResumeJobsWaitingForPacks();
        _loop = Task.Run(RunLoopAsync);
    }

    /// <summary>"언어팩 필요"로 멈춘 작업 중 이제 필요한 팩이 다 있는 것을 대기열에 다시 넣는다(사용자가 멈춘 작업은 그대로).</summary>
    private void ResumeJobsWaitingForPacks()
    {
        var changed = new List<TranslationJobInfo>();
        lock (_gate)
        {
            foreach (var job in _jobs.Values)
            {
                if (job.Phase != TranslationPhase.Paused || job.Reason != TranslationStopReason.NeedsLanguageDownload || job.UserPaused || job.AutoPaused) continue;
                var ready = job.Source is { } source ? _providers.Packs.GetCapability(source, job.Target).IsReady : DetectionWaitingForPacks() is null;
                if (!ready) continue;
                job.Phase = TranslationPhase.Queued;
                job.Reason = TranslationStopReason.None;
                job.ReasonMessage = null;
                changed.Add(Snapshot(job));
            }
        }
        if (changed.Count == 0) return;
        foreach (var c in changed) Raise(c);
        _signal.Release();
    }

    /// <summary>
    /// 원문 언어를 감지하려면 글자 인식 팩이 있어야 한다. 읽을 수 있는 언어가 하나도 없고 받을 수 있는 팩이 있으면 그 상태를 돌려준다
    /// (감지 실패가 아니라 "언어팩 필요"로 멈추게).
    /// </summary>
    private Capability? DetectionWaitingForPacks()
    {
        if (_providers.Recognizer is ILanguageIdentifier { CanIdentify: true }) return null;
        var capabilities = TranslationLanguages.All.Select(_providers.Recognizer.GetCapability).ToList();
        if (capabilities.Any(c => c.IsReady)) return null;
        return capabilities.FirstOrDefault(c => c.Status is CapabilityStatus.Supported or CapabilityStatus.Downloading);
    }

    public event EventHandler<TranslationJobInfo>? JobChanged;
    public event EventHandler<string>? JobRemoved;
    public event EventHandler<TranslationPageUpdate>? PageUpdated;

    public TranslationProviders Providers => _providers;
    public TranslationCache Cache => _cache;
    public PageAnalyzer Analyzer => _analyzer;

    public static string JobKey(string bookKey, string target) => $"{bookKey}\u001f{target}";

    /// <summary>캐시 identity의 provider/model revision.</summary>
    public string ProviderRevision =>
        $"{_providers.Translator.Revision}+det:{_providers.Detector.Revision}+ocr:{_providers.Recognizer.Name}";

    public TranslationCacheKey CacheKey(string pageId, string source, string target, TranslationScope scope)
        => new(pageId, source, target, scope, PageAnalyzer.AnalysisVersion, ProviderRevision);

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

    public TranslatedPage? LoadCached(string pageId, string source, string target, TranslationScope scope)
        => _cache.TryLoad(CacheKey(pageId, source, target, scope));

    /// <summary>완료 캐시가 있는 쪽 index.</summary>
    public HashSet<int> CachedPages(IReadOnlyList<PageEntry> pages, string source, string target, TranslationScope scope)
    {
        var set = new HashSet<int>();
        for (var i = 0; i < pages.Count; i++)
            if (_cache.Contains(CacheKey(pages[i].PageId, source, target, scope))) set.Add(i);
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
            if (!_jobs.TryGetValue(key, out var job) || job.Scope != scope)
            {
                if (job is not null) CancelJob(job);
                job = new Job(book, target, scope) { Seq = ++_seq, Enqueued = DateTimeOffset.UtcNow };
                _jobs[key] = job;
            }
            if (source is not null && job.Source != source)
            {
                job.Source = source;
                job.Completed.Clear();
                job.Plan = null;
                job.StripBubbles.Clear();
                job.CompletedBands.Clear();
                job.StripEpoch++;
                job.InFlight.Clear();
            }
            job.UserPaused = false;
            job.Attempted.Clear();
            job.AttemptedBands.Clear();
            job.FocusPage = Math.Max(0, focusPage);
            if (onlyPages is not null) { job.Only ??= []; job.Only.UnionWith(onlyPages); }
            else job.Only = null;
            if (job.Phase is TranslationPhase.Completed or TranslationPhase.Failed or TranslationPhase.Paused or TranslationPhase.Idle)
            {
                // 다시 시작하면 끝나지 못한 쪽을 실제로 다시 읽는다. band 상태는 작업 스레드가 다음 실행 시작 때 정리한다.
                job.ResetUnfinished = true;
                job.LastError = null;
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
                job.StripEpoch++;
                job.InFlight.Clear();
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
        var cap = _providers.Translator.GetCapability(source, target);
        if (!cap.IsReady) throw new InvalidOperationException(cap.Reason);
        var results = await _providers.Translator.TranslateAsync(items, [], source, target, cancellationToken).ConfigureAwait(false);
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
            if (DetectionWaitingForPacks() is not null)
            {
                Update(job, j =>
                {
                    j.Phase = TranslationPhase.Paused;
                    j.Reason = TranslationStopReason.NeedsLanguageDownload;
                    j.ReasonMessage = Loc.T("Download the language pack to read this book. Its language is detected after that.");
                });
                return;
            }
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
        var capability = _providers.Packs.GetCapability(source, job.Target);
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
                    j.Reason = source == j.Target ? TranslationStopReason.UnsupportedPair : TranslationStopReason.Failed;
                }
                j.ReasonMessage = capability.Reason;
            });
            return;
        }

        // 이미 완료된 캐시를 먼저 반영
        if (job.Completed.Count == 0)
        {
            var cached = CachedPages(book.Pages, source, job.Target, job.Scope);
            if (!job.ForceAll) cached.ExceptWith(job.Forced); else cached.Clear();
            Update(job, j => j.Completed.UnionWith(cached));
        }

        job.FirstResult = false;
        Update(job, j => j.Phase = TranslationPhase.Preparing);
        // 번역 모델 준비(GPU 첫 실행은 20초가량)를 첫 쪽 분석과 겹쳐 돌린다. 실패하면 첫 번역 때 다시 올리며 그 오류를 알린다.
        _ = _providers.Translator.PrepareAsync(ct).ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        if (job.Scope == TranslationScope.Strip) await RunStripAsync(job, book, source, ct).ConfigureAwait(false);
        else await RunPagesAsync(job, book, source, ct).ConfigureAwait(false);
    }

    private IEnumerable<int> RemainingPages(Job job, int count)
    {
        foreach (var p in TranslationOrder.Pages(count, job.FocusPage))
        {
            if (job.Only is not null && !job.Only.Contains(p)) continue;
            if (job.Completed.Contains(p) || job.Attempted.Contains(p) || job.InFlight.Contains(p)) continue;
            yield return p;
        }
    }

    /// <summary>
    /// 쪽 범위: 분석(탐지·OCR)이 쪽을 앞서 읽어 대기열에 넣고, 번역은 대기열에 쌓인 여러 쪽의 풍선을 묶어 처리한다.
    /// 두 단계가 겹쳐 돌고 번역기에 한 번에 여러 문장이 들어가므로, 쪽마다 차례로 분석→번역하던 때보다 빠르다.
    /// </summary>
    private async Task RunPagesAsync(Job job, OpenedBook book, string source, CancellationToken ct)
    {
        lock (_gate) job.InFlight.Clear();
        var yielded = await RunPipelineAsync<PageWork>(
            (writer, token) => AnalyzePagesAsync(job, book, source, writer, token),
            (reader, token) => TranslateQueuedAsync(job, reader, source, w => w.Slots,
                (works, pages) => { foreach (var w in works.Where(w => pages.Contains(w.Page))) PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, w.Page, BuildPage(w, false))); },
                (w, elapsed) => FinishPage(job, book, w, elapsed), token),
            ct).ConfigureAwait(false);
        if (yielded)
        {
            Update(job, j => j.Phase = TranslationPhase.Queued);
            return;
        }
        FinishJob(job, book.Pages.Count);
    }

    /// <returns>더 급한 작업에 자리를 내주려고 멈췄는지.</returns>
    private async Task<bool> AnalyzePagesAsync(Job job, OpenedBook book, string source, ChannelWriter<PageWork> writer, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int page;
            bool forced;
            lock (_gate)
            {
                var next = RemainingPages(job, book.Pages.Count).Cast<int?>().FirstOrDefault();
                if (next is null) return false;
                page = next.Value;
                job.InFlight.Add(page);
                forced = job.ForceAll || job.Forced.Contains(page);
            }
            var entry = book.Pages[page];
            var key = CacheKey(entry.PageId, source, job.Target, TranslationScope.Page);
            if (!forced && _cache.TryLoad(key) is { } cached)
            {
                Update(job, j => { j.Completed.Add(page); j.Attempted.Add(page); j.InFlight.Remove(page); j.Processed++; });
                PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, page, cached));
            }
            else
            {
                var generation = _cache.Generation;
                var pixels = await _pixels.GetPixelsAsync(book, entry, ct).ConfigureAwait(false);
                var bubbles = await _analyzer.AnalyzeAsync(pixels, source, ct).ConfigureAwait(false);
                var analyzed = new TranslatedPage
                {
                    PageId = entry.PageId,
                    SourceLanguage = source,
                    TargetLanguage = job.Target,
                    Scope = TranslationScope.Page,
                    ProviderRevision = key.ProviderRevision,
                    AnalysisVersion = PageAnalyzer.AnalysisVersion,
                    ImageWidth = pixels.Width,
                    ImageHeight = pixels.Height,
                    IsComplete = false,
                    Bubbles = bubbles,
                };
                PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, page, analyzed));
                await writer.WriteAsync(new PageWork(page, analyzed, key, generation, bubbles.Select(b => new Slot(page, -1, b)).ToList()), ct).ConfigureAwait(false);
            }
            if (ShouldYield(job)) return true;
        }
    }

    private TranslatedPage BuildPage(PageWork work, bool complete)
    {
        lock (_gate)
        {
            return work.Analyzed with
            {
                Bubbles = work.Slots.Select(s => s.Bubble).ToList(),
                IsComplete = complete && work.Slots.All(s => s.IsFinished),
            };
        }
    }

    private void FinishPage(Job job, OpenedBook book, PageWork work, TimeSpan elapsed)
    {
        var page = BuildPage(work, true);
        if (page.IsComplete && _cache.Store(work.Key, page, work.Generation))
            Update(job, j => { j.Completed.Add(work.Page); j.Forced.Remove(work.Page); });
        Update(job, j => { j.Attempted.Add(work.Page); j.InFlight.Remove(work.Page); j.Processed++; j.ProcessingTime += elapsed; });
        PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, work.Page, page));
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
                j.ReasonMessage = j.LastError ?? Loc.T("Translation was stopped before the book was finished.");
            }
        });
    }

    /// <summary>
    /// 분석(produce)과 번역(consume)을 함께 돌린다. 대기열은 <see cref="TranslationOptions.AnalysisLookahead"/>개까지 쌓인다.
    /// 한쪽이 실패하면 다른 쪽도 멈추고, 취소가 아닌 실제 오류를 알린다. 자리를 내줄 때는 이미 분석한 단위까지 번역을 마친다.
    /// </summary>
    /// <returns>produce가 자리를 내주려고 멈췄는지.</returns>
    private async Task<bool> RunPipelineAsync<T>(Func<ChannelWriter<T>, CancellationToken, Task<bool>> produce, Func<ChannelReader<T>, CancellationToken, Task> consume, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(Math.Max(1, _options.AnalysisLookahead)) { SingleReader = true, SingleWriter = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumer = Task.Run(async () =>
        {
            try
            {
                await consume(channel.Reader, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }, CancellationToken.None);
        bool yielded;
        try
        {
            yielded = await produce(channel.Writer, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            await cts.CancelAsync().ConfigureAwait(false);
            channel.Writer.TryComplete();
            // 번역 쪽 오류로 분석이 취소된 것이면 그 오류를 알린다
            if (await ObserveAsync(consumer).ConfigureAwait(false) is { } error and not OperationCanceledException)
                ExceptionDispatchInfo.Throw(error);
            throw;
        }
        channel.Writer.TryComplete();
        await consumer.ConfigureAwait(false);
        return yielded;

        static async Task<Exception?> ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }

    /// <summary>
    /// 대기열의 단위를 기다리지 않고 꺼낼 수 있는 만큼(기다리는 풍선이 <see cref="TranslationOptions.TranslateChunk"/>개가 찰 때까지) 모아
    /// 한 번에 번역하고, 꺼낸 순서대로 마무리한다. 처리 시간은 마무리 간격으로 잰다(분석과 번역이 겹쳐 돌므로).
    /// </summary>
    private async Task TranslateQueuedAsync<T>(Job job, ChannelReader<T> reader, string source, Func<T, IReadOnlyList<Slot>> slotsOf,
        Action<IReadOnlyList<T>, IReadOnlyCollection<int>> publish, Action<T, TimeSpan> finish, CancellationToken ct)
    {
        var chunk = Math.Max(1, _options.TranslateChunk);
        var since = Stopwatch.StartNew();
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            var units = new List<T>();
            var waiting = 0;
            while (waiting < chunk && reader.TryRead(out var unit))
            {
                units.Add(unit);
                waiting += slotsOf(unit).Count(s => s.State == SlotState.Pending);
            }
            if (units.Count == 0) continue;
            await TranslateSlotsAsync(job, units.SelectMany(slotsOf).ToList(), source, pages => publish(units, pages), ct).ConfigureAwait(false);
            var elapsed = since.Elapsed / units.Count;
            since.Restart();
            foreach (var u in units) finish(u, elapsed);
        }
    }

    /// <summary>
    /// 기다리는 풍선을 <see cref="TranslationOptions.TranslateChunk"/>개씩 묶어 번역해 slot에 반영하고, 묶음마다 바뀐 쪽을 게시한다.
    /// 한 묶음에 여러 쪽·band의 풍선이 섞인다. 요청 ID는 묶음 안의 순번이라 쪽마다 겹치는 풍선 ID와 상관없다.
    /// 묶음마다 이 작업에서 직전에 번역한 풍선 몇 개를 문맥으로 함께 넘긴다(말투·이름을 이어 간다).
    /// 번역기가 오류 없이 빈 결과·원문 그대로를 낸 풍선(예: "くっく" 같은 짧은 소리, 형식이 어긋나 돌아오지 않은 줄)은
    /// translation=null로 두고 끝난 것으로 본다. 화면에는 원문이 그대로 보이고 말풍선 편집에서 직접 번역을 넣을 수 있다.
    /// 그런 풍선 하나 때문에 쪽이 끝나지 않으면 책 전체가 "중지"로 끝나고 다시 해도 같은 곳에서 멈춘다.
    /// 오류가 나거나 결과가 오지 않은 풍선은 Failed(쪽이 끝나지 않음, 다시 시도할 대상)다.
    /// </summary>
    private async Task TranslateSlotsAsync(Job job, IReadOnlyList<Slot> slots, string source, Action<IReadOnlyCollection<int>> publish, CancellationToken ct)
    {
        var pending = slots.Where(s => s.State == SlotState.Pending).ToList();
        foreach (var chunk in pending.Chunk(Math.Max(1, _options.TranslateChunk)))
        {
            ct.ThrowIfCancellationRequested();
            var request = chunk.Select((s, i) => new TranslationRequestItem(i.ToString(CultureInfo.InvariantCulture), s.Bubble.Source)).ToList();
            IReadOnlyList<TranslationContextLine> context;
            lock (_gate) context = job.Recent.ToList();
            var results = await _providers.Translator.TranslateAsync(request, context, source, job.Target, ct).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var r in results)
                {
                    if (!int.TryParse(r.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i >= chunk.Length || chunk[i].State != SlotState.Pending) continue;
                    var slot = chunk[i];
                    if (r.Error is { } error) { job.LastError = error; continue; }
                    if (IsValidTranslation(slot.Bubble.Source, r.Text, job.Target))
                    {
                        slot.Bubble = slot.Bubble with { Translation = r.Text!.Trim() };
                        slot.State = SlotState.Done;
                        job.Recent.Enqueue(new TranslationContextLine(slot.Bubble.Source, slot.Bubble.Translation));
                        while (job.Recent.Count > Math.Max(0, _options.ContextLines)) job.Recent.Dequeue();
                    }
                    else slot.State = SlotState.Untranslatable;
                }
                foreach (var slot in chunk.Where(s => s.State == SlotState.Pending)) slot.State = SlotState.Failed;
            }
            MarkFirstResult(job);
            publish(chunk.Select(s => s.Page).ToHashSet());
        }
        MarkFirstResult(job);

        // 원문·번역 본문은 남기지 않고 개수만 기록한다
        foreach (var page in pending.Where(s => s.State == SlotState.Untranslatable).GroupBy(s => s.Page))
            _diagnostics?.Log(LogLevel.Info, LogCategory.Translation, $"Page {page.Key + 1}: translator returned no text for {page.Count()} balloon(s); left untranslated");
    }

    private void MarkFirstResult(Job job)
    {
        if (job.FirstResult) return;
        job.FirstResult = true;
        Update(job, j => { if (j.Phase == TranslationPhase.Preparing) j.Phase = TranslationPhase.Translating; });
    }

    private async Task RunStripAsync(Job job, OpenedBook book, string source, CancellationToken ct)
    {
        if (job.Plan is null)
        {
            var sizes = new List<SizeD>(book.Pages.Count);
            foreach (var p in book.Pages) sizes.Add(await _pixels.GetSizeAsync(book, p, ct).ConfigureAwait(false));
            job.Plan = StripPlan.Create(sizes);
        }
        var plan = job.Plan;
        var revision = ProviderRevision;
        lock (_gate)
        {
            if (job.ResetUnfinished) ResetUnfinishedStrip(job);
            job.ResetUnfinished = false;
            job.InFlight.Clear();
            // 지난 실행이 번역을 마치기 전에 멈춘 band의 풍선은 버린다. 그 band는 다시 읽으므로 남겨 두면 풍선이 겹친다.
            foreach (var list in job.StripBubbles.Values) list.RemoveAll(s => !job.CompletedBands.Contains(s.Band));
        }

        // 이미 완료된 쪽의 캐시를 게시
        foreach (var p in job.Completed.ToList())
        {
            if (_cache.TryLoad(CacheKey(book.Pages[p].PageId, source, job.Target, TranslationScope.Strip)) is { } c)
                PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, p, c));
        }

        var yielded = await RunPipelineAsync<BandWork>(
            (writer, token) => AnalyzeBandsAsync(job, book, plan, source, revision, writer, token),
            (reader, token) => TranslateQueuedAsync(job, reader, source, w => w.Slots,
                (_, pages) => { foreach (var p in pages) PublishStripPage(job, book, p, source, revision, complete: false); },
                (w, elapsed) => FinishBand(job, book, plan, w, source, revision, elapsed), token),
            ct).ConfigureAwait(false);
        if (yielded)
        {
            Update(job, j => j.Phase = TranslationPhase.Queued);
            return;
        }
        FinishJob(job, book.Pages.Count);
    }

    /// <summary>band를 읽기 화면과 같은 maxWidth·수평 중앙·연속 y 공간으로 합성해 분석하고, 찾은 풍선을 쪽에 나눠 대기열에 넣는다.</summary>
    /// <returns>더 급한 작업에 자리를 내주려고 멈췄는지.</returns>
    private async Task<bool> AnalyzeBandsAsync(Job job, OpenedBook book, StripPlan plan, string source, string revision, ChannelWriter<BandWork> writer, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            StripBand? band;
            int epoch;
            lock (_gate)
            {
                var focusBand = plan.Bands.FirstOrDefault(b => b.PageIndices.Contains(Math.Clamp(job.FocusPage, 0, Math.Max(0, book.Pages.Count - 1))))?.Index ?? 0;
                band = plan.Bands.Skip(focusBand).Concat(plan.Bands.Take(focusBand))
                    .FirstOrDefault(b => !job.CompletedBands.Contains(b.Index) && !job.AttemptedBands.Contains(b.Index) && !job.InFlight.Contains(b.Index)
                        && b.PageIndices.Any(p => !job.Completed.Contains(p) && (job.Only is null || job.Only.Contains(p))));
                if (band is null) return false;
                job.InFlight.Add(band.Index);
                epoch = job.StripEpoch;
            }
            var generation = _cache.Generation;
            var canvas = PagePixels.Blank(plan.Width, band.Height);
            foreach (var p in plan.PagesIntersecting(band))
            {
                var pixels = await GetPixelsCachedAsync(job, book, p, ct).ConfigureAwait(false);
                var rect = plan.PageRects[p];
                canvas.Blit(pixels, (int)Math.Round(rect.X), (int)Math.Round(rect.Y - band.Top));
            }
            var bubbles = await _analyzer.AnalyzeAsync(canvas, source, ct).ConfigureAwait(false);
            var slots = new List<Slot>();
            var touched = new HashSet<int>();
            lock (_gate)
            {
                // 읽는 동안 처음부터 다시 번역하라는 요청이 왔다: 이 결과는 버리고 다시 고른다
                if (job.StripEpoch != epoch) { job.InFlight.Remove(band.Index); continue; }
                foreach (var b in bubbles)
                {
                    var centerY = band.Top + b.Rect.CenterY;
                    if (!band.Owns(centerY)) continue; // 중심 Y를 소유하는 band에서 한 번만 채택
                    var page = plan.PageAtY(centerY);
                    // 이미 끝난(캐시에 있는) 쪽은 다시 번역하지 않는다. 다시 넣으면 완성된 쪽 화면이 부분 결과로 덮인다.
                    if (job.Completed.Contains(page)) continue;
                    var origin = plan.PageRects[page];
                    var dx = -origin.X; var dy = band.Top - origin.Y;
                    if (!job.StripBubbles.TryGetValue(page, out var list)) job.StripBubbles[page] = list = [];
                    // 번역 중에는 쪽 안에서 겹치지 않는 ID를 준다(band마다 0부터 매기므로). 완료 때 읽는 순서로 다시 매긴다.
                    var local = b with
                    {
                        Id = list.Count == 0 ? 0 : list.Max(s => s.Bubble.Id) + 1,
                        Rect = b.Rect.Offset(dx, dy),
                        BalloonRect = b.BalloonRect?.Offset(dx, dy),
                    };
                    var slot = new Slot(page, band.Index, local);
                    list.Add(slot);
                    slots.Add(slot);
                    touched.Add(page);
                }
            }
            foreach (var p in touched) PublishStripPage(job, book, p, source, revision, complete: false);
            await writer.WriteAsync(new BandWork(band, epoch, generation, slots), ct).ConfigureAwait(false);
            if (ShouldYield(job)) return true;
        }
    }

    private void FinishBand(Job job, OpenedBook book, StripPlan plan, BandWork work, string source, string revision, TimeSpan elapsed)
    {
        var stale = false;
        List<int> finished = [];
        Update(job, j =>
        {
            if (j.StripEpoch != work.Epoch) { stale = true; return; }
            j.InFlight.Remove(work.Band.Index);
            j.AttemptedBands.Add(work.Band.Index);
            j.CompletedBands.Add(work.Band.Index);
            j.Processed += Math.Max(1, work.Band.PageIndices.Count);
            j.ProcessingTime += elapsed;
            finished = plan.CompletedPages(j.CompletedBands).Where(p => !j.Completed.Contains(p)).ToList();
        });
        if (stale) return;
        foreach (var p in finished)
        {
            var page = BuildStripPage(job, book, p, source, revision, complete: true);
            if (!page.IsComplete) { PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, p, page)); continue; }
            var key = CacheKey(book.Pages[p].PageId, source, job.Target, TranslationScope.Strip);
            if (_cache.Store(key, page, work.Generation)) Update(job, j => { j.Completed.Add(p); j.Forced.Remove(p); });
            PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, p, page));
        }
    }

    /// <summary>
    /// strip 작업을 다시 시작할 때: 끝나지 못한 쪽이 걸친 band를 완료 목록에서 뺀다. 그 band의 풍선은 호출한 쪽에서 버린다.
    /// 그대로 두면 band가 이미 끝난 것으로 보여 다시 읽지 않고 곧바로 같은 실패로 끝난다.
    /// 빼낸 band가 걸친 다른 미완료 쪽도 자기 band가 모두 빠지므로 풍선이 중복되지 않는다.
    /// </summary>
    private static void ResetUnfinishedStrip(Job job)
    {
        if (job.Plan is not { } plan) return;
        var unfinished = Enumerable.Range(0, plan.PageRects.Count).Where(p => !job.Completed.Contains(p)).ToHashSet();
        foreach (var band in plan.Bands)
            if (band.PageIndices.Any(unfinished.Contains)) job.CompletedBands.Remove(band.Index);
    }

    private void PublishStripPage(Job job, OpenedBook book, int page, string source, string revision, bool complete)
        => PageUpdated?.Invoke(this, new TranslationPageUpdate(book.BookKey, job.Target, page, BuildStripPage(job, book, page, source, revision, complete)));

    private TranslatedPage BuildStripPage(Job job, OpenedBook book, int page, string source, string revision, bool complete)
    {
        var rect = job.Plan!.PageRects[page];
        List<TranslatedBubble> bubbles;
        bool finished;
        lock (_gate)
        {
            var list = job.StripBubbles.TryGetValue(page, out var l) ? l : [];
            // 페이지 로컬 ID 재부여(위→아래, 같은 높이면 오른쪽 먼저) — 번역 중에는 기존 ID 유지
            bubbles = complete
                ? list.Select(s => s.Bubble).OrderBy(b => b.Rect.Top).ThenByDescending(b => b.Rect.CenterX).Select((b, i) => b with { Id = i }).ToList()
                : list.Select(s => s.Bubble).ToList();
            finished = list.All(s => s.IsFinished);
        }
        return new TranslatedPage
        {
            PageId = book.Pages[page].PageId,
            SourceLanguage = source,
            TargetLanguage = job.Target,
            Scope = TranslationScope.Strip,
            ProviderRevision = revision,
            AnalysisVersion = PageAnalyzer.AnalysisVersion,
            ImageWidth = Math.Max(1, rect.Width),
            ImageHeight = Math.Max(1, rect.Height),
            IsComplete = complete && finished,
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

    private sealed class Job(BookTarget book, string target, TranslationScope scope)
    {
        public BookTarget Book { get; } = book;
        public string Target { get; } = target;
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
        /// <summary>strip: 쪽마다 찾은 풍선(어느 band에서 찾았는지와 번역 상태를 함께 둔다).</summary>
        public Dictionary<int, List<Slot>> StripBubbles { get; } = [];
        public HashSet<int> CompletedBands { get; } = [];
        public HashSet<int> AttemptedBands { get; } = [];
        /// <summary>이번 실행에서 분석을 시작했지만 아직 번역을 마치지 못한 쪽(strip은 band). 멈췄다 다시 돌면 비운다.</summary>
        public HashSet<int> InFlight { get; } = [];
        /// <summary>strip 상태를 처음부터 다시 시작할 때마다 늘린다. 그 전에 읽기 시작한 band 결과는 버린다.</summary>
        public int StripEpoch { get; set; }
        /// <summary>끝나거나 멈춘 작업을 다시 시작함: 다음 strip 실행 시작 때 끝나지 못한 쪽의 band를 다시 연다.</summary>
        public bool ResetUnfinished { get; set; }
        /// <summary>마지막 번역기 오류(끝나지 못한 쪽이 남을 때 안내에 쓴다).</summary>
        public string? LastError { get; set; }
        /// <summary>이 작업에서 직전에 번역한 풍선(번역기 문맥, _gate 안에서 바꾼다).</summary>
        public Queue<TranslationContextLine> Recent { get; } = new();
        public Dictionary<int, PagePixels> PixelCache { get; } = [];
        public Queue<int> PixelOrder { get; } = new();
    }

    private enum SlotState { Pending, Done, Untranslatable, Failed }

    /// <summary>번역할 풍선 하나. 분석이 만들고 번역이 결과를 제자리에 반영한다(_gate 안에서 바꾼다).</summary>
    private sealed class Slot(int page, int band, TranslatedBubble bubble)
    {
        public int Page { get; } = page;
        /// <summary>strip: 이 풍선을 찾은 band(쪽 범위는 -1).</summary>
        public int Band { get; } = band;
        public TranslatedBubble Bubble { get; set; } = bubble;
        public SlotState State { get; set; } = bubble.Translation is null ? SlotState.Pending : SlotState.Done;
        /// <summary>번역됐거나 번역할 수 없는 것으로 끝났다(오류·대기 중이 아니다).</summary>
        public bool IsFinished => State is SlotState.Done or SlotState.Untranslatable;
    }

    /// <summary>쪽 범위: 분석이 끝나 번역을 기다리는 쪽.</summary>
    private sealed record PageWork(int Page, TranslatedPage Analyzed, TranslationCacheKey Key, int Generation, List<Slot> Slots);

    /// <summary>strip: 분석이 끝나 번역을 기다리는 band와 그 band에서 찾은 풍선.</summary>
    private sealed record BandWork(StripBand Band, int Epoch, int Generation, List<Slot> Slots);
}
