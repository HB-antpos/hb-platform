using System.Globalization;
using Zeller.PaymentsSdk.Enums;
using Zeller.PaymentsSdk.Models;
using Zeller.PaymentsSdk.Responses;

namespace Hbpos.Client.Wpf.Services.Zeller.Sdk;

/// <summary>
/// 把 Zeller.PaymentsSdk 的响应类型转换成我们自己的类型。只做转换，不做判断：
/// 被拒算不算失败、错误怎么处理，都交给 <see cref="ZellerResultClassifier"/>。
/// </summary>
/// <remarks>
/// 中文注释：SDK 返回了本程序不认识的东西时（空结果、新的响应子类、新的枚举值），一律转成「结果未知」一侧：
/// 扣款、退款转成 Faulted，查询转成失败，交易状态转成 Unknown。上层会按 reference 核实，不会把它当成没扣款。
/// </remarks>
internal static class ZellerSdkMapper
{
    public static ZellerCallResult ToCallResult(PurchaseResponse? response) => response switch
    {
        PurchaseSuccessResponse { Result: { } transaction } => ZellerCallResult.FromTransaction(ToTransaction(transaction)),
        PurchaseErrorResponse error => ToErrorResult(error.Result),
        _ => Unexpected(response)
    };

    public static ZellerCallResult ToCallResult(RefundResponse? response) => response switch
    {
        RefundSuccessResponse { Result: { } transaction } => ZellerCallResult.FromTransaction(ToTransaction(transaction)),
        RefundErrorResponse error => ToErrorResult(error.Result),
        _ => Unexpected(response)
    };

    public static ZellerQueryResult ToQueryResult(GetTransactionsResponse? response) => response switch
    {
        GetTransactionsSuccessResponse success => ZellerQueryResult.Found(
            (success.Result ?? []).Where(transaction => transaction is not null).Select(ToTransaction).ToArray()),
        GetTransactionsErrorResponse error => ZellerQueryResult.Failed(ToErrorType(error.Result), ToErrorMessage(error.Result)),
        _ => ZellerQueryResult.Failed(ZellerErrorType.Unrecognised, DescribeUnexpected(response))
    };

    public static ZellerPairingResult ToPairingResult(InitialiseResponse? response) => response switch
    {
        InitialiseSuccessResponse success => new ZellerPairingResult(success.Result),
        InitialiseErrorResponse error => new ZellerPairingResult(false, ToErrorType(error.Result), ToErrorMessage(error.Result)),
        _ => new ZellerPairingResult(false, ZellerErrorType.Unrecognised, DescribeUnexpected(response))
    };

    public static ZellerPairingResult ToPairingResult(SetupResponse? response) => response switch
    {
        SetupSuccessResponse success => new ZellerPairingResult(success.Result),
        SetupErrorResponse error => new ZellerPairingResult(false, ToErrorType(error.Result), ToErrorMessage(error.Result)),
        _ => new ZellerPairingResult(false, ZellerErrorType.Unrecognised, DescribeUnexpected(response))
    };

    public static ZellerTerminalEvent ToEvent(EventResponseValueData? data)
    {
        if (data is null)
        {
            return new ZellerTerminalEvent(ZellerTerminalEventKind.Other, "Empty");
        }

        var kind = data.Event switch
        {
            EventType.AwaitingPaymentMethod => ZellerTerminalEventKind.AwaitingPaymentMethod,
            EventType.Processing => ZellerTerminalEventKind.Processing,
            _ => ZellerTerminalEventKind.Other
        };
        return new ZellerTerminalEvent(kind, data.Event.ToString(), NullIfEmpty(data.Message));
    }

    public static ZellerTransaction ToTransaction(TransactionResponseData transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new ZellerTransaction(
            ToStatus(transaction.Status),
            ToType(transaction.Type),
            transaction.Amount,
            NullIfEmpty(transaction.TransactionUuid),
            NullIfEmpty(transaction.ExternalReference),
            ResponseCode: NullIfEmpty(transaction.ResponseCode),
            ResponseText: NullIfEmpty(transaction.ResponseText),
            ApprovalCode: NullIfEmpty(transaction.ApprovalCode),
            Rrn: NullIfEmpty(transaction.Rrn),
            PanMasked: NullIfEmpty(transaction.PanMasked),
            Scheme: NullIfEmpty(transaction.Scheme),
            Bin: NullIfEmpty(transaction.Bin),
            // 中文注释：Caid / Catid 是收单的商户号（Card Acceptor ID）和终端号（Card Acceptor Terminal ID）。
            MerchantId: NullIfEmpty(transaction.Caid),
            TerminalId: NullIfEmpty(transaction.Catid),
            ReceiptText: NullIfEmpty(transaction.ReceiptText),
            ReceiptLink: NullIfEmpty(transaction.ReceiptLink),
            Timestamp: ParseTimestamp(transaction.TimestampLocal));
    }

    /// <summary>按数值转换 SDK 的错误类型；不认识的数值（SDK 以后新增的）转成 Unrecognised。</summary>
    public static ZellerErrorType ToErrorType(ErrorType errorType)
    {
        var value = (int)errorType;
        return value >= 0 && Enum.IsDefined(typeof(ZellerErrorType), value)
            ? (ZellerErrorType)value
            : ZellerErrorType.Unrecognised;
    }

    /// <summary>
    /// 刷卡机给的时间。实测有两种写法：UTC 带 Z（2026-11-05T01:27:12.345Z）和带时区（2026-11-05T11:24:01+10:00）；
    /// 没有时区的按 UTC 处理。只用于显示和排序，解析不了就留空。
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static ZellerTransactionStatus ToStatus(TransactionStatus status) => status switch
    {
        TransactionStatus.Approved => ZellerTransactionStatus.Approved,
        TransactionStatus.Declined => ZellerTransactionStatus.Declined,
        TransactionStatus.Failed => ZellerTransactionStatus.Failed,
        _ => ZellerTransactionStatus.Unknown
    };

    private static ZellerTransactionType ToType(TransactionType type) => type switch
    {
        TransactionType.Purchase => ZellerTransactionType.Purchase,
        TransactionType.Refund => ZellerTransactionType.Refund,
        _ => ZellerTransactionType.Unknown
    };

    private static ZellerCallResult ToErrorResult(ErrorResponseData? error) =>
        ZellerCallResult.FromError(ToErrorType(error), ToErrorMessage(error));

    private static ZellerErrorType ToErrorType(ErrorResponseData? error) =>
        error is null ? ZellerErrorType.Unrecognised : ToErrorType(error.Type);

    private static string? ToErrorMessage(ErrorResponseData? error) =>
        error is null ? "the Zeller SDK returned an error without details" : NullIfEmpty(error.Message);

    private static ZellerCallResult Unexpected(object? response) => ZellerCallResult.Faulted(DescribeUnexpected(response));

    private static string DescribeUnexpected(object? response) => response switch
    {
        null => "the Zeller SDK returned no response",
        PurchaseSuccessResponse or RefundSuccessResponse => "the Zeller SDK returned a success response without a transaction",
        _ => $"the Zeller SDK returned an unexpected response type {response.GetType().Name}"
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
