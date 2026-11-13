using BlazorApp.Api.Features.Packing;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 扫码打包：分店和总仓共用。装箱、封箱、组托盘、指定去向、店间调货发出与扫码签收、商品位置查询。
/// 只接受登录账号（要记操作人）。能操作哪些分店 / 总仓由 IPackingAccessService 按账号算出，
/// 服务层只认「当前操作人」里的地点，不会跨店读写。
/// </summary>
[ApiController]
[Route("api/react/v1/packing")]
[Authorize]
public sealed class PackingController(
    IPackingService packingService,
    IPackingAccessService accessService,
    ILogger<PackingController> logger
) : ControllerBase
{
    /// <summary>当前账号能操作的地点（分店 / 总仓），界面据此显示分店切换。</summary>
    [HttpGet("scope")]
    public Task<IActionResult> GetScope() =>
        RunAsync(nameof(GetScope), op => Task.FromResult(PackingResult<PackingScopeDto>.Ok(new PackingScopeDto
        {
            IsManager = op.IsManager,
            IsAdmin = op.IsAdmin,
            Owners = op.Owners.Select(owner => new PackingOwnerDto { Code = owner.Code, Name = owner.Name, IsWarehouse = owner.IsWarehouse }).ToList(),
        })));

    // ---- 箱 ----

    /// <summary>箱列表；filter：all / packing / stored / outgoing，mine=true 只看自己建的。</summary>
    [HttpGet("cartons")]
    public Task<IActionResult> ListCartons([FromQuery] string? ownerCode, [FromQuery] string? filter, [FromQuery] string? keyword, [FromQuery] bool mine = false) =>
        RunAsync(nameof(ListCartons), op => packingService.ListCartonsAsync(op, ownerCode, filter, keyword, mine));

    [HttpPost("cartons")]
    public Task<IActionResult> CreateCarton([FromBody] PackingCreateCartonRequestDto request) =>
        RunAsync(nameof(CreateCarton), op => packingService.CreateCartonAsync(op, request ?? new PackingCreateCartonRequestDto()));

    [HttpGet("cartons/{cartonGuid}")]
    public Task<IActionResult> GetCarton(string cartonGuid) =>
        RunAsync(nameof(GetCarton), op => packingService.GetCartonAsync(op, cartonGuid.Trim()));

    /// <summary>扫商品进箱；一码多品时返回 409 CODE_AMBIGUOUS 和候选商品，界面选定后带 productCode 重新提交。</summary>
    [HttpPost("cartons/{cartonGuid}/scan")]
    public Task<IActionResult> ScanIntoCarton(string cartonGuid, [FromBody] PackingScanRequestDto request) =>
        RunAsync(nameof(ScanIntoCarton), op => packingService.ScanIntoCartonAsync(op, cartonGuid.Trim(), request ?? new PackingScanRequestDto()));

    /// <summary>直接设置某商品在箱里的总数量；0 表示移除。</summary>
    [HttpPut("cartons/{cartonGuid}/items/{productCode}")]
    public Task<IActionResult> SetCartonItemQuantity(string cartonGuid, string productCode, [FromBody] PackingSetQuantityRequestDto request) =>
        RunAsync(nameof(SetCartonItemQuantity), op =>
            packingService.SetCartonItemQuantityAsync(op, cartonGuid.Trim(), productCode, request?.Quantity ?? 0));

    [HttpPut("cartons/{cartonGuid}/header")]
    public Task<IActionResult> UpdateCartonHeader(string cartonGuid, [FromBody] PackingCartonHeaderRequestDto request) =>
        RunAsync(nameof(UpdateCartonHeader), op =>
            packingService.UpdateCartonHeaderAsync(op, cartonGuid.Trim(), request ?? new PackingCartonHeaderRequestDto()));

    [HttpPost("cartons/{cartonGuid}/seal")]
    public Task<IActionResult> SealCarton(string cartonGuid, [FromBody] PackingSealRequestDto? request) =>
        RunAsync(nameof(SealCarton), op => packingService.SealCartonAsync(op, cartonGuid.Trim(), request ?? new PackingSealRequestDto()));

    /// <summary>重新开箱：箱号不变，原标签继续有效；只有店长 / 仓库经理 / 管理员可以操作。</summary>
    [HttpPost("cartons/{cartonGuid}/reopen")]
    public Task<IActionResult> ReopenCarton(string cartonGuid) =>
        RunAsync(nameof(ReopenCarton), op => packingService.ReopenCartonAsync(op, cartonGuid.Trim()));

    [HttpPost("cartons/{cartonGuid}/void")]
    public Task<IActionResult> VoidCarton(string cartonGuid) =>
        RunAsync(nameof(VoidCarton), op => packingService.VoidCartonAsync(op, cartonGuid.Trim()));

    [HttpPut("cartons/{cartonGuid}/location")]
    public Task<IActionResult> SetCartonLocation(string cartonGuid, [FromBody] PackingLocationRequestDto? request) =>
        RunAsync(nameof(SetCartonLocation), op => packingService.SetCartonLocationAsync(op, cartonGuid.Trim(), request?.LocationCode));

    /// <summary>发出散箱：去向必须是其他分店或总仓，发出后由对方扫码签收。</summary>
    [HttpPost("cartons/{cartonGuid}/dispatch")]
    public Task<IActionResult> DispatchCarton(string cartonGuid) =>
        RunAsync(nameof(DispatchCarton), op => packingService.DispatchCartonAsync(op, cartonGuid.Trim()));

    /// <summary>签收一只箱（散箱，或托盘上「只签收这一箱」）。</summary>
    [HttpPost("cartons/{cartonGuid}/receive")]
    public Task<IActionResult> ReceiveCarton(string cartonGuid, [FromBody] PackingReceiveCartonRequestDto? request) =>
        RunAsync(nameof(ReceiveCarton), op =>
            packingService.ReceiveCartonAsync(op, cartonGuid.Trim(), request ?? new PackingReceiveCartonRequestDto()));

    [HttpGet("cartons/{cartonGuid}/events")]
    public Task<IActionResult> ListCartonEvents(string cartonGuid) =>
        RunAsync(nameof(ListCartonEvents), op => packingService.ListEventsAsync(op, PackTargetTypes.Carton, cartonGuid.Trim()));

    // ---- 托盘 ----

    /// <summary>托盘列表；filter：all / building / stored / outgoing。</summary>
    [HttpGet("pallets")]
    public Task<IActionResult> ListPallets([FromQuery] string? ownerCode, [FromQuery] string? filter, [FromQuery] string? keyword) =>
        RunAsync(nameof(ListPallets), op => packingService.ListPalletsAsync(op, ownerCode, filter, keyword));

    [HttpPost("pallets")]
    public Task<IActionResult> CreatePallet([FromBody] PackingCreatePalletRequestDto? request) =>
        RunAsync(nameof(CreatePallet), op => packingService.CreatePalletAsync(op, request ?? new PackingCreatePalletRequestDto()));

    [HttpGet("pallets/{palletGuid}")]
    public Task<IActionResult> GetPallet(string palletGuid) =>
        RunAsync(nameof(GetPallet), op => packingService.GetPalletAsync(op, palletGuid.Trim()));

    /// <summary>扫箱标签把箱放上托盘；去向不一致、箱未封、箱在别的托盘上时返回 409 并带回那只箱。</summary>
    [HttpPost("pallets/{palletGuid}/cartons")]
    public Task<IActionResult> AddCartonToPallet(string palletGuid, [FromBody] PackingAddCartonRequestDto request) =>
        RunAsync(nameof(AddCartonToPallet), op =>
            packingService.AddCartonToPalletAsync(op, palletGuid.Trim(), request ?? new PackingAddCartonRequestDto()));

    [HttpDelete("pallets/{palletGuid}/cartons/{cartonGuid}")]
    public Task<IActionResult> RemoveCartonFromPallet(string palletGuid, string cartonGuid) =>
        RunAsync(nameof(RemoveCartonFromPallet), op => packingService.RemoveCartonFromPalletAsync(op, palletGuid.Trim(), cartonGuid.Trim()));

    [HttpPut("pallets/{palletGuid}/header")]
    public Task<IActionResult> UpdatePalletHeader(string palletGuid, [FromBody] PackingPalletHeaderRequestDto request) =>
        RunAsync(nameof(UpdatePalletHeader), op =>
            packingService.UpdatePalletHeaderAsync(op, palletGuid.Trim(), request ?? new PackingPalletHeaderRequestDto()));

    [HttpPost("pallets/{palletGuid}/seal")]
    public Task<IActionResult> SealPallet(string palletGuid, [FromBody] PackingSealRequestDto? request) =>
        RunAsync(nameof(SealPallet), op => packingService.SealPalletAsync(op, palletGuid.Trim(), request ?? new PackingSealRequestDto()));

    /// <summary>重新开托：托盘号不变；只有店长 / 仓库经理 / 管理员可以操作。</summary>
    [HttpPost("pallets/{palletGuid}/reopen")]
    public Task<IActionResult> ReopenPallet(string palletGuid) =>
        RunAsync(nameof(ReopenPallet), op => packingService.ReopenPalletAsync(op, palletGuid.Trim()));

    [HttpPost("pallets/{palletGuid}/void")]
    public Task<IActionResult> VoidPallet(string palletGuid) =>
        RunAsync(nameof(VoidPallet), op => packingService.VoidPalletAsync(op, palletGuid.Trim()));

    [HttpPut("pallets/{palletGuid}/location")]
    public Task<IActionResult> SetPalletLocation(string palletGuid, [FromBody] PackingLocationRequestDto? request) =>
        RunAsync(nameof(SetPalletLocation), op => packingService.SetPalletLocationAsync(op, palletGuid.Trim(), request?.LocationCode));

    /// <summary>发出整个托盘：托盘上的箱一起变成在路上，由对方扫码签收。</summary>
    [HttpPost("pallets/{palletGuid}/dispatch")]
    public Task<IActionResult> DispatchPallet(string palletGuid) =>
        RunAsync(nameof(DispatchPallet), op => packingService.DispatchPalletAsync(op, palletGuid.Trim()));

    /// <summary>
    /// 签收托盘：整托签收必须带实到箱数（和系统记录不一致返回 409 COUNT_MISMATCH）；
    /// 带 cartonGuids 表示只签收这几只箱（逐箱核对）。
    /// </summary>
    [HttpPost("pallets/{palletGuid}/receive")]
    public Task<IActionResult> ReceivePallet(string palletGuid, [FromBody] PackingReceivePalletRequestDto request) =>
        RunAsync(nameof(ReceivePallet), op =>
            packingService.ReceivePalletAsync(op, palletGuid.Trim(), request ?? new PackingReceivePalletRequestDto()));

    [HttpGet("pallets/{palletGuid}/events")]
    public Task<IActionResult> ListPalletEvents(string palletGuid) =>
        RunAsync(nameof(ListPalletEvents), op => packingService.ListEventsAsync(op, PackTargetTypes.Pallet, palletGuid.Trim()));

    // ---- 签收 ----

    /// <summary>待签收（发给本地点、还没签完）和最近签收的托盘 / 散箱。</summary>
    [HttpGet("incoming")]
    public Task<IActionResult> ListIncoming([FromQuery] string? ownerCode) =>
        RunAsync(nameof(ListIncoming), op => packingService.ListIncomingAsync(op, ownerCode));

    /// <summary>扫托盘标签或箱标签找到要签收的东西；托盘标签丢了时扫托盘上任意一只箱的标签，同样返回整个托盘。</summary>
    [HttpGet("receive/lookup")]
    public Task<IActionResult> LookupForReceive([FromQuery] string? code) =>
        RunAsync(nameof(LookupForReceive), op => packingService.LookupForReceiveAsync(op, code));

    // ---- 扫码识别、位置查询、主数据 ----

    /// <summary>识别扫到的是箱号（BX…）还是托盘号（PL…）；都不是时返回 404，客户端按商品码处理。</summary>
    [HttpGet("resolve")]
    public Task<IActionResult> Resolve([FromQuery] string? code) =>
        RunAsync(nameof(Resolve), op => packingService.ResolveCodeAsync(op, code));

    /// <summary>商品位置查询：这个商品在哪些箱 / 托盘 / 位置，各有多少。allStores 只有店长 / 管理员可用。</summary>
    [HttpGet("locate")]
    public Task<IActionResult> Locate([FromQuery] string? ownerCode, [FromQuery] string? code, [FromQuery] bool allStores = false) =>
        RunAsync(nameof(Locate), op => packingService.LocateProductAsync(op, ownerCode, code, allStores));

    [HttpGet("carriers")]
    public Task<IActionResult> ListCarriers([FromQuery] bool includeInactive = false) =>
        RunAsync(nameof(ListCarriers), _ => packingService.ListCarriersAsync(includeInactive));

    [HttpPost("carriers")]
    public Task<IActionResult> CreateCarrier([FromBody] PackingCarrierSaveRequestDto request) =>
        RunAsync(nameof(CreateCarrier), op => packingService.SaveCarrierAsync(op, null, request ?? new PackingCarrierSaveRequestDto()));

    [HttpPut("carriers/{carrierGuid}")]
    public Task<IActionResult> UpdateCarrier(string carrierGuid, [FromBody] PackingCarrierSaveRequestDto request) =>
        RunAsync(nameof(UpdateCarrier), op => packingService.SaveCarrierAsync(op, carrierGuid.Trim(), request ?? new PackingCarrierSaveRequestDto()));

    /// <summary>分店存放位置（总仓返回现有货位）。</summary>
    [HttpGet("locations")]
    public Task<IActionResult> ListLocations([FromQuery] string? ownerCode, [FromQuery] bool includeInactive = false) =>
        RunAsync(nameof(ListLocations), op => packingService.ListLocationsAsync(op, ownerCode, includeInactive));

    [HttpPost("locations")]
    public Task<IActionResult> CreateLocation([FromBody] PackingLocationSaveRequestDto request) =>
        RunAsync(nameof(CreateLocation), op => packingService.SaveLocationAsync(op, null, request ?? new PackingLocationSaveRequestDto()));

    [HttpPut("locations/{locationGuid}")]
    public Task<IActionResult> UpdateLocation(string locationGuid, [FromBody] PackingLocationSaveRequestDto request) =>
        RunAsync(nameof(UpdateLocation), op => packingService.SaveLocationAsync(op, locationGuid.Trim(), request ?? new PackingLocationSaveRequestDto()));

    /// <summary>总仓装箱时选择关联订货单（二期边拣边装用）：只列已提交和配货中的订货单。</summary>
    [HttpGet("orders")]
    public Task<IActionResult> SearchOrders([FromQuery] string? keyword) =>
        RunAsync(nameof(SearchOrders), op => packingService.SearchOrdersAsync(op, keyword));

    private async Task<IActionResult> RunAsync<T>(string operation, Func<PackingOperator, Task<PackingResult<T>>> action)
    {
        try
        {
            var access = await accessService.ResolveAsync(User);
            if (access.Operator == null)
            {
                return StatusCode(access.StatusCode, new { success = false, errorCode = access.ErrorCode, message = access.Message });
            }

            var result = await action(access.Operator);
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
            logger.LogError(exception, "扫码打包接口失败: {Operation}", operation);
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }
}
