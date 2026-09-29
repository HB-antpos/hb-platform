using System.Text;
using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

/// <summary>
/// 直接覆盖退款券幂等标记：标记由服务端从幂等键生成并作为备注首段；
/// 分期取消结案只接受「首段 = 本次标记 + 分隔符」的退款券作为服务端发券证据。
/// </summary>
public sealed class StoreVoucherRefundMarkerTests
{
    [Fact]
    public void Create_wraps_base64_of_trimmed_utf8_key()
    {
        var marker = StoreVoucherRefundMarker.Create("  ORDER-1:PAY-1  ");

        var expectedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("ORDER-1:PAY-1"));
        Assert.Equal($"RefundKey[{expectedPayload}]", marker);
        Assert.StartsWith(StoreVoucherRefundMarker.Prefix, marker, StringComparison.Ordinal);
        Assert.EndsWith("]", marker, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_is_deterministic_and_ignores_surrounding_whitespace_only()
    {
        Assert.Equal(StoreVoucherRefundMarker.Create("key-1"), StoreVoucherRefundMarker.Create("\tkey-1 \r\n"));
        // 幂等键大小写敏感：不同大小写是不同的退款 claim。
        Assert.NotEqual(StoreVoucherRefundMarker.Create("key-1"), StoreVoucherRefundMarker.Create("KEY-1"));
        // 内部空白属于键的一部分。
        Assert.NotEqual(StoreVoucherRefundMarker.Create("key 1"), StoreVoucherRefundMarker.Create("key1"));
    }

    [Theory]
    [InlineData("退款-订单-001")]
    [InlineData("key]with | separator")]
    [InlineData("RefundKey[nested]")]
    public void Create_encodes_unicode_and_marker_syntax_so_payload_cannot_break_out(string idempotencyKey)
    {
        var marker = StoreVoucherRefundMarker.Create(idempotencyKey);

        var payload = marker[StoreVoucherRefundMarker.Prefix.Length..^1];
        Assert.Equal(idempotencyKey, Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        // Base64 字母表不含 ']' 与空格，标记内部不会出现提前闭合或伪造分隔符。
        Assert.DoesNotContain("]", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(StoreVoucherRefundMarker.Separator, marker, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_refund_remark_is_accepted()
    {
        var marker = StoreVoucherRefundMarker.Create("ORDER-1:PAY-1");
        var remark = string.Join(StoreVoucherRefundMarker.Separator, marker, "Refund voucher", "Order ORDER-1", "Customer changed mind");

        Assert.True(StoreVoucherRefundMarker.HasCanonicalPrefix(remark, marker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_remark_is_rejected(string? remark)
    {
        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(remark, StoreVoucherRefundMarker.Create("k")));
    }

    [Fact]
    public void Marker_without_separator_is_not_canonical()
    {
        var marker = StoreVoucherRefundMarker.Create("ORDER-1:PAY-1");

        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(marker, marker));
        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(marker + "|Refund voucher", marker));
    }

    [Fact]
    public void Marker_that_is_not_the_first_segment_is_rejected()
    {
        // 自由文本段里出现的标记不能被当作服务端发券证据。
        var marker = StoreVoucherRefundMarker.Create("ORDER-1:PAY-1");

        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix($"Refund voucher | {marker} | Order ORDER-1", marker));
        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix($" {marker} | Refund voucher", marker));
    }

    [Fact]
    public void Prefix_match_is_ordinal_and_case_sensitive()
    {
        var marker = StoreVoucherRefundMarker.Create("abc");
        var remark = marker + StoreVoucherRefundMarker.Separator + "Refund voucher";

        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(remark.ToLowerInvariant(), marker));
        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(remark.ToUpperInvariant(), marker));
    }

    [Fact]
    public void Marker_of_key_that_is_a_prefix_of_another_key_does_not_match()
    {
        // "abc" 与 "abcd" 的 Base64 有公共前缀；闭合括号 + 分隔符保证不会互相认领。
        var shortMarker = StoreVoucherRefundMarker.Create("abc");
        var longMarker = StoreVoucherRefundMarker.Create("abcd");
        var longRemark = longMarker + StoreVoucherRefundMarker.Separator + "Refund voucher";
        var shortRemark = shortMarker + StoreVoucherRefundMarker.Separator + "Refund voucher";

        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(longRemark, shortMarker));
        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(shortRemark, longMarker));
        Assert.True(StoreVoucherRefundMarker.HasCanonicalPrefix(longRemark, longMarker));
    }

    [Fact]
    public void Remark_for_other_idempotency_key_is_rejected()
    {
        var remark = StoreVoucherRefundMarker.Create("ORDER-1:PAY-1") + StoreVoucherRefundMarker.Separator + "Refund voucher";

        Assert.False(StoreVoucherRefundMarker.HasCanonicalPrefix(remark, StoreVoucherRefundMarker.Create("ORDER-1:PAY-2")));
    }
}
