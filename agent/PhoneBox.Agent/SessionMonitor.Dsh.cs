using System.Collections.Concurrent;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed partial class SessionMonitor
{
    private const int DshDiscoveryIntervalMs = 30_000;
    private const int DshMaxSessions = 50;
    private const int DshMaxReadBytes = 512 * 1024;

    private sealed record DshDetails(
        bool Blank,
        string? Model,
        string? Project,
        long? StartedAt,
        long? LastActivityAt,
        long? InputTokens,
        long? OutputTokens,
        long? CachedInputTokens,
        long? LastInputTokens,
        long? ContextWindow,
        bool TurnOpen,
        long? StepStartedAt,
        int ActiveQuestions);

    private string? _dshDiscoveryHome;
    private long _nextDshDiscovery;
    private string[] _dshFiles = [];
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, DshDetails? Details)> _dshCache = new();

    internal void ClearDshCache()
    {
        _dshDiscoveryHome = null;
        _nextDshDiscovery = 0;
        _dshFiles = [];
        _dshCache.Clear();
    }

    /// <summary>True when the parse cache holds this file at the given write time and length (for tests).</summary>
    internal bool HasCachedDshDetails(string path, DateTime lastWriteUtc, long length) =>
        _dshCache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWriteUtc && cached.Length == length;

    /// <summary>
    /// Recent DeepSeek Harness sessions from ~/.dsh/storages/session_projcache/sessions — the projection DSH keeps for its
    /// own session list and rewrites while a session runs. Only counters, timestamps, the model and the working folder
    /// are read; the prompt, the reply draft, the session title and the rest of the conversation stay untouched on disk.
    /// </summary>
    internal IEnumerable<AgentSession> ReadDsh(string? homeDir = null)
    {
        var home = homeDir ?? Home;
        if (!string.Equals(_dshDiscoveryHome, home, StringComparison.Ordinal))
        {
            ClearDshCache();
            _dshDiscoveryHome = home;
        }

        var dir = Path.Combine(home, ".dsh", "storages", "session_projcache", "sessions");
        if (!Directory.Exists(dir)) yield break;

        var cutoff = DateTime.UtcNow.AddHours(-3);
        if (Environment.TickCount64 >= _nextDshDiscovery)
        {
            var found = new List<(DateTime LastWriteUtc, string Path)>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(file); } catch { continue; }
                    if (!fi.Exists || fi.LastWriteTimeUtc <= cutoff) continue;
                    found.Add((fi.LastWriteTimeUtc, fi.FullName));
                }
            }
            catch (Exception) { /* an unreadable folder: try again next pass */ }
            _dshFiles = found.OrderByDescending(x => x.LastWriteUtc).Take(DshMaxSessions).Select(x => x.Path).ToArray();
            _nextDshDiscovery = Environment.TickCount64 + DshDiscoveryIntervalMs;
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new List<AgentSession>(_dshFiles.Length);
        foreach (var file in _dshFiles)
        {
            try
            {
                var f = new FileInfo(file);
                if (!f.Exists || f.LastWriteTimeUtc <= cutoff) continue;
                active.Add(f.FullName);
                var details = ReadDshDetailsCached(f);
                if (details is null || details.Blank) continue;

                var id = Path.GetFileNameWithoutExtension(f.Name);
                if (id.StartsWith("session-", StringComparison.Ordinal)) id = id["session-".Length..];
                var shortId = id.Length >= 4 ? id[..4] : id;
                var project = SanitizeAntigravityLabel(details.Project, AgyMaxProjectChars) ?? "DSH";

                var startedAt = details.StartedAt ?? new DateTimeOffset(f.CreationTimeUtc).ToUnixTimeMilliseconds();
                var fileWriteMs = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var since = Math.Max(startedAt, details.LastActivityAt ?? fileWriteMs);
                var age = DateTime.UtcNow - f.LastWriteTimeUtc;
                var waiting = details.ActiveQuestions > 0;
                var busy = !waiting && details.TurnOpen && age < TimeSpan.FromMinutes(3);
                var status = waiting ? "waiting" : busy ? "busy" : "idle";

                var hasTokens = details.InputTokens is not null || details.OutputTokens is not null || details.CachedInputTokens is not null;
                long? input = hasTokens ? details.InputTokens ?? 0 : null;
                long? output = hasTokens ? details.OutputTokens ?? 0 : null;
                long? cached = hasTokens ? details.CachedInputTokens ?? 0 : null;
                long? total = hasTokens ? input + output + cached : null;

                emitted.Add(new AgentSession(
                    Id: "dsh:" + id,
                    Tool: "dsh",
                    Name: $"{project.ToLowerInvariant()}-{shortId}",
                    Project: project,
                    Status: status,
                    WaitingFor: waiting ? "answer a question" : null,
                    Since: since,
                    StartedAt: startedAt,
                    Model: SanitizeAntigravityLabel(details.Model, AgyMaxMetaChars),
                    TotalTokens: total,
                    InputTokens: input,
                    OutputTokens: output,
                    CachedInputTokens: cached,
                    LastInputTokens: details.LastInputTokens,
                    ContextWindow: details.ContextWindow,
                    TurnStartedAt: busy ? details.StepStartedAt : null));
            }
            catch (Exception) { /* a file rewritten under us: the next pass reads it again */ }
        }

        foreach (var old in _dshCache.Keys.Where(p => !active.Contains(p)))
            _dshCache.TryRemove(old, out _);

        foreach (var s in emitted.OrderByDescending(s => s.Since).Take(DshMaxSessions))
            yield return s;
    }

    private DshDetails? ReadDshDetailsCached(FileInfo f)
    {
        _dshCache.TryGetValue(f.FullName, out var cached);
        if (cached.Details is not null && cached.LastWriteUtc == f.LastWriteTimeUtc && cached.Length == f.Length)
            return cached.Details;
        if (f.Length > DshMaxReadBytes) return cached.Details;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(f.FullName));
            var details = ParseDsh(doc.RootElement);
            if (details is not null) _dshCache[f.FullName] = (f.LastWriteTimeUtc, f.Length, details);
            return details;
        }
        catch (JsonException) { return cached.Details; }        // half-written: keep the last complete parse
        catch (IOException) { return cached.Details; }
        catch (UnauthorizedAccessException) { return cached.Details; }
    }

    /// <summary>
    /// The rows of DSH's session projection. Every row is <c>{ ver, seq, val }</c>; only the values named here are read,
    /// so conversation rows (turnOutline, titleInput, plan, inbox…) never enter this class.
    /// </summary>
    private static DshDetails? ParseDsh(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object) return null;
        if (!record.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Object) return null;

        // DSH keeps the session's identity beside the rows, not in one: record.identity.createdAt / cwd.
        var identity = record.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.Object ? id : default;
        var tokenUsage = DshRow(rows, "tokenUsage");
        var totals = DshObj(tokenUsage, "totals");
        var context = DshRow(rows, "contextPressure");
        var stats = DshRow(rows, "sessionStats");
        var listMeta = DshRow(rows, "sessionListMetadata");
        var approval = DshRow(rows, "approvalState");
        var timeContext = DshRow(rows, "timeContext");
        var questions = DshObj(DshRow(rows, "userQuestions"), "questions");
        var active = questions.ValueKind == JsonValueKind.Object && questions.TryGetProperty("active", out var act)
            && act.ValueKind == JsonValueKind.Array ? act.GetArrayLength() : 0;

        var workingDirectory = DshRow(rows, "workingDirectory");
        var project = workingDirectory.ValueKind == JsonValueKind.String
            ? Path.GetFileName(workingDirectory.GetString() ?? "")
            : Path.GetFileName(DshText(identity, "cwd") ?? "");

        return new DshDetails(
            Blank: DshBool(listMeta, "blank") ?? false,
            Model: DshText(DshObj(DshRow(rows, "modelSelection"), "lastUsed"), "model"),
            Project: string.IsNullOrEmpty(project) ? null : project,
            StartedAt: DshLong(identity, "createdAt"),
            LastActivityAt: DshLong(timeContext, "lastMessageTime") ?? DshLong(listMeta, "lastPromptAt"),
            InputTokens: DshLong(totals, "uncachedInputTokens"),
            OutputTokens: DshLong(totals, "outputTokens"),
            CachedInputTokens: DshLong(totals, "cacheReadTokens"),
            LastInputTokens: DshLong(context, "pressureTokens") ?? DshLong(context, "surfaceTokens"),
            ContextWindow: DshLong(context, "contextWindow"),
            TurnOpen: DshBool(approval, "turnOpen") ?? DshBool(approval, "turnOpen3") ?? false,
            StepStartedAt: DshLong(DshObj(stats, "openStep"), "startTime"),
            ActiveQuestions: active);
    }

    /// <summary>The <c>val</c> of a row, or a default element when the row is absent (an older projection).</summary>
    private static JsonElement DshRow(JsonElement rows, string name) =>
        rows.ValueKind == JsonValueKind.Object && rows.TryGetProperty(name, out var row)
        && row.ValueKind == JsonValueKind.Object && row.TryGetProperty("val", out var val) ? val : default;

    private static JsonElement DshObj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string? DshText(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? DshLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : null;

    private static bool? DshBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
