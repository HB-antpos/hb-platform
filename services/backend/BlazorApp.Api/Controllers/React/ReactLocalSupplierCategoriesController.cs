using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// POS 商品管理页的供应商分类管理。查看复用商品查看权限，写操作复用商品管理权限，不新增权限码。
/// </summary>
[ApiController]
[Authorize]
[Route("api/react/v1/local-supplier-categories")]
public sealed class ReactLocalSupplierCategoriesController : ControllerBase
{
    private readonly ILocalSupplierCategoryReactService _service;
    private readonly ILogger<ReactLocalSupplierCategoriesController> _logger;

    public ReactLocalSupplierCategoriesController(
        ILocalSupplierCategoryReactService service,
        ILogger<ReactLocalSupplierCategoriesController> logger
    )
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("summary")]
    [Authorize(Policy = Permissions.PosProducts.View)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        try
        {
            var data = await _service.GetSummaryAsync(cancellationToken);
            return Ok(ApiResponse<List<LocalSupplierCategorySupplierSummaryDto>>.OK(data, "查询成功"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询供应商分类概览失败");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<List<LocalSupplierCategorySupplierSummaryDto>>.Error("查询供应商分类概览失败。", "QUERY_ERROR")
            );
        }
    }

    [HttpGet("tree")]
    [Authorize(Policy = Permissions.PosProducts.View)]
    public async Task<IActionResult> GetTree([FromQuery] string? supplierCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplierCode) || supplierCode.Trim().Length > 64)
        {
            return BadRequest(
                ApiResponse<List<LocalSupplierCategoryNodeDto>>.Error("供应商代码无效。", LocalSupplierCategoryErrorCodes.InvalidRequest)
            );
        }

        try
        {
            var data = await _service.GetTreeAsync(supplierCode, cancellationToken);
            return Ok(ApiResponse<List<LocalSupplierCategoryNodeDto>>.OK(data, "查询成功"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询供应商分类树失败 SupplierCode={SupplierCode}", supplierCode);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<List<LocalSupplierCategoryNodeDto>>.Error("查询供应商分类树失败。", "QUERY_ERROR")
            );
        }
    }

    [HttpPatch("{categoryGuid}/promotional")]
    [Authorize(Policy = Permissions.PosProducts.Manage)]
    public async Task<IActionResult> SetPromotional(
        string categoryGuid,
        [FromBody] LocalSupplierCategoryPromotionalUpdateDto request,
        CancellationToken cancellationToken
    )
    {
        if (request?.IsPromotional == null || string.IsNullOrWhiteSpace(categoryGuid) || categoryGuid.Length > 50)
        {
            return BadRequest(
                ApiResponse<LocalSupplierCategoryPromotionalResultDto>.Error("请求参数无效。", LocalSupplierCategoryErrorCodes.InvalidRequest)
            );
        }

        try
        {
            var data = await _service.SetPromotionalAsync(
                categoryGuid,
                request.IsPromotional.Value,
                User.Identity?.Name,
                cancellationToken
            );
            return Ok(ApiResponse<LocalSupplierCategoryPromotionalResultDto>.OK(data, "已更新促销标记"));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<LocalSupplierCategoryPromotionalResultDto>.Error(ex.Message, LocalSupplierCategoryErrorCodes.CategoryNotFound));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新供应商分类促销标记失败 CategoryGuid={CategoryGuid}", categoryGuid);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<LocalSupplierCategoryPromotionalResultDto>.Error("更新促销标记失败。", "QUERY_ERROR")
            );
        }
    }

    [HttpPost("{supplierCode}/resolve")]
    [Authorize(Policy = Permissions.PosProducts.Manage)]
    public async Task<IActionResult> Resolve(string supplierCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplierCode) || supplierCode.Trim().Length > 64)
        {
            return BadRequest(
                ApiResponse<LocalSupplierCategoryResolveResultDto>.Error("供应商代码无效。", LocalSupplierCategoryErrorCodes.InvalidRequest)
            );
        }

        try
        {
            var data = await _service.ResolveSupplierAsync(supplierCode, User.Identity?.Name, cancellationToken);
            return Ok(ApiResponse<LocalSupplierCategoryResolveResultDto>.OK(data, "重新解析完成"));
        }
        catch (LocalSupplierCategoryValidationException ex)
        {
            return BadRequest(ApiResponse<LocalSupplierCategoryResolveResultDto>.Error(ex.Message, ex.ErrorCode));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新解析供应商分类失败 SupplierCode={SupplierCode}", supplierCode);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<LocalSupplierCategoryResolveResultDto>.Error("重新解析失败。", "QUERY_ERROR")
            );
        }
    }
}
