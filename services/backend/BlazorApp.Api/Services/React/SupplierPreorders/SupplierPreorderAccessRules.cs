using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 下单权限判定（纯函数）。能编辑某店订单 = 下单人员授权里有（本人, 该店）且该订单表开启了该店且未截止；
/// 持有 SupplierPreorder.Manage 的人可编辑所有已开启分店，截止/锁定后也能改。
/// </summary>
public static class SupplierPreorderAccessRules
{
    public sealed record StoreEditDecision(bool CanEdit, string? ReadOnlyReason);

    public static StoreEditDecision EvaluateStoreEdit(
        string formStatus,
        DateTime? deadlineAtUtc,
        bool isManager,
        bool isOrdererForStore,
        bool storeEnabled,
        DateTime nowUtc
    )
    {
        if (formStatus == SupplierPreorderFormStatuses.Draft)
        {
            return new(false, "订单表尚未发布");
        }

        if (!storeEnabled)
        {
            return new(false, "这张订单表没有开启本店");
        }

        if (!isManager && !isOrdererForStore)
        {
            return new(false, "你没有被授权替本店下单");
        }

        if (isManager)
        {
            return new(true, null);
        }

        if (formStatus == SupplierPreorderFormStatuses.Closed)
        {
            return new(false, "订单表已截止锁定");
        }

        if (deadlineAtUtc.HasValue && nowUtc > deadlineAtUtc.Value)
        {
            return new(false, "已过下单截止时间");
        }

        return new(true, null);
    }

    /// <summary>对普通下单人是否已锁定（已截止锁定或已过截止时间）。</summary>
    public static bool IsLockedForOrderers(string formStatus, DateTime? deadlineAtUtc, DateTime nowUtc) =>
        formStatus == SupplierPreorderFormStatuses.Closed || (deadlineAtUtc.HasValue && nowUtc > deadlineAtUtc.Value);

    /// <summary>状态流转：Draft→Open（至少 1 款商品）、Open→Closed、Closed→Open、Open→Draft（还没有任何分店下单）。</summary>
    public static string? ValidateStatusTransition(string from, string to, int itemCount, bool hasAnyLines)
    {
        if (from == to)
        {
            return "状态没有变化";
        }

        return (from, to) switch
        {
            (SupplierPreorderFormStatuses.Draft, SupplierPreorderFormStatuses.Open) => itemCount > 0 ? null : "订单表没有商品，不能发布",
            (SupplierPreorderFormStatuses.Open, SupplierPreorderFormStatuses.Closed) => null,
            (SupplierPreorderFormStatuses.Closed, SupplierPreorderFormStatuses.Open) => null,
            (SupplierPreorderFormStatuses.Open, SupplierPreorderFormStatuses.Draft) => hasAnyLines ? "已有分店下单，不能退回草稿" : null,
            _ => $"不能从 {from} 改为 {to}",
        };
    }
}
