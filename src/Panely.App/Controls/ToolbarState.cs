using Panely.Domain.Model;

namespace Panely.App.Controls;

/// <summary>툴바·메뉴가 공유하는 현재 상태 스냅샷.</summary>
public sealed record ToolbarState
{
    public bool HasBook { get; init; }
    public bool IsLoading { get; init; }
    public bool LibraryPinned { get; init; }
    public bool ToolbarPinned { get; init; }
    public bool ThumbnailsVisible { get; init; }
    public ReadingLayout Layout { get; init; }
    public ReadingDirection Direction { get; init; }
    public bool CoverAlone { get; init; }
    public FitMode FitMode { get; init; }
    public bool AtFitScreen { get; init; }
    public bool AtFitWidth { get; init; }
    public bool AtFitHeight { get; init; }
    public bool CanZoomIn { get; init; }
    public bool CanZoomOut { get; init; }
    public bool SizeLocked { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsBookmarked { get; init; }
    public bool HasBookmarks { get; init; }
    public bool TranslationEnabled { get; init; }
    public bool TranslationActive { get; init; }
    public string TranslationLabel { get; init; } = string.Empty;
    public bool CanPreviousPage { get; init; }
    public bool CanNextPage { get; init; }
    public bool HasPreviousVolume { get; init; }
    public bool HasNextVolume { get; init; }
    public bool HasVolumes { get; init; }
    public bool WheelPageTurn { get; init; }
}
