using Hbpos.Api.Services;
using Hbpos.Contracts.CashMovements;

namespace Hbpos.Api.Tests;

public sealed class CashMovementSyncServiceTests
{
    private static readonly TimeSpan StoreOffset = TimeSpan.FromHours(11);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = new(2026, 11, 13, 14, 5, 0, StoreOffset);

    [Fact]
    public async Task SyncAsync_inserts_a_safe_drop_with_normalized_fields()
    {
        var repository = new FakeRepository();

        var response = await CreateService(repository).SyncAsync(
            Request(SafeDrop() with { Note = "  交给店长 Amy  " }),
            "S001",
            "POS-01",
            CancellationToken.None);

        var result = Assert.Single(response.Results);
        Assert.Equal(CashMovementSyncStatuses.Accepted, result.Status);
        var stored = Assert.Single(repository.Records);
        Assert.Equal("Out", stored.Direction);
        Assert.Equal("SafeDrop", stored.ReasonCode);
        Assert.Equal(400m, stored.Amount);
        Assert.Equal("交给店长 Amy", stored.Note);
        Assert.Equal("Wpf", stored.ClientKind);
        Assert.Equal(new DateTime(2026, 11, 13), stored.BusinessDate);
        Assert.Equal(OccurredAt.UtcDateTime, stored.OccurredAtUtc);
        Assert.Equal(Now.UtcDateTime, stored.ReceivedAtUtc);
    }

    [Fact]
    public async Task SyncAsync_is_idempotent_and_flags_content_conflicts_per_item()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        await service.SyncAsync(Request(SafeDrop()), "S001", "POS-01", CancellationToken.None);

        var retry = await service.SyncAsync(Request(SafeDrop()), "S001", "POS-01", CancellationToken.None);
        Assert.Equal(CashMovementSyncStatuses.AlreadySynced, Assert.Single(retry.Results).Status);

        var changed = await service.SyncAsync(Request(SafeDrop() with { Amount = 401m }), "S001", "POS-01", CancellationToken.None);
        Assert.Equal("CASH_MOVEMENT_CONTENT_CONFLICT", Assert.Single(changed.Results).Code);

        var otherDevice = await service.SyncAsync(Request(SafeDrop(), device: "POS-02"), "S001", "POS-02", CancellationToken.None);
        Assert.Equal("CASH_MOVEMENT_SCOPE_CONFLICT", Assert.Single(otherDevice.Results).Code);
        Assert.Single(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_accepts_a_void_in_the_same_batch_and_rejects_a_second_void()
    {
        var repository = new FakeRepository();
        var original = SafeDrop();
        var reversal = Void(original);

        var response = await CreateService(repository).SyncAsync(
            Request(original, reversal),
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.All(response.Results, item => Assert.Equal(CashMovementSyncStatuses.Accepted, item.Status));
        Assert.Equal(2, repository.Records.Count);

        var secondVoid = await CreateService(repository).SyncAsync(
            Request(Void(original) with { MovementGuid = Guid.NewGuid() }),
            "S001",
            "POS-01",
            CancellationToken.None);
        Assert.Equal("ALREADY_VOIDED", Assert.Single(secondVoid.Results).Code);
    }

    [Fact]
    public async Task SyncAsync_rejects_voids_that_do_not_mirror_the_original()
    {
        var repository = new FakeRepository();
        var original = SafeDrop();
        await CreateService(repository).SyncAsync(Request(original), "S001", "POS-01", CancellationToken.None);

        var wrongAmount = await CreateService(repository).SyncAsync(
            Request(Void(original) with { Amount = 300m }),
            "S001",
            "POS-01",
            CancellationToken.None);
        Assert.Equal("INVALID_VOID", Assert.Single(wrongAmount.Results).Code);

        var missing = await CreateService(repository).SyncAsync(
            Request(Void(original) with { VoidsMovementGuid = Guid.NewGuid() }),
            "S001",
            "POS-01",
            CancellationToken.None);
        Assert.Equal("VOIDED_MOVEMENT_NOT_FOUND", Assert.Single(missing.Results).Code);
    }

    [Theory]
    [InlineData("Out", "ChangeTopUp", 50, "x", "DIRECTION_REASON_MISMATCH")]
    [InlineData("In", "SafeDrop", 50, null, "DIRECTION_REASON_MISMATCH")]
    [InlineData("Out", "Unknown", 50, null, "INVALID_REASON_CODE")]
    [InlineData("Sideways", "SafeDrop", 50, null, "INVALID_DIRECTION")]
    [InlineData("Out", "SafeDrop", 0, null, "INVALID_AMOUNT")]
    [InlineData("Out", "SafeDrop", 10.005, null, "INVALID_AMOUNT")]
    [InlineData("Out", "PaidOut", 18.5, null, "NOTE_REQUIRED")]
    [InlineData("Out", "PaidOut", 18.5, "牛", "NOTE_REQUIRED")]
    [InlineData("In", "OtherIn", 5, " ", "NOTE_REQUIRED")]
    public async Task SyncAsync_rejects_invalid_items_without_blocking_the_rest(
        string direction,
        string reason,
        double amount,
        string? note,
        string expectedCode)
    {
        var repository = new FakeRepository();
        var bad = SafeDrop() with
        {
            MovementGuid = Guid.NewGuid(),
            Direction = direction,
            ReasonCode = reason,
            Amount = (decimal)amount,
            Note = note
        };

        var response = await CreateService(repository).SyncAsync(
            Request(bad, SafeDrop()),
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.Equal(expectedCode, response.Results[0].Code);
        Assert.Equal(CashMovementSyncStatuses.Rejected, response.Results[0].Status);
        Assert.Equal(CashMovementSyncStatuses.Accepted, response.Results[1].Status);
        Assert.Single(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_accepts_paid_out_and_change_top_up_with_notes()
    {
        var repository = new FakeRepository();
        var paidOut = SafeDrop() with
        {
            MovementGuid = Guid.NewGuid(),
            ReasonCode = "PaidOut",
            Amount = 18.50m,
            Note = "Woolworths 牛奶 2 瓶"
        };
        var topUp = SafeDrop() with
        {
            MovementGuid = Guid.NewGuid(),
            Direction = "In",
            ReasonCode = "ChangeTopUp",
            Amount = 50m,
            Note = null
        };

        var response = await CreateService(repository).SyncAsync(Request(paidOut, topUp), "S001", "POS-01", CancellationToken.None);

        Assert.All(response.Results, item => Assert.Equal(CashMovementSyncStatuses.Accepted, item.Status));
    }

    [Theory]
    [InlineData(2, "Wpf", 1)]
    [InlineData(1, "Kiosk", 1)]
    [InlineData(1, "Wpf", 0)]
    [InlineData(1, "Wpf", 101)]
    public async Task SyncAsync_rejects_invalid_requests_as_a_whole(int schemaVersion, string clientKind, int count)
    {
        var movements = Enumerable.Range(0, count)
            .Select(_ => SafeDrop() with { MovementGuid = Guid.NewGuid() })
            .ToList();
        var request = new CashMovementSyncRequest(schemaVersion, "S001", "POS-01", clientKind, "1.0.120", movements);

        await Assert.ThrowsAsync<DailyCloseValidationException>(() =>
            CreateService(new FakeRepository()).SyncAsync(request, "S001", "POS-01", CancellationToken.None));
    }

    private static CashMovementSyncService CreateService(FakeRepository repository) =>
        new(repository, new FixedTimeProvider(Now));

    private static CashMovementSyncRequest Request(params CashMovementSync[] movements) =>
        new(1, "S001", "POS-01", "wpf", "1.0.120", movements);

    private static CashMovementSyncRequest Request(CashMovementSync movement, string device) =>
        new(1, "S001", device, "Wpf", "1.0.120", [movement]);

    private static CashMovementSync SafeDrop() => new(
        MovementGuid: Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
        Direction: "Out",
        ReasonCode: "SafeDrop",
        Amount: 400m,
        Note: "交给店长 Amy",
        VoidsMovementGuid: null,
        BusinessDate: new DateOnly(2026, 11, 13),
        OccurredAt: OccurredAt,
        CashierId: "C001",
        CashierName: "Lily",
        AuthorizedById: null,
        AuthorizedByName: null,
        DisplayNumber: "CM-0009");

    private static CashMovementSync Void(CashMovementSync original) => original with
    {
        MovementGuid = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444"),
        Direction = original.Direction == "Out" ? "In" : "Out",
        Note = "金额输错",
        VoidsMovementGuid = original.MovementGuid,
        OccurredAt = original.OccurredAt.AddMinutes(2),
        DisplayNumber = "CM-0010"
    };

    /// <summary>内存仓储：模拟两个唯一索引（Guid、冲正目标）。</summary>
    private sealed class FakeRepository : ICashMovementRepository
    {
        public List<PosmCashMovementRecord> Records { get; } = [];

        public Task<PosmCashMovementRecord?> GetByGuidAsync(Guid movementGuid, CancellationToken cancellationToken) =>
            Task.FromResult(Records.FirstOrDefault(item => item.MovementGuid == movementGuid));

        public Task<CashMovementInsertOutcome> TryInsertAsync(PosmCashMovementRecord record, CancellationToken cancellationToken)
        {
            if (Records.Any(item => item.MovementGuid == record.MovementGuid))
            {
                return Task.FromResult(CashMovementInsertOutcome.DuplicateGuid);
            }

            if (record.VoidsMovementGuid is { } voids && Records.Any(item => item.VoidsMovementGuid == voids))
            {
                return Task.FromResult(CashMovementInsertOutcome.AlreadyVoided);
            }

            Records.Add(record);
            return Task.FromResult(CashMovementInsertOutcome.Inserted);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
