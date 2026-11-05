using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller 尝试表：付款前落库、乐观并发、只允许合法的状态转换、reference 唯一。
/// 用真实 SQLite 文件跑，因为崩溃恢复依赖的正是落盘后的数据。
/// </summary>
public sealed class LocalZellerPaymentAttemptRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 11, 5, 11, 20, 10, TimeSpan.FromHours(10));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-zeller-attempt-{Guid.NewGuid():N}.db");
    private LocalSqliteStore _store = null!;
    private LocalZellerPaymentAttemptRepository _repository = null!;

    public Task InitializeAsync()
    {
        _store = new LocalSqliteStore(_databasePath);
        _repository = new LocalZellerPaymentAttemptRepository(_store);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(_databasePath);

    [Fact]
    public async Task Create_and_get_round_trip_every_field()
    {
        var attempt = Attempt() with
        {
            Status = ZellerAttemptStatus.Approved,
            OperationGuid = Guid.NewGuid(),
            FailureKind = ZellerFailureKind.None,
            UnknownReason = ZellerUnknownReason.None,
            ReachedTerminal = true,
            ProcessingStarted = true,
            TransactionUuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c",
            OriginalTransactionUuid = "98b690b7-5eaa-41ec-8ff8-168c60364aac",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            ApprovalCode = "456321",
            Rrn = "264753970917",
            PanMasked = "…2902",
            Scheme = "VISA",
            Bin = "483028",
            MerchantId = "000000000143281",
            TerminalId = "00134553",
            ReceiptText = "  kippa-ring  \nPURCHASE  $1.00",
            ReceiptLink = "https://services.myzeller.com/member/receipts/au/65c4b343",
            TransactionTimestamp = new DateTimeOffset(2026, 11, 5, 11, 21, 10, 199, TimeSpan.FromHours(10)),
            VerificationRounds = 3,
            LateRecheckDueAt = Created.AddMinutes(1),
            LateRecheckedAt = Created.AddMinutes(2),
            CompletedAt = Created.AddSeconds(33)
        };

        await _repository.CreateAsync(attempt);
        var loaded = await _repository.GetAsync(attempt.AttemptGuid);

        Assert.NotNull(loaded);
        Assert.Equal(attempt, loaded);
    }

    [Fact]
    public async Task Missing_attempt_is_null()
    {
        Assert.Null(await _repository.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Update_increments_the_version_and_rejects_a_stale_snapshot()
    {
        var created = Attempt();
        await _repository.CreateAsync(created);
        var current = (await _repository.GetAsync(created.AttemptGuid))!;

        var submitted = await _repository.TryUpdateAsync(current, current with { Status = ZellerAttemptStatus.Submitted });
        var stale = await _repository.TryUpdateAsync(current, current with { Status = ZellerAttemptStatus.Canceled });

        Assert.NotNull(submitted);
        Assert.Equal(1, submitted.Version);
        Assert.Null(stale);
        Assert.Equal(ZellerAttemptStatus.Submitted, (await _repository.GetAsync(created.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task Two_writers_with_the_same_snapshot_only_one_wins()
    {
        await _repository.CreateAsync(Attempt(ZellerAttemptStatus.Submitted));
        var snapshot = (await _repository.GetAsync(Attempt().AttemptGuid))!;

        var results = await Task.WhenAll(
            _repository.TryUpdateAsync(snapshot, snapshot with { Status = ZellerAttemptStatus.Approved }),
            _repository.TryUpdateAsync(snapshot, snapshot with { Status = ZellerAttemptStatus.Unknown }));

        Assert.Single(results, result => result is not null);
    }

    [Theory]
    [InlineData(ZellerAttemptStatus.Pending, ZellerAttemptStatus.Approved)]
    [InlineData(ZellerAttemptStatus.Declined, ZellerAttemptStatus.Approved)]
    [InlineData(ZellerAttemptStatus.Approved, ZellerAttemptStatus.Canceled)]
    [InlineData(ZellerAttemptStatus.OrderCompleted, ZellerAttemptStatus.Unknown)]
    [InlineData(ZellerAttemptStatus.Unknown, ZellerAttemptStatus.Submitted)]
    public async Task Disallowed_transitions_are_rejected(ZellerAttemptStatus from, ZellerAttemptStatus to)
    {
        await _repository.CreateAsync(Attempt(from));
        var current = (await _repository.GetAsync(Attempt().AttemptGuid))!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.TryUpdateAsync(current, current with { Status = to }));
    }

    [Fact]
    public void Transition_table_allows_the_documented_paths()
    {
        Assert.True(ZellerAttemptTransitions.IsAllowed(ZellerAttemptStatus.Pending, ZellerAttemptStatus.Submitted));
        Assert.True(ZellerAttemptTransitions.IsAllowed(ZellerAttemptStatus.Submitted, ZellerAttemptStatus.Unknown));
        Assert.True(ZellerAttemptTransitions.IsAllowed(ZellerAttemptStatus.Unknown, ZellerAttemptStatus.Approved));
        Assert.True(ZellerAttemptTransitions.IsAllowed(ZellerAttemptStatus.Canceled, ZellerAttemptStatus.Unknown));
        Assert.True(ZellerAttemptTransitions.IsAllowed(ZellerAttemptStatus.Approved, ZellerAttemptStatus.OrderCompleted));
        Assert.False(ZellerAttemptTransitions.IsAllowed(ZellerAttemptStatus.Canceled, ZellerAttemptStatus.Approved));
        Assert.False(ZellerAttemptTransitions.IsAllowed(ZellerAttemptStatus.Declined, ZellerAttemptStatus.Unknown));
    }

    [Fact]
    public async Task Identity_and_amount_cannot_change_after_creation()
    {
        await _repository.CreateAsync(Attempt(ZellerAttemptStatus.Submitted));
        var current = (await _repository.GetAsync(Attempt().AttemptGuid))!;

        await Assert.ThrowsAsync<ArgumentException>(() => _repository.TryUpdateAsync(current, current with { AmountCents = 200 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.TryUpdateAsync(current, current with { Reference = "HBZ-00000000000000000000000000000001" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.TryUpdateAsync(current, current with { OrderDraftJson = "{\"changed\":true}" }));
    }

    [Fact]
    public async Task Two_attempts_can_never_share_a_reference()
    {
        await _repository.CreateAsync(Attempt());

        await Assert.ThrowsAsync<SqliteException>(() => _repository.CreateAsync(Attempt() with { AttemptGuid = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Invalid_attempts_are_rejected_before_writing()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.CreateAsync(Attempt() with { AmountCents = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.CreateAsync(Attempt() with { AttemptGuid = Guid.Empty }));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.CreateAsync(Attempt() with { Reference = " " }));
    }

    [Fact]
    public async Task Open_attempts_are_submitted_or_unknown_on_this_device_only()
    {
        var submitted = await CreateAsync(ZellerAttemptStatus.Submitted);
        var unknown = await CreateAsync(ZellerAttemptStatus.Unknown);
        await CreateAsync(ZellerAttemptStatus.Approved);
        await CreateAsync(ZellerAttemptStatus.Canceled);
        await CreateAsync(ZellerAttemptStatus.Pending);
        await CreateAsync(ZellerAttemptStatus.Unknown, deviceCode: "POS-02");

        var open = await _repository.ListOpenAsync("S001", "POS-01");

        Assert.Equal(
            new[] { submitted.AttemptGuid, unknown.AttemptGuid }.OrderBy(guid => guid),
            open.Select(attempt => attempt.AttemptGuid).OrderBy(guid => guid));
    }

    [Fact]
    public async Task Late_rechecks_due_are_unchecked_canceled_attempts_past_their_due_time()
    {
        var now = Created.AddMinutes(10);
        var due = await CreateAsync(ZellerAttemptStatus.Canceled, lateRecheckDueAt: now.AddSeconds(-1));
        await CreateAsync(ZellerAttemptStatus.Canceled, lateRecheckDueAt: now.AddSeconds(1));
        await CreateAsync(ZellerAttemptStatus.Canceled, lateRecheckDueAt: now.AddSeconds(-5), lateRecheckedAt: now);
        await CreateAsync(ZellerAttemptStatus.Canceled);
        await CreateAsync(ZellerAttemptStatus.Unknown, lateRecheckDueAt: now.AddSeconds(-5));

        var list = await _repository.ListLateRechecksDueAsync(now);

        Assert.Equal(due.AttemptGuid, Assert.Single(list).AttemptGuid);
    }

    [Fact]
    public async Task Open_refund_for_the_same_business_key_is_returned_instead_of_a_second_one()
    {
        var first = RefundAttempt("RETURN-1");
        var second = RefundAttempt("RETURN-1");

        var storedFirst = await _repository.CreateOrGetOpenRefundAsync(first);
        var storedSecond = await _repository.CreateOrGetOpenRefundAsync(second);

        Assert.Equal(first.AttemptGuid, storedFirst.AttemptGuid);
        Assert.Equal(first.AttemptGuid, storedSecond.AttemptGuid);
        Assert.Null(await _repository.GetAsync(second.AttemptGuid));
    }

    [Fact]
    public async Task Concurrent_refunds_for_the_same_business_key_create_only_one_attempt()
    {
        var attempts = Enumerable.Range(0, 6).Select(_ => RefundAttempt("RETURN-RACE")).ToArray();

        var stored = await Task.WhenAll(attempts.Select(attempt => _repository.CreateOrGetOpenRefundAsync(attempt)));

        Assert.Single(stored.Select(attempt => attempt.AttemptGuid).Distinct());
    }

    [Fact]
    public async Task Business_key_is_free_again_once_the_refund_has_an_outcome()
    {
        var first = await _repository.CreateOrGetOpenRefundAsync(RefundAttempt("RETURN-2"));
        var submitted = await _repository.TryUpdateAsync(first, first with { Status = ZellerAttemptStatus.Submitted });
        await _repository.TryUpdateAsync(submitted!, submitted! with { Status = ZellerAttemptStatus.Declined });
        var retry = RefundAttempt("RETURN-2");

        var stored = await _repository.CreateOrGetOpenRefundAsync(retry);

        Assert.Equal(retry.AttemptGuid, stored.AttemptGuid);
    }

    [Fact]
    public async Task Different_business_keys_do_not_block_each_other()
    {
        var a = await _repository.CreateOrGetOpenRefundAsync(RefundAttempt("RETURN-A"));
        var b = await _repository.CreateOrGetOpenRefundAsync(RefundAttempt("RETURN-B"));

        Assert.NotEqual(a.AttemptGuid, b.AttemptGuid);
    }

    [Fact]
    public async Task Only_keyed_refunds_can_be_deduplicated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.CreateOrGetOpenRefundAsync(Attempt()));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.CreateOrGetOpenRefundAsync(RefundAttempt("x") with { RefundBusinessKey = null }));
    }

    [Fact]
    public async Task Refund_target_and_business_key_cannot_change_after_creation()
    {
        var stored = await _repository.CreateOrGetOpenRefundAsync(RefundAttempt("RETURN-3"));

        await Assert.ThrowsAsync<ArgumentException>(() => _repository.TryUpdateAsync(stored, stored with { RefundBusinessKey = "OTHER" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.TryUpdateAsync(stored, stored with { OriginalTransactionUuid = "98b690b7-5eaa-41ec-8ff8-168c60364aac" }));
    }

    [Fact]
    public async Task Works_alongside_the_regular_schema_service()
    {
        await new LocalSchemaService(_store).InitializeAsync();

        await _repository.CreateAsync(Attempt());

        Assert.NotNull(await _repository.GetAsync(Attempt().AttemptGuid));
    }

    [Fact]
    public async Task Unrecognised_stored_values_are_read_conservatively()
    {
        await _repository.CreateAsync(Attempt(ZellerAttemptStatus.Submitted));
        await using (var connection = await _store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE LocalZellerPaymentAttempts SET Status = 'Weird', FailureKind = 'Weird', UnknownReason = 'Weird';";
            await command.ExecuteNonQueryAsync();
        }

        var loaded = (await _repository.GetAsync(Attempt().AttemptGuid))!;

        Assert.Equal(ZellerAttemptStatus.Unknown, loaded.Status);
        Assert.Equal(ZellerFailureKind.ResultUnknown, loaded.FailureKind);
        Assert.Equal(ZellerUnknownReason.SdkError, loaded.UnknownReason);
    }

    [Fact]
    public void Merging_a_queried_transaction_keeps_the_receipt_and_approval_code_already_stored()
    {
        // 查询回来的交易没有小票原文和批准码，不能把扣款返回时已经存下的值抹掉。
        var stored = Attempt() with { ApprovalCode = "456321", ReceiptText = "RECEIPT" };
        var queried = new ZellerTransaction(
            ZellerTransactionStatus.Approved,
            ZellerTransactionType.Purchase,
            100,
            "65c4b343-03e6-49ef-a54d-012e3f2c222c",
            stored.Reference,
            ResponseText: "Approved or completed successfully",
            ApprovalCode: "",
            ReceiptText: null);

        var merged = stored.WithTransaction(queried);

        Assert.Equal("456321", merged.ApprovalCode);
        Assert.Equal("RECEIPT", merged.ReceiptText);
        Assert.Equal("Approved or completed successfully", merged.ResponseText);
        Assert.Equal("65c4b343-03e6-49ef-a54d-012e3f2c222c", merged.TransactionUuid);
    }

    private static readonly Guid FixedGuid = Guid.Parse("799287a3-3dc2-4df1-bca3-d33eea19b35a");

    private static LocalZellerPaymentAttempt Attempt(ZellerAttemptStatus status = ZellerAttemptStatus.Pending) => new(
        FixedGuid,
        ZellerAttemptOperation.Sale,
        "HBZ-799287a33dc24df1bca3d33eea19b35a",
        100,
        status,
        "{\"draft\":1}",
        "S001",
        "POS-01",
        "C001",
        Created,
        Created);

    private static LocalZellerPaymentAttempt RefundAttempt(string businessKey)
    {
        var guid = Guid.NewGuid();
        return Attempt() with
        {
            AttemptGuid = guid,
            OperationKind = ZellerAttemptOperation.Refund,
            Reference = $"HBZR-{guid:N}",
            RefundBusinessKey = businessKey,
            OriginalTransactionUuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c"
        };
    }

    private async Task<LocalZellerPaymentAttempt> CreateAsync(
        ZellerAttemptStatus status,
        string deviceCode = "POS-01",
        DateTimeOffset? lateRecheckDueAt = null,
        DateTimeOffset? lateRecheckedAt = null)
    {
        var guid = Guid.NewGuid();
        var attempt = Attempt(status) with
        {
            AttemptGuid = guid,
            Reference = $"HBZ-{guid:N}",
            DeviceCode = deviceCode,
            LateRecheckDueAt = lateRecheckDueAt,
            LateRecheckedAt = lateRecheckedAt
        };
        await _repository.CreateAsync(attempt);
        return attempt;
    }
}
