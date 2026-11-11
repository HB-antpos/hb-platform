using BlazorApp.Api.Services.StoreSpecialProductCategories;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 手机 App 的分店级「特殊商品分类」维护接口（分类增删改排序、商品归类、取消标记、分类内排序、同步到其他分店）。
    /// 仅登录账号可用：设备会话（匿名绑定设备）一律拒绝，同 store-product-maintenance/products/sync-to-other-stores 先例；
    /// 读接口（GET）需 PosTerminal.SpecialProducts.View 或 Manage，写接口与同步（含 sync/targets）需 PosTerminal.SpecialProducts.Manage；
    /// 所有接口都带分店代码并在服务层校验「管理的分店」范围（主分店口径，同步接口来源与全部目标都校验）。
    /// 响应统一 ApiResponse 包装；业务错误带稳定 errorCode：校验 / 确认类 400，越权 403，不存在 404，重名 / 上限 / 排序冲突 / 并发归类冲突 409。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/store-special-product-categories")]
    [Authorize]
    public class StoreSpecialProductCategoriesController : ControllerBase
    {
        private readonly IStoreSpecialProductCategoryService _service;
        private readonly IAuthorizationService _authorizationService;

        public StoreSpecialProductCategoriesController(
            IStoreSpecialProductCategoryService service,
            IAuthorizationService authorizationService)
        {
            _service = service;
            _authorizationService = authorizationService;
        }

        // ---------------------------------------------------------------- 分类

        [HttpGet("")]
        public async Task<IActionResult> GetList([FromQuery] string? storeCode) =>
            await RequireReadAsync() ?? ToResult(await _service.GetListAsync(storeCode));

        [HttpPost("")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> Create([FromBody] StoreSpecialCategorySaveRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.CreateAsync(request));

        // 字面量路径 order 优先于 {id} 参数路径，PUT order 不会被当成分类 id。
        [HttpPut("order")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> ReorderCategories([FromBody] StoreSpecialCategoryOrderRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.ReorderCategoriesAsync(request));

        [HttpPut("{id}")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> Update(string id, [FromBody] StoreSpecialCategorySaveRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.UpdateAsync(id, request));

        [HttpDelete("{id}")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> Delete(string id, [FromQuery] string? storeCode) =>
            RejectDeviceSession() ?? ToResult(await _service.DeleteAsync(id, storeCode));

        // ---------------------------------------------------------------- 商品

        [HttpGet("products")]
        public async Task<IActionResult> GetProducts(
            [FromQuery] string? storeCode,
            [FromQuery] string? categoryId,
            [FromQuery] string? keyword,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
            await RequireReadAsync() ?? ToResult(await _service.GetProductsAsync(storeCode, categoryId, keyword, page, pageSize));

        [HttpGet("candidates")]
        public async Task<IActionResult> SearchCandidates(
            [FromQuery] string? storeCode,
            [FromQuery] string? keyword,
            [FromQuery] int? limit) =>
            await RequireReadAsync() ?? ToResult(await _service.SearchCandidatesAsync(storeCode, keyword, limit));

        [HttpPost("assign")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> Assign([FromBody] StoreSpecialCategoryAssignRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.AssignAsync(request));

        [HttpPost("unmark")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> Unmark([FromBody] StoreSpecialCategoryUnmarkRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.UnmarkAsync(request));

        [HttpPut("{id}/products/order")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> ReorderProducts(
            string id, [FromBody] StoreSpecialCategoryProductOrderRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.ReorderProductsAsync(id, request));

        // ---------------------------------------------------------------- 同步到其他分店

        [HttpGet("sync/targets")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> GetSyncTargets([FromQuery] string? sourceStoreCode) =>
            RejectDeviceSession() ?? ToResult(await _service.GetSyncTargetsAsync(sourceStoreCode));

        [HttpPost("sync/preview")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> PreviewSync([FromBody] StoreSpecialCategorySyncRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.PreviewSyncAsync(request));

        [HttpPost("sync/execute")]
        [Authorize(Policy = Permissions.PosTerminal.SpecialProducts.Manage)]
        public async Task<IActionResult> ExecuteSync([FromBody] StoreSpecialCategorySyncRequestDto? request) =>
            RejectDeviceSession() ?? ToResult(await _service.ExecuteSyncAsync(request));

        // ---------------------------------------------------------------- 辅助

        /// <summary>
        /// 设备会话绑定单一分店、没有用户账号：整组接口只对登录账号开放。
        /// 类上已有 [Authorize]（匿名请求在到达这里之前就是 401），这里再显式兜底，不依赖过滤器也不会放行匿名调用。
        /// </summary>
        private IActionResult? RejectDeviceSession() =>
            User?.Identity?.IsAuthenticated == true ? null : Forbid();

        /// <summary>
        /// 读接口权限：SpecialProducts.View，或持有 Manage 也放行（SpecialProducts 没有 Manage ⊇ View 的别名，
        /// 而移动端入口只认 Manage，只持 Manage 的账号进得了页面就必须读得了列表）。设备会话先被拒绝。
        /// </summary>
        private async Task<IActionResult?> RequireReadAsync()
        {
            var rejected = RejectDeviceSession();
            if (rejected is not null)
            {
                return rejected;
            }

            foreach (var policy in new[]
                     {
                         Permissions.PosTerminal.SpecialProducts.View,
                         Permissions.PosTerminal.SpecialProducts.Manage,
                     })
            {
                var authorized = await _authorizationService.AuthorizeAsync(User, resource: null, policy);
                if (authorized.Succeeded)
                {
                    return null;
                }
            }

            return Forbid();
        }

        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode switch
            {
                StoreSpecialCategoryErrorCodes.StoreOutOfScope => StatusCode(StatusCodes.Status403Forbidden, result),
                StoreSpecialCategoryErrorCodes.StoreNotFound
                    or StoreSpecialCategoryErrorCodes.CategoryNotFound => NotFound(result),
                StoreSpecialCategoryErrorCodes.NameDuplicate
                    or StoreSpecialCategoryErrorCodes.LimitReached
                    or StoreSpecialCategoryErrorCodes.AssignConflict
                    or StoreSpecialCategoryErrorCodes.OrderConflict => Conflict(result),
                _ => BadRequest(result),
            };
        }
    }
}
