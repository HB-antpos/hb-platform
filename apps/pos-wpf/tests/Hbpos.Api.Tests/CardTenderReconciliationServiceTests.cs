using Hbpos.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class CardTenderReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_reports_approved_sessions_that_have_no_order()
    {
        var repository = new FakeRepository
        {
            Candidates = [Candidate(1, "sess-1", amountCents: 1250)]
        };

        var result = await CreateService(repository).RunAsync(CancellationToken.None);

        Assert.Equal(new CardTenderReconciliationResult(1, 0, 1, 0), result);
        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderReconciliationIssueTypes.ApprovedSessionWithoutOrder, issue.IssueType);
        Assert.Equal(CardTenderIssueSources.ReconciliationJob, issue.Source);
        Assert.Equal(CardTenderIssueSeverities.Error, issue.Severity);
        Assert.Equal("S01", issue.StoreCode);
        Assert.Equal("POS01", issue.DeviceCode);
        Assert.Equal("Production", issue.Environment);
        Assert.Equal("sess-1", issue.SessionId);
        Assert.Equal(12.50m, issue.Amount);
        Assert.Null(issue.OrderGuid);
    }

    [Fact]
    public async Task RunAsync_links_instead_of_reporting_when_the_order_is_already_in_payment_detail()
    {
        var orderGuid = Guid.NewGuid().ToString("D");
        var repository = new FakeRepository
        {
            Candidates = [Candidate(1, "sess-1"), Candidate(2, "sess-2")],
            OrdersBySession = { ["sess-1"] = orderGuid }
        };

        var result = await CreateService(repository).RunAsync(CancellationToken.None);

        Assert.Equal(new CardTenderReconciliationResult(2, 1, 1, 0), result);
        Assert.Equal([(1L, orderGuid)], repository.Links);
        Assert.Equal("sess-2", Assert.Single(repository.UpsertedIssues).SessionId);
    }

    [Fact]
    public async Task RunAsync_uses_the_configured_grace_period_lookback_and_limit()
    {
        var repository = new FakeRepository();
        var options = new CardTenderReconciliationOptions { GracePeriodMinutes = 90, LookbackDays = 3, MaxCandidatesPerRun = 25 };

        await CreateService(repository, options).RunAsync(CancellationToken.None);

        Assert.Equal(Now.UtcDateTime.AddDays(-3), repository.LastAfter);
        Assert.Equal(Now.UtcDateTime.AddMinutes(-90), repository.LastBefore);
        Assert.Equal(25, repository.LastLimit);
    }

    [Fact]
    public async Task RunAsync_resolves_issues_whose_session_has_since_been_linked_even_when_nothing_is_reported()
    {
        var repository = new FakeRepository { ResolvedCount = 3 };

        var result = await CreateService(repository).RunAsync(CancellationToken.None);

        Assert.Equal(new CardTenderReconciliationResult(0, 0, 0, 3), result);
        Assert.Empty(repository.UpsertedIssues);
    }

    [Fact]
    public async Task RunAsync_logs_a_warning_per_orphan_session_so_central_logging_can_alert()
    {
        var repository = new FakeRepository { Candidates = [Candidate(1, "sess-1"), Candidate(2, "sess-2")] };
        var logger = new CapturingLogger<CardTenderReconciliationService>();
        var service = new CardTenderReconciliationService(
            repository, Options.Create(new CardTenderReconciliationOptions()), new FixedTimeProvider(Now), logger);

        await service.RunAsync(CancellationToken.None);

        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("approved session without order")));
    }

    [Fact]
    public async Task BackgroundService_RunOnceAsync_swallows_run_failures_so_the_next_tick_still_runs()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICardTenderReconciliationService>(_ => new ThrowingService());
        await using var provider = services.BuildServiceProvider();
        var background = new CardTenderReconciliationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CardTenderReconciliationOptions()),
            NullLogger<CardTenderReconciliationBackgroundService>.Instance);

        await background.RunOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BackgroundService_does_nothing_when_disabled()
    {
        var services = new ServiceCollection();
        var throwing = new ThrowingService();
        services.AddScoped<ICardTenderReconciliationService>(_ => throwing);
        await using var provider = services.BuildServiceProvider();
        var background = new CardTenderReconciliationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CardTenderReconciliationOptions { Enabled = false, InitialDelayMinutes = 0 }),
            NullLogger<CardTenderReconciliationBackgroundService>.Instance);

        await background.StartAsync(CancellationToken.None);
        await background.StopAsync(CancellationToken.None);

        Assert.Equal(0, throwing.Calls);
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("a%20b", "a!%20b")]
    [InlineData("a_b[1]", "a!_b![1]")]
    [InlineData("50!", "50!!")]
    public void EscapeLike_escapes_every_wildcard_with_bang(string input, string expected)
    {
        Assert.Equal(expected, SqlSugarCardTenderReconciliationRepository.EscapeLike(input));
    }

    private static CardTenderReconciliationService CreateService(
        FakeRepository repository,
        CardTenderReconciliationOptions? options = null)
    {
        return new CardTenderReconciliationService(
            repository,
            Options.Create(options ?? new CardTenderReconciliationOptions()),
            new FixedTimeProvider(Now),
            NullLogger<CardTenderReconciliationService>.Instance);
    }

    private static CardTenderOrphanSessionCandidate Candidate(long id, string sessionId, long? amountCents = 500) => new()
    {
        Id = id,
        Environment = "Production",
        StoreCode = "S01",
        DeviceCode = "POS01",
        SessionId = sessionId,
        TxnRef = "2610090001",
        RequestTxnType = "P",
        RequestAmountCents = amountCents,
        CompletedAtUtc = Now.UtcDateTime.AddHours(-5)
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingService : ICardTenderReconciliationService
    {
        public int Calls { get; private set; }

        public Task<CardTenderReconciliationResult> RunAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("db down");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class FakeRepository : ICardTenderReconciliationRepository
    {
        public IReadOnlyList<CardTenderOrphanSessionCandidate> Candidates { get; init; } = [];

        public Dictionary<string, string> OrdersBySession { get; } = new(StringComparer.Ordinal);

        public int ResolvedCount { get; init; }

        public DateTime? LastAfter { get; private set; }

        public DateTime? LastBefore { get; private set; }

        public int? LastLimit { get; private set; }

        public List<(long SessionRowId, string OrderGuid)> Links { get; } = [];

        public List<CardTenderIssue> UpsertedIssues { get; } = [];

        public Task<IReadOnlyList<CardTenderOrphanSessionCandidate>> FindApprovedSessionsWithoutOrderAsync(
            DateTime completedAfterUtc, DateTime completedBeforeUtc, int limit, CancellationToken cancellationToken)
        {
            LastAfter = completedAfterUtc;
            LastBefore = completedBeforeUtc;
            LastLimit = limit;
            return Task.FromResult(Candidates);
        }

        public Task<string?> FindOrderGuidByBackendPaymentAsync(
            string environment, string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(OrdersBySession.GetValueOrDefault(sessionId));

        public Task<int> ResolveIssuesForLinkedSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ResolvedCount);

        public Task<string?> TryLinkSessionToOrderAsync(long sessionRowId, string orderGuid, CancellationToken cancellationToken)
        {
            Links.Add((sessionRowId, orderGuid));
            return Task.FromResult<string?>(orderGuid);
        }

        public Task UpsertIssuesAsync(IReadOnlyList<CardTenderIssue> issues, CancellationToken cancellationToken)
        {
            UpsertedIssues.AddRange(issues);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CardTenderSessionFact>> FindSessionsAsync(
            string environment, string storeCode, string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CardTenderSessionFact>>([]);
    }
}
