using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Panely.App.Integration;
using Panely.Core.Abstractions;

namespace Panely.App.Imaging;

/// <summary>
/// 페이지 decode(04 이미지 로딩 정책). WIC 우선, 첫 프레임만, EXIF 회전 적용, Pbgra32로 변환 후 Freeze.
/// WIC 코덱이 없으면(HEIC/HEIF/WebP 확장이 없는 PC) Magick.NET으로 fallback.
/// UI 스레드에서 호출하지 않는다.
/// </summary>
public sealed class PageDecoder : IPageDecoder
{
    public async Task<BitmapSource> DecodeAsync(OpenedBook book, PageEntry page, CancellationToken cancellationToken)
    {
        var bytes = await book.Data.ReadAsync(page, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Decode(bytes, page.Extension);
    }

    public BitmapSource Decode(byte[] bytes, string extension, int? maxPixelWidth = null)
    {
        Exception? wicError = null;
        try
        {
            return DecodeWic(bytes, maxPixelWidth);
        }
        catch (Exception ex)
        {
            // 코덱 없음(NotSupported/COMException)·손상(FileFormat) 모두 fallback 시도
            wicError = ex;
        }

        try
        {
            return DecodeMagick(bytes, maxPixelWidth);
        }
        catch (Exception ex)
        {
            throw new ImageDecodeException($"Couldn't decode .{extension} image.", new AggregateException(wicError!, ex));
        }
    }

    private static BitmapSource DecodeWic(byte[] bytes, int? maxPixelWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        // None: 여기서는 헤더·메타데이터만 읽고, 픽셀은 Finalize의 CachedBitmap이 한 번에 끌어온다
        // (OnLoad면 전체 크기 decode 버퍼를 하나 더 만든 뒤 다시 복사한다).
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new NotSupportedException("No frames.");
        var first = decoder.Frames[0];
        BitmapSource frame = first;
        var orientation = ReadOrientation(first);

        if (maxPixelWidth is { } maxW && maxW > 0)
        {
            var rotated = orientation >= 5;
            var orientedWidth = rotated ? first.PixelHeight : first.PixelWidth;
            if (orientedWidth > maxW) frame = DecodeScaled(bytes, rotated, maxW);
        }

        frame = ApplyOrientation(frame, orientation);
        return Finalize(frame);
    }

    /// <summary>
    /// 줄인 크기로 decode(썸네일). WIC가 decode 단계에서 줄이므로(JPEG는 DCT 축소) 전체 decode 후 줄이는 것보다 훨씬 빠르다.
    /// 회전 전 픽셀 기준이라 90° 회전 이미지는 높이를 맞춘다.
    /// </summary>
    private static BitmapSource DecodeScaled(byte[] bytes, bool rotated, int maxWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (rotated) image.DecodePixelHeight = maxWidth;
        else image.DecodePixelWidth = maxWidth;
        image.EndInit();
        return image;
    }

    private static BitmapSource Finalize(BitmapSource source)
    {
        BitmapSource result = source;
        if (result.Format != PixelFormats.Pbgra32 && result.Format != PixelFormats.Bgr32)
            result = new FormatConvertedBitmap(result, PixelFormats.Pbgra32, null, 0);
        var cached = new CachedBitmap(result, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta)
            {
                foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    try
                    {
                        var value = meta.GetQuery(query);
                        if (value is ushort u) return u;
                        if (value is int i) return i;
                        if (value is uint ui) return (int)ui;
                    }
                    catch (Exception)
                    {
                        // 쿼리 미지원 형식
                    }
                }
            }
        }
        catch (Exception)
        {
            // 메타데이터 없음
        }
        return 1;
    }

    /// <summary>EXIF orientation 1..8을 픽셀에 적용.</summary>
    internal static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        return t is null ? source : new TransformedBitmap(source, t);
    }

    private static BitmapSource DecodeMagick(byte[] bytes, int? maxPixelWidth)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        using var image = new MagickImage(bytes, settings);
        image.AutoOrient();
        if (maxPixelWidth is { } maxW && maxW > 0 && image.Width > maxW)
            image.Resize((uint)maxW, 0);
        image.ColorSpace = ColorSpace.sRGB;
        var width = (int)image.Width;
        var height = (int)image.Height;
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidOperationException("No pixels.");
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
        return Finalize(bmp);
    }
}

public sealed class ImageDecodeException(string message, Exception? inner) : Exception(message, inner);
