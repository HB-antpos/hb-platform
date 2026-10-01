using System.Data;
using System.Data.Common;
using System.Text;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
// 项目全局引用了 SqlSugar，其 DbType 与 ADO.NET 参数类型同名。
using DbType = System.Data.DbType;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs;

/// <summary>校验与归一化后的查询条件；分店（至少一个）与时间范围必填。</summary>
public sealed record LegacyEmployeeLogNormalizedQuery(
    IReadOnlyList<string> StoreCodes,
    DateTime From,
    DateTime ToExclusive,
    string? DeviceCode,
    IReadOnlyList<string> EmployeeIds,
    IReadOnlyList<string> Operations,
    string? Keyword,
    int PageNumber,
    int PageSize,
    bool Descending,
    string RiskLens = LegacyEmployeeLogSqlServerQuery.LensAll,
    IReadOnlyList<string>? RuleCodes = null,
    string ReviewStatus = LegacyEmployeeLogSqlServerQuery.ReviewAll
)
{
    public IReadOnlyList<string> RuleCodeList => RuleCodes ?? [];
}

public sealed record LegacyEmployeeLogSqlParameter(string Name, object? Value, DbType DbType, int? Size = null);

public sealed record LegacyEmployeeLogSqlCommand(string Sql, IReadOnlyList<LegacyEmployeeLogSqlParameter> Parameters);

/// <summary>列表批处理的读取结果；RejectedScanRows 非空表示关键字扫描范围超限，其余集合为空。</summary>
public sealed record LegacyEmployeeLogListPage(
    List<LegacyEmployeeLogOperationCountDto> Counts,
    List<LegacyEmployeeLogItemDto> Rows,
    List<LegacyEmployeeLogEmployeeOptionDto> Employees,
    List<LegacyEmployeeLogDeviceOptionDto> Devices,
    long? RejectedScanRows,
    long? LensTotal = null
)
{
    public static LegacyEmployeeLogListPage KeywordRejected(long scanRows) => new([], [], [], [], scanRows);
}

/// <summary>
/// POSM.dbo.EmployeeLogs（旧版收银上传的员工操作日志）的 SQL Server 查询。
/// 生产事实（2026-09-30 只读核验）：约 2,220 万行、6.5 GB，每天约 5 万行；原表只有随机 GUID 聚集主键，
/// 按分店或时间过滤都会扫全表，所以必须配合 IX_EmployeeLogs_StoreCode_OperationTime（见 SqlScripts/PosmEmployeeLogsStoreTimeIndex.sql）。
/// - 语句显式指定该索引：索引缺失时直接报错，而不是悄悄退化成 6.5 GB 的全表扫描压垮线上库。
/// - 列全是 varchar，字符串参数一律按 varchar 传，避免 nvarchar 参数让列发生隐式转换。
/// - OperationTime 是门店本地墙钟时间，边界直接比较，不做时区换算。
/// </summary>
public static class LegacyEmployeeLogSqlServerQuery
{
    public const string IndexName = "IX_EmployeeLogs_StoreCode_OperationTime";
    public const int MaxRangeDays = 31;
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;
    public const int MaxEmployeeFilters = 50;
    public const int MaxOperationFilters = 64;
    public const int MaxKeywordLength = 100;
    public const int ContextWindowMinutes = 5;
    /// <summary>详情「前后操作」可选的窗口；异常核查常要看更长的上下文（例如开钱箱前最近一次结账）。</summary>
    public static readonly IReadOnlySet<int> AllowedContextWindows = new HashSet<int> { 5, 10, 15 };
    public const int ContextRowLimit = 200;
    public const int MaxStoreFilters = 64;

    public const string LensAll = "all";
    public const string LensDanger = "danger";
    public const string LensAbnormal = "abnormal";
    public const string ReviewAll = "all";
    public const string ReviewPending = "pending";
    public const string ReviewReviewed = "reviewed";
    public const string ReviewFollowUp = "followUp";
    private static readonly string[] Lenses = [LensAll, LensDanger, LensAbnormal];
    private static readonly string[] ReviewStatuses = [ReviewAll, ReviewPending, ReviewReviewed, ReviewFollowUp];

    /// <summary>核查结论在库里的取值：0 已撤销（视同待核查）、1 确认正常、2 需跟进。</summary>
    public const byte ReviewRevoked = 0;
    public const byte ReviewNormal = 1;
    public const byte ReviewNeedsFollowUp = 2;

    /// <summary>
    /// 带详情关键字时允许扫描的最大行数。关键字要逐行回表读 OperationDetail（包含列里没有它），
    /// 2026-09-30 生产实测 25 店 7 天约 37 万行冷读要 10 秒以上；单店 31 天约 12 万行热读 3 秒。
    /// 超过上限直接提示缩小范围，不让一次搜索长时间压住线上库。
    /// </summary>
    public const int DefaultKeywordScanRowLimit = 200_000;

    private const string Columns =
        "l.[Id], l.[EmployeeId], l.[EmployeeName], l.[Operation], l.[OperationDetail], l.[OperationTime], l.[DeviceCode], l.[StoreCode], l.[LastUploadTime]";

    private const string PageColumns =
        "x.[Id], x.[EmployeeId], x.[EmployeeName], x.[Operation], x.[OperationDetail], x.[OperationTime], x.[DeviceCode], x.[StoreCode], x.[LastUploadTime]";

    // 与索引定义保持一致：键 (StoreCode, OperationTime)，包含列覆盖汇总与下拉选项，详情和上传时间回表读取。
    internal static readonly string Source = $"[dbo].[EmployeeLogs] AS l WITH (NOLOCK, INDEX([{IndexName}]))";

    /// <summary>校验请求；返回错误文案或归一化后的条件（二者恰有一个非空）。</summary>
    public static (LegacyEmployeeLogNormalizedQuery? Query, string? Error) Normalize(LegacyEmployeeLogQueryDto request)
    {
        // StoreCode 是单店旧参数，与 StoreCodes 合并后去重。
        var storeCodes = NormalizeList((request.StoreCodes ?? []).Append(request.StoreCode ?? string.Empty));
        if (storeCodes.Count == 0)
        {
            return (null, "请选择分店");
        }
        if (storeCodes.Count > MaxStoreFilters || storeCodes.Any(code => code.Length > 200))
        {
            return (null, $"分店条件无效（最多 {MaxStoreFilters} 个）");
        }
        if (request.From is not { } from || request.To is not { } to)
        {
            return (null, "请选择操作时间范围");
        }
        // 前端传的是墙钟时间；即便带了时区标记也只取字面值，与库内口径一致。
        from = DateTime.SpecifyKind(from, DateTimeKind.Unspecified);
        to = DateTime.SpecifyKind(to, DateTimeKind.Unspecified);
        if (to <= from)
        {
            return (null, "结束时间必须晚于开始时间");
        }
        if (to - from > TimeSpan.FromDays(MaxRangeDays))
        {
            return (null, $"时间范围最长 {MaxRangeDays} 天");
        }

        // 超长值直接拒绝而不是丢弃：丢弃会让筛选条件变宽，返回比用户要求更多的记录。
        var employeeIds = NormalizeList(request.EmployeeIds);
        if (employeeIds.Count > MaxEmployeeFilters || employeeIds.Any(id => id.Length > 50))
        {
            return (null, $"员工条件无效（最多 {MaxEmployeeFilters} 个）");
        }
        var operations = NormalizeList(request.Operations);
        if (operations.Count > MaxOperationFilters || operations.Any(operation => operation.Length > 200))
        {
            return (null, $"操作类型条件无效（最多 {MaxOperationFilters} 个）");
        }
        var keyword = TrimToNull(request.Keyword);
        if (keyword is { Length: > MaxKeywordLength })
        {
            return (null, $"关键字最长 {MaxKeywordLength} 个字符");
        }
        var deviceCode = TrimToNull(request.DeviceCode);
        if (deviceCode is { Length: > 200 })
        {
            return (null, "设备编码无效");
        }

        var pageSize = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);
        // 页码上限保证 OFFSET 不会溢出 int。
        var pageNumber = Math.Clamp(request.PageNumber, 1, int.MaxValue / MaxPageSize);
        var descending = !string.Equals(TrimToNull(request.SortOrder), "asc", StringComparison.OrdinalIgnoreCase);

        // 风险入口与规则都是封闭取值；未知值直接拒绝，不静默退回「全部」放宽结果。
        var lens = Lenses.FirstOrDefault(value => string.Equals(value, TrimToNull(request.RiskLens) ?? LensAll, StringComparison.OrdinalIgnoreCase));
        if (lens == null)
        {
            return (null, "风险入口无效");
        }
        var ruleCodes = new List<string>();
        foreach (var code in NormalizeList(request.RuleCodes))
        {
            var known = LegacyEmployeeLogRiskCatalog.AllRules.FirstOrDefault(rule => string.Equals(rule, code, StringComparison.OrdinalIgnoreCase));
            if (known == null)
            {
                return (null, "异常规则条件无效");
            }
            ruleCodes.Add(known);
        }
        var reviewStatus = ReviewStatuses.FirstOrDefault(value => string.Equals(value, TrimToNull(request.ReviewStatus) ?? ReviewAll, StringComparison.OrdinalIgnoreCase));
        if (reviewStatus == null)
        {
            return (null, "核查状态条件无效");
        }
        return (
            new LegacyEmployeeLogNormalizedQuery(
                storeCodes, from, to, deviceCode, employeeIds, operations, keyword, pageNumber, pageSize, descending,
                lens, ruleCodes, reviewStatus),
            null
        );
    }

    /// <summary>
    /// 一次往返返回：[关键字守卫行数]、按操作类型计数（不含操作类型条件）、当前页、员工选项、设备选项。
    /// 总数由服务层用计数结果按操作类型条件累加，省掉一次 COUNT。
    /// - 当前页先只从索引取「时间 + 主键」排好序分页，再按主键回表取详情。多店时索引顺序不再等于时间顺序，
    ///   直接带着详情列排序会先把范围内每一行都回表（生产 25 店 31 天 165 万行要 30 秒），先分页后回表只要 0.6 秒。
    /// - 带关键字时先数一遍要扫描的行数，超过上限只返回守卫结果集；未超限时把命中行落到临时表，
    ///   计数和分页共用，逐行回表的 LIKE 只做一次。
    /// </summary>
    public static LegacyEmployeeLogSqlCommand BuildList(
        LegacyEmployeeLogNormalizedQuery query,
        int keywordScanRowLimit = DefaultKeywordScanRowLimit
    )
    {
        var parameters = new List<LegacyEmployeeLogSqlParameter>();
        string Param(string name, object? value, DbType dbType, int? size = null)
        {
            parameters.Add(new LegacyEmployeeLogSqlParameter(name, value, dbType, size));
            return name;
        }

        var storeNames = query.StoreCodes.Select((code, index) => Param($"@Store{index}", code, DbType.AnsiString, 200));
        var scope = new List<string>
        {
            $"l.[StoreCode] IN ({string.Join(", ", storeNames)})",
            $"l.[OperationTime] >= {Param("@From", query.From, DbType.DateTime)}",
            $"l.[OperationTime] < {Param("@ToExclusive", query.ToExclusive, DbType.DateTime)}",
        };

        var filters = new List<string>(scope);
        if (query.DeviceCode != null)
        {
            filters.Add($"l.[DeviceCode] = {Param("@DeviceCode", query.DeviceCode, DbType.AnsiString, 200)}");
        }
        if (query.EmployeeIds.Count > 0)
        {
            var names = query.EmployeeIds.Select((id, index) => Param($"@Employee{index}", id, DbType.AnsiString, 50));
            filters.Add($"l.[EmployeeId] IN ({string.Join(", ", names)})");
        }

        string? operationList = null;
        if (query.Operations.Count > 0)
        {
            operationList = string.Join(", ", query.Operations.Select((operation, index) => Param($"@Operation{index}", operation, DbType.AnsiString, 200)));
        }

        Param("@Offset", (query.PageNumber - 1) * query.PageSize, DbType.Int32);
        Param("@PageSize", query.PageSize, DbType.Int32);
        var direction = query.Descending ? "DESC" : "ASC";
        var where = string.Join("\n  AND ", filters);
        // 异常入口只作用于当前页与总数；按操作类型计数、员工 / 设备选项保持原口径。
        // 标记表很小，与索引范围做半连接，代价与按操作类型计数同量级。
        var abnormal = query.RiskLens == LensAbnormal;
        var abnormalOn = (string idColumn) => abnormal ? AbnormalPredicate(idColumn, query, parameters) : null;

        var sql = new StringBuilder();
        sql.AppendLine("SET NOCOUNT ON;");
        if (abnormal)
        {
            sql.AppendLine("DECLARE @LensCount TABLE ([Rows] bigint NOT NULL);");
        }
        // 时间范围 1–31 天、分店数量差别很大，统一 RECOMPILE，避免小范围编译的计划被大范围复用。
        if (query.Keyword != null)
        {
            var hitConditions = new List<string>();
            if (operationList != null)
            {
                hitConditions.Add($"h.[Operation] IN ({operationList})");
            }
            if (abnormalOn("h.[Id]") is { } hitAbnormal)
            {
                hitConditions.Add(hitAbnormal);
            }
            var hitWhere = hitConditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", hitConditions);
            Param("@ScanLimit", (long)keywordScanRowLimit, DbType.Int64);
            var pattern = LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(query.Keyword);
            Param("@KeywordPattern", pattern, DbType.AnsiString, 400);
            // 守卫计数先写表变量再取值：生产实测 `SELECT @ScanRows = COUNT_BIG(*) …` 这种变量赋值写法，
            // 25 店的 IN 列表要 11 秒以上（与时间范围无关，加 FORCESEEK 也一样），写入表变量只要 0.3 秒。
            // 临时表建在 tempdb，字符列必须显式跟随当前库排序规则，否则与 POSM 列比较会报排序规则冲突。
            sql.AppendLine($"""
                DECLARE @ScanCount TABLE ([Rows] bigint NOT NULL);
                INSERT INTO @ScanCount ([Rows]) SELECT COUNT_BIG(*) FROM {Source} WHERE {where} OPTION (RECOMPILE);
                DECLARE @ScanRows bigint = (SELECT [Rows] FROM @ScanCount);
                SELECT @ScanRows AS [ScanRows];
                IF @ScanRows > @ScanLimit RETURN;
                IF OBJECT_ID(N'tempdb..#hb_legacy_log_hits') IS NOT NULL DROP TABLE #hb_legacy_log_hits;
                CREATE TABLE #hb_legacy_log_hits (
                    [OperationTime] datetime NOT NULL,
                    [Id] varchar(255) COLLATE DATABASE_DEFAULT NOT NULL,
                    [Operation] varchar(200) COLLATE DATABASE_DEFAULT NULL,
                    PRIMARY KEY CLUSTERED ([OperationTime], [Id])
                );
                INSERT INTO #hb_legacy_log_hits ([OperationTime], [Id], [Operation])
                SELECT l.[OperationTime], l.[Id], l.[Operation]
                FROM {Source}
                WHERE {where}
                  AND l.[OperationDetail] LIKE @KeywordPattern
                OPTION (RECOMPILE);
                SELECT h.[Operation], COUNT_BIG(*) AS [Count]
                FROM #hb_legacy_log_hits AS h
                GROUP BY h.[Operation];
                {(abnormal ? $"INSERT INTO @LensCount ([Rows]) SELECT COUNT_BIG(*) FROM #hb_legacy_log_hits AS h {hitWhere};" : string.Empty)}
                WITH p AS (
                    SELECT h.[Id], h.[OperationTime]
                    FROM #hb_legacy_log_hits AS h
                    {hitWhere}
                    ORDER BY h.[OperationTime] {direction}, h.[Id] {direction}
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
                )
                SELECT {PageColumns}
                FROM p
                JOIN [dbo].[EmployeeLogs] AS x WITH (NOLOCK) ON x.[Id] = p.[Id]
                ORDER BY p.[OperationTime] {direction}, p.[Id] {direction};
                DROP TABLE #hb_legacy_log_hits;
                """);
        }
        else
        {
            var listWhere = operationList == null ? where : $"{where}\n  AND l.[Operation] IN ({operationList})";
            if (abnormalOn("l.[Id]") is { } listAbnormal)
            {
                listWhere = $"{listWhere}\n  AND {listAbnormal}";
                sql.AppendLine($"INSERT INTO @LensCount ([Rows]) SELECT COUNT_BIG(*) FROM {Source} WHERE {listWhere} OPTION (RECOMPILE);");
            }
            sql.AppendLine($"""
                SELECT l.[Operation], COUNT_BIG(*) AS [Count]
                FROM {Source}
                WHERE {where}
                GROUP BY l.[Operation]
                OPTION (RECOMPILE);
                WITH p AS (
                    SELECT l.[Id], l.[OperationTime]
                    FROM {Source}
                    WHERE {listWhere}
                    ORDER BY l.[OperationTime] {direction}, l.[Id] {direction}
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
                )
                SELECT {PageColumns}
                FROM p
                JOIN [dbo].[EmployeeLogs] AS x WITH (NOLOCK) ON x.[Id] = p.[Id]
                ORDER BY p.[OperationTime] {direction}, p.[Id] {direction}
                OPTION (RECOMPILE);
                """);
        }
        sql.AppendLine($"""
            SELECT l.[EmployeeId], MAX(l.[EmployeeName]) AS [EmployeeName], COUNT_BIG(*) AS [Count]
            FROM {Source}
            WHERE {string.Join("\n  AND ", scope)}
            GROUP BY l.[EmployeeId]
            OPTION (RECOMPILE);
            SELECT l.[DeviceCode], COUNT_BIG(*) AS [Count]
            FROM {Source}
            WHERE {string.Join("\n  AND ", scope)}
            GROUP BY l.[DeviceCode]
            OPTION (RECOMPILE);
            """);
        if (abnormal)
        {
            // 放在最后一个结果集，前面的结果集顺序与「全部 / 危险」入口一致。
            sql.AppendLine("SELECT [Rows] FROM @LensCount;");
        }
        return new LegacyEmployeeLogSqlCommand(sql.ToString(), parameters);
    }

    /// <summary>
    /// 异常入口的行过滤：存在未撤回的规则命中（可限定规则），再按核查状态收窄。
    /// 待核查 = 没有「确认正常 / 需跟进」结论（撤销过的回到待核查）。
    /// </summary>
    private static string AbnormalPredicate(
        string idColumn,
        LegacyEmployeeLogNormalizedQuery query,
        List<LegacyEmployeeLogSqlParameter> parameters
    )
    {
        var flagFilter = "f.[RetractedAtUtc] IS NULL";
        if (query.RuleCodeList.Count > 0)
        {
            var names = query.RuleCodeList.Select((code, index) =>
            {
                var name = $"@Rule{index}";
                parameters.Add(new LegacyEmployeeLogSqlParameter(name, code, DbType.AnsiString, 32));
                return name;
            });
            flagFilter += $" AND f.[RuleCode] IN ({string.Join(", ", names)})";
        }
        var flagged = $"EXISTS (SELECT 1 FROM [dbo].[LegacyEmployeeLogFlags] AS f WITH (NOLOCK) WHERE f.[LogId] = {idColumn} AND {flagFilter})";
        string Reviewed(string results) =>
            $"EXISTS (SELECT 1 FROM [dbo].[LegacyEmployeeLogReviews] AS r WITH (NOLOCK) WHERE r.[LogId] = {idColumn} AND r.[Result] IN ({results}))";
        return query.ReviewStatus switch
        {
            ReviewPending => $"{flagged} AND NOT {Reviewed("1, 2")}",
            ReviewReviewed => $"{flagged} AND {Reviewed("1, 2")}",
            ReviewFollowUp => $"{flagged} AND {Reviewed("2")}",
            _ => flagged,
        };
    }

    /// <summary>按主键取单条（聚集索引点查，不需要时间索引）。</summary>
    public static LegacyEmployeeLogSqlCommand BuildTarget(string id) =>
        new(
            $"SELECT TOP (1) {Columns} FROM [dbo].[EmployeeLogs] AS l WITH (NOLOCK) WHERE l.[Id] = @Id;",
            [new LegacyEmployeeLogSqlParameter("@Id", id, DbType.AnsiString, 255)]
        );

    /// <summary>同分店、同设备在目标时间前后窗口内的操作，按时间升序，多取一条用于判断是否截断。</summary>
    public static LegacyEmployeeLogSqlCommand BuildNeighbors(LegacyEmployeeLogItemDto target, int windowMinutes = ContextWindowMinutes)
    {
        var parameters = new List<LegacyEmployeeLogSqlParameter>
        {
            new("@StoreCode", target.StoreCode, DbType.AnsiString, 200),
            new("@From", target.OperationTime.AddMinutes(-windowMinutes), DbType.DateTime),
            new("@To", target.OperationTime.AddMinutes(windowMinutes), DbType.DateTime),
            new("@Limit", ContextRowLimit + 1, DbType.Int32),
        };
        var deviceFilter = "l.[DeviceCode] IS NULL";
        if (target.DeviceCode != null)
        {
            parameters.Add(new LegacyEmployeeLogSqlParameter("@DeviceCode", target.DeviceCode, DbType.AnsiString, 200));
            deviceFilter = "l.[DeviceCode] = @DeviceCode";
        }
        return new LegacyEmployeeLogSqlCommand(
            $"""
            SELECT TOP (@Limit) {Columns}
            FROM {Source}
            WHERE l.[StoreCode] = @StoreCode
              AND l.[OperationTime] >= @From
              AND l.[OperationTime] <= @To
              AND {deviceFilter}
            ORDER BY l.[OperationTime] ASC, l.[Id] ASC;
            """,
            parameters
        );
    }

    /// <summary>
    /// 异常入口计数：与按操作类型计数同口径（分店、时间、设备、员工、关键字），不受操作类型、规则、核查状态影响。
    /// 从标记表出发（按分店 + 时间索引），关键字只对命中的少量记录回表比对。
    /// </summary>
    public static LegacyEmployeeLogSqlCommand BuildRiskSummary(LegacyEmployeeLogNormalizedQuery query)
    {
        var parameters = new List<LegacyEmployeeLogSqlParameter>();
        var conditions = FlagScope(query.StoreCodes, query.From, query.ToExclusive, query.DeviceCode, parameters);
        if (query.EmployeeIds.Count > 0)
        {
            var names = query.EmployeeIds.Select((id, index) => AddParam(parameters, $"@Employee{index}", id, DbType.AnsiString, 50));
            conditions.Add($"f.[EmployeeId] IN ({string.Join(", ", names)})");
        }
        if (query.Keyword != null)
        {
            AddParam(parameters, "@KeywordPattern", LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(query.Keyword), DbType.AnsiString, 400);
            conditions.Add("EXISTS (SELECT 1 FROM [dbo].[EmployeeLogs] AS x WITH (NOLOCK) WHERE x.[Id] = f.[LogId] AND x.[OperationDetail] LIKE @KeywordPattern)");
        }
        var sql = $"""
            SET NOCOUNT ON;
            DECLARE @fl TABLE ([LogId] varchar(255) NOT NULL, [RuleCode] varchar(32) NOT NULL, [EmployeeKey] varchar(50) NULL);
            INSERT INTO @fl ([LogId], [RuleCode], [EmployeeKey])
            SELECT f.[LogId], f.[RuleCode], COALESCE(f.[EmployeeId], f.[EmployeeName])
            FROM [dbo].[LegacyEmployeeLogFlags] AS f WITH (NOLOCK)
            WHERE {string.Join("\n  AND ", conditions)}
            OPTION (RECOMPILE);
            SELECT fl.[RuleCode], COUNT(DISTINCT fl.[LogId]) FROM @fl AS fl GROUP BY fl.[RuleCode];
            SELECT COUNT(DISTINCT fl.[LogId]),
                   COUNT(DISTINCT CASE WHEN r.[LogId] IS NULL THEN fl.[LogId] END),
                   COUNT(DISTINCT fl.[EmployeeKey])
            FROM @fl AS fl
            LEFT JOIN [dbo].[LegacyEmployeeLogReviews] AS r WITH (NOLOCK) ON r.[LogId] = fl.[LogId] AND r.[Result] IN (1, 2);
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    public static async Task<(List<LegacyEmployeeLogRuleCountDto> ByRule, int Total, int Pending, int Employees)> ExecuteRiskSummaryAsync(
        DbConnection connection,
        LegacyEmployeeLogSqlCommand command,
        CancellationToken cancellationToken = default
    ) => await WithReaderAsync(connection, command, async reader =>
    {
        var byRule = new List<LegacyEmployeeLogRuleCountDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            byRule.Add(new LegacyEmployeeLogRuleCountDto { RuleCode = reader.GetString(0), Count = Convert.ToInt32(reader.GetValue(1)) });
        }
        await NextResultAsync(reader, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (byRule, 0, 0, 0);
        }
        return (byRule, Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)), Convert.ToInt32(reader.GetValue(2)));
    }, cancellationToken);

    /// <summary>当前页（或上下文）记录的规则命中、核查结论与金额；编号最多约 200 个。</summary>
    public static LegacyEmployeeLogSqlCommand BuildEnrichment(IReadOnlyCollection<string> ids)
    {
        var parameters = new List<LegacyEmployeeLogSqlParameter>();
        var names = string.Join(", ", ids.Select((id, index) => AddParam(parameters, $"@Id{index}", id, DbType.AnsiString, 255)));
        var sql = $"""
            SET NOCOUNT ON;
            SELECT f.[LogId], f.[RuleCode], f.[EvidenceJson], f.[DetectedAtUtc]
            FROM [dbo].[LegacyEmployeeLogFlags] AS f WITH (NOLOCK)
            WHERE f.[LogId] IN ({names}) AND f.[RetractedAtUtc] IS NULL;
            SELECT r.[LogId], r.[Result], r.[Note], r.[ReviewedByName], r.[ReviewedAtUtc], r.[Version]
            FROM [dbo].[LegacyEmployeeLogReviews] AS r WITH (NOLOCK)
            WHERE r.[LogId] IN ({names});
            SELECT i.[LogId], i.[Amount]
            FROM [dbo].[LegacyEmployeeLogImpacts] AS i WITH (NOLOCK)
            WHERE i.[LogId] IN ({names});
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    /// <summary>把风险信息补到记录上；IsDanger 只看操作类型，不依赖扫描任务。</summary>
    public static async Task EnrichAsync(
        DbConnection connection,
        IReadOnlyList<LegacyEmployeeLogItemDto> items,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var item in items)
        {
            item.IsDanger = LegacyEmployeeLogRiskCatalog.IsDanger(item.Operation);
        }
        var ids = items.Select(item => item.Id).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        var byId = items.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        await WithReaderAsync(connection, BuildEnrichment(ids), async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var flag = new LegacyEmployeeLogFlagDto
                {
                    RuleCode = reader.GetString(1),
                    Evidence = ParseEvidence(reader.GetString(2)),
                    DetectedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                };
                foreach (var item in byId.GetValueOrDefault(reader.GetString(0)) ?? [])
                {
                    item.Flags.Add(flag);
                }
            }
            await NextResultAsync(reader, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var review = ReadReview(reader, 1);
                foreach (var item in byId.GetValueOrDefault(reader.GetString(0)) ?? [])
                {
                    item.Review = review;
                }
            }
            await NextResultAsync(reader, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var amount = reader.GetDecimal(1);
                foreach (var item in byId.GetValueOrDefault(reader.GetString(0)) ?? [])
                {
                    item.AmountImpact = amount;
                }
            }
            return true;
        }, cancellationToken);
        foreach (var item in items)
        {
            item.Flags = item.Flags
                .OrderBy(flag => LegacyEmployeeLogRiskCatalog.AllRules.ToList().IndexOf(flag.RuleCode))
                .ToList();
        }
    }

    /// <summary>
    /// 按员工汇总：操作数与危险数来自日志索引（不回表），异常与待核查来自标记表，金额来自金额表。
    /// 金额表没有设备列，选了设备时按主键回表核对设备（金额表每店每天只有几十行）。
    /// </summary>
    public static LegacyEmployeeLogSqlCommand BuildEmployeeSummary(LegacyEmployeeLogNormalizedQuery query)
    {
        var parameters = new List<LegacyEmployeeLogSqlParameter>();
        var stores = string.Join(", ", query.StoreCodes.Select((code, index) => AddParam(parameters, $"@Store{index}", code, DbType.AnsiString, 200)));
        AddParam(parameters, "@From", query.From, DbType.DateTime);
        AddParam(parameters, "@ToExclusive", query.ToExclusive, DbType.DateTime);
        var logScope = $"l.[StoreCode] IN ({stores}) AND l.[OperationTime] >= @From AND l.[OperationTime] < @ToExclusive";
        var flagScope = $"f.[StoreCode] IN ({stores}) AND f.[OperationTime] >= @From AND f.[OperationTime] < @ToExclusive AND f.[RetractedAtUtc] IS NULL";
        var impactScope = $"i.[StoreCode] IN ({stores}) AND i.[OperationTime] >= @From AND i.[OperationTime] < @ToExclusive";
        if (query.DeviceCode != null)
        {
            AddParam(parameters, "@DeviceCode", query.DeviceCode, DbType.AnsiString, 200);
            logScope += " AND l.[DeviceCode] = @DeviceCode";
            flagScope += " AND f.[DeviceCode] = @DeviceCode";
            impactScope += " AND EXISTS (SELECT 1 FROM [dbo].[EmployeeLogs] AS x WITH (NOLOCK) WHERE x.[Id] = i.[LogId] AND x.[DeviceCode] = @DeviceCode)";
        }
        var danger = string.Join(", ", LegacyEmployeeLogRiskCatalog.DangerOperations.Select((operation, index) =>
            AddParam(parameters, $"@Danger{index}", operation, DbType.AnsiString, 200)));
        var sql = $"""
            SET NOCOUNT ON;
            SELECT l.[EmployeeId], MAX(l.[EmployeeName]), COUNT_BIG(*),
                   SUM(CASE WHEN l.[Operation] IN ({danger}) THEN 1 ELSE 0 END)
            FROM {Source}
            WHERE {logScope}
            GROUP BY l.[EmployeeId]
            OPTION (RECOMPILE);
            SELECT l.[EmployeeId], l.[StoreCode], l.[DeviceCode]
            FROM {Source}
            WHERE {logScope}
            GROUP BY l.[EmployeeId], l.[StoreCode], l.[DeviceCode]
            OPTION (RECOMPILE);
            DECLARE @fl TABLE ([LogId] varchar(255) NOT NULL, [RuleCode] varchar(32) NOT NULL, [EmployeeId] varchar(50) NULL);
            INSERT INTO @fl SELECT f.[LogId], f.[RuleCode], f.[EmployeeId]
            FROM [dbo].[LegacyEmployeeLogFlags] AS f WITH (NOLOCK)
            WHERE {flagScope}
            OPTION (RECOMPILE);
            SELECT fl.[EmployeeId], fl.[RuleCode], COUNT(DISTINCT fl.[LogId]) FROM @fl AS fl GROUP BY fl.[EmployeeId], fl.[RuleCode];
            SELECT fl.[EmployeeId], COUNT(DISTINCT fl.[LogId]), COUNT(DISTINCT CASE WHEN r.[LogId] IS NULL THEN fl.[LogId] END)
            FROM @fl AS fl
            LEFT JOIN [dbo].[LegacyEmployeeLogReviews] AS r WITH (NOLOCK) ON r.[LogId] = fl.[LogId] AND r.[Result] IN (1, 2)
            GROUP BY fl.[EmployeeId];
            SELECT i.[EmployeeId], SUM(i.[Amount])
            FROM [dbo].[LegacyEmployeeLogImpacts] AS i WITH (NOLOCK)
            WHERE {impactScope}
            GROUP BY i.[EmployeeId]
            OPTION (RECOMPILE);
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    public static async Task<List<LegacyEmployeeLogEmployeeSummaryDto>> ExecuteEmployeeSummaryAsync(
        DbConnection connection,
        LegacyEmployeeLogSqlCommand command,
        CancellationToken cancellationToken = default
    ) => await WithReaderAsync(connection, command, async reader =>
    {
        // EmployeeId 可能为空（旧数据），用空串做字典键，回填时还原为 null。
        var rows = new Dictionary<string, LegacyEmployeeLogEmployeeSummaryDto>(StringComparer.Ordinal);
        LegacyEmployeeLogEmployeeSummaryDto Row(string? employeeId) =>
            rows.TryGetValue(employeeId ?? string.Empty, out var row)
                ? row
                : rows[employeeId ?? string.Empty] = new LegacyEmployeeLogEmployeeSummaryDto { EmployeeId = employeeId };
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = Row(NullableString(reader, 0));
            row.EmployeeName = NullableString(reader, 1);
            row.Total = Convert.ToInt32(reader.GetValue(2));
            row.DangerCount = Convert.ToInt32(reader.GetValue(3));
        }
        await NextResultAsync(reader, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = Row(NullableString(reader, 0));
            if (NullableString(reader, 1) is { } store && !row.StoreCodes.Contains(store))
            {
                row.StoreCodes.Add(store);
            }
            if (NullableString(reader, 2) is { } device && !row.DeviceCodes.Contains(device))
            {
                row.DeviceCodes.Add(device);
            }
        }
        await NextResultAsync(reader, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Row(NullableString(reader, 0)).AbnormalByRule.Add(new LegacyEmployeeLogRuleCountDto
            {
                RuleCode = reader.GetString(1),
                Count = Convert.ToInt32(reader.GetValue(2)),
            });
        }
        await NextResultAsync(reader, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = Row(NullableString(reader, 0));
            row.AbnormalCount = Convert.ToInt32(reader.GetValue(1));
            row.PendingReview = Convert.ToInt32(reader.GetValue(2));
        }
        await NextResultAsync(reader, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Row(NullableString(reader, 0)).AmountImpact = reader.GetDecimal(1);
        }
        foreach (var row in rows.Values)
        {
            row.StoreCodes.Sort(StringComparer.Ordinal);
            row.DeviceCodes.Sort(StringComparer.Ordinal);
            row.AbnormalByRule = row.AbnormalByRule.OrderByDescending(rule => rule.Count).ThenBy(rule => rule.RuleCode, StringComparer.Ordinal).ToList();
        }
        // 只在标记表或金额表出现、日志范围内却没有的员工（理论上不会）也保留，避免计数对不上总数。
        return rows.Values.ToList();
    }, cancellationToken);

    internal static LegacyEmployeeLogReviewDto ReadReview(DbDataReader reader, int start) => new()
    {
        Result = Convert.ToByte(reader.GetValue(start)) switch
        {
            ReviewNormal => "normal",
            ReviewNeedsFollowUp => "followUp",
            _ => "revoked",
        },
        Note = NullableString(reader, start + 1),
        ReviewedByName = reader.GetString(start + 2),
        ReviewedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(start + 3), DateTimeKind.Utc),
        Version = reader.GetInt32(start + 4),
    };

    private static Dictionary<string, string> ParseEvidence(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (System.Text.Json.JsonException)
        {
            return new();
        }
    }

    private static List<string> FlagScope(
        IReadOnlyList<string> storeCodes,
        DateTime from,
        DateTime toExclusive,
        string? deviceCode,
        List<LegacyEmployeeLogSqlParameter> parameters
    )
    {
        var stores = string.Join(", ", storeCodes.Select((code, index) => AddParam(parameters, $"@Store{index}", code, DbType.AnsiString, 200)));
        AddParam(parameters, "@From", from, DbType.DateTime);
        AddParam(parameters, "@ToExclusive", toExclusive, DbType.DateTime);
        var conditions = new List<string>
        {
            $"f.[StoreCode] IN ({stores})",
            "f.[OperationTime] >= @From",
            "f.[OperationTime] < @ToExclusive",
            "f.[RetractedAtUtc] IS NULL",
        };
        if (deviceCode != null)
        {
            AddParam(parameters, "@DeviceCode", deviceCode, DbType.AnsiString, 200);
            conditions.Add("f.[DeviceCode] = @DeviceCode");
        }
        return conditions;
    }

    private static string AddParam(List<LegacyEmployeeLogSqlParameter> parameters, string name, object? value, DbType dbType, int? size = null)
    {
        parameters.Add(new LegacyEmployeeLogSqlParameter(name, value, dbType, size));
        return name;
    }

    public static async Task<LegacyEmployeeLogListPage> ExecuteListAsync(
        DbConnection connection,
        LegacyEmployeeLogSqlCommand command,
        bool hasKeywordGuard,
        int keywordScanRowLimit = DefaultKeywordScanRowLimit,
        CancellationToken cancellationToken = default,
        bool hasLensTotal = false
    )
    {
        return await WithReaderAsync(connection, command, async reader =>
        {
            if (hasKeywordGuard)
            {
                // 守卫结果集：超限时批处理已 RETURN，后面没有其他结果集。
                var scanRows = await reader.ReadAsync(cancellationToken) ? Convert.ToInt64(reader.GetValue(0)) : 0L;
                if (scanRows > keywordScanRowLimit)
                {
                    return LegacyEmployeeLogListPage.KeywordRejected(scanRows);
                }
                await NextResultAsync(reader, cancellationToken);
            }
            var counts = new List<LegacyEmployeeLogOperationCountDto>();
            while (await reader.ReadAsync(cancellationToken))
            {
                counts.Add(new LegacyEmployeeLogOperationCountDto
                {
                    Operation = NullableString(reader, 0),
                    Count = Convert.ToInt32(reader.GetValue(1)),
                });
            }
            await NextResultAsync(reader, cancellationToken);
            var rows = await ReadItemsAsync(reader, cancellationToken);
            await NextResultAsync(reader, cancellationToken);
            var employees = new List<LegacyEmployeeLogEmployeeOptionDto>();
            while (await reader.ReadAsync(cancellationToken))
            {
                employees.Add(new LegacyEmployeeLogEmployeeOptionDto
                {
                    EmployeeId = NullableString(reader, 0),
                    EmployeeName = NullableString(reader, 1),
                    Count = Convert.ToInt32(reader.GetValue(2)),
                });
            }
            await NextResultAsync(reader, cancellationToken);
            var devices = new List<LegacyEmployeeLogDeviceOptionDto>();
            while (await reader.ReadAsync(cancellationToken))
            {
                devices.Add(new LegacyEmployeeLogDeviceOptionDto
                {
                    DeviceCode = NullableString(reader, 0),
                    Count = Convert.ToInt32(reader.GetValue(1)),
                });
            }
            long? lensTotal = null;
            if (hasLensTotal)
            {
                await NextResultAsync(reader, cancellationToken);
                lensTotal = await reader.ReadAsync(cancellationToken) ? Convert.ToInt64(reader.GetValue(0)) : 0L;
            }
            return new LegacyEmployeeLogListPage(counts, rows, employees, devices, null, lensTotal);
        }, cancellationToken);
    }

    public static Task<List<LegacyEmployeeLogItemDto>> ExecuteItemsAsync(
        DbConnection connection,
        LegacyEmployeeLogSqlCommand command,
        CancellationToken cancellationToken = default
    ) => WithReaderAsync(connection, command, reader => ReadItemsAsync(reader, cancellationToken), cancellationToken);

    internal static async Task<T> WithReaderAsync<T>(
        DbConnection connection,
        LegacyEmployeeLogSqlCommand command,
        Func<DbDataReader, Task<T>> read,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = 30
    )
    {
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = command.Sql;
            dbCommand.CommandTimeout = commandTimeoutSeconds;
            foreach (var parameter in command.Parameters)
            {
                var dbParameter = dbCommand.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                dbParameter.DbType = parameter.DbType;
                if (parameter.Size.HasValue)
                {
                    dbParameter.Size = parameter.Size.Value;
                }
                dbCommand.Parameters.Add(dbParameter);
            }

            await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken);
            return await read(reader);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<List<LegacyEmployeeLogItemDto>> ReadItemsAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var rows = new List<LegacyEmployeeLogItemDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyEmployeeLogItemDto
            {
                Id = reader.GetString(0),
                EmployeeId = NullableString(reader, 1),
                EmployeeName = NullableString(reader, 2),
                Operation = NullableString(reader, 3),
                OperationDetail = NullableString(reader, 4),
                OperationTime = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Unspecified),
                DeviceCode = NullableString(reader, 6),
                StoreCode = NullableString(reader, 7),
                LastUploadTime = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Unspecified),
            });
        }
        return rows;
    }

    internal static async Task NextResultAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidOperationException("老系统操作日志查询返回的结果集数量不足。");
        }
    }

    internal static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static List<string> NormalizeList(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(TrimToNull)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
