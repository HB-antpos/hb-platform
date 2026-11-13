using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.PosSettings;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 收银端设置下发（后台 / 员工 App）：全局 → 门店 → 设备三层已设置值的读写、修改记录、设备应用情况。
/// 权限码只做门禁（PosSettings.View 读、PosSettings.Manage 写）；门店范围与「全局只有管理员能改」在服务里统一处理。
/// 新权限码不写入角色模板，上线后由管理员在角色管理里显式授予。
/// </summary>
[ApiController]
[Route("api/react/v1/pos-settings")]
[Authorize]
public sealed class PosSettingsController : ControllerBase
{
    private readonly IPosSettingsService _service;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentUserManageableStoreScopeService _scopeService;

    public PosSettingsController(
        IPosSettingsService service,
        ICurrentUserService currentUserService,
        ICurrentUserManageableStoreScopeService scopeService)
    {
        _service = service;
        _currentUserService = currentUserService;
        _scopeService = scopeService;
    }

    [HttpGet("catalog")]
    [Authorize(Policy = Permissions.PosSettings.View)]
    public IActionResult GetCatalog() => Ok(ApiResponse<PosSettingsCatalogDto>.OK(_service.GetCatalog()));

    [HttpGet("global")]
    [Authorize(Policy = Permissions.PosSettings.View)]
    public async Task<IActionResult> GetGlobal(CancellationToken cancellationToken) =>
        ToActionResult(await _service.GetGlobalAsync(cancellationToken));

    [HttpPut("global")]
    [Authorize(Policy = Permissions.PosSettings.Manage)]
    public async Task<IActionResult> SaveGlobal(
        [FromBody] SavePosSettingsRequest request,
        CancellationToken cancellationToken) =>
        ToActionResult(await _service.SaveGlobalAsync(await ResolveActorAsync(), request, cancellationToken));

    [HttpGet("stores")]
    [Authorize(Policy = Permissions.PosSettings.View)]
    public async Task<IActionResult> ListStores(CancellationToken cancellationToken) =>
        ToActionResult(await _service.ListStoresAsync(await ResolveActorAsync(), cancellationToken));

    [HttpGet("stores/{storeCode}")]
    [Authorize(Policy = Permissions.PosSettings.View)]
    public async Task<IActionResult> GetStore(string storeCode, CancellationToken cancellationToken) =>
        ToActionResult(await _service.GetStoreAsync(await ResolveActorAsync(), storeCode, cancellationToken));

    [HttpPut("stores/{storeCode}")]
    [Authorize(Policy = Permissions.PosSettings.Manage)]
    public async Task<IActionResult> SaveStore(
        string storeCode,
        [FromBody] SavePosSettingsRequest request,
        CancellationToken cancellationToken) =>
        ToActionResult(await _service.SaveStoreAsync(await ResolveActorAsync(), storeCode, request, cancellationToken));

    [HttpGet("devices")]
    [Authorize(Policy = Permissions.PosSettings.View)]
    public async Task<IActionResult> ListDevices([FromQuery] string? storeCode, CancellationToken cancellationToken) =>
        ToActionResult(await _service.ListDevicesAsync(await ResolveActorAsync(), storeCode ?? string.Empty, cancellationToken));

    [HttpGet("devices/{deviceCode}")]
    [Authorize(Policy = Permissions.PosSettings.View)]
    public async Task<IActionResult> GetDevice(string deviceCode, CancellationToken cancellationToken) =>
        ToActionResult(await _service.GetDeviceAsync(await ResolveActorAsync(), deviceCode, cancellationToken));

    [HttpPut("devices/{deviceCode}")]
    [Authorize(Policy = Permissions.PosSettings.Manage)]
    public async Task<IActionResult> SaveDevice(
        string deviceCode,
        [FromBody] SavePosSettingsRequest request,
        CancellationToken cancellationToken) =>
        ToActionResult(await _service.SaveDeviceAsync(await ResolveActorAsync(), deviceCode, request, cancellationToken));

    [HttpGet("changes")]
    [Authorize(Policy = Permissions.PosSettings.View)]
    public async Task<IActionResult> ListChanges(
        [FromQuery] string? scope,
        [FromQuery] string? scopeKey,
        [FromQuery] int limit,
        CancellationToken cancellationToken) =>
        ToActionResult(await _service.ListChangesAsync(await ResolveActorAsync(), scope, scopeKey, limit, cancellationToken));

    private async Task<PosSettingsActor> ResolveActorAsync()
    {
        // 与店员 / 现金页同一口径：管理员不限门店，店长取全部关联分店（含非主分店）。
        var scope = await _scopeService.GetAssignedStoreScopeAsync();
        return new PosSettingsActor(
            _currentUserService.GetCurrentUsername(),
            scope.IsAllowed && scope.IsAdmin,
            scope.IsAllowed ? scope.StoreCodes : Array.Empty<string>());
    }

    private IActionResult ToActionResult<T>(ApiResponse<T> result) =>
        result.Success ? Ok(result) : StatusCode(MapStatusCode(result.ErrorCode), result);

    internal static int MapStatusCode(string? errorCode) =>
        errorCode switch
        {
            PosSettingsErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            PosSettingsErrorCodes.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
}
