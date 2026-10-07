using BlazorApp.Api.Data;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/local-supplier-invoices")]
    [Authorize]
    public class ReactLocalSupplierInvoicesController : ControllerBase
    {
        private readonly ILocalSupplierInvoicesReactService _service;
        private readonly ILocalSupplierInvoiceHqSyncService _hqSyncService;
        private readonly ILocalSupplierInvoiceHqProductSyncService? _hqProductSyncService;
        private readonly ILocalSupplierInvoiceBatchUpdateJobService? _batchUpdateJobService;
        private readonly ILocalSupplierInvoiceImportService _importService;
        private readonly SqlSugarContext _dbContext;
        private readonly IAuthorizationService? _authorizationService;

        private static readonly string[] LocalSupplierInvoiceReadPermissions =
        {
            Permissions.LocalPurchase.MobileView,
            Permissions.LocalPurchase.View,
        };

        private static readonly string[] ShopLocalSupplierInvoiceReadPermissions =
        {
            Permissions.OrderFront.View,
        };

        public ReactLocalSupplierInvoicesController(
            ILocalSupplierInvoicesReactService service,
            SqlSugarContext dbContext,
            ILocalSupplierInvoiceHqSyncService hqSyncService,
            ILocalSupplierInvoiceImportService importService,
            ILocalSupplierInvoiceHqProductSyncService? hqProductSyncService = null,
            ILocalSupplierInvoiceBatchUpdateJobService? batchUpdateJobService = null,
            IAuthorizationService? authorizationService = null
        )
        {
            _service = service;
            _dbContext = dbContext;
            _hqSyncService = hqSyncService;
            _importService = importService;
            _hqProductSyncService = hqProductSyncService;
            _batchUpdateJobService = batchUpdateJobService;
            _authorizationService = authorizationService;
        }

        private async Task<bool> HasLocalSupplierInvoiceReadPermissionAsync()
        {
            if (_authorizationService == null)
            {
                return false;
            }

            foreach (var permission in LocalSupplierInvoiceReadPermissions)
            {
                if ((await _authorizationService.AuthorizeAsync(User, null, permission)).Succeeded)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<bool> HasShopLocalSupplierInvoiceReadPermissionAsync()
        {
            if (await HasLocalSupplierInvoiceReadPermissionAsync())
            {
                return true;
            }

            if (_authorizationService == null)
            {
                return false;
            }

            foreach (var permission in ShopLocalSupplierInvoiceReadPermissions)
            {
                if ((await _authorizationService.AuthorizeAsync(User, null, permission)).Succeeded)
                {
                    return true;
                }
            }

            // 与 Web canAccessOrderFront 对齐：Orders.Create 只兼容纯仓库员工，不能扩成通用读取权限。
            return IsWarehouseStaffOnly()
                && (
                    await _authorizationService.AuthorizeAsync(
                        User,
                        null,
                        Permissions.Orders.Create
                    )
                ).Succeeded;
        }

        private bool IsWarehouseStaffOnly()
        {
            return HasAnyRole("WarehouseStaff", "仓库员工")
                && !HasAnyRole("Admin", "管理员")
                && !HasAnyRole("WarehouseManager", "仓库经理");
        }

        private bool HasAnyRole(params string[] roles)
        {
            return User?.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && roles.Any(role => role.Equals(claim.Value, StringComparison.OrdinalIgnoreCase))
            ) == true;
        }

        private bool IsFullStoreAccessUser()
        {
            var user = User;
            if (user == null) return false;
            return user.Claims.Any(c =>
                c.Type == ClaimTypes.Role
                // 超级管理员角色统一走共享权限常量，避免“管理员”通过授权元数据却被分店 gate 拦截。
                && (Permissions.IsSuperAdminRole(c.Value)
                    || c.Value.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase))
            );
        }

        private string GetCurrentUserGuid()
        {
            return User?.FindFirst("userId")?.Value
                ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? string.Empty;
        }

        private async Task<List<string>> GetCurrentUserStoreCodesAsync()
        {
            var userGuid = GetCurrentUserGuid();
            if (string.IsNullOrEmpty(userGuid))
                return new List<string>();

            var storeGuids = await _dbContext.Db.Queryable<UserStore>()
                .Where(us => us.UserGUID == userGuid)
                .Select(us => us.StoreGUID)
                .ToListAsync();

            if (!storeGuids.Any())
                return new List<string>();

            var codes = await _dbContext.Db.Queryable<Store>()
                .Where(s => storeGuids.Contains(s.StoreGUID))
                .Select(s => s.StoreCode)
                .ToListAsync();

            return codes.Where(c => !string.IsNullOrEmpty(c)).ToList();
        }

        private async Task<bool> CanAccessInvoiceAsync(string invoiceGuid)
        {
            if (IsFullStoreAccessUser())
                return true;

            var userStoreCodes = await GetCurrentUserStoreCodesAsync();
            if (!userStoreCodes.Any())
                return false;

            var storeCode = await _dbContext.Db.Queryable<StoreLocalSupplierInvoice>()
                .Where(i => i.InvoiceGUID == invoiceGuid && i.IsDeleted == false)
                .Select(i => i.StoreCode)
                .FirstAsync();

            return !string.IsNullOrEmpty(storeCode) && userStoreCodes.Contains(storeCode);
        }

        private async Task<bool> CanAccessStoreAsync(string? storeCode)
        {
            if (IsFullStoreAccessUser())
                return true;

            if (string.IsNullOrEmpty(storeCode))
                return false;

            var userStoreCodes = await GetCurrentUserStoreCodesAsync();
            return userStoreCodes.Contains(storeCode);
        }

        private async Task<bool> CanAccessAllStoresAsync(IEnumerable<string?> storeCodes)
        {
            if (IsFullStoreAccessUser())
                return true;

            var requestedStoreCodes = storeCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct()
                .ToList();
            if (!requestedStoreCodes.Any())
                return false;

            var userStoreCodes = await GetCurrentUserStoreCodesAsync();
            return requestedStoreCodes.All(userStoreCodes.Contains);
        }

        private async Task<bool> CanAccessAllEnabledStoresAsync()
        {
            if (IsFullStoreAccessUser())
                return true;

            var activeStoreCodes = await _dbContext.Db.Queryable<Store>()
                .Where(store => store.IsActive == true && store.IsDeleted == false)
                .Select(store => store.StoreCode)
                .ToListAsync();
            activeStoreCodes = activeStoreCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct()
                .ToList();
            if (!activeStoreCodes.Any())
                return false;

            var userStoreCodes = await GetCurrentUserStoreCodesAsync();
            return activeStoreCodes.All(userStoreCodes.Contains);
        }

        private async Task<(
            ApiResponse<BatchExecuteConfirmationDetailsDto>? Error,
            List<StoreLocalSupplierInvoiceDetails> ConfirmedDetails
        )> ValidateBatchExecuteConfirmationAsync(
            string invoiceGuid,
            BatchExecuteActionsRequestDto dto
        )
        {
            var selectedDetailGuids = dto.DetailGuids
                .Where(detailGuid => !string.IsNullOrWhiteSpace(detailGuid))
                .Distinct()
                .ToList();

            if (selectedDetailGuids.Count == 0)
                return (null, new List<StoreLocalSupplierInvoiceDetails>());

            var expectedActions = dto.ExpectedActions
                .Where(item => !string.IsNullOrWhiteSpace(item.DetailGuid))
                .GroupBy(item => item.DetailGuid)
                .Select(group => group.First())
                .ToList();

            if (expectedActions.Count == 0 || dto.ConfirmedCreateProductCount == null)
            {
                return (
                    ApiResponse<BatchExecuteConfirmationDetailsDto>.Error(
                        "批量执行确认已失效：缺少确认动作快照，请刷新后重试",
                        "VALIDATION_ERROR",
                        new BatchExecuteConfirmationDetailsDto
                        {
                            RequestedDetailCount = selectedDetailGuids.Count,
                            CurrentDetailCount = 0,
                            ConfirmedCreateProductCount = dto.ConfirmedCreateProductCount,
                            CurrentCreateProductCount = 0,
                        }
                    ),
                    new List<StoreLocalSupplierInvoiceDetails>()
                );
            }

            var currentDetails = await _dbContext.Db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(detail =>
                    detail.InvoiceGUID == invoiceGuid
                    && selectedDetailGuids.Contains(detail.DetailGUID)
                    && detail.IsDeleted == false
                )
                .ToListAsync();

            var currentByDetailGuid = currentDetails.ToDictionary(detail => detail.DetailGUID);
            var mismatchDetails = new List<BatchExecuteConfirmationMismatchDetailDto>();

            if (expectedActions.Count != selectedDetailGuids.Count)
            {
                mismatchDetails.Add(new BatchExecuteConfirmationMismatchDetailDto
                {
                    Message = "确认动作数量与当前选中明细数量不一致",
                });
            }

            foreach (var detailGuid in selectedDetailGuids.Where(detailGuid =>
                         expectedActions.All(item => item.DetailGuid != detailGuid)))
            {
                mismatchDetails.Add(new BatchExecuteConfirmationMismatchDetailDto
                {
                    DetailGuid = detailGuid,
                    Message = "缺少该明细的确认动作",
                });
            }

            foreach (var expectedAction in expectedActions)
            {
                var expectedValue = expectedAction.GetActionValue();
                if (!currentByDetailGuid.TryGetValue(expectedAction.DetailGuid, out var currentDetail))
                {
                    mismatchDetails.Add(new BatchExecuteConfirmationMismatchDetailDto
                    {
                        DetailGuid = expectedAction.DetailGuid,
                        ExpectedAction = expectedValue,
                        Message = "明细不存在、已删除或不属于当前进货单",
                    });
                    continue;
                }

                var currentValue = currentDetail.ActivityType ?? (int)DetailAction.None;
                if (expectedValue == null)
                {
                    mismatchDetails.Add(new BatchExecuteConfirmationMismatchDetailDto
                    {
                        DetailGuid = expectedAction.DetailGuid,
                        CurrentAction = currentValue,
                        Message = "缺少确认动作值",
                    });
                    continue;
                }

                if (expectedValue.Value != currentValue)
                {
                    mismatchDetails.Add(new BatchExecuteConfirmationMismatchDetailDto
                    {
                        DetailGuid = expectedAction.DetailGuid,
                        ExpectedAction = expectedValue,
                        CurrentAction = currentValue,
                        Message = "确认动作与数据库当前动作不一致",
                    });
                }
            }

            var currentCreateProductCount = currentDetails.Count(detail =>
                (detail.ActivityType ?? (int)DetailAction.None) == (int)DetailAction.CreateProduct
            );

            if (dto.ConfirmedCreateProductCount.Value != currentCreateProductCount)
            {
                mismatchDetails.Add(new BatchExecuteConfirmationMismatchDetailDto
                {
                    Message = "确认的新建商品数量与数据库当前数量不一致",
                });
            }

            if (mismatchDetails.Count == 0)
                return (null, currentDetails);

            var details = new BatchExecuteConfirmationDetailsDto
            {
                RequestedDetailCount = selectedDetailGuids.Count,
                CurrentDetailCount = currentDetails.Count,
                ConfirmedCreateProductCount = dto.ConfirmedCreateProductCount,
                CurrentCreateProductCount = currentCreateProductCount,
                MismatchedDetails = mismatchDetails,
            };

            var message = dto.ConfirmedCreateProductCount.Value != currentCreateProductCount
                ? "批量执行确认已失效：创建商品数量已变化，请刷新后重试"
                : "批量执行确认已失效：明细动作已变化，请刷新后重试";

            return (
                ApiResponse<BatchExecuteConfirmationDetailsDto>.Error(
                    message,
                    "VALIDATION_ERROR",
                    details
                ),
                new List<StoreLocalSupplierInvoiceDetails>()
            );
        }

        private static ShopLocalSupplierInvoiceHeaderDto ToShopHeader(
            LocalSupplierInvoiceListDto source
        )
        {
            return new ShopLocalSupplierInvoiceHeaderDto
            {
                InvoiceGUID = source.InvoiceGUID,
                StoreCode = source.StoreCode,
                StoreName = source.StoreName,
                SupplierCode = source.SupplierCode,
                SupplierName = source.SupplierName,
                InvoiceNo = source.InvoiceNo,
                OrderDate = source.OrderDate,
                InboundDate = source.InboundDate,
                TotalAmount = source.TotalAmount,
                ReceivedTotalAmount = source.ReceivedTotalAmount,
                FlowStatus = source.FlowStatus,
                InboundStatus = source.InboundStatus,
                Remarks = source.Remarks,
            };
        }

        private static ShopLocalSupplierInvoiceHeaderDto ToShopHeader(
            LocalSupplierInvoiceDetailDto source
        )
        {
            return new ShopLocalSupplierInvoiceHeaderDto
            {
                InvoiceGUID = source.InvoiceGUID,
                StoreCode = source.StoreCode,
                StoreName = source.StoreName,
                SupplierCode = source.SupplierCode,
                SupplierName = source.SupplierName,
                InvoiceNo = source.InvoiceNo,
                OrderDate = source.OrderDate,
                InboundDate = source.InboundDate,
                TotalAmount = source.TotalAmount,
                ReceivedTotalAmount = source.ReceivedTotalAmount,
                FlowStatus = source.FlowStatus,
                InboundStatus = source.InboundStatus,
                Remarks = source.Remarks,
            };
        }

        private static ShopLocalSupplierInvoiceItemDto ToShopItem(
            LocalSupplierInvoiceItemDto source
        )
        {
            return new ShopLocalSupplierInvoiceItemDto
            {
                DetailGUID = source.DetailGUID,
                StoreProductCode = source.StoreProductCode,
                ProductCode = source.ProductCode,
                ItemNumber = source.ItemNumber,
                Barcode = source.Barcode,
                ProductName = source.ProductName,
                ProductImage = source.ProductImage,
                Specification = source.Specification,
                Unit = source.Unit,
                Quantity = source.Quantity,
                LastPurchasePrice = source.LastPurchasePrice,
                PurchasePrice = source.PurchasePrice,
                RetailPrice = source.RetailPrice,
                Amount = source.Amount,
                NewAutoRetailPrice = source.NewAutoRetailPrice,
            };
        }

        [HttpPost("grid")]
        public async Task<IActionResult> Grid([FromBody] GridRequestDto request)
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            var allowedStoreCodes = IsFullStoreAccessUser()
                ? null
                : await GetCurrentUserStoreCodesAsync();
            var result = await _service.GetGridDataAsync(request, allowedStoreCodes);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = new { Items = result.Items, Total = result.Total },
                        message = result.Message,
                    }
                );
            return Ok(
                new
                {
                    success = false,
                    data = new
                    {
                        Items = result.Items ?? new List<LocalSupplierInvoiceListDto>(),
                        Total = result.Total,
                    },
                    message = result.Message,
                }
            );
        }

        /// <summary>
        /// 列表左侧分店栏的单数。请求体与 grid 相同（调用方去掉分店条件），权限与分店范围口径也与 grid 一致。
        /// </summary>
        [HttpPost("store-counts")]
        public async Task<IActionResult> StoreCounts([FromBody] GridRequestDto request)
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            var allowedStoreCodes = IsFullStoreAccessUser()
                ? null
                : await GetCurrentUserStoreCodesAsync();
            var result = await _service.GetStoreCountsAsync(request, allowedStoreCodes);
            return Ok(
                new
                {
                    success = result.Success,
                    data = result.Data ?? new List<LocalSupplierInvoiceStoreCountDto>(),
                    message = result.Message,
                }
            );
        }

        [HttpPost("shop/grid")]
        public async Task<IActionResult> ShopGrid([FromBody] GridRequestDto request)
        {
            if (!await HasShopLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            var allowedStoreCodes = IsFullStoreAccessUser()
                ? null
                : await GetCurrentUserStoreCodesAsync();
            var result = await _service.GetGridDataAsync(request, allowedStoreCodes);
            if (result.Success)
            {
                return Ok(
                    new
                    {
                        success = true,
                        data = new
                        {
                            Items = (result.Items ?? new List<LocalSupplierInvoiceListDto>())
                                .Select(ToShopHeader)
                                .ToList(),
                            Total = result.Total,
                        },
                        message = result.Message,
                    }
                );
            }

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    success = false,
                    data = new
                    {
                        Items = new List<ShopLocalSupplierInvoiceHeaderDto>(),
                        Total = result.Total,
                    },
                    message = result.Message,
                }
            );
        }

        [HttpGet("shop/filter-options")]
        public async Task<IActionResult> GetFilterOptions([FromQuery] string? storeCode)
        {
            if (!await HasShopLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            var normalizedStoreCode = storeCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedStoreCode))
            {
                normalizedStoreCode = null;
            }

            if (
                normalizedStoreCode != null
                && !await CanAccessStoreAsync(normalizedStoreCode)
            )
            {
                return Forbid();
            }

            var allowedStoreCodes = IsFullStoreAccessUser()
                ? null
                : await GetCurrentUserStoreCodesAsync();
            var result = await _service.GetFilterOptionsAsync(
                allowedStoreCodes,
                normalizedStoreCode
            );
            if (result.Success)
            {
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            }

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    success = false,
                    data = new LocalSupplierInvoiceFilterOptionsDto(),
                    message = result.Message,
                    errorCode = result.ErrorCode,
                }
            );
        }

        [HttpGet("{invoiceGuid}")]
        public async Task<IActionResult> GetInvoice(string invoiceGuid)
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetInvoiceAsync(invoiceGuid);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return NotFound(new { success = false, message = result.Message });
        }

        [HttpGet("shop/{invoiceGuid}")]
        public async Task<IActionResult> GetShopInvoice(string invoiceGuid)
        {
            if (!await HasShopLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetInvoiceAsync(invoiceGuid);
            if (result.Success && result.Data != null)
            {
                return Ok(
                    new
                    {
                        success = true,
                        data = ToShopHeader(result.Data),
                        message = result.Message,
                    }
                );
            }

            var error = new
            {
                success = false,
                message = result.Message,
                errorCode = result.ErrorCode,
            };
            return result.ErrorCode == "NOT_FOUND"
                ? NotFound(error)
                : StatusCode(StatusCodes.Status500InternalServerError, error);
        }

        [HttpGet("{invoiceGuid}/details")]
        public async Task<IActionResult> GetDetails(string invoiceGuid)
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetDetailsAsync(invoiceGuid);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("{invoiceGuid}/details/grid")]
        public async Task<IActionResult> GetDetailsGrid(
            string invoiceGuid,
            [FromBody] GridRequestDto request
        )
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetDetailsGridAsync(invoiceGuid, request);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = new { Items = result.Items, Total = result.Total },
                        message = result.Message,
                    }
                );
            return Ok(
                new
                {
                    success = false,
                    data = new
                    {
                        Items = result.Items ?? new List<LocalSupplierInvoiceItemDto>(),
                        Total = result.Total,
                    },
                    message = result.Message,
                }
            );
        }

        [HttpPost("shop/{invoiceGuid}/details/grid")]
        public async Task<IActionResult> GetShopDetailsGrid(
            string invoiceGuid,
            [FromBody] GridRequestDto request
        )
        {
            if (!await HasShopLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetDetailsGridAsync(invoiceGuid, request);
            if (result.Success)
            {
                return Ok(
                    new
                    {
                        success = true,
                        data = new
                        {
                            Items = (result.Items ?? new List<LocalSupplierInvoiceItemDto>())
                                .Select(ToShopItem)
                                .ToList(),
                            Total = result.Total,
                        },
                        message = result.Message,
                    }
                );
            }

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    success = false,
                    data = new
                    {
                        Items = new List<ShopLocalSupplierInvoiceItemDto>(),
                        Total = result.Total,
                    },
                    message = result.Message,
                }
            );
        }

        [HttpPost]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> Create([FromBody] CreateInvoiceRequest dto)
        {
            if (!await CanAccessStoreAsync(dto.StoreCode))
                return Forbid();

            var result = await _service.CreateAsync(dto);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("import/preview")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        [Authorize(Roles = "Admin,管理员")]
        public async Task<IActionResult> ImportPreview(
            [FromForm] IFormFile file,
            CancellationToken cancellationToken
        )
        {
            var result = await _importService.PreviewAsync(file, cancellationToken);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );

            return BadRequest(
                new
                {
                    success = false,
                    message = result.Message,
                    code = result.ErrorCode,
                    details = result.Details,
                }
            );
        }

        [HttpPost("import/confirm")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        [Authorize(Roles = "Admin,管理员")]
        public async Task<IActionResult> ImportConfirm(
            [FromBody] LocalSupplierInvoiceImportConfirmRequest dto,
            CancellationToken cancellationToken
        )
        {
            if (dto?.Header == null)
                return BadRequest(new { success = false, message = "缺少导入确认数据" });

            // 确认导入前再次校验分店权限，避免用户通过改包导入无权门店。
            if (!await CanAccessStoreAsync(dto.Header.StoreCode))
                return Forbid();

            var result = await _importService.ConfirmAsync(dto, cancellationToken);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );

            return BadRequest(
                new
                {
                    success = false,
                    message = result.Message,
                    code = result.ErrorCode,
                    details = result.Details,
                }
            );
        }

        [HttpPut("{invoiceGuid}")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> Update(
            string invoiceGuid,
            [FromBody] UpdateInvoiceRequest dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();
            if (!string.IsNullOrWhiteSpace(dto.StoreCode) && !await CanAccessStoreAsync(dto.StoreCode))
                return Forbid();

            var result = await _service.UpdateAsync(invoiceGuid, dto);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("{invoiceGuid}/details/batch-upsert")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> BatchUpsertDetails(
            string invoiceGuid,
            [FromBody] List<InvoiceDetailUpsertItemDto> items
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            var result = await _service.BatchUpsertDetailsAsync(invoiceGuid, items, user);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpDelete("{invoiceGuid}")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> Delete(string invoiceGuid)
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            var result = await _service.DeleteAsync(invoiceGuid, user);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("detect/supplier-item")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> DetectSupplierItem(
            [FromBody] DetectSupplierItemRequest dto
        )
        {
            if (!await CanAccessStoreAsync(dto.StoreCode))
                return Forbid();

            var result = await _service.DetectSupplierItemAsync(dto);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("detect/barcode")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> DetectBarcode([FromBody] DetectBarcodeRequest dto)
        {
            if (!await CanAccessStoreAsync(dto.StoreCode))
                return Forbid();

            var result = await _service.DetectBarcodeAsync(dto);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("update-to-store-prices")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> UpdateToStorePrices(
            [FromBody] UpdateToStorePricesRequest dto
        )
        {
            if (!await CanAccessInvoiceAsync(dto.InvoiceGuid))
                return Forbid();
            if (dto.TargetStoreCodes == null || !await CanAccessAllStoresAsync(dto.TargetStoreCodes))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            var result = await _service.UpdateDetailsToStorePricesAsync(dto, user);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            var error = new
            {
                success = false,
                message = result.Message,
                code = result.ErrorCode,
                details = result.Details ?? result.Data,
            };
            // 获锁阶段确认零写入的专用码也属于成本冲突，兼容同步调用方的 409 语义。
            return result.ErrorCode is SetChildPurchasePriceMutationLock.BusyErrorCode or "STORE_UPDATE_COST_LOCK_BUSY"
                ? StatusCode(StatusCodes.Status409Conflict, error)
                : BadRequest(error);
        }

        [HttpPost("update-to-store-prices/jobs")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> StartUpdateToStorePricesJob(
            [FromBody] UpdateToStorePricesRequest? dto,
            CancellationToken cancellationToken
        )
        {
            if (dto == null)
                return BadRequest(new { success = false, message = "请求参数不能为空" });
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (!await CanAccessInvoiceAsync(dto.InvoiceGuid))
                return Forbid();
            if (dto.TargetStoreCodes == null || !await CanAccessAllStoresAsync(dto.TargetStoreCodes))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            try
            {
                var job = await _batchUpdateJobService.StartUpdateToStorePricesJobAsync(
                    dto,
                    user,
                    cancellationToken
                );
                return Ok(new { success = true, data = job, message = "更新到分店价格任务已提交" });
            }
            catch (LocalSupplierInvoiceBatchUpdateJobConflictException ex)
            {
                return Conflict(new { success = false, message = ex.Message, data = new { ex.ExistingJobId } });
            }
        }

        [HttpGet("update-to-store-prices/jobs/{jobId}")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> GetUpdateToStorePricesJob(
            [FromRoute] string jobId,
            CancellationToken cancellationToken
        )
        {
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { success = false, message = "jobId 不能为空" });

            var job = await _batchUpdateJobService.GetUpdateToStorePricesJobAsync(jobId, cancellationToken);
            if (job == null)
                return NotFound(new { success = false, message = "更新到分店价格任务不存在或已过期" });

            if (!await CanAccessInvoiceAsync(job.InvoiceGuid))
                return Forbid();
            if (!await CanAccessAllStoresAsync(job.TargetStoreCodes))
                return Forbid();

            return Ok(new { success = true, data = job, message = "查询成功" });
        }

        [HttpPost("check-products")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> CheckProducts([FromBody] CheckProductsRequest dto)
        {
            if (!await CanAccessInvoiceAsync(dto.InvoiceGuid))
                return Forbid();

            var result = await _service.CheckProductsAsync(dto);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("check-products/jobs")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> StartCheckProductsJob(
            [FromBody] CheckProductsRequest? dto,
            CancellationToken cancellationToken
        )
        {
            if (dto == null)
                return BadRequest(new { success = false, message = "请求参数不能为空" });
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (!await CanAccessInvoiceAsync(dto.InvoiceGuid))
                return Forbid();

            try
            {
                var job = await _batchUpdateJobService.StartCheckProductsJobAsync(
                    dto,
                    cancellationToken
                );
                return Ok(new { success = true, data = job, message = "商品检测任务已提交" });
            }
            catch (LocalSupplierInvoiceBatchUpdateJobConflictException ex)
            {
                return Conflict(new { success = false, message = ex.Message, data = new { ex.ExistingJobId } });
            }
        }

        [HttpGet("{invoiceGuid}/check-products/jobs/{jobId}")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> GetCheckProductsJob(
            [FromRoute] string invoiceGuid,
            [FromRoute] string jobId,
            CancellationToken cancellationToken
        )
        {
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { success = false, message = "jobId 不能为空" });
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var job = await _batchUpdateJobService.GetCheckProductsJobAsync(jobId, cancellationToken);
            if (job == null)
                return NotFound(new { success = false, message = "商品检测任务不存在或已过期" });
            if (!string.Equals(job.InvoiceGuid, invoiceGuid, StringComparison.OrdinalIgnoreCase))
                return NotFound(new { success = false, message = "商品检测任务不存在或已过期" });

            return Ok(new { success = true, data = job, message = "查询成功" });
        }

        [HttpPost("check-products/batch-jobs")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> StartBatchCheckProductsJob(
            [FromBody] StartBatchCheckProductsRequest? dto,
            CancellationToken cancellationToken
        )
        {
            if (dto == null)
                return BadRequest(new { success = false, message = "请求参数不能为空" });
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });

            var invoiceGuids = (dto.InvoiceGuids ?? new List<string>())
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Select(guid => guid.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (invoiceGuids.Count == 0)
                return BadRequest(new { success = false, message = "请选择要检测的进货单" });
            if (invoiceGuids.Count > LocalSupplierInvoiceBatchCheckProductsLimits.MaxInvoices)
                return BadRequest(new
                {
                    success = false,
                    message = $"单次最多检测 {LocalSupplierInvoiceBatchCheckProductsLimits.MaxInvoices} 张进货单",
                });

            var headers = await _dbContext.Db.Queryable<StoreLocalSupplierInvoice>()
                .Where(i => invoiceGuids.Contains(i.InvoiceGUID) && i.IsDeleted == false)
                .Select(i => new { i.InvoiceGUID, i.StoreCode })
                .ToListAsync();
            // 关键位置：分店范围用户必须每张单都能找到且属于自己的分店；全局用户找不到的单交给任务标记失败。
            if (!IsFullStoreAccessUser() && headers.Count != invoiceGuids.Count)
                return Forbid();
            var storeCodes = headers
                .Select(h => h.StoreCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct()
                .ToList();
            if (!await CanAccessAllStoresAsync(storeCodes))
                return Forbid();

            var job = await _batchUpdateJobService.StartBatchCheckProductsJobAsync(
                invoiceGuids,
                storeCodes,
                cancellationToken
            );
            return Ok(new { success = true, data = job, message = "批量商品检测任务已提交" });
        }

        [HttpGet("check-products/batch-jobs/{jobId}")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> GetBatchCheckProductsJob(
            [FromRoute] string jobId,
            CancellationToken cancellationToken
        )
        {
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { success = false, message = "jobId 不能为空" });

            var job = await _batchUpdateJobService.GetBatchCheckProductsJobAsync(jobId, cancellationToken);
            if (job == null)
                return NotFound(new { success = false, message = "批量商品检测任务不存在或已过期" });
            if (!IsFullStoreAccessUser() && !await CanAccessAllStoresAsync(job.StoreCodes))
                return Forbid();

            return Ok(new { success = true, data = job, message = "查询成功" });
        }

        [HttpPost("check-products/batch-jobs/{jobId}/cancel")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> CancelBatchCheckProductsJob(
            [FromRoute] string jobId,
            CancellationToken cancellationToken
        )
        {
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { success = false, message = "jobId 不能为空" });

            var existing = await _batchUpdateJobService.GetBatchCheckProductsJobAsync(jobId, cancellationToken);
            if (existing == null)
                return NotFound(new { success = false, message = "批量商品检测任务不存在或已过期" });
            if (!IsFullStoreAccessUser() && !await CanAccessAllStoresAsync(existing.StoreCodes))
                return Forbid();

            var job = await _batchUpdateJobService.CancelBatchCheckProductsJobAsync(jobId, cancellationToken);
            return Ok(new { success = true, data = job, message = "已停止剩余检测" });
        }

        [HttpPost("{invoiceGuid}/details/ensure-hq-products")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> EnsureHqProducts(
            [FromRoute] string invoiceGuid,
            [FromBody] EnsureHqProductsRequest dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();
            if (dto.TargetStoreCodes == null || !await CanAccessAllStoresAsync(dto.TargetStoreCodes))
                return Forbid();
            if (!await CanAccessAllEnabledStoresAsync())
                return Forbid();
            if (_hqProductSyncService == null)
                return BadRequest(new { success = false, message = "HQ商品同步服务未注册" });

            var user = User.Identity?.Name ?? "system";
            var result = await _hqProductSyncService.EnsureHqProductsAsync(invoiceGuid, dto, user);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(
                new
                {
                    success = false,
                    message = result.Message,
                    details = result.Details ?? result.Data,
                }
            );
        }

        [HttpPost("{invoiceGuid}/details/update-hq-products")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        [Authorize(Policy = Permissions.LocalPurchase.PushToHq)]
        public async Task<IActionResult> UpdateHqProducts(
            [FromRoute] string invoiceGuid,
            [FromBody] UpdateHqProductsRequest? dto
        )
        {
            if (dto == null)
                return BadRequest(new { success = false, message = "请求参数不能为空" });
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();
            if (dto.TargetStoreCodes == null || !await CanAccessAllStoresAsync(dto.TargetStoreCodes))
                return Forbid();
            if (_hqProductSyncService == null)
                return BadRequest(new { success = false, message = "HQ商品更新服务未注册" });

            var user = User.Identity?.Name ?? "system";
            var result = await _hqProductSyncService.UpdateHqProductsAsync(invoiceGuid, dto, user);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(
                new
                {
                    success = false,
                    message = result.Message,
                    details = result.Details ?? result.Data,
                }
            );
        }

        [HttpPost("{invoiceGuid}/details/update-hq-products/jobs")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        [Authorize(Policy = Permissions.LocalPurchase.PushToHq)]
        public async Task<IActionResult> StartUpdateHqProductsJob(
            [FromRoute] string invoiceGuid,
            [FromBody] UpdateHqProductsRequest? dto,
            CancellationToken cancellationToken
        )
        {
            if (dto == null)
                return BadRequest(new { success = false, message = "请求参数不能为空" });
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();
            if (dto.TargetStoreCodes == null || !await CanAccessAllStoresAsync(dto.TargetStoreCodes))
                return Forbid();

            var actorUserGuid = GetCurrentUserGuid();
            var actorName = User.Identity?.Name ?? "system";
            try
            {
                var job = await _batchUpdateJobService.StartUpdateHqProductsJobAsync(
                    invoiceGuid,
                    dto,
                    actorUserGuid,
                    actorName,
                    cancellationToken
                );
                return Ok(new { success = true, data = job, message = "更新HQ商品任务已提交" });
            }
            catch (LocalSupplierInvoiceBatchUpdateJobConflictException ex)
            {
                return Conflict(new { success = false, message = ex.Message, data = new { ex.ExistingJobId } });
            }
        }

        [HttpGet("{invoiceGuid}/details/update-hq-products/jobs/{jobId}")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        [Authorize(Policy = Permissions.LocalPurchase.PushToHq)]
        public async Task<IActionResult> GetUpdateHqProductsJob(
            [FromRoute] string invoiceGuid,
            [FromRoute] string jobId,
            CancellationToken cancellationToken
        )
        {
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { success = false, message = "jobId 不能为空" });
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var job = await _batchUpdateJobService.GetUpdateHqProductsJobAsync(jobId, cancellationToken);
            if (job == null)
                return NotFound(new { success = false, message = "更新HQ商品任务不存在或已过期" });
            if (!string.Equals(job.InvoiceGuid, invoiceGuid, StringComparison.OrdinalIgnoreCase))
                return NotFound(new { success = false, message = "更新HQ商品任务不存在或已过期" });
            if (!await CanAccessAllStoresAsync(job.TargetStoreCodes))
                return Forbid();

            return Ok(new { success = true, data = job, message = "查询成功" });
        }

        [HttpPost("{invoiceGuid}/details/paste")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> PasteDetails(
            [FromRoute] string invoiceGuid,
            [FromBody] PasteDetailsRequest dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            dto.InvoiceGuid = invoiceGuid;
            var result = await _service.PasteDetailsAsync(dto, user);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("{invoiceGuid}/details/paste/jobs")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> StartPasteDetailsJob(
            [FromRoute] string invoiceGuid,
            [FromBody] PasteDetailsRequest? dto,
            CancellationToken cancellationToken
        )
        {
            if (dto == null)
                return BadRequest(new { success = false, message = "请求参数不能为空" });
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            dto.InvoiceGuid = invoiceGuid;
            try
            {
                var job = await _batchUpdateJobService.StartPasteDetailsJobAsync(
                    dto,
                    user,
                    cancellationToken
                );
                return Ok(new { success = true, data = job, message = "粘贴明细任务已提交" });
            }
            catch (LocalSupplierInvoiceBatchUpdateJobConflictException ex)
            {
                return Conflict(new { success = false, message = ex.Message, data = new { ex.ExistingJobId } });
            }
        }

        [HttpGet("{invoiceGuid}/details/paste/jobs/{jobId}")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> GetPasteDetailsJob(
            [FromRoute] string invoiceGuid,
            [FromRoute] string jobId,
            CancellationToken cancellationToken
        )
        {
            if (_batchUpdateJobService == null)
                return BadRequest(new { success = false, message = "本地进货单后台任务服务未注册" });
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { success = false, message = "jobId 不能为空" });
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var job = await _batchUpdateJobService.GetPasteDetailsJobAsync(jobId, cancellationToken);
            if (job == null)
                return NotFound(new { success = false, message = "粘贴明细任务不存在或已过期" });
            if (!string.Equals(job.InvoiceGuid, invoiceGuid, StringComparison.OrdinalIgnoreCase))
                return NotFound(new { success = false, message = "粘贴明细任务不存在或已过期" });

            return Ok(new { success = true, data = job, message = "查询成功" });
        }

        [HttpPost("{invoiceGuid}/details/batch-update")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> BatchUpdateDetails(
            [FromRoute] string invoiceGuid,
            [FromBody] BatchUpdateInvoiceDetailsRequest dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            var result = await _service.BatchUpdateDetailsAsync(invoiceGuid, dto, user);
            if (result.Success)
                return Ok(new { success = true, data = result.Data, message = result.Message });
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("{invoiceGuid}/details/update-last-purchase-prices")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        [Authorize(Roles = "Admin,管理员")]
        public async Task<IActionResult> UpdateLastPurchasePrices(
            [FromRoute] string invoiceGuid,
            [FromBody] UpdateLastPurchasePricesRequest? dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            var result = await _service.UpdateLastPurchasePricesAsync(
                invoiceGuid,
                dto ?? new UpdateLastPurchasePricesRequest(),
                user
            );
            if (result.Success)
                return Ok(new { success = true, data = result.Data, message = result.Message });
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPut("{invoiceGuid}/details/{detailGuid}/action")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> UpdateDetailAction(
            [FromRoute] string invoiceGuid,
            [FromRoute] string detailGuid,
            [FromBody] UpdateDetailActionRequest dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.UpdateDetailActionAsync(
                invoiceGuid,
                detailGuid,
                dto.Action
            );
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPut("{invoiceGuid}/details/batch-action")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> BatchUpdateDetailAction(
            [FromRoute] string invoiceGuid,
            [FromBody] BatchUpdateDetailActionRequest dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();
            // 批量动作最终可能触发全启用分店写入，入口必须具备全店访问能力。
            if (!await CanAccessAllEnabledStoresAsync())
                return Forbid();

            var result = await _service.BatchUpdateDetailActionAsync(invoiceGuid, dto);
            if (result.Success)
                return Ok(new { success = true, data = result.Data });
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpDelete("{invoiceGuid}/details")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> DeleteDetails(
            [FromRoute] string invoiceGuid,
            [FromBody] List<string> detailGuids
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var user = User.Identity?.Name ?? "system";
            var result = await _service.DeleteDetailsAsync(invoiceGuid, detailGuids, user);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpGet("{invoiceGuid}/barcode-abnormal-details")]
        public async Task<IActionResult> GetBarcodeAbnormalDetails([FromRoute] string invoiceGuid)
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetBarcodeAbnormalDetailsAsync(invoiceGuid);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpGet("{invoiceGuid}/products-by-barcode")]
        public async Task<IActionResult> GetProductsByBarcode(
            [FromRoute] string invoiceGuid,
            [FromQuery] string barcode
        )
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetProductsByBarcodeAsync(invoiceGuid, barcode);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpGet("{invoiceGuid}/products-by-product-code")]
        public async Task<IActionResult> GetProductsByProductCode(
            [FromRoute] string invoiceGuid,
            [FromQuery] string productCode
        )
        {
            if (!await HasLocalSupplierInvoiceReadPermissionAsync())
                return Forbid();

            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();

            var result = await _service.GetProductsByProductCodeAsync(invoiceGuid, productCode);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("check-invoice-no")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> CheckInvoiceNoExists([FromBody] CheckInvoiceNoExistsRequest dto)
        {
            if (!await CanAccessStoreAsync(dto.StoreCode))
                return Forbid();

            var result = await _service.CheckInvoiceNoExistsAsync(dto.StoreCode, dto.SupplierCode, dto.InvoiceNo);
            if (result.Success)
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message,
                    }
                );
            return BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost("{invoiceGuid}/details/batch-execute")]
        [Authorize(Policy = Permissions.LocalPurchase.Edit)]
        public async Task<IActionResult> BatchExecuteActions(
            [FromRoute] string invoiceGuid,
            [FromBody] BatchExecuteActionsRequestDto dto
        )
        {
            if (!await CanAccessInvoiceAsync(invoiceGuid))
                return Forbid();
            // 新建商品/多码执行会影响所有启用分店，不能只按当前进货单分店授权。
            if (!await CanAccessAllEnabledStoresAsync())
                return Forbid();

            var confirmationValidation = await ValidateBatchExecuteConfirmationAsync(invoiceGuid, dto);
            if (confirmationValidation.Error != null)
            {
                return BadRequest(
                    new
                    {
                        success = false,
                        message = confirmationValidation.Error.Message,
                        code = confirmationValidation.Error.Code,
                        details = confirmationValidation.Error.Details,
                    }
                );
            }

            var user = User.Identity?.Name ?? "system";
            var result = await _service.BatchExecuteActionsAsync(
                invoiceGuid,
                dto.DetailGuids,
                user,
                dto.NewProductProductTypeSelections,
                dto.ExpectedActions,
                confirmationValidation.ConfirmedDetails,
                dto.ConfirmedLargePriceChange == true
            );
            if (result.Success)
                return Ok(new { success = true, data = result.Data, message = result.Message });
            var error = new
            {
                success = false,
                message = result.Message,
                code = result.Code,
                details = result.Details,
            };
            return result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                ? StatusCode(StatusCodes.Status409Conflict, error)
                : BadRequest(error);
        }

        [HttpPost("push-to-hq")]
        [Authorize(Policy = Permissions.LocalPurchase.PushToHq)]
        public async Task<IActionResult> PushInvoicesToHq([FromBody] PushToHqRequest request)
        {
            try
            {
                if (request.InvoiceGuids == null || !request.InvoiceGuids.Any())
                    return BadRequest(new { success = false, message = "请选择要推送的进货单" });
                foreach (var invoiceGuid in request.InvoiceGuids)
                {
                    if (!await CanAccessInvoiceAsync(invoiceGuid))
                        return Forbid();
                }

                var result = await _service.PushInvoicesToHqAsync(request.InvoiceGuids);
                if (result.IsSuccess)
                    return Ok(ApiResponse<SyncResult>.OK(result, result.Message));
                return Ok(ApiResponse<SyncResult>.OK(result, result.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<SyncResult>.Error($"推送异常: {ex.Message}", "INTERNAL_ERROR"));
            }
        }

        // 2026-09-29 起停用：HQ 进货单主表/详情 → 本地 StoreLocalSupplierInvoice 属于 HQ → HBweb 方向，统一返回 410；
        // 上方 push-to-hq（本地 → HQ）保持可用。
        [HttpPost("sync-from-hq")]
        [Authorize(Roles = "Admin,管理员")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncFromHq([FromBody] LocalSupplierInvoiceHqSyncRequest? request)
        {
            request ??= new LocalSupplierInvoiceHqSyncRequest();

            if (request.EndDate.HasValue
                && request.StartDate.HasValue
                && request.EndDate.Value < request.StartDate.Value)
            {
                return BadRequest(
                    ApiResponse<LocalSupplierInvoiceHqSyncResult>.Error(
                        "结束日期不能早于起始日期",
                        "INVALID_DATE_RANGE"
                    )
                );
            }

            var result = await _hqSyncService.SyncForPageAsync(
                request.SelectedStoreCodes,
                request.StartDate,
                request.EndDate
            );
            return result.Success ? Ok(result) : BadRequest(result);
        }
    }
}
