using BlazorApp.Api.Services.Planogram;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 商品陈列图（接口契约见 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 A / B / C）：
    /// 批 1 的货架型号、挂钩型号、商品陈列参数，批 2 的陈列方案（planograms/…），批 3 的发布、版本快照、JSON 导入导出。
    /// 权限按方法区分：GET 一律 Planogram.View（含版本列表与历史版本）；商品参数保存、方案新建 / 保存 / 校验 / 复制、
    /// 方案 JSON 导出 / 导入 Planogram.Edit；型号增改删、商品参数清除、方案发布 / 归档 / 恢复 / 删除 Planogram.Manage。
    /// Manage 隐含 Edit、Edit 隐含 View（Permissions.PermissionAliases，登录时展开进权限声明）。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/planogram")]
    public class PlanogramController : ControllerBase
    {
        private readonly IPlanogramModelService _models;
        private readonly IProductDisplaySpecService _specs;
        private readonly IPlanogramPlanService _plans;

        public PlanogramController(
            IPlanogramModelService models,
            IProductDisplaySpecService specs,
            IPlanogramPlanService plans)
        {
            _models = models;
            _specs = specs;
            _plans = plans;
        }

        // ---------------------------------------------------------------- 货架型号

        [HttpGet("shelf-models")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> ListShelfModels(
            [FromQuery] string? keyword,
            [FromQuery] bool? isActive,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default) =>
            ToResult(await _models.ListShelfModelsAsync(keyword, isActive, page, pageSize, cancellationToken));

        [HttpPost("shelf-models")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> CreateShelfModel(
            [FromBody] ShelfModelDto? input,
            CancellationToken cancellationToken) =>
            ToResult(await _models.CreateShelfModelAsync(
                input ?? new ShelfModelDto(), CurrentUserName(), cancellationToken));

        [HttpPut("shelf-models/{guid}")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> UpdateShelfModel(
            string guid,
            [FromBody] ShelfModelDto? input,
            CancellationToken cancellationToken) =>
            ToResult(await _models.UpdateShelfModelAsync(
                guid, input ?? new ShelfModelDto(), CurrentUserName(), cancellationToken));

        [HttpDelete("shelf-models/{guid}")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> DeleteShelfModel(string guid, CancellationToken cancellationToken) =>
            ToResult(await _models.DeleteShelfModelAsync(guid, cancellationToken));

        // ---------------------------------------------------------------- 挂钩型号

        [HttpGet("hook-models")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> ListHookModels(
            [FromQuery] string? keyword,
            [FromQuery] bool? isActive,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default) =>
            ToResult(await _models.ListHookModelsAsync(keyword, isActive, page, pageSize, cancellationToken));

        [HttpPost("hook-models")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> CreateHookModel(
            [FromBody] HookModelDto? input,
            CancellationToken cancellationToken) =>
            ToResult(await _models.CreateHookModelAsync(
                input ?? new HookModelDto(), CurrentUserName(), cancellationToken));

        [HttpPut("hook-models/{guid}")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> UpdateHookModel(
            string guid,
            [FromBody] HookModelDto? input,
            CancellationToken cancellationToken) =>
            ToResult(await _models.UpdateHookModelAsync(
                guid, input ?? new HookModelDto(), CurrentUserName(), cancellationToken));

        [HttpDelete("hook-models/{guid}")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> DeleteHookModel(string guid, CancellationToken cancellationToken) =>
            ToResult(await _models.DeleteHookModelAsync(guid, cancellationToken));

        // ---------------------------------------------------------------- 商品陈列参数

        /// <summary>商品参数列表（联 Product）；status：all / set / unset。</summary>
        [HttpGet("product-specs")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> ListProductSpecs(
            [FromQuery] string? keyword,
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default) =>
            ToResult(await _specs.ListAsync(keyword, status, page, pageSize, cancellationToken));

        [HttpGet("product-specs/{productCode}")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> GetProductSpec(string productCode, CancellationToken cancellationToken) =>
            ToResult(await _specs.GetAsync(productCode, cancellationToken));

        [HttpPut("product-specs/{productCode}")]
        [Authorize(Policy = Permissions.Planogram.Edit)]
        public async Task<IActionResult> SaveProductSpec(
            string productCode,
            [FromBody] ProductDisplaySpecDto? input,
            CancellationToken cancellationToken) =>
            ToResult(await _specs.SaveAsync(
                productCode, input ?? new ProductDisplaySpecDto(), CurrentUserName(), cancellationToken));

        [HttpDelete("product-specs/{productCode}")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> DeleteProductSpec(string productCode, CancellationToken cancellationToken) =>
            ToResult(await _specs.DeleteAsync(productCode, cancellationToken));

        // ---------------------------------------------------------------- 陈列方案（批 2）

        /// <summary>方案列表；status：0 草稿 / 1 已发布 / 2 已归档；keyword 匹配名称与明细商品货号 / 名称。</summary>
        [HttpGet("planograms")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> ListPlans(
            [FromQuery] string? shelfModelGuid,
            [FromQuery] int? status,
            [FromQuery] string? keyword,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default) =>
            ToResult(await _plans.ListAsync(shelfModelGuid, status, keyword, page, pageSize, cancellationToken));

        [HttpPost("planograms")]
        [Authorize(Policy = Permissions.Planogram.Edit)]
        public async Task<IActionResult> CreatePlan(
            [FromBody] PlanogramCreateRequest? input,
            CancellationToken cancellationToken) =>
            ToResult(await _plans.CreateAsync(input ?? new PlanogramCreateRequest(), CurrentUserName(), cancellationToken));

        /// <summary>设计器一次拿齐：头 + 明细 + 货架 / 挂钩型号 + 商品参数 + 冲突 + 统计。</summary>
        [HttpGet("planograms/{guid}")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> GetPlan(string guid, CancellationToken cancellationToken) =>
            ToResult(await _plans.GetAsync(guid, cancellationToken));

        /// <summary>整体保存头 + 明细（rowVersion 乐观并发，过期返回 PLANOGRAM_STALE）。</summary>
        [HttpPut("planograms/{guid}")]
        [Authorize(Policy = Permissions.Planogram.Edit)]
        public async Task<IActionResult> SavePlan(
            string guid,
            [FromBody] PlanogramSaveRequest? input,
            CancellationToken cancellationToken) =>
            ToResult(await _plans.SaveAsync(guid, input ?? new PlanogramSaveRequest(), CurrentUserName(), cancellationToken));

        /// <summary>只校验不保存（请求体同 PUT，不含 rowVersion）。</summary>
        [HttpPost("planograms/{guid}/validate")]
        [Authorize(Policy = Permissions.Planogram.Edit)]
        public async Task<IActionResult> ValidatePlan(
            string guid,
            [FromBody] PlanogramSaveRequest? input,
            CancellationToken cancellationToken) =>
            ToResult(await _plans.ValidateAsync(guid, input ?? new PlanogramSaveRequest(), cancellationToken));

        [HttpPost("planograms/{guid}/duplicate")]
        [Authorize(Policy = Permissions.Planogram.Edit)]
        public async Task<IActionResult> DuplicatePlan(
            string guid,
            [FromBody] PlanogramDuplicateRequest? input,
            CancellationToken cancellationToken) =>
            ToResult(await _plans.DuplicateAsync(
                guid, input ?? new PlanogramDuplicateRequest(), CurrentUserName(), cancellationToken));

        [HttpPost("planograms/{guid}/archive")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> ArchivePlan(string guid, CancellationToken cancellationToken) =>
            ToResult(await _plans.ArchiveAsync(guid, CurrentUserName(), cancellationToken));

        [HttpPost("planograms/{guid}/restore")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> RestorePlan(string guid, CancellationToken cancellationToken) =>
            ToResult(await _plans.RestoreAsync(guid, CurrentUserName(), cancellationToken));

        /// <summary>仅草稿且从未发布的方案可删，否则返回 PLANOGRAM_NOT_DELETABLE。</summary>
        [HttpDelete("planograms/{guid}")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> DeletePlan(string guid, CancellationToken cancellationToken) =>
            ToResult(await _plans.DeleteAsync(guid, cancellationToken));

        // ---------------------------------------------------------------- 发布 / 版本 / 导入导出（批 3）

        /// <summary>
        /// 发布：请求体 <c>{rowVersion}</c>；有 error 级冲突 PLANOGRAM_HAS_CONFLICTS、空方案 PLANOGRAM_EMPTY、已归档 PLANOGRAM_ARCHIVED、
        /// 当前版本已有快照 PLANOGRAM_ALREADY_PUBLISHED、rowVersion 过期 PLANOGRAM_STALE。
        /// </summary>
        [HttpPost("planograms/{guid}/publish")]
        [Authorize(Policy = Permissions.Planogram.Manage)]
        public async Task<IActionResult> PublishPlan(
            string guid,
            [FromBody] PlanogramPublishRequest? input,
            CancellationToken cancellationToken) =>
            ToResult(await _plans.PublishAsync(
                guid, input ?? new PlanogramPublishRequest(), CurrentUserName(), cancellationToken));

        /// <summary>历史版本列表，按版本倒序。</summary>
        [HttpGet("planograms/{guid}/versions")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> ListPlanVersions(string guid, CancellationToken cancellationToken) =>
            ToResult(await _plans.ListVersionsAsync(guid, cancellationToken));

        /// <summary>某个历史版本的快照（发布时固化，供按当时数据重出 PDF）。版本号必须是整数，否则路由不匹配（404）。</summary>
        [HttpGet("planograms/{guid}/versions/{version:int}")]
        [Authorize(Policy = Permissions.Planogram.View)]
        public async Task<IActionResult> GetPlanVersion(string guid, int version, CancellationToken cancellationToken) =>
            ToResult(await _plans.GetVersionAsync(guid, version, cancellationToken));

        /// <summary>导出方案 JSON（型号编码 + 商品货号，不带 guid）。</summary>
        [HttpGet("planograms/{guid}/export")]
        [Authorize(Policy = Permissions.Planogram.Edit)]
        public async Task<IActionResult> ExportPlan(string guid, CancellationToken cancellationToken) =>
            ToResult(await _plans.ExportAsync(guid, cancellationToken));

        /// <summary>
        /// 导入方案 JSON，创建新草稿方案。路由说明：<c>planograms/import</c> 是 POST 的字面量路径，
        /// 与 <c>POST planograms</c>（新建）不同路径；<c>planograms/{guid}</c> 只有 GET / PUT / DELETE，没有 POST，
        /// 且 ASP.NET Core 路由对字面量段的优先级高于参数段，所以不会被 <c>{guid}</c> 吞掉。
        /// </summary>
        [HttpPost("planograms/import")]
        [Authorize(Policy = Permissions.Planogram.Edit)]
        public async Task<IActionResult> ImportPlan(
            [FromBody] PlanogramImportRequest? input,
            CancellationToken cancellationToken) =>
            ToResult(await _plans.ImportAsync(
                input ?? new PlanogramImportRequest(), CurrentUserName(), cancellationToken));

        /// <summary>与 CurrentUserService.GetCurrentUsername 同口径：写入审计列（UpdatedBy）。</summary>
        private string CurrentUserName() =>
            User.Identity?.Name ?? User.FindFirst("fullName")?.Value ?? "System";

        private IActionResult ToResult<T>(ApiResponse<T> result) =>
            result.Success ? Ok(result) : BadRequest(result);
    }
}
