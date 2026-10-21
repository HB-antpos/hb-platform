using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;

internal static class StoreOrderManagementRules
{
    internal static bool IsEditableOrder(WareHouseOrder order)
    {
        return order.FlowStatus is 0 or 1 or 3;
    }

    internal static decimal CalculateOrderImportAmount(
        decimal? quantity,
        decimal? importPrice
    )
    {
        return (quantity ?? 0) * (importPrice ?? 0);
    }

    internal static decimal NormalizeMinimumOrderQuantity(decimal? minimumOrderQuantity)
    {
        var value = minimumOrderQuantity ?? 1;
        return value < 1 ? 1 : value;
    }

    internal static bool ShouldSoftDelete(decimal? quantity, decimal? allocatedQuantity)
    {
        // 空行 = 订货数量 <= 0 且发货数量恰好为 0。发货数量为负的是退货行（Quantity=0、AllocQuantity<0），
        // 它是有效数据，不能被当成空行软删。
        return (quantity ?? 0) <= 0 && (allocatedQuantity ?? 0) == 0;
    }

    internal static bool ShouldSoftDeleteExistingDetail(
        decimal? quantity,
        decimal? allocatedQuantity
    )
    {
        // 原单行更新使用可空比较；任一历史列为 NULL 时不自动软删除。
        // 发货数量必须恰好为 0 才算空行：负数是退货行（见 ShouldSoftDelete），不能被软删。
        return quantity <= 0 && allocatedQuantity == 0;
    }

    internal static bool CanUseDetailGuidQuantityBatchUpdate(
        BatchUpdateOrderLineInput input
    )
    {
        return input.Items.Count > 0
            && input.Items.All(item =>
                !string.IsNullOrWhiteSpace(item.DetailGuid)
                && item.Quantity.HasValue
                && !item.ImportPrice.HasValue
                && !item.SyncImportPrice
            );
    }

    internal static DateTime ResolveOrderRevisionAt(DateTime now)
    {
        var revision = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        return DateTimeOffset.FromUnixTimeMilliseconds(revision).LocalDateTime;
    }
}
