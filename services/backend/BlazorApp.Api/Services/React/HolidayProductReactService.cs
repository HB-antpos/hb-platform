using System.Globalization;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class HolidayProductReactService : IHolidayProductReactService
    {
        private readonly SqlSugarContext _context;
        private readonly HBSalesRecordSqlSugarContext _hbSalesContext;
        private readonly POSMSqlSugarContext _posmContext;
        private readonly HqSqlSugarContext _hqContext;
        private readonly IMapper _mapper;
        private readonly ILogger<HolidayProductReactService> _logger;
        private readonly IMemoryCache _cache;

        public HolidayProductReactService(
          SqlSugarContext context,
          HBSalesRecordSqlSugarContext hbSalesContext,
          POSMSqlSugarContext posmContext,
          HqSqlSugarContext hqContext,
          IMapper mapper,
          ILogger<HolidayProductReactService> logger,
          IMemoryCache cache
        )
        {
            _context = context;
            _hbSalesContext = hbSalesContext;
            _posmContext = posmContext;
            _hqContext = hqContext;
            _mapper = mapper;
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// 从Excel导入节日商品
        /// </summary>
        /// <param name="request">导入请求DTO，包含商品列表、供应商代码、节日类型和年份等信息</param>
        /// <returns>返回导入结果，包含成功导入的商品数量</returns>
        public async Task<ApiResponse<object>> ImportHolidayProductsFromExcelAsync(
          HolidayProductImportRequestDto request
        )
        {
            try
            {
                var itemNumbers = request.Products.Select(p => p.ItemNumber).Distinct().ToList();

                var products = await _context
                  .Db.Queryable<Product>()
                  .Where(p =>
                    p.ItemNumber != null
                    && itemNumbers.Contains(p.ItemNumber)
                    && p.LocalSupplierCode == request.SupplierCode
                  )
                  .ToListAsync();

                var productCodeDict = products
                  .Where(p => p.ItemNumber != null && p.ProductCode != null)
                  .ToDictionary(p => p.ItemNumber!, p => p.ProductCode!);

                var existingRecords = await _context
                  .Db.Queryable<HolidayProduct>()
                  .Where(hp =>
                    hp.SupplierCode == request.SupplierCode
                    && hp.Year == request.Year
                    && itemNumbers.Contains(hp.ItemNumber)
                  )
                  .ToListAsync();

                var existingDict = existingRecords.ToDictionary(hp =>
                  $"{hp.ItemNumber}_{hp.SupplierCode}_{hp.Year}"
                );

                var toUpdate = new List<HolidayProduct>();
                var toInsert = new List<HolidayProduct>();

                for (var i = 0; i < request.Products.Count; i++)
                {
                    var item = request.Products[i];
                    var rowNumber = item.Row ?? (i + 2);
                    var productCode = productCodeDict.GetValueOrDefault(item.ItemNumber);
                    var key = $"{item.ItemNumber}_{request.SupplierCode}_{request.Year}";

                    if (existingDict.TryGetValue(key, out var existing))
                    {
                        existing.Sequence = item.Sequence;
                        existing.ProductCode = productCode ?? string.Empty;
                        existing.ProductImage = item.ProductImage;
                        existing.row = rowNumber;
                        existing.HolidayType = request.HolidayType;
                        existing.UpdatedAt = DateTime.Now;
                        toUpdate.Add(existing);
                    }
                    else
                    {
                        var newProduct = new HolidayProduct
                        {
                            GUID = Guid.NewGuid().ToString(),
                            Sequence = item.Sequence,
                            ProductCode = productCode ?? string.Empty,
                            ItemNumber = item.ItemNumber,
                            SupplierCode = request.SupplierCode,
                            ProductImage = item.ProductImage,
                            row = rowNumber,
                            HolidayType = request.HolidayType,
                            Year = request.Year,
                            ImportDate = DateTime.Now,
                            CreatedAt = DateTime.Now,
                            UpdatedAt = DateTime.Now,
                        };
                        toInsert.Add(newProduct);
                    }
                }

                if (toUpdate.Count > 0)
                {
                    await _context.HolidayProductDb.UpdateRangeAsync(toUpdate);
                }

                if (toInsert.Count > 0)
                {
                    await _context.HolidayProductDb.InsertRangeAsync(toInsert);
                }

                _logger.LogInformation(
                  "成功导入节日商品，更新 {UpdateCount} 条，插入 {InsertCount} 条，供应商：{SupplierCode}，年份：{Year}",
                  toUpdate.Count,
                  toInsert.Count,
                  request.SupplierCode,
                  request.Year
                );

                return ApiResponse<object>.OK(new { Count = toUpdate.Count + toInsert.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入节日商品失败");
                return ApiResponse<object>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 获取节日商品分析数据
        /// </summary>
        /// <param name="request">分析请求参数，包含供应商代码、节日类型、年份、日期范围、分店代码等</param>
        /// <returns>返回节日商品分析响应，包含商品列表、各分店的进货和销售数据</returns>
        public async Task<
          ApiResponse<HolidayProductAnalysisResponseDto>
        > GetHolidayProductsAnalysisAsync(HolidayProductAnalysisRequestDto request)
        {
            try
            {
                // 获取日期范围和节日类型名称
                var (startDate, endDate) = GetDateRange(request);
                var holidayTypeName = GetHolidayTypeName(request.HolidayType);

                // 获取节日商品列表（分页）
                var (holidayProducts, totalCount) = await GetHolidayProductsAsync(request);
                if (!holidayProducts.Any())
                {
                    return ApiResponse<HolidayProductAnalysisResponseDto>.OK(
                      new HolidayProductAnalysisResponseDto
                      {
                          Items = new List<HolidayProductAnalysisItemDto>(),
                          StartDate = startDate,
                          EndDate = endDate,
                          HolidayTypeName = holidayTypeName,
                          TotalCount = totalCount,
                      }
                    );
                }

                // 获取所有商品代码（去重且过滤空值）
                var productCodes = holidayProducts
                  .Select(p => p.ProductCode)
                  .Where(pc => !string.IsNullOrEmpty(pc))
                  .Distinct()
                  .ToList();
                // 获取分店代码列表，如果未指定则获取所有激活的分店
                var storeCodes =
                  request.StoreCodes?.Any() == true ? request.StoreCodes : await GetActiveStoreCodesAsync();

                // 并行获取进货数据和销售数据
                var purchaseDataTask = GetPurchaseDataAsync(productCodes, storeCodes, startDate, endDate);
                var salesDataTask = GetSalesDataAsync(productCodes, storeCodes, startDate, endDate);

                await Task.WhenAll(purchaseDataTask, salesDataTask);

                var purchaseData = await purchaseDataTask;
                var salesData = await salesDataTask;

                // 预先加载所有分店信息并构建字典，避免N+1查询
                var stores = await _context
                  .Db.Queryable<Store>()
                  .Where(s => storeCodes.Contains(s.StoreCode))
                  .ToListAsync();

                var storeNameDict = stores.ToDictionary(s => s.StoreCode, s => s.StoreName);

                // 构建分析结果
                var items = new List<HolidayProductAnalysisItemDto>();
                foreach (var holidayProduct in holidayProducts.OrderBy(p => p.Sequence))
                {
                    // 筛选当前商品的进货明细
                    var purchaseDetails = purchaseData
                      .Where(d => d.ProductCode == holidayProduct.ProductCode)
                      .ToList();

                    // 筛选当前商品的销售明细
                    var salesDetails = salesData
                      .Where(d => d.ProductCode == holidayProduct.ProductCode)
                      .ToList();

                    var item = new HolidayProductAnalysisItemDto
                    {
                        Sequence = holidayProduct.Sequence,
                        ProductCode = holidayProduct.ProductCode,
                        ItemNumber = holidayProduct.ItemNumber,
                        ProductImage = holidayProduct.ProductImage,
                        ProductName = holidayProduct.Product?.ProductName,

                        // 汇总进货数据
                        TotalPurchaseQuantity = purchaseDetails.Sum(d => d.Quantity),
                        // 汇总销售数据
                        TotalSalesQuantity = salesDetails.Sum(d => d.Quantity),
                        TotalSalesAmount = salesDetails.Sum(d => d.Amount),
                        TotalOriginalAmount = salesDetails.Sum(d => d.OriginalAmount),
                        TotalDiscountAmount = salesDetails.Sum(d => d.DiscountAmount),
                        AverageDiscountRate = salesDetails.Any()
                        ? salesDetails.Average(d => d.DiscountRate)
                        : 0,
                        TotalDiscountQuantity = salesDetails.Sum(d => d.DiscountQuantity),
                    };

                    // 为每个分店添加明细数据
                    foreach (var storeCode in storeCodes)
                    {
                        var storePurchase = purchaseDetails.FirstOrDefault(d => d.StoreCode == storeCode);
                        var storeSales = salesDetails.FirstOrDefault(d => d.StoreCode == storeCode);

                        item.BranchDetails.Add(
                          new BranchDetailDto
                          {
                              StoreCode = storeCode,
                              // 使用字典快速获取分店名称，避免数据库查询
                              StoreName = storeNameDict.GetValueOrDefault(storeCode, storeCode),
                              PurchaseQuantity = storePurchase?.Quantity ?? 0,
                              SalesQuantity = storeSales?.Quantity ?? 0,
                              SalesAmount = storeSales?.Amount ?? 0,
                              OriginalAmount = storeSales?.OriginalAmount ?? 0,
                              DiscountAmount = storeSales?.DiscountAmount ?? 0,
                              DiscountRate = storeSales?.DiscountRate ?? 0,
                              DiscountQuantity = storeSales?.DiscountQuantity ?? 0,
                          }
                        );
                    }

                    items.Add(item);
                }

                var response = new HolidayProductAnalysisResponseDto
                {
                    Items = items,
                    StartDate = startDate,
                    EndDate = endDate,
                    HolidayTypeName = holidayTypeName,
                    TotalCount = totalCount,
                };

                return ApiResponse<HolidayProductAnalysisResponseDto>.OK(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取节日商品分析失败");
                return ApiResponse<HolidayProductAnalysisResponseDto>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 获取商品每周销量数据（用于图表展示）
        /// </summary>
        /// <param name="productCode">商品代码</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <param name="storeCodes">分店代码列表，为空时获取所有激活分店</param>
        /// <returns>返回每周销量图表数据</returns>
        public async Task<ApiResponse<WeeklySalesChartDto>> GetProductWeeklySalesAsync(
          string productCode,
          DateTime startDate,
          DateTime endDate,
          List<string>? storeCodes = null
        )
        {
            try
            {
                // 获取分店代码列表
                var actualStoreCodes =
                  storeCodes?.Any() == true ? storeCodes : await GetActiveStoreCodesAsync();
                // 获取所有分店信息并构建字典
                var stores = await _context.StoreDb.GetListAsync();
                var storeDict = stores.ToDictionary(s => s.StoreCode, s => s.StoreName);

                // 从HBSalesRecord数据库获取销售数据
                var hbSalesData = await GetHBSalesDataAsync(
                  productCode,
                  actualStoreCodes,
                  startDate,
                  endDate
                );
                // 从POSM数据库获取销售数据
                var posmSalesData = await GetPOSMSalesDataAsync(
                  productCode,
                  actualStoreCodes,
                  startDate,
                  endDate
                );

                // 合并两个数据源的销售数据
                var allSalesData = hbSalesData.Concat(posmSalesData).ToList();
                var weeklyData = new List<WeeklySalesDataDto>();

                // 按周分组统计销售数据
                var weekNumber = 1;
                var currentWeekStart = GetWeekStartDate(startDate);
                while (currentWeekStart <= endDate)
                {
                    var currentWeekEnd = currentWeekStart.AddDays(6);
                    if (currentWeekEnd > endDate)
                        currentWeekEnd = endDate;

                    // 筛选本周数据并按分店分组
                    var weekData = allSalesData
                      .Where(d => d.Date >= currentWeekStart && d.Date <= currentWeekEnd)
                      .GroupBy(d => d.StoreCode)
                      .Select(g => new BranchWeeklySalesDto
                      {
                          StoreCode = g.Key,
                          StoreName = storeDict.GetValueOrDefault(g.Key, g.Key),
                          Quantity = g.Sum(d => d.Quantity),
                          Amount = g.Sum(d => d.Amount),
                      })
                      .ToList();

                    // 添加本周数据到结果列表
                    weeklyData.Add(
                      new WeeklySalesDataDto
                      {
                          WeekNumber = weekNumber,
                          WeekLabel = $"W{weekNumber}",
                          WeekStartDate = currentWeekStart,
                          WeekEndDate = currentWeekEnd,
                          TotalQuantity = weekData.Sum(w => w.Quantity),
                          TotalAmount = weekData.Sum(w => w.Amount),
                          BranchData = weekData,
                      }
                    );

                    // 移动到下一周
                    currentWeekStart = currentWeekStart.AddDays(7);
                    weekNumber++;
                }

                // 获取商品名称
                var product = await _context.ProductDb.GetSingleAsync(p => p.ProductCode == productCode);

                return ApiResponse<WeeklySalesChartDto>.OK(
                  new WeeklySalesChartDto
                  {
                      ProductCode = productCode,
                      ProductName = product?.ProductName,
                      WeeklyData = weeklyData,
                  }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品每周销量失败: {ProductCode}", productCode);
                return ApiResponse<WeeklySalesChartDto>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 根据商品编号和供应商代码获取商品代码
        /// </summary>
        /// <param name="itemNumber">商品编号</param>
        /// <param name="supplierCode">供应商代码</param>
        /// <returns>返回商品代码，未找到时返回null</returns>
        private async Task<string?> GetProductCodeByItemNumberAsync(
          string itemNumber,
          string supplierCode
        )
        {
            var product = await _context.ProductDb.GetSingleAsync(p =>
              p.ItemNumber == itemNumber && p.LocalSupplierCode == supplierCode
            );
            return product?.ProductCode;
        }

        /// <summary>
        /// 获取节日商品列表（分页）
        /// </summary>
        /// <param name="request">查询条件，包含供应商代码、节日类型、年份、页码等</param>
        /// <returns>返回节日商品列表和总数</returns>
        private async Task<(List<HolidayProduct> Items, int TotalCount)> GetHolidayProductsAsync(
          HolidayProductAnalysisRequestDto request
        )
        {
            var query = _context.Db.Queryable<HolidayProduct>();

            if (!string.IsNullOrWhiteSpace(request.SupplierCode))
                query = query.Where(hp => hp.SupplierCode == request.SupplierCode);

            if (request.HolidayType.HasValue)
                query = query.Where(hp => hp.HolidayType == request.HolidayType.Value);

            if (request.Year.HasValue)
                query = query.Where(hp => hp.Year == request.Year.Value);

            query = query.OrderBy(hp => hp.Sequence);

            RefAsync<int> totalCount = 0;
            var list = await query.ToPageListAsync(request.PageNumber, request.PageSize, totalCount);

            return (list, totalCount);
        }

        /// <summary>
        /// 获取日期范围
        /// </summary>
        /// <param name="request">请求参数，可能包含指定的日期范围或节日类型</param>
        /// <returns>返回开始日期和结束日期的元组</returns>
        private (DateTime StartDate, DateTime EndDate) GetDateRange(
          HolidayProductAnalysisRequestDto request
        )
        {
            // 如果请求中指定了日期范围，直接使用
            if (request.StartDate.HasValue && request.EndDate.HasValue)
                return (request.StartDate.Value, request.EndDate.Value);

            // 如果指定了节日类型，使用节日对应的日期范围
            if (request.HolidayType.HasValue)
            {
                var holidayType = (HolidayType)request.HolidayType.Value;
                var year = request.Year ?? DateTime.Now.Year;
                return holidayType.GetDateRange(year);
            }

            // 默认使用指定年份的全年范围
            var defaultYear = request.Year ?? DateTime.Now.Year;
            return (new DateTime(defaultYear, 1, 1), new DateTime(defaultYear, 12, 31));
        }

        /// <summary>
        /// 获取节日类型名称
        /// </summary>
        /// <param name="holidayType">节日类型枚举值</param>
        /// <returns>返回节日类型的中文名称，未指定时返回"全部"</returns>
        private string GetHolidayTypeName(int? holidayType)
        {
            if (!holidayType.HasValue)
                return "全部";

            return ((HolidayType)holidayType.Value).GetName();
        }

        /// <summary>
        /// 获取所有激活的分店代码列表
        /// </summary>
        /// <returns>返回激活的分店代码列表</returns>
        private async Task<List<string>> GetActiveStoreCodesAsync()
        {
            var stores = await _context.StoreDb.GetListAsync(s => s.IsActive);
            return stores.Select(s => s.StoreCode).ToList();
        }

        /// <summary>
        /// 根据分店代码获取分店名称
        /// </summary>
        /// <param name="storeCode">分店代码</param>
        /// <returns>返回分店名称，未找到时返回分店代码</returns>
        /// <remarks>
        /// ⚠️ 警告：此方法每次调用都会执行一次数据库查询。
        /// 请勿在循环中调用此方法，否则会产生 N+1 查询问题。
        /// 
        /// 推荐做法：在循环外批量获取门店数据，使用字典查找：
        /// <code>
        /// var storeDict = (await _context.Db.Queryable&lt;Store&gt;().ToListAsync())
        ///     .ToDictionary(s => s.StoreCode, s => s.StoreName);
        /// foreach (var item in items)
        /// {
        ///     var storeName = storeDict.GetValueOrDefault(item.StoreCode, item.StoreCode);
        /// }
        /// </code>
        /// </remarks>
        private string GetStoreName(string storeCode)
        {
            var store = _context.Db.Queryable<Store>().Where(s => s.StoreCode == storeCode).First();
            return store?.StoreName ?? storeCode;
        }

        /// <summary>
        /// 获取进货数据
        /// </summary>
        /// <param name="productCodes">商品代码列表</param>
        /// <param name="storeCodes">分店代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <returns>返回进货数据列表</returns>
        private async Task<List<PurchaseDataItem>> GetPurchaseDataAsync(
          List<string> productCodes,
          List<string> storeCodes,
          DateTime startDate,
          DateTime endDate
        )
        {
            var query = _context
              .Db.Queryable<StoreLocalSupplierInvoiceDetails>()
              .InnerJoin<StoreLocalSupplierInvoice>((d, m) => d.InvoiceGUID == m.InvoiceGUID)
              .Where(
                (d, m) =>
                  productCodes.Contains(d.ProductCode ?? string.Empty)
                  && storeCodes.Contains(d.StoreCode ?? string.Empty)
                  && m.InboundDate >= startDate
                  && m.InboundDate <= endDate
                  && m.StoreCode != "1006"
              )
              .Select(
                (d, m) =>
                  new PurchaseDataItem
                  {
                      ProductCode = d.ProductCode ?? string.Empty,
                      StoreCode = d.StoreCode ?? string.Empty,
                      Quantity = (int)(d.Quantity ?? 0),
                  }
              );

            return await query.ToListAsync();
        }

        /// <summary>
        /// 获取销售数据（合并HBSalesRecord和POSM两个数据源）
        /// </summary>
        /// <param name="productCodes">商品代码列表</param>
        /// <param name="storeCodes">分店代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <returns>返回合并后的销售数据列表</returns>
        private async Task<List<SalesDataItem>> GetSalesDataAsync(
          List<string> productCodes,
          List<string> storeCodes,
          DateTime startDate,
          DateTime endDate
        )
        {
            // 从HBSalesRecord获取销售数据
            var hbSalesData = await GetHBSalesDataAsync(productCodes, storeCodes, startDate, endDate);
            // 从POSM获取销售数据
            var posmSalesData = await GetPOSMSalesDataAsync(productCodes, storeCodes, startDate, endDate);

            // 合并两个数据源的销售数据
            var mergedData = new Dictionary<string, SalesDataItem>();
            var keyComparer = StringComparer.OrdinalIgnoreCase;

            // 处理HBSalesRecord数据
            foreach (var item in hbSalesData)
            {
                var key = $"{item.StoreCode}_{item.ProductCode}";
                if (!mergedData.ContainsKey(key))
                {
                    mergedData[key] = new SalesDataItem
                    {
                        StoreCode = item.StoreCode,
                        ProductCode = item.ProductCode,
                        Quantity = 0,
                        Amount = 0,
                        OriginalAmount = 0,
                        DiscountAmount = 0,
                        DiscountRate = 0,
                        DiscountQuantity = 0,
                    };
                }
                mergedData[key].Quantity += item.Quantity;
                mergedData[key].Amount += item.Amount;
                mergedData[key].OriginalAmount += item.OriginalAmount;
                mergedData[key].DiscountAmount += item.DiscountAmount;
                mergedData[key].DiscountQuantity += item.DiscountQuantity;
            }

            // 处理POSM数据
            foreach (var item in posmSalesData)
            {
                var key = $"{item.StoreCode}_{item.ProductCode}";
                if (!mergedData.ContainsKey(key))
                {
                    mergedData[key] = new SalesDataItem
                    {
                        StoreCode = item.StoreCode,
                        ProductCode = item.ProductCode,
                        Quantity = 0,
                        Amount = 0,
                        OriginalAmount = 0,
                        DiscountAmount = 0,
                        DiscountRate = 0,
                        DiscountQuantity = 0,
                    };
                }
                mergedData[key].Quantity += item.Quantity;
                mergedData[key].Amount += item.Amount;
                mergedData[key].OriginalAmount += item.OriginalAmount;
                mergedData[key].DiscountAmount += item.DiscountAmount;
                mergedData[key].DiscountQuantity += item.DiscountQuantity;
            }

            // 重新计算折扣率（基于合并后的原价和折扣金额）
            foreach (var item in mergedData.Values)
            {
                if (item.OriginalAmount > 0)
                {
                    item.DiscountRate = item.DiscountAmount / item.OriginalAmount;
                }
            }

            return mergedData.Values.ToList();
        }

        /// <summary>
        /// 从HBSalesRecord数据库获取销售数据
        /// </summary>
        /// <param name="productCodes">商品代码列表</param>
        /// <param name="storeCodes">分店代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <returns>返回销售数据列表</returns>
        private async Task<List<SalesDataItem>> GetHBSalesDataAsync(
          List<string> productCodes,
          List<string> storeCodes,
          DateTime startDate,
          DateTime endDate
        )
        {
            if (!LegacyDatabaseGate.HBSalesEnabled)
                return await GetFrozenHistorySalesAsync(productCodes, storeCodes, startDate, endDate);

            try
            {
                var data = await _hbSalesContext
                  .Db.Queryable<SalesOrderDetailRecord>()
                  .Where(d =>
                    storeCodes.Contains(d.B分店代码 ?? string.Empty)
                    && d.B结账日期 >= startDate
                    && d.B结账日期 <= endDate
                    && productCodes.Contains(d.B产品编号 ?? string.Empty)
                  )
                  .Select(d => new SalesDataItem
                  {
                      StoreCode = d.B分店代码 ?? string.Empty,
                      ProductCode = d.B产品编号 ?? string.Empty,
                      Quantity = (int)(d.B数量 ?? 0),
                      Amount = d.B合计金额 ?? 0,
                      OriginalAmount = d.B原价合计金额 ?? 0,
                      DiscountAmount = (d.B原价合计金额 - d.B合计金额) ?? 0,
                      DiscountRate = d.B折扣率 ?? 0,
                      DiscountQuantity = d.B数量 > 0 && d.B折扣率 > 0 ? (int)(d.B数量 ?? 0) : 0,
                  })
                  .ToListAsync();

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 HBSalesRecord 获取销售数据失败");
                return new List<SalesDataItem>();
            }
        }

        /// <summary>
        /// 从POSM数据库获取销售数据
        /// </summary>
        /// <param name="productCodes">商品代码列表</param>
        /// <param name="storeCodes">分店代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <returns>返回销售数据列表</returns>
        private async Task<List<SalesDataItem>> GetPOSMSalesDataAsync(
          List<string> productCodes,
          List<string> storeCodes,
          DateTime startDate,
          DateTime endDate
        )
        {
            // 窗口内的 POSM 销售已包含在日统计里，这里再读会重复计算。
            if (!LegacyDatabaseGate.HBSalesEnabled)
            {
                if (startDate < SalesStatisticsHBSalesHistoryWindow.EndExclusive)
                    startDate = SalesStatisticsHBSalesHistoryWindow.EndExclusive;
                if (startDate > endDate)
                    return new List<SalesDataItem>();
            }

            try
            {
                var data = await _posmContext
                  .Db.Queryable<SalesOrderDetail>()
                  .InnerJoin<SalesOrder>((d, o) => d.OrderGuid == o.OrderGuid)
                  .Where(
                    (d, o) =>
                      storeCodes.Contains(o.BranchCode ?? string.Empty)
                      && o.OrderTime >= startDate
                      && o.OrderTime <= endDate
                      && productCodes.Contains(d.ProductCode ?? string.Empty)
                  )
                  .Select(
                    (d, o) =>
                      new SalesDataItem
                      {
                          StoreCode = o.BranchCode ?? string.Empty,
                          ProductCode = d.ProductCode ?? string.Empty,
                          Quantity = (int)(d.Quantity ?? 0),
                          Amount = d.ActualAmount ?? 0,
                          OriginalAmount = d.Subtotal ?? 0,
                          DiscountAmount = d.DiscountAmount ?? 0,
                          DiscountRate = d.DiscountRate ?? 0,
                          DiscountQuantity =
                          d.Quantity > 0 && d.DiscountAmount > 0 ? (int)(d.Quantity ?? 0) : 0,
                      }
                  )
                  .ToListAsync();

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 POSM 获取销售数据失败");
                return new List<SalesDataItem>();
            }
        }

        /// <summary>
        /// 从HBSalesRecord数据库获取单个商品的销售数据（包含日期字段）
        /// </summary>
        /// <param name="productCode">商品代码</param>
        /// <param name="storeCodes">分店代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <returns>返回销售数据列表</returns>
        private async Task<List<SalesDataItem>> GetHBSalesDataAsync(
          string productCode,
          List<string> storeCodes,
          DateTime startDate,
          DateTime endDate
        )
        {
            if (!LegacyDatabaseGate.HBSalesEnabled)
                return await GetFrozenHistorySalesAsync(new List<string> { productCode }, storeCodes, startDate, endDate);

            try
            {
                var data = await _hbSalesContext
                  .Db.Queryable<SalesOrderDetailRecord>()
                  .Where(d =>
                    storeCodes.Contains(d.B分店代码 ?? string.Empty)
                    && d.B结账日期 >= startDate
                    && d.B结账日期 <= endDate
                    && d.B产品编号 == productCode
                  )
                  .Select(d => new SalesDataItem
                  {
                      StoreCode = d.B分店代码 ?? string.Empty,
                      ProductCode = d.B产品编号 ?? string.Empty,
                      Date = d.B结账日期 ?? DateTime.MinValue,
                      Quantity = (int)(d.B数量 ?? 0),
                      Amount = d.B合计金额 ?? 0,
                      OriginalAmount = d.B原价合计金额 ?? 0,
                      DiscountAmount = (d.B原价合计金额 - d.B合计金额) ?? 0,
                      DiscountRate = d.B折扣率 ?? 0,
                      DiscountQuantity = d.B数量 > 0 && d.B折扣率 > 0 ? (int)(d.B数量 ?? 0) : 0,
                  })
                  .ToListAsync();

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 HBSalesRecord 获取销售数据失败");
                return new List<SalesDataItem>();
            }
        }

        /// <summary>
        /// 从POSM数据库获取单个商品的销售数据（包含日期字段）
        /// </summary>
        /// <param name="productCode">商品代码</param>
        /// <param name="storeCodes">分店代码列表</param>
        /// <param name="startDate">开始日期</param>
        /// <param name="endDate">结束日期</param>
        /// <returns>返回销售数据列表</returns>
        private async Task<List<SalesDataItem>> GetPOSMSalesDataAsync(
          string productCode,
          List<string> storeCodes,
          DateTime startDate,
          DateTime endDate
        )
        {
            // 窗口内的 POSM 销售已包含在日统计里，这里再读会重复计算。
            if (!LegacyDatabaseGate.HBSalesEnabled)
            {
                if (startDate < SalesStatisticsHBSalesHistoryWindow.EndExclusive)
                    startDate = SalesStatisticsHBSalesHistoryWindow.EndExclusive;
                if (startDate > endDate)
                    return new List<SalesDataItem>();
            }

            try
            {
                var data = await _posmContext
                  .Db.Queryable<SalesOrderDetail>()
                  .InnerJoin<SalesOrder>((d, o) => d.OrderGuid == o.OrderGuid)
                  .Where(
                    (d, o) =>
                      storeCodes.Contains(o.BranchCode ?? string.Empty)
                      && o.OrderTime >= startDate
                      && o.OrderTime <= endDate
                      && d.ProductCode == productCode
                  )
                  .Select(
                    (d, o) =>
                      new SalesDataItem
                      {
                          StoreCode = o.BranchCode ?? string.Empty,
                          ProductCode = d.ProductCode ?? string.Empty,
                          Date = o.OrderTime ?? DateTime.MinValue,
                          Quantity = (int)(d.Quantity ?? 0),
                          Amount = d.ActualAmount ?? 0,
                          OriginalAmount = d.Subtotal ?? 0,
                          DiscountAmount = d.DiscountAmount ?? 0,
                          DiscountRate = d.DiscountRate ?? 0,
                          DiscountQuantity =
                          d.Quantity > 0 && d.DiscountAmount > 0 ? (int)(d.Quantity ?? 0) : 0,
                      }
                  )
                  .ToListAsync();

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 POSM 获取销售数据失败");
                return new List<SalesDataItem>();
            }
        }

        /// <summary>
        /// 获取日期所在周的周一日期
        /// </summary>
        /// <param name="date">任意日期</param>
        /// <returns>返回该日期所在周的周一</returns>
        private DateTime GetWeekStartDate(DateTime date)
        {
            var dayOfWeek = (int)date.DayOfWeek;
            // 如果是周日（0），减6天得到周一；否则减去 (dayOfWeek - 1) 天
            var daysToSubtract = dayOfWeek == 0 ? 6 : dayOfWeek - 1;
            return date.AddDays(-daysToSubtract).Date;
        }

        /// <summary>
        /// 进货数据内部类
        /// </summary>
        private class PurchaseDataItem
        {
            public string ProductCode { get; set; } = string.Empty;
            public string StoreCode { get; set; } = string.Empty;
            public int Quantity { get; set; }
        }

        /// <summary>
        /// 销售数据内部类
        /// </summary>
        /// <summary>
        /// HBSales 断开后的历史销量来源：窗口内的商品分店日统计。
        /// 日统计没有原价和折扣明细，这些字段按无折扣返回。
        /// </summary>
        private async Task<List<SalesDataItem>> GetFrozenHistorySalesAsync(
          List<string> productCodes,
          List<string> storeCodes,
          DateTime startDate,
          DateTime endDate
        )
        {
            var windowEnd = SalesStatisticsHBSalesHistoryWindow.EndExclusive;
            return await _context.Db.Queryable<ProductStoreDailySalesStatistic>()
              .Where(s =>
                storeCodes.Contains(s.BranchCode)
                && productCodes.Contains(s.ProductCode)
                && s.Date >= startDate.Date
                && s.Date <= endDate
                && s.Date < windowEnd
              )
              .Select(s => new SalesDataItem
              {
                  StoreCode = s.BranchCode,
                  ProductCode = s.ProductCode,
                  Date = s.Date,
                  Quantity = s.TotalQuantity,
                  Amount = s.TotalAmount,
                  OriginalAmount = s.TotalAmount,
              })
              .ToListAsync();
        }

        private class SalesDataItem
        {
            public string ProductCode { get; set; } = string.Empty;
            public string StoreCode { get; set; } = string.Empty;
            public DateTime Date { get; set; } = DateTime.MinValue;
            public int Quantity { get; set; }
            public decimal Amount { get; set; }
            public decimal OriginalAmount { get; set; }
            public decimal DiscountAmount { get; set; }
            public decimal DiscountRate { get; set; }
            public int DiscountQuantity { get; set; }
        }

        /// <summary>
        /// 获取节日商品列表（仅基本信息，不包含销售数据）
        /// </summary>
        public async Task<ApiResponse<HolidayProductListResponseDto>> GetHolidayProductsListAsync(
          HolidayProductListRequestDto request
        )
        {
            try
            {
                var cacheKey =
                  $"HolidayProductList_{request.SupplierCode}_{request.HolidayType}_{request.Year}_{request.PageNumber}_{request.PageSize}";

                //if (
                //    _cache.TryGetValue<HolidayProductListResponseDto>(
                //        cacheKey,
                //        out var cachedResult
                //    )
                //)
                //{
                //    _logger.LogInformation("从缓存获取节日商品列表: {CacheKey}", cacheKey);
                //    return ApiResponse<HolidayProductListResponseDto>.OK(cachedResult);
                //}

                var query = _context.Db.Queryable<HolidayProduct>();

                if (!string.IsNullOrWhiteSpace(request.SupplierCode))
                    query = query.Where(hp => hp.SupplierCode == request.SupplierCode);

                if (request.HolidayType.HasValue)
                    query = query.Where(hp => hp.HolidayType == request.HolidayType.Value);

                if (request.Year.HasValue)
                    query = query.Where(hp => hp.Year == request.Year.Value);

                query = query.OrderBy(hp => hp.Sequence);

                RefAsync<int> totalCount = 0;
                var list = await query
                  .Select(hp => new HolidayProductDto
                  {
                      GUID = hp.GUID,
                      Sequence = hp.Sequence,
                      ProductCode = hp.ProductCode,
                      ItemNumber = hp.ItemNumber,
                      SupplierCode = hp.SupplierCode,
                      ProductImage = hp.ProductImage,
                      HolidayType = hp.HolidayType,
                      Year = hp.Year,
                      ImportDate = hp.ImportDate,
                      ProductName = null,
                      Row = hp.row,
                  })
                  .ToPageListAsync(request.PageNumber, request.PageSize, totalCount);

                var response = new HolidayProductListResponseDto { Items = list, TotalCount = totalCount };

                var cacheOptions = new MemoryCacheEntryOptions()
                  .SetAbsoluteExpiration(TimeSpan.FromHours(1))
                  .SetSlidingExpiration(TimeSpan.FromMinutes(10));

                _cache.Set(cacheKey, response, cacheOptions);
                _logger.LogInformation(
                  "节日商品列表已缓存: {CacheKey}, 过期时间: {Expiration}",
                  cacheKey,
                  DateTime.Now.AddHours(1)
                );

                return ApiResponse<HolidayProductListResponseDto>.OK(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取节日商品列表失败");
                return ApiResponse<HolidayProductListResponseDto>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 根据商品代码获取进货数据（固定日期：8月1日-12月20日）
        /// </summary>
        public async Task<ApiResponse<PurchaseDataResponseDto>> GetPurchaseDataByProductCodesAsync(
          PurchaseDataRequestDto request
        )
        {
            try
            {
                var startDate = new DateTime(request.Year, 8, 1);
                var endDate = new DateTime(request.Year, 12, 20);

                var storeCodes =
                  request.StoreCodes?.Any() == true ? request.StoreCodes : await GetActiveStoreCodesAsync();

                var purchaseData = await GetPurchaseDataAsync(
                  request.ProductCodes,
                  storeCodes,
                  startDate,
                  endDate
                );

                var stores = await _context
                  .Db.Queryable<Store>()
                  .Where(s => storeCodes.Contains(s.StoreCode))
                  .ToListAsync();

                var storeNameDict = stores.ToDictionary(s => s.StoreCode, s => s.StoreName);

                var productPurchaseQuantity = new Dictionary<string, int>();
                var branchPurchaseData = new Dictionary<string, List<BranchPurchaseDto>>();

                foreach (var productCode in request.ProductCodes)
                {
                    var productPurchases = purchaseData.Where(d => d.ProductCode == productCode).ToList();

                    var totalQuantity = productPurchases.Sum(d => d.Quantity);
                    productPurchaseQuantity[productCode] = totalQuantity;

                    var branches = new List<BranchPurchaseDto>();
                    foreach (var storeCode in storeCodes)
                    {
                        var storePurchase = productPurchases.FirstOrDefault(d => d.StoreCode == storeCode);
                        branches.Add(
                          new BranchPurchaseDto
                          {
                              StoreCode = storeCode,
                              StoreName = storeNameDict.GetValueOrDefault(storeCode, storeCode),
                              Quantity = storePurchase?.Quantity ?? 0,
                          }
                        );
                    }
                    branchPurchaseData[productCode] = branches;
                }

                var response = new PurchaseDataResponseDto
                {
                    ProductPurchaseQuantity = productPurchaseQuantity,
                    BranchPurchaseData = branchPurchaseData,
                };

                return ApiResponse<PurchaseDataResponseDto>.OK(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取进货数据失败");
                return ApiResponse<PurchaseDataResponseDto>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 根据商品代码获取销售数据
        /// </summary>
        public async Task<ApiResponse<SalesDataResponseDto>> GetSalesDataByProductCodesAsync(
          SalesDataRequestDto request
        )
        {
            try
            {
                var storeCodes =
                  request.StoreCodes?.Any() == true ? request.StoreCodes : await GetActiveStoreCodesAsync();

                var salesData = await GetSalesDataAsync(
                  request.ProductCodes,
                  storeCodes,
                  request.StartDate,
                  request.EndDate
                );

                var stores = await _context
                  .Db.Queryable<Store>()
                  .Where(s => storeCodes.Contains(s.StoreCode))
                  .ToListAsync();

                var storeNameDict = stores.ToDictionary(s => s.StoreCode, s => s.StoreName);

                var productSalesData = new Dictionary<string, ProductSalesDataDto>();

                foreach (var productCode in request.ProductCodes)
                {
                    var productSales = salesData.Where(d => d.ProductCode == productCode).ToList();

                    var branches = new List<BranchSalesDto>();
                    foreach (var storeCode in storeCodes)
                    {
                        var storeSales = productSales.FirstOrDefault(d => d.StoreCode == storeCode);
                        branches.Add(
                          new BranchSalesDto
                          {
                              StoreCode = storeCode,
                              StoreName = storeNameDict.GetValueOrDefault(storeCode, storeCode),
                              SalesQuantity = storeSales?.Quantity ?? 0,
                              SalesAmount = storeSales?.Amount ?? 0,
                              OriginalAmount = storeSales?.OriginalAmount ?? 0,
                              DiscountAmount = storeSales?.DiscountAmount ?? 0,
                              DiscountRate = storeSales?.DiscountRate ?? 0,
                              DiscountQuantity = storeSales?.DiscountQuantity ?? 0,
                          }
                        );
                    }

                    productSalesData[productCode] = new ProductSalesDataDto
                    {
                        TotalSalesQuantity = productSales.Sum(d => d.Quantity),
                        TotalSalesAmount = productSales.Sum(d => d.Amount),
                        TotalOriginalAmount = productSales.Sum(d => d.OriginalAmount),
                        TotalDiscountAmount = productSales.Sum(d => d.DiscountAmount),
                        AverageDiscountRate = productSales.Any()
                        ? productSales.Average(d => d.DiscountRate)
                        : 0,
                        TotalDiscountQuantity = productSales.Sum(d => d.DiscountQuantity),
                        BranchSalesData = branches,
                    };
                }

                var response = new SalesDataResponseDto { ProductSalesData = productSalesData };

                return ApiResponse<SalesDataResponseDto>.OK(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取销售数据失败");
                return ApiResponse<SalesDataResponseDto>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 获取节日商品简化分析数据（仅总数，不含分店明细）
        /// </summary>
        /// <param name="request">分析请求参数</param>
        /// <returns>返回仅包含总数的节日商品分析响应</returns>
        public async Task<
          ApiResponse<HolidayProductAnalysisSimpleResponseDto>
        > GetHolidayProductsSimpleAsync(HolidayProductAnalysisRequestDto request)
        {
            try
            {
                var (startDate, endDate) = GetDateRange(request);
                var holidayTypeName = GetHolidayTypeName(request.HolidayType);

                var (holidayProducts, totalCount) = await GetHolidayProductsAsync(request);
                if (!holidayProducts.Any())
                {
                    return ApiResponse<HolidayProductAnalysisSimpleResponseDto>.OK(
                      new HolidayProductAnalysisSimpleResponseDto
                      {
                          Items = new List<HolidayProductAnalysisSimpleItemDto>(),
                          StartDate = startDate,
                          EndDate = endDate,
                          HolidayTypeName = holidayTypeName,
                          TotalCount = totalCount,
                      }
                    );
                }

                var productCodes = holidayProducts
                  .Select(hp => hp.ProductCode)
                  .Where(pc => !string.IsNullOrEmpty(pc))
                  .Distinct()
                  .ToList();
                var storeCodes =
                  request.StoreCodes?.Any() == true ? request.StoreCodes : await GetActiveStoreCodesAsync();

                var purchaseDataTask = GetPurchaseDataAsync(productCodes, storeCodes, startDate, endDate);
                var salesDataTask = GetSalesDataAsync(productCodes, storeCodes, startDate, endDate);

                await Task.WhenAll(purchaseDataTask, salesDataTask);

                var purchaseData = await purchaseDataTask;
                var salesData = await salesDataTask;

                var items = new List<HolidayProductAnalysisSimpleItemDto>();
                foreach (var holidayProduct in holidayProducts.OrderBy(hp => hp.Sequence))
                {
                    var purchaseDetails = purchaseData
                      .Where(d => d.ProductCode == holidayProduct.ProductCode)
                      .ToList();
                    var salesDetails = salesData
                      .Where(d => d.ProductCode == holidayProduct.ProductCode)
                      .ToList();

                    items.Add(
                      new HolidayProductAnalysisSimpleItemDto
                      {
                          Sequence = holidayProduct.Sequence,
                          ProductCode = holidayProduct.ProductCode,
                          ItemNumber = holidayProduct.ItemNumber,
                          ProductImage = holidayProduct.ProductImage,
                          ProductName = null,
                          TotalPurchaseQuantity = purchaseDetails.Sum(d => d.Quantity),
                          TotalSalesQuantity = salesDetails.Sum(d => d.Quantity),
                          TotalSalesAmount = salesDetails.Sum(d => d.Amount),
                          TotalOriginalAmount = salesDetails.Sum(d => d.OriginalAmount),
                          TotalDiscountAmount = salesDetails.Sum(d => d.DiscountAmount),
                          AverageDiscountRate = salesDetails.Any()
                          ? salesDetails.Average(d => d.DiscountRate)
                          : 0,
                          TotalDiscountQuantity = salesDetails.Sum(d => d.DiscountQuantity),
                      }
                    );
                }

                var response = new HolidayProductAnalysisSimpleResponseDto
                {
                    Items = items,
                    StartDate = startDate,
                    EndDate = endDate,
                    HolidayTypeName = holidayTypeName,
                    TotalCount = totalCount,
                };

                return ApiResponse<HolidayProductAnalysisSimpleResponseDto>.OK(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取节日商品简化分析失败");
                return ApiResponse<HolidayProductAnalysisSimpleResponseDto>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 获取单个商品的分店明细数据
        /// </summary>
        /// <param name="request">请求参数，包含商品代码、日期范围、分店代码</param>
        /// <returns>返回该商品的所有分店明细</returns>
        public async Task<ApiResponse<ProductBranchDetailsResponseDto>> GetProductBranchDetailsAsync(
          ProductBranchDetailsRequestDto request
        )
        {
            try
            {
                var startDate = request.StartDate ?? DateTime.MinValue;
                var endDate = request.EndDate ?? DateTime.MaxValue;
                var storeCodes =
                  request.StoreCodes?.Any() == true ? request.StoreCodes : await GetActiveStoreCodesAsync();

                var purchaseDataTask = GetPurchaseDataAsync(
                  new List<string> { request.ProductCode },
                  storeCodes,
                  startDate,
                  endDate
                );
                var salesDataTask = GetSalesDataAsync(
                  new List<string> { request.ProductCode },
                  storeCodes,
                  startDate,
                  endDate
                );

                await Task.WhenAll(purchaseDataTask, salesDataTask);

                var purchaseData = await purchaseDataTask;
                var salesData = await salesDataTask;

                var stores = await _context
                  .Db.Queryable<Store>()
                  .Where(s => storeCodes.Contains(s.StoreCode))
                  .ToListAsync();

                var storeNameDict = stores.ToDictionary(s => s.StoreCode, s => s.StoreName);

                var branchDetails = new List<BranchDetailDto>();
                foreach (var storeCode in storeCodes)
                {
                    var storePurchase = purchaseData.FirstOrDefault(d => d.StoreCode == storeCode);
                    var storeSales = salesData.FirstOrDefault(d => d.StoreCode == storeCode);

                    branchDetails.Add(
                      new BranchDetailDto
                      {
                          StoreCode = storeCode,
                          StoreName = storeNameDict.GetValueOrDefault(storeCode, storeCode),
                          PurchaseQuantity = storePurchase?.Quantity ?? 0,
                          SalesQuantity = storeSales?.Quantity ?? 0,
                          SalesAmount = storeSales?.Amount ?? 0,
                          OriginalAmount = storeSales?.OriginalAmount ?? 0,
                          DiscountAmount = storeSales?.DiscountAmount ?? 0,
                          DiscountRate = storeSales?.DiscountRate ?? 0,
                          DiscountQuantity = storeSales?.DiscountQuantity ?? 0,
                      }
                    );
                }

                var response = new ProductBranchDetailsResponseDto
                {
                    ProductCode = request.ProductCode,
                    BranchDetails = branchDetails,
                };

                return ApiResponse<ProductBranchDetailsResponseDto>.OK(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品分店明细失败: {ProductCode}", request.ProductCode);
                return ApiResponse<ProductBranchDetailsResponseDto>.Error(ex.Message);
            }
        }
    }
}
