using System.IO.Compression;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public static class SignalChartRenderer
{
    public static byte[] Render(IReadOnlyList<BybitKline> candles, SignalCandidate candidate, bool lineMode,
        string? timeframeBadge = null, decimal? targetSimilarity = null, decimal? stopSimilarity = null,
        ElliottScenario? elliott = null)
    {
        const int width = 1000, height = 730, left = 30, right = 25, top = 25, bottom = 165;
        var footerTop = height - bottom + 14;
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
        // Keep the validated count's time span visible even though its origin
        // is implicit in the preceding structure and is never labelled "0".
        if (elliott is not null && elliott.Waves.Count > 0)
        {
            var allWaves = elliott.ContextWaves.Concat(elliott.Waves).Concat(elliott.Subwaves).ToArray();
            var waveStart = FindNearestIndex(candles, allWaves.MinBy(x => x.Time)!.Time);
            var waveEnd = FindNearestIndex(candles, allWaves.MaxBy(x => x.Time)!.Time);
            var wavePadding = Math.Clamp((waveEnd - waveStart + 1) / 8, 8, 80);
            viewStart = Math.Min(viewStart, Math.Max(0, waveStart - wavePadding));
            viewEnd = Math.Max(viewEnd, Math.Min(candles.Count - 1, waveEnd + wavePadding));
        }
        candles = candles.Skip(viewStart).Take(viewEnd - viewStart + 1).ToArray();
        var pixels = new byte[width * height * 3];
        Fill(pixels, 255, 255, 255);
        var min = candles.Min(x => lineMode ? x.Close : x.Low);
        var max = candles.Max(x => lineMode ? x.Close : x.High);
        min = Math.Min(min, Math.Min(candidate.Floor, Math.Min(candidate.TakeProfit, candidate.StopLoss)));
        max = Math.Max(max, Math.Max(candidate.Ceiling, Math.Max(candidate.TakeProfit, candidate.StopLoss)));
        int X(int index) => left + (int)Math.Round(index * (width - left - right - 1d) / Math.Max(1, candles.Count - 1));
        int Y(decimal price) => top + (int)Math.Round(
            LogarithmicYFraction(min, max, price) * (height - top - bottom - 1));
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
        if (elliott is not null)
        {
            var occupied = new List<(int Left, int Top, int Right, int Bottom)>();
            var imageInterval = elliott.Waves.FirstOrDefault()?.Timeframe;
            // Monthly, weekly, daily and hourly colors are fixed regardless
            // of the image interval. A shorter active interval is a fifth degree.
            var macroWaves = elliott.ContextWaves.Concat(elliott.Waves)
                .Where(w => ShouldDisplayWaveLabel(w) && WaveStyle(w.Timeframe, imageInterval).Visible)
                .OrderBy(w => WavePriority(w.Timeframe, imageInterval))
                .GroupBy(w => $"{w.Time}|{w.Label}|{w.Price}|{w.Degree}").Select(g => g.First()).ToArray();
            var wavePoints = macroWaves.Where(w => w.Time >= candles[0].OpenTime && w.Time <= candles[^1].OpenTime)
                .Select(w => (Wave: w, Index: FindNearestIndex(candles, w.Time))).ToArray();
            // Keep the major count readable; an occupied spot is never painted over.
            foreach (var point in wavePoints)
            {
                var x = X(point.Index); var y = Y(point.Wave.Price);
                var style = WaveStyle(point.Wave.Timeframe, imageInterval);
                TryDrawWaveLabel(point.Wave.Label, x, y, style.Scale, style.R, style.G, style.B);
            }

            // All validated subdivisions remain in the analysis. Show only a
            // sparse sample when their parent leg has enough horizontal space.
            var visibleDetails = elliott.Subwaves.GroupBy(x => x.Parent)
                .SelectMany(group =>
                {
                    var children = group.OrderBy(x => x.Time).ToArray();
                    if (children.Length < 2) return Enumerable.Empty<ElliottWavePoint>();
                    var span = X(FindNearestIndex(candles, children[^1].Time)) -
                        X(FindNearestIndex(candles, children[0].Time));
                    if (span < 90)
                        return Enumerable.Empty<ElliottWavePoint>();
                    return children.Skip(1).Where((w, index) =>
                        ShouldDisplayWaveLabel(w) && (index == 0 || w == children[^1] ||
                            (span >= 180 && index % 2 == 0)));
                })
                .Where(w => imageInterval is not ("15" or "5") &&
                    w.Time >= candles[0].OpenTime && w.Time <= candles[^1].OpenTime)
                .TakeLast(10).OrderByDescending(w => w.Time);
            foreach (var wave in visibleDetails)
            {
                var index = FindNearestIndex(candles, wave.Time);
                TryDrawWaveLabel(wave.Label.ToLowerInvariant(), X(index), Y(wave.Price), 2, 111, 63, 145);
            }

            void TryDrawWaveLabel(string label, int x, int y, int scale, byte r, byte g, byte b)
            {
                var textWidth = label.Length * 6 * scale;
                var leftEdge = x - textWidth / 2;
                foreach (var topEdge in new[] { y - 13 - 7 * scale, y + 12 })
                {
                    var box = (Left: leftEdge - 3, Top: topEdge - 3,
                        Right: leftEdge + textWidth + 3, Bottom: topEdge + 7 * scale + 3);
                    if (box.Left < left || box.Right > width - right || box.Top < top || box.Bottom > height - bottom ||
                        occupied.Any(other => box.Left < other.Right && box.Right > other.Left &&
                            box.Top < other.Bottom && box.Bottom > other.Top)) continue;
                    DrawTinyText(pixels, width, height, leftEdge, topEdge, label, scale, r, g, b);
                    occupied.Add(box);
                    return;
                }
            }
        }
        Level(candidate.Ceiling, 240, 185, 11); Level(candidate.Floor, 169, 108, 242);
        Level(candidate.EntryPrice, 70, 166, 255); Level(candidate.TakeProfit, 56, 211, 159); Level(candidate.StopLoss, 255, 97, 117);
        DrawLine(pixels, width, height, 0, footerTop - 8, width - 1, footerTop - 8, 218, 222, 225, 2);
        DrawBadge(pixels, width, height,
            string.IsNullOrWhiteSpace(timeframeBadge) ? "LOG" : $"{timeframeBadge} LOG", footerTop + 28);
        if (elliott is not null)
        {
            DrawTinyText(pixels, width, height, 385, footerTop + 30, "M", 2, 18, 130, 65);
            DrawTinyText(pixels, width, height, 440, footerTop + 30, "W", 2, 30, 85, 210);
            DrawTinyText(pixels, width, height, 495, footerTop + 30, "D", 2, 205, 50, 62);
            DrawTinyText(pixels, width, height, 550, footerTop + 30,
                elliott.Waves.FirstOrDefault()?.Timeframe is "15" or "5" ? "1H" : timeframeBadge ?? "",
                2, 25, 25, 25);
            if (elliott.Waves.FirstOrDefault()?.Timeframe is "15" or "5")
                DrawTinyText(pixels, width, height, 625, footerTop + 30,
                    timeframeBadge ?? "", 2, 111, 63, 145);
        }
        if (targetSimilarity.HasValue)
            DrawSimilarityBar(pixels, width, height, 18, footerTop, "TP", targetSimilarity.Value, 31, 170, 118);
        if (stopSimilarity.HasValue)
            DrawSimilarityBar(pixels, width, height, 18, footerTop + 52, "SL", stopSimilarity.Value, 220, 55, 82);
        return EncodePng(pixels, width, height);

        void Level(decimal value, byte r, byte g, byte b) => DrawLine(pixels, width, height, left, Y(value), width - right, Y(value), r, g, b, 2);
    }

    public static double LogarithmicYFraction(decimal min, decimal max, decimal price)
    {
        if (min <= 0m || max < min || price <= 0m)
            throw new ArgumentOutOfRangeException(nameof(price), "Logarithmic chart prices must be positive.");
        if (max == min) return 0.5d;
        var logMin = Math.Log((double)min);
        var logMax = Math.Log((double)max);
        var fraction = (logMax - Math.Log((double)price)) / (logMax - logMin);
        return Math.Clamp(fraction, 0d, 1d);
    }

    public static bool ShouldDisplayWaveLabel(ElliottWavePoint wave) =>
        !string.IsNullOrWhiteSpace(wave.Label) && wave.Label != "0";

    public static (byte R, byte G, byte B, int Scale, bool Visible) WaveStyle(
        string? timeframe, string? imageTimeframe) => timeframe switch
    {
        "M" => (18, 130, 65, 6, true),
        "W" => (30, 85, 210, 5, true),
        "D" => (205, 50, 62, 4, true),
        "60" => (25, 25, 25, 3, true),
        "15" or "5" when timeframe == imageTimeframe => (111, 63, 145, 2, true),
        null => (25, 25, 25, 3, true),
        _ when timeframe == imageTimeframe => (25, 25, 25, 3, true),
        _ => (25, 25, 25, 3, false)
    };

    private static int WavePriority(string? timeframe, string? imageTimeframe) => timeframe switch
    {
        "M" => 0, "W" => 1, "D" => 2, "60" => 3,
        _ when timeframe == imageTimeframe => 4,
        _ => 5
    };

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
    private static void DrawBadge(byte[] pixels, int width, int height, string text, int y)
    {
        const int scale = 4, glyphWidth = 5, gap = 1, padding = 9;
        text = text.ToUpperInvariant();
        var badgeWidth = padding * 2 + text.Length * glyphWidth * scale + Math.Max(0, text.Length - 1) * gap * scale;
        var badgeHeight = padding * 2 + 7 * scale;
        var x = width - badgeWidth - 18;
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
    private static void DrawMetricBadge(byte[] pixels, int width, int height, int x, int y, string text,
        byte backgroundR, byte backgroundG, byte backgroundB, byte textR, byte textG, byte textB)
    {
        const int scale = 4, glyphWidth = 5, gap = 1, padding = 9;
        var badgeWidth = padding * 2 + text.Length * glyphWidth * scale + Math.Max(0, text.Length - 1) * gap * scale;
        var badgeHeight = padding * 2 + 7 * scale;
        FillRect(pixels, width, height, x, y, badgeWidth, badgeHeight, backgroundR, backgroundG, backgroundB);
        for (var border = 0; border < 2; border++)
        {
            DrawLine(pixels, width, height, x + border, y + border, x + badgeWidth - 1 - border, y + border, textR, textG, textB);
            DrawLine(pixels, width, height, x + border, y + badgeHeight - 1 - border, x + badgeWidth - 1 - border, y + badgeHeight - 1 - border, textR, textG, textB);
            DrawLine(pixels, width, height, x + border, y + border, x + border, y + badgeHeight - 1 - border, textR, textG, textB);
            DrawLine(pixels, width, height, x + badgeWidth - 1 - border, y + border, x + badgeWidth - 1 - border, y + badgeHeight - 1 - border, textR, textG, textB);
        }
        var cursor = x + padding;
        foreach (var character in text.ToUpperInvariant())
        {
            var rows = Glyph(character);
            for (var row = 0; row < rows.Length; row++)
                for (var column = 0; column < glyphWidth; column++)
                    if ((rows[row] & (1 << (glyphWidth - 1 - column))) != 0)
                        FillRect(pixels, width, height, cursor + column * scale, y + padding + row * scale,
                            scale, scale, textR, textG, textB);
            cursor += (glyphWidth + gap) * scale;
        }
    }

    private static void DrawSimilarityBar(byte[] pixels, int width, int height, int x, int y,
        string label, decimal score, byte fillR, byte fillG, byte fillB)
    {
        const int barWidth = 340, barHeight = 42, scale = 3, glyphWidth = 5, gap = 1;
        score = Math.Clamp(score, 0m, 100m);
        FillRect(pixels, width, height, x, y, barWidth, barHeight, 24, 28, 34);
        var fillWidth = (int)Math.Round((barWidth - 4) * (double)score / 100d);
        FillRect(pixels, width, height, x + 2, y + 2, fillWidth, barHeight - 4, fillR, fillG, fillB);
        DrawLine(pixels, width, height, x, y, x + barWidth, y, 238, 238, 238);
        DrawLine(pixels, width, height, x, y + barHeight, x + barWidth, y + barHeight, 238, 238, 238);
        DrawLine(pixels, width, height, x, y, x, y + barHeight, 238, 238, 238);
        DrawLine(pixels, width, height, x + barWidth, y, x + barWidth, y + barHeight, 238, 238, 238);
        var text = $"{label} {score:0.0}%";
        var cursor = x + 10;
        foreach (var character in text)
        {
            var rows = Glyph(character);
            for (var row = 0; row < rows.Length; row++)
                for (var column = 0; column < glyphWidth; column++)
                    if ((rows[row] & (1 << (glyphWidth - 1 - column))) != 0)
                        FillRect(pixels, width, height, cursor + column * scale, y + 10 + row * scale,
                            scale, scale, 255, 255, 255);
            cursor += (glyphWidth + gap) * scale;
        }
    }

    private static void DrawTinyText(byte[] pixels, int width, int height, int x, int y, string text,
        int scale, byte r, byte g, byte b)
    {
        foreach (var character in text)
        {
            var rows = Glyph(character);
            for (var row = 0; row < rows.Length; row++)
                for (var column = 0; column < 5; column++)
                    if ((rows[row] & (1 << (4 - column))) != 0)
                        FillRect(pixels, width, height, x + column * scale, y + row * scale, scale, scale, r, g, b);
            x += 6 * scale;
        }
    }

    private static int[] Glyph(char character) => character switch
    {
        '0' => [14, 17, 19, 21, 25, 17, 14], '1' => [4, 12, 4, 4, 4, 4, 14],
        '2' => [14, 17, 1, 2, 4, 8, 31], '3' => [30, 1, 1, 14, 1, 1, 30],
        '4' => [2, 6, 10, 18, 31, 2, 2], '5' => [31, 16, 16, 30, 1, 1, 30],
        '6' => [14, 16, 16, 30, 17, 17, 14], '7' => [31, 1, 2, 4, 8, 8, 8],
        '8' => [14, 17, 17, 14, 17, 17, 14], '9' => [14, 17, 17, 15, 1, 1, 14],
        'a' => [0, 0, 14, 1, 15, 17, 15], 'b' => [16, 16, 30, 17, 17, 17, 30],
        'c' => [0, 0, 15, 16, 16, 16, 15], 'd' => [1, 1, 15, 17, 17, 17, 15],
        'e' => [0, 0, 14, 17, 31, 16, 14],
        'A' => [14, 17, 17, 31, 17, 17, 17], 'B' => [30, 17, 17, 30, 17, 17, 30],
        'C' => [14, 17, 16, 16, 16, 17, 14], 'D' => [30, 17, 17, 17, 17, 17, 30],
        'E' => [31, 16, 16, 30, 16, 16, 31],
        'M' => [17, 27, 21, 21, 17, 17, 17], 'W' => [17, 17, 17, 21, 21, 21, 10],
        'H' => [17, 17, 17, 31, 17, 17, 17],
        'L' => [16, 16, 16, 16, 16, 16, 31], 'O' => [14, 17, 17, 17, 17, 17, 14],
        'G' => [14, 17, 16, 23, 17, 17, 15], 'T' => [31, 4, 4, 4, 4, 4, 4],
        'P' => [30, 17, 17, 30, 16, 16, 16], 'S' => [15, 16, 16, 14, 1, 1, 30],
        '%' => [17, 2, 4, 8, 17, 0, 0], '.' => [0, 0, 0, 0, 0, 12, 12],
        ' ' => [0, 0, 0, 0, 0, 0, 0],
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
