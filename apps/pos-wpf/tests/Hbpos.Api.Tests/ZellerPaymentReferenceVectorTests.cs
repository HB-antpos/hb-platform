using System.Text.Json;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

/// <summary>
/// 跨端黄金向量：与 packages/pos-payments-core 的 zeller-payment-reference.test.ts 共用
/// test-fixtures/zeller/payment-reference-vectors.json。iPad / 手持用 TS 移植版生成和解析 Zeller 引用，
/// 服务端和 WPF 用本类，两端任何一边改动格式都会在这里或 TS 侧失败。
/// </summary>
public sealed class ZellerPaymentReferenceVectorTests
{
    private static JsonElement LoadVectors()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "test-fixtures", "zeller", "payment-reference-vectors.json");
            if (File.Exists(candidate))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                return document.RootElement.Clone();
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "未找到 test-fixtures/zeller/payment-reference-vectors.json：" + AppContext.BaseDirectory);
    }

    private static JsonElement[] Cases(string section, int atLeast)
    {
        var cases = LoadVectors().GetProperty(section).EnumerateArray().ToArray();
        Assert.True(cases.Length >= atLeast, section);
        return cases;
    }

    private static string Format(string kind, string reference, string? transactionUuid) => kind switch
    {
        "payment" => ZellerPaymentReference.FormatPayment(reference, transactionUuid ?? string.Empty),
        "refund" => ZellerPaymentReference.FormatRefund(reference, transactionUuid ?? string.Empty),
        "unverifiedPayment" => ZellerPaymentReference.FormatUnverifiedPayment(reference),
        "unverifiedRefund" => ZellerPaymentReference.FormatUnverifiedRefund(reference),
        _ => throw new InvalidOperationException("向量里出现了不认识的 kind：" + kind)
    };

    [Fact]
    public void Attempt_references_match_the_shared_vectors()
    {
        foreach (var item in Cases("attempts", 4))
        {
            var attempt = Guid.Parse(item.GetProperty("attemptGuid").GetString()!);

            Assert.Equal(item.GetProperty("purchaseReference").GetString(), ZellerPaymentReference.CreatePurchaseReference(attempt));
            Assert.Equal(item.GetProperty("refundReference").GetString(), ZellerPaymentReference.CreateRefundReference(attempt));
        }
    }

    [Fact]
    public void Formatting_matches_the_shared_vectors()
    {
        foreach (var item in Cases("format", 5))
        {
            Assert.Equal(
                item.GetProperty("formatted").GetString(),
                Format(
                    item.GetProperty("kind").GetString()!,
                    item.GetProperty("reference").GetString()!,
                    item.GetProperty("transactionUuid").GetString()));
        }
    }

    [Fact]
    public void Formatting_rejects_what_the_shared_vectors_reject()
    {
        foreach (var item in Cases("formatRejects", 10))
        {
            Assert.Throws<ArgumentException>(() => Format(
                item.GetProperty("kind").GetString()!,
                item.GetProperty("reference").GetString()!,
                item.GetProperty("transactionUuid").GetString()));
        }
    }

    [Fact]
    public void Parsing_matches_the_shared_vectors()
    {
        foreach (var item in Cases("parse", 15))
        {
            var input = item.GetProperty("input").GetString();
            var expected = item.GetProperty("parts");
            var parsed = item.GetProperty("kind").GetString() == "payment"
                ? ZellerPaymentReference.TryParsePayment(input, out var parts)
                : ZellerPaymentReference.TryParseRefund(input, out parts);

            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.False(parsed, input);
                continue;
            }

            Assert.True(parsed, input);
            Assert.Equal(expected.GetProperty("reference").GetString(), parts.Reference);
            Assert.Equal(expected.GetProperty("transactionUuid").GetString(), parts.TransactionUuid);
        }
    }

    [Fact]
    public void Prefix_detection_matches_the_shared_vectors()
    {
        foreach (var item in Cases("isZeller", 10))
        {
            var input = item.GetProperty("input").GetString();
            Assert.True(item.GetProperty("expected").GetBoolean() == ZellerPaymentReference.IsZeller(input), input);
        }

        foreach (var item in Cases("isUnverified", 8))
        {
            var input = item.GetProperty("input").GetString();
            Assert.True(item.GetProperty("expected").GetBoolean() == ZellerPaymentReference.IsUnverified(input), input);
        }
    }
}
