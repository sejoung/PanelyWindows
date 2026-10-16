using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Panely.Core;
using Panely.Core.Abstractions;
using Panely.Domain.Model;
using PageRange = Panely.Domain.Model.PageRange;

namespace Panely.App.Integration;

/// <summary>앱 전체 서비스 묶음(composition root에서 한 번 만든다).</summary>
public sealed class AppServices
{
    public required AppPaths Paths { get; init; }
    public required IStateStore State { get; init; }
    public required IDiagnostics Diagnostics { get; init; }
    public required IBookOpener Opener { get; init; }
    public required IExtractionCache ExtractionCache { get; init; }
    public required IPageDecoder Decoder { get; init; }
}

/// <summary>
/// 전체 해상도 페이지 decode(EXIF 회전 적용, 첫 프레임, Freeze된 BitmapSource). worker 스레드에서 호출 가능.
/// 번역 OCR·내보내기 렌더링이 사용한다.
/// </summary>
public interface IPageDecoder
{
    Task<BitmapSource> DecodeAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken);

    /// <summary>이미 받은 bytes를 decode. maxPixelWidth가 있으면 축소 decode.</summary>
    BitmapSource Decode(byte[] bytes, string extension, int? maxPixelWidth = null);
}

/// <summary>
/// 리더(UI 담당)가 번역 기능에 제공하는 호스트. 모든 멤버는 UI 스레드에서 호출한다.
/// 좌표: "view"는 OverlayLayer 기준 DIP, "page px"는 회전 보정된 페이지 픽셀 좌상단 원점.
/// </summary>
public interface IReaderHost
{
    AppServices Services { get; }
    Window OwnerWindow { get; }

    OpenedBook? CurrentBook { get; }
    int CurrentPageIndex { get; }
    /// <summary>화면에 실제로 반영된(committed) 논리 쪽 범위.</summary>
    PageRange VisibleRange { get; }
    ReadingLayout Layout { get; }
    ReadingDirection EffectiveDirection { get; }

    /// <summary>새 책이 화면에 반영되었거나 닫혔을 때.</summary>
    event EventHandler? BookChanged;
    /// <summary>committed visible range가 바뀌었을 때(이미지 교체와 함께).</summary>
    event EventHandler? VisiblePagesChanged;

    /// <summary>알려진 페이지 픽셀 크기(EXIF 적용). 모르면 null.</summary>
    SizeD? GetPageSize(int pageIndex);

    /// <summary>view 점이 가리키는 페이지와 그 페이지 픽셀 좌표. vertical에서는 strip 위 실제 페이지.</summary>
    (int PageIndex, Point PagePx)? HitTestPage(Point viewPoint);

    /// <summary>페이지 픽셀 사각형을 view 사각형으로. 페이지가 화면에 없으면 null.</summary>
    Rect? PageRectToView(int pageIndex, RectD pagePx);

    /// <summary>뷰어 위 팝오버/편집 바를 올리는 레이어(Canvas, 뷰어 좌표, 클릭 가능).</summary>
    Canvas OverlayLayer { get; }

    /// <summary>뷰어 하단 +76 진행 pill 영역(StackPanel, 가운데 정렬).</summary>
    Panel ProgressLayer { get; }

    /// <summary>번역 오버레이를 다시 그린다(RenderPageOverlay 재호출).</summary>
    void InvalidateOverlay();

    /// <summary>번역 범주 안내(알림 우선순위 최하위). duration null이면 사용자가 닫을 때까지.</summary>
    void ShowNotice(string message, TimeSpan? duration = null);

    Task OpenAsync(OpenRequest request);

    /// <summary>팝오버/입력 후 리더로 키보드 포커스를 돌린다.</summary>
    void FocusReader();

    /// <summary>번역 패널 앵커(툴바 번역 버튼). 없으면 null.</summary>
    FrameworkElement? TranslationButtonAnchor { get; }
}

/// <summary>
/// 번역 기능(06-translation.md) 전체. 번역 담당이 구현하고 리더는 이 인터페이스로만 호출한다.
/// 기능이 꺼져 있으면(IsEnabled=false) 리더는 번역 UI를 숨긴다.
/// </summary>
public interface ITranslationFeature
{
    bool IsEnabled { get; }

    /// <summary>툴바·메뉴·썸네일 배지·슬라이더 마크를 다시 계산해야 할 때.</summary>
    event EventHandler? StateChanged;

    void Attach(IReaderHost host);

    /// <summary>
    /// 한 페이지의 번역 오버레이를 그린다. dc는 이미 page px → 문서 변환이 적용된 상태.
    /// 페이지 경계로 clip하지 않는다(strip 풍선이 다음 이미지로 넘칠 수 있음).
    /// </summary>
    void RenderPageOverlay(DrawingContext dc, int pageIndex, SizeD pagePixelSize);

    /// <summary>번역 레이어가 보이는 상태(원본 표시 중이면 false). 편집 모드는 원본에서도 오버레이를 그린다.</summary>
    bool IsOverlayActive { get; }

    /// <summary>포인터 입력. true면 소비(뷰어는 pan/더블클릭 확대를 하지 않음).</summary>
    bool OnPointerPressed(Point viewPoint, MouseButton button, int clickCount);
    bool OnPointerMoved(Point viewPoint, bool leftButtonDown);
    bool OnPointerReleased(Point viewPoint, MouseButton button);
    /// <summary>Escape 등. true면 소비.</summary>
    bool OnKeyDown(KeyEventArgs e);

    /// <summary>현재 책에서 (보이는 언어로) 번역 완료된 논리 쪽.</summary>
    IReadOnlySet<int> TranslatedPageIndices { get; }

    /// <summary>툴바 언어 메뉴 이름: 실제로 보이는 언어 또는 "Original".</summary>
    string VisibleLanguageLabel { get; }

    /// <summary>번역 결과 또는 실행 중 작업이 있으면 true(툴바 버튼이 원본↔번역 전환).</summary>
    bool HasResultsOrJob { get; }

    /// <summary>툴바 번역 버튼: 결과가 없으면 패널, 있으면 전환.</summary>
    void OnToolbarButtonClicked();
    /// <summary>패널을 연다(언어 메뉴 "Translation Panel…").</summary>
    void ShowPanel();
    /// <summary>툴바 언어 메뉴 항목.</summary>
    IReadOnlyList<Control> BuildLanguageMenuItems();
    /// <summary>페이지 우클릭 항목(Translate This Page Again, Add a Balloon Here, AI Chat, Edit Balloons).</summary>
    IReadOnlyList<Control> BuildPageContextMenuItems(Point viewPoint);
    /// <summary>라이브러리 책/폴더 우클릭 항목(Export Translated Book/Series).</summary>
    IReadOnlyList<Control> BuildLibraryContextMenuItems(string path, bool isFolder);

    bool CanToggleOriginal { get; }
    void ToggleOriginal();
    bool CanRunOrPause { get; }
    bool IsRunningCurrentBook { get; }
    void RunOrPause();
    bool CanEdit { get; }
    bool IsEditing { get; }
    void ToggleEditMode();
    bool HasHiddenBalloons { get; }
    void ShowHiddenBalloons();
    bool CanExport { get; }
    void ShowExport();
    void ShowJobs();

    /// <summary>설정 창 "실험" 탭 내용.</summary>
    FrameworkElement CreateExperimentalSettingsView();

    /// <summary>종료: 작업 중지, partial 정리. 완료 캐시는 남긴다.</summary>
    Task ShutdownAsync();
}
