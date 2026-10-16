global using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Panely.App.Tests;

/// <summary>WPF 객체는 STA 스레드가 필요하다. xUnit 테스트 스레드(MTA)에서 STA로 실행한다.</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() => { result = func(); });
        return result;
    }
}

internal static class TestImages
{
    /// <summary>단색 배경에 검은 사각형을 그린 이미지를 지정 형식으로 인코딩한다.</summary>
    public static byte[] Make(int width, int height, string format, Color background, Int32Rect? blackBox = null, ushort? exifOrientation = null)
        => Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
                if (blackBox is { } b) dc.DrawRectangle(Brushes.Black, null, new Rect(b.X, b.Y, b.Width, b.Height));
            }
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            BitmapEncoder encoder = format switch
            {
                "jpg" => new JpegBitmapEncoder { QualityLevel = 95 },
                "bmp" => new BmpBitmapEncoder(),
                "gif" => new GifBitmapEncoder(),
                "tif" => new TiffBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };
            BitmapMetadata? metadata = null;
            if (exifOrientation is { } o && format == "jpg")
            {
                metadata = new BitmapMetadata("jpg");
                metadata.SetQuery("/app1/ifd/{ushort=274}", o);
            }
            BitmapSource frame = format == "jpg" ? new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0) : rtb;
            encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });

    /// <summary>(x,y) 픽셀의 (R,G,B).</summary>
    public static (byte R, byte G, byte B) PixelAt(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    public static BitmapSource Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}

/// <summary>저장소 루트와 테스트가 쓰는 빌드 산출물 위치.</summary>
internal static class Repo
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Panely.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Panely.sln을 찾지 못했습니다.");
        }
    }

    public static string Configuration => AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    /// <summary>
    /// 테스트와 같은 구성으로 빌드된 Panely.exe(프로젝트 참조로 함께 빌드됨).
    /// PANELY_SMOKE_EXE가 있으면 그 실행 파일(예: self-contained 게시본)로 smoke 테스트를 돌린다.
    /// </summary>
    public static string AppExe =>
        Environment.GetEnvironmentVariable("PANELY_SMOKE_EXE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Root, "src", "Panely.App", "bin", Configuration, "net10.0-windows10.0.19041.0", "Panely.exe");
}
