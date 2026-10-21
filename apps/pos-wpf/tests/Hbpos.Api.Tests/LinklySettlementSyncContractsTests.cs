using System.Text.Json;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Api.Tests;

public sealed class LinklySettlementSyncContractsTests
{
    private const string OfficialFixedWidthSettlement =
        "000000002138VISA                000000100001000000100001000000100001+000000300003" +
        "DEBIT               000000100001000000100001000000100001+000000300003" +
        "069TOTAL               000000300001000000300001000000300001+000000900009";

    [Fact]
    public void Request_exposes_the_complete_versioned_settlement_snapshot()
    {
        var properties = typeof(LinklySettlementSyncRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            [
                "SchemaVersion", "SettlementGuid", "StoreCode", "DeviceCode", "BusinessDate",
                "ConnectionMode", "Environment", "ProviderSessionId", "Status", "ResponseCode",
                "ResponseText", "SettlementData", "ReceiptTexts", "RequestedAt", "CompletedAt",
                "FirstPrintedAt", "LastPrintedAt", "PrintCount", "LastPrintError", "ClientRevision",
                "ProviderSubmissionState"
            ],
            properties);
    }

    [Fact]
    public void Provider_submission_state_uses_a_strict_string_wire_format()
    {
        Assert.Equal("\"Submitted\"", JsonSerializer.Serialize(ProviderSubmissionState.Submitted));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProviderSubmissionState>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProviderSubmissionState>("\"1\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProviderSubmissionState>("\" Submitted \""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProviderSubmissionState>("\"submitted\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((ProviderSubmissionState)99));
    }

    [Fact]
    public void Receipt_sanitizer_masks_pan_but_preserves_reference_lines()
    {
        Assert.Equal("CARD ****1111", LinklyReceiptTextSanitizer.Sanitize("CARD 4111111111111111"));
        Assert.Equal("RRN 123456789012", LinklyReceiptTextSanitizer.Sanitize("RRN 123456789012"));
        Assert.Equal(
            "RRN 123456789012 PAN ****1111",
            LinklyReceiptTextSanitizer.Sanitize("RRN 123456789012 PAN 4111111111111111"));
        Assert.Equal(
            "prefix RRN=234567890123 CardNumber=****1111",
            LinklyReceiptTextSanitizer.Sanitize(
                "prefix RRN=234567890123 CardNumber=4111111111111111"));
    }

    [Theory]
    [InlineData("prefix RRN 123456789012", "123456789012")]
    [InlineData("prefix txnRef: 234567890123", "234567890123")]
    [InlineData("prefix retrievalReference 345678901234", "345678901234")]
    [InlineData("prefix STAN 456789012345", "456789012345")]
    [InlineData("prefix trace no 567890123456", "567890123456")]
    [InlineData("prefix invoice no 678901234567", "678901234567")]
    [InlineData("prefix batch number 789012345678", "789012345678")]
    public void Receipt_sanitizer_preserves_each_labeled_business_reference_anywhere(
        string prefix,
        string reference)
    {
        var sanitized = LinklyReceiptTextSanitizer.Sanitize(
            $"header\n{prefix} PAN 4111111111111111\nfooter");

        Assert.Contains(reference, sanitized, StringComparison.Ordinal);
        Assert.Contains("PAN ****1111", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_sanitizer_removes_multi_word_token_fields()
    {
        var sanitized = LinklyReceiptTextSanitizer.Sanitize(
            "Access Token: access-secret\nRefresh Token=refresh-secret\nBearer Token: bearer-secret\nKEEP: ok");

        Assert.Equal("KEEP: ok", sanitized);
    }

    [Fact]
    public void Receipt_sanitizer_removes_inline_sensitive_fields_without_losing_card_tail_or_reference()
    {
        var sanitized = LinklyReceiptTextSanitizer.Sanitize(
            "error refreshToken=secret, RRN 123456789012, Track2=4111111111111111=2912; CVV=123; " +
            "CVC=456; Authorization=Bearer raw-secret; Access Token=access-secret; CardNumber=4111111111111111");

        Assert.Contains("error", sanitized, StringComparison.Ordinal);
        Assert.Contains("RRN 123456789012", sanitized, StringComparison.Ordinal);
        Assert.Contains("CardNumber=****1111", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Track2", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CVV", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CVC", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Token", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Receipt_sanitizer_only_canonicalizes_recognized_card_number_shapes()
    {
        Assert.Equal("****1111", LinklyReceiptTextSanitizer.SanitizeCardNumber("4111111111111111"));
        Assert.Equal("****1111", LinklyReceiptTextSanitizer.SanitizeCardNumber("XXXX-XXXX-XXXX-1111"));
        Assert.Equal("****1111", LinklyReceiptTextSanitizer.SanitizeCardNumber("1111"));
        Assert.Null(LinklyReceiptTextSanitizer.SanitizeCardNumber("12345678901"));
        Assert.Null(LinklyReceiptTextSanitizer.SanitizeCardNumber("123"));
    }

    [Fact]
    public void Settlement_data_sanitizer_masks_card_fields_without_masking_business_references()
    {
        var sanitized = LinklyReceiptTextSanitizer.SanitizeSettlementData(
            """
            {
              "CardNumber": "4111111111111111",
              "MaskedCardNumber": "XXXX-XXXX-XXXX-1111",
              "Track2": "4111111111111111=2512",
              "Authorization": "secret",
              "TxnRef": "123456789012",
              "RRN": "234567890123",
              "Trace": "345678901234 PAN 4111111111111111",
              "Batch": 345678901234,
              "SettlementData": "{\"CardNumber\":\"4111111111111111\",\"Track2\":\"4111111111111111=2512\",\"TxnRef\":\"456789012345\"}"
            }
            """);

        using var document = JsonDocument.Parse(sanitized!);
        var root = document.RootElement;
        Assert.Equal("****1111", root.GetProperty("CardNumber").GetString());
        Assert.Equal("****1111", root.GetProperty("MaskedCardNumber").GetString());
        Assert.False(root.TryGetProperty("Track2", out _));
        Assert.False(root.TryGetProperty("Authorization", out _));
        Assert.Equal("123456789012", root.GetProperty("TxnRef").GetString());
        Assert.Equal("234567890123", root.GetProperty("RRN").GetString());
        Assert.Equal("****1234 PAN ****1111", root.GetProperty("Trace").GetString());
        Assert.Equal(345678901234, root.GetProperty("Batch").GetInt64());
        using var nested = JsonDocument.Parse(root.GetProperty("SettlementData").GetString()!);
        Assert.Equal("****1111", nested.RootElement.GetProperty("CardNumber").GetString());
        Assert.False(nested.RootElement.TryGetProperty("Track2", out _));
        Assert.Equal("456789012345", nested.RootElement.GetProperty("TxnRef").GetString());
    }

    [Fact]
    public void Settlement_data_sanitizer_preserves_strict_official_fixed_width_payload()
    {
        Assert.Equal(
            OfficialFixedWidthSettlement,
            LinklyReceiptTextSanitizer.SanitizeSettlementData(OfficialFixedWidthSettlement));
    }

    [Fact]
    public void Settlement_data_sanitizer_preserves_real_terminal_payload_with_totals_name_and_space_sign()
    {
        // 生产终端实测：汇总行叫 TOTALS，非负合计的符号位是空格；之前会被 PAN 正则整段误掩码。
        const string realTerminalSettlement =
            "000000001069" +
            "VISA                000041520038000000000000000000000000 000041520038" +
            "069TOTALS              000041520038000000000000000000000000 000041520038";

        Assert.Equal(
            realTerminalSettlement,
            LinklyReceiptTextSanitizer.SanitizeSettlementData(realTerminalSettlement));
    }

    [Fact]
    public void Settlement_data_sanitizer_does_not_treat_arbitrary_twelve_digit_text_as_fixed_width()
    {
        Assert.Equal(
            "MERCHANT ****9012",
            LinklyReceiptTextSanitizer.SanitizeSettlementData("MERCHANT 123456789012"));
    }

    [Fact]
    public void Settlement_data_sanitizer_rejects_pan_hidden_in_fixed_width_card_name()
    {
        var malicious = OfficialFixedWidthSettlement.Replace(
            "VISA                ",
            "4111111111111111    ",
            StringComparison.Ordinal);

        var sanitized = LinklyReceiptTextSanitizer.SanitizeSettlementData(malicious);

        Assert.NotEqual(malicious, sanitized);
        Assert.DoesNotContain("4111111111111111", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Settlement_data_sanitizer_sanitizes_optional_text_tail_and_rewrites_its_length()
    {
        const string tail = "Authorization=secret";
        var source = OfficialFixedWidthSettlement + tail.Length.ToString("D3") + tail;

        var sanitized = LinklyReceiptTextSanitizer.SanitizeSettlementData(source);

        Assert.DoesNotContain("secret", sanitized, StringComparison.Ordinal);
        Assert.EndsWith("000", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_sanitizer_preserves_duplicate_receipts_for_print_audit_order()
    {
        var receipts = LinklyReceiptTextSanitizer.SanitizeReceipts(
            ["CARD 4111111111111111", "CARD 4111111111111111"]);

        Assert.Equal(["CARD ****1111", "CARD ****1111"], receipts);
    }
}
