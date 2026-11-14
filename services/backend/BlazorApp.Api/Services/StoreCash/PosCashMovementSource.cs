using System.Collections.Concurrent;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 一笔收银端现金进出（POSM 库 POSM_CashMovement，表由 Hbpos.Api 建立，后台只读）。
/// Direction：In / Out；ReasonCode：ChangeTopUp / OtherIn / SafeDrop / PaidOut；
/// VoidsMovementGuid 非空表示这是一条冲正记录（方向与原记录相反、金额相同）。
/// </summary>
public sealed record PosCashMovement(
    long Id,
    Guid MovementGuid,
    string StoreCode,
    string DeviceCode,
    string Direction,
    string ReasonCode,
    decimal Amount,
    string? Note,
    Guid? VoidsMovementGuid,
    DateOnly BusinessDate,
    DateTime OccurredAtUtc,
    string CashierName,
    string? AuthorizedByName,
    string? DisplayNumber,
    DateTime ReceivedAtUtc
);

public static class PosCashMovementReasons
{
    public const string ChangeTopUp = "ChangeTopUp";
    public const string OtherIn = "OtherIn";
    public const string SafeDrop = "SafeDrop";
    public const string PaidOut = "PaidOut";
}

public interface IPosCashMovementSource
{
    /// <summary>表是否已由 POS API 建立；未建立时一律按「没有收银端现金进出」处理。</summary>
    bool IsConnected { get; }

    /// <summary>按分店与营业日区间（含两端）取全部记录（含冲正），按发生时间排序。</summary>
    Task<IReadOnlyList<PosCashMovement>> GetByStoreAsync(
        string storeCode,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken
    );

    /// <summary>按自增 Id 增量取小额支出（含其冲正记录），供自动生成现金支出使用。</summary>
    Task<IReadOnlyList<PosCashMovement>> GetPaidOutsAfterAsync(long afterId, int take, CancellationToken cancellationToken);
}

internal sealed class PosmCashMovementSource : IPosCashMovementSource
{
    private const string Columns = """
        [Id], [MovementGuid], [StoreCode], [DeviceCode], [Direction], [ReasonCode], [Amount], [Note],
        [VoidsMovementGuid], [BusinessDate], [OccurredAtUtc], [CashierName], [AuthorizedByName],
        [DisplayNumber], [ReceivedAtUtc]
        """;

    // 表一旦确认存在就不会消失：按连接串记住，之后不再查元数据。
    private static readonly ConcurrentDictionary<string, bool> s_tableConfirmed = new(StringComparer.Ordinal);

    private readonly ISqlSugarClient _posmDb;

    public PosmCashMovementSource(ISqlSugarClient posmDb)
    {
        _posmDb = posmDb;
    }

    public bool IsConnected
    {
        get
        {
            var key = _posmDb.CurrentConnectionConfig.ConnectionString ?? string.Empty;
            if (s_tableConfirmed.ContainsKey(key))
            {
                return true;
            }

            var exists = _posmDb.DbMaintenance.IsAnyTable("POSM_CashMovement", false);
            if (exists)
            {
                s_tableConfirmed.TryAdd(key, true);
            }

            return exists;
        }
    }

    public async Task<IReadOnlyList<PosCashMovement>> GetByStoreAsync(
        string storeCode,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(storeCode) || to < from || !IsConnected)
        {
            return Array.Empty<PosCashMovement>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rows = await _posmDb.Ado.SqlQueryAsync<Row>(
            $"SELECT {Columns} FROM [dbo].[POSM_CashMovement] "
                + "WHERE [StoreCode] = @StoreCode AND [BusinessDate] >= @From AND [BusinessDate] <= @To "
                + "ORDER BY [OccurredAtUtc], [Id]",
            new SugarParameter("@StoreCode", storeCode.Trim()),
            new SugarParameter("@From", from.ToDateTime(TimeOnly.MinValue)) { DbType = System.Data.DbType.Date },
            new SugarParameter("@To", to.ToDateTime(TimeOnly.MinValue)) { DbType = System.Data.DbType.Date }
        );
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<PosCashMovement>> GetPaidOutsAfterAsync(
        long afterId,
        int take,
        CancellationToken cancellationToken
    )
    {
        if (!IsConnected)
        {
            return Array.Empty<PosCashMovement>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rows = await _posmDb.Ado.SqlQueryAsync<Row>(
            $"SELECT TOP (@Take) {Columns} FROM [dbo].[POSM_CashMovement] "
                + "WHERE [ReasonCode] = N'PaidOut' AND [Id] > @AfterId ORDER BY [Id]",
            new SugarParameter("@Take", take),
            new SugarParameter("@AfterId", afterId)
        );
        return rows.Select(ToModel).ToList();
    }

    private static PosCashMovement ToModel(Row row) =>
        new(
            row.Id,
            row.MovementGuid,
            row.StoreCode,
            row.DeviceCode,
            row.Direction,
            row.ReasonCode,
            row.Amount,
            row.Note,
            row.VoidsMovementGuid,
            DateOnly.FromDateTime(row.BusinessDate),
            DateTime.SpecifyKind(row.OccurredAtUtc, DateTimeKind.Utc),
            row.CashierName,
            row.AuthorizedByName,
            row.DisplayNumber,
            DateTime.SpecifyKind(row.ReceivedAtUtc, DateTimeKind.Utc)
        );

    private sealed class Row
    {
        public long Id { get; set; }
        public Guid MovementGuid { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string DeviceCode { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string ReasonCode { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string? Note { get; set; }
        public Guid? VoidsMovementGuid { get; set; }
        public DateTime BusinessDate { get; set; }
        public DateTime OccurredAtUtc { get; set; }
        public string CashierName { get; set; } = string.Empty;
        public string? AuthorizedByName { get; set; }
        public string? DisplayNumber { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
    }
}
