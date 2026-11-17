using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobPayRateSyncService
{
    Task<ApiResponse<MyobPayRateSyncPreviewDto>> PreviewAsync(
        MyobPayRateSyncPreviewRequest request, CancellationToken cancellationToken);

    Task<ApiResponse<MyobPayRateSyncApplyResultDto>> ApplyAsync(
        MyobPayRateSyncApplyRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 从 MYOB 同步转账时薪到我方个人时薪表（预览 → 勾选 → 写入）。设计见 docs/plans/2026-11-16-myob-hourly-rate-sync.md。
/// 只读 MYOB；只往 <c>EmployeePayRate</c> 新增记录，写入一律经 <see cref="IAttendanceReactService.CreatePayRateAsync"/>，
/// 所以分店权限、已锁定周期、同一生效日重复、金额范围等校验与手填完全同一条路径，这里不复制任何校验。
/// 安全约束：①前端只传「员工 + 预览哈希 + 勾选的格」，价格由服务端重新向 MYOB 取并重新推算；
/// ②写入前比对哈希，预览之后我方或 MYOB 的值变了就不写；③一个员工失败不影响其他人。
/// </summary>
public sealed class MyobPayRateSyncService : IMyobPayRateSyncService
{
    private const string EmploymentCasual = "Casual";
    private const string PayModeTransfer = "Transfer";
    private const string PayPeriodSubmitted = "Submitted";

    private readonly ISqlSugarClient _db;
    private readonly IAttendanceReactService _attendance;
    private readonly IMyobEmployeeWageSource _wages;
    private readonly IMyobPayCategoryService _categories;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly TimeProvider _time;

    public MyobPayRateSyncService(
        SqlSugarContext context,
        IAttendanceReactService attendance,
        IMyobEmployeeWageSource wages,
        IMyobPayCategoryService categories,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        TimeProvider? timeProvider = null)
        : this(context.Db, attendance, wages, categories, client, tokens, timeProvider)
    {
    }

    internal MyobPayRateSyncService(
        ISqlSugarClient db,
        IAttendanceReactService attendance,
        IMyobEmployeeWageSource wages,
        IMyobPayCategoryService categories,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _attendance = attendance;
        _wages = wages;
        _categories = categories;
        _client = client;
        _tokens = tokens;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── 预览 ─────────────────────────

    public async Task<ApiResponse<MyobPayRateSyncPreviewDto>> PreviewAsync(
        MyobPayRateSyncPreviewRequest request, CancellationToken cancellationToken)
    {
        var connectionGuid = request.ConnectionGuid?.Trim() ?? string.Empty;
        var storeCode = request.StoreCode?.Trim() ?? string.Empty;
        if (connectionGuid.Length == 0 || storeCode.Length == 0)
        {
            return ApiResponse<MyobPayRateSyncPreviewDto>.Error("缺少公司文件或分店", MyobPayRateSyncErrorCodes.InvalidRequest);
        }

        var load = await LoadAsync(connectionGuid, storeCode, request.Refresh, cancellationToken);
        if (load.Error is not null)
        {
            return ApiResponse<MyobPayRateSyncPreviewDto>.Error(load.Error.Value.Message, load.Error.Value.Code);
        }

        var ctx = load.Context!;
        var preview = new MyobPayRateSyncPreviewDto
        {
            ConnectionGuid = connectionGuid,
            ConnectionName = ctx.ConnectionName,
            StoreCode = storeCode,
            EffectiveFrom = request.EffectiveFrom?.Date ?? await ResolveDefaultEffectiveFromAsync(storeCode),
            FetchedAt = ctx.FetchedAt,
        };

        foreach (var our in ctx.Ours)
        {
            if (!ctx.UidByUser.TryGetValue(our.UserGuid, out var uid))
            {
                preview.NotBoundCount++;
                continue;
            }

            preview.Rows.Add(BuildRow(ctx, our, uid).Row);
        }

        return ApiResponse<MyobPayRateSyncPreviewDto>.OK(preview);
    }

    // ───────────────────────── 写入 ─────────────────────────

    public async Task<ApiResponse<MyobPayRateSyncApplyResultDto>> ApplyAsync(
        MyobPayRateSyncApplyRequest request, CancellationToken cancellationToken)
    {
        var connectionGuid = request.ConnectionGuid?.Trim() ?? string.Empty;
        var storeCode = request.StoreCode?.Trim() ?? string.Empty;
        if (connectionGuid.Length == 0 || storeCode.Length == 0 || request.EffectiveFrom == default)
        {
            return ApiResponse<MyobPayRateSyncApplyResultDto>.Error(
                "缺少公司文件、分店或生效日期", MyobPayRateSyncErrorCodes.InvalidRequest);
        }

        if (request.Items is not { Count: > 0 })
        {
            return ApiResponse<MyobPayRateSyncApplyResultDto>.Error("没有要写入的员工", MyobPayRateSyncErrorCodes.NoItems);
        }

        // 写入前一律绕过缓存重新读 MYOB：用户可能是几分钟前打开的预览。
        var load = await LoadAsync(connectionGuid, storeCode, refresh: true, cancellationToken);
        if (load.Error is not null)
        {
            return ApiResponse<MyobPayRateSyncApplyResultDto>.Error(load.Error.Value.Message, load.Error.Value.Code);
        }

        var ctx = load.Context!;
        var effectiveFrom = request.EffectiveFrom.Date;
        var remark = $"来自 MYOB 同步 {_time.GetUtcNow().UtcDateTime:yyyy-MM-dd}";
        var result = new MyobPayRateSyncApplyResultDto { EffectiveFrom = effectiveFrom };
        var ours = ctx.Ours.ToDictionary(item => item.UserGuid, StringComparer.OrdinalIgnoreCase);

        foreach (var item in request.Items
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.UserGuid))
                     .GroupBy(entry => entry.UserGuid.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var userGuid = item.UserGuid.Trim();
            if (!ours.TryGetValue(userGuid, out var our))
            {
                result.Failed.Add(Fail(userGuid, userGuid, MyobPayRateSyncErrorCodes.NotInPreview, "该员工不属于所选分店"));
                continue;
            }

            var name = our.UserName ?? userGuid;
            if (!ctx.UidByUser.TryGetValue(userGuid, out var uid))
            {
                result.Failed.Add(Fail(userGuid, name, MyobPayRateSyncErrorCodes.NotBound, "该员工没有绑定这个 MYOB 公司文件"));
                continue;
            }

            var (_, computed) = BuildRow(ctx, our, uid);
            if (computed.SkipReason == MyobPayRateSyncReasons.CardMissing)
            {
                result.Failed.Add(Fail(userGuid, name, MyobPayRateSyncReasons.CardMissing, "MYOB 里已经找不到这张员工卡"));
                continue;
            }

            if (!string.Equals(computed.Hash, item.Hash, StringComparison.Ordinal))
            {
                result.Failed.Add(Fail(userGuid, name, MyobPayRateSyncErrorCodes.ValueChanged,
                    "预览之后 MYOB 或我方的时薪已变化，请重新预览"));
                continue;
            }

            var cells = NormalizeCells(item.Cells);
            var rejected = RejectUnavailable(computed, cells);
            if (rejected is not null)
            {
                result.Failed.Add(Fail(userGuid, name, MyobPayRateSyncErrorCodes.NotApplicable, rejected));
                continue;
            }

            var dto = BuildCreateDto(storeCode, effectiveFrom, remark, our, computed, cells);
            if (dto is null)
            {
                // 没勾平日价、而写入后转账平日价仍为空（新建 / 补空时）：CreatePayRateAsync 会拒绝，这里给出更明确的原因。
                result.Failed.Add(Fail(userGuid, name, MyobPayRateSyncErrorCodes.NotApplicable,
                    "该员工还没有转账平日价，必须勾选「平日价」才能写入"));
                continue;
            }

            var created = await _attendance.CreatePayRateAsync(dto);
            if (!created.Success)
            {
                result.Failed.Add(Fail(userGuid, name, created.ErrorCode ?? MyobPayRateSyncErrorCodes.NotApplicable, created.Message));
                continue;
            }

            result.Written.Add(new MyobPayRateSyncWrittenDto
            {
                UserGuid = userGuid,
                UserName = name,
                RateGuid = created.Data?.RateGuid ?? string.Empty,
                Cells = cells.ToList(),
            });
        }

        return ApiResponse<MyobPayRateSyncApplyResultDto>.OK(result);
    }

    /// <summary>勾的格里有「没有可写建议值」的就整个员工拒绝，不写一部分。</summary>
    private static string? RejectUnavailable(PayRateSyncResult computed, HashSet<string> cells)
    {
        if (cells.Count == 0)
        {
            return "没有勾选可写入的价格";
        }

        if (computed.Kind == MyobPayRateSyncKinds.Skip)
        {
            return "该员工不参与同步";
        }

        foreach (var cell in cells)
        {
            var target = CellOf(computed, cell);
            if (!target.CanApply)
            {
                return target.UnavailableReason is null
                    ? "所选价格与我方现值一致，没有可写入的内容"
                    : "所选价格无法按 MYOB 推算";
            }
        }

        return null;
    }

    /// <summary>
    /// 组装新增记录：有旧记录就整条复制（发放方式、现金价、补贴、最低时薪、固定转账工时原样带过来），
    /// 只改勾选的格；没有旧记录就新建全转账。公假价没勾时传 null，由 CreatePayRateAsync 按「没带则等于平日价」处理。
    /// 转账价写入后平日价仍为空返回 null。
    /// </summary>
    private static CreateEmployeePayRateDto? BuildCreateDto(
        string storeCode,
        DateTime effectiveFrom,
        string remark,
        AttendancePayRateDto our,
        PayRateSyncResult computed,
        HashSet<string> cells)
    {
        var hasRecord = !string.IsNullOrEmpty(our.RateGuid);
        decimal? Pick(string cell, decimal? current) =>
            cells.Contains(cell) ? CellOf(computed, cell).Suggested : current;

        var weekday = Pick(MyobPayRateSyncCells.Weekday, our.TransferWeekdayRate);
        if (weekday is null)
        {
            return null;
        }

        return new CreateEmployeePayRateDto
        {
            StoreCode = storeCode,
            UserGuid = our.UserGuid,
            PayMode = hasRecord ? our.PayMode ?? PayModeTransfer : PayModeTransfer,
            FixedTransferHoursPerWeek = hasRecord ? our.FixedTransferHoursPerWeek : 0m,
            TransferWeekdayRate = weekday,
            TransferSaturdayRate = Pick(MyobPayRateSyncCells.Saturday, our.TransferSaturdayRate),
            TransferSundayRate = Pick(MyobPayRateSyncCells.Sunday, our.TransferSundayRate),
            TransferPublicHolidayRate = Pick(MyobPayRateSyncCells.PublicHoliday, our.TransferPublicHolidayRate),
            CashRate = hasRecord ? our.CashRate : null,
            CashAllowancePerPeriod = hasRecord ? our.CashAllowancePerPeriod : 0m,
            MinimumRate = hasRecord ? our.MinimumRate : null,
            EffectiveFrom = effectiveFrom,
            Remark = remark,
        };
    }

    // ───────────────────────── 计算一行 ─────────────────────────

    private (MyobPayRateSyncRowDto Row, PayRateSyncResult Result) BuildRow(SyncContext ctx, AttendancePayRateDto our, string uid)
    {
        var ourValues = new PayRateSyncOur(
            !string.IsNullOrEmpty(our.RateGuid),
            our.PayMode,
            our.TransferWeekdayRate,
            our.TransferSaturdayRate,
            our.TransferSundayRate,
            our.TransferPublicHolidayRate);

        PayRateSyncResult result;
        if (!ctx.Cards.TryGetValue(uid, out var card))
        {
            var blank = new PayRateSyncCell(null, null, null, false, null, false);
            result = new PayRateSyncResult(
                MyobPayRateSyncKinds.Skip, MyobPayRateSyncReasons.CardMissing, null, false,
                blank with { OurRate = our.TransferWeekdayRate },
                blank with { OurRate = our.TransferSaturdayRate },
                blank with { OurRate = our.TransferSundayRate },
                blank with { OurRate = our.TransferPublicHolidayRate },
                MyobPayRateSyncRules.Hash(ourValues, null, null, null, null));
        }
        else
        {
            var casual = string.Equals(our.EmploymentType, EmploymentCasual, StringComparison.OrdinalIgnoreCase);
            var map = ctx.Mapping;
            result = MyobPayRateSyncRules.Compute(new PayRateSyncInput(
                ourValues,
                new PayRateSyncMyob(card.PayBasis, card.HourlyRate, card.TerminationDate),
                Multiple(ctx, card, casual ? map.SaturdayCasualUid : map.SaturdayUid),
                Multiple(ctx, card, casual ? map.SundayCasualUid : map.SundayUid),
                Multiple(ctx, card, casual ? map.PublicHolidayCasualUid : map.PublicHolidayUid),
                _time.GetUtcNow().UtcDateTime));
        }

        var row = new MyobPayRateSyncRowDto
        {
            UserGuid = our.UserGuid,
            UserName = our.UserName ?? our.UserGuid,
            EmploymentType = our.EmploymentType,
            Kind = result.Kind,
            SkipReason = result.SkipReason,
            PayMode = our.PayMode,
            MyobHourlyRate = result.MyobHourlyRate,
            Rounded = result.Rounded,
            Weekday = ToDto(result.Weekday),
            Saturday = ToDto(result.Saturday),
            Sunday = ToDto(result.Sunday),
            PublicHoliday = ToDto(result.PublicHoliday),
            Hash = result.Hash,
        };
        return (row, result);
    }

    private static PayRateSyncMultipleInput Multiple(SyncContext ctx, MyobEmployeePayrollRecord card, string? mappedUid)
    {
        var uid = string.IsNullOrWhiteSpace(mappedUid) ? null : mappedUid.Trim();
        MyobWageCategoryRecord? category = null;
        if (uid is not null)
        {
            ctx.Categories.TryGetValue(uid, out category);
        }

        var onCard = uid is not null
            && card.WageCategoryUids?.Contains(uid, StringComparer.OrdinalIgnoreCase) == true;
        return new PayRateSyncMultipleInput(uid, category, onCard);
    }

    private static MyobPayRateSyncCellDto ToDto(PayRateSyncCell cell) => new()
    {
        OurRate = cell.OurRate,
        Suggested = cell.Suggested,
        Multiplier = cell.Multiplier,
        CanApply = cell.CanApply,
        UnavailableReason = cell.UnavailableReason,
        DefaultSelected = cell.DefaultSelected,
    };

    private static PayRateSyncCell CellOf(PayRateSyncResult result, string cell) => cell switch
    {
        MyobPayRateSyncCells.Weekday => result.Weekday,
        MyobPayRateSyncCells.Saturday => result.Saturday,
        MyobPayRateSyncCells.Sunday => result.Sunday,
        _ => result.PublicHoliday,
    };

    /// <summary>只保留认识的格并去重。</summary>
    private static HashSet<string> NormalizeCells(IEnumerable<string>? cells)
    {
        var known = new[]
        {
            MyobPayRateSyncCells.Weekday, MyobPayRateSyncCells.Saturday,
            MyobPayRateSyncCells.Sunday, MyobPayRateSyncCells.PublicHoliday,
        };
        return known.Where(cell => cells?.Contains(cell, StringComparer.Ordinal) == true).ToHashSet(StringComparer.Ordinal);
    }

    private static MyobPayRateSyncFailureDto Fail(string userGuid, string userName, string code, string message) => new()
    {
        UserGuid = userGuid,
        UserName = userName,
        Code = code,
        Message = message,
    };

    // ───────────────────────── 默认生效日期 ─────────────────────────

    /// <summary>
    /// 下一个未锁定周期的起始日：该分店最后一个已提交（锁定）周期结束的次日；一个都没提交过就取本周周一。
    /// 员工若还属于别的分店，别店的锁定由 CreatePayRateAsync 逐员工拦截并报 PERIOD_LOCKED。
    /// </summary>
    private async Task<DateTime> ResolveDefaultEffectiveFromAsync(string storeCode)
    {
        var last = await _db.Queryable<AttendancePayPeriod>()
            .Where(item => item.StoreCode == storeCode && item.Status == PayPeriodSubmitted)
            .OrderByDescending(item => item.PeriodStart)
            .FirstAsync();
        if (last is not null)
        {
            return last.PeriodStart.Date.AddDays(Math.Max(1, last.Weeks) * 7);
        }

        var today = _time.GetUtcNow().UtcDateTime.Date;
        var sinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-sinceMonday);
    }

    // ───────────────────────── 读取上下文 ─────────────────────────

    private sealed class SyncContext
    {
        public string ConnectionName { get; init; } = string.Empty;
        public DateTime FetchedAt { get; init; }

        /// <summary>该分店的员工（含还没有时薪记录的），已按「经理在前、姓名」排好序。</summary>
        public List<AttendancePayRateDto> Ours { get; init; } = [];

        /// <summary>我方员工 → MYOB 员工 UID（只含已绑定本公司文件的）。</summary>
        public Dictionary<string, string> UidByUser { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, MyobEmployeePayrollRecord> Cards { get; init; } =
            new Dictionary<string, MyobEmployeePayrollRecord>();

        public Dictionary<string, MyobWageCategoryRecord> Categories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public MyobPayCategoryMappingDto Mapping { get; init; } = new();
    }

    // 错误元组的元素顺序是（消息，错误码），下面所有 return 与调用方读取都按这个顺序。
    private async Task<(SyncContext? Context, (string Message, string Code)? Error)> LoadAsync(
        string connectionGuid, string storeCode, bool refresh, CancellationToken cancellationToken)
    {
        var connection = await _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == connectionGuid && item.Status == MyobConnectionStatuses.Active);
        if (connection is null)
        {
            return (null, ("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive));
        }

        // 我方员工与现行时薪：走现有时薪表接口，分店权限（只能看自己管的分店）一并沿用。
        var rates = await _attendance.GetPayRatesAsync(new AttendancePayRateQueryDto { StoreCode = storeCode });
        if (!rates.Success)
        {
            return (null, (rates.Message, rates.ErrorCode ?? MyobPayRateSyncErrorCodes.InvalidRequest));
        }

        var ours = rates.Data ?? [];
        var userGuids = ours.Select(item => item.UserGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var links = (await _db.Queryable<EmployeeMyobLink>()
                .Where(item => item.ConnectionGuid == connectionGuid && item.UnboundAt == null)
                .ToListAsync())
            .Where(link => userGuids.Contains(link.UserGuid))
            .GroupBy(link => link.UserGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MyobEmployeeUid, StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, MyobEmployeePayrollRecord> cards;
        IReadOnlyList<MyobWageCategoryRecord> categories;
        try
        {
            cards = await _wages.GetAsync(connectionGuid, refresh, cancellationToken);
            var accessToken = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
            categories = await _client.GetWageCategoriesAsync(connection.BusinessId, accessToken, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return (null, (ex.Message, ex.Code));
        }

        return (new SyncContext
        {
            ConnectionName = connection.BusinessName,
            FetchedAt = _time.GetUtcNow().UtcDateTime,
            Ours = ours,
            UidByUser = links,
            Cards = cards,
            Categories = categories
                .GroupBy(item => item.Uid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase),
            Mapping = await _categories.GetSavedMappingAsync(connectionGuid),
        }, null);
    }
}
