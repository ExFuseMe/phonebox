using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The Ollama Cloud reader: three ollama.com endpoints with the key from the settings, turned into the numbers the
/// Ollama widget draws. No key means no request at all, and a failure keeps the last good numbers with an error.
/// </summary>
public sealed class OllamaMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-ollama-" + Guid.NewGuid());

    private const string Me = """{"ID":"745af721","Email":"person@example.com","Name":"person","Plan":"free"}""";
    private const string Balance = """
        {"included":{"balance_usd":0,"allowance_usd":0,"period":{"from":"2026-11-21T14:49:41.299842Z","until":"2026-12-21T14:49:41.299842Z"}},
         "purchased":{"balance_usd":19.4038}}
        """;
    private const string Day = """
        {"range":"24h","scope":"self","granularity":"hour","totals":{"request_count":391,"usage_usd":0.59499,"input_tokens":65868713,"cached_input_tokens":64434084,"output_tokens":307598},
         "buckets":[{"from":"2026-11-20T20:00:00Z","usage_usd":0},{"from":"2026-11-20T21:00:00Z","usage_usd":0.25},
                    {"from":"2026-11-20T22:00:00Z","usage_usd":0.125}]}
        """;
    private const string Week = """
        {"range":"7d","scope":"self","granularity":"day","totals":{"request_count":380,"usage_usd":0.5866,"input_tokens":65338611,"cached_input_tokens":63919486,"output_tokens":300175},
         "buckets":[]}
        """;

    private (OllamaMonitor Monitor, FakeOllama Api, ConfigStore Config) Agent(string? key = "key-1")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, key is null ? "{}" : JsonSerializer.Serialize(new { ollama = new { apiKey = key } }));
        var api = new FakeOllama();
        return (new OllamaMonitor(new ConfigStore(path), NullLogger<OllamaMonitor>.Instance) { HttpHandler = api }, api, new ConfigStore(path));
    }

    [Fact]
    public async Task ReadsThePlanTheBalanceAndTheUsage()
    {
        var (monitor, api, _) = Agent();
        api.Respond("https://ollama.com/api/me", Me);
        api.Respond("https://ollama.com/api/balance", Balance);
        api.Respond("https://ollama.com/api/usage?range=24h", Day);
        api.Respond("https://ollama.com/api/usage?range=7d", Week);

        Assert.True(await monitor.RefreshAsync("key-1", 1_800_000_000_000, default));
        var s = monitor.Snapshot()!;

        Assert.Equal("Free", s.Plan);
        Assert.Equal(19.4038, s.AvailableUsd, 4);
        Assert.Equal(0.5950, s.DayUsd, 4);
        Assert.Equal(0.5866, s.WeekUsd, 4);
        Assert.Equal(0.0838, s.BurnPerDay!.Value, 4);
        Assert.Equal(232, s.RunwayDays);                       // 19.4038 / 0.0838
        Assert.Equal(391L, s.Requests24h);
        Assert.Equal(380L, s.Requests7d);
        Assert.Equal(300175L, s.OutputTokens7d);              // the seven-day total, not the day's
        Assert.Equal(new[] { 0d, 0.25, 0.125 }, s.Hours);
        Assert.Equal(DateTimeOffset.Parse("2026-12-21T14:49:41.299842Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), s.PeriodEndsAt);
        Assert.Null(s.Error);

        // every call carried the key, and /api/me is a POST
        Assert.All(api.Requests, r => Assert.Equal("Bearer key-1", r.Authorization));
        Assert.Equal("POST", api.Requests.Single(r => r.Url.EndsWith("/api/me")).Method);
    }

    [Fact]
    public async Task ARefusedKeyBecomesAnActionableError()
    {
        var (monitor, api, _) = Agent();
        api.Respond("https://ollama.com/api/me", """{"error":"invalid credentials"}""", HttpStatusCode.Unauthorized);

        Assert.False(await monitor.RefreshAsync("bad", 1, default));
        var s = monitor.Snapshot()!;
        Assert.Contains("refused the API key", s.Error);
        Assert.Equal(0, s.AvailableUsd);
        Assert.Equal(1, s.UpdatedAt);                          // the time of the failed reading
    }

    [Fact]
    public async Task AFailureKeepsTheLastGoodNumbersWithTheError()
    {
        var (monitor, api, _) = Agent();
        api.Respond("https://ollama.com/api/me", Me);
        api.Respond("https://ollama.com/api/balance", Balance);
        api.Respond("https://ollama.com/api/usage?range=24h", Day);
        api.Respond("https://ollama.com/api/usage?range=7d", Week);
        Assert.True(await monitor.RefreshAsync("key-1", 2_000, default));

        api.Fail("https://ollama.com/api/balance", HttpStatusCode.InternalServerError);
        Assert.False(await monitor.RefreshAsync("key-1", 3_000, default));
        var s = monitor.Snapshot()!;
        Assert.Equal(19.4038, s.AvailableUsd, 4);              // still the last reading
        Assert.Equal(2_000, s.UpdatedAt);
        Assert.Contains("HTTP 500", s.Error);
    }

    [Fact]
    public void WithoutAKeyNothingIsReadOrSentToThePhone()
    {
        var (monitor, _, _) = Agent(key: null);
        Assert.Null(monitor.Snapshot());
        Assert.Null(new AgentConfig().Ollama.ApiKey);
        Assert.DoesNotContain("apiKey", JsonSerializer.Serialize(new AgentConfig().ToPublic()));
    }

    [Fact]
    public void TheKeyIsTrimmedAsItIsSaved()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "blank-key.json");
        File.WriteAllText(path, """{"ollama":{"apiKey":"   "}}""");
        Assert.Null(new ConfigStore(path).Get().Ollama.ApiKey);
    }

    private sealed record Call(string Method, string Url, string? Authorization);

    private sealed class FakeOllama : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new();
        public List<Call> Requests { get; } = [];

        public void Respond(string url, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses[url] = (status, body);

        public void Fail(string url, HttpStatusCode status) => _responses[url] = (status, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(new Call(request.Method.Method, url, request.Headers.Authorization?.ToString()));
            await Task.Yield();
            if (!_responses.TryGetValue(url, out var reply))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"path not found"}""", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
