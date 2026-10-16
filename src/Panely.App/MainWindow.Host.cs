using System.Windows;
using System.Windows.Controls;
using Panely.App.Integration;
using Panely.Core.Abstractions;
using Panely.Core.Reader;
using Panely.Domain.Model;
using PageRange = Panely.Domain.Model.PageRange;

namespace Panely.App;

/// <summary>번역 기능에 제공하는 리더 호스트(IReaderHost). 좌표는 FeatureLayer(=뷰어) 기준 DIP.</summary>
public partial class MainWindow : IReaderHost
{
    public event EventHandler? BookChanged;
    public event EventHandler? VisiblePagesChanged;

    AppServices IReaderHost.Services => _services;
    Window IReaderHost.OwnerWindow => this;
    OpenedBook? IReaderHost.CurrentBook => _session.Book;
    int IReaderHost.CurrentPageIndex => _session.PageIndex;
    PageRange IReaderHost.VisibleRange => Viewer.CommittedRange;
    ReadingLayout IReaderHost.Layout => _session.Layout;
    ReadingDirection IReaderHost.EffectiveDirection => _session.EffectiveDirection;

    SizeD? IReaderHost.GetPageSize(int pageIndex) => _cache.GetSize(pageIndex);

    (int PageIndex, Point PagePx)? IReaderHost.HitTestPage(Point viewPoint) => Viewer.HitTestPage(viewPoint);

    Rect? IReaderHost.PageRectToView(int pageIndex, RectD pagePx) => Viewer.PageRectToView(pageIndex, pagePx);

    Canvas IReaderHost.OverlayLayer => FeatureLayer;

    Panel IReaderHost.ProgressLayer => ProgressLayer;

    void IReaderHost.InvalidateOverlay() => Viewer.InvalidateOverlay();

    void IReaderHost.ShowNotice(string message, TimeSpan? duration)
        => _session.Notices.Post(new Notice(NoticeCategory.Translation, message, duration is null ? [NoticeAction.Dismiss] : [], duration));

    Task IReaderHost.OpenAsync(OpenRequest request) => OpenRequestAsync(request);

    void IReaderHost.FocusReader() => FocusReader();

    FrameworkElement? IReaderHost.TranslationButtonAnchor => _toolbar.TranslationAnchor;
}
