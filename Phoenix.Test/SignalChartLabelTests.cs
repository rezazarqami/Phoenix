using System.IO.Compression;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class SignalChartLabelTests
{
    public static void CompactLabelsPreserveCandles()
    {
        var candles = Enumerable.Range(0, 180).Select(i =>
        {
            var price = 100m + i * .13m + (decimal)Math.Sin(i / 9d) * 3;
            return new BybitKline(i * 60_000L, price - .2m, price + .6m, price - .6m, price, 10m);
        }).ToArray();
        var candidate = new SignalCandidate("NEARUSDT", "1", "Long", 123m, 102m, 124m,
            118m, 128m, 99m, null, null, 10m, 1m, 80m,
            candles[145].OpenTime, candles[20].OpenTime, 0, candles[^1].OpenTime, candles.Length, "", false, null);
        var waves = new List<ElliottWavePoint>();
        foreach (var (index, label) in new[] { (14, "W"), (42, "X"), (70, "Y"), (98, "A"), (126, "B"), (164, "C") })
            waves.Add(new(label, candles[index].OpenTime, candles[index].High) { Timeframe = "60", ValidationStatus = "Verified" });
        // Reproduce the crowded right edge with multiple degrees on one pivot.
        foreach (var tf in new[] { "M", "W", "D", "15" })
            waves.Add(new("A", candles[164].OpenTime, candles[164].High) { Timeframe = tf, ValidationStatus = "Verified" });
        var empty = new ElliottScenario("Bullish", 80m, [], [], 99m, 102m,
            new(.5m, 1.6m, .3m), "Impulse", "Developing", "5", "");
        var report = new ElliottCoverageReport(0, 1, 0, 0, [],
            [new(candles[0].OpenTime, candles[^1].OpenTime, "SubdivisionUnknown")]);
        empty = empty with { Coverage = report };
        // This fixture tests typography for accepted labels; unverified labels are tested separately.
        var scenario = empty with { Waves = waves, ValidationStatus = "Verified" };
        foreach (var mode in new[] { false, true })
        {
            var baseline = SignalChartRenderer.Render(candles, candidate, mode, "15M", 82m, 78m, empty);
            var rendered = SignalChartRenderer.Render(candles, candidate, mode, "15M", 82m, 78m, scenario);
            var original = Decode(baseline); var labeled = Decode(rendered);
            var changed = 0;
            for (var y = 25; y < 565; y++)
            for (var x = 30; x < 975; x++)
            {
                var p = (y * 1000 + x) * 3;
                var candle = original[p] == 16 && original[p + 1] == 155 && original[p + 2] == 112 ||
                    original[p] == 220 && original[p + 1] == 55 && original[p + 2] == 82 ||
                    original[p] == 14 && original[p + 1] == 125 && original[p + 2] == 96;
                if (candle && !original.AsSpan(p, 3).SequenceEqual(labeled.AsSpan(p, 3)))
                    throw new Exception("A wave label covered a candle or price line.");
                if (!original.AsSpan(p, 3).SequenceEqual(labeled.AsSpan(p, 3))) changed++;
                if (original.AsSpan(p, 3).ContainsAnyExcept((byte)255) &&
                    !labeled.AsSpan(p, 3).ContainsAnyExcept((byte)255))
                    throw new Exception("A label background erased chart detail.");
            }
            // Coverage diagnostics occupy their own footer row and do not
            // disappear when no valid count is available.
            var noCount = Decode(SignalChartRenderer.Render(candles, candidate, mode, "15M", 82m, 78m,
                ElliottCoverage.Unavailable(report)));
            var footerInk = 0;
            for (var y = 697; y < 712; y++)
            for (var x = 30; x < 975; x++)
            {
                var p = (y * 1000 + x) * 3;
                if (!noCount.AsSpan(p, 3).SequenceEqual(new byte[] { 255, 255, 255 })) footerInk++;
            }
            if (footerInk < 100) throw new Exception("No-count diagnostics are missing from the footer.");
            if (changed < 150 || changed > 2500) throw new Exception($"Unexpected label ink area: {changed}.");
            var output = Environment.GetEnvironmentVariable("PHOENIX_CHART_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllBytes(Path.Combine(output, mode ? "elliott-line.png" : "elliott-candles.png"), rendered);
            }
        }
        foreach (var tf in new[] { "M", "W", "D", "60", "15" })
        {
            var wave = new ElliottWavePoint("A", 0, 100m) { Timeframe = tf };
            var height = SignalChartRenderer.WaveLabelHeight(wave, "15");
            if (height < 9 || height > 19 || SignalChartRenderer.WaveLabelHeight(wave with { Degree = 2 }, "15") >= height)
                throw new Exception("Label hierarchy must stay compact.");
        }
    }

    private static byte[] Decode(byte[] png)
    {
        using var compressed = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (System.Text.Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
                compressed.Write(png, offset + 8, length);
            offset += length + 12;
        }
        compressed.Position = 0;
        using var z = new ZLibStream(compressed, CompressionMode.Decompress);
        var pixels = new byte[1000 * 730 * 3];
        for (var y = 0; y < 730; y++)
        {
            if (z.ReadByte() != 0) throw new Exception("Unexpected PNG filter.");
            z.ReadExactly(pixels.AsSpan(y * 3000, 3000));
        }
        return pixels;
    }
}
