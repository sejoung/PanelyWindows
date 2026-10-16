using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Panely.App.Integration;
using Panely.Core.Abstractions;
using Panely.Core.Translation;
using Panely.Domain.Model;
using Panely.Domain.Translation;

namespace Panely.App.Translation;

/// <summary>앱 decoder로 페이지 픽셀(BGRA32)과 치수를 제공한다.</summary>
public sealed class WpfPagePixelSource(IPageDecoder decoder, Func<OpenedBook, int, SizeD?>? knownSize = null) : IPagePixelSource
{
    public async Task<PagePixels> GetPixelsAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken)
    {
        var bitmap = await decoder.DecodeAsync(book, page, cancellationToken).ConfigureAwait(false);
        return ToPixels(bitmap);
    }

    public static PagePixels ToPixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var data = new byte[stride * source.PixelHeight];
        source.CopyPixels(data, stride, 0);
        return new PagePixels(source.PixelWidth, source.PixelHeight, data, stride);
    }

    public async Task<SizeD> GetSizeAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken)
    {
        if (knownSize?.Invoke(book, page.Index) is { } known && !known.IsEmpty) return known;
        var bytes = await book.Data.ReadAsync(page, cancellationToken).ConfigureAwait(false);
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var orientation = 1;
            try
            {
                if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
            }
            catch (Exception) { }
            return orientation >= 5 ? new SizeD(frame.PixelHeight, frame.PixelWidth) : new SizeD(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            var decoded = decoder.Decode(bytes, page.Extension);
            return new SizeD(decoded.PixelWidth, decoded.PixelHeight);
        }
    }
}

/// <summary>
/// 내보내기 렌더러: 원본 해상도로 이미지+patch를 그린다. 뷰어와 같은 OverlayPainter를 쓴다.
/// WPF 렌더링은 전용 STA 스레드에서 실행해 읽기 UI를 막지 않는다.
/// </summary>
public sealed class WpfTranslationRenderer : ITranslatedPageRenderer, IDisposable
{
    private readonly IPageDecoder _decoder;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;

    public WpfTranslationRenderer(IPageDecoder decoder)
    {
        _decoder = decoder;
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Panely export renderer" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public async Task<(byte[] Bytes, string Extension)> RenderAsync(byte[] original, string extension, IReadOnlyList<PatchLayout> patches, CancellationToken cancellationToken)
    {
        var op = _dispatcher.InvokeAsync(() => Render(original, extension, patches), DispatcherPriority.Normal, cancellationToken);
        return await op.Task.ConfigureAwait(false);
    }

    private (byte[], string) Render(byte[] original, string extension, IReadOnlyList<PatchLayout> patches)
    {
        var bitmap = _decoder.Decode(original, extension);
        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        var png = extension.Equals("png", StringComparison.OrdinalIgnoreCase);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (!png) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(bitmap, new Rect(0, 0, w, h));
            OverlayPainter.DrawPatches(dc, patches, pendingDimmed: false);
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return (ms.ToArray(), png ? "png" : "jpg");
    }

    public void Dispose() => _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
}
