using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Client.Wpf.Services.Zeller.Sdk;
using Microsoft.Extensions.Logging;
using Zeller.PaymentsSdk.Enums;
using Zeller.PaymentsSdk.Models;
using Zeller.PaymentsSdk.Responses;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller SDK 适配层：把 SDK 0.1.0 的真实响应类型转换成我们自己的类型。
/// SDK 的模型属性是 internal set（由它自己用 Newtonsoft 填），测试用反射赋值来构造。
/// 不构造真正的 Terminal：它的构造会去建 SDK 的本机目录、读配对文件。
/// </summary>
public sealed class ZellerSdkAdapterTests
{
    private const string Reference = "HBZ-799287a33dc24df1bca3d33eea19b35a";
    private const string RefundReference = "HBZR-0f3c5d9a6b2e4c1d8e7f6a5b4c3d2e1f";
    private const string TransactionUuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";

    // ── 扣款、退款的返回 ──

    [Fact]
    public void Approved_purchase_maps_every_field_we_keep()
    {
        var result = ZellerSdkMapper.ToCallResult(new PurchaseSuccessResponse(Transaction()));

        Assert.Equal(ZellerCallOutcome.Transaction, result.Outcome);
        Assert.True(result.ReachedTerminal);
        Assert.True(result.ProcessingStarted);
        var transaction = Assert.IsType<ZellerTransaction>(result.Transaction);
        Assert.Equal(ZellerTransactionStatus.Approved, transaction.Status);
        Assert.Equal(ZellerTransactionType.Purchase, transaction.Type);
        Assert.Equal(1250, transaction.AmountCents);
        Assert.Equal(TransactionUuid, transaction.TransactionUuid);
        Assert.Equal(Reference, transaction.ExternalReference);
        Assert.Equal("00", transaction.ResponseCode);
        Assert.Equal("APPROVED", transaction.ResponseText);
        Assert.Equal("123456", transaction.ApprovalCode);
        Assert.Equal("530912345678", transaction.Rrn);
        Assert.Equal("...2902", transaction.PanMasked);
        Assert.Equal("VISA", transaction.Scheme);
        Assert.Equal("476173", transaction.Bin);
        Assert.Equal("CAID-0001", transaction.MerchantId);
        Assert.Equal("CATID-01", transaction.TerminalId);
        Assert.Equal("RECEIPT", transaction.ReceiptText);
        Assert.Equal("https://receipt.example/1", transaction.ReceiptLink);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 1, 27, 12, 345, TimeSpan.Zero), transaction.Timestamp);
        Assert.Null(result.ErrorType);
    }

    [Fact]
    public void Declined_purchase_stays_a_transaction_so_the_classifier_reads_its_status()
    {
        // 真机：余额不足是「成交类响应」+ Status=Declined（code 51），不是错误。
        var result = ZellerSdkMapper.ToCallResult(new PurchaseSuccessResponse(Transaction(TransactionStatus.Declined, responseCode: "51")));

        Assert.Equal(ZellerCallOutcome.Transaction, result.Outcome);
        Assert.Equal(ZellerTransactionStatus.Declined, result.Transaction!.Status);
        Assert.Equal("51", result.Transaction.ResponseCode);
    }

    [Fact]
    public void Failed_status_maps_to_failed()
    {
        var result = ZellerSdkMapper.ToCallResult(new PurchaseSuccessResponse(Transaction(TransactionStatus.Failed)));

        Assert.Equal(ZellerTransactionStatus.Failed, result.Transaction!.Status);
    }

    [Fact]
    public void Status_values_this_build_does_not_know_map_to_unknown()
    {
        var result = ZellerSdkMapper.ToCallResult(new PurchaseSuccessResponse(Transaction((TransactionStatus)7)));

        Assert.Equal(ZellerTransactionStatus.Unknown, result.Transaction!.Status);
    }

    [Fact]
    public void Approved_refund_maps_to_a_refund_transaction()
    {
        var refund = Transaction(type: TransactionType.Refund, externalReference: RefundReference);

        var result = ZellerSdkMapper.ToCallResult(new RefundSuccessResponse(refund));

        Assert.Equal(ZellerCallOutcome.Transaction, result.Outcome);
        Assert.Equal(ZellerTransactionType.Refund, result.Transaction!.Type);
        Assert.Equal(RefundReference, result.Transaction.ExternalReference);
    }

    [Fact]
    public void Success_response_without_a_transaction_is_faulted_not_approved()
    {
        var purchase = ZellerSdkMapper.ToCallResult(new PurchaseSuccessResponse(null!));
        var refund = ZellerSdkMapper.ToCallResult(new RefundSuccessResponse(null!));

        Assert.Equal(ZellerCallOutcome.Faulted, purchase.Outcome);
        Assert.Contains("without a transaction", purchase.Message);
        Assert.Equal(ZellerCallOutcome.Faulted, refund.Outcome);
    }

    [Fact]
    public void Missing_response_is_faulted()
    {
        Assert.Equal(ZellerCallOutcome.Faulted, ZellerSdkMapper.ToCallResult((PurchaseResponse?)null).Outcome);
        Assert.Equal(ZellerCallOutcome.Faulted, ZellerSdkMapper.ToCallResult((RefundResponse?)null).Outcome);
    }

    [Fact]
    public void Error_response_keeps_the_error_type_and_message()
    {
        var result = ZellerSdkMapper.ToCallResult(new PurchaseErrorResponse(Error(ErrorType.InactivityTimeout, "User response timeout")));

        Assert.Equal(ZellerCallOutcome.Error, result.Outcome);
        Assert.Equal(ZellerErrorType.InactivityTimeout, result.ErrorType);
        Assert.Equal("User response timeout", result.Message);
        Assert.False(result.ReachedTerminal);
        Assert.False(result.ProcessingStarted);
        Assert.Null(result.Transaction);
    }

    [Fact]
    public void Refund_error_for_a_missing_original_maps_to_transaction_not_found()
    {
        var result = ZellerSdkMapper.ToCallResult(new RefundErrorResponse(Error(ErrorType.TransactionNotFound, "not found")));

        Assert.Equal(ZellerCallOutcome.Error, result.Outcome);
        Assert.Equal(ZellerErrorType.TransactionNotFound, result.ErrorType);
    }

    [Fact]
    public void Error_response_without_details_is_unrecognised()
    {
        var result = ZellerSdkMapper.ToCallResult(new PurchaseErrorResponse(null!));

        Assert.Equal(ZellerCallOutcome.Error, result.Outcome);
        Assert.Equal(ZellerErrorType.Unrecognised, result.ErrorType);
        Assert.Contains("without details", result.Message);
    }

    [Fact]
    public void Every_sdk_error_type_maps_to_ours_with_the_same_name()
    {
        // 第一块按 SDK 0.1.0 的 26 种错误类型手抄了数值；这里拿真 SDK 核对一遍，SDK 升级改了数值会在这里失败。
        var sdkTypes = Enum.GetValues<ErrorType>();

        Assert.Equal(26, sdkTypes.Length);
        foreach (var sdkType in sdkTypes)
        {
            Assert.Equal(sdkType.ToString(), ZellerSdkMapper.ToErrorType(sdkType).ToString());
        }
    }

    [Fact]
    public void Error_types_added_by_a_later_sdk_map_to_unrecognised()
    {
        Assert.Equal(ZellerErrorType.Unrecognised, ZellerSdkMapper.ToErrorType((ErrorType)99));
        Assert.Equal(ZellerErrorType.Unrecognised, ZellerSdkMapper.ToErrorType((ErrorType)(-1)));
    }

    [Fact]
    public void Blank_strings_become_null()
    {
        var transaction = Transaction(approvalCode: "", receiptText: "  ");

        var mapped = ZellerSdkMapper.ToTransaction(transaction);

        Assert.Null(mapped.ApprovalCode);
        Assert.Null(mapped.ReceiptText);
    }

    // ── 时间戳 ──

    [Theory]
    [InlineData("2026-11-05T01:27:12.345Z", "2026-11-05T01:27:12.3450000+00:00")]
    [InlineData("2026-11-05T11:24:01+10:00", "2026-11-05T11:24:01.0000000+10:00")]
    [InlineData("2026-11-05T03:22:45.828", "2026-11-05T03:22:45.8280000+00:00")]
    public void Terminal_timestamps_parse_in_both_observed_forms(string value, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ZellerSdkMapper.ParseTimestamp(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a time")]
    public void Unparseable_timestamps_are_left_empty(string? value)
    {
        Assert.Null(ZellerSdkMapper.ParseTimestamp(value));
    }

    // ── 查询 ──

    [Fact]
    public void Query_with_no_transactions_is_a_successful_empty_result()
    {
        var result = ZellerSdkMapper.ToQueryResult(new GetTransactionsSuccessResponse([]));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public void Query_keeps_every_transaction_and_skips_null_entries()
    {
        var first = Transaction();
        var second = Transaction(TransactionStatus.Declined, transactionUuid: "b91ac661-0000-4000-8000-000000000002");

        var result = ZellerSdkMapper.ToQueryResult(new GetTransactionsSuccessResponse([first, null!, second]));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal(ZellerTransactionStatus.Declined, result.Transactions[1].Status);
    }

    [Fact]
    public void Query_with_a_null_array_is_an_empty_success()
    {
        var result = ZellerSdkMapper.ToQueryResult(new GetTransactionsSuccessResponse(null!));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public void Query_error_is_a_failed_query_not_an_empty_one()
    {
        var result = ZellerSdkMapper.ToQueryResult(new GetTransactionsErrorResponse(Error(ErrorType.NetworkFailure, "offline")));

        Assert.False(result.Succeeded);
        Assert.Equal(ZellerErrorType.NetworkFailure, result.ErrorType);
        Assert.Equal("offline", result.Message);
    }

    [Fact]
    public void Missing_query_response_is_a_failed_query()
    {
        var result = ZellerSdkMapper.ToQueryResult(null);

        Assert.False(result.Succeeded);
        Assert.Equal(ZellerErrorType.Unrecognised, result.ErrorType);
    }

    // ── 初始化与配对 ──

    [Fact]
    public void Initialise_and_setup_report_pairing()
    {
        Assert.True(ZellerSdkMapper.ToPairingResult(new InitialiseSuccessResponse(true)).IsPaired);
        Assert.False(ZellerSdkMapper.ToPairingResult(new InitialiseSuccessResponse(false)).IsPaired);
        Assert.True(ZellerSdkMapper.ToPairingResult(new SetupSuccessResponse(true)).IsPaired);

        var notPaired = ZellerSdkMapper.ToPairingResult(new InitialiseErrorResponse(Error(ErrorType.SetupRequired, "pair first")));
        Assert.False(notPaired.IsPaired);
        Assert.Equal(ZellerErrorType.SetupRequired, notPaired.ErrorType);
        Assert.Equal("pair first", notPaired.Message);

        var cancelled = ZellerSdkMapper.ToPairingResult(new SetupErrorResponse(Error(ErrorType.Cancelled, "closed")));
        Assert.False(cancelled.IsPaired);
        Assert.Equal(ZellerErrorType.Cancelled, cancelled.ErrorType);

        Assert.Equal(ZellerErrorType.Unrecognised, ZellerSdkMapper.ToPairingResult((InitialiseResponse?)null).ErrorType);
        Assert.Equal(ZellerErrorType.Unrecognised, ZellerSdkMapper.ToPairingResult((SetupResponse?)null).ErrorType);
    }

    // ── 事件 ──

    [Theory]
    [InlineData(EventType.AwaitingPaymentMethod, ZellerTerminalEventKind.AwaitingPaymentMethod)]
    [InlineData(EventType.Processing, ZellerTerminalEventKind.Processing)]
    [InlineData(EventType.AwaitingPinEntry, ZellerTerminalEventKind.Other)]
    [InlineData(EventType.RetryingPaymentMethod, ZellerTerminalEventKind.Other)]
    [InlineData(EventType.Unknown, ZellerTerminalEventKind.Other)]
    public void Events_keep_only_the_two_kinds_the_state_machine_needs(EventType sdkEvent, ZellerTerminalEventKind expected)
    {
        var mapped = ZellerSdkMapper.ToEvent(Event(sdkEvent, "Present card"));

        Assert.Equal(expected, mapped.Kind);
        Assert.Equal(sdkEvent.ToString(), mapped.Name);
        Assert.Equal("Present card", mapped.Message);
    }

    [Fact]
    public void Null_event_is_other()
    {
        Assert.Equal(ZellerTerminalEventKind.Other, ZellerSdkMapper.ToEvent(null).Kind);
    }

    // ── 调用包装 ──

    [Fact]
    public async Task Operation_maps_the_response_and_forwards_cancel_and_events()
    {
        var response = new TaskCompletionSource<PurchaseResponse>();
        Action<EventResponseValueData>? listener = null;
        var cancels = 0;
        var operation = new ZellerSdkOperation<PurchaseResponse, ZellerCallResult>(
            response.Task,
            () => cancels++,
            callback => listener = callback,
            ZellerSdkMapper.ToCallResult);
        var events = new List<ZellerTerminalEvent>();

        operation.OnEvent(events.Add);
        listener!(Event(EventType.AwaitingPaymentMethod));
        listener(Event(EventType.Processing));
        operation.Cancel();
        Assert.False(operation.Completion.IsCompleted);
        response.SetResult(new PurchaseSuccessResponse(Transaction()));
        var result = await operation.Completion;

        Assert.Equal(1, cancels);
        Assert.Equal([ZellerTerminalEventKind.AwaitingPaymentMethod, ZellerTerminalEventKind.Processing], events.Select(e => e.Kind));
        Assert.Equal(ZellerCallOutcome.Transaction, result.Outcome);
    }

    [Fact]
    public async Task Operation_passes_sdk_exceptions_through_for_the_gateway_to_record()
    {
        var operation = new ZellerSdkOperation<PurchaseResponse, ZellerCallResult>(
            Task.FromException<PurchaseResponse>(new InvalidOperationException("WebView2 runtime not found")),
            () => { },
            _ => { },
            ZellerSdkMapper.ToCallResult);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Completion);
        Assert.Contains("WebView2", ex.Message);
    }

    [Fact]
    public async Task Gateway_turns_a_real_sdk_purchase_response_into_an_approved_classification()
    {
        // 串起来走一遍：真 SDK 响应对象 → 适配层 → 网关（事件证据）→ 分类器。
        var response = new TaskCompletionSource<PurchaseResponse>();
        Action<EventResponseValueData>? listener = null;
        var terminal = new StubSdkTerminal(new ZellerSdkOperation<PurchaseResponse, ZellerCallResult>(
            response.Task,
            () => { },
            callback => listener = callback,
            ZellerSdkMapper.ToCallResult));
        using var gateway = new ZellerTerminalGateway(new StubSdkFactory(terminal), new ImmediateZellerDispatcher());

        var call = gateway.PurchaseAsync(new ZellerPurchaseRequest(1250, Reference));
        await WaitUntilAsync(() => listener is not null);
        listener!(Event(EventType.AwaitingPaymentMethod));
        listener(Event(EventType.Processing));
        response.SetResult(new PurchaseSuccessResponse(Transaction()));
        var result = await call;

        var classification = ZellerResultClassifier.ClassifyCall(
            result,
            new ZellerAttemptExpectation(ZellerTransactionType.Purchase, 1250, Reference));
        Assert.Equal(ZellerCallDecision.Approved, classification.Decision);
        Assert.Equal(TransactionUuid, classification.Transaction!.TransactionUuid);
    }

    [Fact]
    public async Task Gateway_keeps_event_evidence_when_the_sdk_reports_a_network_failure()
    {
        // 真机：刷卡中断网，约 72 秒后返回 NetworkFailure，但钱可能已经扣了。收到过「处理中」就不能当成没扣款。
        var response = new TaskCompletionSource<PurchaseResponse>();
        Action<EventResponseValueData>? listener = null;
        var terminal = new StubSdkTerminal(new ZellerSdkOperation<PurchaseResponse, ZellerCallResult>(
            response.Task,
            () => { },
            callback => listener = callback,
            ZellerSdkMapper.ToCallResult));
        using var gateway = new ZellerTerminalGateway(new StubSdkFactory(terminal), new ImmediateZellerDispatcher());

        var call = gateway.PurchaseAsync(new ZellerPurchaseRequest(1250, Reference));
        await WaitUntilAsync(() => listener is not null);
        listener!(Event(EventType.AwaitingPaymentMethod));
        listener(Event(EventType.Processing));
        response.SetResult(new PurchaseErrorResponse(Error(ErrorType.NetworkFailure, "network")));
        var result = await call;

        Assert.Equal(ZellerCallOutcome.Error, result.Outcome);
        Assert.True(result.ReachedTerminal);
        Assert.True(result.ProcessingStarted);
        var classification = ZellerResultClassifier.ClassifyCall(
            result,
            new ZellerAttemptExpectation(ZellerTransactionType.Purchase, 1250, Reference));
        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
    }

    // ── 日志 ──

    [Fact]
    public void Sdk_debug_lines_are_dropped_by_default()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        using var provider = new ZellerSdkLoggerProvider(sink: (level, message, _) => lines.Add((level, message)));
        var logger = provider.CreateLogger("zeller.sdk.dotnet.Path");

        logger.LogDebug("preparing directories");
        logger.LogInformation("initialised");
        logger.LogWarning("slow response");

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.Equal(
            [(LogLevel.Information, "zeller.sdk.dotnet.Path: initialised"), (LogLevel.Warning, "zeller.sdk.dotnet.Path: slow response")],
            lines);
    }

    [Fact]
    public void Sdk_debug_lines_are_kept_when_the_minimum_level_is_lowered()
    {
        var lines = new List<string>();
        using var provider = new ZellerSdkLoggerProvider(LogLevel.Debug, (_, message, _) => lines.Add(message));

        provider.CreateLogger("zeller.sdk.dotnet.Message").LogDebug("received message");

        Assert.Equal(["zeller.sdk.dotnet.Message: received message"], lines);
    }

    [Fact]
    public void A_failing_log_sink_never_reaches_the_sdk()
    {
        using var provider = new ZellerSdkLoggerProvider(sink: (_, _, _) => throw new IOException("disk full"));

        var ex = Record.Exception(() => provider.CreateLogger("zeller.sdk.dotnet.UiService").LogError("boom"));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_none_is_never_enabled()
    {
        using var provider = new ZellerSdkLoggerProvider(LogLevel.Trace, (_, _, _) => { });

        Assert.False(provider.CreateLogger("x").IsEnabled(LogLevel.None));
    }

    // ── 厂商信息 ──

    [Fact]
    public void Vendor_info_uses_the_pos_name_and_a_three_part_version()
    {
        var vendor = ZellerSdkVendorInfo.ForCurrentApplication();

        Assert.Equal("HBPOS", vendor.ApplicationName);
        Assert.Equal("POS", vendor.DeviceType);
        Assert.Equal("HotBargain", vendor.VendorName);
        Assert.Matches(@"^\d+\.\d+\.\d+$", vendor.ApplicationVersion);
    }

    // ── 构造 SDK 对象 ──

    private static TransactionResponseData Transaction(
        TransactionStatus status = TransactionStatus.Approved,
        TransactionType type = TransactionType.Purchase,
        string responseCode = "00",
        string transactionUuid = TransactionUuid,
        string externalReference = Reference,
        string approvalCode = "123456",
        string receiptText = "RECEIPT") =>
        SdkObject<TransactionResponseData>(
            (nameof(TransactionResponseData.Status), status),
            (nameof(TransactionResponseData.Type), type),
            (nameof(TransactionResponseData.Amount), 1250),
            (nameof(TransactionResponseData.TransactionUuid), transactionUuid),
            (nameof(TransactionResponseData.ExternalReference), externalReference),
            (nameof(TransactionResponseData.ResponseCode), responseCode),
            (nameof(TransactionResponseData.ResponseText), "APPROVED"),
            (nameof(TransactionResponseData.ApprovalCode), approvalCode),
            (nameof(TransactionResponseData.Rrn), "530912345678"),
            (nameof(TransactionResponseData.PanMasked), "...2902"),
            (nameof(TransactionResponseData.Scheme), "VISA"),
            (nameof(TransactionResponseData.Bin), "476173"),
            (nameof(TransactionResponseData.Caid), "CAID-0001"),
            (nameof(TransactionResponseData.Catid), "CATID-01"),
            (nameof(TransactionResponseData.ReceiptText), receiptText),
            (nameof(TransactionResponseData.ReceiptLink), "https://receipt.example/1"),
            (nameof(TransactionResponseData.TimestampLocal), "2026-11-05T01:27:12.345Z"));

    private static ErrorResponseData Error(ErrorType type, string message) =>
        SdkObject<ErrorResponseData>((nameof(ErrorResponseData.Type), type), (nameof(ErrorResponseData.Message), message));

    private static EventResponseValueData Event(EventType type, string? message = null) =>
        SdkObject<EventResponseValueData>((nameof(EventResponseValueData.Event), type), (nameof(EventResponseValueData.Message), message));

    private static T SdkObject<T>(params (string Name, object? Value)[] values) where T : new()
    {
        var instance = new T();
        foreach (var (name, value) in values)
        {
            var property = typeof(T).GetProperty(name) ?? throw new InvalidOperationException($"{typeof(T).Name}.{name} not found");
            property.SetValue(instance, value);
        }

        return instance;
    }

    private sealed class StubSdkFactory(IZellerSdkTerminal terminal) : IZellerSdkTerminalFactory
    {
        public IZellerSdkTerminal Create() => terminal;
    }

    private sealed class StubSdkTerminal(IZellerSdkOperation<ZellerCallResult> purchase) : IZellerSdkTerminal
    {
        public IZellerSdkOperation<ZellerPairingResult> Initialise() => throw new NotSupportedException();

        public IZellerSdkOperation<ZellerPairingResult> Setup() => throw new NotSupportedException();

        public IZellerSdkOperation<ZellerCallResult> Purchase(ZellerPurchaseRequest request) => purchase;

        public IZellerSdkOperation<ZellerCallResult> Refund(ZellerRefundRequest request) => throw new NotSupportedException();

        public IZellerSdkOperation<ZellerQueryResult> GetTransactions(string reference) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
