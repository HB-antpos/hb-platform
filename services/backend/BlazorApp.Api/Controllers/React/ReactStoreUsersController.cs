using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/store-users")]
    [Authorize]
    public class ReactStoreUsersController : ControllerBase
    {
        private readonly IStoreUserReactService _service;
        private readonly StoreUserCashierBarcodeService _cashierBarcodeService;

        public ReactStoreUsersController(
            IStoreUserReactService service,
            StoreUserCashierBarcodeService cashierBarcodeService
        )
        {
            _service = service;
            _cashierBarcodeService = cashierBarcodeService;
        }

        [HttpPost("grid")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> Grid([FromBody] StoreUserGridRequestDto request)
        {
            var result = await _service.GetGridDataAsync(request);
            if (result.Success)
            {
                return Ok(
                    new
                    {
                        success = true,
                        data = new { items = result.Items, total = result.Total },
                        message = result.Message,
                    }
                );
            }

            return Ok(
                new
                {
                    success = false,
                    data = new { items = result.Items ?? new List<StoreUserListDto>(), total = result.Total },
                    message = result.Message,
                }
            );
        }

        [HttpGet("{userGuid}")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetByUserGuid(string userGuid, [FromQuery] string? storeCode)
        {
            var result = await _service.GetByUserGuidAsync(userGuid, storeCode);
            return Ok(result);
        }

        [HttpGet("{userGuid}/profile")]
        [Authorize(Policy = Permissions.Users.View)]
        public async Task<IActionResult> GetProfile(string userGuid, [FromQuery] string? storeCode)
        {
            var result = await _service.GetByUserGuidAsync(userGuid, storeCode);
            return Ok(result);
        }

        [HttpGet("{userGuid}/cashier-barcode")]
        [Authorize(Policy = Permissions.Users.EditStoreStaff)]
        public async Task<IActionResult> GetCashierBarcode(
            string userGuid,
            [FromQuery] string? storeCode
        ) => Ok(await _cashierBarcodeService.GetAsync(userGuid, storeCode));

        [HttpPost("{userGuid}/cashier-barcode/ensure")]
        [Authorize(Policy = Permissions.Users.EditStoreStaff)]
        public async Task<IActionResult> EnsureCashierBarcode(
            string userGuid,
            [FromBody] StoreUserCashierBarcodeEnsureRequest request
        ) => Ok(await _cashierBarcodeService.EnsureAsync(userGuid, request.StoreCode));

        [HttpPost("{userGuid}/cashier-barcode/print-confirmation")]
        [Authorize(Policy = Permissions.Users.EditStoreStaff)]
        public async Task<IActionResult> ConfirmCashierBarcodePrint(
            string userGuid,
            [FromBody] StoreUserCashierBarcodePrintConfirmationRequest request
        ) => Ok(await _cashierBarcodeService.ConfirmPrintAsync(userGuid, request));

        [HttpPost]
        [Authorize(Policy = Permissions.Users.CreateStoreStaff)]
        public async Task<IActionResult> Create([FromBody] CreateStoreUserDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(
                    ApiResponse<StoreUserDetailDto>.Error(
                        "请求参数验证失败",
                        "VALIDATION_ERROR",
                        ModelState
                    )
                );
            }

            var result = await _service.CreateAsync(dto, User.Identity?.Name ?? "system");
            return Ok(result);
        }

        [HttpPut("{userGuid}")]
        [Authorize(Policy = Permissions.Users.EditStoreStaff)]
        public async Task<IActionResult> Update(string userGuid, [FromBody] UpdateStoreUserDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(
                    ApiResponse<StoreUserDetailDto>.Error(
                        "请求参数验证失败",
                        "VALIDATION_ERROR",
                        ModelState
                    )
                );
            }

            var result = await _service.UpdateAsync(userGuid, dto, User.Identity?.Name ?? "system");
            return Ok(result);
        }

        [HttpPut("{userGuid}/status")]
        [Authorize(Policy = Permissions.Users.EditStoreStaff)]
        public async Task<IActionResult> UpdateStatus(
            string userGuid,
            [FromBody] UpdateStoreUserStatusDto dto
        )
        {
            var result = await _service.UpdateStatusAsync(
                userGuid,
                dto,
                User.Identity?.Name ?? "system"
            );
            return Ok(result);
        }

        [HttpPut("{userGuid}/password")]
        [Authorize(Policy = Permissions.Users.ResetStoreStaffPassword)]
        public async Task<IActionResult> UpdatePassword(
            string userGuid,
            [FromBody] UpdateStoreUserPasswordDto dto
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(
                    ApiResponse<bool>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState)
                );
            }

            var result = await _service.UpdatePasswordAsync(
                userGuid,
                dto,
                User.Identity?.Name ?? "system"
            );
            return Ok(result);
        }

        /// <summary>给本店店员邮箱发设置密码验证码：新建后重发，或代替店长手动重置密码。</summary>
        [HttpPost("{userGuid}/password-setup-email")]
        [Authorize(Policy = Permissions.Users.ResetStoreStaffPassword)]
        public async Task<IActionResult> SendPasswordSetupEmail(
            string userGuid,
            [FromBody] SendStoreUserPasswordSetupEmailDto dto
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(
                    ApiResponse<PasswordSetupEmailResultDto>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState)
                );
            }

            var result = await _service.SendPasswordSetupEmailAsync(
                userGuid,
                dto,
                User.Identity?.Name ?? "system"
            );
            return Ok(result);
        }
    }
}
