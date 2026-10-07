using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Logging;

namespace Hbpos.Api.Tests;

public sealed class LinklySettlementSyncServiceTests
{
    private const string OfficialFixedWidthSettlement =
        "000000002138VISA                000000100001000000100001000000100001+000000300003" +
        "DEBIT               000000100001000000100001000000100001+000000300003" +
        "069TOTAL               000000300001000000300001000000300001+000000900009";

    private static readonly DateTimeOffset RequestedAt =
        new(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CompletedAt = RequestedAt.AddMinutes(1);

    [Fact]
    public async Task SyncAsync_accepts_new_snapshot_and_is_idempotent_for_the_same_revision()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1);

        var first = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);
        var second = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.False(first.AlreadySynced);
        Assert.True(second.AlreadySynced);
        Assert.Equal(1, second.AcceptedRevision);
        Assert.Single(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_rejects_different_content_for_the_same_revision()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1);
        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(
                request with { ResponseText = "DIFFERENT" },
                "S001",
                "POS-01",
                CancellationToken.None));

        Assert.Equal("REVISION_CONTENT_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_treats_an_older_revision_as_already_synced_without_overwrite()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var current = CreateRequest(status: "Succeeded", revision: 2);
        await service.SyncAsync(current, "S001", "POS-01", CancellationToken.None);

        var response = await service.SyncAsync(
            current with { ClientRevision = 1, ResponseText = "STALE" },
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.True(response.AlreadySynced);
        Assert.Equal(2, response.AcceptedRevision);
        Assert.Equal("APPROVED", Assert.Single(repository.Records).ResponseText);
    }

    [Fact]
    public async Task SyncAsync_allows_unknown_to_final_then_print_audit_progression()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var unknown = CreateRequest(status: "Unknown", revision: 1) with
        {
            ResponseCode = null,
            ResponseText = "Result unknown",
            SettlementData = null,
            ReceiptTexts = [],
            CompletedAt = null
        };
        await service.SyncAsync(unknown, "S001", "POS-01", CancellationToken.None);
        var final = unknown with
        {
            Status = "Succeeded",
            ResponseCode = "00",
            ResponseText = "APPROVED",
            SettlementData = "TOTAL=10.00",
            ReceiptTexts = ["SETTLEMENT RECEIPT"],
            CompletedAt = CompletedAt,
            ClientRevision = 2
        };
        await service.SyncAsync(final, "S001", "POS-01", CancellationToken.None);
        var printedAt = CompletedAt.AddMinutes(1);

        var response = await service.SyncAsync(
            final with
            {
                FirstPrintedAt = printedAt,
                LastPrintedAt = printedAt,
                PrintCount = 1,
                ClientRevision = 3
            },
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.False(response.AlreadySynced);
        var stored = Assert.Single(repository.Records);
        Assert.Equal("Succeeded", stored.Status);
        Assert.Equal(1, stored.PrintCount);
    }

    [Fact]
    public async Task SyncAsync_rejects_higher_revision_that_rewrites_final_bank_evidence()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var final = CreateRequest(status: "Succeeded", revision: 1);
        await service.SyncAsync(final, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(
                final with { ResponseText = "CHANGED", ClientRevision = 2 },
                "S001",
                "POS-01",
                CancellationToken.None));

        Assert.Equal("BANK_EVIDENCE_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_sanitizes_free_text_card_shapes_before_persistence()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1) with
        {
            ResponseText = "BATCH 123456789012",
            SettlementData = "MERCHANT 123456789012",
            ReceiptTexts = ["CARD 4111111111111111"]
        };

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var stored = Assert.Single(repository.Records);
        Assert.Equal("BATCH 123456789012", stored.ResponseText);
        Assert.Equal("MERCHANT ****9012", stored.SettlementData);
        Assert.Contains("****1111", stored.ReceiptTextsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_preserves_official_fixed_width_settlement_for_central_parsing()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1) with
        {
            SettlementData = OfficialFixedWidthSettlement,
        };

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.Equal(OfficialFixedWidthSettlement, Assert.Single(repository.Records).SettlementData);
    }

    [Fact]
    public async Task SyncAsync_allows_a_failed_unsubmitted_cloud_settlement_without_a_provider_session()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Failed", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = null,
            ProviderSubmissionState = ProviderSubmissionState.NotSubmitted
        };

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(0, repository.CloudBackendLookupCount);
        Assert.Equal("NotSubmitted", Assert.Single(repository.Records).ProviderSubmissionState);
    }

    [Fact]
    public async Task SyncAsync_inferrs_the_legacy_unsubmitted_cloud_failure_state()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Failed", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = null,
            ProviderSubmissionState = null
        };

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.Equal("NotSubmitted", Assert.Single(repository.Records).ProviderSubmissionState);
    }

    [Fact]
    public async Task SyncAsync_inferrs_legacy_local_success_as_submitted_without_provider_session()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1) with
        {
            ProviderSessionId = null,
            ProviderSubmissionState = null
        };

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.Equal("Submitted", Assert.Single(repository.Records).ProviderSubmissionState);
    }

    [Fact]
    public async Task SyncAsync_rejects_a_submitted_cloud_failure_without_a_provider_session()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Failed", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = null,
            ProviderSubmissionState = ProviderSubmissionState.Submitted
        };

        var exception = await Assert.ThrowsAsync<LinklySettlementValidationException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("PROVIDER_SESSION_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_rejects_a_pending_cloud_settlement_with_a_final_submission_state()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Unknown", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = null,
            CompletedAt = null,
            ProviderSubmissionState = ProviderSubmissionState.Submitted
        };

        var exception = await Assert.ThrowsAsync<LinklySettlementValidationException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("INVALID_PROVIDER_SUBMISSION_STATE", exception.Code);
    }

    [Theory]
    [InlineData("LocalIp", "Succeeded", ProviderSubmissionState.NotSubmitted)]
    [InlineData("CloudDirectSync", "Failed", ProviderSubmissionState.Unknown)]
    public async Task SyncAsync_rejects_invalid_final_submission_state_for_local_modes(
        string connectionMode,
        string status,
        ProviderSubmissionState providerSubmissionState)
    {
        var service = CreateService(new FakeRepository());
        var request = CreateRequest(status, revision: 1) with
        {
            ConnectionMode = connectionMode,
            ProviderSubmissionState = providerSubmissionState
        };

        var exception = await Assert.ThrowsAsync<LinklySettlementValidationException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("INVALID_PROVIDER_SUBMISSION_STATE", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_links_existing_cloud_backend_session_without_rewriting_the_client_snapshot()
    {
        var repository = new FakeRepository
        {
            CloudBackendFact = new LinklyCloudBackendSettlementFact
            {
                Id = 42,
                Status = "Completed",
                OperationSuccess = true,
                ResponseCode = "00",
                SettlementReceiptTexts = "[\"SETTLEMENT RECEIPT\"]"
            }
        };
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = "backend-session-1",
            ResponseText = "CLIENT APPROVED"
        };

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(1, repository.CloudBackendLookupCount);
        var stored = Assert.Single(repository.Records);
        Assert.Equal(42, stored.CloudBackendSessionId);
        Assert.Equal("Succeeded", stored.Status);
        Assert.Equal("CLIENT APPROVED", stored.ResponseText);
        Assert.Equal(CompletedAt, stored.CompletedAtUtc);
    }

    [Fact]
    public async Task SyncAsync_accepts_failed_cloud_backend_session_without_receipt()
    {
        var repository = new FakeRepository
        {
            CloudBackendFact = new LinklyCloudBackendSettlementFact
            {
                Id = 42,
                Status = "Completed",
                OperationSuccess = false,
                ResponseCode = "05"
            }
        };
        var service = CreateService(repository);
        var request = CreateRequest(status: "Failed", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = "backend-session-1",
            ProviderSubmissionState = ProviderSubmissionState.Submitted
        };

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal("Failed", Assert.Single(repository.Records).Status);
    }

    [Fact]
    public async Task SyncAsync_rejects_final_cloud_backend_snapshot_until_the_linked_session_is_final()
    {
        var repository = new FakeRepository
        {
            CloudBackendFact = new LinklyCloudBackendSettlementFact
            {
                Id = 42,
                Status = "Pending"
            }
        };
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = "backend-session-1"
        };

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("CLOUD_BACKEND_SESSION_NOT_FINAL", exception.Code);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_rejects_cloud_backend_result_that_disagrees_with_the_linked_session()
    {
        var repository = new FakeRepository
        {
            CloudBackendFact = new LinklyCloudBackendSettlementFact
            {
                Id = 42,
                Status = "Completed",
                OperationSuccess = false,
                SettlementReceiptTexts = "[\"DECLINED RECEIPT\"]"
            }
        };
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = "backend-session-1"
        };

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("CLOUD_BACKEND_RESULT_CONFLICT", exception.Code);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_rejects_cloud_backend_provider_session_outside_existing_facts()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Unknown", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = "missing-session",
            CompletedAt = null
        };

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("CLOUD_BACKEND_SESSION_NOT_FOUND", exception.Code);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_does_not_create_a_second_record_for_the_same_provider_session()
    {
        var repository = new FakeRepository
        {
            CloudBackendFact = new LinklyCloudBackendSettlementFact
            {
                Id = 42,
                Status = "Completed",
                OperationSuccess = true,
                ResponseCode = "00",
                SettlementReceiptTexts = "[\"SETTLEMENT RECEIPT\"]"
            }
        };
        var service = CreateService(repository);
        var first = CreateRequest(status: "Succeeded", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = "backend-session-1"
        };
        await service.SyncAsync(first, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(
                first with { SettlementGuid = Guid.NewGuid() },
                "S001",
                "POS-01",
                CancellationToken.None));

        Assert.Equal("PROVIDER_SESSION_CONFLICT", exception.Code);
        Assert.Single(repository.Records);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Q1")]
    public async Task SyncAsync_treats_cloud_backend_success_flag_without_approved_code_as_failed(string? responseCode)
    {
        var repository = new FakeRepository
        {
            CloudBackendFact = new LinklyCloudBackendSettlementFact
            {
                Id = 42,
                Status = "Completed",
                OperationSuccess = true,
                ResponseCode = responseCode,
                SettlementReceiptTexts = "[\"SETTLEMENT RECEIPT\"]"
            }
        };
        var service = CreateService(repository);
        var request = CreateRequest(status: "Failed", revision: 1) with
        {
            ConnectionMode = "CloudBackendAsync",
            ProviderSessionId = "backend-session-1"
        };

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal("Failed", Assert.Single(repository.Records).Status);
    }

    // 2026-10 线上 1042 测试机 BD0F3F55 的真实时间：设备本地库是原值，服务端库里是被 SQL datetime 舍入后的值。
    private static readonly DateTimeOffset ProductionRequestedAt =
        new DateTimeOffset(2026, 10, 2, 6, 48, 52, TimeSpan.Zero).AddTicks(9574255);

    private static readonly DateTimeOffset ProductionCompletedAt =
        new DateTimeOffset(2026, 10, 2, 6, 49, 3, TimeSpan.Zero).AddTicks(375697);

    [Fact]
    public void LegacyRound_reproduces_the_values_stored_in_production_before_the_fix()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 10, 2, 6, 48, 52, TimeSpan.Zero).AddTicks(9566667),
            LegacyRound(ProductionRequestedAt));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 2, 6, 49, 3, TimeSpan.Zero).AddTicks(366667),
            LegacyRound(ProductionCompletedAt));
        Assert.True(LinklySettlementSyncService.IsOnLegacySqlDateTimeGrid(LegacyRound(ProductionRequestedAt).UtcDateTime));
        Assert.False(LinklySettlementSyncService.IsOnLegacySqlDateTimeGrid(ProductionRequestedAt.UtcDateTime));
    }

    [Fact]
    public async Task SyncAsync_accepts_higher_revision_when_stored_timestamps_were_rounded_by_legacy_datetime_writes()
    {
        // 复现线上：修订 4 已落库（时间被旧仓储舍入），打印失败产生修订 5 再上传，修复前必然 IMMUTABLE_FIELDS_CONFLICT。
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var revision4 = CreateProductionRequest(revision: 4);
        await service.SyncAsync(revision4, "S001", "POS-01", CancellationToken.None);
        SimulateLegacyStoredTimestamps(Assert.Single(repository.Records));

        var response = await service.SyncAsync(
            revision4 with { ClientRevision = 5, LastPrintError = "Printer port could not be opened." },
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.Equal(5, response.AcceptedRevision);
        var stored = Assert.Single(repository.Records);
        Assert.Equal(5, stored.ClientRevision);
        Assert.Equal("Printer port could not be opened.", stored.LastPrintError);
    }

    [Fact]
    public async Task SyncAsync_treats_same_revision_retry_as_idempotent_when_stored_timestamps_were_rounded()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var revision4 = CreateProductionRequest(revision: 4);
        await service.SyncAsync(revision4, "S001", "POS-01", CancellationToken.None);
        SimulateLegacyStoredTimestamps(Assert.Single(repository.Records));

        var retry = await service.SyncAsync(revision4, "S001", "POS-01", CancellationToken.None);

        Assert.True(retry.Accepted);
        Assert.True(retry.AlreadySynced);
        Assert.Equal(4, retry.AcceptedRevision);
    }

    [Fact]
    public async Task SyncAsync_accepts_print_progress_when_legacy_rounding_moved_stored_print_time_forward()
    {
        // .0398 秒按 1/300 秒舍入后进位成 .0400 秒：库里的末次打印时间比客户端原值晚，修复前会被判成「打印审计倒退」。
        var printedAt = new DateTimeOffset(2026, 10, 2, 6, 50, 0, TimeSpan.Zero).AddTicks(398000);
        Assert.True(LegacyRound(printedAt) > printedAt);
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var printed = CreateProductionRequest(revision: 5) with
        {
            FirstPrintedAt = printedAt,
            LastPrintedAt = printedAt,
            PrintCount = 1
        };
        await service.SyncAsync(printed, "S001", "POS-01", CancellationToken.None);
        SimulateLegacyStoredTimestamps(Assert.Single(repository.Records));

        var response = await service.SyncAsync(
            printed with { ClientRevision = 6, LastPrintError = "Paper out." },
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.Equal(6, response.AcceptedRevision);
        Assert.False(response.AlreadySynced);
    }

    [Fact]
    public async Task SyncAsync_still_rejects_a_real_requested_at_change_and_reports_both_values()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var revision4 = CreateProductionRequest(revision: 4);
        await service.SyncAsync(revision4, "S001", "POS-01", CancellationToken.None);
        SimulateLegacyStoredTimestamps(Assert.Single(repository.Records));

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(
                revision4 with { ClientRevision = 5, RequestedAt = ProductionRequestedAt.AddMilliseconds(5) },
                "S001",
                "POS-01",
                CancellationToken.None));

        Assert.Equal("IMMUTABLE_FIELDS_CONFLICT", exception.Code);
        Assert.Equal(
            "field=RequestedAtUtc stored=2026-10-02T06:48:52.9566667Z incoming=2026-10-02T06:48:52.9624255Z",
            exception.Detail);
    }

    [Fact]
    public async Task SyncAsync_does_not_tolerate_millisecond_drift_for_full_precision_rows()
    {
        // 容差只给落在 1/300 秒刻度上的旧行；修复后按 DATETIME2 写入的新行仍要求精确相等。
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var revision4 = CreateProductionRequest(revision: 4);
        await service.SyncAsync(revision4, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(
                revision4 with { ClientRevision = 5, RequestedAt = ProductionRequestedAt.AddMilliseconds(1) },
                "S001",
                "POS-01",
                CancellationToken.None));

        Assert.Equal("IMMUTABLE_FIELDS_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_conflict_detail_names_the_field_but_never_echoes_receipt_text()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest(status: "Succeeded", revision: 1);
        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<LinklySettlementConflictException>(() =>
            service.SyncAsync(
                request with { ReceiptTexts = ["CARD 4111 SECRET"] },
                "S001",
                "POS-01",
                CancellationToken.None));

        Assert.Equal("REVISION_CONTENT_CONFLICT", exception.Code);
        Assert.StartsWith("field=ReceiptTextsJson storedLength=", exception.Detail);
        Assert.DoesNotContain("SECRET", exception.Detail);
        Assert.DoesNotContain("SETTLEMENT RECEIPT", exception.Detail);
    }

    [Fact]
    public async Task SyncAsync_logs_every_accepted_outcome_with_scope_and_revisions()
    {
        var repository = new FakeRepository();
        var logger = new RecordingLogger<LinklySettlementSyncService>();
        var service = CreateService(repository, logger);
        var request = CreateRequest(status: "Succeeded", revision: 2);

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);
        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);
        await service.SyncAsync(request with { ClientRevision = 1 }, "S001", "POS-01", CancellationToken.None);
        await service.SyncAsync(request with { ClientRevision = 3, LastPrintError = "Paper out." }, "S001", "POS-01", CancellationToken.None);

        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.Collection(
            logger.Entries,
            entry => Assert.Contains($"result=Inserted store=S001 device=POS-01 settlement={request.SettlementGuid} revision=2 storedRevision=", entry.Message),
            entry => Assert.Contains("result=DuplicateRevision", entry.Message),
            entry => Assert.Contains("result=StaleRevision", entry.Message),
            entry => Assert.Contains("result=Updated", entry.Message));
    }

    private static LinklySettlementSyncRequest CreateProductionRequest(long revision)
    {
        return CreateRequest(status: "Succeeded", revision: revision) with
        {
            BusinessDate = new DateOnly(2026, 10, 2),
            RequestedAt = ProductionRequestedAt,
            CompletedAt = ProductionCompletedAt
        };
    }

    /// <summary>模拟修复前：SqlClient 按 SQL datetime 发送（一天内的时间按 1/300 秒四舍五入），落库后以 DATETIME2(7) 读回。</summary>
    private static DateTimeOffset LegacyRound(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        var dayStart = utc.Ticks - utc.Ticks % TimeSpan.TicksPerDay;
        var sqlTicks = (long)((utc.Ticks - dayStart) / (double)TimeSpan.TicksPerMillisecond * 0.3 + 0.5);
        var restored = (long)Math.Round(sqlTicks * TimeSpan.TicksPerSecond / 300d, MidpointRounding.AwayFromZero);
        return new DateTimeOffset(dayStart + restored, TimeSpan.Zero);
    }

    private static void SimulateLegacyStoredTimestamps(PosmLinklySettlementRecord stored)
    {
        stored.RequestedAtUtc = LegacyRound(stored.RequestedAtUtc);
        stored.CompletedAtUtc = stored.CompletedAtUtc is { } completed ? LegacyRound(completed) : null;
        stored.FirstPrintedAtUtc = stored.FirstPrintedAtUtc is { } first ? LegacyRound(first) : null;
        stored.LastPrintedAtUtc = stored.LastPrintedAtUtc is { } last ? LegacyRound(last) : null;
    }

    private static LinklySettlementSyncService CreateService(
        FakeRepository repository,
        ILogger<LinklySettlementSyncService>? logger = null)
    {
        return new LinklySettlementSyncService(
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero)),
            logger);
    }

    private static LinklySettlementSyncRequest CreateRequest(string status, long revision)
    {
        return new LinklySettlementSyncRequest(
            1,
            Guid.NewGuid(),
            "S001",
            "POS-01",
            new DateOnly(2026, 8, 1),
            "LocalIp",
            "Production",
            "provider-session-1",
            status,
            "00",
            "APPROVED",
            "TOTAL=10.00",
            ["SETTLEMENT RECEIPT"],
            RequestedAt,
            status is "Succeeded" or "Failed" ? CompletedAt : null,
            null,
            null,
            0,
            null,
            revision);
    }

    private sealed class FakeRepository : ILinklySettlementRepository
    {
        public List<PosmLinklySettlementRecord> Records { get; } = [];

        public LinklyCloudBackendSettlementFact? CloudBackendFact { get; set; }

        public int CloudBackendLookupCount { get; private set; }

        public Task<PosmLinklySettlementRecord?> GetAsync(
            string storeCode,
            string deviceCode,
            Guid settlementGuid,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Records.FirstOrDefault(record =>
                record.StoreCode == storeCode &&
                record.DeviceCode == deviceCode &&
                record.SettlementGuid == settlementGuid));
        }

        public Task<PosmLinklySettlementRecord?> GetByProviderSessionAsync(
            string connectionMode,
            string environment,
            string storeCode,
            string deviceCode,
            string providerSessionId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Records.FirstOrDefault(record =>
                record.ConnectionMode == connectionMode &&
                record.Environment == environment &&
                record.StoreCode == storeCode &&
                record.DeviceCode == deviceCode &&
                record.ProviderSessionId == providerSessionId));
        }

        public Task<LinklyCloudBackendSettlementFact?> GetCloudBackendSettlementAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string providerSessionId,
            CancellationToken cancellationToken)
        {
            CloudBackendLookupCount++;
            return Task.FromResult(CloudBackendFact);
        }

        public Task<bool> TryInsertAsync(
            PosmLinklySettlementRecord settlement,
            CancellationToken cancellationToken)
        {
            if (Records.Any(record =>
                    record.StoreCode == settlement.StoreCode &&
                    record.DeviceCode == settlement.DeviceCode &&
                    (record.SettlementGuid == settlement.SettlementGuid ||
                     record.ProviderSessionId is not null &&
                     record.ProviderSessionId == settlement.ProviderSessionId)))
            {
                return Task.FromResult(false);
            }

            settlement.Id = Records.Count + 1;
            Records.Add(settlement);
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateAsync(
            PosmLinklySettlementRecord settlement,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var index = Records.FindIndex(record =>
                record.StoreCode == settlement.StoreCode &&
                record.DeviceCode == settlement.DeviceCode &&
                record.SettlementGuid == settlement.SettlementGuid &&
                record.ClientRevision == expectedRevision);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Records[index] = settlement;
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
