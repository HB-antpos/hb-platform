using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using PCEFTPOS.EFTClient.IPInterface;
using System.Text.Json;

namespace Hbpos.Client.Tests;

public sealed class LinklyTerminalClientTests
{
    [Fact]
    public async Task PurchaseAsync_sends_purchase_request_and_returns_card_transaction()
    {
        using var logs = new ConsoleLogCapture();
        var eftClient = new FakeLinklyEftClient(
            new EFTReceiptResponse
            {
                ReceiptText = ["MERCHANT COPY", "APPROVED"]
            },
            new EFTTransactionResponse
            {
                Success = true,
                TxnRef = "TXN-1",
                AmtPurchase = 10m,
                Pan = "411111******1234",
                CardType = "VISA",
                AuthCode = 123456,
                CardName = 4,
                Caid = "MID-1",
                ResponseCode = "00",
                ResponseText = "APPROVED",
                Stan = 42,
                DateSettlement = DateTime.Parse("2026-05-26T00:00:00Z")
            })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Matches("^ANZ:P[0-9A-HJKMNP-TV-Z]{15}$", result.Reference);
        Assert.Equal("P", result.TxnType);
        var request = Assert.IsType<EFTTransactionRequest>(eftClient.LastRequest);
        Assert.Equal(TransactionType.PurchaseCash, request.TxnType);
        Assert.Equal(10m, request.AmtPurchase);
        Assert.Equal("00", request.Merchant);
        Assert.Equal(TerminalApplication.EFTPOS, request.Application);
        Assert.Matches("^P[0-9A-HJKMNP-TV-Z]{15}$", request.TxnRef);
        var wire = new DefaultMessageParser().EFTRequestToString(request);
        Assert.Contains(request.TxnRef, wire, StringComparison.Ordinal);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("ANZ", transaction.Processor);
        Assert.Equal(request.TxnRef, transaction.TxnRef);
        Assert.Equal("****1234", transaction.MaskedCardNumber);
        Assert.Contains("MERCHANT COPY", transaction.ReceiptText);

        var events = logs.ReadJsonEvents("LinklyLocal");
        var connectEvent = AssertEvent(events, "connect", "succeeded", "response");
        Assert.True(connectEvent.GetProperty("response").GetProperty("connected").GetBoolean());
        var requestEvent = AssertEvent(events, "transaction", "sent", "request");
        Assert.True(requestEvent.TryGetProperty("request", out var requestJson));
        Assert.Matches("^P[0-9A-HJKMNP-TV-Z]{15}$", requestJson.GetProperty("txnRef").GetString());
        Assert.Equal("00", requestJson.GetProperty("merchant").GetString());
        Assert.Equal("10", requestJson.GetProperty("amtPurchase").GetRawText());
        var receiptEvent = AssertEvent(events, "receipt", "received", "response");
        Assert.True(receiptEvent.TryGetProperty("response", out var receiptJson));
        Assert.Equal("MERCHANT COPY", receiptJson.GetProperty("receiptText")[0].GetString());
        var responseEvent = AssertEvent(events, "transaction", "received", "response");
        Assert.True(responseEvent.TryGetProperty("response", out var responseJson));
        Assert.Equal(request.TxnRef, responseJson.GetProperty("txnRef").GetString());
        Assert.Equal("00", responseJson.GetProperty("responseCode").GetString());
        Assert.Equal("10", responseJson.GetProperty("amtPurchase").GetRawText());
        var disconnectEvent = AssertEvent(events, "disconnect", "succeeded", "response");
        Assert.True(disconnectEvent.GetProperty("response").GetProperty("disconnected").GetBoolean());
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public void LinklyLocalTxnRef_uses_the_exact_sha256_big_endian_base32_contract()
    {
        const string stableIdentity = "00000000-0000-0000-0000-000000000001";
        var purchase = LinklyLocalTxnRef.Create('P', stableIdentity);
        var refund = LinklyLocalTxnRef.Create('R', stableIdentity);

        Assert.Equal("PMND6WYVF2GBVY6G", purchase);
        Assert.Equal("RB7VFXNSJNPBZ6QX", refund);
        Assert.Matches("^[PR][0-9A-HJKMNP-TV-Z]{15}$", purchase);
        Assert.Matches("^[PR][0-9A-HJKMNP-TV-Z]{15}$", refund);
    }

    [Fact]
    public void BuildTxnRef_retains_settlement_operation_id_device_and_utc_timestamp()
    {
        var operationId = LinklyTerminalClient.BuildTxnRef(CreateSession("DEVICE-12345678901234567890"));

        Assert.Matches("^DEVICE12345678901234567890\\d{15}$", operationId);
        Assert.True(operationId.Length > 16);
        Assert.DoesNotMatch("^P[0-9A-HJKMNP-TV-Z]{15}$", operationId);
        Assert.DoesNotMatch("^R[0-9A-HJKMNP-TV-Z]{15}$", operationId);
    }

    [Fact]
    public async Task PurchaseWithReferenceAsync_accepts_an_anz_prefix_before_a_sixteen_character_reference()
    {
        const string normalizedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnRef = normalizedReference,
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var factory = new FakeLinklyEftClientFactory(eftClient);
        var client = new LinklyTerminalClient(factory);

        var result = await client.PurchaseWithReferenceAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            $"ANZ:{normalizedReference}");

        Assert.True(result.Approved);
        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(normalizedReference, Assert.IsType<EFTTransactionRequest>(eftClient.LastRequest).TxnRef);
    }

    [Fact]
    public async Task PurchaseWithReferenceAsync_rejects_an_anz_prefix_before_a_seventeen_character_reference()
    {
        var eftClient = new FakeLinklyEftClient();
        var factory = new FakeLinklyEftClientFactory(eftClient);
        var client = new LinklyTerminalClient(factory);

        var result = await client.PurchaseWithReferenceAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "ANZ:P1234567890123456");

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(0, factory.CreatedCount);
        Assert.Empty(eftClient.Requests);
    }

    [Fact]
    public async Task VoidAsync_generates_distinct_non_protocol_references_for_consecutive_calls_with_a_long_device_code()
    {
        var firstClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnType = TransactionType.Void,
            AmtPurchase = 10m,
            ResponseCode = "00"
        })
        {
            EchoTransactionReference = true
        };
        var secondClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnType = TransactionType.Void,
            AmtPurchase = 10m,
            ResponseCode = "00"
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(firstClient, secondClient));
        var session = CreateSession("DEVICE-12345678901234567890");

        var first = await client.VoidAsync(10m, session, CreateSettings(), null);
        var second = await client.VoidAsync(10m, session, CreateSettings(), null);

        var firstReference = Assert.IsType<EFTTransactionRequest>(firstClient.LastRequest).TxnRef;
        var secondReference = Assert.IsType<EFTTransactionRequest>(secondClient.LastRequest).TxnRef;
        Assert.Matches("^V[0-9A-F]{15}$", firstReference);
        Assert.Matches("^V[0-9A-F]{15}$", secondReference);
        Assert.NotEqual(firstReference, secondReference);
        Assert.True(first.Approved);
        Assert.True(second.Approved);
    }

    [Fact]
    public async Task SettlementAsync_keeps_a_long_operation_id_separate_from_line_transaction_references()
    {
        using var logs = new ConsoleLogCapture();
        var eftClient = new FakeLinklyEftClient(new EFTSettlementResponse
        {
            Success = true,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));
        var session = CreateSession("DEVICE-12345678901234567890");

        var result = await client.SettlementAsync(session, CreateSettings());

        Assert.True(result.Succeeded);
        Assert.Matches("^DEVICE12345678901234567890\\d{15}$", result.SessionId);
        Assert.True(result.SessionId!.Length > 16);
        var sentEvent = AssertEvent(logs.ReadJsonEvents("LinklyLocal"), "settlement", "sent", "request");
        Assert.Equal(result.SessionId, sentEvent.GetProperty("sessionId").GetString());
        Assert.IsType<EFTSettlementRequest>(eftClient.LastRequest);
    }

    [Fact]
    public async Task PurchaseAsync_generates_a_unique_protocol_reference_for_each_call()
    {
        var firstClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            AmtPurchase = 10m,
            ResponseCode = "00",
            TxnType = TransactionType.PurchaseCash
        })
        {
            EchoTransactionReference = true
        };
        var secondClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            AmtPurchase = 10m,
            ResponseCode = "00",
            TxnType = TransactionType.PurchaseCash
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(firstClient, secondClient));

        var first = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        var second = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        var firstRequest = Assert.IsType<EFTTransactionRequest>(firstClient.LastRequest);
        var secondRequest = Assert.IsType<EFTTransactionRequest>(secondClient.LastRequest);
        Assert.Matches("^P[0-9A-HJKMNP-TV-Z]{15}$", firstRequest.TxnRef);
        Assert.Matches("^P[0-9A-HJKMNP-TV-Z]{15}$", secondRequest.TxnRef);
        Assert.NotEqual(firstRequest.TxnRef, secondRequest.TxnRef);
        Assert.Equal("P", first.TxnType);
        Assert.Equal("P", second.TxnType);
    }

    [Fact]
    public async Task RefundAsync_generates_a_new_refund_protocol_reference_instead_of_reusing_the_sale_reference()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            AmtPurchase = 10m,
            ResponseCode = "00",
            TxnType = TransactionType.Refund
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RefundAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "ANZ:ORIGINAL-SALE-001");

        var request = Assert.IsType<EFTTransactionRequest>(eftClient.LastRequest);
        Assert.Equal(TransactionType.Refund, request.TxnType);
        Assert.Matches("^R[0-9A-HJKMNP-TV-Z]{15}$", request.TxnRef);
        Assert.NotEqual("ORIGINAL-SALE-001", request.TxnRef);
        Assert.Equal("R", result.TxnType);
    }

    [Theory]
    [InlineData("12345678901234567")]
    [InlineData(" BAD\u001FREF ")]
    [InlineData("é")]
    public async Task Invalid_historical_reference_fails_before_local_client_creation(string invalidReference)
    {
        var eftClient = new FakeLinklyEftClient();
        var factory = new FakeLinklyEftClientFactory(eftClient);
        var client = new LinklyTerminalClient(factory);

        var result = await client.RecoverLastTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            invalidReference);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(0, factory.CreatedCount);
        Assert.Empty(eftClient.Requests);
    }

    [Fact]
    public async Task PurchaseAsync_reuses_the_same_protocol_reference_for_timeout_get_last_recovery()
    {
        var purchaseClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var getLastClient = new FakeLinklyEftClient
        {
            ResponseFactory = request => new EFTGetLastTransactionResponse
            {
                Success = true,
                LastTransactionSuccess = true,
                TxnRef = Assert.IsType<EFTGetLastTransactionRequest>(request).TxnRef,
                TxnType = TransactionType.PurchaseCash,
                AmtPurchase = 10m,
                ResponseCode = "00",
                ResponseText = "APPROVED"
            }
        };
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        var purchaseRequest = Assert.IsType<EFTTransactionRequest>(purchaseClient.LastRequest);
        var recoveryRequest = Assert.IsType<EFTGetLastTransactionRequest>(getLastClient.LastRequest);
        Assert.True(result.Approved);
        Assert.Equal(purchaseRequest.TxnRef, recoveryRequest.TxnRef);
        Assert.Matches("^P[0-9A-HJKMNP-TV-Z]{15}$", purchaseRequest.TxnRef);
        Assert.Equal("P", result.TxnType);
    }

    [Fact]
    public async Task PurchaseWithReferenceAsync_requires_exact_transaction_reference_without_prefix_matching()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnRef = requestedReference + "X",
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseWithReferenceAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            requestedReference);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task PurchaseWithReferenceAsync_accepts_protocol_space_padding_but_compares_the_trimmed_reference_exactly()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnRef = $"  {requestedReference}  ",
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseWithReferenceAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
    }

    [Fact]
    public async Task VoidAsync_generates_a_transmittable_reference_without_P_or_R_identity_semantics()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.VoidAsync(10m, CreateSession(), CreateSettings(), null);

        var request = Assert.IsType<EFTTransactionRequest>(eftClient.LastRequest);
        Assert.True(result.Approved);
        Assert.Equal(TransactionType.Void, request.TxnType);
        Assert.Matches("^V[0-9A-F]{15}$", request.TxnRef);
        Assert.Null(result.TxnType);
    }

    [Fact]
    public async Task PurchaseAsync_maps_pinpad_offline_rejection_to_definitive_failure()
    {
        // 生产实测形态：EFT-Client 连不上 PIN pad 时回 PF、16 个空格的 TxnRef、金额 0。
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = new string(' ', 16),
            AmtPurchase = 0m,
            ResponseCode = "PF",
            ResponseText = "PINpad Offline      "
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.local.pinpadOffline", result.StatusKey);
        Assert.Equal("PF", result.ResponseCode);
        Assert.Equal("PINpad Offline", result.ResponseText);
        Assert.Equal("P", result.TxnType);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(0, true)]
    public async Task PurchaseAsync_keeps_pf_responses_with_transaction_evidence_unknown(int amount, bool echoReference)
    {
        // 带回显引用或金额的 PF 可能是刷卡中途断线，不能当作确定未提交。
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = new string(' ', 16),
            TxnType = TransactionType.Refund,
            AmtPurchase = amount,
            ResponseCode = "PF",
            ResponseText = "PINpad Offline"
        })
        {
            EchoTransactionReference = echoReference
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.NotEqual("linkly.local.pinpadOffline", result.StatusKey);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_pf_response_after_receipt_unknown()
    {
        var eftClient = new FakeLinklyEftClient(
            new EFTReceiptResponse { Type = ReceiptType.Customer, ReceiptText = ["CUSTOMER COPY"] },
            new EFTTransactionResponse
            {
                Success = false,
                TxnRef = new string(' ', 16),
                AmtPurchase = 0m,
                ResponseCode = "PF",
                ResponseText = "PINpad Offline"
            });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_keeps_pinpad_offline_unknown_with_offline_message()
    {
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = false,
            LastTransactionSuccess = false,
            TxnRef = new string(' ', 16),
            AmtPurchase = 0m,
            ResponseCode = "PF",
            ResponseText = "PINpad Offline      "
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), "P123456789012345");

        // 上一笔仍无法核对，必须保持未知；只是提示要说明刷卡机离线。
        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Contains("offline", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PurchaseAsync_marks_transaction_type_and_amount_conflicts_as_unknown()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnType = TransactionType.Refund,
            AmtPurchase = 9m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task PurchaseAsync_marks_a_transaction_type_conflict_as_unknown_even_when_amount_matches()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnType = TransactionType.Refund,
            AmtPurchase = 10m,
            ResponseCode = "00"
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task PurchaseAsync_marks_an_approved_amount_conflict_as_unknown_even_when_type_matches()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 9m,
            ResponseCode = "00"
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_marks_transaction_type_and_amount_conflicts_as_unknown()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = requestedReference,
            TxnType = TransactionType.Refund,
            AmtPurchase = 9m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_marks_an_explicit_decline_with_a_wrong_reference_as_unknown()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = false,
            TxnRef = "P123456789012346",
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 0m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_marks_an_explicit_decline_with_a_wrong_type_as_unknown()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = false,
            TxnRef = requestedReference,
            TxnType = TransactionType.Refund,
            AmtPurchase = 0m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_marks_an_explicit_decline_with_a_nonzero_wrong_amount_as_unknown()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = false,
            TxnRef = requestedReference,
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 9m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_returns_a_known_explicit_decline_with_correct_identity_and_zero_amount()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = false,
            TxnRef = requestedReference,
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 0m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Contains("DECLINED", result.Message);
    }

    [Fact]
    public async Task Declined_transaction_with_a_nonzero_mismatched_amount_is_unknown()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = requestedReference,
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 9m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseWithReferenceAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task Declined_transaction_with_zero_amount_is_a_known_decline()
    {
        const string requestedReference = "P123456789012345";
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = requestedReference,
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 0m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseWithReferenceAsync(10m, CreateSession(), CreateSettings(), requestedReference);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Contains("DECLINED", result.Message);
    }

    [Fact]
    public async Task Invalid_purchase_and_refund_historical_references_fail_before_factory_creation()
    {
        const string invalidReference = "12345678901234567";
        var purchaseFactory = new FakeLinklyEftClientFactory(new FakeLinklyEftClient());
        var purchaseClient = new LinklyTerminalClient(purchaseFactory);
        var purchaseResult = await purchaseClient.PurchaseWithReferenceAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            invalidReference);

        var refundFactory = new FakeLinklyEftClientFactory(new FakeLinklyEftClient());
        var refundClient = new LinklyTerminalClient(refundFactory);
        var refundResult = await refundClient.RefundWithReferenceAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "ANZ:ORIGINAL-SALE-001",
            invalidReference);

        Assert.False(purchaseResult.Approved);
        Assert.False(refundResult.Approved);
        Assert.Equal(0, purchaseFactory.CreatedCount);
        Assert.Equal(0, refundFactory.CreatedCount);
    }

    [Fact]
    public async Task PurchaseAsync_fails_closed_when_connection_fails()
    {
        using var logs = new ConsoleLogCapture();
        var eftClient = new FakeLinklyEftClient { ConnectResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("connection failed", result.Message, StringComparison.OrdinalIgnoreCase);
        var events = logs.ReadJsonEvents("LinklyLocal");
        var connectEvent = AssertEvent(events, "connect", "failed", "response");
        Assert.True(connectEvent.TryGetProperty("request", out _));
        Assert.True(connectEvent.TryGetProperty("response", out var responseJson));
        Assert.False(responseJson.GetProperty("connected").GetBoolean());
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task PurchaseAsync_fails_closed_when_request_cannot_be_sent()
    {
        var eftClient = new FakeLinklyEftClient { WriteResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("could not be sent", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task PurchaseAsync_fails_closed_for_declined_response()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = "TXN-DECLINE",
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 10m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        })
        {
            EchoTransactionReference = true
        };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Matches("^ANZ:P[0-9A-HJKMNP-TV-Z]{15}$", result.Reference);
        Assert.Contains("DECLINED", result.Message);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task PurchaseWithReferenceAsync_uses_supplied_txn_ref_and_closes_socket()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnRef = "LOCAL-TXN-001",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.PurchaseWithReferenceAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "LOCAL-TXN-001");

        Assert.True(result.Approved);
        Assert.Equal("ANZ:LOCAL-TXN-001", result.Reference);
        var request = Assert.IsType<EFTTransactionRequest>(eftClient.LastRequest);
        Assert.Equal("LOCAL-TXN-001", request.TxnRef);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_result_unknown_when_get_last_transaction_reference_does_not_match_after_timeout()
    {
        var purchaseClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var getLastClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.IsType<EFTGetLastTransactionRequest>(getLastClient.LastRequest);
        Assert.Equal(1, purchaseClient.DisconnectCallCount);
        Assert.True(purchaseClient.Disposed);
        Assert.Equal(1, getLastClient.DisconnectCallCount);
        Assert.True(getLastClient.Disposed);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_sends_get_last_request_and_closes_socket()
    {
        var eftClient = new FakeLinklyEftClient(
            new EFTReceiptResponse
            {
                ReceiptText = ["MERCHANT COPY", "APPROVED"]
            },
            new EFTGetLastTransactionResponse
            {
                Success = true,
                LastTransactionSuccess = true,
                TxnRef = "LOCAL-TXN-001",
                AmtPurchase = 10m,
                ResponseCode = "00",
                ResponseText = "APPROVED"
            });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "LOCAL-TXN-001");

        Assert.True(result.Approved);
        Assert.Equal("ANZ:LOCAL-TXN-001", result.Reference);
        Assert.IsType<EFTGetLastTransactionRequest>(eftClient.LastRequest);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task PurchaseAsync_sends_cancel_key_after_caller_cancels_and_returns_decline()
    {
        using var cts = new CancellationTokenSource();
        var readCount = 0;
        var purchaseClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = false,
            TxnRef = "TERM12605260000000",
            TxnType = TransactionType.PurchaseCash,
            AmtPurchase = 10m,
            ResponseCode = "C0",
            ResponseText = "CANCELLED"
        })
        {
            EchoTransactionReference = true,
            OnRead = () =>
            {
                if (readCount++ == 0)
                {
                    cts.Cancel();
                }
            }
        };
        purchaseClient.ReadExceptions.Enqueue(new OperationCanceledException(cts.Token));
        var factory = new QueueLinklyEftClientFactory(purchaseClient);
        var client = new LinklyTerminalClient(factory);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cts.Token);

        Assert.False(result.Approved);
        Assert.Contains("CANCELLED", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.CreatedCount);
        Assert.IsType<EFTTransactionRequest>(purchaseClient.Requests[0]);
        var cancelRequest = Assert.IsType<EFTSendKeyRequest>(purchaseClient.Requests[1]);
        Assert.Equal(EFTPOSKey.OkCancel, cancelRequest.Key);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_result_unknown_when_cancel_recovery_reference_does_not_match()
    {
        using var logs = new ConsoleLogCapture();
        using var cts = new CancellationTokenSource();
        var readCount = 0;
        var purchaseClient = new FakeLinklyEftClient
        {
            CancelRequestResult = false,
            OnRead = () =>
            {
                if (readCount++ == 0)
                {
                    cts.Cancel();
                }
            }
        };
        purchaseClient.ReadExceptions.Enqueue(new OperationCanceledException(cts.Token));
        var getLastClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cts.Token);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.IsType<EFTSendKeyRequest>(purchaseClient.Requests[1]);
        Assert.IsType<EFTGetLastTransactionRequest>(getLastClient.LastRequest);
        Assert.Equal(1, purchaseClient.DisconnectCallCount);
        Assert.True(purchaseClient.Disposed);
        Assert.Equal(1, getLastClient.DisconnectCallCount);
        Assert.True(getLastClient.Disposed);
        var events = logs.ReadJsonEvents("LinklyLocal");
        var cancelRequestEvent = AssertEvent(events, "cancel", "sent", "request");
        Assert.True(cancelRequestEvent.GetProperty("request").TryGetProperty("key", out _));
        var cancelFailedEvent = AssertEvent(events, "cancel", "failed", "response");
        Assert.Equal("send-cancel-failed", cancelFailedEvent.GetProperty("reason").GetString());
        var recoveryRequestEvent = AssertEvent(events, "get-last-transaction", "sent", "request");
        Assert.Matches("^P[0-9A-HJKMNP-TV-Z]{15}$", recoveryRequestEvent.GetProperty("request").GetProperty("txnRef").GetString());
        var recoveryResponseEvent = AssertEvent(events, "get-last-transaction", "received", "response");
        Assert.True(recoveryResponseEvent.TryGetProperty("response", out var recoveryResponse));
        Assert.Equal("TERM12605260000000", recoveryResponse.GetProperty("txnRef").GetString());
        Assert.Equal("00", recoveryResponse.GetProperty("responseCode").GetString());
        Assert.True(recoveryResponseEvent.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task PurchaseAsync_keeps_result_unknown_when_exception_recovery_reference_does_not_match()
    {
        using var logs = new ConsoleLogCapture();
        var purchaseClient = new FakeLinklyEftClient();
        purchaseClient.ReadExceptions.Enqueue(new InvalidOperationException("Linkly parser failed."));
        var getLastClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "TERM12605260000000",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.IsType<EFTGetLastTransactionRequest>(getLastClient.LastRequest);
        Assert.Equal(1, purchaseClient.DisconnectCallCount);
        Assert.True(purchaseClient.Disposed);
        Assert.Equal(1, getLastClient.DisconnectCallCount);
        Assert.True(getLastClient.Disposed);
        var events = logs.ReadJsonEvents("LinklyLocal");
        var failureEvent = AssertEvent(events, "transaction", "failed", "response");
        Assert.True(failureEvent.TryGetProperty("request", out var failedRequest));
        Assert.True(failedRequest.TryGetProperty("txnType", out _));
        Assert.Equal("InvalidOperationException", failureEvent.GetProperty("reason").GetString());
        Assert.Equal("Linkly parser failed.", failureEvent.GetProperty("details").GetProperty("message").GetString());
        var recoveryEvent = AssertEvent(events, "get-last-transaction", "received", "response");
        Assert.True(recoveryEvent.TryGetProperty("response", out var recoveryResponse));
        Assert.Equal("TERM12605260000000", recoveryResponse.GetProperty("txnRef").GetString());
    }

    [Fact]
    public async Task PurchaseAsync_marks_result_unknown_when_get_last_transaction_connect_throws_after_submission()
    {
        var purchaseClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var getLastClient = new FakeLinklyEftClient
        {
            ConnectException = new NullReferenceException("Third-party Linkly connect failed.")
        };
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("ANZ Linkly transaction timed out.", result.Message);
        Assert.Equal(1, purchaseClient.DisconnectCallCount);
        Assert.True(purchaseClient.Disposed);
        Assert.Equal(1, getLastClient.DisconnectCallCount);
        Assert.True(getLastClient.Disposed);
    }

    [Fact]
    public async Task PurchaseAsync_fails_when_get_last_transaction_is_not_successful()
    {
        var purchaseClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var getLastClient = new FakeLinklyEftClient
        {
            ResponseFactory = request => new EFTGetLastTransactionResponse
            {
                Success = true,
                LastTransactionSuccess = false,
                TxnRef = Assert.IsType<EFTGetLastTransactionRequest>(request).TxnRef,
                TxnType = TransactionType.PurchaseCash,
                AmtPurchase = 10m,
                ResponseCode = "05",
                ResponseText = "DECLINED"
            }
        };
        var client = new LinklyTerminalClient(new QueueLinklyEftClientFactory(purchaseClient, getLastClient));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("DECLINED", result.Message);
        Assert.Equal(1, purchaseClient.DisconnectCallCount);
        Assert.True(purchaseClient.Disposed);
        Assert.Equal(1, getLastClient.DisconnectCallCount);
        Assert.True(getLastClient.Disposed);
    }

    [Fact]
    public async Task TestConnectionAsync_uses_supplied_timeout_instead_of_linkly_business_wait()
    {
        // 中文注释：本用例只需区分"按传入的 30ms 超时结束"与"退回 BusinessWait（180 秒）"两种行为。
        // 不能用固定墙钟窗口（如 Task.Delay(120) 后检查 IsCompleted）去赛跑：CancelAfter 回调依赖线程池，
        // 而测试的 await 续体跑在 xUnit 同步上下文自有线程上，CI 线程池饥饿时测试会先醒来误报。
        // 改为等待任务完成：等待预算远小于 BusinessWait，两种行为之间留有确定的间隔，与调度抖动无关。
        Assert.True(
            LinklyTimeoutPolicy.BusinessWait >= AsyncTestWaitSupport.DefaultTimeout * 2,
            "等待预算必须明显小于 Linkly 业务等待，否则本用例无法区分是否使用了传入的超时。");
        using var callerCts = new CancellationTokenSource();
        var eftClient = new FakeLinklyEftClient { WaitForCancellationOnConnect = true };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var resultTask = client.TestConnectionAsync(
            "127.0.0.1",
            2011,
            TimeSpan.FromMilliseconds(30),
            callerCts.Token);
        try
        {
            var result = await resultTask.WaitUntilCompletedAsync(
                () => "Connection test ignored the supplied timeout and kept waiting for the Linkly business wait.");
            Assert.False(result.Succeeded);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // 失败路径也取消调用方令牌，不把挂起的连接测试带到后续用例。
            callerCts.Cancel();
        }
    }

    [Fact]
    public async Task TestConnectionAsync_succeeds_only_when_pinpad_is_online_and_logged_on()
    {
        var eftClient = new FakeLinklyEftClient(new EFTStatusResponse
        {
            Success = true,
            LoggedOn = true,
            ResponseCode = "00",
            ResponseText = "APPROVED",
            PinPadVersion = "1.2.3"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.True(result.Succeeded);
        Assert.True(result.PinPadLoggedOn);
        var request = Assert.IsType<EFTStatusRequest>(eftClient.LastRequest);
        Assert.Equal(TerminalApplication.EFTPOS, request.Application);
        Assert.Equal("00", request.Merchant);
        Assert.Equal(StatusType.Standard, request.StatusType);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task LogonAsync_sends_standard_request_and_accepts_success_after_receipt_and_display()
    {
        const string receiptText = "SECRET LOGON RECEIPT";
        const string displayText = "SECRET DISPLAY TEXT";
        using var logs = new ConsoleLogCapture();
        var eftClient = new FakeLinklyEftClient(
            new EFTReceiptResponse
            {
                Type = ReceiptType.Logon,
                ReceiptText = [receiptText]
            },
            new EFTDisplayResponse
            {
                NumberOfLines = 1,
                DisplayText = [displayText]
            },
            new EFTLogonResponse
            {
                Success = true,
                ResponseCode = "00",
                ResponseText = "APPROVED"
            });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.LogonAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.True(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal("00", result.ResponseCode);
        Assert.Contains(receiptText, Assert.Single(result.ReceiptTexts!));
        var request = Assert.IsType<EFTLogonRequest>(eftClient.LastRequest);
        Assert.Equal("00", request.Merchant);
        Assert.Equal(LogonType.Standard, request.LogonType);
        Assert.Equal(TerminalApplication.EFTPOS, request.Application);
        Assert.Equal(ReceiptPrintModeType.POSPrinter, request.ReceiptAutoPrint);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);

        var logPayload = string.Join(
            Environment.NewLine,
            logs.ReadJsonEvents("LinklyLocal").Select(element => element.GetRawText()));
        Assert.DoesNotContain(receiptText, logPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(displayText, logPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogonAsync_rejects_response_when_response_code_is_not_00()
    {
        var eftClient = new FakeLinklyEftClient(new EFTLogonResponse
        {
            Success = true,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.LogonAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal("05", result.ResponseCode);
        Assert.Contains("DECLINED", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogonAsync_returns_unknown_when_response_times_out_after_request_is_sent()
    {
        var eftClient = new FakeLinklyEftClient { WaitForCancellationOnRead = true };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.LogonAsync("127.0.0.1", 2011, TimeSpan.FromMilliseconds(50));

        Assert.False(result.Succeeded);
        Assert.True(result.ResultUnknown);
        Assert.IsType<EFTLogonRequest>(eftClient.LastRequest);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
    }

    [Fact]
    public async Task LogonAsync_returns_unknown_when_connection_drops_after_request_is_sent()
    {
        var eftClient = new FakeLinklyEftClient();
        eftClient.ReadExceptions.Enqueue(new ConnectionException("Connection dropped."));
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.LogonAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.True(result.ResultUnknown);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
    }

    [Fact]
    public async Task LogonAsync_attempts_cancel_when_caller_cancels_after_request_is_sent()
    {
        var eftClient = new FakeLinklyEftClient { WaitForCancellationOnRead = true };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await client.LogonAsync(
            "127.0.0.1",
            2011,
            TimeSpan.FromSeconds(5),
            cancellationTokenSource.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.ResultUnknown);
        Assert.Collection(
            eftClient.Requests,
            request => Assert.IsType<EFTLogonRequest>(request),
            request => Assert.IsType<EFTSendKeyRequest>(request));
        Assert.Equal(1, eftClient.DisconnectCallCount);
    }

    [Fact]
    public async Task LogonAsync_returns_unknown_for_empty_response_after_request_is_sent()
    {
        var eftClient = new FakeLinklyEftClient { ReturnNullOnRead = true };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.LogonAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.True(result.ResultUnknown);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogonAsync_returns_retryable_failure_when_request_cannot_be_sent()
    {
        var eftClient = new FakeLinklyEftClient { WriteResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.LogonAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Contains("could not be sent", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
    }

    [Fact]
    public async Task SettlementAsync_sends_settlement_request_and_collects_settlement_receipt()
    {
        var eftClient = new FakeLinklyEftClient(
            new EFTReceiptResponse
            {
                Type = ReceiptType.Settlement,
                ReceiptText = ["SETTLEMENT", "TOTAL $10.00"]
            },
            new EFTSettlementResponse
            {
                Success = true,
                ResponseCode = "00",
                ResponseText = "APPROVED",
                SettlementData = "TOTAL=10.00"
            });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.True(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.Submitted, result.ProviderSubmissionState);
        Assert.Equal("00", result.ResponseCode);
        Assert.Equal("TOTAL=10.00", result.SettlementData);
        Assert.Contains("SETTLEMENT", Assert.Single(result.ReceiptTexts!));
        var request = Assert.IsType<EFTSettlementRequest>(eftClient.LastRequest);
        Assert.Equal(SettlementType.Settlement, request.SettlementType);
        Assert.Equal("00", request.Merchant);
        Assert.Equal(TerminalApplication.EFTPOS, request.Application);
        Assert.Equal(ReceiptPrintModeType.POSPrinter, request.ReceiptAutoPrint);
        Assert.Equal(1, eftClient.DisconnectCallCount);
    }

    [Fact]
    public async Task SettlementAsync_connection_failure_explains_settlement_was_not_sent()
    {
        var eftClient = new FakeLinklyEftClient { ConnectResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.NotSubmitted, result.ProviderSubmissionState);
        Assert.Equal(
            "Could not connect to the Linkly EFT-Client on this POS, so the settlement was not sent. Make sure the Linkly program is open, then try again.",
            result.Message);
        Assert.Empty(eftClient.Requests);
    }

    [Fact]
    public async Task SettlementAsync_returns_unknown_after_request_is_sent_and_response_fails()
    {
        var eftClient = new FakeLinklyEftClient { ThrowOnRead = true };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.True(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.Unknown, result.ProviderSubmissionState);
        Assert.IsType<EFTSettlementRequest>(eftClient.LastRequest);
        Assert.Single(eftClient.Requests);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_returns_unknown_when_get_last_envelope_is_not_successful()
    {
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = false,
            LastTransactionSuccess = false,
            TxnRef = "LOCAL-TXN-001",
            AmtPurchase = 10m,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), "LOCAL-TXN-001");

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_returns_unknown_when_approved_result_lacks_amount()
    {
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "LOCAL-TXN-001",
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), "LOCAL-TXN-001");

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Null(result.AuthorizedAmount);
    }

    [Fact]
    public async Task RecoverLastTransactionAsync_returns_unknown_when_transaction_reference_does_not_match_attempt()
    {
        var eftClient = new FakeLinklyEftClient(new EFTGetLastTransactionResponse
        {
            Success = true,
            LastTransactionSuccess = true,
            TxnRef = "OTHER-TXN-001",
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RecoverLastTransactionAsync(10m, CreateSession(), CreateSettings(), "LOCAL-TXN-001");

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RefundWithReferenceAsync_uses_persisted_refund_txn_ref_in_terminal_request()
    {
        var eftClient = new FakeLinklyEftClient(new EFTTransactionResponse
        {
            Success = true,
            TxnRef = "REFUND-TXN-001",
            TxnType = TransactionType.Refund,
            AmtPurchase = 10m,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.RefundWithReferenceAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "ANZ:ORIGINAL-SALE-001",
            "REFUND-TXN-001");

        Assert.True(result.Approved);
        var request = Assert.IsType<EFTTransactionRequest>(eftClient.LastRequest);
        Assert.Equal(TransactionType.Refund, request.TxnType);
        Assert.Equal("REFUND-TXN-001", request.TxnRef);
    }

    [Fact]
    public async Task TestConnectionAsync_fails_when_pinpad_is_offline()
    {
        var eftClient = new FakeLinklyEftClient(new EFTStatusResponse
        {
            Success = false,
            LoggedOn = false,
            ResponseCode = "PF",
            ResponseText = "PINpad Offline"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Contains("offline (PF)", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task TestConnectionAsync_explicit_offline_response_overrides_stale_logged_on_flag()
    {
        var eftClient = new FakeLinklyEftClient(new EFTStatusResponse
        {
            Success = true,
            LoggedOn = true,
            ResponseCode = "PF",
            ResponseText = "PINpad Offline"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Null(result.PinPadLoggedOn);
        Assert.Contains("offline (PF)", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnectionAsync_succeeds_with_warning_when_pinpad_is_online_but_not_logged_on()
    {
        var eftClient = new FakeLinklyEftClient(new EFTStatusResponse
        {
            Success = true,
            LoggedOn = false,
            ResponseCode = "00",
            ResponseText = "APPROVED"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.True(result.Succeeded);
        Assert.False(result.PinPadLoggedOn);
        Assert.Contains("not logged on", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnectionAsync_reports_status_failure_response()
    {
        var eftClient = new FakeLinklyEftClient(new EFTStatusResponse
        {
            Success = false,
            ResponseCode = "XX",
            ResponseText = "NOT READY"
        });
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Contains("NOT READY (XX)", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnectionAsync_fails_when_status_request_cannot_be_sent()
    {
        var eftClient = new FakeLinklyEftClient { WriteResult = false };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Contains("could not be sent", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task TestConnectionAsync_fails_when_status_response_is_empty()
    {
        var eftClient = new FakeLinklyEftClient { ReturnNullOnRead = true };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync("127.0.0.1", 2011, TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Contains("no status response", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    [Fact]
    public async Task TestConnectionAsync_uses_supplied_timeout_while_waiting_for_pinpad_status()
    {
        var eftClient = new FakeLinklyEftClient { WaitForCancellationOnRead = true };
        var client = new LinklyTerminalClient(new FakeLinklyEftClientFactory(eftClient));

        var result = await client.TestConnectionAsync(
            "127.0.0.1",
            2011,
            TimeSpan.FromMilliseconds(30));

        Assert.False(result.Succeeded);
        Assert.Contains("readiness check timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, eftClient.DisconnectCallCount);
        Assert.True(eftClient.Disposed);
    }

    private static PosSessionState CreateSession(string deviceCode = "TERM-1")
    {
        return new PosSessionState(
            "HB POS",
            "1001",
            "Main",
            deviceCode,
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSettings()
    {
        return new CardTerminalSettings(
            CardProcessorKind.Linkly,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            null,
            null,
            null,
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(10));
    }

    private sealed class FakeLinklyEftClientFactory(ILinklyEftClient client) : ILinklyEftClientFactory
    {
        public int CreatedCount { get; private set; }

        public ILinklyEftClient Create()
        {
            CreatedCount++;
            return client;
        }
    }

    private sealed class QueueLinklyEftClientFactory(params ILinklyEftClient[] clients) : ILinklyEftClientFactory
    {
        private readonly Queue<ILinklyEftClient> _clients = new(clients);

        public int CreatedCount { get; private set; }

        public ILinklyEftClient Create()
        {
            CreatedCount++;
            return _clients.Dequeue();
        }
    }

    private sealed class FakeLinklyEftClient(params EFTResponse[] responses) : ILinklyEftClient
    {
        private readonly Queue<EFTResponse> _responses = new(responses);
        private EFTTransactionRequest? _lastTransactionRequest;

        public EFTRequest? LastRequest { get; private set; }

        public List<EFTRequest> Requests { get; } = [];

        public Queue<Exception> ReadExceptions { get; } = new();

        public bool ConnectResult { get; init; } = true;

        public bool WriteResult { get; init; } = true;

        public bool CancelRequestResult { get; init; } = true;

        public bool ThrowOnRead { get; init; }

        public Exception? ConnectException { get; init; }

        public Action? OnRead { get; init; }

        public bool WaitForCancellationOnConnect { get; init; }

        public bool WaitForCancellationOnRead { get; init; }

        public bool ReturnNullOnRead { get; init; }

        public bool EchoTransactionReference { get; init; }

        public Func<EFTRequest?, EFTResponse?>? ResponseFactory { get; init; }

        public int DisconnectCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public bool Disposed => DisposeCallCount > 0;

        public async Task<bool> ConnectAsync(string hostName, int hostPort, bool useSsl, bool useKeepAlive)
        {
            if (ConnectException is not null)
            {
                throw ConnectException;
            }

            if (WaitForCancellationOnConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }

            return ConnectResult;
        }

        public Task<bool> WriteRequestAsync(EFTRequest request)
        {
            LastRequest = request;
            if (request is EFTTransactionRequest transactionRequest)
            {
                _lastTransactionRequest = transactionRequest;
            }

            Requests.Add(request);
            return Task.FromResult(WriteResult);
        }

        public Task<bool> SendCancelRequestAsync()
        {
            var request = new EFTSendKeyRequest { Key = EFTPOSKey.OkCancel };
            LastRequest = request;
            Requests.Add(request);
            return Task.FromResult(CancelRequestResult);
        }

        public async Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken)
        {
            OnRead?.Invoke();
            if (WaitForCancellationOnRead)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (ReturnNullOnRead)
            {
                return null;
            }

            if (ReadExceptions.Count > 0)
            {
                throw ReadExceptions.Dequeue();
            }

            if (ThrowOnRead)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : ResponseFactory?.Invoke(LastRequest) ?? throw new InvalidOperationException("No fake Linkly response configured.");
            if (EchoTransactionReference && response is EFTTransactionResponse transaction && _lastTransactionRequest is not null)
            {
                transaction.TxnRef = _lastTransactionRequest.TxnRef;
            }

            return response;
        }

        public bool Disconnect()
        {
            DisconnectCallCount++;
            return true;
        }

        public void Dispose()
        {
            DisposeCallCount++;
        }
    }

    private static JsonElement AssertEvent(
        IReadOnlyList<JsonElement> events,
        string operation,
        string phase,
        string direction)
    {
        var match = events.FirstOrDefault(element =>
            string.Equals(element.GetProperty("operation").GetString(), operation, StringComparison.Ordinal) &&
            string.Equals(element.GetProperty("phase").GetString(), phase, StringComparison.Ordinal) &&
            string.Equals(element.GetProperty("direction").GetString(), direction, StringComparison.Ordinal));
        Assert.True(match.ValueKind != JsonValueKind.Undefined, $"Missing log event {operation}/{phase}/{direction}.");
        return match;
    }

    private static JsonElement ParseJsonPayload(string line)
    {
        var jsonStart = line.IndexOf('{', StringComparison.Ordinal);
        Assert.True(jsonStart >= 0, $"Expected JSON payload in line: {line}");
        using var document = JsonDocument.Parse(line[jsonStart..]);
        return document.RootElement.Clone();
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        public IReadOnlyList<JsonElement> ReadJsonEvents(string category)
        {
            lock (_lines)
            {
                return _lines
                    .Where(line => line.Contains($"[HBPOS][Client][{category}]", StringComparison.Ordinal))
                    .Select(ParseJsonPayload)
                    .ToArray();
            }
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
