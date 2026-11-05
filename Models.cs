namespace InvoLab;

// ======================================================================
// Invo API DTOs - only the fields we actually use. Unknown fields are ignored.
// ======================================================================

public sealed record ScoreboardResponse(List<ScoreboardItem>? Items);
public sealed record ScoreboardItem(int Rank, ApiUser? User, TopPortfolio? TopPortfolio);
public sealed record ApiUser(string? Id, string? Name, string? Username);
public sealed record TopPortfolio(string? Id, string? Title, string? Type, double? Score, double? PercentPnl);

public sealed record PortfolioEnvelope(PortfolioInfo? Portfolio);
public sealed record PortfolioInfo(
    string? Id, string? Title, string? Type, double? PlSnapshot, double? WinRate,
    int? ClosedPositions, bool? Liquidated, int? FollowerCount, PortfolioOwner? Owner);
public sealed record PortfolioOwner(string? Id, string? Username, string? StatusTypeId);

public sealed record ScoreEnvelope(string? Status, ScoreDetail? Detail);
public sealed record ScoreDetail(
    double? Score, double? NetReturn30d, double? NetReturn90d, double? NetReturn180d,
    double? MaxDrawdown90d, int? ActiveWeeks90d, int? WinningWeeks90d,
    int? PortfolioAgeDays, int? ClosedInvestmentCount);

public sealed record HistoryResponse(string? Status, List<HistoryPoint>? Items, int? Page, int? Size);
public sealed record HistoryPoint(DateTimeOffset SnapshotAt, double? PlSnapshot);

public sealed record InvestmentsResponse(List<OpenInvestment>? InvestmentsTicker);
public sealed record OpenInvestment(string? Ticker, bool? DirectionLong, double? Leverage);

// ======================================================================
// Our own output models (these get written to site/data/*.json)
// ======================================================================

public sealed class TraderAnalysis
{
    public int PlatformRank { get; set; }
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string PortfolioId { get; set; } = "";
    public string PortfolioTitle { get; set; } = "";
    public string PortfolioType { get; set; } = "";

    /// <summary>Cumulative % P&amp;L of the portfolio since inception (Invo's plSnapshot).</summary>
    public double PlSnapshot { get; set; }

    public double? PlatformScore { get; set; }
    public double? NetReturn30d { get; set; }
    public double? NetReturn90d { get; set; }
    public double? NetReturn180d { get; set; }
    public double? MaxDrawdown90d { get; set; }
    public double? WinRate { get; set; }
    public int? ActiveWeeks90d { get; set; }
    public int? WinningWeeks90d { get; set; }
    public int? ClosedTrades { get; set; }
    public int? AgeDays { get; set; }
    public int? Followers { get; set; }

    // Derived from the equity curve
    public double? Sharpe90 { get; set; }
    public double? CurrentDrawdownPct { get; set; }

    // From open positions (shortlist only)
    public double? MaxOpenLeverage { get; set; }

    public bool ScoreComplete { get; set; }
    public bool Liquidated { get; set; }
    public bool Flagged { get; set; }
    public string? FetchError { get; set; }
    public bool OpenPositionsUnavailable { get; set; }

    // Our verdict
    public double Total { get; set; }
    public Dictionary<string, double?> Components { get; set; } = new();
    public List<string> Flags { get; set; } = new();
    public string? ExcludedReason { get; set; }
}

public sealed class Leaderboard
{
    public DateTimeOffset GeneratedAt { get; set; }
    public List<TraderAnalysis> Traders { get; set; } = new();
}

public sealed record ArmPoint(DateTimeOffset At, double Pl, double Value);

public sealed class Arm
{
    public string Key { get; set; } = "";          // algo | platform-top | random
    public string Label { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string PortfolioId { get; set; } = "";
    public string PortfolioTitle { get; set; } = "";
    public double StartPl { get; set; }
    public double CurrentPl { get; set; }
    public double Value { get; set; }
    public double ReturnPct { get; set; }
    public List<ArmPoint> History { get; set; } = new();

    /// <summary>
    /// Mirrors the trader's equity curve: if the trader's cumulative P&amp;L went from
    /// StartPl% to pl%, our copied capital scales by (100+pl)/(100+StartPl).
    /// </summary>
    public void Record(DateTimeOffset at, double pl, double capital)
    {
        CurrentPl = pl;
        var denom = 100 + StartPl;
        Value = denom > 0 ? Math.Max(0, capital * (100 + pl) / denom) : 0;
        ReturnPct = (Value / capital - 1) * 100;
        History.Add(new ArmPoint(at, pl, Value));
    }
}

public sealed class Experiment
{
    public string Id { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public double InitialCapital { get; set; }
    public string Status { get; set; } = "active";  // active | finished
    public List<Arm> Arms { get; set; } = new();
}

public sealed class ExperimentFile
{
    public List<Experiment> Experiments { get; set; } = new();
}
