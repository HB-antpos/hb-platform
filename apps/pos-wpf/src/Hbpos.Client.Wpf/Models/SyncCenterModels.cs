using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Services;
using System.Globalization;

namespace Hbpos.Client.Wpf.Models;

public sealed partial class RowSelectionState : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;
}

public sealed record SyncQueueOverview(
    int PendingCount,
    int FailedCount,
    int SyncingCount,
    string? LastError);

public sealed record SyncQueueListItem(
    Guid EntityId,
    string EntityType,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastTriedAt,
    string? ErrorMessage,
    decimal? Amount)
{
    public RowSelectionState Selection { get; } = new();

    public bool CanRetry =>
        (EntityType.Equals("Order", StringComparison.OrdinalIgnoreCase) ||
         EntityType.Equals("LinklySettlement", StringComparison.OrdinalIgnoreCase)) &&
        (Status.Equals("Pending", StringComparison.OrdinalIgnoreCase) ||
         Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));

    // 中文注释：订单行显示订单短码（GUID 末 8 位，与历史页、小票一致）；结算等其它实体没有订单短码，仍显示前 10 位。
    public string ShortEntityId => EntityType.Equals("Order", StringComparison.OrdinalIgnoreCase)
        ? ReceiptOrderShortCode.Create(EntityId)
        : EntityId.ToString("N")[..10].ToUpperInvariant();

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string LastTriedAtDisplay => LastTriedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    public string AmountDisplay => Amount is { } amount ? string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}") : "-";
}

public sealed record OperationAuditQueueListItem(
    Guid EventId,
    string Status,
    DateTimeOffset OccurredAt,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    string? ErrorCode,
    string? ErrorMessage)
{
    public RowSelectionState Selection { get; } = new();

    public string ShortEventId => EventId.ToString("N")[..10].ToUpperInvariant();

    public string OccurredAtDisplay => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string NextAttemptAtDisplay => NextAttemptAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
