using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Route("api/react/v1/container-new-products")]
[Authorize(Policy = Permissions.Container.MobileNewProductsView)]
[MaskContainerNumbers]
public sealed class ContainerNewProductsController(IContainerNewProductsReactService service) : ControllerBase
{
    /// <param name="windowBasis">
    /// 可选，时间窗口按哪个日期算：<c>store</c>（默认，预计到店区间，旧版 App 口径）或 <c>warehouse</c>（货柜到仓库日期），大小写不敏感；
    /// 不认识的值返回 400（errorCode = WINDOW_BASIS_INVALID）。
    /// </param>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? storeCode, CancellationToken cancellationToken, [FromQuery] bool includeExisting = false, [FromQuery] string? windowBasis = null)
    {
        if (string.IsNullOrWhiteSpace(storeCode)) return BadRequest(new { errorCode = "STORE_CODE_REQUIRED" });
        if (!TryParseWindowBasis(windowBasis, out var basis)) return BadRequest(new { errorCode = "WINDOW_BASIS_INVALID" });
        try { return Ok(await service.GetAsync(storeCode, includeExisting, cancellationToken, basis)); }
        catch (ContainerNewProductsForbiddenException) { return Forbid(); }
        catch (ContainerNewProductsStoreNotFoundException) { return NotFound(new { errorCode = "STORE_NOT_FOUND" }); }
        catch (ContainerNewProductsStateUnknownException) { return BadRequest(new { errorCode = "STORE_STATE_UNKNOWN" }); }
    }

    // 缺省或空白 = store（改动前的行为，不带参数的旧版 App 不受影响）；store / warehouse 大小写不敏感，前后空白忽略；其余值一律非法
    internal static bool TryParseWindowBasis(string? value, out ContainerNewProductsWindowBasis basis)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || string.Equals(text, "store", StringComparison.OrdinalIgnoreCase))
        {
            basis = ContainerNewProductsWindowBasis.StoreArrival;
            return true;
        }
        if (string.Equals(text, "warehouse", StringComparison.OrdinalIgnoreCase))
        {
            basis = ContainerNewProductsWindowBasis.WarehouseArrival;
            return true;
        }
        basis = ContainerNewProductsWindowBasis.StoreArrival;
        return false;
    }
}
