using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React;

public partial class SalesDashboardReactService
{
    private sealed class RevenueSnapshotReadBatch
    {
        public List<RevenueSnapshotRefreshRow> RefreshRows { get; } = new();
        public List<RevenueSnapshotStoreRow> StoreRows { get; } = new();
        public List<RevenueSnapshotHourlyRow> HourlyRows { get; } = new();
        public List<RevenueSnapshotHourlyCoverageRow> HourlyCoverageRows { get; } = new();
        public Dictionary<string, string> ActiveStoreNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 状态、日统计、小时统计和门店目录共用一条命令，减少远程数据库往返。
    /// 事务也放在批次中；所有结果集读完后才返回，避免混入刷新前后的两版数据。
    /// </summary>
    private async Task<RevenueSnapshotReadBatch> ReadRevenueSnapshotBatchAsync(
        DateRangeDto dateRange,
        IReadOnlyCollection<string> branchCodes,
        IReadOnlyCollection<string>? focusBranchCodes,
        bool includeActiveStores,
        bool includeLastDay,
        CancellationToken cancellationToken
    )
    {
        var db = _context.Db;
        var elapsed = Stopwatch.StartNew();
        var ownsTransaction = db.Ado.Transaction == null;
        // 单条报表批次不需要多个活动结果集；独立连接避免 MARS 给远程大结果读取增加等待。
        await using var dedicatedConnection = ownsTransaction
            ? new SqlConnection(new SqlConnectionStringBuilder(db.CurrentConnectionConfig.ConnectionString)
                { MultipleActiveResultSets = false, MinPoolSize = 1 }.ConnectionString)
            : null;
        var connection = (DbConnection?)dedicatedConnection ?? (DbConnection)db.Ado.Connection;
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);
        var openedAt = elapsed.ElapsedMilliseconds;

        var failed = true;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Ado.Transaction as DbTransaction;
            command.CommandTimeout = 8;

            void Parameter(string name, object value, System.Data.DbType type)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                parameter.DbType = type;
                command.Parameters.Add(parameter);
            }

            string Scope(string column, IReadOnlyCollection<string> codes, string prefix)
            {
                if (codes.Count == 0)
                    return string.Empty;
                var names = codes.Select((code, index) =>
                {
                    var name = $"@{prefix}{index}";
                    Parameter(name, code, System.Data.DbType.String);
                    return name;
                });
                return $" AND {column} IN ({string.Join(",", names)})";
            }

            var storeScope = Scope("[BranchCode]", branchCodes, "Branch");
            // focus 只能进一步收窄授权范围；不传 focus 时仍受原分店范围约束。
            var hourlyScope = focusBranchCodes is { Count: 0 }
                ? " AND 1 = 0"
                : storeScope + Scope("[BranchCode]", focusBranchCodes ?? Array.Empty<string>(), "Focus");
            Parameter("@Start", dateRange.StartDate.Date, System.Data.DbType.Date);
            Parameter("@End", dateRange.EndDate.Date.AddDays(1), System.Data.DbType.Date);
            Parameter("@CompareStart", dateRange.CompareStartDate?.Date ?? dateRange.StartDate.Date, System.Data.DbType.Date);
            Parameter("@CompareEnd", dateRange.CompareEndDate?.Date.AddDays(1) ?? dateRange.EndDate.Date.AddDays(1), System.Data.DbType.Date);
            Parameter("@HasCompare", dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue, System.Data.DbType.Boolean);
            // 期间 2/3 是本期、同期的最后一天，只在多日区间且最后一天是今天时取，用于前端把今天对齐到整点。
            Parameter("@LastStart", dateRange.EndDate.Date, System.Data.DbType.Date);
            Parameter("@CompareLastStart", dateRange.CompareEndDate?.Date ?? dateRange.EndDate.Date, System.Data.DbType.Date);
            Parameter("@IncludeLastDay", includeLastDay, System.Data.DbType.Boolean);
            Parameter("@IncludeActiveStores", includeActiveStores, System.Data.DbType.Boolean);
            Parameter("@OwnTransaction", ownsTransaction, System.Data.DbType.Boolean);

            // 只使用数据库已启用的快照；未启用时快速失败，避免范围锁阻塞统计刷新。
            const string period = "(([Date] >= @Start AND [Date] < @End) OR ([Date] >= @CompareStart AND [Date] < @CompareEnd))";
            // 空 FOR JSON 子查询会返回 NULL；先转为 [] 再压缩，保留合法空结果与结果缺失的区别。
            command.CommandText = $"""
                SET NOCOUNT ON;
                IF @OwnTransaction = 1
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND snapshot_isolation_state = 1)
                        SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
                    ELSE
                        THROW 51001, N'统计快照读取尚未启用，暂时无法读取完整报表。', 1;
                    BEGIN TRANSACTION;
                END;
                BEGIN TRY
                    SELECT COMPRESS(COALESCE((SELECT [StatisticType], [Date], [Status], [LastAggregatedAtUtc], [CompletedAtUtc]
                    FROM [dbo].[SalesStatisticRefreshState]
                    WHERE [StatisticType] IN (N'StoreSales', N'HourlySales') AND {period} FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')) AS [Data];
                    SELECT COMPRESS(COALESCE((SELECT [Date], [BranchCode], COALESCE([BranchName], N'') [BranchName], [TotalAmount], [OrderCount]
                    FROM [dbo].[StoreSalesStatistic]
                    WHERE {period}{storeScope} FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')) AS [Data];
                    SELECT COMPRESS(COALESCE((SELECT p.[DateStart] AS [Date], [Hour], [BranchCode], MAX([BranchName]) AS [BranchName],
                        SUM([TotalAmount]) AS [TotalAmount], SUM([OrderCount]) AS [OrderCount], p.[Period]
                    FROM [dbo].[HourlySalesStatistic] h
                    INNER JOIN (VALUES (0, @Start, @End), (1, @CompareStart, @CompareEnd),
                        (2, @LastStart, @End), (3, @CompareLastStart, @CompareEnd)) p([Period], [DateStart], [DateEnd])
                        ON h.[Date] >= p.[DateStart] AND h.[Date] < p.[DateEnd]
                    WHERE (p.[Period] = 0 OR (p.[Period] = 1 AND @HasCompare = 1)
                        OR (p.[Period] = 2 AND @IncludeLastDay = 1) OR (p.[Period] = 3 AND @IncludeLastDay = 1 AND @HasCompare = 1))
                        AND [BranchCode] IS NOT NULL AND [BranchCode] <> N'ALL'{hourlyScope}
                    GROUP BY p.[Period], p.[DateStart], [Hour], [BranchCode] FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')) AS [Data];
                    SELECT COMPRESS(COALESCE((SELECT [StoreCode], COALESCE([StoreName], [StoreCode]) [StoreName]
                    FROM [dbo].[Store]
                    WHERE @IncludeActiveStores = 1 AND [IsActive] = 1 AND [IsDeleted] = 0
                        AND [StoreCode] IS NOT NULL AND [StoreCode] <> N'' FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')) AS [Data];
                    SELECT COMPRESS(COALESCE((SELECT DISTINCT [Date], [BranchCode]
                    FROM [dbo].[HourlySalesStatistic]
                    WHERE [BranchCode] IS NOT NULL AND [BranchCode] <> N'ALL'
                        AND {period}{hourlyScope} FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')) AS [Data];
                    IF @OwnTransaction = 1
                    BEGIN
                        COMMIT TRANSACTION;
                        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                    END;
                END TRY
                BEGIN CATCH
                    IF @OwnTransaction = 1 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                    IF @OwnTransaction = 1 SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                    THROW;
                END CATCH;
                """;

            var result = new RevenueSnapshotReadBatch();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var firstResultAt = elapsed.ElapsedMilliseconds;
            // SQL Server 压缩整个结果集，避免远程 SQL 连接逐包传输数千行日统计。
            result.RefreshRows.AddRange(await ReadCompressedRevenueRowsAsync<RevenueSnapshotRefreshRow>(reader, cancellationToken));
            await NextResult();
            result.StoreRows.AddRange(await ReadCompressedRevenueRowsAsync<RevenueSnapshotStoreRow>(reader, cancellationToken));
            await NextResult();
            result.HourlyRows.AddRange(await ReadCompressedRevenueRowsAsync<RevenueSnapshotHourlyRow>(reader, cancellationToken));
            await NextResult();
            foreach (var row in await ReadCompressedRevenueRowsAsync<RevenueSnapshotStoreDirectoryRow>(reader, cancellationToken))
                if (!string.IsNullOrWhiteSpace(row.StoreCode))
                    result.ActiveStoreNames.TryAdd(row.StoreCode, row.StoreName);
            await NextResult();
            result.HourlyCoverageRows.AddRange(await ReadCompressedRevenueRowsAsync<RevenueSnapshotHourlyCoverageRow>(reader, cancellationToken));

            // NextResult 消费命令尾部的 COMMIT；不能在还没完成发布边界读取时返回。
            while (await reader.NextResultAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) { }
            failed = false;
            _logger.LogInformation(
                "营业额统计批次读取完成：连接 {OpenMs}ms，首结果 {FirstResultMs}ms，读取结果 {ReadMs}ms，共 {TotalMs}ms",
                openedAt, firstResultAt - openedAt, elapsed.ElapsedMilliseconds - firstResultAt, elapsed.ElapsedMilliseconds);
            return result;

            async Task NextResult()
            {
                if (!await reader.NextResultAsync(cancellationToken))
                    throw new InvalidOperationException("营业额统计批次缺少结果集。");
            }
        }
        finally
        {
            // 取消可能绕过 SQL 的 CATCH；关闭自管连接可让连接池回滚未结束的只读事务。
            if (shouldClose || (failed && ownsTransaction))
                await connection.CloseAsync();
        }
    }

    private sealed class RevenueSnapshotStoreDirectoryRow
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
    }

    private static async Task<List<T>> ReadCompressedRevenueRowsAsync<T>(DbDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            throw new InvalidOperationException("营业额统计压缩结果缺失。");
        var bytes = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
        using var compressed = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        // SQL Server FOR JSON 的 nvarchar 内容按 UTF-16LE 压缩，不能按 UTF-8 解释。
        using var jsonReader = new StreamReader(gzip, Encoding.Unicode);
        var json = await jsonReader.ReadToEndAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<T>>(json)
            ?? throw new InvalidOperationException("营业额统计压缩结果无效。");
    }

}
