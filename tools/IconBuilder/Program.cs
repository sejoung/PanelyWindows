// assets/panely-icon-stacked.svg 를 WPF 도형으로 옮겨 16..256 PNG와 .ico를 만든다.
// 사용: dotnet run --project tools/IconBuilder -- <출력 폴더>
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Controls;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

var thread = new Thread(() =>
{
    var pngs = new List<(int Size, byte[] Data)>();
    foreach (var size in sizes)
    {
        var png = Render(size);
        pngs.Add((size, png));
        if (size is 256 or 48 or 32 or 16)
            File.WriteAllBytes(System.IO.Path.Combine(outDir, $"panely-{size}.png"), png);
    }
    WriteIco(System.IO.Path.Combine(outDir, "Panely.ico"), pngs);
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine($"Icons written to {System.IO.Path.GetFullPath(outDir)}");

static byte[] Render(int size)
{
    var canvas = BuildIcon();
    var scale = size / 1024.0;
    canvas.LayoutTransform = new ScaleTransform(scale, scale);
    canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    canvas.Arrange(new Rect(0, 0, size, size));
    canvas.UpdateLayout();
    var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(canvas);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(rtb));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    return ms.ToArray();
}

static Canvas BuildIcon()
{
    var root = new Canvas { Width = 1024, Height = 1024 };
    root.Children.Add(Rounded(96, 96, 832, 832, 188, Gradient("#1E2430", "#11151D", 160, 120, 864, 904)));

    var group = new Canvas { Width = 1024, Height = 1024 };
    group.Effect = new DropShadowEffect { Direction = 270, ShadowDepth = 22, BlurRadius = 48, Opacity = 0.28, Color = Color.FromRgb(5, 11, 23) };
    group.Children.Add(Rounded(218, 238, 418, 520, 44, Gradient("#3F5C96", "#22314D", 214, 212, 654, 704), new RotateTransform(-7, 218, 238)));
    group.Children.Add(Rounded(286, 212, 432, 544, 48, Gradient("#7EB7FF", "#3A7BFF", 264, 236, 700, 734), new RotateTransform(4, 286, 212)));
    group.Children.Add(Rounded(302, 248, 438, 544, 52, Gradient("#F8FBFF", "#D8E7FF", 316, 262, 742, 760)));
    var ink = (Color)ColorConverter.ConvertFromString("#2B3C5F");
    group.Children.Add(Rounded(354, 314, 128, 164, 24, new SolidColorBrush(Color.FromArgb((byte)(0.18 * 255), ink.R, ink.G, ink.B))));
    group.Children.Add(Rounded(510, 314, 178, 84, 22, new SolidColorBrush(Color.FromArgb((byte)(0.14 * 255), ink.R, ink.G, ink.B))));
    group.Children.Add(Rounded(510, 414, 178, 174, 26, new SolidColorBrush(Color.FromArgb((byte)(0.10 * 255), ink.R, ink.G, ink.B))));
    group.Children.Add(Rounded(354, 506, 128, 214, 24, new SolidColorBrush(Color.FromArgb((byte)(0.12 * 255), ink.R, ink.G, ink.B))));
    group.Children.Add(Rounded(510, 612, 178, 108, 24, new SolidColorBrush(Color.FromArgb((byte)(0.16 * 255), ink.R, ink.G, ink.B))));
    root.Children.Add(group);
    return root;
}

static Rectangle Rounded(double x, double y, double w, double h, double r, Brush fill, Transform? transform = null)
{
    // SVG rotate(a cx cy)는 사용자 좌표 기준. Canvas에 x,y로 놓은 뒤 요소 기준 원점(0,0)으로 회전한다.
    var rect = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = fill };
    Canvas.SetLeft(rect, x);
    Canvas.SetTop(rect, y);
    if (transform is RotateTransform rt)
        rect.RenderTransform = new RotateTransform(rt.Angle, rt.CenterX - x, rt.CenterY - y);
    return rect;
}

static Brush Gradient(string from, string to, double x1, double y1, double x2, double y2)
{
    // userSpaceOnUse 좌표를 Canvas 전체 기준 절대 좌표로 사용
    return new LinearGradientBrush(
        new GradientStopCollection
        {
            new((Color)ColorConverter.ConvertFromString(from), 0),
            new((Color)ColorConverter.ConvertFromString(to), 1),
        },
        new Point(x1, y1), new Point(x2, y2))
    { MappingMode = BrushMappingMode.Absolute, Transform = Transform.Identity };
}

static void WriteIco(string path, List<(int Size, byte[] Data)> images)
{
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(data.Length);
        bw.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) bw.Write(data);
}
