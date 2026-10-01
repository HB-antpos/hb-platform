using System.Data.Common;
using System.Text.Json;
using DbType = System.Data.DbType;
using C = BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskCatalog;
using Q = BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogSqlServerQuery;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;

public sealed record LegacyRiskScanResult(int Inserted, int Updated, int Retracted, int ImpactsWritten, int RowsRead);

/// <summary>
/// 单店单窗口的异常扫描：读候选行 → 纯函数判定 → 一个事务写回标记与金额。
/// - 读取全部走 IX_EmployeeLogs_StoreCode_OperationTime：操作序列只读索引列；需要详情的操作（删除、改价、折扣、
///   无小票退货、重打印）每店每天几十到一百多条，按主键回表。API 与库之间是公网带宽，回传量控制在每店每窗口几百 KB。
/// - 写回：窗口内重新判定，新命中插入、依据变化更新、不再成立且未核查（确认正常 / 需跟进）的写 RetractedAtUtc 撤回；
///   已核查的标记即使不再成立也保留，核查记录才不会悬空。
/// </summary>
public static class LegacyEmployeeLogRiskScanner
{
    public const int CommandTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>四张表都在才扫描；迁移未执行时调用方跳过并记一次 Warning。</summary>
    public static async Task<bool> SchemaReadyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var command = new LegacyEmployeeLogSqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(N'dbo.LegacyEmployeeLogFlags', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.LegacyEmployeeLogImpacts', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.LegacyEmployeeLogReviews', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.LegacyEmployeeLogReviewHistory', N'U') IS NOT NULL
                    THEN 1 ELSE 0 END;
            """,
            []
        );
        return await Q.WithReaderAsync(connection, command, async reader =>
            await reader.ReadAsync(cancellationToken) && Convert.ToInt32(reader.GetValue(0)) == 1, cancellationToken);
    }

    /// <summary>
    /// 逐个取「比上一个大的最小分店编码」：每次是一次索引查找，几十家店几十次查找，
    /// 避免对 2,000 多万行做 DISTINCT（递归 CTE 里不允许聚合与 TOP，所以在应用侧循环）。
    /// </summary>
    public static async Task<List<string>> GetStoreCodesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var stores = new List<string>();
        var previous = string.Empty;
        for (var guard = 0; guard < 500; guard++)
        {
            var command = new LegacyEmployeeLogSqlCommand(
                $"SELECT TOP (1) l.[StoreCode] FROM {Q.Source} WHERE l.[StoreCode] > @Previous ORDER BY l.[StoreCode];",
                [new LegacyEmployeeLogSqlParameter("@Previous", previous, DbType.AnsiString, 200)]
            );
            var next = await Q.WithReaderAsync(connection, command, async reader =>
                await reader.ReadAsync(cancellationToken) ? Q.NullableString(reader, 0) : null, cancellationToken);
            if (next == null)
            {
                break;
            }
            if (!string.IsNullOrWhiteSpace(next))
            {
                stores.Add(next);
            }
            previous = next;
        }
        return stores;
    }

    public static async Task<LegacyRiskScanResult> ScanAsync(
        DbConnection connection,
        string storeCode,
        DateTime evalFrom,
        DateTime evalTo,
        LegacyEmployeeLogRiskOptions options,
        DateTime nowUtc,
        CancellationToken cancellationToken
    )
    {
        var rows = await FetchAsync(connection, storeCode, evalFrom, evalTo, options, cancellationToken);
        var evaluation = LegacyEmployeeLogRiskEvaluator.Evaluate(new LegacyRiskEvaluationInput(storeCode, evalFrom, evalTo, rows), options);
        var result = await ApplyAsync(connection, storeCode, evalFrom, evalTo, evaluation, nowUtc, cancellationToken);
        return result with { RowsRead = rows.Count };
    }

    internal static LegacyEmployeeLogSqlCommand BuildFetch(
        string storeCode,
        DateTime evalFrom,
        DateTime evalTo,
        LegacyEmployeeLogRiskOptions options
    )
    {
        var margin = LegacyEmployeeLogRiskEvaluator.SequenceMargin(options);
        var parameters = new List<LegacyEmployeeLogSqlParameter>
        {
            new("@Store", storeCode, DbType.AnsiString, 200),
            new("@SeqFrom", evalFrom - margin, DbType.DateTime),
            new("@SeqTo", evalTo + margin, DbType.DateTime),
            // 重打印按「同一天」计次、非营业时段要找当段第一条，所以从评估起点当天零点读起。
            new("@DayFrom", evalFrom.Date, DbType.DateTime),
            new("@EvalTo", evalTo, DbType.DateTime),
            new("@Open", options.BusinessOpen, DbType.Time),
            new("@Close", options.BusinessClose, DbType.Time),
        };
        string List(string prefix, IReadOnlyList<string> values) => string.Join(", ", values.Select((value, index) =>
        {
            var name = $"@{prefix}{index}";
            parameters.Add(new LegacyEmployeeLogSqlParameter(name, value, DbType.AnsiString, 200));
            return name;
        }));
        var sequenceOps = List("Seq", C.SequenceOperations);
        var detailOps = List("Detail", C.DetailOperations);
        parameters.Add(new LegacyEmployeeLogSqlParameter("@Reprint", C.Reprint, DbType.AnsiString, 200));

        var sql = $"""
            SET NOCOUNT ON;
            SELECT l.[Id], l.[DeviceCode], l.[EmployeeId], l.[EmployeeName], l.[Operation], l.[OperationTime]
            FROM {Q.Source}
            WHERE l.[StoreCode] = @Store AND l.[OperationTime] >= @SeqFrom AND l.[OperationTime] < @SeqTo
              AND l.[Operation] IN ({sequenceOps})
            OPTION (RECOMPILE);
            WITH ids AS (
                SELECT l.[Id] FROM {Q.Source}
                WHERE l.[StoreCode] = @Store AND l.[OperationTime] >= @SeqFrom AND l.[OperationTime] < @SeqTo
                  AND l.[Operation] IN ({detailOps})
                UNION
                SELECT l.[Id] FROM {Q.Source}
                WHERE l.[StoreCode] = @Store AND l.[OperationTime] >= @DayFrom AND l.[OperationTime] < @EvalTo
                  AND l.[Operation] = @Reprint
            )
            SELECT x.[Id], x.[DeviceCode], x.[EmployeeId], x.[EmployeeName], x.[Operation], x.[OperationTime], x.[OperationDetail]
            FROM ids JOIN [dbo].[EmployeeLogs] AS x WITH (NOLOCK) ON x.[Id] = ids.[Id]
            OPTION (RECOMPILE);
            SELECT l.[Id], l.[DeviceCode], l.[EmployeeId], l.[EmployeeName], l.[Operation], l.[OperationTime]
            FROM {Q.Source}
            WHERE l.[StoreCode] = @Store AND l.[OperationTime] >= @DayFrom AND l.[OperationTime] < @EvalTo
              AND (CAST(l.[OperationTime] AS time) < @Open OR CAST(l.[OperationTime] AS time) >= @Close)
            OPTION (RECOMPILE);
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    private static async Task<List<LegacyLogRow>> FetchAsync(
        DbConnection connection,
        string storeCode,
        DateTime evalFrom,
        DateTime evalTo,
        LegacyEmployeeLogRiskOptions options,
        CancellationToken cancellationToken
    )
    {
        return await Q.WithReaderAsync(connection, BuildFetch(storeCode, evalFrom, evalTo, options), async reader =>
        {
            // 同一行可能同时出现在序列与详情结果里，带详情的版本优先。
            var rows = new Dictionary<string, LegacyLogRow>(StringComparer.Ordinal);
            async Task ReadAsync(bool withDetail)
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new LegacyLogRow(
                        reader.GetString(0),
                        storeCode,
                        Q.NullableString(reader, 1),
                        Q.NullableString(reader, 2),
                        Q.NullableString(reader, 3),
                        Q.NullableString(reader, 4)?.Trim(),
                        DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Unspecified),
                        withDetail ? Q.NullableString(reader, 6) : null
                    );
                    if (withDetail || !rows.ContainsKey(row.Id))
                    {
                        rows[row.Id] = row;
                    }
                }
            }
            await ReadAsync(withDetail: false);
            await Q.NextResultAsync(reader, cancellationToken);
            await ReadAsync(withDetail: true);
            await Q.NextResultAsync(reader, cancellationToken);
            await ReadAsync(withDetail: false);
            return rows.Values.ToList();
        }, cancellationToken, CommandTimeoutSeconds);
    }

    internal static LegacyEmployeeLogSqlCommand BuildApply(
        string storeCode,
        DateTime evalFrom,
        DateTime evalTo,
        LegacyRiskEvaluation evaluation,
        DateTime nowUtc
    )
    {
        var flags = evaluation.Flags
            .GroupBy(flag => (flag.Target.Id, flag.RuleCode))
            .Select(group => group.First())
            .Select(flag => new
            {
                logId = flag.Target.Id,
                ruleCode = flag.RuleCode,
                deviceCode = flag.Target.DeviceCode,
                employeeId = flag.Target.EmployeeId,
                employeeName = flag.Target.EmployeeName,
                operation = flag.Target.Operation,
                operationTime = flag.Target.OperationTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                evidenceJson = JsonSerializer.Serialize(flag.Evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary()),
            });
        var impacts = evaluation.Impacts
            .DistinctBy(impact => impact.Target.Id)
            .Select(impact => new
            {
                logId = impact.Target.Id,
                employeeId = impact.Target.EmployeeId,
                operation = impact.Target.Operation,
                operationTime = impact.Target.OperationTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                amount = impact.Amount,
            });
        var parameters = new List<LegacyEmployeeLogSqlParameter>
        {
            new("@Store", storeCode, DbType.AnsiString, 200),
            new("@EvalFrom", evalFrom, DbType.DateTime),
            new("@EvalTo", evalTo, DbType.DateTime),
            new("@Now", nowUtc, DbType.DateTime2),
            new("@RuleVersion", C.RuleVersion, DbType.Int32),
            new("@FlagsJson", JsonSerializer.Serialize(flags, JsonOptions), DbType.String, -1),
            new("@ImpactsJson", JsonSerializer.Serialize(impacts, JsonOptions), DbType.String, -1),
        };
        // OPENJSON 的 WITH 子句直接声明成与表一致的 varchar 类型，比较时不让列发生隐式转换。
        const string sql = """
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            DECLARE @Flags TABLE (
                [LogId] varchar(255) NOT NULL, [RuleCode] varchar(32) NOT NULL,
                [DeviceCode] varchar(200) NULL, [EmployeeId] varchar(50) NULL, [EmployeeName] varchar(50) NULL,
                [Operation] varchar(200) NULL, [OperationTime] datetime NOT NULL, [EvidenceJson] nvarchar(2000) NOT NULL,
                PRIMARY KEY ([LogId], [RuleCode])
            );
            INSERT INTO @Flags
            SELECT j.[LogId], j.[RuleCode], j.[DeviceCode], j.[EmployeeId], j.[EmployeeName], j.[Operation], j.[OperationTime], j.[EvidenceJson]
            FROM OPENJSON(@FlagsJson) WITH (
                [LogId] varchar(255) '$.logId', [RuleCode] varchar(32) '$.ruleCode',
                [DeviceCode] varchar(200) '$.deviceCode', [EmployeeId] varchar(50) '$.employeeId', [EmployeeName] varchar(50) '$.employeeName',
                [Operation] varchar(200) '$.operation', [OperationTime] datetime '$.operationTime', [EvidenceJson] nvarchar(2000) '$.evidenceJson'
            ) AS j;
            DECLARE @Impacts TABLE (
                [LogId] varchar(255) NOT NULL PRIMARY KEY, [EmployeeId] varchar(50) NULL, [Operation] varchar(200) NULL,
                [OperationTime] datetime NOT NULL, [Amount] decimal(18,2) NOT NULL
            );
            INSERT INTO @Impacts
            SELECT j.[LogId], j.[EmployeeId], j.[Operation], j.[OperationTime], j.[Amount]
            FROM OPENJSON(@ImpactsJson) WITH (
                [LogId] varchar(255) '$.logId', [EmployeeId] varchar(50) '$.employeeId', [Operation] varchar(200) '$.operation',
                [OperationTime] datetime '$.operationTime', [Amount] decimal(18,2) '$.amount'
            ) AS j;

            DECLARE @Inserted int = 0, @Updated int = 0, @Retracted int = 0, @ImpactsWritten int = 0;
            BEGIN TRANSACTION;
            -- 不再成立且未核查的标记撤回；已核查的（确认正常 / 需跟进）保留。
            UPDATE f SET f.[RetractedAtUtc] = @Now, f.[UpdatedAtUtc] = @Now
            FROM [dbo].[LegacyEmployeeLogFlags] AS f WITH (UPDLOCK)
            WHERE f.[StoreCode] = @Store AND f.[OperationTime] >= @EvalFrom AND f.[OperationTime] < @EvalTo
              AND f.[RetractedAtUtc] IS NULL
              AND NOT EXISTS (SELECT 1 FROM @Flags AS n WHERE n.[LogId] = f.[LogId] AND n.[RuleCode] = f.[RuleCode])
              AND NOT EXISTS (SELECT 1 FROM [dbo].[LegacyEmployeeLogReviews] AS r WHERE r.[LogId] = f.[LogId] AND r.[Result] IN (1, 2));
            SET @Retracted = @@ROWCOUNT;

            UPDATE f SET f.[RuleVersion] = @RuleVersion, f.[DeviceCode] = n.[DeviceCode], f.[EmployeeId] = n.[EmployeeId],
                f.[EmployeeName] = n.[EmployeeName], f.[Operation] = n.[Operation], f.[EvidenceJson] = n.[EvidenceJson],
                f.[RetractedAtUtc] = NULL, f.[UpdatedAtUtc] = @Now
            FROM [dbo].[LegacyEmployeeLogFlags] AS f WITH (UPDLOCK)
            JOIN @Flags AS n ON n.[LogId] = f.[LogId] AND n.[RuleCode] = f.[RuleCode]
            WHERE f.[RetractedAtUtc] IS NOT NULL OR f.[RuleVersion] <> @RuleVersion OR f.[EvidenceJson] <> n.[EvidenceJson];
            SET @Updated = @@ROWCOUNT;

            INSERT INTO [dbo].[LegacyEmployeeLogFlags]
                ([LogId], [RuleCode], [RuleVersion], [StoreCode], [DeviceCode], [EmployeeId], [EmployeeName], [Operation],
                 [OperationTime], [EvidenceJson], [DetectedAtUtc], [UpdatedAtUtc], [RetractedAtUtc])
            SELECT n.[LogId], n.[RuleCode], @RuleVersion, @Store, n.[DeviceCode], n.[EmployeeId], n.[EmployeeName], n.[Operation],
                   n.[OperationTime], n.[EvidenceJson], @Now, @Now, NULL
            FROM @Flags AS n
            WHERE NOT EXISTS (
                SELECT 1 FROM [dbo].[LegacyEmployeeLogFlags] AS f WITH (UPDLOCK, HOLDLOCK)
                WHERE f.[LogId] = n.[LogId] AND f.[RuleCode] = n.[RuleCode]);
            SET @Inserted = @@ROWCOUNT;

            UPDATE i SET i.[Amount] = n.[Amount], i.[EmployeeId] = n.[EmployeeId], i.[Operation] = n.[Operation], i.[UpdatedAtUtc] = @Now
            FROM [dbo].[LegacyEmployeeLogImpacts] AS i WITH (UPDLOCK)
            JOIN @Impacts AS n ON n.[LogId] = i.[LogId]
            WHERE i.[Amount] <> n.[Amount];
            SET @ImpactsWritten = @@ROWCOUNT;
            INSERT INTO [dbo].[LegacyEmployeeLogImpacts] ([LogId], [StoreCode], [EmployeeId], [Operation], [OperationTime], [Amount], [UpdatedAtUtc])
            SELECT n.[LogId], @Store, n.[EmployeeId], n.[Operation], n.[OperationTime], n.[Amount], @Now
            FROM @Impacts AS n
            WHERE NOT EXISTS (SELECT 1 FROM [dbo].[LegacyEmployeeLogImpacts] AS i WITH (UPDLOCK, HOLDLOCK) WHERE i.[LogId] = n.[LogId]);
            SET @ImpactsWritten += @@ROWCOUNT;
            COMMIT TRANSACTION;
            SELECT @Inserted, @Updated, @Retracted, @ImpactsWritten;
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    private static async Task<LegacyRiskScanResult> ApplyAsync(
        DbConnection connection,
        string storeCode,
        DateTime evalFrom,
        DateTime evalTo,
        LegacyRiskEvaluation evaluation,
        DateTime nowUtc,
        CancellationToken cancellationToken
    )
    {
        return await Q.WithReaderAsync(connection, BuildApply(storeCode, evalFrom, evalTo, evaluation, nowUtc), async reader =>
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("异常标记写入没有返回计数。");
            }
            return new LegacyRiskScanResult(
                Convert.ToInt32(reader.GetValue(0)),
                Convert.ToInt32(reader.GetValue(1)),
                Convert.ToInt32(reader.GetValue(2)),
                Convert.ToInt32(reader.GetValue(3)),
                0
            );
        }, cancellationToken, CommandTimeoutSeconds);
    }
}
