using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LinklySettlementServiceTests
{
    [Fact]
    public async Task Settlement_holds_selection_gate_until_terminal_submission_completes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-gate-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "done"))
            {
                SettlementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                DeferredSettlementResult = new TaskCompletionSource<LinklySettlementResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            using var gate = new LinklyTerminalSelectionTransitionGate();
            var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                new LocalLinklySettlementRepository(store),
                new FakeLinklyBankReceiptPrinter(),
                linklyTerminalSelectionTransitionGate: gate);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            var settling = service.SettleAndPrintAsync(session, DateTime.Today);
            await terminal.SettlementStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.Null(await gate.TryEnterAssignmentAsync());

            terminal.DeferredSettlementResult.SetResult(new LinklySettlementResult(false, "declined"));
            await settling;
            await using var assignmentLease = await gate.TryEnterAssignmentAsync();
            Assert.NotNull(assignmentLease);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Settle_then_reprint_persists_once_and_does_not_submit_settlement_again()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-service-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                Environment = CardTerminalEnvironment.Production,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(
                true,
                "Settlement complete",
                SessionId: "backend-settlement-001",
                ResponseCode: "00",
                ResponseText: "Approved",
                SettlementData: "Totals: 3",
                ReceiptTexts: ["MERCHANT COPY\nCARD 4111 1111 1111 1111"],
                ProviderSubmissionState: ProviderSubmissionState.Submitted));
            var printer = new FakeLinklyBankReceiptPrinter();
            var backend = new FakeLinklyBackendTerminalClient();
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                printer,
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            var execution = await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(execution.PrintResult?.Succeeded);
            Assert.Equal(1, terminal.SettlementCallCount);
            Assert.Equal(1, printer.PrintCallCount);
            Assert.Equal(LinklyBankReceiptKind.Settlement, printer.LastKind);
            Assert.Equal(1, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(1, backend.MarkReceiptPrintedCallCount);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, stored.Status);
            Assert.Equal(1, stored.PrintCount);
            Assert.Equal(
                "MERCHANT COPY\nCARD ****1111",
                Assert.Single(stored.ReceiptTexts).Replace("\r\n", "\n", StringComparison.Ordinal));

            var reprint = await service.ReprintAsync(stored);
            var reprinted = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(reprint.Succeeded);
            Assert.Equal(1, terminal.SettlementCallCount);
            Assert.Equal(2, printer.PrintCallCount);
            Assert.Equal(2, reprinted.PrintCount);
            Assert.Equal(2, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Unknown_settlement_is_persisted_without_backend_acknowledgement()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-unknown-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(
                false,
                "No terminal response",
                SessionId: "backend-settlement-unknown",
                ResultUnknown: true));
            var backend = new FakeLinklyBackendTerminalClient();
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            await service.SettleAndPrintAsync(session, businessDate);
            await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            Assert.Equal(1, terminal.SettlementCallCount);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(0, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Initial_settlement_propagates_caller_cancellation_and_keeps_pending_lock()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-caller-cancel-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            using var cancellation = new CancellationTokenSource();
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not complete"))
            {
                SettlementExceptionFactory = token =>
                {
                    cancellation.Cancel();
                    return new OperationCanceledException(token);
                }
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                new FakeLinklyBackendTerminalClient());
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SettleAndPrintAsync(session, businessDate, cancellation.Token));
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.Equal(1, terminal.SettlementCallCount);
            Assert.Equal(LocalLinklySettlementStatus.Pending, stored.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Initial_settlement_timeout_is_persisted_as_unknown_without_resubmission()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-timeout-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not complete"))
            {
                SettlementExceptionFactory = _ => new TaskCanceledException("terminal request timed out")
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                new FakeLinklyBackendTerminalClient());
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            var result = await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(result.ResultUnknown);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            Assert.Equal(1, terminal.SettlementCallCount);

            var blocked = await service.SettleAndPrintAsync(session, businessDate);
            Assert.True(blocked.ResultUnknown);
            Assert.Equal(1, terminal.SettlementCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Default_unknown_submission_state_is_persisted_as_unknown_without_resubmission()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-default-state-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(false, "Settlement failed before submission."));
            var printer = new FakeLinklyBankReceiptPrinter();
            var backend = new FakeLinklyBackendTerminalClient();
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                repository,
                printer,
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            await service.SettleAndPrintAsync(session, DateTime.Today);
            await service.SettleAndPrintAsync(session, DateTime.Today);
            var stored = Assert.Single(await repository.GetByBusinessDateAsync(
                session.StoreCode,
                session.DeviceCode,
                DateTime.Today));

            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            Assert.Null(stored.ProviderSessionId);
            Assert.Equal(ProviderSubmissionState.Unknown, stored.ProviderSubmissionState);
            Assert.Equal(1, terminal.SettlementCallCount);
            Assert.Equal(0, printer.PrintCallCount);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Existing_provider_session_reuses_record_after_acknowledgement_failure()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-reuse-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(
                true,
                "Settlement complete",
                SessionId: "backend-settlement-reuse",
                ReceiptTexts: ["SETTLEMENT RECEIPT"],
                ProviderSubmissionState: ProviderSubmissionState.Submitted));
            var backend = new FakeLinklyBackendTerminalClient
            {
                AcknowledgeSettlementException = new HttpRequestException("temporary backend outage")
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            await service.SettleAndPrintAsync(session, businessDate);
            backend.AcknowledgeSettlementException = null;
            await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.Equal(2, terminal.SettlementCallCount);
            Assert.Equal(2, backend.AcknowledgeSettlementCallCount);
            Assert.Equal("backend-settlement-reuse", stored.ProviderSessionId);
            Assert.Equal(1, stored.PrintCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Historical_business_date_is_rejected_before_the_terminal_is_called()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-historical-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "not used"));
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment()),
                new LocalLinklySettlementRepository(store),
                new FakeLinklyBankReceiptPrinter());
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SettleAndPrintAsync(session, DateTime.Today.AddDays(-1)));

            Assert.Equal(0, terminal.SettlementCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Print_failure_keeps_settlement_result_and_reprint_does_not_resubmit()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-print-failure-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(
                true,
                "Settlement complete",
                SessionId: "backend-settlement-print-failure",
                ReceiptTexts: ["SETTLEMENT RECEIPT"],
                ProviderSubmissionState: ProviderSubmissionState.Submitted));
            var printer = new FakeLinklyBankReceiptPrinter
            {
                Result = new ReceiptPrintResult(false, "paper out")
            };
            var backend = new FakeLinklyBackendTerminalClient();
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                printer,
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            var execution = await service.SettleAndPrintAsync(session, businessDate);
            var failedPrintRecord = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.False(execution.PrintResult?.Succeeded);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, failedPrintRecord.Status);
            Assert.Equal(0, failedPrintRecord.PrintCount);
            Assert.Equal("paper out", failedPrintRecord.LastPrintError);
            Assert.Equal(0, backend.MarkReceiptPrintedCallCount);

            printer.Result = new ReceiptPrintResult(true, "printed");
            var reprint = await service.ReprintAsync(failedPrintRecord);
            var reprintedRecord = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(reprint.Succeeded);
            Assert.Equal(1, terminal.SettlementCallCount);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, reprintedRecord.Status);
            Assert.Equal(1, reprintedRecord.PrintCount);
            Assert.Null(reprintedRecord.LastPrintError);
            Assert.Equal(1, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Multi_receipt_partial_print_records_the_successful_physical_copy_before_failure()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-partial-print-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var printer = new FakeLinklyBankReceiptPrinter();
            printer.Results.Enqueue(new ReceiptPrintResult(true, "printed"));
            printer.Results.Enqueue(new ReceiptPrintResult(false, "paper out"));
            var service = new LinklySettlementService(
                new FakeLinklyTerminalClient(new LinklySettlementResult(
                    true,
                    "Settlement complete",
                    SessionId: "backend-settlement-partial-print",
                    ResponseCode: "00",
                    ReceiptTexts: ["MERCHANT COPY", "CUSTOMER COPY"],
                    ProviderSubmissionState: ProviderSubmissionState.Submitted)),
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
                }),
                repository,
                printer,
                new FakeLinklyBackendTerminalClient());
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            var execution = await service.SettleAndPrintAsync(session, DateTime.Today);
            var stored = Assert.Single(await service.GetHistoryAsync(session, DateTime.Today));

            Assert.False(execution.PrintResult?.Succeeded);
            Assert.Equal(2, printer.PrintCallCount);
            Assert.Equal(1, stored.PrintCount);
            Assert.NotNull(stored.FirstPrintedAt);
            Assert.Equal("paper out", stored.LastPrintError);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Reused_final_settlement_is_not_downgraded_by_unknown_empty_recovery_response()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-final-evidence-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(
                true,
                "Settlement complete",
                SessionId: "backend-settlement-final-evidence",
                ResponseCode: "00",
                ResponseText: "Approved",
                SettlementData: "Totals: 3",
                ReceiptTexts: ["SETTLEMENT RECEIPT"],
                ProviderSubmissionState: ProviderSubmissionState.Submitted));
            var printer = new FakeLinklyBankReceiptPrinter();
            var backend = new FakeLinklyBackendTerminalClient();
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                printer,
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            await service.SettleAndPrintAsync(session, businessDate);
            var finalRecord = Assert.Single(await service.GetHistoryAsync(session, businessDate));
            terminal.Result = new LinklySettlementResult(
                false,
                "Settlement recovery timed out",
                SessionId: finalRecord.ProviderSessionId,
                ResultUnknown: true);

            var recovery = await service.SettleAndPrintAsync(session, businessDate);
            var preservedRecord = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(recovery.ResultUnknown);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, preservedRecord.Status);
            Assert.Equal("00", preservedRecord.ResponseCode);
            Assert.Equal("Approved", preservedRecord.ResponseText);
            Assert.Equal("Totals: 3", preservedRecord.SettlementData);
            Assert.Equal(finalRecord.ReceiptTexts, preservedRecord.ReceiptTexts);
            Assert.Equal(finalRecord.FirstPrintedAt, preservedRecord.FirstPrintedAt);
            Assert.Equal(finalRecord.LastPrintedAt, preservedRecord.LastPrintedAt);
            Assert.Equal(finalRecord.PrintCount, preservedRecord.PrintCount);
            Assert.Equal(2, terminal.SettlementCallCount);
            Assert.Equal(1, printer.PrintCallCount);
            Assert.Equal(1, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(1, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Unknown_cloud_settlement_recovers_final_receipt_without_submitting_a_new_settlement()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-cloud-recovery-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var printer = new FakeLinklyBankReceiptPrinter();
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement(
                    "recovered-settlement-001",
                    "Completed",
                    operationSuccess: true,
                    receiptTexts: ["SETTLEMENT RECEIPT"])
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                printer,
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            await CreateUnknownSettlementAsync(repository, session, businessDate, providerSessionId: null);

            var recovered = await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.Equal(LocalLinklySettlementStatus.Succeeded, recovered.Settlement.Status);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, stored.Status);
            Assert.Equal("recovered-settlement-001", stored.ProviderSessionId);
            Assert.Equal("SETTLEMENT RECEIPT", Assert.Single(stored.ReceiptTexts));
            Assert.Equal(1, stored.PrintCount);
            Assert.Equal(1, backend.GetResumableSettlementCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Equal(1, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(1, printer.PrintCallCount);
            Assert.Equal(1, backend.MarkReceiptPrintedCallCount);
            Assert.Equal(LinklyConnectionMode.CloudBackendAsync, backend.LastResumableSettlementSettings?.LinklyConnectionMode);
            Assert.Equal(CardTerminalEnvironment.Production, backend.LastResumableSettlementSettings?.Environment);
            Assert.Equal(LinklyConnectionMode.CloudBackendAsync, backend.LastAcknowledgeSettlementSettings?.LinklyConnectionMode);
            Assert.Equal(CardTerminalEnvironment.Production, backend.LastAcknowledgeSettlementSettings?.Environment);
            Assert.Equal(LinklyConnectionMode.CloudBackendAsync, backend.LastReceiptPrintedSettings?.LinklyConnectionMode);
            Assert.Equal(CardTerminalEnvironment.Production, backend.LastReceiptPrintedSettings?.Environment);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Q1")]
    public async Task Unknown_cloud_settlement_recovery_requires_success_flag_and_approved_code(string? responseCode)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-cloud-outcome-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement(
                    "recovered-settlement-outcome",
                    "Completed",
                    operationSuccess: true,
                    receiptTexts: ["DECLINED RECEIPT"]) with
                {
                    ResponseCode = responseCode
                }
            };
            var service = new LinklySettlementService(
                new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted")),
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            await CreateUnknownSettlementAsync(repository, session, businessDate, providerSessionId: null);

            var recovered = await service.SettleAndPrintAsync(session, businessDate);

            Assert.Equal(LocalLinklySettlementStatus.Failed, recovered.Settlement.Status);
            Assert.Equal(LocalLinklySettlementStatus.Failed, Assert.Single(await service.GetHistoryAsync(session, businessDate)).Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Pending_cloud_recovery_does_not_submit_or_modify_the_local_unknown_record()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-pending-recovery-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement("pending-settlement-001", "Pending")
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            var unresolved = await CreateUnknownSettlementAsync(repository, session, businessDate, "pending-settlement-001");

            var result = await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(result.ResultUnknown);
            Assert.Equal(unresolved.SettlementGuid, stored.SettlementGuid);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            // 云端模式下结算前先对账前一营业日遗留会话（1 次查询），再做同日恢复（1 次查询）。
            Assert.Equal(2, backend.GetResumableSettlementCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(0, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Mismatched_cloud_recovery_session_does_not_overwrite_or_submit_the_local_record()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-mismatched-recovery-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement(
                    "other-settlement-001",
                    "Completed",
                    operationSuccess: true,
                    receiptTexts: ["OTHER SETTLEMENT RECEIPT"])
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            var unresolved = await CreateUnknownSettlementAsync(repository, session, businessDate, "expected-settlement-001");

            var result = await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(result.ResultUnknown);
            Assert.Equal(unresolved.SettlementGuid, stored.SettlementGuid);
            Assert.Equal("expected-settlement-001", stored.ProviderSessionId);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            Assert.Empty(stored.ReceiptTexts);
            // 云端模式下结算前先对账前一营业日遗留会话（1 次查询），再做同日恢复（1 次查询）。
            Assert.Equal(2, backend.GetResumableSettlementCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(0, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Mismatched_cloud_recovery_environment_does_not_query_bind_or_submit()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-environment-recovery-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Environment = CardTerminalEnvironment.Sandbox,
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement(
                    "sandbox-settlement-001",
                    "Completed",
                    operationSuccess: true,
                    receiptTexts: ["SANDBOX RECEIPT"])
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            var unresolved = await CreateUnknownSettlementAsync(repository, session, businessDate, providerSessionId: null);

            var result = await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(result.ResultUnknown);
            Assert.Equal(unresolved.SettlementGuid, stored.SettlementGuid);
            Assert.Null(stored.ProviderSessionId);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            // 结算前的前一营业日对账只查当前环境（Sandbox）的会话，不会碰另一环境（Production）的未决记录。
            Assert.Equal(1, backend.GetResumableSettlementCallCount);
            Assert.Equal(CardTerminalEnvironment.Sandbox, backend.LastResumableSettlementSettings?.Environment);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(0, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Resumable_settlement_lookup_timeout_returns_original_record_without_resubmission_or_overwrite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-resumable-timeout-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient
            {
                GetResumableSettlementException = new TaskCanceledException("resumable settlement lookup timed out")
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            var unresolved = await CreateUnknownSettlementAsync(repository, session, businessDate, providerSessionId: null);

            var result = await service.SettleAndPrintAsync(session, businessDate);
            var stored = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.True(result.ResultUnknown);
            Assert.Equal(unresolved.SettlementGuid, result.Settlement.SettlementGuid);
            Assert.Equal(unresolved.SettlementGuid, stored.SettlementGuid);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            // 前一营业日对账那次查询超时被忽略（不阻塞），同日恢复的查询再超时才把原记录按未决阻塞。
            Assert.Equal(2, backend.GetResumableSettlementCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(0, backend.MarkReceiptPrintedCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Resumable_settlement_lookup_callercancelled_propagates_without_blocking()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-resumable-cancel-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
            };
            using var cts = new CancellationTokenSource();
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlementExceptionFactory = token =>
                {
                    cts.Cancel();
                    return new OperationCanceledException(token);
                }
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(settings),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                backend);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            await CreateUnknownSettlementAsync(repository, session, businessDate, providerSessionId: null);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SettleAndPrintAsync(session, businessDate, cts.Token));

            Assert.Equal(1, backend.GetResumableSettlementCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Concurrent_settlement_attempts_create_only_one_pending_record_and_call_the_terminal_once()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-concurrent-{Guid.NewGuid():N}.db");

        try
        {
            var businessDate = DateTime.Today;
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "Settlement complete"))
            {
                SettlementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                DeferredSettlementResult = new TaskCompletionSource<LinklySettlementResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                new LocalLinklySettlementRepository(store),
                new FakeLinklyBankReceiptPrinter());
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            var first = service.SettleAndPrintAsync(session, businessDate);
            await terminal.SettlementStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var second = await service.SettleAndPrintAsync(session, businessDate);
            var pending = Assert.Single(await service.GetHistoryAsync(session, businessDate));

            Assert.Equal(LocalLinklySettlementStatus.Pending, second.Settlement.Status);
            Assert.Equal(LocalLinklySettlementStatus.Pending, pending.Status);
            Assert.Equal(1, terminal.SettlementCallCount);

            terminal.DeferredSettlementResult.SetResult(new LinklySettlementResult(true, "Settlement complete"));
            await first;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Settlement_does_not_create_a_record_or_call_the_terminal_when_linkly_is_not_active()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-disabled-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be called"));
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Square,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                repository,
                new FakeLinklyBankReceiptPrinter());
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SettleAndPrintAsync(session, DateTime.Today));

            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Empty(await repository.GetByBusinessDateAsync(session.StoreCode, session.DeviceCode, DateTime.Today));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Manual_resolution_updates_only_an_unresolved_local_ip_record_and_never_calls_the_terminal()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-manual-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            var settlement = await CreateUnknownSettlementAsync(
                repository,
                session,
                DateTime.Today,
                providerSessionId: null,
                connectionMode: LinklyConnectionMode.LocalIp);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be called"));
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                repository,
                new FakeLinklyBankReceiptPrinter());

            var result = await service.ResolveUncertainAsync(
                session,
                settlement,
                LocalLinklySettlementManualResolution.ConfirmedSucceeded);
            var persisted = Assert.Single(await repository.GetByBusinessDateAsync(session.StoreCode, session.DeviceCode, DateTime.Today));

            Assert.True(result.Resolved);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, persisted.Status);
            Assert.Equal(ProviderSubmissionState.Submitted, persisted.ProviderSubmissionState);
            Assert.Contains("manually confirmed", persisted.ResponseText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Manual_resolution_rejects_a_stale_ui_payload_revision()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-stale-manual-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
            var viewedSettlement = await CreateUnknownSettlementAsync(
                repository,
                session,
                DateTime.Today,
                providerSessionId: null,
                connectionMode: LinklyConnectionMode.LocalIp);
            await repository.MarkPrintFailedAsync(viewedSettlement.SettlementGuid, "printer changed record", DateTimeOffset.UtcNow);
            var service = new LinklySettlementService(
                new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be called")),
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                repository,
                new FakeLinklyBankReceiptPrinter());

            var result = await service.ResolveUncertainAsync(
                session,
                viewedSettlement,
                LocalLinklySettlementManualResolution.ConfirmedSucceeded);
            var persisted = Assert.Single(await repository.GetByBusinessDateAsync(
                session.StoreCode,
                session.DeviceCode,
                DateTime.Today));

            Assert.False(result.Resolved);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, persisted.Status);
            Assert.True(persisted.PayloadRevision > viewedSettlement.PayloadRevision);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Auto_settle_after_daily_close_only_for_integrated_linkly_today()
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        await WithAutoSettleServiceAsync(new PaymentMethodSettings(UseManualCard: false), CardProcessorKind.Linkly, async (service, _) =>
        {
            Assert.True(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today));
            // 补做昨天的日结不能结掉今天的终端批次。
            Assert.False(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today.AddDays(-1)));
        });
        await WithAutoSettleServiceAsync(new PaymentMethodSettings(UseManualCard: true), CardProcessorKind.Linkly, async (service, _) =>
            Assert.False(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today)));
        await WithAutoSettleServiceAsync(new PaymentMethodSettings(UseManualCard: false), CardProcessorKind.Square, async (service, _) =>
            Assert.False(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today)));
        await WithAutoSettleServiceAsync((IPaymentMethodSettingsService?)null, CardProcessorKind.Linkly, async (service, _) =>
            Assert.False(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today)));
    }

    [Fact]
    public async Task Auto_settle_after_daily_close_reads_persisted_manual_card_setting()
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        // 内存里是默认值（集成刷卡），但本地设置已开启手动刷卡：必须以持久化设置为准。
        var paymentMethods = new MutablePaymentMethodSettingsService(new PaymentMethodSettings(UseManualCard: false))
        {
            LoadHandler = _ => Task.FromResult(new PaymentMethodSettings(UseManualCard: true))
        };
        await WithAutoSettleServiceAsync(paymentMethods, CardProcessorKind.Linkly, async (service, _) =>
            Assert.False(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today)));
    }

    [Fact]
    public async Task Auto_settle_after_daily_close_skips_when_today_already_succeeded_or_unresolved()
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var integrated = new PaymentMethodSettings(UseManualCard: false);

        // 之前失败的结算允许在日结时自动重试。
        await WithAutoSettleServiceAsync(integrated, CardProcessorKind.Linkly, async (service, repository) =>
        {
            await CreateCompletedSettlementAsync(repository, session, LocalLinklySettlementStatus.Failed);
            Assert.True(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today));
        });

        // 一天保存多次日结时，已有成功结算就不能重复结算。
        await WithAutoSettleServiceAsync(integrated, CardProcessorKind.Linkly, async (service, repository) =>
        {
            await CreateCompletedSettlementAsync(repository, session, LocalLinklySettlementStatus.Succeeded);
            Assert.False(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today));
        });

        // 结果未知的结算必须由收银员在结算页处理，自动流程不能再发一次。
        await WithAutoSettleServiceAsync(integrated, CardProcessorKind.Linkly, async (service, repository) =>
        {
            await CreateUnknownSettlementAsync(repository, session, DateTime.Today, providerSessionId: null);
            Assert.False(await service.ShouldAutoSettleAfterDailyCloseAsync(session, DateTime.Today));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Settlement_is_blocked_before_any_record_when_terminal_is_not_ready(bool pinpadOffline)
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be sent"))
        {
            ConnectionTestResult = new LinklyConnectionTestResult(false, "terminal check failed", PinPadOffline: pinpadOffline)
        };

        await WithTerminalCheckServiceAsync(LinklyConnectionMode.LocalIp, terminal, async (service, repository) =>
        {
            var exception = await Assert.ThrowsAsync<LinklySettlementTerminalUnavailableException>(
                () => service.SettleAndPrintAsync(session, DateTime.Today));

            Assert.Equal(pinpadOffline, exception.Check.Offline);
            Assert.Equal("terminal check failed", exception.Check.Message);
            // 刷卡机未就绪时既不能发出结算，也不能留下任何本地记录，否则会被上传成没有金额的失败结算。
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Empty(await repository.GetByBusinessDateAsync(session.StoreCode, session.DeviceCode, DateTime.Today));
            Assert.Empty(await repository.GetActiveUploadItemsAsync());
        });
    }

    [Fact]
    public async Task Terminal_check_reports_offline_without_creating_a_record()
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be sent"))
        {
            ConnectionTestResult = new LinklyConnectionTestResult(false, "PINpad offline", PinPadOffline: true)
        };

        await WithTerminalCheckServiceAsync(LinklyConnectionMode.LocalIp, terminal, async (service, repository) =>
        {
            var check = await service.CheckTerminalReadyAsync(session, DateTime.Today);

            Assert.False(check.Ready);
            Assert.True(check.Offline);
            Assert.Equal(1, terminal.TestConnectionCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Empty(await repository.GetByBusinessDateAsync(session.StoreCode, session.DeviceCode, DateTime.Today));
        });
    }

    [Fact]
    public async Task Settlement_proceeds_after_terminal_check_when_pinpad_is_online()
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(
            true,
            "done",
            ResponseCode: "00",
            ResponseText: "Approved",
            ProviderSubmissionState: ProviderSubmissionState.Submitted));

        await WithTerminalCheckServiceAsync(LinklyConnectionMode.LocalIp, terminal, async (service, repository) =>
        {
            Assert.True((await service.CheckTerminalReadyAsync(session, DateTime.Today)).Ready);
            Assert.Equal(1, terminal.TestConnectionCallCount);

            var execution = await service.SettleAndPrintAsync(session, DateTime.Today);

            // 发送前服务层还会再查一次，兜住确认框期间刷卡机掉线。
            Assert.Equal(2, terminal.TestConnectionCallCount);
            Assert.Equal(1, terminal.SettlementCallCount);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, execution.Settlement.Status);
        });
    }

    [Fact]
    public async Task Terminal_check_allows_pinpad_that_is_online_but_not_logged_on()
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "done"))
        {
            // 刷卡机在线但未登录银行网络：终端会自己回银行响应，不属于离线，不拦截。
            ConnectionTestResult = new LinklyConnectionTestResult(true, "not logged on", PinPadLoggedOn: false)
        };

        await WithTerminalCheckServiceAsync(LinklyConnectionMode.LocalIp, terminal, async (service, _) =>
            Assert.True((await service.CheckTerminalReadyAsync(session, DateTime.Today)).Ready));
    }

    [Theory]
    [InlineData(LinklyConnectionMode.CloudDirectSync)]
    [InlineData(LinklyConnectionMode.CloudBackendAsync)]
    public async Task Terminal_check_is_skipped_outside_local_ip_mode(LinklyConnectionMode mode)
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "done"))
        {
            ConnectionTestResult = new LinklyConnectionTestResult(false, "would be offline", PinPadOffline: true)
        };

        // 云端模式没有等价的轻量状态查询：不能拿本地 EFT-Client 的状态去拦云端结算。
        await WithTerminalCheckServiceAsync(mode, terminal, async (service, _) =>
        {
            Assert.True((await service.CheckTerminalReadyAsync(session, DateTime.Today)).Ready);
            Assert.Equal(0, terminal.TestConnectionCallCount);
        });
    }

    [Fact]
    public async Task Terminal_check_does_not_mask_the_unresolved_settlement_block()
    {
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be sent"))
        {
            ConnectionTestResult = new LinklyConnectionTestResult(false, "PINpad offline", PinPadOffline: true)
        };

        await WithTerminalCheckServiceAsync(LinklyConnectionMode.LocalIp, terminal, async (service, repository) =>
        {
            var unresolved = await CreateUnknownSettlementAsync(
                repository,
                session,
                DateTime.Today,
                providerSessionId: null,
                connectionMode: LinklyConnectionMode.LocalIp);

            // 已有结果未知的记录时，应由结算自己的“未决记录”阻塞提示说话，而不是误报成刷卡机离线。
            Assert.True((await service.CheckTerminalReadyAsync(session, DateTime.Today)).Ready);
            var execution = await service.SettleAndPrintAsync(session, DateTime.Today);

            Assert.Equal(unresolved.SettlementGuid, execution.Settlement.SettlementGuid);
            Assert.Equal(0, terminal.TestConnectionCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
        });
    }

    private static async Task WithTerminalCheckServiceAsync(
        LinklyConnectionMode mode,
        FakeLinklyTerminalClient terminal,
        Func<LinklySettlementService, ILocalLinklySettlementRepository, Task> assert)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-terminal-check-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = mode
                }),
                repository,
                new FakeLinklyBankReceiptPrinter());

            await assert(service, repository);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static Task WithAutoSettleServiceAsync(
        PaymentMethodSettings? paymentMethods,
        CardProcessorKind processor,
        Func<LinklySettlementService, ILocalLinklySettlementRepository, Task> assert) =>
        WithAutoSettleServiceAsync(
            paymentMethods is null ? null : new MutablePaymentMethodSettingsService(paymentMethods),
            processor,
            assert);

    private static async Task WithAutoSettleServiceAsync(
        IPaymentMethodSettingsService? paymentMethods,
        CardProcessorKind processor,
        Func<LinklySettlementService, ILocalLinklySettlementRepository, Task> assert)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-auto-settle-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "done"));
            var service = new LinklySettlementService(
                terminal,
                new FixedCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with { Processor = processor }),
                repository,
                new FakeLinklyBankReceiptPrinter(),
                paymentMethodSettingsService: paymentMethods);

            await assert(service, repository);

            // 判断本身绝不能触发终端结算。
            Assert.Equal(0, terminal.SettlementCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static async Task<(LocalSqliteStore Store, LocalLinklySettlementRepository Repository, string DatabasePath)> CreateCloudRepositoryAsync(string name)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-{name}-{Guid.NewGuid():N}.db");
        var store = new LocalSqliteStore(databasePath);
        await new LocalSchemaService(store).InitializeAsync();
        return (store, new LocalLinklySettlementRepository(store), databasePath);
    }

    private static void DeleteDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static CardTerminalSettings CloudSettings() => CardTerminalSettings.FromEnvironment() with
    {
        Processor = CardProcessorKind.Linkly,
        LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
    };

    private static PosSessionState CreatePosSession() =>
        new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task Earlier_business_day_unknown_settlement_is_completed_before_todays_settlement_is_sent()
    {
        // 回归 H14：D 日结算结果未知（带 sessionId），D+1 日结算时必须先把 D 日的补完并 ack，
        // 再单独发今天的结算；不能把 D 日会话当成今天的结算接管。
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("earlier-unknown");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            await CreateUnknownSettlementAsync(repository, session, yesterday, "settlement-yesterday");
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "Settled", "settlement-today", "00", "Approved", "Totals", ["TODAY RECEIPT"], ProviderSubmissionState: ProviderSubmissionState.Submitted));
            var printer = new FakeLinklyBankReceiptPrinter();
            var backend = new FakeLinklyBackendTerminalClient();
            backend.ResumableSettlementSequence.Enqueue(CreateResumableSettlement(
                "settlement-yesterday", "Completed", operationSuccess: true, receiptTexts: ["YESTERDAY RECEIPT"], createdAt: DateTimeOffset.Now.AddDays(-1)));
            backend.ResumableSettlementSequence.Enqueue(null);
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, printer, backend);

            var result = await service.SettleAndPrintAsync(session, DateTime.Today);

            var earlier = Assert.Single(await service.GetHistoryAsync(session, yesterday));
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, earlier.Status);
            Assert.Equal("YESTERDAY RECEIPT", Assert.Single(earlier.ReceiptTexts));
            Assert.Equal(["settlement-yesterday"], backend.AcknowledgedSessionIds.Take(1));
            Assert.False(result.BlockedByEarlierBusinessDay);
            Assert.Equal(1, terminal.SettlementCallCount);
            var today = Assert.Single(await service.GetHistoryAsync(session, DateTime.Today));
            Assert.Equal(result.Settlement.SettlementGuid, today.SettlementGuid);
            Assert.Equal("settlement-today", today.ProviderSessionId);
            Assert.Equal(["TODAY RECEIPT"], today.ReceiptTexts);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Earlier_business_day_session_is_bound_to_the_matching_unbound_record_not_to_todays_record()
    {
        // H14 变体：D 日本地记录没有 sessionId，服务端会话 S1（D 日创建）未 ack。
        // 旧行为是把 S1 绑到 D+1 的记录上，结果记错日期。
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("earlier-unbound");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var unbound = await CreateUnknownSettlementAsync(repository, session, yesterday, providerSessionId: null);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "Settled", "settlement-today", "00", "Approved", "Totals", ["TODAY RECEIPT"], ProviderSubmissionState: ProviderSubmissionState.Submitted));
            var backend = new FakeLinklyBackendTerminalClient();
            backend.ResumableSettlementSequence.Enqueue(CreateResumableSettlement(
                "settlement-yesterday", "Completed", operationSuccess: true, receiptTexts: ["YESTERDAY RECEIPT"], createdAt: DateTimeOffset.Now.AddDays(-1)));
            backend.ResumableSettlementSequence.Enqueue(null);
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            await service.SettleAndPrintAsync(session, DateTime.Today);

            var earlier = Assert.Single(await service.GetHistoryAsync(session, yesterday));
            Assert.Equal(unbound.SettlementGuid, earlier.SettlementGuid);
            Assert.Equal("settlement-yesterday", earlier.ProviderSessionId);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, earlier.Status);
            var today = Assert.Single(await service.GetHistoryAsync(session, DateTime.Today));
            Assert.Equal("settlement-today", today.ProviderSessionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Earlier_business_day_settlement_that_cannot_be_completed_blocks_todays_settlement()
    {
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("earlier-blocked");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var unknown = await CreateUnknownSettlementAsync(repository, session, yesterday, "settlement-yesterday");
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient
            {
                // 服务端等回调超时后收口成“结果未知”：不可交付。
                ResumableSettlement = CreateResumableSettlement(
                    "settlement-yesterday", "Pending", createdAt: DateTimeOffset.Now.AddDays(-1), recoveryAction: "ResultUnknown")
            };
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            var result = await service.SettleAndPrintAsync(session, DateTime.Today);

            Assert.True(result.BlockedByEarlierBusinessDay);
            Assert.True(result.ResultUnknown);
            Assert.Equal(unknown.SettlementGuid, result.Settlement.SettlementGuid);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Empty(await service.GetHistoryAsync(session, DateTime.Today));
            Assert.Equal(LocalLinklySettlementStatus.Unknown, Assert.Single(await service.GetHistoryAsync(session, yesterday)).Status);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Earlier_business_day_server_session_without_local_record_is_adopted_and_acknowledged()
    {
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("earlier-orphan");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "Settled", "settlement-today", "00", "Approved", "Totals", ["TODAY RECEIPT"], ProviderSubmissionState: ProviderSubmissionState.Submitted));
            var backend = new FakeLinklyBackendTerminalClient();
            // 银行拒绝、没有回单：OperationSuccess=false 即失败终态（与服务端同口径）。
            backend.ResumableSettlementSequence.Enqueue(CreateResumableSettlement(
                "settlement-orphan", "Completed", operationSuccess: false, createdAt: DateTimeOffset.Now.AddDays(-1)));
            backend.ResumableSettlementSequence.Enqueue(null);
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            await service.SettleAndPrintAsync(session, DateTime.Today);

            var adopted = Assert.Single(await service.GetHistoryAsync(session, yesterday));
            Assert.Equal("settlement-orphan", adopted.ProviderSessionId);
            Assert.Equal(LocalLinklySettlementStatus.Failed, adopted.Status);
            Assert.Contains("settlement-orphan", backend.AcknowledgedSessionIds);
            Assert.Equal(1, terminal.SettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Earlier_business_day_session_whose_local_record_is_final_gets_its_acknowledgement_replayed()
    {
        // ack 补发：本地早已定论，只是当时 ack 失败，服务端会话一直未 ack。
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("earlier-ack-replay");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var unknown = await CreateUnknownSettlementAsync(repository, session, yesterday, "settlement-yesterday");
            Assert.True(await repository.TryResolveUncertainAsync(
                unknown.SettlementGuid, unknown.PayloadRevision, LocalLinklySettlementManualResolution.ConfirmedFailed, DateTimeOffset.UtcNow));
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "Settled"));
            var backend = new FakeLinklyBackendTerminalClient();
            // 服务端会话仍是非终态（主管结案的情形）：补发 ack 要带主管标记才能关闭它。
            backend.ResumableSettlementSequence.Enqueue(CreateResumableSettlement(
                "settlement-yesterday", "Pending", createdAt: DateTimeOffset.Now.AddDays(-1), recoveryAction: "ResultUnknown"));
            backend.ResumableSettlementSequence.Enqueue(null);
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            await service.SettleAndPrintAsync(session, DateTime.Today);

            Assert.Equal(["settlement-yesterday"], backend.SupervisorAcknowledgedSessionIds);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(1, terminal.SettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Todays_unbound_record_is_not_bound_to_a_session_created_on_another_business_day()
    {
        // 回归 M25：恢复逻辑原先会把任意 resumable 会话绑到当天没有 sessionId 的记录上。
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("day-mismatch");
        try
        {
            var session = CreatePosSession();
            var unbound = await CreateUnknownSettlementAsync(repository, session, DateTime.Today, providerSessionId: null);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient();
            // 对账那次查询失败被忽略，同日恢复那次查询却返回了昨天的会话（例如两次查询之间服务端状态变化）。
            backend.ResumableSettlementSequence.Enqueue(null);
            backend.ResumableSettlementSequence.Enqueue(CreateResumableSettlement(
                "settlement-yesterday", "Completed", operationSuccess: true, receiptTexts: ["YESTERDAY RECEIPT"], createdAt: DateTimeOffset.Now.AddDays(-1)));
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            var result = await service.SettleAndPrintAsync(session, DateTime.Today);

            Assert.True(result.ResultUnknown);
            var stored = Assert.Single(await service.GetHistoryAsync(session, DateTime.Today));
            Assert.Equal(unbound.SettlementGuid, stored.SettlementGuid);
            Assert.Null(stored.ProviderSessionId);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Unknown_record_without_session_is_confirmed_not_submitted_when_the_server_has_no_session()
    {
        // 回归 M24：服务端在建会话前就拒绝了（终端未配对等），本地记录没有 sessionId，服务端也没有任何会话，
        // 这条记录不能一直是未决，否则当天再也无法结算。
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("not-submitted");
        try
        {
            var session = CreatePosSession();
            var unknown = await CreateUnknownSettlementAsync(repository, session, DateTime.Today, providerSessionId: null);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient();
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            var result = await service.SettleAndPrintAsync(session, DateTime.Today);

            Assert.Equal(unknown.SettlementGuid, result.Settlement.SettlementGuid);
            Assert.Equal(LocalLinklySettlementStatus.Failed, result.Settlement.Status);
            Assert.False(result.ResultUnknown);
            var stored = Assert.Single(await service.GetHistoryAsync(session, DateTime.Today));
            Assert.Equal(LocalLinklySettlementStatus.Failed, stored.Status);
            Assert.Equal(ProviderSubmissionState.NotSubmitted, stored.ProviderSubmissionState);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Recently_started_pending_record_without_session_is_not_confirmed_not_submitted()
    {
        // 刚创建的 Pending 记录可能是另一次结算正在进行（还没建出服务端会话），不能据此断定未提交。
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("pending-in-flight");
        try
        {
            var session = CreatePosSession();
            var pending = new LocalLinklySettlementRecord(
                Guid.NewGuid(), session.StoreCode, session.DeviceCode, DateTime.Today,
                LinklyConnectionMode.CloudBackendAsync.ToString(), CardTerminalEnvironment.Production.ToString(),
                ProviderSessionId: null, LocalLinklySettlementStatus.Pending, null, null, null, [],
                DateTimeOffset.UtcNow, null, null, null, 0, null);
            await repository.CreatePendingAsync(pending);
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var service = new LinklySettlementService(
                terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), new FakeLinklyBackendTerminalClient());

            var result = await service.SettleAndPrintAsync(session, DateTime.Today);

            Assert.Equal(LocalLinklySettlementStatus.Pending, result.Settlement.Status);
            Assert.Equal(0, terminal.SettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Supervisor_resolution_of_a_cloud_backend_settlement_acknowledges_the_server_session_as_supervisor_resolved()
    {
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("cloud-supervisor");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var unknown = await CreateUnknownSettlementAsync(repository, session, yesterday, "settlement-yesterday");
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var backend = new FakeLinklyBackendTerminalClient();
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            var result = await service.ResolveUncertainAsync(session, unknown, LocalLinklySettlementManualResolution.ConfirmedFailed);

            Assert.True(result.Resolved);
            Assert.Equal(LocalLinklySettlementStatus.Failed, result.Settlement.Status);
            Assert.Equal(["settlement-yesterday"], backend.SupervisorAcknowledgedSessionIds);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
            Assert.Equal(0, terminal.SettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Supervisor_resolution_binds_the_same_day_server_session_first_so_the_server_session_can_be_closed()
    {
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("cloud-supervisor-bind");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var unknown = await CreateUnknownSettlementAsync(repository, session, yesterday, providerSessionId: null);
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement(
                    "settlement-yesterday", "Pending", createdAt: DateTimeOffset.Now.AddDays(-1), recoveryAction: "ResultUnknown")
            };
            var service = new LinklySettlementService(
                new FakeLinklyTerminalClient(new LinklySettlementResult(true, "x")),
                new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            var result = await service.ResolveUncertainAsync(session, unknown, LocalLinklySettlementManualResolution.ConfirmedNotSubmitted);

            Assert.True(result.Resolved);
            Assert.Equal("settlement-yesterday", result.Settlement.ProviderSessionId);
            Assert.Equal(["settlement-yesterday"], backend.SupervisorAcknowledgedSessionIds);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_records_the_result_of_an_earlier_business_day_settlement_without_sending_a_new_one()
    {
        // M25：非当天的未决记录“只查询、补录结果”的入口。
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("query-earlier");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var unknown = await CreateUnknownSettlementAsync(repository, session, yesterday, "settlement-yesterday");
            var terminal = new FakeLinklyTerminalClient(new LinklySettlementResult(true, "must not be submitted"));
            var printer = new FakeLinklyBankReceiptPrinter();
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement(
                    "settlement-yesterday", "Completed", operationSuccess: true, receiptTexts: ["YESTERDAY RECEIPT"], createdAt: DateTimeOffset.Now.AddDays(-1))
            };
            var service = new LinklySettlementService(terminal, new FixedCardTerminalSettingsProvider(CloudSettings()), repository, printer, backend);

            var result = await service.QueryUnresolvedAsync(session, unknown);

            Assert.True(result.Resolved);
            Assert.Equal(LocalLinklySettlementStatus.Succeeded, result.Settlement.Status);
            Assert.Equal(0, terminal.SettlementCallCount);
            Assert.Contains("settlement-yesterday", backend.AcknowledgedSessionIds);
            Assert.Equal(1, printer.PrintCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Query_leaves_the_record_unresolved_when_the_server_has_no_final_result_yet()
    {
        var (_, repository, databasePath) = await CreateCloudRepositoryAsync("query-pending");
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var session = CreatePosSession();
            var unknown = await CreateUnknownSettlementAsync(repository, session, yesterday, "settlement-yesterday");
            var backend = new FakeLinklyBackendTerminalClient
            {
                ResumableSettlement = CreateResumableSettlement(
                    "settlement-yesterday", "Pending", createdAt: DateTimeOffset.Now.AddDays(-1), recoveryAction: "ResultUnknown")
            };
            var service = new LinklySettlementService(
                new FakeLinklyTerminalClient(new LinklySettlementResult(true, "x")),
                new FixedCardTerminalSettingsProvider(CloudSettings()), repository, new FakeLinklyBankReceiptPrinter(), backend);

            var result = await service.QueryUnresolvedAsync(session, unknown);

            Assert.False(result.Resolved);
            Assert.Equal(LocalLinklySettlementStatus.Unknown, Assert.Single(await service.GetHistoryAsync(session, yesterday)).Status);
            Assert.Equal(0, backend.AcknowledgeSettlementCallCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static async Task CreateCompletedSettlementAsync(
        ILocalLinklySettlementRepository repository,
        PosSessionState session,
        LocalLinklySettlementStatus status)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var settlement = new LocalLinklySettlementRecord(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            DateTime.Today,
            LinklyConnectionMode.LocalIp.ToString(),
            CardTerminalEnvironment.Production.ToString(),
            ProviderSessionId: null,
            LocalLinklySettlementStatus.Pending,
            ResponseCode: null,
            ResponseText: null,
            SettlementData: null,
            ReceiptTexts: [],
            requestedAt,
            CompletedAt: null,
            FirstPrintedAt: null,
            LastPrintedAt: null,
            PrintCount: 0,
            LastPrintError: null);
        await repository.CreatePendingAsync(settlement);
        await repository.CompleteAsync(
            settlement.SettlementGuid,
            new LocalLinklySettlementCompletion(
                status,
                ResponseCode: status == LocalLinklySettlementStatus.Succeeded ? "00" : "XX",
                ResponseText: status.ToString(),
                SettlementData: null,
                ReceiptTexts: [],
                requestedAt.AddSeconds(10),
                ProviderSubmissionState.Submitted));
    }

    private static async Task<LocalLinklySettlementRecord> CreateUnknownSettlementAsync(
        ILocalLinklySettlementRepository repository,
        PosSessionState session,
        DateTime businessDate,
        string? providerSessionId,
        LinklyConnectionMode connectionMode = LinklyConnectionMode.CloudBackendAsync)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var settlement = new LocalLinklySettlementRecord(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            businessDate,
            connectionMode.ToString(),
            CardTerminalEnvironment.Production.ToString(),
            ProviderSessionId: null,
            LocalLinklySettlementStatus.Pending,
            ResponseCode: null,
            ResponseText: null,
            SettlementData: null,
            ReceiptTexts: [],
            requestedAt,
            CompletedAt: null,
            FirstPrintedAt: null,
            LastPrintedAt: null,
            PrintCount: 0,
            LastPrintError: null);
        await repository.CreatePendingAsync(settlement);
        if (!string.IsNullOrWhiteSpace(providerSessionId))
        {
            await repository.BindProviderSessionAsync(settlement.SettlementGuid, providerSessionId);
            settlement = settlement with { ProviderSessionId = providerSessionId };
        }

        var completion = new LocalLinklySettlementCompletion(
            LocalLinklySettlementStatus.Unknown,
            ResponseCode: null,
            ResponseText: "terminal timeout",
            SettlementData: null,
            ReceiptTexts: [],
            requestedAt.AddMinutes(1));
        await repository.CompleteAsync(settlement.SettlementGuid, completion);
        return (await repository.GetByBusinessDateAsync(
                session.StoreCode,
                session.DeviceCode,
                businessDate))
            .Single(item => item.SettlementGuid == settlement.SettlementGuid);
    }

    private static LinklyCloudBackendSessionResponse CreateResumableSettlement(
        string sessionId,
        string status,
        bool? operationSuccess = null,
        IReadOnlyList<string>? receiptTexts = null,
        DateTimeOffset? createdAt = null,
        string? recoveryAction = null)
    {
        return new LinklyCloudBackendSessionResponse(
            CardTerminalEnvironment.Production.ToString(),
            "S001",
            "POS-01",
            sessionId,
            status,
            TxnRef: null,
            ResponseCode: operationSuccess == true ? "00" : null,
            ResponseText: operationSuccess == true ? "Approved" : null,
            RecoveryAction: recoveryAction,
            DisplayText: null,
            CancelKeyFlag: false,
            OKKeyFlag: false,
            AcceptYesKeyFlag: false,
            DeclineNoKeyFlag: false,
            AuthoriseKeyFlag: false,
            InputType: null,
            GraphicCode: null,
            DisplayLines: null,
            ReceiptText: null,
            RecoveryCount: 0,
            ReceiptPrintedAt: null,
            ClientAcknowledgedAt: null,
            LastHttpStatus: 200,
            Notifications: [],
            TransactionSuccess: null,
            OperationType: "Settlement",
            OperationSuccess: operationSuccess,
            SettlementData: operationSuccess == true ? "Totals: 3" : null,
            SettlementReceiptTexts: receiptTexts,
            CreatedAt: createdAt);
    }

    private sealed class FixedCardTerminalSettingsProvider(CardTerminalSettings settings) : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
    }

    private sealed class FakeLinklyTerminalClient : ILinklyTerminalClient
    {
        public FakeLinklyTerminalClient(LinklySettlementResult result)
        {
            Result = result;
        }

        public int SettlementCallCount { get; private set; }

        public LinklySettlementResult Result { get; set; }

        public TaskCompletionSource? SettlementStarted { get; set; }

        public TaskCompletionSource<LinklySettlementResult>? DeferredSettlementResult { get; set; }

        public Func<CancellationToken, Exception>? SettlementExceptionFactory { get; set; }

        // 结算前预检用的刷卡机状态查询结果；默认在线。
        public LinklyConnectionTestResult ConnectionTestResult { get; set; } = new(true);

        public int TestConnectionCallCount { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            TestConnectionCallCount++;
            return Task.FromResult(ConnectionTestResult);
        }

        public Task<LinklySettlementResult> SettlementAsync(PosSessionState session, CardTerminalSettings settings, CancellationToken cancellationToken = default)
        {
            SettlementCallCount++;
            SettlementStarted?.TrySetResult();
            if (SettlementExceptionFactory is not null)
            {
                return Task.FromException<LinklySettlementResult>(SettlementExceptionFactory(cancellationToken));
            }

            return DeferredSettlementResult?.Task ?? Task.FromResult(Result);
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string txnRef, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string txnRef, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string? originalReference, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> VoidAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string? originalReference, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        private static Task<PaymentAuthorizationResult> UnsupportedPaymentAsync() => Task.FromException<PaymentAuthorizationResult>(new NotSupportedException());
    }

    private sealed class FakeLinklyBankReceiptPrinter : ILinklyBankReceiptPrinter
    {
        public int PrintCallCount { get; private set; }

        public LinklyBankReceiptKind? LastKind { get; private set; }

        public ReceiptPrintResult Result { get; set; } = new(true, "printed");

        public Queue<ReceiptPrintResult> Results { get; } = new();

        public Task<ReceiptPrintResult> PrintAsync(
            string environment,
            string sessionId,
            string receiptText,
            LinklyBankReceiptKind kind = LinklyBankReceiptKind.SignatureRequired,
            string? cardType = null,
            string? maskedCardNumber = null,
            string? responseCode = null,
            string? responseText = null,
            CancellationToken cancellationToken = default)
        {
            PrintCallCount++;
            LastKind = kind;
            return Task.FromResult(Results.TryDequeue(out var result) ? result : Result);
        }
    }

    private sealed class FakeLinklyBackendTerminalClient : ILinklyBackendTerminalClient
    {
        public int AcknowledgeSettlementCallCount { get; private set; }

        public List<string> AcknowledgedSessionIds { get; } = [];

        public List<string> SupervisorAcknowledgedSessionIds { get; } = [];

        // 设置后按顺序返回（用尽后回到 ResumableSettlement）：模拟结算过程中服务端状态的变化。
        public Queue<LinklyCloudBackendSessionResponse?> ResumableSettlementSequence { get; } = new();

        public int MarkReceiptPrintedCallCount { get; private set; }

        public int GetResumableSettlementCallCount { get; private set; }

        public LinklyCloudBackendSessionResponse? ResumableSettlement { get; set; }

        public CardTerminalSettings? LastResumableSettlementSettings { get; private set; }

        public CardTerminalSettings? LastAcknowledgeSettlementSettings { get; private set; }

        public CardTerminalSettings? LastReceiptPrintedSettings { get; private set; }

        public Exception? AcknowledgeSettlementException { get; set; }

        public Exception? GetResumableSettlementException { get; set; }

        public Func<CancellationToken, Exception>? ResumableSettlementExceptionFactory { get; set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinklyConnectionTestResult(true));

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinklyConnectionTestResult(true));

        public Task<PaymentAuthorizationResult> PurchaseAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string? originalReference, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSettlementAsync(CardTerminalSettings settings, CancellationToken cancellationToken = default)
        {
            GetResumableSettlementCallCount++;
            LastResumableSettlementSettings = settings;
            if (ResumableSettlementSequence.Count > 0)
            {
                return Task.FromResult(ResumableSettlementSequence.Dequeue());
            }

            if (ResumableSettlementExceptionFactory is not null)
            {
                return Task.FromException<LinklyCloudBackendSessionResponse?>(ResumableSettlementExceptionFactory(cancellationToken));
            }

            return GetResumableSettlementException is null
                ? Task.FromResult(ResumableSettlement)
                : Task.FromException<LinklyCloudBackendSessionResponse?>(GetResumableSettlementException);
        }

        public Task AcknowledgeSettlementAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default)
        {
            AcknowledgeSettlementCallCount++;
            AcknowledgedSessionIds.Add(sessionId);
            LastAcknowledgeSettlementSettings = settings;
            return AcknowledgeSettlementException is null
                ? Task.CompletedTask
                : Task.FromException(AcknowledgeSettlementException);
        }

        public Task AcknowledgeSupervisorResolvedSettlementAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default)
        {
            SupervisorAcknowledgedSessionIds.Add(sessionId);
            return AcknowledgeSettlementException is null
                ? Task.CompletedTask
                : Task.FromException(AcknowledgeSettlementException);
        }

        public Task MarkSettlementReceiptPrintedAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default)
        {
            MarkReceiptPrintedCallCount++;
            LastReceiptPrintedSettings = settings;
            return Task.CompletedTask;
        }

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(CardTerminalSettings settings, CancellationToken cancellationToken = default) => UnsupportedSessionAsync<LinklyCloudBackendSessionResponse?>();

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) => UnsupportedSessionAsync<LinklyCloudBackendSessionResponse>();

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(CardTerminalSettings settings, LinklyCloudBackendSessionResponse activeStatus, CancellationToken cancellationToken = default) => UnsupportedSessionAsync<LinklyCloudBackendSessionResponse>();

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) => UnsupportedSessionAsync<LinklyCloudBackendSessionResponse>();

        public Task AcknowledgeSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AcknowledgeSupervisorResolvedSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) =>
            AcknowledgeSessionAsync(settings, sessionId, cancellationToken);

        private static Task<PaymentAuthorizationResult> UnsupportedPaymentAsync() => Task.FromException<PaymentAuthorizationResult>(new NotSupportedException());

        private static Task<T> UnsupportedSessionAsync<T>() => Task.FromException<T>(new NotSupportedException());
    }
}
