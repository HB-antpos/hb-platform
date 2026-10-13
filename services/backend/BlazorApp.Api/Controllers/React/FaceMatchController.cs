using System.Globalization;
using System.Security.Claims;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 门店人脸比对服务管理端（Web 后台 /system/face-match，设计 13.4、14.5）。
/// 五个权限互相独立：FaceMatch.Manage（档案 / 同意 / 登记 / 审计）、FaceMatch.Traffic.View（客流统计）、
/// FaceMatch.Unwelcome.Manage / Approve / Review（名单提交 / 批准 / 复核与看图）。Admin 角色靠角色放行。
/// 所有响应都带 no-store；图片只经服务端转发字节（Content-Type 只会是 image/jpeg 或 image/png，nosniff）。
/// 业务错误统一转成 ApiResponse（errorCode 是 Web 文案映射的契约），消息固定，不含图片、对象键、密钥。
/// </summary>
[ApiController]
[Route("api/react/v1/face-match")]
[Authorize]
[FaceCameraNoStore]
public sealed class FaceMatchController(
    IFaceStoreProfileService profileService,
    IFaceStaffService staffService,
    IFaceTrafficService trafficService,
    IFaceUnwelcomeService unwelcomeService,
    IAuthorizationService authorizationService) : ControllerBase
{
    private const int MaxFormFiles = 6;

    // ---------- 配置 ----------

    /// <summary>功能开关与当前账号的权限标志；只要持有五个权限之一即可读取。</summary>
    [HttpGet("config")]
    public async Task<IActionResult> Config(CancellationToken cancellationToken)
    {
        var flags = await ResolveFlagsAsync();
        if (!flags.Manage && !flags.TrafficView && !flags.UnwelcomeManage && !flags.UnwelcomeApprove && !flags.UnwelcomeReview)
        {
            return Forbidden();
        }

        return await RunAsync(() => profileService.GetConfigAsync(flags, cancellationToken));
    }

    // ---------- 门店档案 ----------

    [HttpGet("profiles")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> ListProfiles(CancellationToken cancellationToken) =>
        RunAsync(() => profileService.ListAsync(cancellationToken));

    [HttpGet("profiles/{storeCode}")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> GetProfile(string storeCode, CancellationToken cancellationToken) =>
        RunAsync(() => profileService.GetAsync(storeCode, cancellationToken));

    /// <summary>只算闸门、不保存：表单实时提示「缺哪一项」。</summary>
    [HttpPost("profiles/{storeCode}/check")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> CheckProfile(
        string storeCode, [FromBody] FaceStoreProfileUpdateRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => profileService.CheckAsync(storeCode, request ?? new(), cancellationToken));

    [HttpPut("profiles/{storeCode}")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> UpdateProfile(
        string storeCode, [FromBody] FaceStoreProfileUpdateRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => profileService.UpdateAsync(storeCode, request ?? new(), ResolveActor(), cancellationToken));

    // ---------- 员工同意与登记 ----------

    [HttpGet("staff")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> ListStaff(
        [FromQuery] string? storeCode, [FromQuery] string? keyword, CancellationToken cancellationToken) =>
        RunAsync(() => staffService.ListStaffAsync(storeCode ?? string.Empty, keyword, cancellationToken));

    [HttpGet("consents")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> ListConsents(
        [FromQuery] string? userGuid, [FromQuery] bool includeWithdrawn, CancellationToken cancellationToken) =>
        RunAsync(() => staffService.ListConsentsAsync(userGuid, includeWithdrawn, cancellationToken));

    /// <summary>multipart/form-data：userGuid、consentVersion、signedAtUtc、method、nswNoticeDate，文件字段 scan。</summary>
    [HttpPost("consents")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public Task<IActionResult> CreateConsent(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var form = await ReadFormAsync(cancellationToken);
            var scans = await ReadFilesAsync(form, "scan", FaceMatchRules.MaxConsentScanBytes, cancellationToken);
            var request = new FaceConsentCreateRequest
            {
                UserGuid = Field(form, "userGuid"),
                ConsentVersion = Field(form, "consentVersion"),
                SignedAtUtc = ParseUtc(Field(form, "signedAtUtc"), "signedAtUtc"),
                Method = Field(form, "method"),
                NswNoticeDate = Field(form, "nswNoticeDate"),
            };
            return await staffService.CreateConsentAsync(request, scans.FirstOrDefault(), ResolveActor(), cancellationToken);
        });

    [HttpPost("consents/{consentId}/withdraw")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> WithdrawConsent(
        string consentId, [FromBody] FaceConsentWithdrawRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => staffService.WithdrawConsentAsync(consentId, request ?? new(), ResolveActor(), cancellationToken));

    [HttpGet("enrollments")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> ListEnrollments(
        [FromQuery] string? userGuid, [FromQuery] bool activeOnly, CancellationToken cancellationToken) =>
        RunAsync(() => staffService.ListEnrollmentsAsync(userGuid, activeOnly, cancellationToken));

    /// <summary>multipart/form-data：userGuid、consentId，文件字段 photos（3–5 张）。</summary>
    [HttpPost("enrollments")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    [RequestSizeLimit(28 * 1024 * 1024)]
    public Task<IActionResult> CreateEnrollment(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var form = await ReadFormAsync(cancellationToken);
            var photos = await ReadFilesAsync(form, "photos", FaceMatchRules.MaxImageBytes, cancellationToken);
            var request = new FaceEnrollmentCreateRequest
            {
                UserGuid = Field(form, "userGuid"),
                ConsentId = Field(form, "consentId"),
            };
            return await staffService.CreateEnrollmentAsync(request, photos, ResolveActor(), cancellationToken);
        });

    [HttpPost("enrollments/{enrollmentId}/withdraw")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> WithdrawEnrollment(
        string enrollmentId, [FromBody] FaceEnrollmentWithdrawRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => staffService.WithdrawEnrollmentAsync(enrollmentId, request ?? new(), ResolveActor(), cancellationToken));

    // ---------- 客流统计与审计 ----------

    [HttpGet("traffic")]
    [Authorize(Policy = Permissions.FaceMatch.Traffic.View)]
    public Task<IActionResult> Traffic(
        [FromQuery] string? store, [FromQuery] string? from, [FromQuery] string? to, CancellationToken cancellationToken) =>
        RunAsync(() => trafficService.GetTrafficAsync(store ?? string.Empty, from, to, ResolveActor(), cancellationToken));

    [HttpGet("audit")]
    [Authorize(Policy = Permissions.FaceMatch.Manage)]
    public Task<IActionResult> Audit(
        [FromQuery] string? store,
        [FromQuery] string? action,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => trafficService.ListAuditAsync(store, action, from, to, page, pageSize, cancellationToken));

    // ---------- 不受欢迎人员名单：条目 ----------

    /// <summary>条目列表：Manage 或 Approve 任一即可；canApprove 按当前账号的 Approve 权限计算。</summary>
    [HttpGet("unwelcome/entries")]
    public async Task<IActionResult> ListEntries(
        [FromQuery] string? status,
        [FromQuery] string? storeCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var flags = await ResolveFlagsAsync();
        if (!flags.UnwelcomeManage && !flags.UnwelcomeApprove)
        {
            return Forbidden();
        }

        return await RunAsync(() => unwelcomeService.ListEntriesAsync(
            status, storeCode, page, pageSize, ResolveActor(), flags.UnwelcomeApprove, cancellationToken));
    }

    /// <summary>multipart/form-data：incidentCategory、categoryNote、evidenceRef、storeScope（逗号分隔）、expiresAtUtc，文件字段 photos。</summary>
    [HttpPost("unwelcome/entries")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Manage)]
    [RequestSizeLimit(28 * 1024 * 1024)]
    public Task<IActionResult> CreateEntry(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var form = await ReadFormAsync(cancellationToken);
            var photos = await ReadFilesAsync(form, "photos", FaceMatchRules.MaxImageBytes, cancellationToken);
            var request = new FaceUnwelcomeEntryCreateRequest
            {
                IncidentCategory = ParseInt(Field(form, "incidentCategory"), "incidentCategory"),
                CategoryNote = Field(form, "categoryNote"),
                EvidenceRef = Field(form, "evidenceRef"),
                StoreScope = (Field(form, "storeScope") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ExpiresAtUtc = ParseUtc(Field(form, "expiresAtUtc"), "expiresAtUtc"),
            };
            return await unwelcomeService.CreateEntryAsync(request, photos, ResolveActor(), cancellationToken);
        });

    [HttpPost("unwelcome/entries/{entryId}/approve")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Approve)]
    public Task<IActionResult> ApproveEntry(string entryId, CancellationToken cancellationToken) =>
        RunAsync(() => unwelcomeService.ApproveEntryAsync(entryId, ResolveActor(), cancellationToken));

    [HttpPost("unwelcome/entries/{entryId}/withdraw")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Manage)]
    public Task<IActionResult> WithdrawEntry(
        string entryId, [FromBody] FaceUnwelcomeWithdrawRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => unwelcomeService.WithdrawEntryAsync(entryId, request ?? new(), ResolveActor(), cancellationToken));

    [HttpPost("unwelcome/entries/{entryId}/dispute-note")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Manage)]
    public Task<IActionResult> SetDisputeNote(
        string entryId, [FromBody] FaceUnwelcomeDisputeRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => unwelcomeService.SetDisputeNoteAsync(entryId, request ?? new(), ResolveActor(), cancellationToken));

    // ---------- 复核任务 ----------

    [HttpGet("unwelcome/tasks")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Review)]
    public Task<IActionResult> ListTasks(
        [FromQuery] string? status,
        [FromQuery] string? storeCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => unwelcomeService.ListTasksAsync(status, storeCode, page, pageSize, ResolveActor(), cancellationToken));

    [HttpPost("unwelcome/tasks/{taskId}/review")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Review)]
    public Task<IActionResult> ReviewTask(
        string taskId, [FromBody] FaceUnwelcomeReviewRequest? request, CancellationToken cancellationToken) =>
        RunAsync(() => unwelcomeService.ReviewTaskAsync(taskId, request ?? new(), ResolveActor(), cancellationToken));

    /// <summary>
    /// 命中抓拍图（服务端转发字节）。对象键只来自数据库，客户端只传任务编号；仅 Review 权限；
    /// 响应只有图片字节：不缓存、不嗅探、不带 ETag 与任何 COS 响应头。每次查看（含被拒绝）都写持久审计。
    /// </summary>
    [HttpGet("unwelcome/tasks/{taskId}/image")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Review)]
    public Task<IActionResult> TaskImage(string taskId, CancellationToken cancellationToken) =>
        RunImageAsync(() => unwelcomeService.GetTaskImageAsync(taskId, ResolveActor(), cancellationToken));

    /// <summary>条目参考照片（并排对比用），规则同命中抓拍图；index 从 0 起。</summary>
    [HttpGet("unwelcome/tasks/{taskId}/reference/{index:int}")]
    [Authorize(Policy = Permissions.FaceMatch.Unwelcome.Review)]
    public Task<IActionResult> ReferencePhoto(string taskId, int index, CancellationToken cancellationToken) =>
        RunImageAsync(() => unwelcomeService.GetReferencePhotoAsync(taskId, index, ResolveActor(), cancellationToken));

    /// <summary>误报统计（只有计数与驳回率，没有照片）：Manage / Approve / Review 任一即可。</summary>
    [HttpGet("unwelcome/stats")]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken)
    {
        var flags = await ResolveFlagsAsync();
        if (!flags.UnwelcomeManage && !flags.UnwelcomeApprove && !flags.UnwelcomeReview)
        {
            return Forbidden();
        }

        return await RunAsync(() => unwelcomeService.GetStatsAsync(ResolveActor(), cancellationToken));
    }

    // ---------- 辅助 ----------

    private async Task<FaceMatchPermissionFlagsDto> ResolveFlagsAsync() => new()
    {
        Manage = await HasAsync(Permissions.FaceMatch.Manage),
        TrafficView = await HasAsync(Permissions.FaceMatch.Traffic.View),
        UnwelcomeManage = await HasAsync(Permissions.FaceMatch.Unwelcome.Manage),
        UnwelcomeApprove = await HasAsync(Permissions.FaceMatch.Unwelcome.Approve),
        UnwelcomeReview = await HasAsync(Permissions.FaceMatch.Unwelcome.Review),
    };

    private async Task<bool> HasAsync(string permission) =>
        (await authorizationService.AuthorizeAsync(User, null, permission)).Succeeded;

    private IActionResult Forbidden() =>
        StatusCode(403, ApiResponse<object>.Error("没有权限访问人脸比对。", "FACE_MATCH_FORBIDDEN"));

    private FaceMatchActor ResolveActor() => new(
        User.FindFirst("userId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown",
        User.Identity?.Name ?? "unknown",
        IsAdmin: User.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && Permissions.SuperAdminRoleNames.Any(role => claim.Value.Equals(role, StringComparison.OrdinalIgnoreCase))));

    private async Task<IActionResult> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Ok(ApiResponse<T>.OK(await operation()));
        }
        catch (FaceMatchException ex)
        {
            // 业务错误消息由服务层固定构造，不含图片、对象键、密钥。
            return StatusCode(ex.StatusCode, ApiResponse<T>.Error(ex.Message, ex.Code, ex.Details));
        }
    }

    private async Task<IActionResult> RunImageAsync(Func<Task<FaceMatchImageContent>> operation)
    {
        try
        {
            var content = await operation();
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers.ContentDisposition = "inline";
            return File(content.Bytes, content.ContentType);
        }
        catch (FaceMatchException ex)
        {
            // 错误体是统一信封（JSON），消息固定，不含桶名、密钥、签名、对象键或地址。
            return StatusCode(ex.StatusCode, ApiResponse<object>.Error(ex.Message, ex.Code, ex.Details));
        }
    }

    private async Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            throw new FaceMatchException(400, "FACE_MATCH_FIELD_INVALID", "请求必须是 multipart/form-data。");
        }

        try
        {
            return await Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw new FaceMatchException(400, "FACE_MATCH_FIELD_INVALID", "上传内容格式不正确或超过大小限制。");
        }
    }

    private static string? Field(IFormCollection form, string name) =>
        form.TryGetValue(name, out var value) ? value.ToString() : null;

    /// <summary>读取同名文件字段；声明大小超限直接拒绝（不读入内存）。</summary>
    private static async Task<IReadOnlyList<FaceMatchUpload>> ReadFilesAsync(
        IFormCollection form, string name, int maxBytes, CancellationToken cancellationToken)
    {
        var files = form.Files.GetFiles(name);
        if (files.Count > MaxFormFiles)
        {
            throw new FaceMatchException(400, "FACE_MATCH_FIELD_INVALID", "上传的文件过多。", new { field = name });
        }

        var result = new List<FaceMatchUpload>();
        foreach (var file in files)
        {
            if (file.Length > maxBytes)
            {
                throw new FaceMatchException(400, "FACE_MATCH_FIELD_INVALID", "文件过大。", new { field = name });
            }

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream((int)Math.Min(file.Length, maxBytes));
            await stream.CopyToAsync(buffer, cancellationToken);
            result.Add(new FaceMatchUpload(file.FileName, file.ContentType, buffer.ToArray()));
        }

        return result;
    }

    private static DateTime? ParseUtc(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParse(
                value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new FaceMatchException(400, "FACE_MATCH_FIELD_INVALID", $"{field} 不是有效的时间。", new { field });
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static int? ParseInt(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FaceMatchException(400, "FACE_MATCH_FIELD_INVALID", $"{field} 不是有效的数字。", new { field });
    }
}
