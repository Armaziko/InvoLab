namespace InvoLab;

/// <summary>Creates and advances the "copy a trader for N days" experiments.</summary>
public static class Lab
{
    /// <summary>
    /// Starts an experiment with three arms so one lucky/unlucky month can be put in context:
    ///   algo         - our top-ranked eligible trader
    ///   platform-top - Invo's own #1 (the obvious naive strategy)
    ///   random       - a random trader from the top list (baseline)
    /// </summary>
    public static Experiment Start(Leaderboard lb, double capital, int days, DateTimeOffset now)
    {
        var algo = lb.Traders.FirstOrDefault(t => t.ExcludedReason is null)
            ?? throw new InvalidOperationException("No eligible trader found - check the filters or the data.");

        var withData = lb.Traders.Where(t => t.FetchError is null).ToList();
        var platformTop = withData.OrderBy(t => t.PlatformRank).First();

        var rng = new Random((int)(now.ToUnixTimeSeconds() / 86400));
        var pool = withData.Where(t => t.UserId != algo.UserId).ToList();
        var random = pool.Count > 0 ? pool[rng.Next(pool.Count)] : algo;

        var strengths = string.Join(", ", algo.Components
            .Where(kv => kv.Value.HasValue)
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => $"{kv.Key} {kv.Value:0.00}"));

        var e = new Experiment
        {
            Id = now.ToString("yyyy-MM-dd"),
            StartedAt = now,
            EndsAt = now.AddDays(days),
            InitialCapital = capital,
            Status = "active",
            Arms =
            {
                MakeArm("algo", "Our algorithm", $"Score {algo.Total:0.0}/100. Strongest: {strengths}.", algo, capital, now),
                MakeArm("platform-top", "Invo's #1", $"Platform rank {platformTop.PlatformRank}, platform score {platformTop.PlatformScore:0.00}.", platformTop, capital, now),
                MakeArm("random", "Random pick", "Random trader from the top list (baseline).", random, capital, now),
            }
        };
        return e;
    }

    private static Arm MakeArm(string key, string label, string why, TraderAnalysis t, double capital, DateTimeOffset now)
    {
        var arm = new Arm
        {
            Key = key,
            Label = label,
            Rationale = why,
            UserId = t.UserId,
            Username = t.Username,
            PortfolioId = t.PortfolioId,
            PortfolioTitle = t.PortfolioTitle,
            StartPl = t.PlSnapshot,
        };
        arm.Record(now, t.PlSnapshot, capital);
        return arm;
    }

    public static async Task UpdateAsync(Experiment e, InvoClient api, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var arm in e.Arms)
        {
            try
            {
                var p = (await api.GetPortfolioAsync(arm.PortfolioId, ct)).Portfolio;
                if (p?.PlSnapshot is not double pl) continue;
                arm.Record(now, pl, e.InitialCapital);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"  ! could not update '{arm.Label}': {ex.Message}");
            }
        }

        if (now >= e.EndsAt) e.Status = "finished";
    }
}
