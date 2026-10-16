using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Panely.App.Integration;
using Panely.Core.Abstractions;
using Panely.Core.Localization;
using Panely.Domain.Model;

namespace Panely.App.Controls;

/// <summary>썸네일 셀 하나. Image를 처음 바인딩할 때(가상화로 실제 셀이 생길 때) decode를 요청한다.</summary>
public sealed class ThumbItem : INotifyPropertyChanged
{
    private readonly ThumbnailPanel _owner;
    private BitmapSource? _image;
    private bool _requested;
    private bool _isActive;
    private bool _isTranslated;
    private double _aspect = 1.5;

    public ThumbItem(ThumbnailPanel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    public int Index { get; }
    public string Caption => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Tooltip => Loc.T("Page %lld", Index + 1);

    /// <summary>알려진 h/w를 .3..3으로 clamp, 미상은 1.5.</summary>
    public double Aspect
    {
        get => _aspect;
        set { var v = Math.Clamp(value, 0.3, 3); if (Math.Abs(v - _aspect) < 0.001) return; _aspect = v; Raise(); Raise(nameof(ImageHeight)); }
    }

    public double ImageHeight => 112 * _aspect;

    public BitmapSource? Image
    {
        get
        {
            if (!_requested && _image is null)
            {
                _requested = true;
                _owner.RequestThumbnail(this);
            }
            return _image;
        }
        set { _image = value; Raise(); }
    }

    public void ResetRequest() => _requested = false;

    public bool IsActive { get => _isActive; set { if (_isActive == value) return; _isActive = value; Raise(); } }

    public bool IsTranslated
    {
        get => _isTranslated;
        set { if (_isTranslated == value) return; _isTranslated = value; Raise(); Raise(nameof(TranslatedVisibility)); }
    }

    public Visibility TranslatedVisibility => _isTranslated ? Visibility.Visible : Visibility.Collapsed;
    public string TranslatedTooltip => Loc.T("Translated");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 오른쪽 미리보기 목록(02): 폭 148, 이미지 폭 112, 셀 간격·padding 8, radius 4, 활성 테두리 accent 2.
/// 가상화(천 쪽도 보이는 셀만 생성), 활성 셀을 가운데로 자동 스크롤, 번역 완료 원형 배지.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "주 창과 수명이 같은 컨트롤. SemaphoreSlim은 WaitAsync만 쓰므로 해제할 대기 핸들이 없다.")]
public partial class ThumbnailPanel : UserControl
{
    private const int MaxCached = 300;
    private readonly ObservableCollection<ThumbItem> _items = new();
    private readonly SemaphoreSlim _gate = new(2);
    private readonly LinkedList<ThumbItem> _lru = new();
    private OpenedBook? _book;
    private IPageDecoder? _decoder;
    private CancellationTokenSource _cts = new();
    private int _active = -1;

    public ThumbnailPanel()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        List.PreviewMouseLeftButtonUp += OnClick;
        List.KeyDown += OnKeyDown;
    }

    public event Action<int>? PageSelected;

    public void SetDecoder(IPageDecoder decoder) => _decoder = decoder;

    public void SetBook(OpenedBook? book, Func<int, SizeD?> sizeOf)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _book = book;
        _items.Clear();
        _lru.Clear();
        _active = -1;
        if (book is null) return;
        for (var i = 0; i < book.Pages.Count; i++)
        {
            var item = new ThumbItem(this, i);
            if (sizeOf(i) is { Width: > 0 } s) item.Aspect = s.Height / s.Width;
            _items.Add(item);
        }
    }

    public void UpdateSizes(IEnumerable<int> indices, Func<int, SizeD?> sizeOf)
    {
        foreach (var i in indices)
            if (i >= 0 && i < _items.Count && sizeOf(i) is { Width: > 0 } s) _items[i].Aspect = s.Height / s.Width;
    }

    public void SetActive(Panely.Domain.Model.PageRange range, bool scroll)
    {
        for (var i = 0; i < _items.Count; i++) _items[i].IsActive = range.Contains(i);
        if (range.IsEmpty || range.Start >= _items.Count) return;
        if (_active == range.Start && !scroll) return;
        _active = range.Start;
        if (IsVisible) CenterOn(range.Start);
    }

    public void SetTranslated(IReadOnlySet<int> translated)
    {
        for (var i = 0; i < _items.Count; i++) _items[i].IsTranslated = translated.Contains(i);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsVisibleProperty && IsVisible && _active >= 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CenterOn(_active));
    }

    private void CenterOn(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var sv = FindScrollViewer(List);
        if (sv is null) { List.ScrollIntoView(_items[index]); return; }
        // 가변 높이: 앞 셀 높이 합으로 위치 계산(가상화와 무관하게 정확)
        double y = 8;
        for (var i = 0; i < index; i++) y += _items[i].ImageHeight + 4 + 16 + 8;
        var target = y + (_items[index].ImageHeight + 20) / 2 - sv.ViewportHeight / 2;
        sv.ScrollToVerticalOffset(Math.Max(0, target));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var r = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (r is not null) return r;
        }
        return null;
    }

    internal void RequestThumbnail(ThumbItem item)
    {
        var book = _book;
        var decoder = _decoder;
        if (book is null || decoder is null || item.Index >= book.Pages.Count) return;
        var token = _cts.Token;
        var page = book.Pages[item.Index];
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Ceiling(112 * Math.Max(1, scale));
        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var bytes = await book.Data.ReadAsync(page, token).ConfigureAwait(false);
                    var image = decoder.Decode(bytes, page.Extension, width);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        item.Image = image;
                        Track(item);
                    });
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception)
            {
                // 썸네일 실패는 빈 셀로 둔다
            }
        }, token);
    }

    private void Track(ThumbItem item)
    {
        _lru.AddLast(item);
        while (_lru.Count > MaxCached)
        {
            var old = _lru.First!.Value;
            _lru.RemoveFirst();
            old.ResetRequest();
            old.Image = null;
        }
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: ThumbItem t })
            PageSelected?.Invoke(t.Index);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is ThumbItem t)
        {
            PageSelected?.Invoke(t.Index);
            e.Handled = true;
        }
    }
}
