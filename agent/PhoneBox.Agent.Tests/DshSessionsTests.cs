using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The DeepSeek Harness session reader: DSH's own session projection under ~/.dsh is the input, and only counters,
/// timestamps, the model and the folder may leave it — never a prompt, a reply draft or a tool argument.
/// </summary>
public sealed class DshSessionsTests : IDisposable
{
    private const string Secret = "PRIVATE PROMPT: delete the production database";
    private const string Reply = "PRIVATE DRAFT REPLY";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-dsh-" + Guid.NewGuid());

    private string WriteSession(string id, object rows, DateTime? writeUtc = null, string? homeDir = null)
    {
        var dir = Path.Combine(homeDir ?? _root, ".dsh", "storages", "session_projcache", "sessions");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            version = 7,
            record = new
            {
                identity = new { formatVersion = 4, createdAt = 1795294537225L, cwd = "/Users/test/phonebox-src", isSeeded = false },
                rows,
            },
        }));
        if (writeUtc is { } wt) File.SetLastWriteTimeUtc(file, wt);
        return file;
    }

    /// <summary>A projection shaped like DSH's: rows carry ver/seq/val, and conversation rows sit next to them.</summary>
    private static object Rows(bool blank = false, bool turnOpen = true, int openStep = 1, int questions = 0) => new
    {
        title = new { ver = 1, seq = 446, val = "A title DSH drew from the conversation" },
        turnBoundary = new { ver = 2, seq = 446, val = new { openTurnStartSeq = 403, lastStepStartSeq = 446, lastTurn = 2 } },
        tokenUsage = new { ver = 2, seq = 446, val = new { totals = new { uncachedInputTokens = 105224, outputTokens = 45865, cacheReadTokens = 3185396 } } },
        contextPressure = new { ver = 5, seq = 446, val = new { surfaceTokens = 88772, contextWindow = 1000000, pressureTokens = 108503 } },
        sessionStats = new { ver = 1, seq = 446, val = new { turns = 2, steps = 52, openStep = openStep > 0 ? (object?)new { turn = 2, step = openStep, startTime = 1795294946432L } : null } },
        modelSelection = new { ver = 3, seq = 446, val = new { lastUsed = new { provider = "ollama", model = "deepseek-v4.1-flash" } } },
        sessionListMetadata = new { ver = 1, seq = 446, val = new { blank, lastPromptAt = 1795294925882L } },
        approvalState = new { ver = 1, seq = 446, val = new { policy = "ask", turnOpen } },
        userQuestions = new { ver = 2, seq = 446, val = new { questions = new { active = questions > 0 ? new object[questions] : Array.Empty<object>() } } },
        workingDirectory = new { ver = 1, seq = 446, val = "/Users/test/phonebox-src" },
        timeContext = new { ver = 3, seq = 446, val = new { lastMessageTime = 1795294946421L } },
        // conversation rows: must never be read, stored or sent
        turnOutline = new { ver = 2, seq = 446, val = new { turns = new[] { new { prompt = Secret, response = Reply } }, draft = Reply } },
        titleInput = new { ver = 3, seq = 446, val = new { first = new { seq = 8, text = Secret } } },
    };

    private static IEnumerable<AgentSession> Read(string home) =>
        new SessionMonitor(NullLogger<SessionMonitor>.Instance).ReadDsh(home);

    [Fact]
    public void ReadsModelTokensContextAndFolder()
    {
        WriteSession("session-3942fa18-4add-4b54-b24c-a8a6f0382124", Rows());

        var s = Assert.Single(Read(_root));

        Assert.Equal("dsh:3942fa18-4add-4b54-b24c-a8a6f0382124", s.Id);
        Assert.Equal("dsh", s.Tool);
        Assert.Equal("phonebox-src-3942", s.Name);
        Assert.Equal("phonebox-src", s.Project);
        Assert.Equal("deepseek-v4.1-flash", s.Model);
        Assert.Equal("busy", s.Status);                      // a turn is open and the file was written just now
        Assert.Equal(1795294946432L, s.TurnStartedAt);
        Assert.Equal(105224L, s.InputTokens);
        Assert.Equal(45865L, s.OutputTokens);
        Assert.Equal(3185396L, s.CachedInputTokens);
        Assert.Equal(105224L + 45865L + 3185396L, s.TotalTokens);
        Assert.Equal(108503L, s.LastInputTokens);            // pressure tokens, not the surface estimate
        Assert.Equal(1000000L, s.ContextWindow);
        Assert.Null(s.Title);                                // DSH's title comes from the conversation, so it stays off
        Assert.Equal(1795294537225L, s.StartedAt);
    }

    [Fact]
    public void ATurnThatIsClosedOrColdIsIdle()
    {
        var closed = Path.Combine(_root, "closed");
        var cold = Path.Combine(_root, "cold");
        WriteSession("closed", Rows(turnOpen: false), homeDir: closed);
        Assert.Equal("idle", Assert.Single(Read(closed)).Status);

        WriteSession("cold", Rows(), writeUtc: DateTime.UtcNow.AddMinutes(-20), homeDir: cold);
        Assert.Equal("idle", Assert.Single(Read(cold)).Status);
    }

    [Fact]
    public void AnOpenQuestionIsWaitingForYou()
    {
        WriteSession("asking", Rows(questions: 2));
        var s = Assert.Single(Read(_root));
        Assert.Equal("waiting", s.Status);
        Assert.Equal("answer a question", s.WaitingFor);
    }

    [Fact]
    public void BlankSessionsAreSkipped()
    {
        WriteSession("blank", Rows(blank: true));
        Assert.Empty(Read(_root));
    }

    [Fact]
    public void TheConversationNeverLeavesTheReader()
    {
        WriteSession("session-1", Rows());
        var s = Assert.Single(Read(_root));
        var json = JsonSerializer.Serialize(s);
        Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain("database", json);
        Assert.DoesNotContain("A title DSH drew", json);
    }

    [Fact]
    public void ASessionFileIsParsedAgainOnlyWhenItChanges()
    {
        var file = WriteSession("cached", Rows());
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        Assert.Single(monitor.ReadDsh(_root));
        var written = File.GetLastWriteTimeUtc(file);
        Assert.True(monitor.HasCachedDshDetails(file, written, new FileInfo(file).Length));

        File.WriteAllText(file, JsonSerializer.Serialize(new { version = 7, record = new { identity = new { createdAt = 1L }, rows = Rows(openStep: 0, turnOpen: false) } }));
        File.SetLastWriteTimeUtc(file, written.AddSeconds(5));
        monitor.ClearDshCache();
        var again = Assert.Single(monitor.ReadDsh(_root));
        Assert.Equal("idle", again.Status);                  // no step running in the rewritten file
        Assert.Equal(105224L, again.InputTokens);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
