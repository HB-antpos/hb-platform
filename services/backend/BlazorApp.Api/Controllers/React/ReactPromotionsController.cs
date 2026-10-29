using System;
using System.Threading.Tasks;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/promotions")]
    [Authorize]
    public class ReactPromotionsController : ControllerBase
    {
        private readonly IPromotionReactService _service;

        public ReactPromotionsController(IPromotionReactService service)
        {
            _service = service;
        }

        [HttpPost("grid")]
        [Authorize(Policy = Permissions.Promotions.View)]
        public async Task<ActionResult<GridResponseDto<PromotionListDto>>> Grid(
            [FromBody] GridRequestDto request
        )
        {
            var res = await _service.GetGridAsync(request);
            return Ok(res);
        }

        [HttpGet("{id}")]
        [Authorize(Policy = Permissions.Promotions.View)]
        public async Task<ActionResult<ApiResponse<PromotionDetailDto>>> Get(string id)
        {
            var res = await _service.GetByIdAsync(id);
            return Ok(res);
        }

        [HttpPost("store/grid")]
        [Authorize(Policy = Permissions.Promotions.View)]
        public async Task<ActionResult<GridResponseDto<PromotionListDto>>> StoreGrid(
            [FromBody] StorePromotionGridRequestDto request
        )
        {
            var res = await _service.GetStoreGridAsync(request);
            return Ok(res);
        }

        [HttpGet("store/status-counts")]
        [Authorize(Policy = Permissions.Promotions.View)]
        public async Task<ActionResult<ApiResponse<PromotionStoreStatusCountsDto>>> StoreStatusCounts(
            [FromQuery] string storeCode,
            [FromQuery] string? keyword
        )
        {
            var res = await _service.GetStoreStatusCountsAsync(storeCode, keyword);
            return Ok(res);
        }

        // 商品查询页「本商品促销」：当前分店相关、未结束（含未开始、已停用）的促销，带范围与可编辑标记。
        [HttpGet("store/by-product")]
        [Authorize(Policy = Permissions.Promotions.View)]
        public async Task<ActionResult<ApiResponse<System.Collections.Generic.List<PromotionListDto>>>> StoreProductPromotions(
            [FromQuery] string productCode,
            [FromQuery] string storeCode
        )
        {
            var res = await _service.GetStoreProductPromotionsAsync(productCode, storeCode);
            return Ok(res);
        }

        [HttpGet("store/{id}")]
        [Authorize(Policy = Permissions.Promotions.View)]
        public async Task<ActionResult<ApiResponse<PromotionDetailDto>>> GetStorePromotion(
            string id,
            [FromQuery] string storeCode
        )
        {
            var res = await _service.GetStoreByIdAsync(id, storeCode);
            return Ok(res);
        }

        [HttpPost]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        [Authorize(Roles = "Admin,管理员")]
        public async Task<ActionResult<ApiResponse<PromotionDetailDto>>> Create(
            [FromBody] CreatePromotionDto dto
        )
        {
            var res = await _service.CreateAsync(dto);
            return Ok(res);
        }

        [HttpPost("store")]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        public async Task<ActionResult<ApiResponse<PromotionDetailDto>>> CreateStorePromotion(
            [FromQuery] string storeCode,
            [FromBody] CreatePromotionDto dto
        )
        {
            var res = await _service.CreateStorePromotionAsync(storeCode, dto);
            return Ok(res);
        }

        [HttpPut("{id}")]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        [Authorize(Roles = "Admin,管理员")]
        public async Task<ActionResult<ApiResponse<PromotionDetailDto>>> Update(
            string id,
            [FromBody] UpdatePromotionDto dto
        )
        {
            var res = await _service.UpdateAsync(id, dto);
            return Ok(res);
        }

        [HttpPut("store/{id}")]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        public async Task<ActionResult<ApiResponse<PromotionDetailDto>>> UpdateStorePromotion(
            string id,
            [FromQuery] string storeCode,
            [FromBody] UpdatePromotionDto dto
        )
        {
            var res = await _service.UpdateStorePromotionAsync(id, storeCode, dto);
            return Ok(res);
        }

        [HttpPost("store/copy")]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        public async Task<ActionResult<ApiResponse<PromotionDetailDto>>> CopyToStore(
            [FromBody] CopyStorePromotionRequestDto dto
        )
        {
            var res = await _service.CopyToStoreAsync(dto);
            return Ok(res);
        }

        [HttpDelete("{id}")]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        [Authorize(Roles = "Admin,管理员")]
        public async Task<ActionResult<ApiResponse<bool>>> Delete(string id)
        {
            var res = await _service.DeleteAsync(id);
            return Ok(res);
        }

        [HttpPost("{id}/enable")]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        [Authorize(Roles = "Admin,管理员")]
        public async Task<ActionResult<ApiResponse<bool>>> Enable(
            string id,
            [FromQuery] bool enable = true
        )
        {
            var res = await _service.EnableAsync(id, enable);
            return Ok(res);
        }

        [HttpPost("store/{id}/enable")]
        [Authorize(Policy = Permissions.Promotions.Edit)]
        public async Task<ActionResult<ApiResponse<bool>>> EnableStorePromotion(
            string id,
            [FromQuery] string storeCode,
            [FromQuery] bool enable = true
        )
        {
            var res = await _service.EnableStorePromotionAsync(id, storeCode, enable);
            return Ok(res);
        }

        [HttpPost("evaluate")]
        public async Task<ActionResult<ApiResponse<PromotionEvaluateResponse>>> Evaluate([FromBody] PromotionEvaluateRequest req)
        {
            var res = await _service.EvaluateAsync(req);
            return Ok(res);
        }

        [HttpGet("valid")]
        public async Task<ActionResult<ApiResponse<System.Collections.Generic.List<PromotionListDto>>>> GetValidByStore([FromQuery] string storeCode, [FromQuery] DateTime? asOf)
        {
            var res = await _service.GetValidByStoreAsync(storeCode, asOf);
            return Ok(res);
        }

        [HttpGet("valid/by-product")]
        public async Task<ActionResult<ApiResponse<System.Collections.Generic.List<PromotionListDto>>>> GetValidByProduct([FromQuery] string productCode, [FromQuery] string storeCode, [FromQuery] DateTime? asOf)
        {
            var res = await _service.GetValidByProductAndStoreAsync(productCode, storeCode, asOf);
            return Ok(res);
        }
    }
}
