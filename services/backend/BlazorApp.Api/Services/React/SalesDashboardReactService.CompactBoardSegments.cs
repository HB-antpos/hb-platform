using System.Data;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Cache;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 紧凑销售看板的自然月分片缓存。
/// 看板区间按自然月切片（首尾不满月的部分单独成片），每片缓存「门店×商品×日统计原始供应商编码」的原始聚合；
/// 不同区间只要覆盖同一片就复用，统计每小时重算只让当月那一片失效。国内供应商归属（POSM 映射）、
/// 商品资料与名称随时会变，不进分片，合并后照旧解析。
/// </summary>
public partial class SalesDashboardReactService
{
    // 已结束的整月身份基本不变，缓存久一些；含今天或首尾不满月的分片只被相近区间复用，缓存短一些。
    private static readonly TimeSpan CompactSalesBoardClosedMonthCacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan CompactSalesBoardOpenSegmentCacheDuration = TimeSpan.FromMinutes(30);

    /// <summary>看板区间与一个自然月的交集 [Start, EndExclusive)。</summary>
    internal readonly record struct CompactSalesBoardSegmentRange(DateTime Start, DateTime EndExclusive)
    {
        public DateTime Month => new(Start.Year, Start.Month, 1);
        public bool IsWholeMonth => Start == Month && EndExclusive == Month.AddMonths(1);
        public bool Contains(DateTime date) => date >= Start && date < EndExclusive;
    }

    private readonly record struct CompactSalesBoardSegmentCell(
        int Branch,
        int Product,
        int Supplier,
        int Quantity,
        decimal Amount,
        DateTime LastDate
    );

    /// <summary>一个分片的原始聚合；维度字符串去重后用下标引用，一个月约 7 万格、3 MB。</summary>
    private sealed record CompactSalesBoardSegment(
        string[] Branches,
        string[] Products,
        string[] Suppliers,
        CompactSalesBoardSegmentCell[] Cells
    );

    private sealed class CompactSalesBoardSegmentBuilder
    {
        private readonly Dictionary<string, int> _branches = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _products = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _suppliers = new(StringComparer.Ordinal);
        private readonly List<CompactSalesBoardSegmentCell> _cells = new();

        public void Add(string branch, string product, string supplier, int quantity, decimal amount, DateTime lastDate)
        {
            _cells.Add(new CompactSalesBoardSegmentCell(
                IndexOf(_branches, branch), IndexOf(_products, product), IndexOf(_suppliers, supplier),
                quantity, amount, lastDate));
        }

        public CompactSalesBoardSegment Build() => new(
            Ordered(_branches), Ordered(_products), Ordered(_suppliers), _cells.ToArray());

        private static int IndexOf(Dictionary<string, int> map, string value)
        {
            if (!map.TryGetValue(value, out var index))
            {
                index = map.Count;
                map[value] = index;
            }
            return index;
        }

        private static string[] Ordered(Dictionary<string, int> map)
        {
            var values = new string[map.Count];
            foreach (var (value, index) in map)
                values[index] = value;
            return values;
        }
    }

    internal static List<CompactSalesBoardSegmentRange> SplitCompactSalesBoardSegments(DateTime startDate, DateTime endDate)
    {
        var segments = new List<CompactSalesBoardSegmentRange>();
        var start = startDate.Date;
        var endExclusive = endDate.Date.AddDays(1);
        while (start < endExclusive)
        {
            var nextMonth = new DateTime(start.Year, start.Month, 1).AddMonths(1);
            var end = nextMonth < endExclusive ? nextMonth : endExclusive;
            segments.Add(new CompactSalesBoardSegmentRange(start, end));
            start = end;
        }
        return segments;
    }

    /// <summary>
    /// 分片身份：片内每天的（日期、来源版本、聚合时间）。刻意不含状态与完成时间：排队/重算中在快照里读到的
    /// 仍是上一版已发布事实，只有发布（聚合时间变化）才需要重建，与销售明细月投影的月身份同一口径。
    /// </summary>
    private static string BuildCompactSalesBoardSegmentIdentity(
        IEnumerable<SalesDetailReportStatusSqlRow> rows,
        CompactSalesBoardSegmentRange segment
    )
    {
        var source = string.Join(
            "|",
            rows.Where(row => segment.Contains(row.Date.Date))
                .OrderBy(row => row.Date)
                .Select(row => $"{row.Date:yyyyMMdd}:{row.SourceProductVersion}:{row.LastAggregatedAtUtc?.Ticks}")
        );
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    /// <summary>
    /// 读取看板区间的原始聚合行：命中缓存的分片直接复用，其余分片在一个统计快照里一次读回。
    /// 返回的状态行 = 复用分片沿用预检状态（身份相同），补读分片用快照内状态，调用方据此判定完整性与缓存版本。
    /// </summary>
    private async Task<(List<SalesDetailReportStatusSqlRow> States, List<CompactSalesBoardCubeRow> Rows)> ReadCompactSalesBoardSegmentedRowsAsync(
        DateRangeDto dateRange,
        List<SalesDetailReportStatusSqlRow> precheckStates,
        List<string> chinaFamilyCodes,
        bool forceRefresh,
        long expectedGeneration
    )
    {
        // 编码族决定分片里收了哪些行；新增国内供应商后旧分片自然失效。
        var codeFamilySignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", chinaFamilyCodes))));
        var ranges = SplitCompactSalesBoardSegments(dateRange.StartDate, dateRange.EndDate);
        var segments = new CompactSalesBoardSegment?[ranges.Count];
        var missing = new List<int>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var key = SalesDashboardCacheKeys.CompactSalesBoardSegment(
                ranges[i].Start,
                ranges[i].EndExclusive,
                BuildCompactSalesBoardSegmentIdentity(precheckStates, ranges[i]),
                codeFamilySignature
            );
            // 强制刷新连分片一起绕过：统计表被手工修正而状态未变时，只有这条路能读到新数据。
            if (!forceRefresh
                && _cache.TryGetValue<CompactSalesBoardSegment>(key, out var cached)
                && cached != null)
            {
                segments[i] = cached;
            }
            else
            {
                missing.Add(i);
            }
        }

        var states = precheckStates;
        if (missing.Count > 0)
        {
            var missingRanges = missing.Select(i => ranges[i]).ToList();
            var readRange = new DateRangeDto
            {
                StartDate = missingRanges[0].Start,
                EndDate = missingRanges[^1].EndExclusive.AddDays(-1),
            };
            // 状态与聚合同一快照：分片身份与数据一一对应。SQL Server 走独立非 MARS 连接一次读回；
            // 其他数据库或已有外部事务时沿用 SqlSugar 快照读取（事务内不附加 NOLOCK）。
            var (snapshotStates, built) = UsesCompactBoardDedicatedConnection()
                ? await ReadCompactSalesBoardSegmentsOnDedicatedConnectionAsync(readRange, missingRanges, chinaFamilyCodes)
                : await ReadReportSnapshotAsync(async () => (
                    await ReadCompactSalesBoardStatusRowsAsync(readRange),
                    await ReadCompactSalesBoardSegmentFactsAsync(missingRanges, chinaFamilyCodes)
                ));

            bool InMissing(DateTime date) => missingRanges.Any(range => range.Contains(date.Date));
            states = precheckStates
                .Where(row => !InMissing(row.Date))
                .Concat(snapshotStates.Where(row => InMissing(row.Date)))
                .OrderBy(row => row.Date)
                .ToList();

            var today = SalesStatisticsBusinessDate.Today();
            for (var j = 0; j < missing.Count; j++)
            {
                var range = missingRanges[j];
                segments[missing[j]] = built[j];
                var key = SalesDashboardCacheKeys.CompactSalesBoardSegment(
                    range.Start,
                    range.EndExclusive,
                    BuildCompactSalesBoardSegmentIdentity(snapshotStates, range),
                    codeFamilySignature
                );
                var duration = range.IsWholeMonth && range.EndExclusive <= today
                    ? CompactSalesBoardClosedMonthCacheDuration
                    : CompactSalesBoardOpenSegmentCacheDuration;
                SalesDashboardCacheKeys.TryExecuteProductSalesAnalysisCacheWrite(
                    key,
                    expectedGeneration,
                    (registrationToken, expirationToken) => _cache.Set(
                        key,
                        built[j],
                        BuildProductSalesAnalysisCacheOptions(key, duration, registrationToken, expirationToken)
                    )
                );
            }
        }

        return (states, MergeCompactSalesBoardSegments(segments.Select(segment => segment!).ToList()));
    }

    private bool UsesCompactBoardDedicatedConnection() =>
        _context.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer && _context.Db.Ado.Transaction == null;

    /// <summary>
    /// 看板大结果集读取用的独立连接：关闭 MARS。生产连接串开了 MARS，Linux 上 SqlClient 托管网络层读大结果集
    /// 只有每秒约 3 万行（2026-09-24 Query Store：分桶语句 CPU 5.2 秒、时长 28.6 秒，其余都在等 API 读走 65 万行；
    /// API 到库往返仅 1 毫秒）。与销售明细相同的连接串改法，共用同一个连接池。
    /// </summary>
    private async Task<SqlConnection> OpenCompactBoardDedicatedConnectionAsync()
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(_context.Db.CurrentConnectionConfig.ConnectionString)
        {
            MultipleActiveResultSets = false,
            MinPoolSize = 1,
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// SQL Server：在独立连接上用一个批次读状态与缺失分片，数据库启用快照隔离时两者在同一快照里。
    /// 事实用一条语句按月分桶聚合，首尾分片被区间边界截断，正好等于不满月的分片；读取器直接装片，不经 ORM 实体化。
    /// </summary>
    private async Task<(List<SalesDetailReportStatusSqlRow> States, List<CompactSalesBoardSegment> Segments)> ReadCompactSalesBoardSegmentsOnDedicatedConnectionAsync(
        DateRangeDto readRange,
        List<CompactSalesBoardSegmentRange> ranges,
        List<string> chinaFamilyCodes
    )
    {
        await using var connection = await OpenCompactBoardDedicatedConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, _context.Db.Ado.CommandTimeOut);
        // 按本次区间编译（RECOMPILE），编码族与月份用 OPENJSON 参数，SQL 文本不随目录或区间变化。
        command.CommandText = """
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND snapshot_isolation_state = 1)
            BEGIN
                SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
                BEGIN TRANSACTION;
            END;
            BEGIN TRY
                SELECT r.[Date], r.[Status], r.[LastAggregatedAtUtc], r.[CompletedAtUtc], r.[SourceProductVersion]
                FROM [dbo].[SalesStatisticRefreshState] r
                WHERE r.[StatisticType] = N'ProductStoreDaily' AND r.[Date] >= @stateStart AND r.[Date] < @stateEndExclusive;

                SELECT DATEADD(month, DATEDIFF(month, 0, s.[Date]), 0) AS [Month],
                       s.[BranchCode], s.[ProductCode], s.[SupplierCode],
                       SUM(s.[TotalQuantity]) AS [TotalQuantity], SUM(s.[TotalAmount]) AS [TotalAmount], MAX(s.[Date]) AS [LastDate]
                FROM [dbo].[ProductStoreDailySalesStatistic] s
                WHERE s.[Date] >= @startDate AND s.[Date] < @endExclusive
                  AND s.[SupplierCode] IN (SELECT CONVERT(nvarchar(50), c.[value]) FROM OPENJSON(@supplierCodes) c)
                  AND DATEADD(month, DATEDIFF(month, 0, s.[Date]), 0) IN (SELECT CONVERT(datetime, m.[value], 112) FROM OPENJSON(@months) m)
                GROUP BY DATEADD(month, DATEDIFF(month, 0, s.[Date]), 0), s.[BranchCode], s.[ProductCode], s.[SupplierCode]
                OPTION (RECOMPILE);

                IF @@TRANCOUNT > 0 COMMIT TRANSACTION;
                SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                THROW;
            END CATCH;
            """;
        command.Parameters.Add("@stateStart", SqlDbType.DateTime).Value = readRange.StartDate.Date;
        command.Parameters.Add("@stateEndExclusive", SqlDbType.DateTime).Value = readRange.EndDate.Date.AddDays(1);
        command.Parameters.Add("@startDate", SqlDbType.DateTime).Value = ranges[0].Start;
        command.Parameters.Add("@endExclusive", SqlDbType.DateTime).Value = ranges[^1].EndExclusive;
        command.Parameters.Add("@supplierCodes", SqlDbType.NVarChar, -1).Value = System.Text.Json.JsonSerializer.Serialize(chinaFamilyCodes);
        command.Parameters.Add("@months", SqlDbType.NVarChar, -1).Value =
            System.Text.Json.JsonSerializer.Serialize(ranges.Select(range => range.Month.ToString("yyyyMMdd")));

        var states = new List<SalesDetailReportStatusSqlRow>();
        var builders = ranges.ToDictionary(range => range.Month, _ => new CompactSalesBoardSegmentBuilder());
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                states.Add(new SalesDetailReportStatusSqlRow
                {
                    Type = SalesStatisticType.ProductStoreDaily,
                    Date = D(reader, 0),
                    Status = S(reader, 1),
                    // 数据库时间不带时区，明确按 UTC 标注，与预检读取一致。
                    LastAggregatedAtUtc = ND(reader, 2),
                    CompletedAtUtc = ND(reader, 3),
                    SourceProductVersion = NS(reader, 4),
                });
            }
            if (!await reader.NextResultAsync())
                throw new InvalidOperationException("看板分片读取缺少事实结果集。");
            while (await reader.ReadAsync())
            {
                if (!builders.TryGetValue(reader.GetDateTime(0), out var builder))
                    continue;
                builder.Add(
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Convert.ToInt32(reader.GetValue(4)),
                    Convert.ToDecimal(reader.GetValue(5)),
                    reader.GetDateTime(6)
                );
            }
            // 消费完剩余结果，让批次里的 COMMIT 执行完毕、错误能抛出来。
            while (await reader.NextResultAsync()) { }
        }

        return (states, ranges.Select(range => builders[range.Month].Build()).ToList());
    }

    /// <summary>其他数据库（测试用 SQLite）或已有外部事务：逐片沿用 SqlSugar 聚合查询，结果与 ranges 一一对应。</summary>
    private async Task<List<CompactSalesBoardSegment>> ReadCompactSalesBoardSegmentFactsAsync(
        List<CompactSalesBoardSegmentRange> ranges,
        List<string> chinaFamilyCodes
    )
    {
        var result = new List<CompactSalesBoardSegment>();
        foreach (var range in ranges)
        {
            var builder = new CompactSalesBoardSegmentBuilder();
            foreach (var row in await ReadCompactSalesBoardStatisticRowsAsync(range.Start, range.EndExclusive, chinaFamilyCodes))
                builder.Add(row.BranchCode, row.ProductCode, row.SupplierCode, row.TotalQuantity, row.TotalAmount, row.LastDate);
            result.Add(builder.Build());
        }
        return result;
    }

    /// <summary>
    /// 把多个分片合并成整段区间的「门店×商品×原始供应商编码」行，等价于整段一次 GROUP BY：
    /// 数量金额相加、最近销售日取最大。键按去尾空格、不区分大小写比较，与 SQL Server 的 CI 排序规则分组一致。
    /// </summary>
    private static List<CompactSalesBoardCubeRow> MergeCompactSalesBoardSegments(IReadOnlyList<CompactSalesBoardSegment> segments)
    {
        var comparer = CompactSalesBoardCodeComparer.Instance;
        var branchIds = new Dictionary<string, int>(comparer);
        var productIds = new Dictionary<string, int>(comparer);
        var supplierIds = new Dictionary<string, int>(comparer);
        var rowIndex = new Dictionary<(int Branch, int Product, int Supplier), int>();
        var rows = new List<CompactSalesBoardCubeRow>(segments.Sum(segment => segment.Cells.Length));

        static int[] MapIds(string[] values, Dictionary<string, int> ids)
        {
            var mapped = new int[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if (!ids.TryGetValue(values[i], out var id))
                {
                    id = ids.Count;
                    ids[values[i]] = id;
                }
                mapped[i] = id;
            }
            return mapped;
        }

        foreach (var segment in segments)
        {
            var branches = MapIds(segment.Branches, branchIds);
            var products = MapIds(segment.Products, productIds);
            var suppliers = MapIds(segment.Suppliers, supplierIds);
            foreach (var cell in segment.Cells)
            {
                var key = (branches[cell.Branch], products[cell.Product], suppliers[cell.Supplier]);
                if (rowIndex.TryGetValue(key, out var index))
                {
                    var row = rows[index];
                    row.TotalQuantity += cell.Quantity;
                    row.TotalAmount += cell.Amount;
                    if (cell.LastDate > row.LastDate)
                        row.LastDate = cell.LastDate;
                    continue;
                }

                rowIndex[key] = rows.Count;
                rows.Add(new CompactSalesBoardCubeRow
                {
                    BranchCode = segment.Branches[cell.Branch],
                    ProductCode = segment.Products[cell.Product],
                    SupplierCode = segment.Suppliers[cell.Supplier],
                    TotalQuantity = cell.Quantity,
                    TotalAmount = cell.Amount,
                    LastDate = cell.LastDate,
                });
            }
        }
        return rows;
    }

    /// <summary>去尾空格后不区分大小写比较，对齐 SQL Server CI 排序规则下 GROUP BY 的分组口径。</summary>
    private sealed class CompactSalesBoardCodeComparer : IEqualityComparer<string>
    {
        public static CompactSalesBoardCodeComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) =>
            string.Equals(x?.TrimEnd(), y?.TrimEnd(), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TrimEnd());
    }
}
