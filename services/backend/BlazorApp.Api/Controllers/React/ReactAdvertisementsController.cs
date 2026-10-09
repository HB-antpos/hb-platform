using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/advertisements")]
    [Authorize]
    public class ReactAdvertisementsController : ControllerBase
    {
        private readonly IAdvertisementReactService _service;

        public ReactAdvertisementsController(IAdvertisementReactService service)
        {
            _service = service;
        }

        [HttpPost("grid")]
        [Authorize(Policy = Permissions.Advertisements.View)]
        public async Task<ActionResult<GridResponseDto<AdvertisementListDto>>> Grid(
            [FromBody] AdvertisementGridRequestDto request
        )
        {
            return Ok(await _service.GetGridAsync(request));
        }

        // 分店选项只要求广告查看权限，不要求 Stores.View；字面量路由优先于下面的 {id}。
        [HttpGet("store-options")]
        [Authorize(Policy = Permissions.Advertisements.View)]
        public async Task<ActionResult<ApiResponse<List<AdvertisementStoreOptionDto>>>> StoreOptions()
        {
            return Ok(await _service.GetStoreOptionsAsync());
        }

        [HttpGet("{id}")]
        [Authorize(Policy = Permissions.Advertisements.View)]
        public async Task<ActionResult<ApiResponse<AdvertisementDetailDto>>> Get(string id)
        {
            return Ok(await _service.GetByIdAsync(id));
        }

        [HttpPost]
        [Authorize(Policy = Permissions.Advertisements.Edit)]
        public async Task<ActionResult<ApiResponse<AdvertisementDetailDto>>> Create(
            [FromBody] CreateAdvertisementDto dto
        )
        {
            return Ok(await _service.CreateAsync(dto));
        }

        [HttpPut("{id}")]
        [Authorize(Policy = Permissions.Advertisements.Edit)]
        public async Task<ActionResult<ApiResponse<AdvertisementDetailDto>>> Update(
            string id,
            [FromBody] UpdateAdvertisementDto dto
        )
        {
            return Ok(await _service.UpdateAsync(id, dto));
        }

        [HttpDelete("{id}")]
        [Authorize(Policy = Permissions.Advertisements.Edit)]
        public async Task<ActionResult<ApiResponse<bool>>> Delete(string id)
        {
            return Ok(await _service.DeleteAsync(id));
        }

        [HttpPost("{id}/enable")]
        [Authorize(Policy = Permissions.Advertisements.Edit)]
        public async Task<ActionResult<ApiResponse<bool>>> Enable(
            string id,
            [FromQuery] bool enable = true
        )
        {
            return Ok(await _service.EnableAsync(id, enable));
        }

        [HttpPost("upload-signature")]
        [Authorize(Policy = Permissions.Advertisements.Edit)]
        public async Task<ActionResult<ApiResponse<AdvertisementUploadSignatureResponseDto>>> UploadSignature(
            [FromBody] AdvertisementUploadSignatureRequestDto request
        )
        {
            return Ok(await _service.GetUploadSignatureAsync(request));
        }
    }
}
