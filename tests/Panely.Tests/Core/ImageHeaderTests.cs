using System.Buffers.Binary;
using System.Text;
using Panely.Core.Imaging;

namespace Panely.Tests.Core;

public class ImageHeaderTests
{
    private static (int W, int H, int O, string F) Read(byte[] data)
    {
        Assert.True(ImageHeaderReader.TryRead(data, out var info));
        return (info.Width, info.Height, info.ExifOrientation, info.Format);
    }

    [Fact]
    public void Png() => Assert.Equal((300, 500, 1, "png"), Read(CoreTestKit.PngBytes(300, 500)));

    [Fact]
    public void Gif()
    {
        var d = new byte[13];
        "GIF89a"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), 640);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), 480);
        Assert.Equal((640, 480, 1, "gif"), Read(d));
    }

    [Fact]
    public void Bmp_TopDownHeight()
    {
        var d = new byte[54];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(18), 123);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(22), -456);
        Assert.Equal((123, 456, 1, "bmp"), Read(d));
    }

    private static byte[] Riff(string chunk, byte[] payload)
    {
        var d = new byte[20 + payload.Length];
        "RIFF"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), (uint)(d.Length - 8));
        "WEBP"u8.CopyTo(d.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(d, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)payload.Length);
        payload.CopyTo(d, 20);
        return d;
    }

    [Fact]
    public void WebP_Lossy()
    {
        var p = new byte[10];
        p[3] = 0x9D; p[4] = 0x01; p[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), 800);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), 1200);
        Assert.Equal((800, 1200, 1, "webp"), Read(Riff("VP8 ", p)));
    }

    [Fact]
    public void WebP_Lossless()
    {
        var p = new byte[10];
        p[0] = 0x2F;
        uint bits = (uint)(1000 - 1) | ((uint)(700 - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), bits);
        Assert.Equal((1000, 700, 1, "webp"), Read(Riff("VP8L", p)));
    }

    [Fact]
    public void WebP_Extended()
    {
        var p = new byte[10];
        var w = 4000 - 1; var h = 3000 - 1;
        p[4] = (byte)w; p[5] = (byte)(w >> 8); p[6] = (byte)(w >> 16);
        p[7] = (byte)h; p[8] = (byte)(h >> 8); p[9] = (byte)(h >> 16);
        Assert.Equal((4000, 3000, 1, "webp"), Read(Riff("VP8X", p)));
    }

    private static byte[] TiffIfd(bool little, int w, int h, int orientation)
    {
        var d = new byte[8 + 2 + 3 * 12 + 4];
        if (little) { d[0] = (byte)'I'; d[1] = (byte)'I'; } else { d[0] = (byte)'M'; d[1] = (byte)'M'; }
        void U16(int o, int v) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o), (ushort)v); }
        void U32(int o, int v) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), (uint)v); else BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(o), (uint)v); }
        U16(2, 42);
        U32(4, 8);
        U16(8, 3);
        var e = 10;
        U16(e, 256); U16(e + 2, 4); U32(e + 4, 1); U32(e + 8, w); e += 12;
        U16(e, 257); U16(e + 2, 3); U32(e + 4, 1); U16(e + 8, h); e += 12;
        U16(e, 274); U16(e + 2, 3); U32(e + 4, 1); U16(e + 8, orientation);
        return d;
    }

    [Fact]
    public void Tiff_BigEndianWithOrientation()
        => Assert.Equal((200, 100, 6, "tiff"), Read(TiffIfd(false, 100, 200, 6)));

    [Fact]
    public void Jpeg_ExifOrientationSwapsSize()
    {
        var exif = TiffIfd(true, 0, 0, 6);
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        // APP1 Exif
        ms.Write([0xFF, 0xE1]);
        var app1Len = 2 + 6 + exif.Length;
        ms.WriteByte((byte)(app1Len >> 8)); ms.WriteByte((byte)app1Len);
        ms.Write("Exif\0\0"u8);
        ms.Write(exif);
        // SOF0: len 17, precision 8, height 1080, width 1920
        ms.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x04, 0x38, 0x07, 0x80, 0x03]);
        ms.Write(new byte[9]);
        Assert.Equal((1080, 1920, 6, "jpeg"), Read(ms.ToArray()));
    }

    [Fact]
    public void Jpeg_TruncatedBeforeSof_ReturnsFalse()
    {
        Assert.False(ImageHeaderReader.TryRead([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A], out _));
        Assert.False(ImageHeaderReader.TryRead([1, 2, 3, 4], out _));
    }

    private static byte[] Box(string type, params byte[][] content)
    {
        var len = 8 + content.Sum(c => c.Length);
        var d = new byte[len];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)len);
        Encoding.ASCII.GetBytes(type).CopyTo(d, 4);
        var o = 8;
        foreach (var c in content) { c.CopyTo(d, o); o += c.Length; }
        return d;
    }

    [Fact]
    public void Heic_IspeAndIrot()
    {
        byte[] ftyp = Box("ftyp", "heic"u8.ToArray(), new byte[4], "mif1heic"u8.ToArray());
        byte[] ispeThumb = Box("ispe", new byte[4], BE(320), BE(240));
        byte[] ispeMain = Box("ispe", new byte[4], BE(4032), BE(3024));
        byte[] irot = Box("irot", [3]);
        byte[] meta = Box("meta", new byte[4], Box("iprp", Box("ipco", ispeThumb, ispeMain, irot)));
        var data = ftyp.Concat(meta).Concat(Box("mdat", new byte[16])).ToArray();
        // irot 3 = 270° 반시계 → EXIF 6, 폭/높이 교환
        Assert.Equal((3024, 4032, 6, "heif"), Read(data));
    }

    private static byte[] BE(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }
}
