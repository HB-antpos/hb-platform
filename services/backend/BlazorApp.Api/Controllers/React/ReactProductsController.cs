using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/products")]
    [Authorize]
    public class ReactProductsController : ControllerBase
    {
        private readonly SqlSugarContext _context;
        private readonly ILogger<ReactProductsController> _logger;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IProductMaintenanceHqProjectionWriter _hqProjectionWriter;

        public ReactProductsController(
            SqlSugarContext context,
            ILogger<ReactProductsController> logger,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService,
            IProductMaintenanceHqProjectionWriter hqProjectionWriter
        )
        {
            _context = context;
            _logger = logger;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
            _hqProjectionWriter = hqProjectionWriter;
        }

        private static string NormalizeLocalSupplierCode(string? value)
        {
            // 未选择本地供应商时统一归到默认供应商 200，保证商品和分店价格供应商一致。
            return string.IsNullOrWhiteSpace(value) ? "200" : value.Trim();
        }

        [HttpPost("generate-local-barcode")]
        [Authorize(Policy = Permissions.StoreProducts.Create)]
        public async Task<IActionResult> GenerateLocalBarcode(
            [FromQuery] string supplierCode,
            [FromServices] ItemBarcodeService barcodeService)
        {
            if (string.IsNullOrWhiteSpace(supplierCode) || supplierCode.Trim() == "200")
                return BadRequest(new { success = false, message = "此供应商商品不能自行创建，请联系管理员" });

            try
            {
                var barcode = await barcodeService.GenerateLocalProductBarcodeAsync();
                return Ok(new { barcode });
            }
            catch (InvalidOperationException exception)
            {
                return Conflict(new { success = false, message = exception.Message });
            }
        }

        /// <summary>
        /// 创建商品并为所有启用分店初始化分店价格
        /// 不联动零售价更新逻辑
        /// </summary>
        [HttpPost("create-with-prices")]
        [Authorize(Policy = Permissions.StoreProducts.Create)]
        public async Task<IActionResult> CreateWithPrices([FromBody] CreateProductWithPricesDto dto)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dto.ProductName))
                {
                    return BadRequest(new { success = false, message = "商品名称不能为空" });
                }
                if (dto.IsAutoPricing == false)
                {
                    if (!dto.RetailPrice.HasValue || dto.RetailPrice.Value <= 0)
                    {
                        return BadRequest(
                            new { success = false, message = "关闭自动定价时必须提供有效零售价" }
                        );
                    }
                }

                var db = _context.Db;
                var now = DateTime.UtcNow;
                var localSupplierCode = NormalizeLocalSupplierCode(dto.LocalSupplierCode);
                await db.Ado.BeginTranAsync();
                try
                {
                    if (dto.Barcode?.Trim().StartsWith("9529", StringComparison.Ordinal) == true)
                    {
                        dto.Barcode = dto.Barcode.Trim();
                        // 自动条码提交时再次检查占用；同一条码的并发创建必须串行，避免重复提交生成两件商品。
                        if (db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                        {
                            await db.Ado.ExecuteCommandAsync("""
DECLARE @result int;
EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
    @LockOwner = 'Transaction', @LockTimeout = 10000;
IF @result < 0 THROW 51062, '获取本地商品条码创建锁失败', 1;
""", new SugarParameter("@resource", $"LocalProductBarcodeCreate:{dto.Barcode}"));
                        }
                        if (await db.Queryable<Product>().AnyAsync(x => x.Barcode == dto.Barcode)
                            || await db.Queryable<ProductSetCode>().AnyAsync(x => x.SetBarcode == dto.Barcode)
                            || await db.Queryable<StoreMultiCodeProduct>().AnyAsync(x => x.MultiBarcode == dto.Barcode)
                            || await db.Queryable<StoreClearancePrice>().AnyAsync(x => x.ClearanceBarcode == dto.Barcode)
                            || await db.Queryable<DomesticProduct>().AnyAsync(x => x.Barcode == dto.Barcode)
                            || await db.Queryable<DomesticSetProduct>().AnyAsync(x => x.SetBarcode == dto.Barcode))
                        {
                            await db.Ado.RollbackTranAsync();
                            return Conflict(new { success = false, message = "条码已被使用，请重新生成条码" });
                        }
                    }
                    // 货号必须在存活商品里唯一：此前这里不校验，已经建出和别的商品货号相同的不同商品。
                    // 空货号保持原行为（不校验）；非空时去掉首尾空白后再比较和保存。
                    var itemNumber = string.IsNullOrWhiteSpace(dto.ItemNumber)
                        ? dto.ItemNumber
                        : dto.ItemNumber.Trim();
                    if (!string.IsNullOrWhiteSpace(itemNumber))
                    {
                        // 同一货号的并发创建必须串行，避免两个请求同时通过检查后各建一件。
                        if (db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                        {
                            await db.Ado.ExecuteCommandAsync("""
DECLARE @result int;
EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
    @LockOwner = 'Transaction', @LockTimeout = 10000;
IF @result < 0 THROW 51063, '获取本地商品货号创建锁失败', 1;
""", new SugarParameter("@resource", $"LocalProductItemNumberCreate:{itemNumber}"));
                        }
                        if (await db.Queryable<Product>().AnyAsync(x => x.ItemNumber == itemNumber && x.IsDeleted == false))
                        {
                            await db.Ado.RollbackTranAsync();
                            return Conflict(new
                            {
                                success = false,
                                message = "货号已存在，请更换货号",
                                errorCode = "ITEM_NUMBER_EXISTS",
                            });
                        }
                    }
                    var product = new Product
                    {
                        UUID = UuidHelper.GenerateUuid7(),
                        ProductCode = Guid.NewGuid().ToString(),
                        ProductCategoryGUID = dto.ProductCategoryGUID,
                        LocalSupplierCode = localSupplierCode,
                        ItemNumber = itemNumber,
                        Barcode = dto.Barcode,
                        ProductName = dto.ProductName,
                        ProductImage = dto.ProductImage,
                        ProductType = dto.ProductType ?? 0,
                        PurchasePrice = dto.PurchasePrice,
                        RetailPrice = dto.RetailPrice,
                        IsAutoPricing = dto.IsAutoPricing,
                        IsSpecialProduct = dto.IsSpecialProduct,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CreatedBy = User.Identity?.Name ?? "system",
                        UpdatedBy = User.Identity?.Name ?? "system",
                        IsDeleted = false,
                    };

                    // 创建前后快照和历史插入必须复用商品创建事务，历史失败时不可留下半成品商品。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { product.ProductCode! }
                    );
                    await db.Insertable(product).ExecuteCommandAsync();

                    var stores = await db.Queryable<Store>()
                        .Where(s => s.IsDeleted == false && s.IsActive == true)
                        .Select(s => new { s.StoreCode })
                        .ToListAsync();

                    var storeProductCodes = new Dictionary<string, string>();
                    var toInsert = new List<StoreRetailPrice>();
                    foreach (var s in stores)
                    {
                        var spCode = UuidHelper.GenerateUuid7();
                        storeProductCodes[s.StoreCode!] = spCode;
                        var srp = new StoreRetailPrice
                        {
                            UUID = UuidHelper.GenerateUuid7(),
                            StoreCode = s.StoreCode,
                            ProductCode = product.ProductCode,
                            StoreProductCode = spCode,
                            SupplierCode = localSupplierCode,
                            PurchasePrice = dto.PurchasePrice,
                            StoreRetailPriceValue = dto.RetailPrice, // 初始零售价（可为空），不随更新进货价联动
                            DiscountRate = null,
                            IsActive = true,
                            IsAutoPricing = dto.IsAutoPricing,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = User.Identity?.Name ?? "system",
                            UpdatedBy = User.Identity?.Name ?? "system",
                            IsDeleted = false,
                        };
                        toInsert.Add(srp);
                    }

                    if (toInsert.Any())
                    {
                        await db.Insertable(toInsert).ExecuteCommandAsync();
                    }

                    var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { product.ProductCode! }
                    );
                    var actorName = _currentUserService.GetCurrentUsername();
                    if (string.IsNullOrWhiteSpace(actorName))
                    {
                        actorName = "System";
                    }
                    var actorUserGuid = _currentUserService.GetCurrentUserGuid();
                    var isSystemActor = string.IsNullOrWhiteSpace(actorUserGuid)
                        && string.Equals(
                            actorName,
                            "System",
                            StringComparison.OrdinalIgnoreCase
                        );
                    await _changeHistoryService.RecordChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        new WarehouseProductChangeHistoryContextDto
                        {
                            Action = "Create",
                            Source = "ProductLegacyCreateWithPrices",
                            SourceReference = product.ProductCode,
                            ActorUserGuid = string.IsNullOrWhiteSpace(actorUserGuid)
                                ? null
                                : actorUserGuid,
                            ActorName = actorName,
                            ActorType = isSystemActor ? "System" : "User",
                            OccurredAtUtc = now,
                        }
                    );

                    var hqSync = await _hqProjectionWriter.EnqueueAsync(
                        db,
                        new ProductMaintenanceHqMutationRequest
                        {
                            OperationKind = ProductMaintenanceHqOperationKinds.ProductCreated,
                            ProductCode = product.ProductCode!,
                            TargetStoreCodes = null,
                            FieldMask = new[] { ProductMaintenanceHqFieldMasks.All },
                            RequestedByUserGuid = ResolveRequestedByUserGuid(),
                            Source = "react-products.create-with-prices",
                            OccurredAtUtc = now,
                        }
                    );

                    await db.Ado.CommitTranAsync();
                    var result = new CreateProductWithPricesResultDto
                    {
                        ProductCode = product.ProductCode!,
                        StoreProductCodes = storeProductCodes,
                        HqSync = hqSync,
                    };
                    return Ok(new { success = true, data = result });
                }
                catch (Exception exTran)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(exTran, "创建商品及分店价格事务失败");
                    return StatusCode(500, new { success = false, message = "创建失败" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建商品及分店价格失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 仅更新分店进货价，不更新零售价
        /// </summary>
        [HttpPost("update-purchase")]
        [Authorize(Policy = Permissions.StoreProducts.Edit)]
        public async Task<IActionResult> UpdatePurchase([FromBody] UpdatePurchaseRequestDto dto)
        {
            try
            {
                if (
                    string.IsNullOrWhiteSpace(dto.StoreCode)
                    || string.IsNullOrWhiteSpace(dto.ProductCode)
                    || dto.NewPurchasePrice == null
                )
                {
                    return BadRequest(new { success = false, message = "参数不完整" });
                }

                var storeCode = dto.StoreCode.Trim();
                var productCode = dto.ProductCode.Trim();
                var storeAccess = await ResolveStoreAccessAsync();
                if (
                    !storeAccess.IsAllowed
                    || (
                        storeAccess.StoreCodes != null
                        && !storeAccess.StoreCodes.Contains(storeCode, StringComparer.Ordinal)
                    )
                )
                {
                    return Forbid();
                }

                var db = _context.Db;
                await db.Ado.BeginTranAsync();
                try
                {
                    await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        new[] { productCode }
                    );
                    var entity = await db.Queryable<StoreRetailPrice>()
                        .Where(x =>
                            x.StoreCode == storeCode
                            && x.ProductCode == productCode
                            && x.IsDeleted == false
                        )
                        .FirstAsync();
                    if (entity == null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return NotFound(new { success = false, message = "分店价格不存在" });
                    }

                    entity.PurchasePrice = dto.NewPurchasePrice;
                    entity.UpdatedAt = DateTime.UtcNow;
                    entity.UpdatedBy = User.Identity?.Name ?? "system";
                    // 该入口只拥有进货价；字段级更新避免覆盖并发保存的零售价和门店策略。
                    var affected = await db.Updateable(entity)
                        .UpdateColumns(item => new
                        {
                            item.PurchasePrice,
                            item.UpdatedAt,
                            item.UpdatedBy,
                        })
                        .ExecuteCommandAsync();
                    if (affected == 0)
                    {
                        throw new InvalidOperationException("分店价格更新未写入任何记录");
                    }

                    var hqSync = await _hqProjectionWriter.EnqueueAsync(
                        db,
                        new ProductMaintenanceHqMutationRequest
                        {
                            OperationKind = ProductMaintenanceHqOperationKinds.StorePriceUpdated,
                            ProductCode = productCode,
                            TargetStoreCodes = new[] { storeCode },
                            AuthorizedStoreCodes = storeAccess.StoreCodes,
                            FieldMask = ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode,
                            RequestedByUserGuid = ResolveRequestedByUserGuid(),
                            Source = "react-products.update-purchase",
                            OccurredAtUtc = entity.UpdatedAt ?? DateTime.UtcNow,
                        }
                    );
                    await db.Ado.CommitTranAsync();

                    return Ok(
                        new
                        {
                            success = true,
                            data = new
                            {
                                currentPurchasePrice = entity.PurchasePrice,
                                hqSync,
                            },
                        }
                    );
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店进货价失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                {
                    return Conflict(new
                    {
                        success = false,
                        errorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                        message = "商品价格正在被其他操作更新，请稍后重试",
                    });
                }
                return StatusCode(500, new { success = false, message = "更新失败" });
            }
        }

        private string? ResolveRequestedByUserGuid() =>
            _currentUserService.GetCurrentUserGuid()
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        private async Task<StoreAccessScope> ResolveStoreAccessAsync()
        {
            if (HasElevatedStoreAccess())
            {
                return new StoreAccessScope(true, null);
            }

            var userGuid = ResolveRequestedByUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                var username = User.Identity?.Name;
                if (!string.IsNullOrWhiteSpace(username))
                {
                    userGuid = await _context.Db.Queryable<User>()
                        .Where(item => item.Username == username && !item.IsDeleted)
                        .Select(item => item.UserGUID)
                        .FirstAsync();
                }
            }

            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return new StoreAccessScope(false, Array.Empty<string>());
            }

            var storeCodes = await _context.Db.Queryable<UserStore>()
                .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                .Where((userStore, store) =>
                    userStore.UserGUID == userGuid
                    && !userStore.IsDeleted
                    && !store.IsDeleted
                )
                .Select((userStore, store) => store.StoreCode)
                .ToListAsync();
            var normalizedStoreCodes = storeCodes
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new StoreAccessScope(true, normalizedStoreCodes);
        }

        private bool HasElevatedStoreAccess() =>
            HasSuperAdminRole()
            || HasRole("Manager")
            || HasRole("WarehouseManager")
            || HasRole("WarehouseStaff");

        private bool HasSuperAdminRole() =>
            User.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role && Permissions.IsSuperAdminRole(claim.Value)
            );

        private bool HasRole(string role) =>
            User.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && claim.Value.Equals(role, StringComparison.OrdinalIgnoreCase)
            );

        private sealed record StoreAccessScope(
            bool IsAllowed,
            IReadOnlyCollection<string>? StoreCodes
        );
    }

    public class UpdatePurchaseRequestDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public decimal? NewPurchasePrice { get; set; }
    }
}
