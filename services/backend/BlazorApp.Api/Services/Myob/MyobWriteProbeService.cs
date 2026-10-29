using System.Diagnostics;
using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

// MYOB Timesheet 写入探针（临时接口，实测后整体删除）：
// 方案 docs/plans/2026-10-29-myob-timesheet-push.md 第 3 节。官方文档没写清 PUT Timesheet 是覆盖还是合并、能否清空、日期与小时的格式，
// 在一个生产公司文件的一张测试员工卡上固定地发几组请求，把结论写回方案，之后才实现正式推送。
//
//   POST /api/react/v1/myob/timesheet/write-probe   Body MyobWriteProbeRequest   Myob.Connection.Manage → MyobWriteProbeDto
//
// 安全约束（都在服务里硬性校验，不靠自觉）：
// 1. 只对 Myob__WriteProbeBusinessId（公司文件的 BusinessId）指定的连接、Myob__WriteProbeEmployeeDisplayId（Employee ID）指定的员工卡生效，任一未配置 / 不符一律按「未开启」处理，不发任何请求；
//    员工卡还要在名册里恰好匹配一张，且是启用中、姓名都是 test 的专用测试卡，否则拒绝；
// 2. 周期必须从今天或以后开始，且写入前先读整个探针区间：该卡只要有任何记录就中止，一次写入都不发；
// 3. 无论中途成功与否，最后都会尽力清空（先用已验证有效的办法，再逐个降级），并再读一次确认；清不干净会明确告知，由人在 MYOB 里手工清理；
// 4. 响应只含：状态码、MYOB 错误类型名、耗时、该测试卡自己的日期 / 小时（因为写入前确认过是空的，读到的都是探针自己写的）。不回显响应体。

public static class MyobWriteProbeErrorCodes
{
    /// <summary>探针未开启：连接 / 员工卡未配置，或请求的连接不是被允许的那一个。</summary>
    public const string NotEnabled = "MYOB_WRITE_PROBE_NOT_ENABLED";

    public const string InvalidRequest = "MYOB_WRITE_PROBE_INVALID_REQUEST";

    /// <summary>按配置的 Employee ID 找不到唯一的卡，或该卡不是启用中的 test test 专用测试卡。</summary>
    public const string TestCardNotAllowed = "MYOB_WRITE_PROBE_TEST_CARD_NOT_ALLOWED";

    /// <summary>平日 / 周六工资类别既没有在请求里给，也没有在「类别映射」里保存。</summary>
    public const string CategoryMissing = "MYOB_WRITE_PROBE_CATEGORY_MISSING";
}

public sealed class MyobWriteProbeRequest
{
    /// <summary>MYOB 公司文件的 BusinessId（API 路径里的 cf_uri）；必须与配置 Myob__WriteProbeBusinessId 一致。</summary>
    public string BusinessId { get; set; } = string.Empty;

    /// <summary>探针区间起始日 yyyy-MM-dd，必须是今天或以后；区间 = 起始日起 21 天，期间该测试卡不能有任何 Timesheet 记录。</summary>
    public string PeriodStart { get; set; } = string.Empty;

    /// <summary>平日类别 UID；省略则用该公司文件已保存的类别映射。</summary>
    public string? WeekdayCategoryUid { get; set; }

    /// <summary>周六类别 UID；省略则用已保存的类别映射。</summary>
    public string? SaturdayCategoryUid { get; set; }

    /// <summary>可选：一个没有挂到测试卡上的工资类别 UID，用来看 PUT 会返回什么。</summary>
    public string? UnassignedCategoryUid { get; set; }
}

public sealed class MyobWriteProbeEntryDto
{
    /// <summary>weekday / saturday / unassigned / other。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>MYOB 返回的日期原文（用来核对格式）。</summary>
    public string Date { get; set; } = string.Empty;

    public decimal? Hours { get; set; }
    public bool Processed { get; set; }
}

public sealed class MyobWriteProbeStepDto
{
    public string Key { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>HTTP 状态码；网络层失败为 null。</summary>
    public int? HttpStatus { get; set; }

    /// <summary>MYOB 错误类型名（Errors[0].Name）或我方错误码；没有为 null。</summary>
    public string? ErrorName { get; set; }

    /// <summary>MYOB 的错误码（Errors[0].ErrorCode，如 220 越界日期、34001 类别未挂卡、34003 Timesheet 设置未开）；没有为 null。</summary>
    public string? ErrorCode { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>读取步骤：读到的全部记录；写入步骤为 null。</summary>
    public List<MyobWriteProbeEntryDto>? Entries { get; set; }
}

/// <summary>由各步结果推出的结论：true / false = 已证实，null = 没测到或数据不足。</summary>
public sealed class MyobWriteProbeVerdictsDto
{
    /// <summary>被接受的日期写法：date = yyyy-MM-dd，datetime = yyyy-MM-ddT00:00:00，datetime-space = yyyy-MM-dd 00:00:00；都被拒绝为 null。</summary>
    public string? DateFormatAccepted { get; set; }

    /// <summary>只带员工 UID、日期区间、类别 UID 与每天工时（不带 Job / Notes / Processed）的请求体是否被接受。</summary>
    public bool? MinimalBodyAccepted { get; set; }

    /// <summary>写入 7.333 小时后读回的值（原样返回，判断保留几位小数）。</summary>
    public decimal? HoursReadBack { get; set; }

    /// <summary>true = 只发另一个类别的一行，先前的类别行就没了（整体覆盖）；false = 保留（按行合并）。</summary>
    public bool? PutReplacesAll { get; set; }

    public int? OutOfRangeHttpStatus { get; set; }

    /// <summary>请求区间之外的一天（起始日 + 10）是否被存下来。</summary>
    public bool? OutOfRangeEntryStored { get; set; }

    /// <summary>DELETE（带 StartDate / EndDate）的 HTTP 状态码；文档只列了 DELETE，没说它做什么。</summary>
    public int? DeleteHttpStatus { get; set; }

    /// <summary>DELETE 能否清空整个探针区间（文档没说，需实测）。</summary>
    public bool? ClearWithDelete { get; set; }

    /// <summary>Lines 为空数组的 PUT 能否清空。</summary>
    public bool? ClearWithEmptyLines { get; set; }

    /// <summary>每行 Entries 为空数组的 PUT 能否清空。</summary>
    public bool? ClearWithEmptyEntries { get; set; }

    /// <summary>把所有天的小时改成 0 能否让记录不再有工时（记录本身还在）。</summary>
    public bool? ClearWithZeroHours { get; set; }

    /// <summary>给测试卡没有的类别写入是否被拒绝（需要请求里提供 UnassignedCategoryUid）。</summary>
    public bool? UnassignedCategoryRejected { get; set; }

    /// <summary>最后一次读：区间内没有任何记录。</summary>
    public bool FinalEmpty { get; set; }

    /// <summary>最后一次读：区间内没有任何非 0 工时（FinalEmpty 为 false 但这个为 true = 只剩 0 工时的记录）。</summary>
    public bool FinalClean { get; set; }

    public int ResidualEntries { get; set; }
}

public sealed class MyobWriteProbeDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string PeriodStart { get; set; } = string.Empty;

    /// <summary>探针区间止（含），起始日 + 20 天。</summary>
    public string PeriodEnd { get; set; } = string.Empty;

    /// <summary>true = 写入前检查没通过，一次写入都没发。</summary>
    public bool Aborted { get; set; }

    public string? AbortReason { get; set; }
    public List<MyobWriteProbeStepDto> Steps { get; set; } = [];
    public MyobWriteProbeVerdictsDto Verdicts { get; set; } = new();

    public string Note { get; set; } =
        "写入探针：只对配置指定的一个连接、一张测试员工卡写入，结束时尽力清空并再读一次确认。FinalClean 为 false 时，请在 MYOB 的 Enter Timesheet 里手工清理该测试卡。";
}

public interface IMyobWriteProbeService
{
    Task<ApiResponse<MyobWriteProbeDto>> RunAsync(MyobWriteProbeRequest request, CancellationToken cancellationToken);
}

internal sealed class MyobWriteProbeService : IMyobWriteProbeService
{
    internal const int RangeDays = 21;

    /// <summary>专用测试卡的姓和名都必须是它（不区分大小写）。</summary>
    private const string TestCardName = "test";
    private const int WindowDays = 7;
    private const int OutOfRangeOffset = 10;

    /// <summary>依次试的日期写法（步骤键、说明）：文档文字写 YYYY-MM-DD HH:MM:SS，文档示例写 yyyy-MM-ddT00:00:00。</summary>
    private static readonly (string Format, string Key, string Description)[] DateFormats =
    [
        ("date", "put-date-format", "平日两天，日期写 yyyy-MM-dd，最小请求体"),
        ("datetime", "put-date-format-datetime", "改用 yyyy-MM-ddT00:00:00 再试（文档示例的写法）"),
        ("datetime-space", "put-date-format-datetime-space", "再改用 yyyy-MM-dd 00:00:00 再试（文档文字写的格式）"),
    ];

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly IMyobPayCategoryService _categories;
    private readonly MyobOptions _options;
    private readonly TimeProvider _time;

    public MyobWriteProbeService(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMyobPayCategoryService categories,
        IOptions<MyobOptions> options,
        TimeProvider? timeProvider = null)
        : this(context.Db, client, tokens, categories, options, timeProvider)
    {
    }

    internal MyobWriteProbeService(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMyobPayCategoryService categories,
        IOptions<MyobOptions> options,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _categories = categories;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<MyobWriteProbeDto>> RunAsync(MyobWriteProbeRequest request, CancellationToken cancellationToken)
    {
        var businessId = request.BusinessId?.Trim() ?? string.Empty;
        var displayId = _options.WriteProbeEmployeeDisplayId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_options.WriteProbeBusinessId)
            || displayId.Length == 0
            || !string.Equals(businessId, _options.WriteProbeBusinessId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return ApiResponse<MyobWriteProbeDto>.Error("MYOB 写入探针未开启", MyobWriteProbeErrorCodes.NotEnabled);
        }

        if (!DateTime.TryParseExact(request.PeriodStart?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return ApiResponse<MyobWriteProbeDto>.Error("探针起始日必须是 yyyy-MM-dd", MyobWriteProbeErrorCodes.InvalidRequest);
        }

        var today = _time.GetUtcNow().UtcDateTime.AddHours(10).Date; // 澳洲东部标准时间，与推送预览同口径
        if (start.Date < today)
        {
            return ApiResponse<MyobWriteProbeDto>.Error("探针只用今天或以后开始的区间，不碰已过去的周期", MyobWriteProbeErrorCodes.InvalidRequest);
        }

        var connection = await _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.BusinessId == businessId && item.Status == MyobConnectionStatuses.Active);
        if (connection is null)
        {
            return ApiResponse<MyobWriteProbeDto>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        var connectionGuid = connection.ConnectionGuid;
        var saved = MyobPayCategoryService.ToDictionary(await _categories.GetSavedMappingAsync(connectionGuid));
        var weekdayUid = FirstNonEmpty(request.WeekdayCategoryUid, saved.GetValueOrDefault(MyobPayCategoryKeys.Weekday));
        var saturdayUid = FirstNonEmpty(request.SaturdayCategoryUid, saved.GetValueOrDefault(MyobPayCategoryKeys.Saturday));
        if (weekdayUid is null || saturdayUid is null)
        {
            return ApiResponse<MyobWriteProbeDto>.Error(
                "平日 / 周六工资类别未提供，也没有保存类别映射", MyobWriteProbeErrorCodes.CategoryMissing);
        }

        string token;
        try
        {
            token = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobWriteProbeDto>.Error(ex.Message, ex.Code);
        }

        // 在员工名册里按 Employee ID 查卡：必须恰好一张，且是启用中的「test test」专用测试卡，否则一次写入都不发。
        IReadOnlyList<MyobEmployeeRecord> employees;
        try
        {
            employees = await _client.GetEmployeesAsync(connection.BusinessId, token, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobWriteProbeDto>.Error(ex.Message, ex.Code);
        }

        var matches = employees.Where(item => string.Equals(item.DisplayId?.Trim(), displayId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            return ApiResponse<MyobWriteProbeDto>.Error(
                "按配置的 Employee ID 没有找到唯一的员工卡", MyobWriteProbeErrorCodes.TestCardNotAllowed);
        }

        var card = matches[0];
        if (!card.IsActive
            || !string.Equals(card.LastName?.Trim(), TestCardName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(card.FirstName?.Trim(), TestCardName, StringComparison.OrdinalIgnoreCase))
        {
            return ApiResponse<MyobWriteProbeDto>.Error(
                "该员工卡不是启用中的 test test 专用测试卡，探针拒绝写入", MyobWriteProbeErrorCodes.TestCardNotAllowed);
        }

        var run = new Runner(
            _client, connection.BusinessId, token, card.Uid, start.Date, weekdayUid, saturdayUid,
            FirstNonEmpty(request.UnassignedCategoryUid, null));
        var dto = await run.ExecuteAsync(cancellationToken);
        dto.ConnectionGuid = connection.ConnectionGuid;
        dto.ConnectionName = connection.BusinessName;
        return ApiResponse<MyobWriteProbeDto>.OK(dto);
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : !string.IsNullOrWhiteSpace(second) ? second.Trim() : null;

    private sealed class Runner
    {
        private readonly IMyobApiClient _client;
        private readonly string _businessId;
        private readonly string _token;
        private readonly string _employeeUid;
        private readonly DateTime _start;
        private readonly DateTime _end;
        private readonly DateTime _wide;
        private readonly string _weekdayUid;
        private readonly string _saturdayUid;
        private readonly string? _unassignedUid;
        private readonly MyobWriteProbeDto _dto = new();
        private string _dateFormat = "date";

        public Runner(
            IMyobApiClient client,
            string businessId,
            string token,
            string employeeUid,
            DateTime start,
            string weekdayUid,
            string saturdayUid,
            string? unassignedUid)
        {
            _client = client;
            _businessId = businessId;
            _token = token;
            _employeeUid = employeeUid;
            _start = start;
            _end = start.AddDays(WindowDays - 1);
            _wide = start.AddDays(RangeDays - 1);
            _weekdayUid = weekdayUid;
            _saturdayUid = saturdayUid;
            _unassignedUid = unassignedUid;
        }

        private MyobWriteProbeVerdictsDto V => _dto.Verdicts;

        public async Task<MyobWriteProbeDto> ExecuteAsync(CancellationToken ct)
        {
            _dto.PeriodStart = Day(_start);
            _dto.PeriodEnd = Day(_wide);

            // 写入前：整个探针区间必须是空的。读不了、或有任何记录，都一次写入不发。
            var pre = await ReadAsync("precheck", "写入前先读整个探针区间：测试卡必须没有任何记录", _start, _wide, ct);
            if (pre.Entries is null)
            {
                return Abort("读不到测试卡的 Timesheet，无法确认区间为空");
            }

            if (pre.Entries.Count > 0)
            {
                return Abort("探针区间内测试卡已有 Timesheet 记录，为避免覆盖真实数据，一次写入都没发");
            }

            try
            {
                await WriteStepsAsync(ct);
            }
            finally
            {
                // 无论成功、失败还是请求被取消，都要尽力清理。
                await CleanupAsync();
            }

            return _dto;
        }

        private MyobWriteProbeDto Abort(string reason)
        {
            _dto.Aborted = true;
            _dto.AbortReason = reason;
            return _dto;
        }

        private async Task WriteStepsAsync(CancellationToken ct)
        {
            var d0 = _start;
            var d1 = _start.AddDays(1);
            var d2 = _start.AddDays(2);

            // ① 日期格式 + 最小请求体（不带 Job / Notes / Processed）
            // 官方文档写的是 YYYY-MM-DD HH:MM:SS，示例却是 yyyy-MM-ddT00:00:00：三种写法依次试，第一个被接受的用于之后所有步骤。
            MyobTimesheetPutResult? put = null;
            foreach (var (format, key, description) in DateFormats)
            {
                _dateFormat = format;
                put = await PutAsync(key, description, _start, _end, Lines((_weekdayUid, d0, 7.5m), (_weekdayUid, d1, 4m)), ct);
                if (put?.Success == true)
                {
                    V.DateFormatAccepted = format;
                    V.MinimalBodyAccepted = true;
                    break;
                }
            }

            if (V.DateFormatAccepted is null)
            {
                V.MinimalBodyAccepted = false;
                return;
            }

            await ReadAsync("read-after-first-put", "读回：日期原文、小时、Processed", _start, _wide, ct);

            // ② 小时精度
            await PutAsync("put-hours-scale", "同一天改写 7.333 小时（看读回保留几位小数）", _start, _end,
                Lines((_weekdayUid, d0, 7.333m), (_weekdayUid, d1, 4m)), ct);
            var scale = await ReadAsync("read-hours-scale", "读回小时", _start, _wide, ct);
            V.HoursReadBack = scale.Entries?.FirstOrDefault(entry => IsDay(entry, d0))?.Hours;

            // ③ 覆盖还是合并
            put = await PutAsync("put-other-category", "只发周六一行（不带先前的平日行）", _start, _end,
                Lines((_saturdayUid, d2, 3m)), ct);
            var replaced = await ReadAsync("read-after-other-category", "读回：平日行还在不在", _start, _wide, ct);
            if (put?.Success == true && replaced.Entries is not null)
            {
                V.PutReplacesAll = replaced.Entries.All(entry => entry.Category != "weekday");
            }

            // ④ 请求区间之外的一天
            put = await PutAsync("put-out-of-range", $"请求区间 {Day(_start)}~{Day(_end)}，但写一条起始日 + {OutOfRangeOffset} 天的记录", _start, _end,
                Lines((_weekdayUid, _start.AddDays(OutOfRangeOffset), 2m)), ct);
            V.OutOfRangeHttpStatus = put?.StatusCode;
            var outside = await ReadAsync("read-after-out-of-range", "读回整个探针区间：越界的一天有没有被存下", _start, _wide, ct);
            if (outside.Entries is not null)
            {
                V.OutOfRangeEntryStored = outside.Entries.Any(entry => IsDay(entry, _start.AddDays(OutOfRangeOffset)));
            }

            // ⑤ 清空办法（逐个降级，哪个有效就停）
            await ClearTestsAsync(ct);

            // ⑥ 可选：测试卡没有的类别
            if (_unassignedUid is not null)
            {
                put = await PutAsync("put-unassigned-category", "写入测试卡没有挂的工资类别", _start, _end,
                    Lines((_unassignedUid, d0, 1m)), ct);
                if (put is not null)
                {
                    V.UnassignedCategoryRejected = !put.Success;
                }

                await ReadAsync("read-after-unassigned", "读回", _start, _wide, ct);
            }
        }

        /// <summary>
        /// 四种清空办法（DELETE、Lines 为空数组、每行 Entries 为空数组、全部改成 0 小时）各自在「刚写好的记录」上独立测一遍，
        /// 不是第一个有效就停，这样结论互不依赖，也能知道哪种最干净。
        /// </summary>
        private async Task ClearTestsAsync(CancellationToken ct)
        {
            if (await SeedAsync("seed-for-delete", ct) is not null)
            {
                var deleted = await DeleteAsync("clear-delete", "DELETE（带 StartDate / EndDate 区间）", _start, _wide, ct);
                V.DeleteHttpStatus = deleted?.StatusCode;
                var read = await ReadAsync("read-after-clear-delete", "读回", _start, _wide, ct);
                V.ClearWithDelete = deleted?.Success == true && read.Entries is { Count: 0 };
            }

            if (await SeedAsync("seed-for-empty-lines", ct) is not null)
            {
                var put = await PutAsync("clear-empty-lines", "Lines 为空数组", _start, _wide, [], ct);
                var read = await ReadAsync("read-after-clear-empty-lines", "读回", _start, _wide, ct);
                V.ClearWithEmptyLines = put?.Success == true && read.Entries is { Count: 0 };
            }

            if (await SeedAsync("seed-for-empty-entries", ct) is not null)
            {
                var put = await PutAsync("clear-empty-entries", "每行 Entries 为空数组", _start, _wide, EmptyEntryLines(), ct);
                var read = await ReadAsync("read-after-clear-empty-entries", "读回", _start, _wide, ct);
                V.ClearWithEmptyEntries = put?.Success == true && read.Entries is { Count: 0 };
            }

            if (await SeedAsync("seed-for-zero-hours", ct) is { } seeded)
            {
                var put = await PutAsync("clear-zero-hours", "把现有每一天的小时改成 0", _start, _wide, ZeroHourLines(seeded), ct);
                var read = await ReadAsync("read-after-clear-zero-hours", "读回", _start, _wide, ct);
                V.ClearWithZeroHours = put?.Success == true && read.Entries is not null && read.Entries.All(entry => entry.Hours is null or 0m);
            }
        }

        /// <summary>清空前先写两条记录并读回确认；写不进或读不到就返回 null（该清空办法跳过）。</summary>
        private async Task<IReadOnlyList<MyobWriteProbeEntryDto>?> SeedAsync(string key, CancellationToken ct)
        {
            var put = await PutAsync(key, "清空前先写两条记录", _start, _end,
                Lines((_weekdayUid, _start, 2m), (_saturdayUid, _start.AddDays(2), 1m)), ct);
            var read = await ReadAsync(key + "-read", "确认已写入", _start, _wide, ct);
            return put?.Success == true && read.Entries is { Count: > 0 } ? read.Entries : null;
        }

        private async Task CleanupAsync()
        {
            var ct = CancellationToken.None;
            var read = await ReadAsync("cleanup-read", "清理前读一次", _start, _wide, ct);
            if (read.Entries is { Count: > 0 })
            {
                // 连只剩 0 小时的记录也尽量清掉（清零测试会留下这样的记录）。已证实有效的办法排在前面，证实无效的跳过，没测到的排后面；零小时垫底。
                var methods = new List<(bool? Verdict, string Key, Func<IReadOnlyList<MyobWriteProbeEntryDto>, Task> Apply)>
                {
                    (V.ClearWithDelete, "cleanup-delete", _ => DeleteAsync("cleanup-delete", "清理", _start, _wide, ct)),
                    (V.ClearWithEmptyLines, "cleanup-empty-lines", _ => PutAsync("cleanup-empty-lines", "清理", _start, _wide, [], ct)),
                    (V.ClearWithEmptyEntries, "cleanup-empty-entries",
                        _ => PutAsync("cleanup-empty-entries", "清理", _start, _wide, EmptyEntryLines(), ct)),
                    (V.ClearWithZeroHours == true ? true : null, "cleanup-zero-hours",
                        entries => PutAsync("cleanup-zero-hours", "清理", _start, _wide, ZeroHourLines(entries), ct)),
                };
                foreach (var (_, key, apply) in methods.Where(item => item.Verdict != false).OrderBy(item => item.Verdict == true ? 0 : 1))
                {
                    await apply(read.Entries ?? []);
                    read = await ReadAsync(key + "-read", "清理后读回", _start, _wide, ct);
                    if (read.Entries is { Count: 0 })
                    {
                        break;
                    }
                }
            }

            var final = await ReadAsync("final-check", "最后确认：区间内是否已清空", _start, _wide, ct);
            var entries = final.Entries;
            V.ResidualEntries = entries?.Count ?? -1;
            V.FinalEmpty = entries is { Count: 0 };
            V.FinalClean = entries is not null && entries.All(entry => entry.Hours is null or 0m);
        }

        // ───────────────────────── 请求辅助 ─────────────────────────

        private async Task<MyobWriteProbeStepDto> ReadAsync(string key, string description, DateTime from, DateTime to, CancellationToken ct)
        {
            var step = NewStep(key, description);
            var watch = Stopwatch.StartNew();
            try
            {
                var lines = await _client.GetTimesheetLinesAsync(_businessId, _token, _employeeUid, from, to, ct);
                step.HttpStatus = 200;
                step.Entries = lines
                    .SelectMany(line => line.Entries.Select(entry => new MyobWriteProbeEntryDto
                    {
                        Category = Label(line.CategoryUid),
                        Date = entry.Date ?? string.Empty,
                        Hours = entry.Hours,
                        Processed = entry.Processed,
                    }))
                    .ToList();
            }
            catch (MyobApiException ex)
            {
                step.HttpStatus = (int?)ex.StatusCode;
                step.ErrorName = ex.Code;
            }

            step.ElapsedMs = watch.ElapsedMilliseconds;
            return step;
        }

        private async Task<MyobTimesheetPutResult?> PutAsync(
            string key,
            string description,
            DateTime from,
            DateTime to,
            IReadOnlyList<MyobTimesheetPutLine> lines,
            CancellationToken ct)
        {
            var step = NewStep(key, description);
            var watch = Stopwatch.StartNew();
            MyobTimesheetPutResult? result = null;
            try
            {
                result = await _client.PutTimesheetAsync(_businessId, _token, _employeeUid, from, to, lines, ct);
                step.HttpStatus = result.StatusCode;
                step.ErrorName = result.ErrorName;
                step.ErrorCode = result.ErrorCode;
            }
            catch (MyobApiException ex)
            {
                step.HttpStatus = (int?)ex.StatusCode;
                step.ErrorName = ex.Code;
            }

            step.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        private async Task<MyobTimesheetPutResult?> DeleteAsync(
            string key, string description, DateTime from, DateTime to, CancellationToken ct)
        {
            var step = NewStep(key, description);
            var watch = Stopwatch.StartNew();
            MyobTimesheetPutResult? result = null;
            try
            {
                result = await _client.DeleteTimesheetAsync(_businessId, _token, _employeeUid, from, to, ct);
                step.HttpStatus = result.StatusCode;
                step.ErrorName = result.ErrorName;
                step.ErrorCode = result.ErrorCode;
            }
            catch (MyobApiException ex)
            {
                step.HttpStatus = (int?)ex.StatusCode;
                step.ErrorName = ex.Code;
            }

            step.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        private MyobWriteProbeStepDto NewStep(string key, string description)
        {
            var step = new MyobWriteProbeStepDto { Key = key, Description = description };
            _dto.Steps.Add(step);
            return step;
        }

        private IReadOnlyList<MyobTimesheetPutLine> Lines(params (string Uid, DateTime Date, decimal Hours)[] items) =>
            items.GroupBy(item => item.Uid)
                .Select(group => new MyobTimesheetPutLine(
                    group.Key,
                    group.Select(item => new MyobTimesheetPutEntry(Format(item.Date), item.Hours)).ToList()))
                .ToList();

        private IReadOnlyList<MyobTimesheetPutLine> EmptyEntryLines()
        {
            var uids = new List<string> { _weekdayUid, _saturdayUid };
            if (_unassignedUid is not null)
            {
                uids.Add(_unassignedUid);
            }

            return uids.Select(uid => new MyobTimesheetPutLine(uid, [])).ToList();
        }

        /// <summary>按读回的记录原样（日期用 MYOB 自己返回的原文）把每天的小时改成 0。</summary>
        private IReadOnlyList<MyobTimesheetPutLine> ZeroHourLines(IReadOnlyList<MyobWriteProbeEntryDto> entries) =>
            entries
                .GroupBy(entry => UidOf(entry.Category))
                .Where(group => group.Key is not null)
                .Select(group => new MyobTimesheetPutLine(
                    group.Key!,
                    group.Select(entry => new MyobTimesheetPutEntry(entry.Date, 0m)).ToList()))
                .ToList();

        private string Label(string? uid) =>
            uid is null ? "other"
            : uid == _weekdayUid ? "weekday"
            : uid == _saturdayUid ? "saturday"
            : uid == _unassignedUid ? "unassigned"
            : "other";

        private string? UidOf(string label) => label switch
        {
            "weekday" => _weekdayUid,
            "saturday" => _saturdayUid,
            "unassigned" => _unassignedUid,
            _ => null,
        };

        private string Format(DateTime date) =>
            date.ToString(
                _dateFormat switch
                {
                    "datetime" => "yyyy-MM-ddT00:00:00",
                    "datetime-space" => "yyyy-MM-dd 00:00:00",
                    _ => "yyyy-MM-dd",
                },
                CultureInfo.InvariantCulture);

        private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static bool IsDay(MyobWriteProbeEntryDto entry, DateTime date) =>
            entry.Date.StartsWith(Day(date), StringComparison.Ordinal);
    }
}
