using System.Collections.Concurrent;
using BlazorApp.Api.Models.DailyClose;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 现金日结的真实取数口：读 POSM 库 POSM_DailyClose（收银端日结上传与运维回填，表由 Hbpos.Api 建立，后台只读）。
/// 只取有实点金额的记录（DetailLevel 为 Full / CashOnly）；TraceOnly 回填行没有金额，不能计入现金池，当作「没有日结」。
/// 同一设备同一营业日的多份存档原样返回，由 CashCloseSelectionResolver 决定纳入哪几份。
/// </summary>
internal sealed class PosmCashDailyCloseSource : ICashDailyCloseSource
{
    // SQL Server 单语句参数上限 2100：分店代码按批查询，留足余量。
    private const int StoreCodeChunkSize = 500;

    // 表一旦确认存在就不会消失：按连接串记住「已确认」，之后整个进程不再查元数据；不存在时每次都重查，表建好后自动接入。
    private static readonly ConcurrentDictionary<string, bool> s_tableConfirmed = new(StringComparer.Ordinal);

    // 现金存入 / 取出两列由新版 POS API 启动时补加；同样按连接串记住「已确认」，未补加前按 0 处理。
    private static readonly ConcurrentDictionary<string, bool> s_cashMovementColumnsConfirmed = new(StringComparer.Ordinal);

    private readonly ISqlSugarClient _posmDb;

    public PosmCashDailyCloseSource(ISqlSugarClient posmDb)
    {
        _posmDb = posmDb;
    }

    /// <summary>
    /// 表由 POS API 启动时建立：后台若先于 POS API 上线、表还不存在，按「日结未接入」处理（余额显示不可算），
    /// 而不是查询报错，也不能把流入当 0 算出错误余额。
    /// </summary>
    public bool IsConnected
    {
        get
        {
            var key = _posmDb.CurrentConnectionConfig.ConnectionString ?? string.Empty;
            if (s_tableConfirmed.ContainsKey(key))
            {
                return true;
            }

            var exists = _posmDb.DbMaintenance.IsAnyTable("POSM_DailyClose", false);
            if (exists)
            {
                s_tableConfirmed.TryAdd(key, true);
            }

            return exists;
        }
    }

    public async Task<IReadOnlyList<CashCloseArchive>> GetArchivesAsync(
        IReadOnlyCollection<string> storeCodes,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken
    )
    {
        var codes = storeCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0 || to < from || !IsConnected)
        {
            return Array.Empty<CashCloseArchive>();
        }

        var fromColumn = from.ToDateTime(TimeOnly.MinValue);
        var toColumn = to.ToDateTime(TimeOnly.MinValue);
        var result = new List<CashCloseArchive>();
        foreach (var chunk in codes.Chunk(StoreCodeChunkSize))
        {
            var batch = chunk.ToList();
            var rows = await _posmDb.Queryable<PosmDailyClose>()
                .Where(item => batch.Contains(item.StoreCode)
                    && item.BusinessDate >= fromColumn
                    && item.BusinessDate <= toColumn
                    && item.CountedCashAmount != null)
                .Select(item => new
                {
                    item.DailyCloseGuid,
                    item.StoreCode,
                    item.DeviceCode,
                    item.BusinessDate,
                    item.PeriodFromUtc,
                    item.PeriodToUtc,
                    item.SavedAtUtc,
                    item.CountedCashAmount,
                    item.ExpectedCashAmount,
                    item.CashDifference,
                })
                .ToListAsync(cancellationToken);

            var movementTotals = await LoadCashMovementTotalsAsync(
                rows.Select(row => row.DailyCloseGuid).ToList(),
                cancellationToken
            );
            foreach (var row in rows)
            {
                var businessDate = DateOnly.FromDateTime(row.BusinessDate);
                var counted = row.CountedCashAmount!.Value;
                // 应有现金优先取上传值；回填行可能只有差异，按「应有 = 实点 − 差异」还原；都没有时按无差异处理。
                var expected = row.ExpectedCashAmount
                    ?? (row.CashDifference.HasValue ? counted - row.CashDifference.Value : counted);
                // 回填行没有统计区间：按整个营业日处理，手选多份时会提示可能重叠，宁可多提示也不漏提示。
                var dayStart = DateTime.SpecifyKind(businessDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
                result.Add(
                    new CashCloseArchive(
                        row.StoreCode,
                        businessDate,
                        row.DeviceCode,
                        row.DailyCloseGuid.ToString("D"),
                        row.PeriodFromUtc ?? dayStart,
                        row.PeriodToUtc ?? dayStart.AddDays(1),
                        row.SavedAtUtc,
                        counted,
                        expected,
                        movementTotals.TryGetValue(row.DailyCloseGuid, out var totals) ? totals.CashIn : 0m,
                        totals.CashOut
                    )
                );
            }
        }

        return result;
    }

    /// <summary>
    /// 单独读现金存入 / 取出合计，而不是加进 PosmDailyClose 实体：实体还被日结查询页整表读取，
    /// 主后台若先于 POS API 上线，表里还没有这两列，加进实体会让那些查询直接报错。
    /// </summary>
    private async Task<Dictionary<Guid, (decimal CashIn, decimal CashOut)>> LoadCashMovementTotalsAsync(
        IReadOnlyList<Guid> guids,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<Guid, (decimal CashIn, decimal CashOut)>();
        if (guids.Count == 0 || !HasCashMovementColumns())
        {
            return result;
        }

        foreach (var chunk in guids.Chunk(StoreCodeChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = chunk
                .Select((guid, index) => new SugarParameter($"@g{index}", guid))
                .ToArray();
            var sql =
                "SELECT [DailyCloseGuid], [CashInAmount], [CashOutAmount] FROM [dbo].[POSM_DailyClose] "
                + "WHERE ([CashInAmount] IS NOT NULL OR [CashOutAmount] IS NOT NULL) AND [DailyCloseGuid] IN ("
                + string.Join(", ", parameters.Select(parameter => parameter.ParameterName))
                + ")";
            var rows = await _posmDb.Ado.SqlQueryAsync<CashMovementTotalsRow>(sql, parameters);
            foreach (var row in rows)
            {
                result[row.DailyCloseGuid] = (row.CashInAmount ?? 0m, row.CashOutAmount ?? 0m);
            }
        }

        return result;
    }

    private bool HasCashMovementColumns()
    {
        var key = _posmDb.CurrentConnectionConfig.ConnectionString ?? string.Empty;
        if (s_cashMovementColumnsConfirmed.ContainsKey(key))
        {
            return true;
        }

        var exists = _posmDb.DbMaintenance.IsAnyColumn("POSM_DailyClose", "CashOutAmount", false);
        if (exists)
        {
            s_cashMovementColumnsConfirmed.TryAdd(key, true);
        }

        return exists;
    }

    private sealed class CashMovementTotalsRow
    {
        public Guid DailyCloseGuid { get; set; }
        public decimal? CashInAmount { get; set; }
        public decimal? CashOutAmount { get; set; }
    }
}
