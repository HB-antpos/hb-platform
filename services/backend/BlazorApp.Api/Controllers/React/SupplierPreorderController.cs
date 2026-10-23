using BlazorApp.Api.Services.React.SupplierPreorders;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>供应商节日预订单 - 管理端（导入、订单表、分店开启、矩阵、导出、下单人员授权）。只认 SupplierPreorder.Manage。</summary>
[ApiController]
[Route("api/react/v1/supplier-preorders/admin")]
[Authorize(Policy = Permissions.SupplierPreorder.Manage)]
public sealed class SupplierPreorderAdminController : ControllerBase
{
    private readonly ISupplierPreorderService _service;

    public SupplierPreorderAdminController(ISupplierPreorderService service)
    {
        _service = service;
    }

    [HttpPost("import/parse-file")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public Task<IActionResult> ParseFile(IFormFile? file, [FromForm] string? localSupplierCode) =>
        SupplierPreorderResponses.ExecuteAsync(this, async () =>
        {
            if (file is null)
            {
                throw new SupplierPreorderException("请选择文件", "FILE_REQUIRED");
            }

            await using var stream = file.OpenReadStream();
            return await _service.ParseFileAsync(stream, file.FileName, file.ContentType, file.Length, localSupplierCode);
        });

    [HttpPost("import/parse-pasted")]
    public Task<IActionResult> ParsePasted([FromBody] SupplierPreorderParsePastedRequestDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.ParsePastedAsync(request));

    [HttpPost("import/preview")]
    public Task<IActionResult> Preview([FromBody] SupplierPreorderPreviewRequestDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.PreviewAsync(request));

    [HttpPost("import/ref-candidates")]
    public Task<IActionResult> ImportRefCandidates([FromBody] SupplierPreorderImportRefCandidatesRequestDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetImportRefCandidatesAsync(request));

    [HttpGet("suppliers")]
    public Task<IActionResult> Suppliers() => SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetSuppliersAsync());

    [HttpGet("stores")]
    public Task<IActionResult> Stores() => SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetActiveStoresAsync());

    [HttpPost("forms")]
    public Task<IActionResult> CreateForm([FromBody] SupplierPreorderCreateFormDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.CreateFormAsync(request), "订单表已导入");

    [HttpGet("forms")]
    public Task<IActionResult> GetForms([FromQuery] SupplierPreorderFormQueryDto query) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetFormsAsync(query));

    [HttpGet("forms/{formGuid}")]
    public Task<IActionResult> GetForm(string formGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetFormAsync(formGuid));

    [HttpPut("forms/{formGuid}")]
    public Task<IActionResult> UpdateForm(string formGuid, [FromBody] SupplierPreorderUpdateFormDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.UpdateFormAsync(formGuid, request), "订单表信息已保存");

    [HttpPost("forms/{formGuid}/status")]
    public Task<IActionResult> SetStatus(string formGuid, [FromBody] SupplierPreorderSetStatusDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.SetStatusAsync(formGuid, request), "状态已更新");

    [HttpDelete("forms/{formGuid}")]
    public Task<IActionResult> DeleteForm(string formGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.DeleteFormAsync(formGuid), "订单表已删除");

    [HttpGet("forms/{formGuid}/items")]
    public Task<IActionResult> GetItems(string formGuid, [FromQuery] SupplierPreorderFormItemQueryDto query) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetFormItemsAsync(formGuid, query));

    [HttpPost("forms/{formGuid}/history/recompute")]
    public Task<IActionResult> RecomputeHistory(string formGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.RecomputeHistoryAsync(formGuid), "已排队重新计算去年历史");

    [HttpPost("forms/{formGuid}/images/retry-failed")]
    public Task<IActionResult> RetryFailedImages(string formGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.RetryFailedImagesAsync(formGuid), "已重新排队转存失败的图片");

    [HttpGet("forms/{formGuid}/items/{itemGuid}/ref-candidates")]
    public Task<IActionResult> GetRefCandidates(string formGuid, string itemGuid, [FromQuery] string? keyword) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetRefCandidatesAsync(formGuid, itemGuid, keyword));

    [HttpPut("forms/{formGuid}/items/{itemGuid}/ref")]
    public Task<IActionResult> SetItemRef(string formGuid, string itemGuid, [FromBody] SupplierPreorderSetRefDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.SetItemRefAsync(formGuid, itemGuid, request), "参考商品已保存");

    [HttpGet("forms/{formGuid}/stores")]
    public Task<IActionResult> GetFormStores(string formGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetFormStoresAsync(formGuid));

    [HttpPut("forms/{formGuid}/stores")]
    public Task<IActionResult> UpdateFormStores(string formGuid, [FromBody] SupplierPreorderUpdateFormStoresDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.UpdateFormStoresAsync(formGuid, request), "开启分店已保存");

    [HttpPost("forms/{formGuid}/stores/copy-from-previous")]
    public Task<IActionResult> CopyStores(string formGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.CopyStoresFromPreviousAsync(formGuid));

    [HttpGet("forms/{formGuid}/matrix")]
    public Task<IActionResult> GetMatrix(string formGuid, [FromQuery] SupplierPreorderMatrixQueryDto query) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetMatrixAsync(formGuid, query));

    [HttpGet("forms/{formGuid}/export")]
    public Task<IActionResult> Export(string formGuid, [FromQuery] string format = "generic") =>
        SupplierPreorderResponses.FileAsync(this, () => _service.ExportAsync(formGuid, format));

    [HttpGet("orderers")]
    public Task<IActionResult> GetOrderers() => SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetOrderersAsync());

    [HttpGet("orderers/candidates")]
    public Task<IActionResult> SearchCandidates([FromQuery] string? keyword) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.SearchUserCandidatesAsync(keyword));

    [HttpPut("orderers/{userGuid}")]
    public Task<IActionResult> SaveOrderer(string userGuid, [FromBody] SupplierPreorderSaveOrdererDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.SaveOrdererAsync(userGuid, request), "下单人员已保存");

    [HttpDelete("orderers/{userGuid}")]
    public Task<IActionResult> RemoveOrderer(string userGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.RemoveOrdererAsync(userGuid), "已移除");

    [HttpGet("auth-logs")]
    public Task<IActionResult> GetAuthLogs([FromQuery] SupplierPreorderAuthLogQueryDto query) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetAuthLogsAsync(query));
}

/// <summary>
/// 供应商节日预订单 - 下单端。入口要求 SupplierPreorder.Order 或 Manage（服务内判定）；
/// 能改哪家店只认「下单人员授权」与订单表开启的分店。
/// </summary>
[ApiController]
[Route("api/react/v1/supplier-preorders/ordering")]
[Authorize]
public sealed class SupplierPreorderOrderingController : ControllerBase
{
    private readonly ISupplierPreorderService _service;

    public SupplierPreorderOrderingController(ISupplierPreorderService service)
    {
        _service = service;
    }

    [HttpGet("forms")]
    public Task<IActionResult> GetForms() => SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetOrderingFormsAsync());

    [HttpGet("forms/{formGuid}/context")]
    public Task<IActionResult> GetContext(string formGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetOrderingContextAsync(formGuid));

    [HttpGet("forms/{formGuid}/summary")]
    public Task<IActionResult> GetSummary(string formGuid, [FromQuery] string storeGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetOrderingSummaryAsync(formGuid, storeGuid));

    [HttpGet("forms/{formGuid}/items")]
    public Task<IActionResult> GetItems(string formGuid, [FromQuery] SupplierPreorderOrderingItemQueryDto query) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetOrderingItemsAsync(formGuid, query));

    [HttpGet("forms/{formGuid}/items/{itemGuid}/history")]
    public Task<IActionResult> GetItemHistory(string formGuid, string itemGuid, [FromQuery] string storeGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.GetOrderingItemHistoryAsync(formGuid, itemGuid, storeGuid));

    [HttpPut("forms/{formGuid}/stores/{storeGuid}/lines/{itemGuid}")]
    public Task<IActionResult> SaveLine(string formGuid, string storeGuid, string itemGuid, [FromBody] SupplierPreorderSaveLineDto request) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.SaveLineAsync(formGuid, storeGuid, itemGuid, request), "已保存");

    [HttpPost("forms/{formGuid}/stores/{storeGuid}/submit")]
    public Task<IActionResult> Submit(string formGuid, string storeGuid) =>
        SupplierPreorderResponses.ExecuteAsync(this, () => _service.SubmitStoreOrderAsync(formGuid, storeGuid), "本店订单已提交");

    [HttpGet("forms/{formGuid}/stores/{storeGuid}/export")]
    public Task<IActionResult> Export(string formGuid, string storeGuid) =>
        SupplierPreorderResponses.FileAsync(this, () => _service.ExportStoreOrderAsync(formGuid, storeGuid));
}

/// <summary>统一把 SupplierPreorderException 转成带错误码的 ApiResponse。</summary>
internal static class SupplierPreorderResponses
{
    public static async Task<IActionResult> ExecuteAsync<T>(ControllerBase controller, Func<Task<T>> action, string message = "操作成功")
    {
        try
        {
            return controller.Ok(ApiResponse<T>.OK(await action(), message));
        }
        catch (SupplierPreorderException ex)
        {
            return controller.StatusCode(ex.StatusCode, ApiResponse<T>.Error(ex.Message, ex.ErrorCode));
        }
    }

    public static async Task<IActionResult> FileAsync(ControllerBase controller, Func<Task<SupplierPreorderExportFile>> action)
    {
        try
        {
            var file = await action();
            return controller.File(file.Content, file.ContentType, file.FileName);
        }
        catch (SupplierPreorderException ex)
        {
            return controller.StatusCode(ex.StatusCode, ApiResponse<object>.Error(ex.Message, ex.ErrorCode));
        }
    }
}
