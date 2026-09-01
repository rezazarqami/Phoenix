using System.IO.Compression;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public static class SignalChartRenderer
{
    public static byte[] Render(IReadOnlyList<BybitKline> candles, SignalCandidate candidate, bool lineMode,
        string? timeframeBadge = null)
    {
        const int width = 1000, height = 600, left = 30, right = 25, top = 25, bottom = 35;
        var ceilingIndex = FindNearestIndex(candles, candidate.CeilingTime);
        var floorIndex = FindNearestIndex(candles, candidate.FloorTime);
        var firstAnchor = Math.Min(ceilingIndex, floorIndex);
        var secondAnchor = Math.Max(ceilingIndex, floorIndex);
        var anchorSpan = Math.Max(1, secondAnchor - firstAnchor + 1);
        // Keep the selected anchors readable while showing enough market context
        // on both sides for Telegram review. This affects framing only.
        var padding = Math.Clamp(anchorSpan * 5 / 4, 150, 450);
        var viewStart = Math.Max(0, firstAnchor - padding);
        var viewEnd = Math.Min(candles.Count - 1, secondAnchor + padding);
        candles = candles.Skip(viewStart).Take(viewEnd - viewStart + 1).ToArray();
        var pixels = new byte[width * height * 3];
        Fill(pixels, 255, 255, 255);
        var min = candles.Min(x => lineMode ? x.Close : x.Low);
        var max = candles.Max(x => lineMode ? x.Close : x.High);
        min = Math.Min(min, Math.Min(candidate.Floor, Math.Min(candidate.TakeProfit, candidate.StopLoss)));
        max = Math.Max(max, Math.Max(candidate.Ceiling, Math.Max(candidate.TakeProfit, candidate.StopLoss)));
        var span = Math.Max(max - min, 0.00000001m);
        int X(int index) => left + (int)Math.Round(index * (width - left - right - 1d) / Math.Max(1, candles.Count - 1));
        int Y(decimal price) => top + (int)Math.Round((double)((max - price) / span) * (height - top - bottom - 1));
        for (var grid = 1; grid < 6; grid++) DrawLine(pixels, width, height, left, top + grid * (height - top - bottom) / 6, width - right, top + grid * (height - top - bottom) / 6, 235, 239, 237);
        if (lineMode)
            for (var i = 1; i < candles.Count; i++) DrawLine(pixels, width, height, X(i - 1), Y(candles[i - 1].Close), X(i), Y(candles[i].Close), 14, 125, 96, 2);
        else
            for (var i = 0; i < candles.Count; i++)
            {
                var candle = candles[i]; var up = candle.Close >= candle.Open;
                var color = up ? (R: (byte)16, G: (byte)155, B: (byte)112) : (R: (byte)220, G: (byte)55, B: (byte)82);
                var x = X(i); DrawLine(pixels, width, height, x, Y(candle.High), x, Y(candle.Low), color.R, color.G, color.B);
                FillRect(pixels, width, height, x - 1, Math.Min(Y(candle.Open), Y(candle.Close)), 3, Math.Max(2, Math.Abs(Y(candle.Open) - Y(candle.Close))), color.R, color.G, color.B);
            }
        Level(candidate.Ceiling, 240, 185, 11); Level(candidate.Floor, 169, 108, 242);
        Level(candidate.EntryPrice, 70, 166, 255); Level(candidate.TakeProfit, 56, 211, 159); Level(candidate.StopLoss, 255, 97, 117);
        if (!string.IsNullOrWhiteSpace(timeframeBadge)) DrawBadge(pixels, width, height, timeframeBadge);
        return EncodePng(pixels, width, height);

        void Level(decimal value, byte r, byte g, byte b) => DrawLine(pixels, width, height, left, Y(value), width - right, Y(value), r, g, b, 2);
    }

    private static int FindNearestIndex(IReadOnlyList<BybitKline> candles, long time)
    {
        var best = 0; var distance = long.MaxValue;
        for (var i = 0; i < candles.Count; i++) { var current = Math.Abs(candles[i].OpenTime - time); if (current >= distance) continue; best = i; distance = current; }
        return best;
    }

    private static void Fill(byte[] pixels, byte r, byte g, byte b) { for (var i = 0; i < pixels.Length; i += 3) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; } }
    private static void FillRect(byte[] p, int w, int h, int x, int y, int rw, int rh, byte r, byte g, byte b) { for (var yy = y; yy < y + rh; yy++) for (var xx = x; xx < x + rw; xx++) Put(p, w, h, xx, yy, r, g, b); }
    private static void DrawLine(byte[] p, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b, int thickness = 1)
    {
        var dx = Math.Abs(x1 - x0); var sx = x0 < x1 ? 1 : -1; var dy = -Math.Abs(y1 - y0); var sy = y0 < y1 ? 1 : -1; var error = dx + dy;
        while (true) { FillRect(p, w, h, x0 - thickness / 2, y0 - thickness / 2, thickness, thickness, r, g, b); if (x0 == x1 && y0 == y1) break; var twice = 2 * error; if (twice >= dy) { error += dy; x0 += sx; } if (twice <= dx) { error += dx; y0 += sy; } }
    }
    private static void Put(byte[] p, int w, int h, int x, int y, byte r, byte g, byte b) { if (x < 0 || y < 0 || x >= w || y >= h) return; var i = (y * w + x) * 3; p[i] = r; p[i + 1] = g; p[i + 2] = b; }
    private static void DrawBadge(byte[] pixels, int width, int height, string text)
    {
        const int scale = 4, glyphWidth = 5, gap = 1, padding = 9;
        text = text.ToUpperInvariant();
        var badgeWidth = padding * 2 + text.Length * glyphWidth * scale + Math.Max(0, text.Length - 1) * gap * scale;
        var badgeHeight = padding * 2 + 7 * scale;
        var x = width - badgeWidth - 18;
        const int y = 14;
        FillRect(pixels, width, height, x, y, badgeWidth, badgeHeight, 19, 16, 10);
        for (var border = 0; border < 2; border++)
        {
            DrawLine(pixels, width, height, x + border, y + border, x + badgeWidth - 1 - border, y + border, 224, 174, 61);
            DrawLine(pixels, width, height, x + border, y + badgeHeight - 1 - border, x + badgeWidth - 1 - border, y + badgeHeight - 1 - border, 224, 174, 61);
            DrawLine(pixels, width, height, x + border, y + border, x + border, y + badgeHeight - 1 - border, 224, 174, 61);
            DrawLine(pixels, width, height, x + badgeWidth - 1 - border, y + border, x + badgeWidth - 1 - border, y + badgeHeight - 1 - border, 224, 174, 61);
        }
        var cursor = x + padding;
        foreach (var character in text)
        {
            var rows = Glyph(character);
            for (var row = 0; row < rows.Length; row++)
                for (var column = 0; column < glyphWidth; column++)
                    if ((rows[row] & (1 << (glyphWidth - 1 - column))) != 0)
                        FillRect(pixels, width, height, cursor + column * scale, y + padding + row * scale,
                            scale, scale, 246, 211, 112);
            cursor += (glyphWidth + gap) * scale;
        }
    }

    private static int[] Glyph(char character) => character switch
    {
        '0' => [14, 17, 19, 21, 25, 17, 14], '1' => [4, 12, 4, 4, 4, 4, 14],
        '2' => [14, 17, 1, 2, 4, 8, 31], '3' => [30, 1, 1, 14, 1, 1, 30],
        '4' => [2, 6, 10, 18, 31, 2, 2], '5' => [31, 16, 16, 30, 1, 1, 30],
        '6' => [14, 16, 16, 30, 17, 17, 14], '7' => [31, 1, 2, 4, 8, 8, 8],
        '8' => [14, 17, 17, 14, 17, 17, 14], '9' => [14, 17, 17, 15, 1, 1, 14],
        'M' => [17, 27, 21, 21, 17, 17, 17], 'H' => [17, 17, 17, 31, 17, 17, 17],
        _ => [0, 0, 0, 0, 0, 0, 0]
    };
    private static byte[] EncodePng(byte[] pixels, int width, int height)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        using var header = new MemoryStream(); WriteInt(header, width); WriteInt(header, height); header.Write(new byte[] { 8, 2, 0, 0, 0 }); WriteChunk(output, "IHDR", header.ToArray());
        using var raw = new MemoryStream(); for (var y = 0; y < height; y++) { raw.WriteByte(0); raw.Write(pixels, y * width * 3, width * 3); }
        using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) z.Write(raw.ToArray());
        WriteChunk(output, "IDAT", compressed.ToArray()); WriteChunk(output, "IEND", []); return output.ToArray();
    }
    private static void WriteChunk(Stream output, string type, byte[] data) { WriteInt(output, data.Length); var name = System.Text.Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data); var crcData = name.Concat(data).ToArray(); WriteInt(output, unchecked((int)Crc32(crcData))); }
    private static void WriteInt(Stream stream, int value) { stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
    private static uint Crc32(byte[] data) { uint crc = 0xffffffff; foreach (var value in data) { crc ^= value; for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); } return ~crc; }
}
