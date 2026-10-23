using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using PCEFTPOS.EFTClient.IPInterface;

namespace Hbpos.Client.Tests;

/// <summary>
/// 2026-10-22 1002 店事故回归：刷卡机已打出 APPROVED 00 的顾客小票，最终响应却回 PF「PINpad Offline」、
/// TxnRef 为空、金额 0。凭与本笔一致的小票证据必须判为已批准并落库证据；证据不匹配或没有小票时维持原规则。
/// </summary>
public sealed class LinklyTerminalClientReceiptEvidenceTests
{
    private const string TxnRef = LinklyLocalReceiptApprovalEvidenceTests.TxnRef;

    private static EFTTransactionResponse PinpadOfflineResponse() => new()
    {
        Success = false,
        TxnRef = new string(' ', 16),
        AmtPurchase = 0m,
        ResponseCode = "PF",
        ResponseText = "PINpad Offline      "
    };

    private static EFTReceiptResponse Receipt(string text) => new()
    {
        Type = ReceiptType.Customer,
        ReceiptText = text.Split(Environment.NewLine)
    };

    [Fact]
    public async Task Approved_receipt_followed_by_pinpad_offline_is_approved_and_evidence_is_recorded()
    {
        var eftClient = new ScriptedEftClient(
            new EFTReceiptResponse { Type = ReceiptType.Customer, ReceiptText = [string.Empty] },
            Receipt(LinklyLocalReceiptApprovalEvidenceTests.ApprovedReceipt()),
            PinpadOfflineResponse());
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var recorded = new List<LinklyLocalReceiptApprovalEvidence>();
        var client = new LinklyTerminalClient(new ScriptedEftClientFactory(eftClient), attemptContextAccessor: accessor);

        PaymentAuthorizationResult result;
        using (accessor.Begin(new LinklyPaymentAttemptContext(
                   Guid.NewGuid(),
                   (_, _, _, _) => Task.CompletedTask,
                   TxnRef)
               {
                   RecordReceiptApprovalEvidenceAsync = (evidence, _) =>
                   {
                       recorded.Add(evidence);
                       return Task.CompletedTask;
                   }
               }))
        {
            result = await client.PurchaseWithReferenceAsync(13.99m, CreateSession(), CreateSettings(), TxnRef);
        }

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal($"ANZ:{TxnRef}", result.Reference);
        Assert.Equal(TxnRef, result.TxnRef);
        Assert.Equal(13.99m, result.AuthorizedAmount);
        Assert.Equal("P", result.TxnType);
        Assert.Equal("00", result.ResponseCode);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("267836", transaction.AuthCode);
        Assert.Equal("147950", transaction.Stan);
        Assert.Equal(13.99m, transaction.Amount);
        Assert.Contains("APPROVAL CODE", transaction.ReceiptText, StringComparison.Ordinal);

        // 小票一到就落库证据（只一次），即使随后 POS 断电，重启恢复也能凭它建单。
        var evidence = Assert.Single(recorded);
        Assert.Equal("P29R5YNFN4XM", evidence.Rrn);
        Assert.Equal("267836", evidence.ApprovalCode);
    }

    [Theory]
    [InlineData("P29R5YNFN4XX", "$13.99")]
    [InlineData("P29R5YNFN4XM", "$31.99")]
    public async Task Receipt_with_mismatched_rrn_or_amount_followed_by_pinpad_offline_stays_unknown(string rrn, string amount)
    {
        var eftClient = new ScriptedEftClient(
            Receipt(LinklyLocalReceiptApprovalEvidenceTests.ApprovedReceipt(rrn, amount)),
            PinpadOfflineResponse());
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var recordCount = 0;
        var client = new LinklyTerminalClient(new ScriptedEftClientFactory(eftClient), attemptContextAccessor: accessor);

        PaymentAuthorizationResult result;
        using (accessor.Begin(new LinklyPaymentAttemptContext(
                   Guid.NewGuid(),
                   (_, _, _, _) => Task.CompletedTask,
                   TxnRef)
               {
                   RecordReceiptApprovalEvidenceAsync = (_, _) =>
                   {
                       recordCount++;
                       return Task.CompletedTask;
                   }
               }))
        {
            result = await client.PurchaseWithReferenceAsync(13.99m, CreateSession(), CreateSettings(), TxnRef);
        }

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.NotEqual("linkly.local.pinpadOffline", result.StatusKey);
        Assert.Equal(0, recordCount);
    }

    [Fact]
    public async Task Pinpad_offline_without_any_receipt_remains_definitively_not_submitted()
    {
        var eftClient = new ScriptedEftClient(PinpadOfflineResponse());
        var client = new LinklyTerminalClient(new ScriptedEftClientFactory(eftClient));

        var result = await client.PurchaseWithReferenceAsync(13.99m, CreateSession(), CreateSettings(), TxnRef);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("linkly.local.pinpadOffline", result.StatusKey);
    }

    [Fact]
    public async Task Approved_receipt_does_not_override_a_response_echoing_another_reference()
    {
        // 回显了别的 TxnRef 属于身份冲突：小票证据不能盖过它，仍按结果未知走人工核对。
        var response = PinpadOfflineResponse();
        response.TxnRef = "P000000000000999";
        var eftClient = new ScriptedEftClient(
            Receipt(LinklyLocalReceiptApprovalEvidenceTests.ApprovedReceipt()),
            response);
        var client = new LinklyTerminalClient(new ScriptedEftClientFactory(eftClient));

        var result = await client.PurchaseWithReferenceAsync(13.99m, CreateSession(), CreateSettings(), TxnRef);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task Get_last_not_found_after_connection_loss_is_approved_by_captured_receipt()
    {
        // 交易连接在小票之后断开（读取异常），恢复查询回 JH / NOT FOUND、空 TxnRef：凭已收到的小票判为已批准。
        var transactionClient = new ScriptedEftClient(
            Receipt(LinklyLocalReceiptApprovalEvidenceTests.ApprovedReceipt()))
        {
            ThrowConnectionClosedWhenEmpty = true
        };
        var recoveryClient = new ScriptedEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = false,
            TxnRef = new string(' ', 16),
            AmtPurchase = 1m,
            ResponseCode = "JH",
            ResponseText = "NOT FOUND           "
        });
        var client = new LinklyTerminalClient(new ScriptedEftClientFactory(transactionClient, recoveryClient));

        var result = await client.PurchaseWithReferenceAsync(13.99m, CreateSession(), CreateSettings(), TxnRef);

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(TxnRef, result.TxnRef);
        Assert.Equal(13.99m, result.AuthorizedAmount);
    }

    private static PosSessionState CreateSession() =>
        new("HB POS", "1002", "Main", "TERM-1", "C001", "Cashier", true, 0);

    private static CardTerminalSettings CreateSettings() =>
        new(
            CardProcessorKind.Linkly,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            null,
            null,
            null,
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(10));

    private sealed class ScriptedEftClientFactory(params ILinklyEftClient[] clients) : ILinklyEftClientFactory
    {
        private readonly Queue<ILinklyEftClient> _clients = new(clients);
        private ILinklyEftClient? _last;

        public ILinklyEftClient Create()
        {
            if (_clients.Count > 0)
            {
                _last = _clients.Dequeue();
            }

            return _last ?? throw new InvalidOperationException("No scripted EFT client.");
        }
    }

    private sealed class ScriptedEftClient(params EFTResponse[] responses) : ILinklyEftClient
    {
        private readonly Queue<EFTResponse> _responses = new(responses);

        public bool ThrowConnectionClosedWhenEmpty { get; init; }

        public Task<bool> ConnectAsync(string hostName, int hostPort, bool useSsl, bool useKeepAlive) =>
            Task.FromResult(true);

        public Task<bool> WriteRequestAsync(EFTRequest request) => Task.FromResult(true);

        public Task<bool> SendCancelRequestAsync() => Task.FromResult(true);

        public Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken)
        {
            if (_responses.Count > 0)
            {
                return Task.FromResult<EFTResponse?>(_responses.Dequeue());
            }

            if (ThrowConnectionClosedWhenEmpty)
            {
                throw new ConnectionException("Connection closed by remote host.");
            }

            return Task.FromResult<EFTResponse?>(null);
        }

        public bool Disconnect() => true;

        public void Dispose()
        {
        }
    }
}
