using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Panely.App.Integration;
using Panely.Core.Abstractions;
using Panely.Core.Imaging;
using Panely.Domain.Model;

namespace Panely.App.Imaging;

/// <summary>
/// 페이지 이미지 스케줄러(07 ImageScheduler, 04 이미지 로딩 정책).
/// - 헤더로 치수만 먼저(아카이브 64KiB prefix, 실패 시 전체).
/// - 우선순위 목록 순서로 decode, 동시성 max(2,min(8,CPU)), 새 요청은 이전 대기 작업을 취소.
/// - 메모리 예산 RAM/16 (150MiB..1GiB), 최대 100장, 비용 w×h×4. keep 범위 밖부터 LRU 제거.
/// - 결과는 UI 스레드에 배치 게시. 실패한 쪽은 keep 범위 밖으로 나가면 실패 상태 해제.
/// 모든 public 멤버는 UI 스레드에서 호출한다.
/// </summary>
public sealed class PageImageCache : IDisposable
{
    public const int PrefixBytes = 64 * 1024;
    public const int MaxImages = 100;

    private sealed class Slot
    {
        public BitmapSource? Image;
        public SizeD? Size;
        public bool Failed;
        public long Cost;
        public long LastUse;
    }

    private readonly record struct Result(long Epoch, int Index, BitmapSource? Image, SizeD? Size, bool Failed, bool SizeOnly);

    private readonly Dispatcher _dispatcher;
    private readonly IPageDecoder _decoder;
    private readonly IDiagnostics? _diagnostics;
    private readonly Dictionary<int, Slot> _slots = new();
    private readonly HashSet<int> _inFlight = new();
    private readonly HashSet<int> _sizeInFlight = new();
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly SemaphoreSlim _decodeGate;
    private readonly SemaphoreSlim _headerGate = new(4);
    private readonly long _budgetBytes;
    private CancellationTokenSource _requestCts = new();
    private CancellationTokenSource _bookCts = new();
    private OpenedBook? _book;
    private long _epoch;
    private long _useClock;
    private long _totalCost;
    private bool _flushScheduled;
    private PageRange _keep;

    public PageImageCache(Dispatcher dispatcher, IPageDecoder decoder, IDiagnostics? diagnostics)
    {
        _dispatcher = dispatcher;
        _decoder = decoder;
        _diagnostics = diagnostics;
        Concurrency = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
        _decodeGate = new SemaphoreSlim(Concurrency);
        _budgetBytes = ComputeBudget();
    }

    public int Concurrency { get; }
    public long BudgetBytes => _budgetBytes;
    public long TotalCost => _totalCost;

    /// <summary>이미지가 준비/실패된 인덱스(배치).</summary>
    public event Action<IReadOnlyCollection<int>>? ImagesChanged;

    /// <summary>새로 알게 된 치수(배치).</summary>
    public event Action<IReadOnlyCollection<int>>? SizesChanged;

    public OpenedBook? Book => _book;

    public static long ComputeBudget()
    {
        long ram;
        try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch (Exception) { ram = 8L * 1024 * 1024 * 1024; }
        if (ram <= 0) ram = 8L * 1024 * 1024 * 1024;
        return Math.Clamp(ram / 16, 150L * 1024 * 1024, 1024L * 1024 * 1024);
    }

    public void SetBook(OpenedBook? book)
    {
        _epoch++;
        _requestCts.Cancel();
        _requestCts = new CancellationTokenSource();
        _bookCts.Cancel();
        _bookCts = new CancellationTokenSource();
        _slots.Clear();
        _inFlight.Clear();
        _sizeInFlight.Clear();
        while (_results.TryDequeue(out _)) { }
        _totalCost = 0;
        _keep = default;
        _book = book;
    }

    public BitmapSource? GetImage(int index)
    {
        if (_slots.TryGetValue(index, out var s) && s.Image is not null)
        {
            s.LastUse = ++_useClock;
            return s.Image;
        }
        return null;
    }

    public bool IsFailed(int index) => _slots.TryGetValue(index, out var s) && s.Failed;

    public bool IsReady(int index) => _slots.TryGetValue(index, out var s) && (s.Image is not null || s.Failed);

    public SizeD? GetSize(int index) => _slots.TryGetValue(index, out var s) ? s.Size : null;

    /// <summary>이미지 없이 알려진 크기만 기록(외부에서 얻은 경우).</summary>
    public void SetSize(int index, SizeD size) => SlotFor(index).Size = size;

    private Slot SlotFor(int index)
    {
        if (!_slots.TryGetValue(index, out var s)) _slots[index] = s = new Slot();
        return s;
    }

    /// <summary>
    /// decode 요청. priority 순서로 시작하고 이전 요청의 아직 시작 안 한 작업은 취소한다(슬라이더 연속 이동).
    /// keep 범위 밖 실패는 해제한다.
    /// </summary>
    public void Request(IReadOnlyList<int> priority, PageRange keep)
    {
        var book = _book;
        if (book is null) return;
        _keep = keep;
        _requestCts.Cancel();
        _requestCts = new CancellationTokenSource();
        var token = CancellationTokenSource.CreateLinkedTokenSource(_requestCts.Token, _bookCts.Token).Token;

        foreach (var (index, slot) in _slots)
            if (slot.Failed && !keep.Contains(index)) slot.Failed = false;

        var epoch = _epoch;
        foreach (var index in priority)
        {
            if (index < 0 || index >= book.Pages.Count) continue;
            if (_slots.TryGetValue(index, out var s) && (s.Image is not null || s.Failed)) { if (s.Image is not null) s.LastUse = ++_useClock; continue; }
            if (!_inFlight.Add(index)) continue;
            var page = book.Pages[index];
            _ = Task.Run(() => DecodeWorker(book, page, index, epoch, token));
        }
        Evict();
    }

    private async Task DecodeWorker(OpenedBook book, PageEntry page, int index, long epoch, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _decodeGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            var image = await _decoder.DecodeAsync(book, page, _bookCts.Token).ConfigureAwait(false);
            Post(new Result(epoch, index, image, new SizeD(image.PixelWidth, image.PixelHeight), false, false));
        }
        catch (OperationCanceledException)
        {
            Post(new Result(epoch, index, null, null, false, SizeOnly: true) with { Failed = false });
        }
        catch (Exception ex)
        {
            _diagnostics?.Log(Panely.Domain.Model.LogLevel.Warning, LogCategory.Image, $"Decode failed page={index + 1} ext={page.Extension}: {ex.GetType().Name}");
            Post(new Result(epoch, index, null, null, true, false));
        }
        finally
        {
            if (entered) _decodeGate.Release();
        }
    }

    /// <summary>헤더 치수 확보(vertical: 전체 쪽 rect를 decode 전에 확보).</summary>
    public void EnsureSizes(IEnumerable<int> indices)
    {
        var book = _book;
        if (book is null) return;
        var epoch = _epoch;
        var token = _bookCts.Token;
        foreach (var index in indices)
        {
            if (index < 0 || index >= book.Pages.Count) continue;
            if (_slots.TryGetValue(index, out var s) && s.Size is not null) continue;
            if (!_sizeInFlight.Add(index)) continue;
            var page = book.Pages[index];
            _ = Task.Run(() => HeaderWorker(book, page, index, epoch, token));
        }
    }

    private async Task HeaderWorker(OpenedBook book, PageEntry page, int index, long epoch, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _headerGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var prefix = await book.Data.ReadPrefixAsync(page, PrefixBytes, token).ConfigureAwait(false);
            if (!ImageHeaderReader.TryRead(prefix, out var info))
            {
                var full = await book.Data.ReadAsync(page, token).ConfigureAwait(false);
                if (!ImageHeaderReader.TryRead(full, out info))
                {
                    Post(new Result(epoch, index, null, null, false, true));
                    return;
                }
            }
            Post(new Result(epoch, index, null, new SizeD(info.Width, info.Height), false, true));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Post(new Result(epoch, index, null, null, false, true));
        }
        finally
        {
            if (entered) _headerGate.Release();
        }
    }

    private void Post(Result result)
    {
        _results.Enqueue(result);
        lock (_results)
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
    }

    private void Flush()
    {
        lock (_results) _flushScheduled = false;
        var changed = new HashSet<int>();
        var sizes = new HashSet<int>();
        while (_results.TryDequeue(out var r))
        {
            if (r.Epoch != _epoch) continue;
            if (r.SizeOnly)
            {
                _sizeInFlight.Remove(r.Index);
                if (r.Image is null && r.Size is null && !r.Failed)
                {
                    // 취소된 decode 또는 헤더 실패
                    _inFlight.Remove(r.Index);
                    continue;
                }
                if (r.Size is { } sz)
                {
                    var slot = SlotFor(r.Index);
                    if (slot.Size is null) { slot.Size = sz; sizes.Add(r.Index); }
                }
                continue;
            }

            _inFlight.Remove(r.Index);
            var s = SlotFor(r.Index);
            if (r.Failed)
            {
                s.Failed = true;
                changed.Add(r.Index);
                continue;
            }
            if (r.Image is null) continue;
            if (s.Image is not null) _totalCost -= s.Cost;
            s.Image = r.Image;
            s.Cost = (long)r.Image.PixelWidth * r.Image.PixelHeight * 4;
            s.LastUse = ++_useClock;
            s.Failed = false;
            _totalCost += s.Cost;
            if (r.Size is { } size && s.Size != size)
            {
                s.Size = size;
                sizes.Add(r.Index);
            }
            changed.Add(r.Index);
        }
        Evict();
        if (sizes.Count > 0) SizesChanged?.Invoke(sizes);
        if (changed.Count > 0) ImagesChanged?.Invoke(changed);
    }

    /// <summary>예산 초과 시 keep 범위 밖, 오래 안 쓴 것부터 제거.</summary>
    private void Evict()
    {
        var count = _slots.Values.Count(s => s.Image is not null);
        if (_totalCost <= _budgetBytes && count <= MaxImages) return;
        var candidates = _slots.Where(kv => kv.Value.Image is not null && !_keep.Contains(kv.Key))
            .OrderBy(kv => kv.Value.LastUse).ToList();
        // keep 안이라도 예산을 크게 넘으면 먼 것부터 버린다
        foreach (var (_, slot) in candidates)
        {
            if (_totalCost <= _budgetBytes && count <= MaxImages) break;
            _totalCost -= slot.Cost;
            slot.Image = null;
            slot.Cost = 0;
            count--;
        }
    }

    public void Dispose()
    {
        _requestCts.Cancel();
        _bookCts.Cancel();
        _slots.Clear();
    }
}
