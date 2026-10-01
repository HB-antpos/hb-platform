using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Route("api/react/v1/container-new-products")]
[Authorize(Policy = Permissions.Container.MobileNewProductsView)]
public sealed class ContainerNewProductsController(IContainerNewProductsReactService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? storeCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode)) return BadRequest(new { errorCode = "STORE_CODE_REQUIRED" });
        try { return Ok(await service.GetAsync(storeCode, cancellationToken)); }
        catch (ContainerNewProductsForbiddenException) { return Forbid(); }
        catch (ContainerNewProductsStoreNotFoundException) { return NotFound(new { errorCode = "STORE_NOT_FOUND" }); }
        catch (ContainerNewProductsStateUnknownException) { return BadRequest(new { errorCode = "STORE_STATE_UNKNOWN" }); }
    }
}
