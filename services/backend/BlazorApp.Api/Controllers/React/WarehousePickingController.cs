using System.Security.Claims;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 仓库 PDA 订单拣货。终端可以是账号会话（含设备账号），也可以是没有个人账号的纯设备会话，
/// 因此整个控制器匿名放行、在每个请求里手工校验终端（与仓库移动端接口同一做法）。
/// 拣货人与终端分开：请求头带扫员工码换来的凭证时按凭证记人，否则只允许账号本人拣货。
/// </summary>
[ApiController]
[Route("api/react/v1/warehouse-picking")]
[AllowAnonymous]
public sealed class WarehousePickingController(
    IWarehousePickingService pickingService,
    IWarehousePickerService pickerService,
    IWarehousePickingAssignmentService assignmentService,
    WarehousePickerTicketProtector ticketProtector,
    IDeviceRegistrationService deviceRegistrationService,
    IAuthorizationService authorizationService,
    ILogger<WarehousePickingController> logger
) : ControllerBase
{
    internal const string PickerTicketHeader = "X-Picker-Ticket";

    private static readonly string[] PickerManagerRoleNames = Permissions.SuperAdminRoleNames
        .Concat(Permissions.WarehouseManagerRoleNames)
        .ToArray();

    private static readonly string[] WarehouseAccountRoleNames = PickerManagerRoleNames
        .Concat(new[] { "WarehouseStaff", "仓库员工" })
        .ToArray();

    /// <summary>扫员工码确认拣货人，返回绑定本终端的短期凭证。</summary>
    [HttpPost("picker/resolve")]
    public Task<IActionResult> ResolvePicker([FromBody] WarehousePickerResolveRequestDto request) =>
        RunAsync(nameof(ResolvePicker), async () =>
        {
            var terminal = await ResolveTerminalAsync();
            if (terminal.Error != null)
            {
                return terminal.Error;
            }

            return ToActionResult(await pickerService.ResolveBarcodeAsync(request?.Barcode, terminal.TerminalKey));
        });

    [HttpGet("orders")]
    public Task<IActionResult> ListOrders([FromQuery] string? filter, [FromQuery] string? keyword) =>
        RunAsync(nameof(ListOrders), async () =>
        {
            var terminal = await ResolveTerminalAsync();
            if (terminal.Error != null)
            {
                return terminal.Error;
            }

            // 列表不强制确认拣货人：认得出（员工码凭证或账号本人）才算“派给我的”，否则只列公共订单。
            var picker = await ResolvePickerAsync(terminal, recheckEligibility: false);
            return ToActionResult(await pickingService.ListOrdersAsync(filter, keyword, picker.Context?.UserGuid));
        });

    /// <summary>解析配货单二维码（HBSO:订单号）或手输订单号。</summary>
    [HttpGet("orders/resolve")]
    public Task<IActionResult> ResolveOrder([FromQuery] string? code) =>
        RunAsync(nameof(ResolveOrder), async () =>
        {
            var terminal = await ResolveTerminalAsync();
            return terminal.Error ?? ToActionResult(await pickingService.ResolveOrderAsync(code));
        });

    /// <summary>加入拣货：已提交订单转为配货中，登记参与人，返回拣货单。</summary>
    [HttpPost("orders/{orderGuid}/join")]
    public Task<IActionResult> Join(string orderGuid) =>
        RunWithPickerAsync(nameof(Join), recheckEligibility: true, picker =>
            pickingService.JoinAsync(orderGuid.Trim(), picker));

    [HttpGet("orders/{orderGuid}/sheet")]
    public Task<IActionResult> GetSheet(string orderGuid) =>
        RunAsync(nameof(GetSheet), async () =>
        {
            var terminal = await ResolveTerminalAsync();
            return terminal.Error ?? ToActionResult(await pickingService.GetSheetAsync(orderGuid.Trim()));
        });

    /// <summary>一起拣：拣货页在前台时轮询，取各行最新合计与同事位置。</summary>
    [HttpGet("orders/{orderGuid}/progress")]
    public Task<IActionResult> GetProgress(string orderGuid) =>
        RunAsync(nameof(GetProgress), async () =>
        {
            var terminal = await ResolveTerminalAsync();
            return terminal.Error ?? ToActionResult(await pickingService.GetProgressAsync(orderGuid.Trim()));
        });

    [HttpPost("orders/{orderGuid}/records")]
    public Task<IActionResult> AppendRecord(
        string orderGuid,
        [FromBody] WarehousePickingRecordRequestDto request
    ) =>
        RunWithPickerAsync(nameof(AppendRecord), recheckEligibility: false, picker =>
            pickingService.AppendRecordAsync(orderGuid.Trim(), request ?? new WarehousePickingRecordRequestDto(), picker));

    [HttpPut("orders/{orderGuid}/lines/{detailGuid}/total")]
    public Task<IActionResult> SetLineTotal(
        string orderGuid,
        string detailGuid,
        [FromBody] WarehousePickingSetTotalRequestDto request
    ) =>
        RunWithPickerAsync(nameof(SetLineTotal), recheckEligibility: false, picker =>
            pickingService.SetLineTotalAsync(orderGuid.Trim(), detailGuid, request ?? new WarehousePickingSetTotalRequestDto(), picker));

    /// <summary>标记“货位没货”：已拣的保留，剩余记为拣不到；之后又拣到货会自动失效。</summary>
    [HttpPut("orders/{orderGuid}/lines/{detailGuid}/stockout")]
    public Task<IActionResult> MarkStockout(
        string orderGuid,
        string detailGuid,
        [FromBody] WarehousePickingStockoutRequestDto request
    ) =>
        RunWithPickerAsync(nameof(MarkStockout), recheckEligibility: false, picker =>
            pickingService.MarkStockoutAsync(orderGuid.Trim(), detailGuid, request?.Reason ?? 0, picker));

    /// <summary>撤销“货位没货”标记（幂等）。</summary>
    [HttpDelete("orders/{orderGuid}/lines/{detailGuid}/stockout")]
    public Task<IActionResult> ClearStockout(string orderGuid, string detailGuid) =>
        RunWithPickerAsync(nameof(ClearStockout), recheckEligibility: false, picker =>
            pickingService.ClearStockoutAsync(orderGuid.Trim(), detailGuid, picker));

    [HttpPut("orders/{orderGuid}/lines/{detailGuid}/min-order-quantity")]
    public Task<IActionResult> SetMinOrderQuantity(
        string orderGuid,
        string detailGuid,
        [FromBody] WarehousePickingMinOrderQuantityRequestDto request
    ) =>
        RunWithPickerAsync(nameof(SetMinOrderQuantity), recheckEligibility: true, picker =>
            pickingService.SetMinOrderQuantityAsync(orderGuid.Trim(), detailGuid, request?.MinOrderQuantity ?? 0, picker));

    /// <summary>提交拣货：各行已拣合计写入配货数，会话锁定；订单保持配货中，由主管按现有流程出库。</summary>
    [HttpPost("orders/{orderGuid}/submit")]
    public Task<IActionResult> Submit(string orderGuid) =>
        RunWithPickerAsync(nameof(Submit), recheckEligibility: true, picker =>
            pickingService.SubmitAsync(orderGuid.Trim(), picker));

    /// <summary>派单：可以派拣货任务的员工及手上在拣的订单数。</summary>
    [HttpGet("pickers")]
    public Task<IActionResult> ListPickerCandidates() =>
        RunAsAssignerAsync(nameof(ListPickerCandidates), _ => assignmentService.ListCandidatesAsync());

    [HttpGet("orders/{orderGuid}/assignments")]
    public Task<IActionResult> GetAssignments(string orderGuid) =>
        RunAsAssignerAsync(nameof(GetAssignments), _ => assignmentService.GetAsync(orderGuid.Trim()));

    /// <summary>派单预览：按走位顺序把订单行按品种数切段（默认平均），不写库。</summary>
    [HttpPost("orders/{orderGuid}/assignments/preview")]
    public Task<IActionResult> PreviewAssignments(
        string orderGuid,
        [FromBody] WarehousePickingAssignmentPreviewRequestDto request
    ) =>
        RunAsAssignerAsync(nameof(PreviewAssignments), _ =>
            assignmentService.PreviewAsync(orderGuid.Trim(), request ?? new WarehousePickingAssignmentPreviewRequestDto()));

    /// <summary>保存派单：提交预览出来的逐行归属，整单替换本单原有分配。</summary>
    [HttpPut("orders/{orderGuid}/assignments")]
    public Task<IActionResult> SaveAssignments(
        string orderGuid,
        [FromBody] WarehousePickingAssignmentSaveRequestDto request
    ) =>
        RunAsAssignerAsync(nameof(SaveAssignments), assigner =>
            assignmentService.SaveAsync(orderGuid.Trim(), request ?? new WarehousePickingAssignmentSaveRequestDto(), assigner));

    [HttpDelete("orders/{orderGuid}/assignments")]
    public Task<IActionResult> ClearAssignments(string orderGuid) =>
        RunAsAssignerAsync(nameof(ClearAssignments), _ => assignmentService.ClearAsync(orderGuid.Trim()));

    /// <summary>批量派单：每张订单都按同一组员工平均分，逐张返回结果。</summary>
    [HttpPost("assignments/batch")]
    public Task<IActionResult> AssignBatch([FromBody] WarehousePickingBatchAssignRequestDto request) =>
        RunAsAssignerAsync(nameof(AssignBatch), assigner =>
            assignmentService.AssignEvenlyAsync(request ?? new WarehousePickingBatchAssignRequestDto(), assigner));

    /// <summary>分单拣货单打印数据（每段一页）；segmentNo 为空时打印全部段。</summary>
    [HttpGet("orders/{orderGuid}/assignments/slips")]
    public Task<IActionResult> GetAssignmentSlips(string orderGuid, [FromQuery] int? segmentNo) =>
        RunAsAssignerAsync(nameof(GetAssignmentSlips), _ => assignmentService.GetSlipsAsync(orderGuid.Trim(), segmentNo));

    /// <summary>PDA 扫分单条码（HBSP:订单号/段号/版本）：返回订单与段，旧分单返回 SLIP_STALE。</summary>
    [HttpGet("slips/resolve")]
    public Task<IActionResult> ResolveSlip([FromQuery] string? code) =>
        RunAsync(nameof(ResolveSlip), async () =>
        {
            var terminal = await ResolveTerminalAsync();
            return terminal.Error ?? ToActionResult(await assignmentService.ResolveSlipAsync(code));
        });

    /// <summary>扫到不在本单的码时查询商品信息，仅用于界面提示。</summary>
    [HttpGet("lookup")]
    public Task<IActionResult> Lookup([FromQuery] string? code) =>
        RunAsync(nameof(Lookup), async () =>
        {
            var terminal = await ResolveTerminalAsync();
            return terminal.Error ?? ToActionResult(await pickingService.LookupCodeAsync(code));
        });

    private async Task<IActionResult> RunWithPickerAsync<T>(
        string operation,
        bool recheckEligibility,
        Func<WarehousePickerContext, Task<WarehousePickingResult<T>>> action
    )
    {
        return await RunAsync(operation, async () =>
        {
            var terminal = await ResolveTerminalAsync();
            if (terminal.Error != null)
            {
                return terminal.Error;
            }

            var picker = await ResolvePickerAsync(terminal, recheckEligibility);
            if (picker.Error != null)
            {
                return picker.Error;
            }

            return ToActionResult(await action(picker.Context!));
        });
    }

    /// <summary>
    /// 派单只允许登录的仓库经理 / 管理员，或持有管理仓库、管理仓库订货权限的账号；
    /// 纯设备会话和只有拣货权限的员工不能派单。
    /// </summary>
    private async Task<IActionResult> RunAsAssignerAsync<T>(
        string operation,
        Func<WarehousePickingAssigner, Task<WarehousePickingResult<T>>> action
    )
    {
        return await RunAsync(operation, async () =>
        {
            var terminal = await ResolveTerminalAsync();
            if (terminal.Error != null)
            {
                return terminal.Error;
            }

            if (!terminal.IsAccount || string.IsNullOrEmpty(terminal.AuthUserGuid) || !await CanAssignAsync())
            {
                return Forbidden(WarehousePickingErrorCodes.AssignNotAllowed, "只有仓库经理可以分配拣货");
            }

            var name = User.FindFirst("fullName")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = User.Identity?.Name ?? terminal.AuthUserGuid;
            }

            return ToActionResult(await action(new WarehousePickingAssigner(terminal.AuthUserGuid, name.Trim())));
        });
    }

    private async Task<bool> CanAssignAsync()
    {
        if (HasAnyRole(PickerManagerRoleNames))
        {
            return true;
        }

        foreach (var permission in new[] { Permissions.Warehouse.ManageOrders, Permissions.Warehouse.Manage })
        {
            if ((await authorizationService.AuthorizeAsync(User, null, permission)).Succeeded)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IActionResult> RunAsync(string operation, Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "仓库拣货接口失败: {Operation}", operation);
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 终端校验：已登录账号须有仓库角色或拣货权限；未登录时按设备头校验已启用的注册设备。
    /// 终端键用于绑定拣货人凭证：设备（含设备账号）按硬件码，普通账号按用户 GUID。
    /// </summary>
    private async Task<TerminalAccess> ResolveTerminalAsync()
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            if (User.HasClaim("token_use", "browser_extension"))
            {
                return TerminalAccess.Denied(Forbidden(WarehousePickingErrorCodes.PickerNotAllowed, "订货助手令牌不能用于仓库拣货"));
            }

            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrEmpty(userGuid))
            {
                return TerminalAccess.Denied(Unauthorized(new { success = false, message = "登录信息无效" }));
            }

            if (!await IsWarehouseAccountAsync())
            {
                return TerminalAccess.Denied(Forbidden(WarehousePickingErrorCodes.PickerNotAllowed, "当前账号没有仓库拣货权限"));
            }

            var hardwareId = User.FindFirst(MobileDeviceAccountTokenIssuer.HardwareIdClaim)?.Value;
            return TerminalAccess.Allowed(
                !string.IsNullOrWhiteSpace(hardwareId) ? $"hw:{hardwareId.Trim()}" : $"user:{userGuid}",
                authUserGuid: userGuid,
                deviceCode: null,
                isAccount: true
            );
        }

        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault()?.Trim();
        var authCode = Request.Headers["X-Auth-Code"].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(authCode))
        {
            return TerminalAccess.Denied(Unauthorized(new { success = false, message = "未登录且缺少设备授权信息" }));
        }

        if (!await deviceRegistrationService.ValidateDeviceAuthCodeAsync(deviceId, authCode))
        {
            return TerminalAccess.Denied(Unauthorized(new { success = false, message = "设备授权无效" }));
        }

        var device = await deviceRegistrationService.GetDeviceByHardwareIdAsync(deviceId);
        if (device == null || device.设备状态 != 1)
        {
            return TerminalAccess.Denied(Unauthorized(new { success = false, message = "设备未启用" }));
        }

        return TerminalAccess.Allowed(
            $"hw:{deviceId}",
            authUserGuid: null,
            deviceCode: string.IsNullOrWhiteSpace(device.系统设备编号) ? deviceId : device.系统设备编号,
            isAccount: false
        );
    }

    private async Task<PickerResolution> ResolvePickerAsync(TerminalAccess terminal, bool recheckEligibility)
    {
        var ticket = Request.Headers[PickerTicketHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(ticket))
        {
            var validation = ticketProtector.Validate(ticket, terminal.TerminalKey, DateTime.UtcNow);
            if (validation.Status == WarehousePickerTicketStatus.Expired)
            {
                return PickerResolution.Denied(Forbidden(WarehousePickingErrorCodes.PickerTicketExpired, "拣货人确认已过期，请重新扫描员工码"));
            }

            if (validation.Status != WarehousePickerTicketStatus.Valid || validation.Identity == null)
            {
                return PickerResolution.Denied(Forbidden(WarehousePickingErrorCodes.PickerTicketInvalid, "拣货人确认无效，请重新扫描员工码"));
            }

            var identity = validation.Identity;
            var canOverwrite = identity.CanOverwriteMinOrderQuantity;
            var name = identity.Name;
            if (recheckEligibility)
            {
                // 凭证有效期较长：加入、提交、改中包数这些低频关键操作重新核对员工是否仍可拣货。
                var eligibility = await pickerService.GetEligibilityAsync(identity.UserGuid);
                if (eligibility == null || !eligibility.IsAllowed)
                {
                    return PickerResolution.Denied(Forbidden(WarehousePickingErrorCodes.PickerNotAllowed, "该员工已不能拣货，请重新确认拣货人"));
                }

                canOverwrite = eligibility.CanOverwriteMinOrderQuantity;
                name = eligibility.Name;
            }

            return PickerResolution.Of(new WarehousePickerContext(
                identity.UserGuid,
                name,
                canOverwrite,
                terminal.AuthUserGuid,
                terminal.DeviceCode
            ));
        }

        if (!terminal.IsAccount || string.IsNullOrEmpty(terminal.AuthUserGuid))
        {
            return PickerResolution.Denied(Forbidden(WarehousePickingErrorCodes.PickerRequired, "请先扫描员工码确认拣货人"));
        }

        var accountName = User.FindFirst("fullName")?.Value;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = User.Identity?.Name ?? terminal.AuthUserGuid;
        }

        return PickerResolution.Of(new WarehousePickerContext(
            terminal.AuthUserGuid,
            accountName.Trim(),
            HasAnyRole(PickerManagerRoleNames),
            terminal.AuthUserGuid,
            terminal.DeviceCode
        ));
    }

    private async Task<bool> IsWarehouseAccountAsync()
    {
        if (HasAnyRole(WarehouseAccountRoleNames))
        {
            return true;
        }

        // 权限策略按别名展开：持有 Warehouse.Manage / Warehouse.ManageOrders 的账号同样具备拣货权限。
        var result = await authorizationService.AuthorizeAsync(User, null, Permissions.Warehouse.Picking);
        return result.Succeeded;
    }

    private bool HasAnyRole(IEnumerable<string> roles)
    {
        return roles.Any(role =>
            User.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && string.Equals(claim.Value, role, StringComparison.OrdinalIgnoreCase)
            )
        );
    }

    private string? ResolveCurrentUserGuid()
    {
        var value = User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("userGuid")?.Value
            ?? User.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private ObjectResult Forbidden(string errorCode, string message) =>
        StatusCode(403, new { success = false, errorCode, message });

    private IActionResult ToActionResult<T>(WarehousePickingResult<T> result)
    {
        if (result.Success)
        {
            return Ok(new { success = true, data = result.Data });
        }

        return StatusCode(
            result.StatusCode,
            new
            {
                success = false,
                errorCode = result.ErrorCode,
                message = result.Message,
                data = result.Data,
            }
        );
    }

    private sealed record TerminalAccess(
        IActionResult? Error,
        string TerminalKey,
        string? AuthUserGuid,
        string? DeviceCode,
        bool IsAccount
    )
    {
        public static TerminalAccess Denied(IActionResult error) => new(error, string.Empty, null, null, false);

        public static TerminalAccess Allowed(string terminalKey, string? authUserGuid, string? deviceCode, bool isAccount) =>
            new(null, terminalKey, authUserGuid, deviceCode, isAccount);
    }

    private sealed record PickerResolution(IActionResult? Error, WarehousePickerContext? Context)
    {
        public static PickerResolution Denied(IActionResult error) => new(error, null);

        public static PickerResolution Of(WarehousePickerContext context) => new(null, context);
    }
}
