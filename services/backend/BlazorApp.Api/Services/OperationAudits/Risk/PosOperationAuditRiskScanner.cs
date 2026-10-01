using System.Data.Common;
using System.Text.Json;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using DbType = System.Data.DbType;
using C = BlazorApp.Api.Services.OperationAudits.Risk.PosOperationAuditRiskCatalog;
using Q = BlazorApp.Api.Features.LegacyEmployeeLogs.LegacyEmployeeLogSqlServerQuery;

namespace BlazorApp.Api.Services.OperationAudits.Risk;

public sealed record PosAuditRiskScanResult(int Inserted, int Updated, int Retracted, int RowsRead);

/// <summary>
/// 新收银操作审计的单店单窗口异常扫描：读候选事件 → 纯函数判定 → 一个事务写回标记。
/// - 读取走 IX_pos_operation_audit_store_time：操作序列只取规则用得到的类型（不读占一半量的加购事件），
///   重打印与销售从门店本地日零点读起，非营业时段按换算成 UTC 的时间段读全部事件。
/// - 时间窗口是 UTC；门店墙钟只用于营业时间与依据文案，按 HBweb 门店的 TimeZoneId 换算。
/// - 写回与老收银一致：新命中插入、依据变化更新、不再成立且未核查的撤回，已核查的保留。
/// </summary>
public static class PosOperationAuditRiskScanner
{
    public const int CommandTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>审计主表（Hbpos.Api 建）与三张风险表都在才扫描；迁移未执行时调用方跳过。</summary>
    public static async Task<bool> SchemaReadyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var command = new LegacyEmployeeLogSqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(N'dbo.pos_operation_audit', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.pos_operation_audit_item', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.PosOperationAuditFlags', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.PosOperationAuditReviews', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.PosOperationAuditReviewHistory', N'U') IS NOT NULL
                    THEN 1 ELSE 0 END;
            """,
            []
        );
        return await Q.WithReaderAsync(connection, command, async reader =>
            await reader.ReadAsync(cancellationToken) && Convert.ToInt32(reader.GetValue(0)) == 1, cancellationToken);
    }

    /// <summary>窗口内出现过事件的分店：按 (store_code, occurred_at_utc) 索引逐个跳到下一个分店，每次一次索引查找。</summary>
    public static async Task<List<string>> GetStoreCodesAsync(DbConnection connection, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var stores = new List<string>();
        var previous = string.Empty;
        for (var guard = 0; guard < 500; guard++)
        {
            var command = new LegacyEmployeeLogSqlCommand(
                """
                SELECT TOP (1) a.[store_code] FROM [dbo].[pos_operation_audit] AS a WITH (NOLOCK)
                WHERE a.[store_code] > @Previous ORDER BY a.[store_code];
                """,
                [new LegacyEmployeeLogSqlParameter("@Previous", previous, DbType.AnsiString, 50)]
            );
            var next = await Q.WithReaderAsync(connection, command, async reader =>
                await reader.ReadAsync(cancellationToken) ? Q.NullableString(reader, 0) : null, cancellationToken);
            if (next == null)
            {
                break;
            }
            previous = next;
            if (string.IsNullOrWhiteSpace(next))
            {
                continue;
            }
            var active = new LegacyEmployeeLogSqlCommand(
                """
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM [dbo].[pos_operation_audit] AS a WITH (NOLOCK)
                    WHERE a.[store_code] = @Store AND a.[occurred_at_utc] >= @Since) THEN 1 ELSE 0 END;
                """,
                [
                    new LegacyEmployeeLogSqlParameter("@Store", next, DbType.AnsiString, 50),
                    new LegacyEmployeeLogSqlParameter("@Since", sinceUtc, DbType.DateTime),
                ]
            );
            if (await Q.WithReaderAsync(connection, active, async reader =>
                    await reader.ReadAsync(cancellationToken) && Convert.ToInt32(reader.GetValue(0)) == 1, cancellationToken))
            {
                stores.Add(next);
            }
        }
        return stores;
    }

    public static async Task<PosAuditRiskScanResult> ScanAsync(
        DbConnection connection,
        string storeCode,
        TimeZoneInfo timeZone,
        DateTime evalFromUtc,
        DateTime evalToUtc,
        LegacyEmployeeLogRiskOptions options,
        DateTime nowUtc,
        CancellationToken cancellationToken
    )
    {
        var rows = await FetchAsync(connection, storeCode, timeZone, evalFromUtc, evalToUtc, options, cancellationToken);
        var flags = PosOperationAuditRiskEvaluator.Evaluate(new PosAuditRiskInput(storeCode, evalFromUtc, evalToUtc, rows), options);
        var result = await ApplyAsync(connection, storeCode, evalFromUtc, evalToUtc, flags, nowUtc, cancellationToken);
        return result with { RowsRead = rows.Count };
    }

    /// <summary>窗口起点所在的门店本地日零点（UTC）。重打印按「同一天上一笔销售」归组、非营业时段找当段第一条，都要从这里读起。</summary>
    internal static DateTime LocalDayStartUtc(DateTime evalFromUtc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(evalFromUtc, timeZone).Date, timeZone);

    /// <summary>
    /// [fromUtc, toUtc) 内每个门店本地日的「开门前」「打烊后」两段，换算成 UTC 时间段。
    /// 开门 / 打烊时刻都在 7–22 点，澳洲夏令时切换在凌晨 2–3 点，换算不会落进无效或重复时刻。
    /// </summary>
    internal static List<(DateTime From, DateTime To)> OffHoursRangesUtc(
        DateTime fromUtc,
        DateTime toUtc,
        TimeZoneInfo timeZone,
        LegacyEmployeeLogRiskOptions options
    )
    {
        var ranges = new List<(DateTime, DateTime)>();
        var firstDay = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, timeZone).Date;
        var lastDay = TimeZoneInfo.ConvertTimeFromUtc(toUtc, timeZone).Date;
        DateTime Utc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            foreach (var (from, to) in new[] { (Utc(day), Utc(day + options.BusinessOpen)), (Utc(day + options.BusinessClose), Utc(day.AddDays(1))) })
            {
                var start = from < fromUtc ? fromUtc : from;
                var end = to > toUtc ? toUtc : to;
                if (start < end)
                {
                    ranges.Add((start, end));
                }
            }
        }
        return ranges;
    }

    private const string Columns = """
        a.[event_id], a.[device_code], a.[cashier_id], a.[cashier_name], a.[operation_type], a.[outcome], a.[reason_code],
        a.[order_guid], a.[occurred_at_utc], a.[before_actual], a.[after_actual], a.[before_discount], a.[after_discount],
        a.[payment_amount], a.[payment_method], a.[is_emergency_override], a.[primary_product], a.[product_count]
        """;

    internal static LegacyEmployeeLogSqlCommand BuildFetch(
        string storeCode,
        TimeZoneInfo timeZone,
        DateTime evalFromUtc,
        DateTime evalToUtc,
        LegacyEmployeeLogRiskOptions options
    )
    {
        var margin = PosOperationAuditRiskEvaluator.SequenceMargin(options);
        var dayFrom = LocalDayStartUtc(evalFromUtc, timeZone);
        var parameters = new List<LegacyEmployeeLogSqlParameter>
        {
            new("@Store", storeCode, DbType.AnsiString, 50),
            new("@SeqFrom", evalFromUtc - margin, DbType.DateTime),
            new("@SeqTo", evalToUtc + margin, DbType.DateTime),
            new("@DayFrom", dayFrom, DbType.DateTime),
            new("@EvalTo", evalToUtc, DbType.DateTime),
            new("@Reprint", C.ReceiptReprint, DbType.AnsiString, 64),
            new("@Sale", C.SaleComplete, DbType.AnsiString, 64),
        };
        string List(string prefix, IReadOnlyList<string> values) => string.Join(", ", values.Select((value, index) =>
        {
            var name = $"@{prefix}{index}";
            parameters.Add(new LegacyEmployeeLogSqlParameter(name, value, DbType.AnsiString, 64));
            return name;
        }));
        var sequenceOps = List("Seq", C.SequenceOperations);
        var itemOps = List("Item", C.ItemDetailOperations);
        var offHours = OffHoursRangesUtc(dayFrom, evalToUtc, timeZone, options);
        var offHoursPredicate = offHours.Count == 0
            ? "1 = 0"
            : string.Join(" OR ", offHours.Select((range, index) =>
            {
                parameters.Add(new LegacyEmployeeLogSqlParameter($"@OffFrom{index}", range.From, DbType.DateTime));
                parameters.Add(new LegacyEmployeeLogSqlParameter($"@OffTo{index}", range.To, DbType.DateTime));
                return $"(a.[occurred_at_utc] >= @OffFrom{index} AND a.[occurred_at_utc] < @OffTo{index})";
            }));

        // 三段结果列一致（第一段多 7 列商品行），读取时按事件编号去重、带商品行的优先。
        var sql = $"""
            SET NOCOUNT ON;
            SELECT {Columns},
                   i.[display_name], i.[before_quantity], i.[before_unit_price], i.[after_unit_price],
                   i.[before_discount_amount], i.[after_discount_amount], i.[after_gross_amount]
            FROM [dbo].[pos_operation_audit] AS a WITH (NOLOCK)
            OUTER APPLY (
                SELECT TOP (1) it.[display_name], it.[before_quantity], it.[before_unit_price], it.[after_unit_price],
                       it.[before_discount_amount], it.[after_discount_amount], it.[after_gross_amount]
                FROM [dbo].[pos_operation_audit_item] AS it WITH (NOLOCK)
                WHERE it.[event_id] = a.[event_id] AND a.[operation_type] IN ({itemOps})
                ORDER BY it.[line_index]
            ) AS i
            WHERE a.[store_code] = @Store AND a.[occurred_at_utc] >= @SeqFrom AND a.[occurred_at_utc] < @SeqTo
              AND (a.[operation_type] IN ({sequenceOps}) OR a.[is_emergency_override] = 1)
            OPTION (RECOMPILE);
            SELECT {Columns}
            FROM [dbo].[pos_operation_audit] AS a WITH (NOLOCK)
            WHERE a.[store_code] = @Store AND a.[occurred_at_utc] >= @DayFrom AND a.[occurred_at_utc] < @EvalTo
              AND a.[operation_type] IN (@Reprint, @Sale)
            OPTION (RECOMPILE);
            SELECT {Columns}
            FROM [dbo].[pos_operation_audit] AS a WITH (NOLOCK)
            WHERE a.[store_code] = @Store AND ({offHoursPredicate})
            OPTION (RECOMPILE);
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    private static async Task<List<PosAuditRiskRow>> FetchAsync(
        DbConnection connection,
        string storeCode,
        TimeZoneInfo timeZone,
        DateTime evalFromUtc,
        DateTime evalToUtc,
        LegacyEmployeeLogRiskOptions options,
        CancellationToken cancellationToken
    )
    {
        return await Q.WithReaderAsync(connection, BuildFetch(storeCode, timeZone, evalFromUtc, evalToUtc, options), async reader =>
        {
            var rows = new Dictionary<Guid, PosAuditRiskRow>();
            async Task ReadAsync(bool withItem)
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var occurred = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
                    PosAuditRiskItem? item = null;
                    // OUTER APPLY 只给折扣 / 改价事件带出商品行，其余事件这 7 列全为 NULL。
                    if (withItem && Enumerable.Range(18, 7).Any(ordinal => !reader.IsDBNull(ordinal)))
                    {
                        item = new PosAuditRiskItem(
                            Q.NullableString(reader, 18),
                            NullableDecimal(reader, 19),
                            NullableDecimal(reader, 20),
                            NullableDecimal(reader, 21),
                            NullableDecimal(reader, 22),
                            NullableDecimal(reader, 23),
                            NullableDecimal(reader, 24)
                        );
                    }
                    var row = new PosAuditRiskRow(
                        reader.GetGuid(0),
                        storeCode,
                        Q.NullableString(reader, 1),
                        Q.NullableString(reader, 2),
                        Q.NullableString(reader, 3),
                        Q.NullableString(reader, 4)?.Trim() ?? string.Empty,
                        Q.NullableString(reader, 5)?.Trim() ?? string.Empty,
                        Q.NullableString(reader, 6),
                        Q.NullableString(reader, 7),
                        occurred,
                        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(occurred, timeZone), DateTimeKind.Unspecified),
                        NullableDecimal(reader, 9),
                        NullableDecimal(reader, 10),
                        NullableDecimal(reader, 11),
                        NullableDecimal(reader, 12),
                        NullableDecimal(reader, 13),
                        Q.NullableString(reader, 14),
                        reader.GetBoolean(15),
                        Q.NullableString(reader, 16),
                        reader.GetInt32(17),
                        item
                    );
                    if (!rows.TryGetValue(row.EventId, out var existing) || (existing.Item == null && row.Item != null))
                    {
                        rows[row.EventId] = row;
                    }
                }
            }
            await ReadAsync(withItem: true);
            await Q.NextResultAsync(reader, cancellationToken);
            await ReadAsync(withItem: false);
            await Q.NextResultAsync(reader, cancellationToken);
            await ReadAsync(withItem: false);
            return rows.Values.ToList();
        }, cancellationToken, CommandTimeoutSeconds);
    }

    internal static LegacyEmployeeLogSqlCommand BuildApply(
        string storeCode,
        DateTime evalFromUtc,
        DateTime evalToUtc,
        IReadOnlyList<PosAuditRiskFlag> flags,
        DateTime nowUtc
    )
    {
        var payload = flags
            .GroupBy(flag => (flag.Target.EventId, flag.RuleCode))
            .Select(group => group.First())
            .Select(flag => new
            {
                eventId = flag.Target.EventId,
                ruleCode = flag.RuleCode,
                deviceCode = flag.Target.DeviceCode,
                cashierId = flag.Target.CashierId,
                cashierName = flag.Target.CashierName,
                operationType = flag.Target.OperationType,
                occurredAtUtc = flag.Target.OccurredAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                evidenceJson = JsonSerializer.Serialize(flag.Evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary()),
            });
        var parameters = new List<LegacyEmployeeLogSqlParameter>
        {
            new("@Store", storeCode, DbType.AnsiString, 50),
            new("@EvalFrom", evalFromUtc, DbType.DateTime),
            new("@EvalTo", evalToUtc, DbType.DateTime),
            new("@Now", nowUtc, DbType.DateTime2),
            new("@RuleVersion", C.RuleVersion, DbType.Int32),
            new("@FlagsJson", JsonSerializer.Serialize(payload, JsonOptions), DbType.String, -1),
        };
        const string sql = """
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            DECLARE @Flags TABLE (
                [EventId] uniqueidentifier NOT NULL, [RuleCode] varchar(32) NOT NULL,
                [DeviceCode] varchar(64) NULL, [CashierId] varchar(100) NULL, [CashierName] nvarchar(128) NULL,
                [OperationType] varchar(64) NOT NULL, [OccurredAtUtc] datetime NOT NULL, [EvidenceJson] nvarchar(2000) NOT NULL,
                PRIMARY KEY ([EventId], [RuleCode])
            );
            INSERT INTO @Flags
            SELECT j.[EventId], j.[RuleCode], j.[DeviceCode], j.[CashierId], j.[CashierName], j.[OperationType], j.[OccurredAtUtc], j.[EvidenceJson]
            FROM OPENJSON(@FlagsJson) WITH (
                [EventId] uniqueidentifier '$.eventId', [RuleCode] varchar(32) '$.ruleCode',
                [DeviceCode] varchar(64) '$.deviceCode', [CashierId] varchar(100) '$.cashierId', [CashierName] nvarchar(128) '$.cashierName',
                [OperationType] varchar(64) '$.operationType', [OccurredAtUtc] datetime '$.occurredAtUtc', [EvidenceJson] nvarchar(2000) '$.evidenceJson'
            ) AS j;

            DECLARE @Inserted int = 0, @Updated int = 0, @Retracted int = 0;
            BEGIN TRANSACTION;
            -- 不再成立且未核查的标记撤回；已核查的（确认正常 / 需跟进）保留。
            UPDATE f SET f.[RetractedAtUtc] = @Now, f.[UpdatedAtUtc] = @Now
            FROM [dbo].[PosOperationAuditFlags] AS f WITH (UPDLOCK)
            WHERE f.[StoreCode] = @Store AND f.[OccurredAtUtc] >= @EvalFrom AND f.[OccurredAtUtc] < @EvalTo
              AND f.[RetractedAtUtc] IS NULL
              AND NOT EXISTS (SELECT 1 FROM @Flags AS n WHERE n.[EventId] = f.[EventId] AND n.[RuleCode] = f.[RuleCode])
              AND NOT EXISTS (SELECT 1 FROM [dbo].[PosOperationAuditReviews] AS r WHERE r.[EventId] = f.[EventId] AND r.[Result] IN (1, 2));
            SET @Retracted = @@ROWCOUNT;

            UPDATE f SET f.[RuleVersion] = @RuleVersion, f.[DeviceCode] = n.[DeviceCode], f.[CashierId] = n.[CashierId],
                f.[CashierName] = n.[CashierName], f.[OperationType] = n.[OperationType], f.[EvidenceJson] = n.[EvidenceJson],
                f.[RetractedAtUtc] = NULL, f.[UpdatedAtUtc] = @Now
            FROM [dbo].[PosOperationAuditFlags] AS f WITH (UPDLOCK)
            JOIN @Flags AS n ON n.[EventId] = f.[EventId] AND n.[RuleCode] = f.[RuleCode]
            WHERE f.[RetractedAtUtc] IS NOT NULL OR f.[RuleVersion] <> @RuleVersion OR f.[EvidenceJson] <> n.[EvidenceJson];
            SET @Updated = @@ROWCOUNT;

            INSERT INTO [dbo].[PosOperationAuditFlags]
                ([EventId], [RuleCode], [RuleVersion], [StoreCode], [DeviceCode], [CashierId], [CashierName], [OperationType],
                 [OccurredAtUtc], [EvidenceJson], [DetectedAtUtc], [UpdatedAtUtc], [RetractedAtUtc])
            SELECT n.[EventId], n.[RuleCode], @RuleVersion, @Store, n.[DeviceCode], n.[CashierId], n.[CashierName], n.[OperationType],
                   n.[OccurredAtUtc], n.[EvidenceJson], @Now, @Now, NULL
            FROM @Flags AS n
            WHERE NOT EXISTS (
                SELECT 1 FROM [dbo].[PosOperationAuditFlags] AS f WITH (UPDLOCK, HOLDLOCK)
                WHERE f.[EventId] = n.[EventId] AND f.[RuleCode] = n.[RuleCode]);
            SET @Inserted = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SELECT @Inserted, @Updated, @Retracted;
            """;
        return new LegacyEmployeeLogSqlCommand(sql, parameters);
    }

    private static async Task<PosAuditRiskScanResult> ApplyAsync(
        DbConnection connection,
        string storeCode,
        DateTime evalFromUtc,
        DateTime evalToUtc,
        IReadOnlyList<PosAuditRiskFlag> flags,
        DateTime nowUtc,
        CancellationToken cancellationToken
    )
    {
        return await Q.WithReaderAsync(connection, BuildApply(storeCode, evalFromUtc, evalToUtc, flags, nowUtc), async reader =>
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("新收银异常标记写入没有返回计数。");
            }
            return new PosAuditRiskScanResult(
                Convert.ToInt32(reader.GetValue(0)),
                Convert.ToInt32(reader.GetValue(1)),
                Convert.ToInt32(reader.GetValue(2)),
                0
            );
        }, cancellationToken, CommandTimeoutSeconds);
    }

    private static decimal? NullableDecimal(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
}
