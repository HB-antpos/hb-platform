using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 订单短码在各处显示必须一致：小票条码、历史页、同步中心都是订单 GUID 末 8 位，
/// 不能再出现「历史页显示开头 8 / 10 位、小票印末 8 位」这种对不上的情况。
/// </summary>
public sealed class OrderShortCodeDisplayTests
{
    // N 格式：2336d69e7a1c4f3e9a219762c035aabb，开头 8 位 2336D69E，末 8 位 C035AABB。
    private static readonly Guid OrderGuid = Guid.Parse("2336d69e-7a1c-4f3e-9a21-9762c035aabb");

    [Fact]
    public void Receipt_short_code_is_the_last_eight_hex_digits()
    {
        Assert.Equal("C035AABB", ReceiptOrderShortCode.Create(OrderGuid));
    }

    [Fact]
    public void Local_order_summary_shows_the_same_short_code_as_the_receipt()
    {
        var summary = new LocalOrderSummary(
            OrderGuid, "1042", "POS-01", "Alice", DateTimeOffset.UtcNow, 1.49m, 0m, 1.49m, "Synced", 2, "Cash");

        Assert.Equal(ReceiptOrderShortCode.Create(OrderGuid), summary.ShortOrderId);
        Assert.Equal("C035AABB", summary.ShortOrderId);
    }

    [Fact]
    public void Sync_center_shows_the_order_short_code_for_orders_but_keeps_the_ten_char_prefix_for_other_entities()
    {
        Assert.Equal("C035AABB", CreateSyncItem("Order").ShortEntityId);
        Assert.Equal("2336D69E7A", CreateSyncItem("LinklySettlement").ShortEntityId);
    }

    private static SyncQueueListItem CreateSyncItem(string entityType) => new(
        OrderGuid,
        entityType,
        "Pending",
        DateTimeOffset.UtcNow,
        null,
        null,
        1.49m);
}
