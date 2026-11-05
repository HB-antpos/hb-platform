using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ClientLogSanitizerTests
{
    [Fact]
    public void Serialize_removes_query_from_relative_request_path_before_outbox_persistence()
    {
        var json = ClientLogSanitizer.Serialize(new
        {
            RequestPath = "/api/v1/payments?customer=alice&token=relative-secret"
        });

        Assert.Contains("\"requestPath\":\"/api/v1/payments\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("customer=alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("relative-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("?", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_preserves_numeric_product_identifier_but_redacts_pan_in_free_text()
    {
        var json = ClientLogSanitizer.Serialize(new
        {
            ProductCode = "9300675072651",
            ItemNumber = "1234567890123",
            SafeMessage = "terminal returned card 4111111111111111"
        });

        Assert.Contains("9300675072651", json, StringComparison.Ordinal);
        Assert.Contains("1234567890123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_redacts_sensitive_keys_bearer_values_and_url_queries()
    {
        var payload = new
        {
            Authorization = "Bearer top-secret-token",
            Url = "https://api.example.com/pay?token=query-secret&customer=alice",
            Message = "request failed Authorization: Bearer inline-secret card 4111 1111 1111 1111 voucherCode=VOUCHER-SECRET employeeBarcode=EMP-999",
            Safe = "order uploaded",
            Nested = new Dictionary<string, object?>
            {
                ["password"] = "password-secret",
                ["apiKey"] = "api-key-secret",
                ["squareAccessToken"] = "square-token-secret",
                ["requestBody"] = "full-request-secret",
                ["customerName"] = "Customer PII",
                ["productCode"] = "P001"
            }
        };

        var json = ClientLogSanitizer.Serialize(payload);

        Assert.DoesNotContain("top-secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("inline-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("square-token-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("full-request-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer PII", json, StringComparison.Ordinal);
        Assert.DoesNotContain("4111 1111 1111 1111", json, StringComparison.Ordinal);
        Assert.DoesNotContain("VOUCHER-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EMP-999", json, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com/pay", json, StringComparison.Ordinal);
        Assert.Contains("order uploaded", json, StringComparison.Ordinal);
        Assert.Contains("P001", json, StringComparison.Ordinal);
    }

    [Theory]
    // 真机日志里被误伤的尝试编号（中间恰好连成 13 位以上数字的 GUID）。
    [InlineData("attempt 15b5f123-4567-8901-23f7-1f3cdc97cc6a: call ended as Error Cancelled")]
    // 全是数字的 GUID 段。
    [InlineData("order 12345678-1234-1234-1234-123456789012 uploaded")]
    // 发给 Zeller 的 reference（HBZ- 加 32 位十六进制）。
    [InlineData("attempt created, reference HBZ-15b5f12345678901234567f71f3cdc97, amount 99 cents")]
    [InlineData("refund reference HBZR-a1234567890123456789012345678901 sent")]
    public void Serialize_keeps_guids_and_zeller_references_that_happen_to_contain_long_digit_runs(string message)
    {
        var json = ClientLogSanitizer.Serialize(new { Message = message });

        Assert.DoesNotContain("REDACTED", json, StringComparison.Ordinal);
        Assert.Contains(message, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("card 4111111111111111 declined", "4111111111111111")]
    [InlineData("card:4111-1111-1111-1111.", "4111-1111-1111-1111")]
    [InlineData("(4111 1111 1111 1111)", "4111 1111 1111 1111")]
    [InlineData("card_4111111111111111", "4111111111111111")]
    public void Serialize_still_redacts_card_numbers_next_to_punctuation(string message, string pan)
    {
        var json = ClientLogSanitizer.Serialize(new { Message = message });

        Assert.DoesNotContain(pan, json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_CARD]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_redacts_bare_voucher_assignment_in_message_text()
    {
        var json = ClientLogSanitizer.Serialize(new
        {
            Message = "release failed voucher=HB-VOUCHER-7788 voucherTail=7788 error=HttpRequestException"
        });

        Assert.DoesNotContain("HB-VOUCHER-7788", json, StringComparison.Ordinal);
        // 只记后 4 位的 voucherTail 不是完整券号，必须保留以便排查。
        Assert.Contains("voucherTail=7788", json, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", json, StringComparison.Ordinal);
    }
}
