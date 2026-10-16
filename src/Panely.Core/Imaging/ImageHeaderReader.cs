using System.Buffers.Binary;
using Panely.Core.Abstractions;

namespace Panely.Core.Imaging;

/// <summary>
/// 헤더만으로 이미지 치수와 회전을 읽는다(04-files.md 이미지 로딩 정책).
/// Width/Height는 EXIF/irot 회전을 적용한 표시 픽셀 크기다(orientation 5–8은 w/h 교환).
/// 데이터가 부족하면 false → 호출자가 전체 entry를 읽어 다시 시도한다.
/// </summary>
public static class ImageHeaderReader
{
    public static bool TryRead(ReadOnlySpan<byte> data, out ImageHeaderInfo info)
    {
        info = default;
        try
        {
            if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature)) return TryPng(data, out info);
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return TryJpeg(data, out info);
            if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return TryGif(data, out info);
            if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M') return TryBmp(data, out info);
            if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return TryWebP(data, out info);
            if (data.Length >= 8 && (data[..4].SequenceEqual("II*\0"u8) || data[..4].SequenceEqual("MM\0*"u8))) return TryTiff(data, out info);
            if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8)) return TryHeif(data, out info);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 잘린 헤더
        }
        catch (IndexOutOfRangeException)
        {
        }
        info = default;
        return false;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ImageHeaderInfo Make(int w, int h, int orientation, string format)
    {
        if (orientation is >= 5 and <= 8) (w, h) = (h, w);
        return new ImageHeaderInfo(w, h, orientation is >= 1 and <= 8 ? orientation : 1, format);
    }

    private static bool TryPng(ReadOnlySpan<byte> d, out ImageHeaderInfo info)
    {
        info = default;
        if (!d.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        var w = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4));
        var h = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4));
        if (w <= 0 || h <= 0) return false;
        info = Make(w, h, 1, "png");
        return true;
    }

    private static bool TryGif(ReadOnlySpan<byte> d, out ImageHeaderInfo info)
    {
        var w = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(6, 2));
        var h = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(8, 2));
        info = Make(w, h, 1, "gif");
        return w > 0 && h > 0;
    }

    private static bool TryBmp(ReadOnlySpan<byte> d, out ImageHeaderInfo info)
    {
        info = default;
        if (d.Length < 26) return false;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(14, 4));
        int w, h;
        if (headerSize == 12)
        {
            w = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(18, 2));
            h = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(20, 2));
        }
        else
        {
            w = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(18, 4));
            h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d.Slice(22, 4)));
        }
        if (w <= 0 || h <= 0) return false;
        info = Make(w, h, 1, "bmp");
        return true;
    }

    private static bool TryWebP(ReadOnlySpan<byte> d, out ImageHeaderInfo info)
    {
        info = default;
        if (d.Length < 30) return false;
        var chunk = d.Slice(12, 4);
        int w, h;
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // frame tag(3) + start code 9D 01 2A + 14bit w/h
            if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return false;
            w = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF;
            h = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF;
        }
        else if (chunk.SequenceEqual("VP8L"u8))
        {
            if (d[20] != 0x2F) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(21, 4));
            w = (int)(bits & 0x3FFF) + 1;
            h = (int)((bits >> 14) & 0x3FFF) + 1;
        }
        else if (chunk.SequenceEqual("VP8X"u8))
        {
            w = (d[24] | d[25] << 8 | d[26] << 16) + 1;
            h = (d[27] | d[28] << 8 | d[29] << 16) + 1;
        }
        else
        {
            return false;
        }
        if (w <= 0 || h <= 0) return false;
        info = Make(w, h, 1, "webp");
        return true;
    }

    private static bool TryJpeg(ReadOnlySpan<byte> d, out ImageHeaderInfo info)
    {
        info = default;
        var orientation = 1;
        var i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker == 0xD9 || marker == 0xDA) return false; // SOS 전에 SOF가 없음
            var length = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 2, 2));
            if (length < 2) return false;
            var segStart = i + 4;
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (segStart + 5 > d.Length) return false;
                var h = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(segStart + 1, 2));
                var w = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(segStart + 3, 2));
                if (w == 0 || h == 0) return false;
                info = Make(w, h, orientation, "jpeg");
                return true;
            }
            if (marker == 0xE1 && segStart + 6 <= d.Length && d.Slice(segStart, 6).SequenceEqual("Exif\0\0"u8))
            {
                var segEnd = Math.Min(d.Length, i + 2 + length);
                if (segEnd > segStart + 6)
                {
                    var tiff = d[(segStart + 6)..segEnd];
                    if (TryReadTiffTags(tiff, out _, out _, out var o) && o is >= 1 and <= 8) orientation = o;
                }
            }
            i += 2 + length;
        }
        return false;
    }

    private static bool TryTiff(ReadOnlySpan<byte> d, out ImageHeaderInfo info)
    {
        info = default;
        if (!TryReadTiffTags(d, out var w, out var h, out var orientation) || w <= 0 || h <= 0) return false;
        info = Make(w, h, orientation, "tiff");
        return true;
    }

    /// <summary>TIFF IFD0의 width(256)/height(257)/orientation(274).</summary>
    private static bool TryReadTiffTags(ReadOnlySpan<byte> t, out int width, out int height, out int orientation)
    {
        width = height = 0;
        orientation = 1;
        if (t.Length < 8) return false;
        var little = t[0] == 'I';
        if (!little && t[0] != 'M') return false;
        var ifd = U32(t, 4, little);
        if (ifd < 8 || ifd + 2 > t.Length) return false;
        var count = U16(t, ifd, little);
        var found = false;
        for (var n = 0; n < count; n++)
        {
            var e = ifd + 2 + n * 12;
            if (e + 12 > t.Length) return found;
            var tag = U16(t, e, little);
            var type = U16(t, e + 2, little);
            int value = type == 3 ? U16(t, e + 8, little) : U32(t, e + 8, little);
            switch (tag)
            {
                case 256: width = value; found = true; break;
                case 257: height = value; found = true; break;
                case 274: orientation = value; found = true; break;
            }
        }
        return found;
    }

    private static int U16(ReadOnlySpan<byte> t, int o, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(t.Slice(o, 2)) : BinaryPrimitives.ReadUInt16BigEndian(t.Slice(o, 2));

    private static int U32(ReadOnlySpan<byte> t, int o, bool little)
        => (int)(little ? BinaryPrimitives.ReadUInt32LittleEndian(t.Slice(o, 4)) : BinaryPrimitives.ReadUInt32BigEndian(t.Slice(o, 4)));

    private static bool TryHeif(ReadOnlySpan<byte> d, out ImageHeaderInfo info)
    {
        info = default;
        var brand = d.Slice(8, 4);
        if (!(brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
              || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8) || brand.SequenceEqual("heif"u8) || brand.SequenceEqual("heim"u8)
              || brand.SequenceEqual("heis"u8) || brand.SequenceEqual("avif"u8)))
            return false;

        int bestW = 0, bestH = 0, rotation = 0;
        var sawMeta = false;
        ScanBoxes(d, 0, d.Length, ref bestW, ref bestH, ref rotation, ref sawMeta, 0);
        if (bestW <= 0 || bestH <= 0) return false;
        // irot은 반시계 방향 90°×angle. EXIF orientation 대응: 1→8, 2→3, 3→6
        var orientation = rotation switch { 1 => 8, 2 => 3, 3 => 6, _ => 1 };
        info = Make(bestW, bestH, orientation, "heif");
        return true;
    }

    private static void ScanBoxes(ReadOnlySpan<byte> d, int start, int end, ref int bestW, ref int bestH, ref int rotation, ref bool sawMeta, int depth)
    {
        if (depth > 6) return;
        var i = start;
        while (i + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i, 4));
            var type = d.Slice(i + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (i + 16 > end) return;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.Slice(i + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - i;
            }
            if (size < header) return;
            var boxEnd = (int)Math.Min(end, i + size);
            if (type.SequenceEqual("meta"u8))
            {
                sawMeta = true;
                ScanBoxes(d, i + header + 4, boxEnd, ref bestW, ref bestH, ref rotation, ref sawMeta, depth + 1);
            }
            else if (type.SequenceEqual("iprp"u8) || type.SequenceEqual("ipco"u8))
            {
                ScanBoxes(d, i + header, boxEnd, ref bestW, ref bestH, ref rotation, ref sawMeta, depth + 1);
            }
            else if (type.SequenceEqual("ispe"u8))
            {
                if (i + header + 12 <= boxEnd)
                {
                    var w = (int)BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i + header + 4, 4));
                    var h = (int)BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i + header + 8, 4));
                    if ((long)w * h > (long)bestW * bestH) { bestW = w; bestH = h; }
                }
            }
            else if (type.SequenceEqual("irot"u8))
            {
                if (i + header < boxEnd) rotation = d[i + header] & 0x3;
            }
            else if (type.SequenceEqual("mdat"u8) && !sawMeta)
            {
                return; // meta가 뒤에 있으면 데이터 부족
            }
            if (i + size > end) return;
            i = (int)(i + size);
        }
    }
}
