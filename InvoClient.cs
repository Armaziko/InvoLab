using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace InvoLab;

/// <summary>Thin, throttled, retrying client for the Invo endpoints we need.</summary>
public sealed class InvoClient : IDisposable
{
    private const int MaxAttempts = 4;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;

    public InvoClient(string? bearerToken, int concurrency = 4)
    {
        _gate = new SemaphoreSlim(concurrency);
        _http = new HttpClient { BaseAddress = new Uri("https://api.invoapp.com/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("invo-copy-lab/0.1");
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<ScoreboardResponse> GetTopAsync(string requesterUserId, int limit, CancellationToken ct) =>
        await PostAsync<ScoreboardResponse>("analytics/score/user/top",
            new { userId = requesterUserId, listType = "all", sortBy = "score", duration = "all_time", limit }, ct)
        ?? new ScoreboardResponse(null);

    public async Task<PortfolioEnvelope> GetPortfolioAsync(string portfolioId, CancellationToken ct) =>
        await PostAsync<PortfolioEnvelope>("v1_0/portfolios/get_portfolio_by_id", new { portfolioId }, ct)
        ?? new PortfolioEnvelope(null);

    public async Task<ScoreEnvelope> GetScoreAsync(string portfolioId, CancellationToken ct) =>
        await PostAsync<ScoreEnvelope>("analytics/score/portfolio", new { portfolioId }, ct)
        ?? new ScoreEnvelope(null, null);

    public async Task<List<OpenInvestment>> GetOpenInvestmentsAsync(string portfolioId, CancellationToken ct)
    {
        var r = await PostAsync<InvestmentsResponse>("v1_0/investments/get_investments",
            new { isOpen = true, @params = new { page = 1, size = 50 }, portfolioId }, ct);
        return r?.InvestmentsTicker ?? new List<OpenInvestment>();
    }

    /// <summary>
    /// Equity-curve snapshots, oldest first. ASSUMPTION: the endpoint pages like get_investments
    /// (params.page / params.size). We keep requesting pages until one adds nothing new.
    /// </summary>
    public async Task<List<HistoryPoint>> GetHistoryAsync(string portfolioId, CancellationToken ct)
    {
        const int size = 120;
        var seen = new HashSet<DateTimeOffset>();
        var all = new List<HistoryPoint>();

        for (var page = 1; page <= 10; page++)
        {
            var r = await PostAsync<HistoryResponse>("analytics/portfolio/pnl/history",
                new { portfolioId, @params = new { filter = "year", page, size } }, ct);
            var items = r?.Items ?? new List<HistoryPoint>();

            var added = 0;
            foreach (var p in items)
                if (seen.Add(p.SnapshotAt)) { all.Add(p); added++; }

            if (added == 0 || items.Count < size) break;
        }

        return all.OrderBy(p => p.SnapshotAt).ToList();
    }

    private async Task<T?> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? res = null;
            await _gate.WaitAsync(ct);
            try
            {
                res = await _http.PostAsJsonAsync(path, body, Json, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                // fall through to backoff + retry
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // request timeout (not a user cancel): retry
            }
            finally
            {
                _gate.Release();
            }

            if (res is not null)
            {
                using (res)
                {
                    if (res.IsSuccessStatusCode)
                        return await res.Content.ReadFromJsonAsync<T>(Json, ct);

                    var code = (int)res.StatusCode;
                    var retryable = code == 429 || code >= 500;
                    if (!retryable || attempt >= MaxAttempts)
                        res.EnsureSuccessStatusCode(); // throws
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
