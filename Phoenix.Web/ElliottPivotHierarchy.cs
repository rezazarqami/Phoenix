namespace Phoenix.Web;

/// <summary>Structural degrees of the same close path, independent of candle interval.</summary>
public static class ElliottPivotHierarchy
{
    public static IEnumerable<ElliottPivot[]> Degrees(IReadOnlyList<ElliottPivot> raw, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var points = raw.ToList();
        var lastCount = points.Count;
        yield return points.ToArray();
        while (points.Count > 4)
        {
            token.ThrowIfCancellationRequested();
            var index = SmallestNestedPair(points);
            if (index < 0) yield break;
            points.RemoveRange(index, 2);
            // Keep every coarse degree near the five/three-wave counts. At
            // fine resolutions, sample by turn count to bound candidate work.
            if (points.Count <= 24 || points.Count <= lastCount * .75)
            {
                lastCount = points.Count;
                yield return points.ToArray();
            }
        }
    }

    public static int SmallestNestedPair(IReadOnlyList<ElliottPivot> points)
    {
        var index = -1;
        var smallest = decimal.MaxValue;
        for (var i = 1; i < points.Count - 2; i++)
        {
            var low = Math.Min(points[i - 1].Price, points[i + 2].Price);
            var high = Math.Max(points[i - 1].Price, points[i + 2].Price);
            // A removal may group an internal retracement but cannot erase
            // an extreme outside the enclosing leg or either endpoint.
            if (points[i].Price < low || points[i].Price > high ||
                points[i + 1].Price < low || points[i + 1].Price > high) continue;
            var amplitude = Math.Abs(points[i + 1].Price - points[i].Price);
            if (amplitude < smallest) { smallest = amplitude; index = i; }
        }
        return index;
    }
}
