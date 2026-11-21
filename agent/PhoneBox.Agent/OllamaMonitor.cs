using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The Ollama Cloud account as the phone shows it: the plan, the credit balance, what the last day and week cost, and
/// how long the balance lasts at that rate. Every number comes from ollama.com with the API key from the settings; the
/// key itself stays on this computer and is never sent to the phone.
/// </summary>
/// <param name="Hours">The cost of each of the last 24 hours, oldest first — the strip under the balance.</param>
public sealed record OllamaSnapshot(string Plan, double AllowanceUsd, double IncludedLeftUsd, double PurchasedUsd,
    double AvailableUsd, double DayUsd, double WeekUsd, long Requests24h, long Requests7d, long InputTokens7d,
    long CachedTokens7d, long OutputTokens7d, double? BurnPerDay, int? RunwayDays, long? PeriodEndsAt,
    IReadOnlyList<double> Hours, long UpdatedAt, string? Error = null);

/// <summary>
/// Reads the Ollama Cloud account for the Ollama widget: the usage API (request counts, tokens and cost over the last
/// 24 hours and 7 days), the balance API (included allowance, remaining included and purchased credits) and the
/// account's plan. Read-only and opt-in: without an API key nothing is sent anywhere.
/// </summary>
public sealed class OllamaMonitor(ConfigStore? config, ILogger<OllamaMonitor> log) : BackgroundService
{
    private const long HealthyMs = 180_000;   // three minutes: usage moves slowly and the API is not free
    private const long FailedMs = 60_000;     // a refusal or an outage retries once a minute, like the quota readers
    private const string BaseUrl = "https://ollama.com";

    private volatile OllamaSnapshot? _snapshot;
    private HttpClient? _httpClient;

    private HttpClient Http => _httpClient ??= new HttpClient(HttpHandler
            ?? new SocketsHttpHandler { ConnectCallback = (ctx, ct) => QuotaMonitor.ConnectIpv4First(ctx.DnsEndPoint, ct) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>For tests: a fake handler instead of the network.</summary>
    internal HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>
    /// The last reading, or null while there is no API key or nothing has been read yet. The phone shows the error
    /// next to the last good numbers, so an outage does not blank the widget.
    /// </summary>
    public OllamaSnapshot? Snapshot() => _snapshot;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        long next = 0;
        var wakes = Wake.Count;
        while (!ct.IsCancellationRequested)
        {
            var key = config?.Get().Ollama.ApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                // No key: the widget shows its "add a key" hint instead of numbers.
                if (_snapshot is not null) { _snapshot = null; StateSignal.Poke(); }
                next = 0;
            }
            else if (Environment.TickCount64 >= next || Wake.Count != wakes)
            {
                wakes = Wake.Count;
                var ok = await RefreshAsync(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
                next = Environment.TickCount64 + (ok ? HealthyMs : FailedMs);
            }
            await Wake.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>
    /// One reading of the account. Returns false and keeps the previous numbers with an error when ollama.com cannot be
    /// reached or refuses the key; a first failure leaves a snapshot that carries only the error.
    /// </summary>
    internal async Task<bool> RefreshAsync(string key, long nowMs, CancellationToken ct)
    {
        try
        {
            var plan = await ReadPlanAsync(key, ct);
            var balance = await ReadJsonAsync($"{BaseUrl}/api/balance", key, ct);
            var day = await ReadJsonAsync($"{BaseUrl}/api/usage?range=24h", key, ct);
            var week = await ReadJsonAsync($"{BaseUrl}/api/usage?range=7d", key, ct);

            var included = Obj(balance, "included");
            var purchased = Obj(balance, "purchased");
            var allowance = Dbl(included, "allowance_usd") ?? 0;
            var includedLeft = Dbl(included, "balance_usd") ?? 0;
            var purchasedLeft = Dbl(purchased, "balance_usd") ?? 0;
            var available = Math.Max(0, includedLeft + purchasedLeft);

            var spentWeek = Dbl(Obj(week, "totals"), "usage_usd") ?? 0;
            var burnPerDay = spentWeek / 7;
            var runway = burnPerDay > 0.0001
                ? (int?)Math.Clamp((long)Math.Round(available / burnPerDay), 0, 3650)
                : null;

            // The strip is the last 24 hourly buckets, oldest first; the API sends them in that order already.
            var hours = new List<double>();
            if (day.ValueKind == JsonValueKind.Object && day.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in buckets.EnumerateArray())
                    hours.Add(Math.Round(Dbl(b, "usage_usd") ?? 0, 4));
                if (hours.Count > 24) hours.RemoveRange(0, hours.Count - 24);
            }

            var totals24 = Obj(day, "totals");
            var totals7 = Obj(week, "totals");
            _snapshot = new OllamaSnapshot(
                Plan: plan,
                AllowanceUsd: Round(allowance),
                IncludedLeftUsd: Round(includedLeft),
                PurchasedUsd: Round(purchasedLeft),
                AvailableUsd: Round(available),
                DayUsd: Round(Dbl(totals24, "usage_usd") ?? 0),
                WeekUsd: Round(spentWeek),
                Requests24h: Lng(totals24, "request_count") ?? 0,
                Requests7d: Lng(totals7, "request_count") ?? 0,
                InputTokens7d: Lng(totals7, "input_tokens") ?? 0,
                CachedTokens7d: Lng(totals7, "cached_input_tokens") ?? 0,
                OutputTokens7d: Lng(totals7, "output_tokens") ?? 0,
                BurnPerDay: burnPerDay > 0.0001 ? Round(burnPerDay) : null,
                RunwayDays: runway,
                PeriodEndsAt: Iso(Str(Obj(included, "period"), "until")),
                Hours: hours,
                UpdatedAt: nowMs);
            StateSignal.Poke();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            Keep("Could not reach ollama.com: " + ex.Message, nowMs);
            return false;
        }
        catch (UnauthorizedAccessException ex) { Keep(ex.Message, nowMs); return false; }
        catch (JsonException) { Keep("Ollama sent a reply this version does not understand", nowMs); return false; }
        catch (Exception ex)
        {
            log.LogDebug("ollama usage: {Message}", ex.Message);
            Keep("Could not read the Ollama account: " + ex.Message, nowMs);
            return false;
        }
    }

    /// <summary>The account's plan, from the same call the CLI signs in with. Throws when the key is refused.</summary>
    private async Task<string> ReadPlanAsync(string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/me")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await Http.SendAsync(req, ct);
        await ThrowForStatusAsync(res, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var plan = Str(doc.RootElement, "Plan") ?? "free";
        return plan.Length == 0 ? "Free" : char.ToUpperInvariant(plan[0]) + plan[1..];
    }

    private async Task<JsonElement> ReadJsonAsync(string url, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await Http.SendAsync(req, ct);
        await ThrowForStatusAsync(res, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    /// <summary>401/403 becomes an actionable message; any other failure is an HTTP error with its status.</summary>
    private static async Task ThrowForStatusAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Ollama refused the API key — make a new one at ollama.com/settings/keys");
        var body = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"ollama.com answered HTTP {(int)res.StatusCode} {body[..Math.Min(body.Length, 80)]}",
            null, res.StatusCode);
    }

    /// <summary>Keeps the last good numbers and attaches the error, like the quota readers do. <paramref name="nowMs"/>
    /// stays the time of the reading that failed, so a broken connection does not make old numbers look fresh.</summary>
    private void Keep(string message, long nowMs)
    {
        var old = _snapshot;
        _snapshot = old is null
            ? new OllamaSnapshot("", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, [], nowMs, message)
            : old with { Error = message };
        StateSignal.Poke();
    }

    private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static long? Lng(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : null;

    private static long? Iso(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d.ToUnixTimeMilliseconds()
            : null;
}
