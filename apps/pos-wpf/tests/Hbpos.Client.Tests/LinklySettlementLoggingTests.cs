using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>
/// 结算服务写入中心日志的级别与 TraceId。ConsoleLog 是全局状态，必须与其它改 sink 的测试串行。
/// </summary>
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class LinklySettlementLoggingTests
{
    [Fact]
    public async Task Terminal_exception_is_logged_as_error_with_settlement_trace_before_storing_unknown()
    {
        var terminal = new ThrowingOrFixedTerminalClient(
            exception: new TaskCanceledException("terminal request timed out"),
            result: null);
        var sink = new RecordingApplicationLogSink();
        var (result, stored) = await RunSettlementAsync(terminal, sink);

        Assert.True(result.ResultUnknown);
        Assert.Equal(LocalLinklySettlementStatus.Unknown, stored.Status);
        // 终端调用异常后直接落库 Unknown，原先没有任何日志；现在要有带异常、按结算 GUID 可追溯的 Error。
        var entry = Assert.Single(
            sink.Entries,
            item => item.Level == "Error" && item.TraceId == stored.SettlementGuid.ToString("D"));
        Assert.Equal("LinklySettlement", entry.Category);
        Assert.Equal(nameof(TaskCanceledException), entry.ExceptionType);
        Assert.Contains("settlement terminal call failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains("mode=LocalIp", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_terminal_result_is_logged_as_warning_with_settlement_trace()
    {
        var terminal = new ThrowingOrFixedTerminalClient(
            exception: null,
            result: new LinklySettlementResult(
                false,
                "ANZ Linkly settlement outcome could not be confirmed.",
                "OP-1",
                ResultUnknown: true,
                ProviderSubmissionState: ProviderSubmissionState.Unknown));
        var sink = new RecordingApplicationLogSink();
        var (result, stored) = await RunSettlementAsync(terminal, sink);

        Assert.True(result.ResultUnknown);
        var entry = Assert.Single(
            sink.Entries,
            item => item.Category == "LinklySettlement" && item.TraceId == stored.SettlementGuid.ToString("D"));
        Assert.Equal("Warning", entry.Level);
        Assert.Contains("status=Unknown", entry.Message, StringComparison.Ordinal);
        Assert.Equal("Unknown", entry.Properties!["status"]);
    }

    private static async Task<(LinklySettlementExecutionResult Result, LocalLinklySettlementRecord Stored)> RunSettlementAsync(
        ILinklyTerminalClient terminal,
        RecordingApplicationLogSink sink)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-logging-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalLinklySettlementRepository(store);
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var service = new LinklySettlementService(
                terminal,
                new FixedSettingsProvider(settings),
                repository,
                new NoopBankReceiptPrinter());
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            ConsoleLog.ConfigureCenterSink(sink);
            LinklySettlementExecutionResult result;
            try
            {
                result = await service.SettleAndPrintAsync(session, DateTime.Today);
            }
            finally
            {
                ConsoleLog.ConfigureCenterSink(null);
            }

            var stored = Assert.Single(await service.GetHistoryAsync(session, DateTime.Today));
            return (result, stored);
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

    private sealed class FixedSettingsProvider(CardTerminalSettings settings) : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
    }

    private sealed class ThrowingOrFixedTerminalClient(Exception? exception, LinklySettlementResult? result) : ILinklyTerminalClient
    {
        public Task<LinklyConnectionTestResult> TestConnectionAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinklyConnectionTestResult(true));

        public Task<LinklySettlementResult> SettlementAsync(PosSessionState session, CardTerminalSettings settings, CancellationToken cancellationToken = default) =>
            exception is not null
                ? Task.FromException<LinklySettlementResult>(exception)
                : Task.FromResult(result!);

        public Task<PaymentAuthorizationResult> PurchaseAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string txnRef, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string txnRef, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string? originalReference, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        public Task<PaymentAuthorizationResult> VoidAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string? originalReference, CancellationToken cancellationToken = default) => UnsupportedPaymentAsync();

        private static Task<PaymentAuthorizationResult> UnsupportedPaymentAsync() => Task.FromException<PaymentAuthorizationResult>(new NotSupportedException());
    }

    private sealed class NoopBankReceiptPrinter : ILinklyBankReceiptPrinter
    {
        public Task<ReceiptPrintResult> PrintAsync(
            string environment,
            string sessionId,
            string receiptText,
            LinklyBankReceiptKind kind = LinklyBankReceiptKind.SignatureRequired,
            string? cardType = null,
            string? maskedCardNumber = null,
            string? responseCode = null,
            string? responseText = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReceiptPrintResult(true, "printed"));
    }
}
