namespace InvoLab;

public sealed class ScoringConfig
{
    // Component weights (renormalised over whichever components have data)
    public double WPlatform { get; set; } = 0.15;      // Invo's own 0-10 score
    public double WRiskAdjusted { get; set; } = 0.25;  // 90d return / 90d max drawdown
    public double WConsistency { get; set; } = 0.15;   // share of winning weeks (90d)
    public double WSharpe { get; set; } = 0.20;        // from the daily equity curve (90d)
    public double WTrack { get; set; } = 0.10;         // number of closed trades + portfolio age
    public double WDrawdownNow { get; set; } = 0.15;   // don't copy someone mid-meltdown

    // Hard filters
    public int MinAgeDays { get; set; } = 45;
    public int MinClosedTrades { get; set; } = 30;
    public bool ExcludeFlagged { get; set; } = true;

    // Soft penalties
    public double DrawdownSoftLimit { get; set; } = 50;  // % - above this the score is x0.6
    public double LeverageComfort { get; set; } = 10;    // above this, linear penalty
    public double LeverageMax { get; set; } = 50;        // at/above this the score is x0.7
}

public sealed record CurveStats(double Sharpe, double CurrentDrawdownPct, double MaxDrawdownPct);

public static class Curve
{
    /// <summary>
    /// Stats over the trailing window ending at <paramref name="asOf"/>, using only data up to that
    /// moment (so the same function can later be used for backtesting).
    /// Returns null when the curve is too short or stale to be trusted.
    /// </summary>
    public static CurveStats? Compute(IReadOnlyList<HistoryPoint> points, DateTimeOffset asOf, int windowDays = 90)
    {
        var upto = points.Where(p => p.SnapshotAt <= asOf && p.PlSnapshot.HasValue).ToList();
        if (upto.Count < 10) return null;
        if ((asOf - upto[^1].SnapshotAt).TotalDays > 3) return null; // stale / incomplete paging

        var first = upto[0].SnapshotAt.UtcDateTime.Date;
        var windowStart = asOf.AddDays(-windowDays).UtcDateTime.Date;
        var from = windowStart > first ? windowStart : first;
        var to = asOf.UtcDateTime.Date;

        // Daily, forward-filled equity (100 + cumulative %)
        var equity = new List<double>();
        var idx = 0;
        var current = 100 + upto[0].PlSnapshot!.Value;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            while (idx < upto.Count && upto[idx].SnapshotAt.UtcDateTime.Date <= d)
            {
                current = 100 + upto[idx].PlSnapshot!.Value;
                idx++;
            }
            equity.Add(current);
        }

        if (equity.Count < 14 || equity.Any(e => e <= 0)) return null;

        var returns = new List<double>();
        for (var i = 1; i < equity.Count; i++) returns.Add(equity[i] / equity[i - 1] - 1);

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var std = Math.Sqrt(variance);
        var sharpe = std < 1e-9 ? 0 : mean / std * Math.Sqrt(365);

        var peak = equity[0];
        var maxDd = 0.0;
        foreach (var e in equity)
        {
            peak = Math.Max(peak, e);
            maxDd = Math.Max(maxDd, (peak - e) / peak * 100);
        }
        var currentDd = (peak - equity[^1]) / peak * 100;

        if (double.IsNaN(sharpe) || double.IsInfinity(sharpe)) return null;
        return new CurveStats(sharpe, currentDd, maxDd);
    }
}

public static class Scorer
{
    private static double Clamp01(double x) => Math.Min(1, Math.Max(0, x));

    /// <summary>Fills Total / Components / Flags / ExcludedReason from the raw metrics already on <paramref name="t"/>.</summary>
    public static void Apply(TraderAnalysis t, ScoringConfig c)
    {
        t.Components = new Dictionary<string, double?>();
        t.Flags = new List<string>();
        t.ExcludedReason = null;
        t.Total = 0;

        if (t.FetchError is not null) { t.ExcludedReason = "data unavailable"; return; }
        if (!t.ScoreComplete) t.ExcludedReason = "platform score still processing";
        else if (t.Liquidated) t.ExcludedReason = "portfolio was liquidated";
        else if (c.ExcludeFlagged && t.Flagged) t.ExcludedReason = "account flagged by platform";
        else if ((t.AgeDays ?? 0) < c.MinAgeDays) t.ExcludedReason = $"younger than {c.MinAgeDays} days";
        else if ((t.ClosedTrades ?? 0) < c.MinClosedTrades) t.ExcludedReason = $"fewer than {c.MinClosedTrades} closed trades";
        else if ((t.NetReturn90d ?? 0) <= 0) t.ExcludedReason = "not profitable over 90 days";
        if (t.ExcludedReason is not null) return;

        double num = 0, den = 0;
        void Add(string name, double? value, double weight)
        {
            t.Components[name] = value.HasValue ? Math.Round(value.Value, 3) : null;
            if (value.HasValue) { num += value.Value * weight; den += weight; }
        }

        Add("platform", t.PlatformScore.HasValue ? Clamp01(t.PlatformScore.Value / 10.0) : null, c.WPlatform);

        var dd = Math.Max(t.MaxDrawdown90d ?? 100, 10);
        Add("riskAdjusted", Clamp01((t.NetReturn90d ?? 0) / dd / 5.0), c.WRiskAdjusted);

        double? consistency = null;
        if (t.ActiveWeeks90d is > 0 && t.WinningWeeks90d.HasValue)
            consistency = (double)t.WinningWeeks90d.Value / t.ActiveWeeks90d.Value * Math.Min(1.0, t.ActiveWeeks90d.Value / 12.0);
        Add("consistency", consistency, c.WConsistency);

        Add("sharpe", t.Sharpe90.HasValue ? Clamp01(t.Sharpe90.Value / 4.0) : null, c.WSharpe);

        var track = 0.5 * Math.Min(1.0, (t.ClosedTrades ?? 0) / 500.0) + 0.5 * Math.Min(1.0, (t.AgeDays ?? 0) / 180.0);
        Add("track", track, c.WTrack);

        Add("notInDrawdown", t.CurrentDrawdownPct.HasValue ? 1 - Clamp01(t.CurrentDrawdownPct.Value / 30.0) : null, c.WDrawdownNow);

        var mult = 1.0;

        if (t.MaxDrawdown90d > c.DrawdownSoftLimit)
        {
            mult *= 0.6;
            t.Flags.Add($"deep drawdown ({t.MaxDrawdown90d:0}%)");
        }

        if (t.MaxOpenLeverage is double lev && lev > c.LeverageComfort)
        {
            var f = Clamp01((lev - c.LeverageComfort) / (c.LeverageMax - c.LeverageComfort));
            mult *= 1 - 0.3 * f;
            t.Flags.Add($"{lev:0}x leverage open");
        }

        if (t.WinRate > 90 && t.MaxDrawdown90d > 20)
            t.Flags.Add("very high win rate with large drawdowns (many small wins, rare big losses?)");
        if (!t.Sharpe90.HasValue) t.Flags.Add("equity curve incomplete");
        if (t.OpenPositionsUnavailable) t.Flags.Add("open positions unavailable");

        t.Total = den > 0 ? Math.Round(100 * num / den * mult, 2) : 0;
    }
}
