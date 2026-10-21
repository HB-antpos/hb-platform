using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public class PDAWarehouseOrderService : IPDAWarehouseOrderService
    {
        private readonly SqlSugarContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<PDAWarehouseOrderService> _logger;
        private readonly IWarehouseProductService _warehouseProductService;
        private readonly IOrderNumberGenerator _orderNumberGenerator;

        public PDAWarehouseOrderService(
            SqlSugarContext context,
            IMapper mapper,
            ILogger<PDAWarehouseOrderService> logger,
            IWarehouseProductService warehouseProductService,
            IOrderNumberGenerator orderNumberGenerator
        )
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
            _warehouseProductService = warehouseProductService;
            _orderNumberGenerator = orderNumberGenerator;
        }

        private ISqlSugarClient Db => _context.Db;

        #region 订单查询

        public async Task<PDAWarehouseOrderListResponseDto> GetOrderListAsync(
            PDAWarehouseOrderFilterDto filter,
            string storeCode
        )
        {
            try
            {
                var db = _context.Db;
                var queryable = db.Queryable<WareHouseOrder>();

                if (!string.IsNullOrEmpty(storeCode))
                {
                    queryable = queryable.Where(o => o.StoreCode == storeCode);
                }

                if (filter.FlowStatus.HasValue)
                {
                    queryable = queryable.Where(o => o.FlowStatus == filter.FlowStatus.Value);
                }

                if (filter.InboundStatus.HasValue)
                {
                    queryable = queryable.Where(o => o.InboundStatus == filter.InboundStatus.Value);
                }

                if (filter.StartDate.HasValue)
                {
                    queryable = queryable.Where(o => o.OrderDate >= filter.StartDate.Value);
                }

                if (filter.EndDate.HasValue)
                {
                    queryable = queryable.Where(o => o.OrderDate <= filter.EndDate.Value);
                }

                if (!string.IsNullOrEmpty(filter.Keyword))
                {
                    var keyword = filter.Keyword.Trim();
                    queryable = queryable.Where(o =>
                        (o.OrderNo != null && o.OrderNo.Contains(keyword))
                        || (o.Remarks != null && o.Remarks.Contains(keyword))
                    );
                }

                var totalCount = await queryable.CountAsync();

                var orders = await queryable
                    .OrderByDescending(o => o.OrderDate)
                    .Skip((filter.PageNumber - 1) * filter.PageSize)
                    .Take(filter.PageSize)
                    .ToListAsync();

                var orderDtos = new List<PDAWarehouseOrderDto>();
                foreach (var order in orders)
                {
                    var dto = _mapper.Map<PDAWarehouseOrderDto>(order);
                    dto.FlowStatusText = GetFlowStatusText(order.FlowStatus);
                    dto.InboundStatusText = GetInboundStatusText(order.InboundStatus);

                    var details = await db.Queryable<WareHouseOrderDetails>()
                        .LeftJoin<Product>((d, p) => d.ProductCode == p.ProductCode)
                        .Where(d => d.OrderGUID == order.OrderGUID)
                        .Select(
                            (d, p) =>
                                new PDAWarehouseOrderDetailDto
                                {
                                    DetailGUID = d.DetailGUID,
                                    OrderGUID = d.OrderGUID,
                                    StoreCode = d.StoreCode,
                                    StoreProductCode = d.StoreProductCode,
                                    ProductCode = d.ProductCode,
                                    ItemNumber = p.ItemNumber,
                                    ProductName = p.ProductName,
                                    ProductImage = p.ProductImage,
                                    Barcode = p.Barcode,
                                    Quantity = d.Quantity,
                                    AllocQuantity = d.AllocQuantity,
                                    LastCost = d.LastCost,
                                    ImportPrice = d.ImportPrice,
                                    ImportAmount = d.ImportAmount,
                                    OEMPrice = d.OEMPrice,
                                    OEMAmount = d.OEMAmount,
                                }
                        )
                        .ToListAsync();

                    var productCodes = details
                        .Select(d => d.ProductCode)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (productCodes.Any())
                    {
                        var warehouseProducts = await db.Queryable<WarehouseProduct>()
                            .Where(wp => productCodes.Contains(wp.ProductCode))
                            .ToListAsync();

                        var productDict = warehouseProducts.ToDictionary(wp => wp.ProductCode);

                        foreach (var detail in details)
                        {
                            if (
                                detail.ProductCode != null
                                && productDict.TryGetValue(detail.ProductCode, out var wp)
                            )
                            {
                                detail.StockQuantity = wp.StockQuantity;
                                detail.MinOrderQuantity = wp.MinOrderQuantity;
                            }
                        }
                    }

                    dto.OrderDetails = details;
                    orderDtos.Add(dto);
                }

                return new PDAWarehouseOrderListResponseDto
                {
                    Orders = orderDtos,
                    TotalCount = totalCount,
                    PageNumber = filter.PageNumber,
                    PageSize = filter.PageSize,
                    TotalPages = (int)Math.Ceiling((double)totalCount / filter.PageSize),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取PDA仓库订单列表失败");
                throw;
            }
        }

        public async Task<PDAWarehouseOrderDto?> GetOrderDetailAsync(
            string orderGuid,
            string storeCode
        )
        {
            try
            {
                var db = _context.Db;
                var order = await db.Queryable<WareHouseOrder>()
                    .Where(o => o.OrderGUID == orderGuid)
                    .FirstAsync();

                if (order == null)
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(storeCode) && order.StoreCode != storeCode)
                {
                    _logger.LogWarning(
                        "订单 {OrderGuid} 不属于分店 {StoreCode}",
                        orderGuid,
                        storeCode
                    );
                    return null;
                }

                var dto = _mapper.Map<PDAWarehouseOrderDto>(order);
                dto.FlowStatusText = GetFlowStatusText(order.FlowStatus);
                dto.InboundStatusText = GetInboundStatusText(order.InboundStatus);

                var details = await db.Queryable<WareHouseOrderDetails>()
                    .LeftJoin<Product>((d, p) => d.ProductCode == p.ProductCode)
                    .Where(d => d.OrderGUID == order.OrderGUID)
                    .Select(
                        (d, p) =>
                            new PDAWarehouseOrderDetailDto
                            {
                                DetailGUID = d.DetailGUID,
                                OrderGUID = d.OrderGUID,
                                StoreCode = d.StoreCode,
                                StoreProductCode = d.StoreProductCode,
                                ProductCode = d.ProductCode,
                                ItemNumber = p.ItemNumber,
                                ProductName = p.ProductName,
                                ProductImage = p.ProductImage,
                                Barcode = p.Barcode,
                                Quantity = d.Quantity,
                                AllocQuantity = d.AllocQuantity,
                                LastCost = d.LastCost,
                                ImportPrice = d.ImportPrice,
                                ImportAmount = d.ImportAmount,
                                OEMPrice = d.OEMPrice,
                                OEMAmount = d.OEMAmount,
                            }
                    )
                    .ToListAsync();

                var productCodes = details
                    .Select(d => d.ProductCode)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
                if (productCodes.Any())
                {
                    var warehouseProducts = await db.Queryable<WarehouseProduct>()
                        .Where(wp => productCodes.Contains(wp.ProductCode))
                        .ToListAsync();

                    var productDict = warehouseProducts.ToDictionary(wp => wp.ProductCode);

                    foreach (var detail in details)
                    {
                        if (
                            detail.ProductCode != null
                            && productDict.TryGetValue(detail.ProductCode, out var wp)
                        )
                        {
                            detail.StockQuantity = wp.StockQuantity;
                            detail.MinOrderQuantity = wp.MinOrderQuantity;
                        }
                    }
                }

                dto.OrderDetails = details;
                return dto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取PDA仓库订单详情失败，订单GUID: {OrderGuid}", orderGuid);
                throw;
            }
        }

        #endregion

        #region 订单操作

        public async Task<PDAWarehouseOrderResponseDto> CreateOrderAsync(
            CreatePDAWarehouseOrderRequestDto request,
            string deviceHardwareId
        )
        {
            try
            {
                var db = _context.Db;

                var existingOrder = await db.Queryable<WareHouseOrder>()
                    .Where(o => o.StoreCode == request.StoreCode && o.FlowStatus == 0)
                    .FirstAsync();

                if (existingOrder != null)
                {
                    _logger.LogInformation(
                        "分店 {StoreCode} 已存在草稿订单 {OrderGuid}",
                        request.StoreCode,
                        existingOrder.OrderGUID
                    );
                    return new PDAWarehouseOrderResponseDto
                    {
                        Success = true,
                        OrderGUID = existingOrder.OrderGUID,
                        OrderNo = existingOrder.OrderNo,
                        Message = "分店已存在草稿订单，请使用现有订单",
                    };
                }

                var order = new WareHouseOrder
                {
                    OrderGUID = Guid.NewGuid().ToString("N"),
                    StoreCode = request.StoreCode,
                    OrderNo = await _orderNumberGenerator.GetNextOrderNoAsync(),
                    OrderDate = request.OrderDate ?? DateTime.Now,
                    FlowStatus = 0,
                    InboundStatus = 0,
                    Remarks = request.Remarks,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                };

                await db.Insertable(order).ExecuteCommandAsync();

                _logger.LogInformation("创建PDA仓库订单成功，订单号: {OrderNo}", order.OrderNo);

                return new PDAWarehouseOrderResponseDto
                {
                    Success = true,
                    OrderGUID = order.OrderGUID,
                    OrderNo = order.OrderNo,
                    Message = "订单创建成功",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建PDA仓库订单失败");
                return new PDAWarehouseOrderResponseDto
                {
                    Success = false,
                    Message = "订单创建失败: " + ex.Message,
                };
            }
        }

        public async Task<PDAWarehouseOrderResponseDto> UpdateOrderAsync(
            UpdatePDAWarehouseOrderRequestDto request,
            string storeCode,
            string deviceHardwareId
        )
        {
            try
            {
                var db = _context.Db;

                var order = await db.Queryable<WareHouseOrder>()
                    .Where(o => o.OrderGUID == request.OrderGUID)
                    .FirstAsync();

                if (order == null)
                {
                    return new PDAWarehouseOrderResponseDto
                    {
                        Success = false,
                        Message = "订单不存在",
                    };
                }

                if (!string.IsNullOrEmpty(storeCode) && order.StoreCode != storeCode)
                {
                    return new PDAWarehouseOrderResponseDto
                    {
                        Success = false,
                        Message = "无权修改该订单",
                    };
                }

                if (order.FlowStatus != 0)
                {
                    return new PDAWarehouseOrderResponseDto
                    {
                        Success = false,
                        Message = "只能修改草稿状态的订单",
                    };
                }

                if (request.OrderDate.HasValue)
                    order.OrderDate = request.OrderDate.Value;

                if (request.Remarks != null)
                    order.Remarks = request.Remarks;

                if (request.ShippingFee.HasValue)
                    order.ShippingFee = request.ShippingFee.Value;

                order.UpdatedAt = DateTime.Now;

                await db.Updateable(order).ExecuteCommandAsync();

                _logger.LogInformation(
                    "更新PDA仓库订单成功，订单GUID: {OrderGuid}",
                    request.OrderGUID
                );

                return new PDAWarehouseOrderResponseDto
                {
                    Success = true,
                    OrderGUID = order.OrderGUID,
                    OrderNo = order.OrderNo,
                    Message = "订单更新成功",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新PDA仓库订单失败");
                return new PDAWarehouseOrderResponseDto
                {
                    Success = false,
                    Message = "订单更新失败: " + ex.Message,
                };
            }
        }

        public async Task<PDAWarehouseOrderResponseDto> SubmitOrderAsync(
            SubmitPDAWarehouseOrderRequestDto request,
            string storeCode,
            string deviceHardwareId
        )
        {
            try
            {
                var db = _context.Db;
                var storeResource = await PreorderGateEvaluator
                    .ResolveStoreLockResourceForOrdinaryOrderWriteAsync(
                        db,
                        storeCode,
                        "PDA.SubmitOrder",
                        _logger
                    );
                await using IAsyncDisposable? storeLock = storeResource == null
                    ? null
                    : await PreorderMutationLock.AcquireProcessAsync(storeResource);
                await db.Ado.BeginTranAsync();

                try
                {
                    var databaseGateLockHeld = false;
                    if (storeResource != null)
                    {
                        var canonicalStoreGuid = PreorderGateEvaluator
                            .GetCanonicalStoreGuidFromLockResource(storeResource);
                        databaseGateLockHeld = await PreorderGateEvaluator
                            .AcquireDatabaseLockForOrdinaryOrderWriteAsync(
                                db,
                                storeResource,
                                canonicalStoreGuid,
                                storeCode,
                                "PDA.SubmitOrder",
                                _logger
                            );
                    }
                    var order = await db.Queryable<WareHouseOrder>()
                        .Where(o => o.OrderGUID == request.OrderGUID)
                        .FirstAsync();

                    if (order == null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            Message = "订单不存在",
                        };
                    }

                    if (!string.Equals(
                        order.StoreCode?.Trim(),
                        storeCode.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    ))
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            ErrorCode = "PDA_ORDER_STORE_MISMATCH",
                            Message = "订单不属于当前设备绑定分店",
                        };
                    }

                    // 关键逻辑：先验证订单归属；仅门禁本身不可用时放行，其他订单校验仍照常执行。
                    var gate = databaseGateLockHeld
                        ? await PreorderGateEvaluator
                            .EvaluateWithHeldStoreGateForOrdinaryOrderWriteAsync(
                                db,
                                storeResource!,
                                storeCode,
                                TimeProvider.System,
                                "PDA.SubmitOrder",
                                _logger
                            )
                        : null;
                    if (gate?.IsBlocked == true)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            ErrorCode = "PREORDER_REQUIRED",
                            Message = "请先完成当前有效的 Preorder，再提交普通订货",
                        };
                    }

                    if (order.FlowStatus != 0)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            Message = "只能提交草稿状态的订单",
                        };
                    }

                    var details = await db.Queryable<WareHouseOrderDetails>()
                        .Where(d => d.OrderGUID == order.OrderGUID)
                        .ToListAsync();

                    if (!details.Any())
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            Message = "订单中没有商品，无法提交",
                        };
                    }

                    order.FlowStatus = 1;
                    order.UpdatedAt = DateTime.Now;

                    if (!string.IsNullOrEmpty(request.Remarks))
                        order.Remarks = request.Remarks;

                    await db.Updateable(order).ExecuteCommandAsync();

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation("提交PDA仓库订单成功，订单号: {OrderNo}", order.OrderNo);

                    return new PDAWarehouseOrderResponseDto
                    {
                        Success = true,
                        OrderGUID = order.OrderGUID,
                        OrderNo = order.OrderNo,
                        Message = "订单提交成功",
                    };
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (PreorderBusinessException ex)
            {
                _logger.LogWarning(ex, "PDA 提交订单时无法确认 Preorder 门禁");
                return new PDAWarehouseOrderResponseDto
                {
                    Success = false,
                    ErrorCode = ex.ErrorCode,
                    Message = ex.Message,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提交PDA仓库订单失败");
                return new PDAWarehouseOrderResponseDto
                {
                    Success = false,
                    Message = "订单提交失败，请稍后重试",
                };
            }
        }

        public async Task<PDAWarehouseOrderResponseDto> DeleteOrderAsync(
            string orderGuid,
            string storeCode,
            string deviceHardwareId
        )
        {
            try
            {
                var db = _context.Db;
                await db.Ado.BeginTranAsync();

                try
                {
                    var order = await db.Queryable<WareHouseOrder>()
                        .Where(o => o.OrderGUID == orderGuid)
                        .FirstAsync();

                    if (order == null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            Message = "订单不存在",
                        };
                    }

                    if (!string.IsNullOrEmpty(storeCode) && order.StoreCode != storeCode)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            Message = "无权删除该订单",
                        };
                    }

                    if (order.FlowStatus != 0)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderResponseDto
                        {
                            Success = false,
                            Message = "只能删除草稿状态的订单",
                        };
                    }

                    await db.Deleteable<WareHouseOrderDetails>()
                        .Where(d => d.OrderGUID == orderGuid)
                        .ExecuteCommandAsync();

                    await db.Deleteable<WareHouseOrder>()
                        .Where(o => o.OrderGUID == orderGuid)
                        .ExecuteCommandAsync();

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation("删除PDA仓库订单成功，订单GUID: {OrderGuid}", orderGuid);

                    return new PDAWarehouseOrderResponseDto
                    {
                        Success = true,
                        Message = "订单删除成功",
                    };
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除PDA仓库订单失败");
                return new PDAWarehouseOrderResponseDto
                {
                    Success = false,
                    Message = "订单删除失败: " + ex.Message,
                };
            }
        }

        #endregion

        #region 订单明细操作

        public async Task<PDAWarehouseOrderDetailResponseDto> AddOrderLineAsync(
            AddPDAWarehouseOrderLineRequestDto request,
            string storeCode
        )
        {
            try
            {
                var db = _context.Db;

                var order = await db.Queryable<WareHouseOrder>()
                    .Where(o => o.OrderGUID == request.OrderGUID)
                    .FirstAsync();

                if (order == null)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "订单不存在",
                    };
                }

                if (!string.IsNullOrEmpty(storeCode) && order.StoreCode != storeCode)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "无权修改该订单",
                    };
                }

                if (order.FlowStatus != 0)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "只能修改草稿状态的订单",
                    };
                }

                var existingDetail = await db.Queryable<WareHouseOrderDetails>()
                    .Where(d =>
                        d.OrderGUID == request.OrderGUID && d.ProductCode == request.ProductCode
                    )
                    .FirstAsync();

                if (existingDetail != null)
                {
                    // 退货行（发货数量为负）只能在后台订货单里由有权限的人处理，PDA 的累加 / 覆盖会悄悄抹掉退货。
                    if ((existingDetail.AllocQuantity ?? 0) < 0)
                    {
                        return new PDAWarehouseOrderDetailResponseDto
                        {
                            Success = false,
                            Message = "该商品在本单是退货行（负数发货），PDA 不能修改，请在后台订货单中处理",
                        };
                    }

                    existingDetail.Quantity += request.Quantity;
                    existingDetail.AllocQuantity =
                        request.AllocQuantity ?? existingDetail.AllocQuantity;
                    existingDetail.ImportAmount =
                        existingDetail.Quantity * (existingDetail.ImportPrice ?? 0);
                    existingDetail.OEMAmount =
                        existingDetail.Quantity * (existingDetail.OEMPrice ?? 0);

                    await db.Updateable(existingDetail).ExecuteCommandAsync();

                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = true,
                        DetailGUID = existingDetail.DetailGUID,
                        Message = "商品数量已累加",
                    };
                }

                var warehouseProduct = await db.Queryable<WarehouseProduct>()
                    .LeftJoin<Product>((wp, p) => wp.ProductCode == p.ProductCode)
                    .Where(wp => wp.ProductCode == request.ProductCode)
                    .Select((wp, p) => new { wp, p })
                    .FirstAsync();

                if (warehouseProduct == null)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "商品不存在",
                    };
                }

                var detail = new WareHouseOrderDetails
                {
                    DetailGUID = Guid.NewGuid().ToString("N"),
                    OrderGUID = request.OrderGUID,
                    StoreCode = order.StoreCode,
                    ProductCode = request.ProductCode,
                    Quantity = request.Quantity,
                    AllocQuantity = request.AllocQuantity ?? request.Quantity,
                    LastCost =
                        warehouseProduct.wp.StockValue
                        / (
                            warehouseProduct.wp.StockQuantity > 0
                                ? warehouseProduct.wp.StockQuantity
                                : 1
                        ),
                    ImportPrice = warehouseProduct.wp.ImportPrice,
                    OEMPrice = warehouseProduct.wp.OEMPrice,
                    ImportAmount = request.Quantity * (warehouseProduct.wp.ImportPrice ?? 0),
                    OEMAmount = request.Quantity * (warehouseProduct.wp.OEMPrice ?? 0),
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                };

                await db.Insertable(detail).ExecuteCommandAsync();

                await RecalculateOrderTotalsAsync(request.OrderGUID);

                return new PDAWarehouseOrderDetailResponseDto
                {
                    Success = true,
                    DetailGUID = detail.DetailGUID,
                    Message = "商品添加成功",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加PDA仓库订单明细失败");
                return new PDAWarehouseOrderDetailResponseDto
                {
                    Success = false,
                    Message = "商品添加失败: " + ex.Message,
                };
            }
        }

        public async Task<PDAWarehouseOrderDetailResponseDto> UpdateOrderLineAsync(
            UpdatePDAWarehouseOrderLineRequestDto request,
            string storeCode
        )
        {
            try
            {
                var db = _context.Db;

                var detail = await db.Queryable<WareHouseOrderDetails>()
                    .FirstAsync(d => d.DetailGUID == request.DetailGUID);

                if (detail == null)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "订单明细不存在",
                    };
                }

                var order = await db.Queryable<WareHouseOrder>()
                    .FirstAsync(o => o.OrderGUID == detail.OrderGUID);

                if (order == null)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "订单不存在",
                    };
                }

                if (!string.IsNullOrEmpty(storeCode) && order.StoreCode != storeCode)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "无权修改该订单",
                    };
                }

                if (order.FlowStatus != 0)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "只能修改草稿状态的订单",
                    };
                }

                // 退货行不允许 PDA 改动（同累加入口）。
                if ((detail.AllocQuantity ?? 0) < 0)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "该商品在本单是退货行（负数发货），PDA 不能修改，请在后台订货单中处理",
                    };
                }

                detail.Quantity = request.Quantity;
                detail.AllocQuantity = request.AllocQuantity ?? detail.AllocQuantity;

                if (request.ImportPrice.HasValue)
                    detail.ImportPrice = request.ImportPrice.Value;

                if (request.OEMPrice.HasValue)
                    detail.OEMPrice = request.OEMPrice.Value;

                detail.ImportAmount = detail.Quantity * (detail.ImportPrice ?? 0);
                detail.OEMAmount = detail.Quantity * (detail.OEMPrice ?? 0);
                detail.UpdatedAt = DateTime.Now;

                await db.Updateable(detail).ExecuteCommandAsync();

                if (string.IsNullOrWhiteSpace(detail.OrderGUID))
                {
                    throw new InvalidOperationException("订单明细缺少订单GUID，无法重算订单汇总");
                }

                await RecalculateOrderTotalsAsync(detail.OrderGUID);

                return new PDAWarehouseOrderDetailResponseDto
                {
                    Success = true,
                    DetailGUID = detail.DetailGUID,
                    Message = "订单明细更新成功",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新PDA仓库订单明细失败");
                return new PDAWarehouseOrderDetailResponseDto
                {
                    Success = false,
                    Message = "订单明细更新失败: " + ex.Message,
                };
            }
        }

        public async Task<PDAWarehouseOrderDetailResponseDto> DeleteOrderLineAsync(
            string detailGuid,
            string storeCode
        )
        {
            try
            {
                var db = _context.Db;

                var detail = await db.Queryable<WareHouseOrderDetails>()
                    .FirstAsync(d => d.DetailGUID == detailGuid);

                if (detail == null)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "订单明细不存在",
                    };
                }

                var order = await db.Queryable<WareHouseOrder>()
                    .FirstAsync(o => o.OrderGUID == detail.OrderGUID);

                if (order == null)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "订单不存在",
                    };
                }

                if (!string.IsNullOrEmpty(storeCode) && order.StoreCode != storeCode)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "无权修改该订单",
                    };
                }

                if (order.FlowStatus != 0)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "只能修改草稿状态的订单",
                    };
                }

                // 退货行（发货数量为负）不允许 PDA 硬删，否则无权限者可以抹掉别人录的退货。
                if ((detail.AllocQuantity ?? 0) < 0)
                {
                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = false,
                        Message = "该商品在本单是退货行（负数发货），PDA 不能修改，请在后台订货单中处理",
                    };
                }

                await db.Deleteable<WareHouseOrderDetails>()
                    .Where(d => d.DetailGUID == detailGuid)
                    .ExecuteCommandAsync();

                if (string.IsNullOrWhiteSpace(detail.OrderGUID))
                {
                    throw new InvalidOperationException("订单明细缺少订单GUID，无法重算订单汇总");
                }

                await RecalculateOrderTotalsAsync(detail.OrderGUID);

                return new PDAWarehouseOrderDetailResponseDto
                {
                    Success = true,
                    Message = "订单明细删除成功",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除PDA仓库订单明细失败");
                return new PDAWarehouseOrderDetailResponseDto
                {
                    Success = false,
                    Message = "订单明细删除失败: " + ex.Message,
                };
            }
        }

        public async Task<PDAWarehouseOrderDetailResponseDto> BatchAddOrderLinesAsync(
            BatchAddPDAWarehouseOrderLinesRequestDto request,
            string storeCode
        )
        {
            try
            {
                var db = _context.Db;
                await db.Ado.BeginTranAsync();

                try
                {
                    var order = await db.Queryable<WareHouseOrder>()
                        .Where(o => o.OrderGUID == request.OrderGUID)
                        .FirstAsync();

                    if (order == null)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderDetailResponseDto
                        {
                            Success = false,
                            Message = "订单不存在",
                        };
                    }

                    if (!string.IsNullOrEmpty(storeCode) && order.StoreCode != storeCode)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderDetailResponseDto
                        {
                            Success = false,
                            Message = "无权修改该订单",
                        };
                    }

                    if (order.FlowStatus != 0)
                    {
                        await db.Ado.RollbackTranAsync();
                        return new PDAWarehouseOrderDetailResponseDto
                        {
                            Success = false,
                            Message = "只能修改草稿状态的订单",
                        };
                    }

                    var existingDetails = await db.Queryable<WareHouseOrderDetails>()
                        .Where(d => d.OrderGUID == request.OrderGUID)
                        .ToListAsync();

                    var existingDetailMap = existingDetails.ToDictionary(d => d.ProductCode ?? "");

                    var productCodes = request.Lines.Select(l => l.ProductCode).Distinct().ToList();
                    var warehouseProducts = await db.Queryable<WarehouseProduct>()
                        .LeftJoin<Product>((wp, p) => wp.ProductCode == p.ProductCode)
                        .Where(wp => productCodes.Contains(wp.ProductCode))
                        .Select((wp, p) => new { wp, p })
                        .ToListAsync();

                    var productDict = warehouseProducts.ToDictionary(x => x.wp.ProductCode);

                    var newDetails = new List<WareHouseOrderDetails>();
                    var updateDetails = new List<WareHouseOrderDetails>();

                    foreach (var line in request.Lines)
                    {
                        if (existingDetailMap.TryGetValue(line.ProductCode, out var existingDetail))
                        {
                            // 批量里碰到退货行整批回滚：不能只跳过，否则前端会以为这一行已经加上。
                            if ((existingDetail.AllocQuantity ?? 0) < 0)
                            {
                                await db.Ado.RollbackTranAsync();
                                return new PDAWarehouseOrderDetailResponseDto
                                {
                                    Success = false,
                                    Message = "该商品在本单是退货行（负数发货），PDA 不能修改，请在后台订货单中处理",
                                };
                            }

                            existingDetail.Quantity += line.Quantity;
                            existingDetail.AllocQuantity =
                                line.AllocQuantity ?? existingDetail.AllocQuantity;
                            existingDetail.ImportAmount =
                                existingDetail.Quantity * (existingDetail.ImportPrice ?? 0);
                            existingDetail.OEMAmount =
                                existingDetail.Quantity * (existingDetail.OEMPrice ?? 0);
                            updateDetails.Add(existingDetail);
                        }
                        else if (productDict.TryGetValue(line.ProductCode, out var wp))
                        {
                            var detail = new WareHouseOrderDetails
                            {
                                DetailGUID = Guid.NewGuid().ToString("N"),
                                OrderGUID = request.OrderGUID,
                                StoreCode = order.StoreCode,
                                ProductCode = line.ProductCode,
                                Quantity = line.Quantity,
                                AllocQuantity = line.AllocQuantity ?? line.Quantity,
                                LastCost =
                                    wp.wp.StockValue
                                    / (wp.wp.StockQuantity > 0 ? wp.wp.StockQuantity : 1),
                                ImportPrice = wp.wp.ImportPrice,
                                OEMPrice = wp.wp.OEMPrice,
                                ImportAmount = line.Quantity * (wp.wp.ImportPrice ?? 0),
                                OEMAmount = line.Quantity * (wp.wp.OEMPrice ?? 0),
                                CreatedAt = DateTime.Now,
                                UpdatedAt = DateTime.Now,
                            };
                            newDetails.Add(detail);
                        }
                    }

                    if (newDetails.Count > 0)
                        await db.Insertable(newDetails).ExecuteCommandAsync();

                    if (updateDetails.Count > 0)
                        await db.Updateable(updateDetails).ExecuteCommandAsync();

                    await RecalculateOrderTotalsAsync(request.OrderGUID);

                    await db.Ado.CommitTranAsync();

                    _logger.LogInformation(
                        "批量添加PDA仓库订单明细成功，新增 {NewCount} 条，更新 {UpdateCount} 条",
                        newDetails.Count,
                        updateDetails.Count
                    );

                    return new PDAWarehouseOrderDetailResponseDto
                    {
                        Success = true,
                        Message =
                            $"批量添加成功，新增 {newDetails.Count} 条，更新 {updateDetails.Count} 条",
                    };
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量添加PDA仓库订单明细失败");
                return new PDAWarehouseOrderDetailResponseDto
                {
                    Success = false,
                    Message = "批量添加失败: " + ex.Message,
                };
            }
        }

        #endregion

        #region 商品查询

        public async Task<PDAWarehouseProductListResponseDto> GetProductsAsync(
            PDAWarehouseProductFilterDto filter
        )
        {
            try
            {
                var query = new WarehouseProductQueryDto
                {
                    Keyword = filter.Keyword,
                    CategoryGUID = filter.CategoryGUID,
                    IsActive = filter.IsActive,
                    MinStockQuantity = filter.MinStockQuantity,
                    MaxStockQuantity = filter.MaxStockQuantity,
                    MinPrice = filter.MinPrice,
                    MaxPrice = filter.MaxPrice,
                    PriceType = filter.PriceType ?? "DomesticPrice",
                    PageNumber = filter.PageNumber,
                    PageSize = filter.PageSize,
                    SortBy = filter.SortBy ?? "ItemNumber",
                    SortDescending = filter.SortDescending,
                };

                var result = await _warehouseProductService.GetPagedProductsAsync(query);

                var productDtos = result
                    .Items.Select(wp => new PDAWarehouseProductDto
                    {
                        ProductCode = wp.ProductCode,
                        ItemNumber = wp.ItemNumber,
                        ProductName = wp.ProductBaseName,
                        ProductImage = wp.ProductImage,
                        Barcode = wp.ProductBarcode,
                        CategoryName = wp.ProductCategoryName,
                        DomesticPrice = wp.DomesticPrice,
                        OEMPrice = wp.OEMPrice,
                        ImportPrice = wp.ImportPrice,
                        StockQuantity = wp.StockQuantity,
                        MinOrderQuantity = wp.MinOrderQuantity,
                        StockAlertQuantity = wp.StockAlertQuantity,
                        Volume = wp.Volume,
                        IsActive = wp.IsActive,
                        LocationCode = wp.Locations?.FirstOrDefault()?.LocationCode,
                        CreatedAt = wp.CreatedAt,
                        UpdatedAt = wp.UpdatedAt,
                    })
                    .ToList();

                if (filter.OnlyInStock == true)
                {
                    productDtos = productDtos
                        .Where(p => p.StockQuantity.HasValue && p.StockQuantity.Value > 0)
                        .ToList();
                }

                return new PDAWarehouseProductListResponseDto
                {
                    Products = productDtos,
                    TotalCount = result.Total,
                    PageNumber = filter.PageNumber,
                    PageSize = filter.PageSize,
                    TotalPages = (int)Math.Ceiling((double)result.Total / filter.PageSize),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取PDA仓库商品列表失败");
                throw;
            }
        }

        public async Task<PDAWarehouseProductDto?> GetProductByCodeAsync(string productCode)
        {
            try
            {
                var product = await _warehouseProductService.GetProductByCodeAsync(productCode);

                if (product == null)
                    return null;

                return new PDAWarehouseProductDto
                {
                    ProductCode = product.ProductCode,
                    ItemNumber = product.Product?.ItemNumber,
                    ProductName = product.ProductName ?? product.Product?.ProductName,
                    ProductImage = product.Product?.ProductImage,
                    Barcode = product.Barcode,
                    CategoryName = product.WarehouseCategory?.CategoryName,
                    DomesticPrice = product.DomesticPrice,
                    OEMPrice = product.OEMPrice,
                    ImportPrice = product.ImportPrice,
                    StockQuantity = product.StockQuantity,
                    MinOrderQuantity = product.MinOrderQuantity,
                    StockAlertQuantity = product.StockAlertQuantity,
                    Volume = product.Volume,
                    IsActive = product.IsActive,
                    LocationCode = product.Locations?.FirstOrDefault()?.LocationCode,
                    CreatedAt = null,
                    UpdatedAt = null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取PDA仓库商品详情失败，商品代码: {ProductCode}",
                    productCode
                );
                return null;
            }
        }

        public async Task<PDAWarehouseProductDto?> ScanProductAsync(
            PDAScanProductRequestDto request
        )
        {
            try
            {
                if (!string.IsNullOrEmpty(request.Barcode))
                {
                    var products = await _warehouseProductService.SearchProductsByBarcodeAsync(
                        request.Barcode
                    );
                    if (products.Any())
                    {
                        return await GetProductByCodeAsync(products.First().ProductCode);
                    }
                }

                if (!string.IsNullOrEmpty(request.ItemNumber))
                {
                    var product = await _warehouseProductService.GetProductByItemNumberAsync(
                        request.ItemNumber
                    );
                    if (product != null)
                    {
                        return await GetProductByCodeAsync(product.ProductCode);
                    }
                }

                if (!string.IsNullOrEmpty(request.ProductCode))
                {
                    return await GetProductByCodeAsync(request.ProductCode);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫码查询PDA仓库商品失败");
                return null;
            }
        }

        public async Task<
            Dictionary<string, PDAWarehouseProductDto>
        > BatchGetProductsByItemNumbersAsync(List<string> itemNumbers)
        {
            try
            {
                var productDict = await _warehouseProductService.BatchGetProductsByItemNumbersAsync(
                    itemNumbers
                );

                var result = new Dictionary<string, PDAWarehouseProductDto>();
                foreach (var kvp in productDict)
                {
                    result[kvp.Key] = new PDAWarehouseProductDto
                    {
                        ProductCode = kvp.Value.ProductCode,
                        ItemNumber = kvp.Value.Product?.ItemNumber,
                        ProductName = kvp.Value.ProductName ?? kvp.Value.Product?.ProductName,
                        ProductImage = kvp.Value.Product?.ProductImage,
                        Barcode = kvp.Value.Barcode,
                        CategoryName = kvp.Value.WarehouseCategory?.CategoryName,
                        DomesticPrice = kvp.Value.DomesticPrice,
                        OEMPrice = kvp.Value.OEMPrice,
                        ImportPrice = kvp.Value.ImportPrice,
                        StockQuantity = kvp.Value.StockQuantity,
                        MinOrderQuantity = kvp.Value.MinOrderQuantity,
                        StockAlertQuantity = kvp.Value.StockAlertQuantity,
                        Volume = kvp.Value.Volume,
                        IsActive = kvp.Value.IsActive,
                        LocationCode = kvp.Value.Locations?.FirstOrDefault()?.LocationCode,
                        CreatedAt = null,
                        UpdatedAt = null,
                    };
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量获取PDA仓库商品失败");
                return new Dictionary<string, PDAWarehouseProductDto>();
            }
        }

        #endregion

        #region 私有方法

        private async Task RecalculateOrderTotalsAsync(string orderGuid)
        {
            try
            {
                var db = _context.Db;

                var details = await db.Queryable<WareHouseOrderDetails>()
                    .Where(d => d.OrderGUID == orderGuid)
                    .ToListAsync();

                var importTotalAmount = details.Sum(d => d.ImportAmount ?? 0);
                var oemTotalAmount = details.Sum(d => d.OEMAmount ?? 0);

                await db.Updateable<WareHouseOrder>()
                    .SetColumns(o => new WareHouseOrder
                    {
                        ImportTotalAmount = importTotalAmount,
                        OEMTotalAmount = oemTotalAmount,
                        UpdatedAt = DateTime.Now,
                    })
                    .Where(o => o.OrderGUID == orderGuid)
                    .ExecuteCommandAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新计算订单总金额失败，订单GUID: {OrderGuid}", orderGuid);
            }
        }

        private string GetFlowStatusText(int? flowStatus)
        {
            return flowStatus switch
            {
                0 => "草稿",
                1 => "已提交",
                2 => "审核中",
                3 => "已审核",
                4 => "已取消",
                _ => "未知",
            };
        }

        private string GetInboundStatusText(int? inboundStatus)
        {
            return inboundStatus switch
            {
                0 => "未入库",
                1 => "部分入库",
                2 => "已入库",
                _ => "未知",
            };
        }

        #endregion
    }
}
