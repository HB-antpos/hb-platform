using BlazorApp.Api.Services.StoreCash;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 分店现金管理入口：店长把现金存银行（多张存单拍照）、记现金支出，管理员与财务查看统计。
/// 权限与范围规则全部在 <see cref="IStoreCashService"/> 内统一处理（分店范围、T2 可见性、补录与作废限制），
/// 这里只做权限码门禁和 HTTP 映射，不重复写业务判断。新权限码不写入角色模板，上线后需在角色管理里授权。
/// </summary>
[ApiController]
[Route("api/react/v1/cash")]
[Authorize]
public sealed class StoreCashController : ControllerBase
{
    private readonly IStoreCashService _service;
    private readonly ICashAccessResolver _accessResolver;
    private readonly ILogger<StoreCashController> _logger;

    public StoreCashController(
        IStoreCashService service,
        ICashAccessResolver accessResolver,
        ILogger<StoreCashController> logger
    )
    {
        _service = service;
        _accessResolver = accessResolver;
        _logger = logger;
    }

    // ───────────────────────── 总览与日结 ─────────────────────────

    [HttpGet("context")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> GetContext(CancellationToken cancellationToken) =>
        RunAsync(access => _service.GetContextAsync(access, cancellationToken), cancellationToken);

    [HttpGet("summary")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> GetSummary([FromQuery] string? storeCode, CancellationToken cancellationToken) =>
        RunAsync(access => _service.GetSummaryAsync(access, storeCode, cancellationToken), cancellationToken);

    /// <summary>Web 多店总览；storeCodes 只表示可见范围的子集，越权分店直接忽略。</summary>
    [HttpGet("overview")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> GetOverview(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] List<string>? storeCodes,
        CancellationToken cancellationToken
    ) => RunAsync(access => _service.GetOverviewAsync(access, from, to, storeCodes, cancellationToken), cancellationToken);

    [HttpGet("daily")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> GetDaily(
        [FromQuery] string? storeCode,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken
    )
    {
        if (!from.HasValue || !to.HasValue)
        {
            return Task.FromResult<IActionResult>(
                BadRequest(
                    ApiResponse<CashDailyDto>.Error("请提供起止日期", StoreCashConstants.ErrorCodes.InvalidRequest)
                )
            );
        }

        return RunAsync(
            access => _service.GetDailyAsync(access, storeCode, from.Value, to.Value, cancellationToken),
            cancellationToken
        );
    }

    [HttpPut("close-selection")]
    [Authorize(Policy = Permissions.Cash.DepositCreate)]
    public Task<IActionResult> SetCloseSelection(
        [FromBody] CashCloseSelectionRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(access => _service.SetCloseSelectionAsync(access, request, cancellationToken), cancellationToken);

    // ───────────────────────── 图片上传 ─────────────────────────

    /// <summary>存单与收据共用；具体能不能上传（存款或支出权限、分店范围）由服务判断。</summary>
    [HttpPost("attachments/upload-signature")]
    public Task<IActionResult> CreateAttachmentUpload(
        [FromBody] CashAttachmentUploadRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(access => _service.CreateAttachmentUploadAsync(access, request, cancellationToken), cancellationToken);

    // ───────────────────────── 存款 ─────────────────────────

    [HttpGet("deposits")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> ListDeposits(
        [FromQuery] string? storeCode,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] bool includeVoided = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default
    ) => RunAsync(
        access => _service.ListDepositsAsync(access, storeCode, from, to, includeVoided, limit, offset, cancellationToken),
        cancellationToken
    );

    [HttpGet("deposits/{depositGuid}")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> GetDeposit(string depositGuid, CancellationToken cancellationToken) =>
        RunAsync(access => _service.GetDepositAsync(access, depositGuid, cancellationToken), cancellationToken);

    [HttpPost("deposits")]
    [Authorize(Policy = Permissions.Cash.DepositCreate)]
    public Task<IActionResult> CreateDeposit(
        [FromBody] CreateCashDepositRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(access => _service.CreateDepositAsync(access, request, cancellationToken), cancellationToken);

    /// <summary>作废：持有 Cash.Void 或本人时限内，由服务判断；这里只要求登录。</summary>
    [HttpPost("deposits/{depositGuid}/void")]
    public Task<IActionResult> VoidDeposit(
        string depositGuid,
        [FromBody] CashVoidRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(
        access => _service.VoidDepositAsync(access, depositGuid, request ?? new CashVoidRequest(), cancellationToken),
        cancellationToken
    );

    // ───────────────────────── 支出 ─────────────────────────

    [HttpGet("expenses")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> ListExpenses(
        [FromQuery] string? storeCode,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? category,
        [FromQuery] string? reviewStatus,
        [FromQuery] bool includeVoided = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default
    ) => RunAsync(
        access => _service.ListExpensesAsync(
            access,
            storeCode,
            from,
            to,
            category,
            reviewStatus,
            includeVoided,
            limit,
            offset,
            cancellationToken
        ),
        cancellationToken
    );

    [HttpGet("expenses/{expenseGuid}")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> GetExpense(string expenseGuid, CancellationToken cancellationToken) =>
        RunAsync(access => _service.GetExpenseAsync(access, expenseGuid, cancellationToken), cancellationToken);

    [HttpPost("expenses")]
    [Authorize(Policy = Permissions.Cash.ExpenseCreate)]
    public Task<IActionResult> CreateExpense(
        [FromBody] CreateCashExpenseRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(access => _service.CreateExpenseAsync(access, request, cancellationToken), cancellationToken);

    /// <summary>财务事后核对标记（已核 / 存疑 / 清除），与作废同属 Cash.Void。</summary>
    [HttpPost("expenses/{expenseGuid}/review")]
    [Authorize(Policy = Permissions.Cash.Void)]
    public Task<IActionResult> ReviewExpense(
        string expenseGuid,
        [FromBody] CashExpenseReviewRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(
        access => _service.ReviewExpenseAsync(access, expenseGuid, request ?? new CashExpenseReviewRequest(), cancellationToken),
        cancellationToken
    );

    [HttpPost("expenses/{expenseGuid}/void")]
    public Task<IActionResult> VoidExpense(
        string expenseGuid,
        [FromBody] CashVoidRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(
        access => _service.VoidExpenseAsync(access, expenseGuid, request ?? new CashVoidRequest(), cancellationToken),
        cancellationToken
    );

    // ───────────────────────── 期初与盘点 ─────────────────────────

    [HttpGet("entries")]
    [Authorize(Policy = Permissions.Cash.OverviewView)]
    public Task<IActionResult> ListEntries(
        [FromQuery] string? storeCode,
        [FromQuery] bool includeVoided = false,
        CancellationToken cancellationToken = default
    ) => RunAsync(
        access => _service.ListEntriesAsync(access, storeCode, includeVoided, cancellationToken),
        cancellationToken
    );

    [HttpPut("opening")]
    [Authorize(Policy = Permissions.Cash.DepositCreate)]
    public Task<IActionResult> SetOpening(
        [FromBody] SetCashOpeningRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(access => _service.SetOpeningAsync(access, request, cancellationToken), cancellationToken);

    [HttpPost("counts")]
    [Authorize(Policy = Permissions.Cash.DepositCreate)]
    public Task<IActionResult> CreateCount(
        [FromBody] CreateCashCountRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(access => _service.CreateCountAsync(access, request, cancellationToken), cancellationToken);

    [HttpPost("entries/{entryGuid}/void")]
    public Task<IActionResult> VoidEntry(
        string entryGuid,
        [FromBody] CashVoidRequest request,
        CancellationToken cancellationToken
    ) => RunAsync(
        access => _service.VoidEntryAsync(access, entryGuid, request ?? new CashVoidRequest(), cancellationToken),
        cancellationToken
    );

    // ───────────────────────── 公共 ─────────────────────────

    private async Task<IActionResult> RunAsync<T>(
        Func<CashAccess, Task<ApiResponse<T>>> action,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var access = await _accessResolver.ResolveAsync(cancellationToken);
            var result = await action(access);
            return result.Success ? Ok(result) : StatusCode(MapStatusCode(result.ErrorCode), result);
        }
        catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, HttpContext.RequestAborted))
        {
            // 客户端已断开：不按 500 记错误。
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "现金管理请求失败：{Path}", Request.Path);
            return StatusCode(500, ApiResponse<T>.Error("服务器内部错误", "CASH_INTERNAL_ERROR"));
        }
    }

    /// <summary>错误码到 HTTP 状态码：越权 403，记录或分店不存在 404，冲突 409，其余参数与规则错误 400。</summary>
    internal static int MapStatusCode(string? errorCode) =>
        errorCode switch
        {
            StoreCashConstants.ErrorCodes.StoreForbidden or StoreCashConstants.ErrorCodes.VoidNotAllowed =>
                StatusCodes.Status403Forbidden,
            StoreCashConstants.ErrorCodes.StoreNotFound
                or StoreCashConstants.ErrorCodes.RecordNotFound
                or StoreCashConstants.ErrorCodes.CloseNotFound => StatusCodes.Status404NotFound,
            StoreCashConstants.ErrorCodes.Conflict or StoreCashConstants.ErrorCodes.OpeningExists =>
                StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
}
