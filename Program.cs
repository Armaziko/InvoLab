using System.Globalization;
using InvoLab;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var opts = Options.Parse(args.Skip(1));

if (command is "help" or "--help" or "-h")
{
    Console.WriteLine("""
        InvoLab - copy-trading experiment runner

        Commands
          collect   Fetch the top traders, score them, write site/data/leaderboard.json
          start     collect + start a new experiment (--capital 10000 --days 30 [--force])
          update    advance active experiments with fresh P&L, then refresh the leaderboard

        Options
          --data <dir>    output folder (default site/data)
          --limit <n>     how many top traders to analyse (default 100)

        Environment
          INVO_USER_ID    your Invo user id (sent as "userId" in the scoreboard request)  [required]
          INVO_TOKEN      bearer token, if the API requires one                            [optional]
        """);
    return 0;
}

var dataDir = opts.GetValueOrDefault("data", "site/data");
var leaderboardPath = Path.Combine(dataDir, "leaderboard.json");
var experimentsPath = Path.Combine(dataDir, "experiments.json");
var limit = int.Parse(opts.GetValueOrDefault("limit", "100"));
var capital = double.Parse(opts.GetValueOrDefault("capital", "10000"));
var days = int.Parse(opts.GetValueOrDefault("days", "30"));

var requesterId = Environment.GetEnvironmentVariable("INVO_USER_ID") ?? opts.GetValueOrDefault("user");
if (string.IsNullOrWhiteSpace(requesterId))
{
    Console.Error.WriteLine("INVO_USER_ID is not set.");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var api = new InvoClient(Environment.GetEnvironmentVariable("INVO_TOKEN"));
var cfg = new ScoringConfig();
var now = DateTimeOffset.UtcNow;

async Task<Leaderboard> CollectAsync()
{
    var lb = await new Collector(api, cfg).RunAsync(requesterId!, limit, now, cts.Token);
    Store.Save(leaderboardPath, lb);

    var eligible = lb.Traders.Count(t => t.ExcludedReason is null);
    Console.WriteLine($"Scored {lb.Traders.Count} traders, {eligible} eligible. Top 5:");
    foreach (var t in lb.Traders.Take(5))
        Console.WriteLine($"  {t.Total,6:0.0}  @{t.Username}  (platform #{t.PlatformRank})");
    return lb;
}

try
{
    switch (command)
    {
        case "collect":
            await CollectAsync();
            break;

        case "start":
        {
            var file = Store.Load(experimentsPath, () => new ExperimentFile());
            if (file.Experiments.Any(x => x.Status == "active") && !opts.ContainsKey("force"))
            {
                Console.Error.WriteLine("An experiment is already running. Use --force to start another.");
                return 3;
            }

            var lb = await CollectAsync();
            var exp = Lab.Start(lb, capital, days, now);
            file.Experiments.Add(exp);
            Store.Save(experimentsPath, file);

            Console.WriteLine($"Experiment {exp.Id} started: ${capital:0} for {days} days.");
            foreach (var arm in exp.Arms) Console.WriteLine($"  {arm.Label}: @{arm.Username}");
            break;
        }

        case "update":
        {
            var file = Store.Load(experimentsPath, () => new ExperimentFile());
            foreach (var exp in file.Experiments.Where(x => x.Status == "active"))
            {
                await Lab.UpdateAsync(exp, api, now, cts.Token);
                Console.WriteLine($"Experiment {exp.Id} ({exp.Status}):");
                foreach (var arm in exp.Arms)
                    Console.WriteLine($"  {arm.Label,-14} ${arm.Value,9:0.00}  {arm.ReturnPct,7:+0.00;-0.00}%");
            }
            Store.Save(experimentsPath, file);

            await CollectAsync();
            break;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Try 'help'.");
            return 1;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"API request failed: {ex.Message}");
    return 1;
}

return 0;
