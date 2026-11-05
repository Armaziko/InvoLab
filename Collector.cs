namespace InvoLab;

/// <summary>Fetches the top traders, enriches each one, and ranks them with <see cref="Scorer"/>.</summary>
public sealed class Collector(InvoClient api, ScoringConfig cfg)
{
    private const int ShortlistSize = 15;

    public async Task<Leaderboard> RunAsync(string requesterUserId, int limit, DateTimeOffset now, CancellationToken ct)
    {
        var top = await api.GetTopAsync(requesterUserId, limit, ct);
        var items = (top.Items ?? new List<ScoreboardItem>())
            .Where(i => i.User?.Id is not null && i.TopPortfolio?.Id is not null)
            .ToList();
        Console.WriteLine($"Scoreboard: {items.Count} traders");

        var traders = (await Task.WhenAll(items.Select(i => AnalyzeAsync(i, now, ct)))).ToList();
        foreach (var t in traders) Scorer.Apply(t, cfg);

        // Second pass: look at open positions (leverage) for the shortlist only, then re-score.
        var shortlist = traders.Where(t => t.ExcludedReason is null)
                               .OrderByDescending(t => t.Total)
                               .Take(ShortlistSize)
                               .ToList();
        await Task.WhenAll(shortlist.Select(async t =>
        {
            try
            {
                var open = await api.GetOpenInvestmentsAsync(t.PortfolioId, ct);
                t.MaxOpenLeverage = open.Count == 0 ? 0 : open.Max(o => o.Leverage ?? 1);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                t.OpenPositionsUnavailable = true;
            }
        }));
        foreach (var t in shortlist) Scorer.Apply(t, cfg);

        var ranked = traders.OrderBy(t => t.ExcludedReason is not null)
                            .ThenByDescending(t => t.Total)
                            .ThenBy(t => t.PlatformRank)
                            .ToList();

        return new Leaderboard { GeneratedAt = now, Traders = ranked };
    }

    private async Task<TraderAnalysis> AnalyzeAsync(ScoreboardItem item, DateTimeOffset now, CancellationToken ct)
    {
        var top = item.TopPortfolio!;
        var t = new TraderAnalysis
        {
            PlatformRank = item.Rank,
            UserId = item.User!.Id!,
            Username = item.User.Username ?? "",
            Name = item.User.Name ?? "",
            PortfolioId = top.Id!,
            PortfolioTitle = (top.Title ?? "").Trim(),
            PortfolioType = top.Type ?? "",
            PlatformScore = top.Score,
        };

        try
        {
            var pTask = api.GetPortfolioAsync(t.PortfolioId, ct);
            var sTask = api.GetScoreAsync(t.PortfolioId, ct);
            var hTask = api.GetHistoryAsync(t.PortfolioId, ct);
            await Task.WhenAll(pTask, sTask, hTask);

            var p = pTask.Result.Portfolio;
            if (p is null) { t.FetchError = "portfolio not returned"; return t; }

            t.PlSnapshot = p.PlSnapshot ?? 0;
            t.WinRate = p.WinRate;
            t.Liquidated = p.Liquidated ?? false;
            t.Followers = p.FollowerCount;
            t.Flagged = string.Equals(p.Owner?.StatusTypeId, "flagged", StringComparison.OrdinalIgnoreCase);

            var s = sTask.Result;
            t.ScoreComplete = s.Status == "complete" && s.Detail is not null;
            if (s.Detail is { } d)
            {
                t.PlatformScore = d.Score ?? t.PlatformScore;
                t.NetReturn30d = d.NetReturn30d;
                t.NetReturn90d = d.NetReturn90d;
                t.NetReturn180d = d.NetReturn180d;
                t.MaxDrawdown90d = d.MaxDrawdown90d;
                t.ActiveWeeks90d = d.ActiveWeeks90d;
                t.WinningWeeks90d = d.WinningWeeks90d;
                t.AgeDays = d.PortfolioAgeDays;
                t.ClosedTrades = d.ClosedInvestmentCount;
            }

            var curve = Curve.Compute(hTask.Result, now);
            if (curve is not null)
            {
                t.Sharpe90 = Math.Round(curve.Sharpe, 2);
                t.CurrentDrawdownPct = Math.Round(curve.CurrentDrawdownPct, 2);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            t.FetchError = ex.Message;
        }

        return t;
    }
}
