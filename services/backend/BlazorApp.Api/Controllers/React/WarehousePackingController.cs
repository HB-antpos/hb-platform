using System.Security.Claims;
using BlazorApp.Api.Features.WarehousePacking;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 仓库扫码打包：装箱、封箱、组托盘、指定去向与商品位置查询。移动端和 Web 共用。
/// 只接受登录账号（需要记操作人），权限为 Warehouse.Packing；持有管理仓库 / 管理仓库订货权限时按别名自动具备。
/// </summary>
[ApiController]
[Route("api/react/v1/warehouse-packing")]
[Authorize(Policy = Permissions.Warehouse.Packing)]
public sealed class WarehousePackingController(
    IWarehousePackingService packingService,
    IAuthorizationService authorizationService,
    ILogger<WarehousePackingController> logger
) : ControllerBase
{
    private static readonly string[] ManagerRoleNames = Permissions.SuperAdminRoleNames
        .Concat(Permissions.WarehouseManagerRoleNames)
        .ToArray();

    // ---- 箱 ----

    /// <summary>箱列表；filter：all / packing / sealed / loose（已封箱未上托盘），mine=true 只看自己建的。</summary>
    [HttpGet("cartons")]
    public Task<IActionResult> ListCartons([FromQuery] string? filter, [FromQuery] string? keyword, [FromQuery] bool mine = false) =>
        RunAsync(nameof(ListCartons), op => packingService.ListCartonsAsync(filter, keyword, mine ? op.UserGuid : null));

    [HttpPost("cartons")]
    public Task<IActionResult> CreateCarton([FromBody] WarehousePackingCreateCartonRequestDto request) =>
        RunAsync(nameof(CreateCarton), op => packingService.CreateCartonAsync(request ?? new WarehousePackingCreateCartonRequestDto(), op));

    [HttpGet("cartons/{cartonGuid}")]
    public Task<IActionResult> GetCarton(string cartonGuid) =>
        RunAsync(nameof(GetCarton), _ => packingService.GetCartonAsync(cartonGuid.Trim()));

    /// <summary>扫商品进箱；一码多品时返回 409 CODE_AMBIGUOUS 和候选商品，界面选定后带 productCode 重新提交。</summary>
    [HttpPost("cartons/{cartonGuid}/scan")]
    public Task<IActionResult> ScanIntoCarton(string cartonGuid, [FromBody] WarehousePackingScanRequestDto request) =>
        RunAsync(nameof(ScanIntoCarton), op =>
            packingService.ScanIntoCartonAsync(cartonGuid.Trim(), request ?? new WarehousePackingScanRequestDto(), op));

    /// <summary>直接设置某商品在箱里的总数量；0 表示移除。</summary>
    [HttpPut("cartons/{cartonGuid}/items/{productCode}")]
    public Task<IActionResult> SetCartonItemQuantity(
        string cartonGuid,
        string productCode,
        [FromBody] WarehousePackingSetQuantityRequestDto request
    ) =>
        RunAsync(nameof(SetCartonItemQuantity), op =>
            packingService.SetCartonItemQuantityAsync(cartonGuid.Trim(), productCode, request?.Quantity ?? 0, op));

    [HttpPut("cartons/{cartonGuid}/header")]
    public Task<IActionResult> UpdateCartonHeader(string cartonGuid, [FromBody] WarehousePackingCartonHeaderRequestDto request) =>
        RunAsync(nameof(UpdateCartonHeader), op =>
            packingService.UpdateCartonHeaderAsync(cartonGuid.Trim(), request ?? new WarehousePackingCartonHeaderRequestDto(), op));

    [HttpPost("cartons/{cartonGuid}/seal")]
    public Task<IActionResult> SealCarton(string cartonGuid, [FromBody] WarehousePackingSealRequestDto? request) =>
        RunAsync(nameof(SealCarton), op =>
            packingService.SealCartonAsync(cartonGuid.Trim(), request ?? new WarehousePackingSealRequestDto(), op));

    /// <summary>重新开箱：箱号不变，原标签继续有效；只有仓库经理 / 管理员可以操作。</summary>
    [HttpPost("cartons/{cartonGuid}/reopen")]
    public Task<IActionResult> ReopenCarton(string cartonGuid) =>
        RunAsManagerAsync(nameof(ReopenCarton), "只有仓库经理可以重新开箱", op =>
            packingService.ReopenCartonAsync(cartonGuid.Trim(), op));

    [HttpPost("cartons/{cartonGuid}/void")]
    public Task<IActionResult> VoidCarton(string cartonGuid) =>
        RunAsync(nameof(VoidCarton), op => packingService.VoidCartonAsync(cartonGuid.Trim(), op));

    [HttpPut("cartons/{cartonGuid}/location")]
    public Task<IActionResult> SetCartonLocation(string cartonGuid, [FromBody] WarehousePackingLocationRequestDto? request) =>
        RunAsync(nameof(SetCartonLocation), op =>
            packingService.SetCartonLocationAsync(cartonGuid.Trim(), request?.LocationCode, op));

    [HttpGet("cartons/{cartonGuid}/events")]
    public Task<IActionResult> ListCartonEvents(string cartonGuid) =>
        RunAsync(nameof(ListCartonEvents), _ => packingService.ListEventsAsync(WarehousePackTargetTypes.Carton, cartonGuid.Trim()));

    // ---- 托盘 ----

    /// <summary>托盘列表；filter：all / building / sealed。</summary>
    [HttpGet("pallets")]
    public Task<IActionResult> ListPallets([FromQuery] string? filter, [FromQuery] string? keyword) =>
        RunAsync(nameof(ListPallets), _ => packingService.ListPalletsAsync(filter, keyword));

    [HttpPost("pallets")]
    public Task<IActionResult> CreatePallet([FromBody] WarehousePackingDestinationRequestDto? request) =>
        RunAsync(nameof(CreatePallet), op => packingService.CreatePalletAsync(request ?? new WarehousePackingDestinationRequestDto(), op));

    [HttpGet("pallets/{palletGuid}")]
    public Task<IActionResult> GetPallet(string palletGuid) =>
        RunAsync(nameof(GetPallet), _ => packingService.GetPalletAsync(palletGuid.Trim()));

    /// <summary>扫箱标签把箱放上托盘；去向不一致、箱未封、箱在别的托盘上时返回 409 并带回那只箱。</summary>
    [HttpPost("pallets/{palletGuid}/cartons")]
    public Task<IActionResult> AddCartonToPallet(string palletGuid, [FromBody] WarehousePackingAddCartonRequestDto request) =>
        RunAsync(nameof(AddCartonToPallet), op =>
            packingService.AddCartonToPalletAsync(palletGuid.Trim(), request ?? new WarehousePackingAddCartonRequestDto(), op));

    [HttpDelete("pallets/{palletGuid}/cartons/{cartonGuid}")]
    public Task<IActionResult> RemoveCartonFromPallet(string palletGuid, string cartonGuid) =>
        RunAsync(nameof(RemoveCartonFromPallet), op =>
            packingService.RemoveCartonFromPalletAsync(palletGuid.Trim(), cartonGuid.Trim(), op));

    [HttpPut("pallets/{palletGuid}/header")]
    public Task<IActionResult> UpdatePalletHeader(string palletGuid, [FromBody] WarehousePackingPalletHeaderRequestDto request) =>
        RunAsync(nameof(UpdatePalletHeader), op =>
            packingService.UpdatePalletHeaderAsync(palletGuid.Trim(), request ?? new WarehousePackingPalletHeaderRequestDto(), op));

    [HttpPost("pallets/{palletGuid}/seal")]
    public Task<IActionResult> SealPallet(string palletGuid, [FromBody] WarehousePackingSealRequestDto? request) =>
        RunAsync(nameof(SealPallet), op =>
            packingService.SealPalletAsync(palletGuid.Trim(), request ?? new WarehousePackingSealRequestDto(), op));

    /// <summary>重新开托：托盘号不变；只有仓库经理 / 管理员可以操作。</summary>
    [HttpPost("pallets/{palletGuid}/reopen")]
    public Task<IActionResult> ReopenPallet(string palletGuid) =>
        RunAsManagerAsync(nameof(ReopenPallet), "只有仓库经理可以重新开托", op =>
            packingService.ReopenPalletAsync(palletGuid.Trim(), op));

    [HttpPost("pallets/{palletGuid}/void")]
    public Task<IActionResult> VoidPallet(string palletGuid) =>
        RunAsync(nameof(VoidPallet), op => packingService.VoidPalletAsync(palletGuid.Trim(), op));

    [HttpPut("pallets/{palletGuid}/location")]
    public Task<IActionResult> SetPalletLocation(string palletGuid, [FromBody] WarehousePackingLocationRequestDto? request) =>
        RunAsync(nameof(SetPalletLocation), op =>
            packingService.SetPalletLocationAsync(palletGuid.Trim(), request?.LocationCode, op));

    [HttpGet("pallets/{palletGuid}/events")]
    public Task<IActionResult> ListPalletEvents(string palletGuid) =>
        RunAsync(nameof(ListPalletEvents), _ => packingService.ListEventsAsync(WarehousePackTargetTypes.Pallet, palletGuid.Trim()));

    // ---- 扫码识别、位置查询、主数据 ----

    /// <summary>识别扫到的是箱号（BX…）还是托盘号（PL…）；都不是时返回 404，客户端按商品码处理。</summary>
    [HttpGet("resolve")]
    public Task<IActionResult> Resolve([FromQuery] string? code) =>
        RunAsync(nameof(Resolve), _ => packingService.ResolveCodeAsync(code));

    /// <summary>商品位置查询：这个商品在哪些箱 / 托盘 / 货位，各有多少。</summary>
    [HttpGet("locate")]
    public Task<IActionResult> Locate([FromQuery] string? code) =>
        RunAsync(nameof(Locate), _ => packingService.LocateProductAsync(code));

    [HttpGet("carriers")]
    public Task<IActionResult> ListCarriers([FromQuery] bool includeInactive = false) =>
        RunAsync(nameof(ListCarriers), _ => packingService.ListCarriersAsync(includeInactive));

    [HttpPost("carriers")]
    public Task<IActionResult> CreateCarrier([FromBody] WarehousePackingCarrierSaveRequestDto request) =>
        RunAsync(nameof(CreateCarrier), _ => packingService.SaveCarrierAsync(null, request ?? new WarehousePackingCarrierSaveRequestDto()));

    [HttpPut("carriers/{carrierGuid}")]
    public Task<IActionResult> UpdateCarrier(string carrierGuid, [FromBody] WarehousePackingCarrierSaveRequestDto request) =>
        RunAsManagerAsync(nameof(UpdateCarrier), "只有仓库经理可以修改运输公司", _ =>
            packingService.SaveCarrierAsync(carrierGuid.Trim(), request ?? new WarehousePackingCarrierSaveRequestDto()));

    /// <summary>装箱时选择关联订货单：只列已提交和配货中的订货单。</summary>
    [HttpGet("orders")]
    public Task<IActionResult> SearchOrders([FromQuery] string? keyword) =>
        RunAsync(nameof(SearchOrders), _ => packingService.SearchOrdersAsync(keyword));

    private Task<IActionResult> RunAsManagerAsync<T>(
        string operation,
        string deniedMessage,
        Func<WarehousePackingOperator, Task<WarehousePackingResult<T>>> action
    ) =>
        RunAsync(operation, op => op.IsManager
            ? action(op)
            : Task.FromResult(WarehousePackingResult<T>.Fail(403, WarehousePackingErrorCodes.NotAllowed, deniedMessage)));

    private async Task<IActionResult> RunAsync<T>(
        string operation,
        Func<WarehousePackingOperator, Task<WarehousePackingResult<T>>> action
    )
    {
        try
        {
            if (User.HasClaim("token_use", "browser_extension"))
            {
                return StatusCode(403, new { success = false, errorCode = WarehousePackingErrorCodes.NotAllowed, message = "订货助手令牌不能用于仓库打包" });
            }

            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrEmpty(userGuid))
            {
                return Unauthorized(new { success = false, message = "登录信息无效" });
            }

            var name = User.FindFirst("fullName")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = User.Identity?.Name ?? userGuid;
            }

            var result = await action(new WarehousePackingOperator(userGuid, name.Trim(), await IsManagerAsync()));
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
        catch (Exception exception)
        {
            logger.LogError(exception, "仓库打包接口失败: {Operation}", operation);
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>仓库经理 / 管理员角色，或持有管理仓库、管理仓库订货权限的账号。</summary>
    private async Task<bool> IsManagerAsync()
    {
        var hasManagerRole = ManagerRoleNames.Any(role =>
            User.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && string.Equals(claim.Value, role, StringComparison.OrdinalIgnoreCase)
            )
        );
        if (hasManagerRole)
        {
            return true;
        }

        foreach (var permission in new[] { Permissions.Warehouse.Manage, Permissions.Warehouse.ManageOrders })
        {
            if ((await authorizationService.AuthorizeAsync(User, null, permission)).Succeeded)
            {
                return true;
            }
        }

        return false;
    }

    private string? ResolveCurrentUserGuid()
    {
        var value = User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("userGuid")?.Value
            ?? User.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
