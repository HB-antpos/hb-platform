using BlazorApp.Shared.Helper;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 义乌货柜服务实现 - 基于新的Container模型
    /// </summary>
    public class YiwuContainerService : IYiwuContainerService
    {
        private readonly SqlSugarContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<YiwuContainerService> _logger;
        private readonly ContainerExportService _exportService;
        private readonly ITranslationService _translationService;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;

        public YiwuContainerService(
            SqlSugarContext context,
            IMapper mapper,
            ILogger<YiwuContainerService> logger,
            ContainerExportService exportService,
            ITranslationService translationService,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
            _exportService = exportService;
            _translationService = translationService;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        #region 货柜主表操作

        /// <summary>
        /// 获取货柜列表
        /// </summary>
        public async Task<YiwuContainerListResponse> GetContainersAsync(YiwuContainerQueryRequest request)
        {
            try
            {
                var query = _context.Db.Queryable<Container>()
                    .Where(c => !c.IsDeleted);

                // 日期过滤
                if (request.StartDate.HasValue && request.EndDate.HasValue)
                {
                    switch (request.DateType.ToLower())
                    {
                        case "loadingdate":
                            query = query.Where(c => c.LoadingDate >= request.StartDate && c.LoadingDate <= request.EndDate);
                            break;
                        case "estimatedarrivaldate":
                            query = query.Where(c => c.EstimatedArrivalDate >= request.StartDate && c.EstimatedArrivalDate <= request.EndDate);
                            break;
                        case "actualarrivaldate":
                            query = query.Where(c => c.ActualArrivalDate >= request.StartDate && c.ActualArrivalDate <= request.EndDate);
                            break;
                        default:
                            query = query.Where(c => c.LoadingDate >= request.StartDate && c.LoadingDate <= request.EndDate);
                            break;
                    }
                }

                // 货柜编号过滤
                if (!string.IsNullOrEmpty(request.ContainerNumberFilter))
                {
                    query = query.Where(c => c.ContainerNumber != null && c.ContainerNumber.Contains(request.ContainerNumberFilter));
                }

                // 状态过滤
                if (request.StatusFilter.HasValue)
                {
                    query = query.Where(c => c.Status == request.StatusFilter.Value);
                }

                // 商品货号过滤
                if (!string.IsNullOrEmpty(request.ItemNumberFilter))
                {
                    var containerCodesWithItem = await _context.Db.Queryable<ContainerDetail>()
                        .LeftJoin<DomesticProduct>((cd, p) => cd.ProductCode == p.ProductCode)
                        .Where((cd, p) => !cd.IsDeleted && p.HBProductNo != null && p.HBProductNo.Contains(request.ItemNumberFilter))
                        .Select((cd, p) => cd.ContainerCode)
                        .ToListAsync();

                    if (containerCodesWithItem.Any())
                    {
                        query = query.Where(c => containerCodesWithItem.Contains(c.ContainerCode));
                    }
                    else
                    {
                        // 如果没有找到匹配的商品，返回空结果
                        return new YiwuContainerListResponse
                        {
                            Containers = new List<YiwuContainerDto>(),
                            TotalCount = 0,
                            Page = request.Page,
                            PageSize = request.PageSize
                        };
                    }
                }

                // 排序
                switch (request.SortBy.ToLower())
                {
                    case "loadingdate":
                        query = request.SortDirection.ToLower() == "asc"
                            ? query.OrderBy(c => c.LoadingDate)
                            : query.OrderByDescending(c => c.LoadingDate);
                        break;
                    case "estimatedarrivaldate":
                        query = request.SortDirection.ToLower() == "asc"
                            ? query.OrderBy(c => c.EstimatedArrivalDate)
                            : query.OrderByDescending(c => c.EstimatedArrivalDate);
                        break;
                    case "actualarrivaldate":
                        query = request.SortDirection.ToLower() == "asc"
                            ? query.OrderBy(c => c.ActualArrivalDate)
                            : query.OrderByDescending(c => c.ActualArrivalDate);
                        break;
                    case "containernumber":
                        query = request.SortDirection.ToLower() == "asc"
                            ? query.OrderBy(c => c.ContainerNumber)
                            : query.OrderByDescending(c => c.ContainerNumber);
                        break;
                    default:
                        query = query.OrderByDescending(c => c.LoadingDate);
                        break;
                }

                // 获取总数
                var totalCount = await query.CountAsync();

                // 分页查询
                var containers = await query
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToListAsync();

                var containerDtos = _mapper.Map<List<YiwuContainerDto>>(containers);

                return new YiwuContainerListResponse
                {
                    Containers = containerDtos,
                    TotalCount = totalCount,
                    Page = request.Page,
                    PageSize = request.PageSize
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜列表失败");
                throw;
            }
        }

        /// <summary>
        /// 获取货柜详情
        /// </summary>
        public async Task<YiwuContainerDto?> GetContainerAsync(string containerCode)
        {
            try
            {
                var container = await _context.Db.Queryable<Container>()
                    .Where(c => c.ContainerCode == containerCode && !c.IsDeleted)
                    .FirstAsync();

                if (container == null)
                    return null;

                var containerDto = _mapper.Map<YiwuContainerDto>(container);

                // 获取明细
                containerDto.Details = await GetContainerDetailsAsync(containerCode);

                return containerDto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜详情失败, ContainerCode: {ContainerCode}", containerCode);
                throw;
            }
        }

        /// <summary>
        /// 创建货柜
        /// </summary>
        public async Task<string> CreateContainerAsync(YiwuContainerDto containerDto)
        {
            try
            {
                await _context.Db.Ado.BeginTranAsync();
                await ContainerMutationLock.AcquireContainersAsync(
                    _context.Db,
                    new[] { containerDto.ContainerCode }
                );

                var container = _mapper.Map<Container>(containerDto);
                container.CreatedAt = DateTime.UtcNow;
                container.IsDeleted = false;

                var result = await _context.ContainerDb.InsertReturnEntityAsync(container);

                await _context.Db.Ado.CommitTranAsync();

                _logger.LogInformation("创建货柜成功, ContainerCode: {ContainerCode}", result.ContainerCode);
                return result.ContainerCode;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "创建货柜失败");
                throw;
            }
        }

        /// <summary>
        /// 更新货柜
        /// </summary>
        public async Task<bool> UpdateContainerAsync(YiwuContainerDto containerDto)
        {
            try
            {
                await _context.Db.Ado.BeginTranAsync();
                await ContainerMutationLock.AcquireContainersAsync(
                    _context.Db,
                    new[] { containerDto.ContainerCode }
                );

                var existingContainer = await _context.ContainerDb.GetByIdAsync(containerDto.ContainerCode);
                if (existingContainer == null || existingContainer.IsDeleted)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                    return false;
                }

                _mapper.Map(containerDto, existingContainer);
                existingContainer.UpdatedAt = DateTime.UtcNow;

                var success = await _context.ContainerDb.UpdateAsync(existingContainer);

                await _context.Db.Ado.CommitTranAsync();

                _logger.LogInformation("更新货柜成功, ContainerCode: {ContainerCode}", containerDto.ContainerCode);
                return success;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "更新货柜失败, ContainerCode: {ContainerCode}", containerDto.ContainerCode);
                throw;
            }
        }

        /// <summary>
        /// 删除货柜
        /// </summary>
        public async Task<bool> DeleteContainerAsync(string containerCode)
        {
            try
            {
                await _context.Db.Ado.BeginTranAsync();
                await ContainerMutationLock.AcquireContainersAsync(
                    _context.Db,
                    new[] { containerCode }
                );

                // 软删除货柜
                var success = await _context.Db.Updateable<Container>()
                    .SetColumns(c => new Container { IsDeleted = true, UpdatedAt = DateTime.UtcNow })
                    .Where(c => c.ContainerCode == containerCode)
                    .ExecuteCommandAsync() > 0;

                if (success)
                {
                    // 软删除相关明细
                    await _context.Db.Updateable<ContainerDetail>()
                        .SetColumns(cd => new ContainerDetail { IsDeleted = true, UpdatedAt = DateTime.UtcNow })
                        .Where(cd => cd.ContainerCode == containerCode)
                        .ExecuteCommandAsync();
                }

                await _context.Db.Ado.CommitTranAsync();

                _logger.LogInformation("删除货柜成功, ContainerCode: {ContainerCode}", containerCode);
                return success;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "删除货柜失败, ContainerCode: {ContainerCode}", containerCode);
                throw;
            }
        }

        /// <summary>
        /// 批量删除货柜
        /// </summary>
        public async Task<BatchOperationResponse> BatchDeleteContainersAsync(List<string> containerCodes)
        {
            var response = new BatchOperationResponse();
            var errors = new List<string>();
            if (containerCodes == null || containerCodes.Count == 0)
            {
                response.Success = true;
                response.Message = "没有需要删除的货柜";
                return response;
            }

            try
            {
                await _context.Db.Ado.BeginTranAsync();
                await ContainerMutationLock.AcquireContainersAsync(_context.Db, containerCodes);

                foreach (var containerCode in containerCodes)
                {
                    try
                    {
                        var success = await _context.Db.Updateable<Container>()
                            .SetColumns(c => new Container { IsDeleted = true, UpdatedAt = DateTime.UtcNow })
                            .Where(c => c.ContainerCode == containerCode)
                            .ExecuteCommandAsync() > 0;

                        if (success)
                        {
                            // 软删除相关明细
                            await _context.Db.Updateable<ContainerDetail>()
                                .SetColumns(cd => new ContainerDetail { IsDeleted = true, UpdatedAt = DateTime.UtcNow })
                                .Where(cd => cd.ContainerCode == containerCode)
                                .ExecuteCommandAsync();

                            response.SuccessCount++;
                        }
                        else
                        {
                            errors.Add($"货柜 {containerCode} 不存在或已删除");
                            response.FailedCount++;
                        }
                    }
                    catch (Exception ex)
                        when (!ContainerMutationLock.TryResolveConflict(ex, out _))
                    {
                        errors.Add($"删除货柜 {containerCode} 失败: {ex.Message}");
                        response.FailedCount++;
                    }
                }

                await _context.Db.Ado.CommitTranAsync();

                response.Success = response.FailedCount == 0;
                response.Errors = errors;
                response.Message = $"成功删除 {response.SuccessCount} 个货柜，失败 {response.FailedCount} 个";

                return response;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "批量删除货柜失败");
                throw;
            }
        }

        #endregion

        #region 货柜明细操作

        /// <summary>
        /// 获取货柜明细列表
        /// </summary>
        public async Task<List<YiwuContainerDetailDto>> GetContainerDetailsAsync(string containerCode)
        {
            try
            {
                var details = await _context.Db.Queryable<ContainerDetail>()
                    .LeftJoin<DomesticProduct>((cd, p) => cd.ProductCode == p.ProductCode)
                    .Where((cd, p) => cd.ContainerCode == containerCode && !cd.IsDeleted)
                    .Select((cd, p) => new ContainerDetail
                    {
                        DetailCode = cd.DetailCode,
                        ContainerCode = cd.ContainerCode,
                        ProductCode = cd.ProductCode,
                        LoadingType = cd.LoadingType,
                        MixedGroupCode = cd.MixedGroupCode,
                        ProductType = cd.ProductType,
                        SetQuantity = cd.SetQuantity,
                        LoadingPieces = cd.LoadingPieces,
                        LoadingQuantity = cd.LoadingQuantity,
                        DomesticPrice = cd.DomesticPrice,
                        AdjustmentRate = cd.AdjustmentRate,
                        ImportPrice = cd.ImportPrice,
                        OEMPrice = cd.OEMPrice,
                        PackingQuantity = cd.PackingQuantity,
                        UnitVolume = cd.UnitVolume,
                        TotalAmount = cd.TotalAmount,
                        TotalVolume = cd.TotalVolume,
                        TransportCost = cd.TransportCost,
                        Status = cd.Status,
                        Remarks = cd.Remarks,
                        CreatedAt = cd.CreatedAt,
                        UpdatedAt = cd.UpdatedAt,
                        IsDeleted = cd.IsDeleted,
                        Product = p
                    })
                    .OrderBy(cd => cd.ProductCode)
                    .ToListAsync();

                var detailDtos = _mapper.Map<List<YiwuContainerDetailDto>>(details);

                // 手动计算体积：装柜件数 × 单件体积
                foreach (var dto in detailDtos)
                {
                    if (dto.LoadingPieces.HasValue && dto.UnitVolume.HasValue)
                    {
                        dto.TotalVolume = Math.Round(dto.LoadingPieces.Value * dto.UnitVolume.Value, 3);
                    }
                }

                return detailDtos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜明细列表失败, ContainerCode: {ContainerCode}", containerCode);
                throw;
            }
        }

        /// <summary>
        /// 获取货柜明细详情
        /// </summary>
        public async Task<YiwuContainerDetailDto?> GetContainerDetailAsync(string detailCode)
        {
            try
            {
                var detail = await _context.Db.Queryable<ContainerDetail>()
                    .LeftJoin<DomesticProduct>((cd, p) => cd.ProductCode == p.ProductCode)
                    .Where((cd, p) => cd.DetailCode == detailCode && !cd.IsDeleted)
                    .Select((cd, p) => new ContainerDetail
                    {
                        DetailCode = cd.DetailCode,
                        ContainerCode = cd.ContainerCode,
                        ProductCode = cd.ProductCode,
                        LoadingType = cd.LoadingType,
                        MixedGroupCode = cd.MixedGroupCode,
                        ProductType = cd.ProductType,
                        SetQuantity = cd.SetQuantity,
                        LoadingPieces = cd.LoadingPieces,
                        LoadingQuantity = cd.LoadingQuantity,
                        DomesticPrice = cd.DomesticPrice,
                        AdjustmentRate = cd.AdjustmentRate,
                        ImportPrice = cd.ImportPrice,
                        OEMPrice = cd.OEMPrice,
                        PackingQuantity = cd.PackingQuantity,
                        UnitVolume = cd.UnitVolume,
                        TotalAmount = cd.TotalAmount,
                        TotalVolume = cd.TotalVolume,
                        TransportCost = cd.TransportCost,
                        Status = cd.Status,
                        Remarks = cd.Remarks,
                        CreatedAt = cd.CreatedAt,
                        UpdatedAt = cd.UpdatedAt,
                        IsDeleted = cd.IsDeleted,
                        Product = p
                    })
                    .FirstAsync();

                if (detail == null)
                    return null;

                var detailDto = _mapper.Map<YiwuContainerDetailDto>(detail);

                // 手动计算体积：装柜件数 × 单件体积
                if (detailDto.LoadingPieces.HasValue && detailDto.UnitVolume.HasValue)
                {
                    detailDto.TotalVolume = Math.Round(detailDto.LoadingPieces.Value * detailDto.UnitVolume.Value, 3);
                }

                return detailDto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货柜明细详情失败, DetailCode: {DetailCode}", detailCode);
                throw;
            }
        }

        /// <summary>
        /// 创建货柜明细
        /// </summary>
        public async Task<string> CreateContainerDetailAsync(YiwuContainerDetailDto detailDto)
        {
            try
            {
                await _context.Db.Ado.BeginTranAsync();
                await ContainerMutationLock.AcquireContainersAsync(
                    _context.Db,
                    new[] { detailDto.ContainerCode }
                );

                var detail = _mapper.Map<ContainerDetail>(detailDto);
                detail.CreatedAt = DateTime.UtcNow;
                detail.IsDeleted = false;

                // 计算字段
                detail.UpdateCalculatedFields();

                var result = await _context.ContainerDetailDb.InsertReturnEntityAsync(detail);

                // 重新计算货柜汇总信息
                await RecalculateContainerSummaryCoreAsync(detail.ContainerCode);

                await _context.Db.Ado.CommitTranAsync();

                _logger.LogInformation("创建货柜明细成功, DetailCode: {DetailCode}", result.DetailCode);
                return result.DetailCode;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "创建货柜明细失败");
                throw;
            }
        }

        /// <summary>
        /// 更新货柜明细
        /// </summary>
        public async Task<bool> UpdateContainerDetailAsync(YiwuContainerDetailDto detailDto)
        {
            try
            {
                var candidateContainerCode = await _context.Db.Queryable<ContainerDetail>()
                    .Where(detail => detail.DetailCode == detailDto.DetailCode && !detail.IsDeleted)
                    .Select(detail => detail.ContainerCode)
                    .FirstAsync();
                if (string.IsNullOrWhiteSpace(candidateContainerCode))
                {
                    return false;
                }

                await _context.Db.Ado.BeginTranAsync();
                var mutationLock = await ContainerMutationLock.AcquireContainersAsync(
                    _context.Db,
                    new[] { candidateContainerCode, detailDto.ContainerCode }
                );

                var existingDetail = await _context.ContainerDetailDb.GetByIdAsync(detailDto.DetailCode);
                if (existingDetail == null || existingDetail.IsDeleted)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                    return false;
                }
                mutationLock.EnsureCovers(
                    new[] { existingDetail.ContainerCode, detailDto.ContainerCode }
                );
                var originalContainerCode = existingDetail.ContainerCode;

                _mapper.Map(detailDto, existingDetail);
                existingDetail.UpdatedAt = DateTime.UtcNow;

                // 重新计算字段
                existingDetail.UpdateCalculatedFields();

                var success = await _context.ContainerDetailDb.UpdateAsync(existingDetail);

                if (success)
                {
                    // 重新计算货柜汇总信息
                    await BatchRecalculateContainerSummaryCoreAsync(
                        new[] { originalContainerCode, existingDetail.ContainerCode }
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    );
                }

                await _context.Db.Ado.CommitTranAsync();

                _logger.LogInformation("更新货柜明细成功, DetailCode: {DetailCode}", detailDto.DetailCode);
                return success;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "更新货柜明细失败, DetailCode: {DetailCode}", detailDto.DetailCode);
                throw;
            }
        }

        /// <summary>
        /// 删除货柜明细
        /// </summary>
        public async Task<bool> DeleteContainerDetailAsync(string detailCode)
        {
            try
            {
                var candidateContainerCode = await _context.Db.Queryable<ContainerDetail>()
                    .Where(detail => detail.DetailCode == detailCode)
                    .Select(detail => detail.ContainerCode)
                    .FirstAsync();
                if (string.IsNullOrWhiteSpace(candidateContainerCode))
                {
                    return false;
                }

                await _context.Db.Ado.BeginTranAsync();
                var mutationLock = await ContainerMutationLock.AcquireContainersAsync(
                    _context.Db,
                    new[] { candidateContainerCode }
                );

                var detail = await _context.ContainerDetailDb.GetByIdAsync(detailCode);
                if (detail == null)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                    return false;
                }
                mutationLock.EnsureCovers(new[] { detail.ContainerCode });

                // 软删除
                var success = await _context.Db.Updateable<ContainerDetail>()
                    .SetColumns(cd => new ContainerDetail { IsDeleted = true, UpdatedAt = DateTime.UtcNow })
                    .Where(cd => cd.DetailCode == detailCode)
                    .ExecuteCommandAsync() > 0;

                if (success)
                {
                    // 重新计算货柜汇总信息
                    await RecalculateContainerSummaryCoreAsync(detail.ContainerCode);
                }

                await _context.Db.Ado.CommitTranAsync();

                _logger.LogInformation("删除货柜明细成功, DetailCode: {DetailCode}", detailCode);
                return success;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "删除货柜明细失败, DetailCode: {DetailCode}", detailCode);
                throw;
            }
        }

        /// <summary>
        /// 批量添加货柜明细（通过货号和件数）
        /// </summary>
        public async Task<BatchOperationResponse> BatchAddContainerDetailsAsync(BatchAddYiwuContainerDetailsRequest request)
        {
            var response = new BatchOperationResponse();
            var errors = new List<string>();

            try
            {
                // 验证数据
                var validationResult = await ValidateContainerDetailsAsync(request.Details);
                if (!validationResult.IsValid)
                {
                    response.Success = false;
                    response.Errors = validationResult.Errors;
                    response.Message = "数据验证失败";
                    return response;
                }

                await _context.Db.Ado.BeginTranAsync();
                await ContainerMutationLock.AcquireContainersAsync(
                    _context.Db,
                    new[] { request.ContainerCode }
                );

                // 验证货柜是否存在
                var container = await _context.ContainerDb.GetByIdAsync(request.ContainerCode);
                if (container == null || container.IsDeleted)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                    response.Success = false;
                    response.Message = "货柜不存在或已删除";
                    return response;
                }

                // 批量获取所有相关的商品信息
                var itemNumbers = request.Details.Select(d => d.ItemNumber).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                var products = await _context.Db.Queryable<DomesticProduct>()
                    .Where(p => itemNumbers.Contains(p.HBProductNo!) && !p.IsDeleted)
                    .ToListAsync();

                var productDict = products.Where(p => !string.IsNullOrEmpty(p.HBProductNo))
                    .ToDictionary(p => p.HBProductNo!, p => p);

                // 批量获取货柜中已存在的明细
                var productCodes = products.Select(p => p.ProductCode).Where(x => !string.IsNullOrEmpty(x)).ToList();
                var existingDetails = await _context.ContainerDetailDb
                    .AsQueryable()
                    .Where(d => d.ContainerCode == request.ContainerCode
                           && productCodes.Contains(d.ProductCode!)
                           && !d.IsDeleted)
                    .ToListAsync();

                var existingDetailDict = existingDetails.Where(d => !string.IsNullOrEmpty(d.ProductCode))
                    .ToDictionary(d => d.ProductCode!, d => d);

                // 准备批量操作的数据
                var detailsToInsert = new List<ContainerDetail>();
                var detailsToUpdate = new List<ContainerDetail>();

                foreach (var item in request.Details)
                {
                    try
                    {
                        // 检查商品是否存在
                        if (!productDict.TryGetValue(item.ItemNumber, out var product))
                        {
                            errors.Add($"货号 {item.ItemNumber} 对应的商品不存在");
                            response.FailedCount++;
                            continue;
                        }

                        // 检查该商品是否已在货柜中
                        if (existingDetailDict.TryGetValue(product.ProductCode, out var existingDetail))
                        {
                            // 商品已存在，准备更新
                            existingDetail.LoadingPieces = item.LoadingPieces;
                            existingDetail.LoadingQuantity = item.LoadingPieces * (product.PackingQuantity ?? 1);
                            existingDetail.DomesticPrice = product.DomesticPrice;
                            existingDetail.ImportPrice = product.ImportPrice;
                            existingDetail.OEMPrice = product.OEMPrice;
                            existingDetail.PackingQuantity = product.PackingQuantity;
                            existingDetail.UnitVolume = product.UnitVolume;
                            existingDetail.UpdatedAt = DateTime.UtcNow;

                            // 合并备注信息
                            if (!string.IsNullOrEmpty(item.Remarks))
                            {
                                if (string.IsNullOrEmpty(existingDetail.Remarks))
                                {
                                    existingDetail.Remarks = item.Remarks;
                                }
                                else if (!existingDetail.Remarks.Contains(item.Remarks))
                                {
                                    existingDetail.Remarks += $"; {item.Remarks}";
                                }
                            }

                            // 重新计算字段
                            existingDetail.UpdateCalculatedFields();

                            detailsToUpdate.Add(existingDetail);
                            response.UpdatedCount++;
                        }
                        else
                        {
                            // 商品不存在，准备新增
                            var detail = new ContainerDetail
                            {
                                ContainerCode = request.ContainerCode,
                                ProductCode = product.ProductCode,
                                LoadingPieces = item.LoadingPieces,
                                LoadingQuantity = item.LoadingPieces * (product.PackingQuantity ?? 1),
                                DomesticPrice = product.DomesticPrice,
                                ImportPrice = product.ImportPrice,
                                OEMPrice = product.OEMPrice,
                                PackingQuantity = product.PackingQuantity,
                                UnitVolume = product.UnitVolume,
                                AdjustmentRate = 1.0m,
                                Status = 0, // 正常状态
                                Remarks = item.Remarks,
                                CreatedAt = DateTime.UtcNow,
                                IsDeleted = false
                            };

                            // 计算字段
                            detail.UpdateCalculatedFields();

                            detailsToInsert.Add(detail);
                            response.CreatedCount++;
                        }

                        response.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"处理货号 {item.ItemNumber} 时发生错误");
                        errors.Add($"处理货号 {item.ItemNumber} 失败: {ex.Message}");
                        response.FailedCount++;
                    }
                }

                // 执行批量插入
                if (detailsToInsert.Any())
                {
                    await _context.ContainerDetailDb.InsertRangeAsync(detailsToInsert);
                    _logger.LogInformation($"批量新增货柜明细 {detailsToInsert.Count} 条");
                }

                // 执行批量更新
                if (detailsToUpdate.Any())
                {
                    await _context.ContainerDetailDb.UpdateRangeAsync(detailsToUpdate);
                    _logger.LogInformation($"批量更新货柜明细 {detailsToUpdate.Count} 条");
                }

                // 重新计算货柜汇总信息
                if (response.SuccessCount > 0)
                {
                    await RecalculateContainerSummaryCoreAsync(request.ContainerCode);
                    await AllocateTransportCostCoreAsync(request.ContainerCode);
                }

                await _context.Db.Ado.CommitTranAsync();

                response.Success = response.FailedCount == 0;
                response.Errors = errors;
                response.Message = $"成功添加 {response.SuccessCount} 个明细，失败 {response.FailedCount} 个";

                return response;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "批量添加货柜明细失败");
                throw;
            }
        }

        /// <summary>
        /// 批量删除货柜明细
        /// </summary>
        public async Task<BatchOperationResponse> BatchDeleteContainerDetailsAsync(List<string> detailCodes)
        {
            var response = new BatchOperationResponse();
            var errors = new List<string>();
            var containerCodes = new HashSet<string>();

            try
            {
                var candidateContainerCodes = await _context.Db.Queryable<ContainerDetail>()
                    .Where(detail => detailCodes.Contains(detail.DetailCode!) && !detail.IsDeleted)
                    .Where(detail => detail.ContainerCode != null)
                    .Select(detail => detail.ContainerCode!)
                    .ToListAsync();
                await _context.Db.Ado.BeginTranAsync();
                ContainerMutationLockScope? mutationLock = null;
                if (candidateContainerCodes.Count > 0)
                {
                    mutationLock = await ContainerMutationLock.AcquireContainersAsync(
                        _context.Db,
                        candidateContainerCodes
                    );
                }

                // 批量查询所有需要删除的明细
                var existingDetails = await _context.ContainerDetailDb
                    .AsQueryable()
                    .Where(d => detailCodes.Contains(d.DetailCode!) && !d.IsDeleted)
                    .ToListAsync();
                if (mutationLock == null && existingDetails.Count > 0)
                {
                    throw new ContainerMutationScopeChangedException(
                        ContainerMutationLock.NormalizeContainerCodes(
                            existingDetails.Select(detail => detail.ContainerCode)
                        )
                    );
                }
                mutationLock?.EnsureCovers(existingDetails.Select(detail => detail.ContainerCode));

                // 检查哪些明细不存在
                var existingDetailCodes = existingDetails.Select(d => d.DetailCode).ToHashSet();
                var notFoundDetails = detailCodes.Where(code => !existingDetailCodes.Contains(code)).ToList();

                // 记录不存在的明细
                foreach (var notFoundCode in notFoundDetails)
                {
                    errors.Add($"明细 {notFoundCode} 不存在或已删除");
                    response.FailedCount++;
                }

                // 收集涉及的货柜代码
                foreach (var detail in existingDetails)
                {
                    if (!string.IsNullOrEmpty(detail.ContainerCode))
                    {
                        containerCodes.Add(detail.ContainerCode);
                    }
                }

                // 批量软删除明细
                if (existingDetails.Any())
                {
                    var affectedRows = await _context.Db.Updateable<ContainerDetail>()
                        .SetColumns(cd => new ContainerDetail { IsDeleted = true, UpdatedAt = DateTime.UtcNow })
                        .Where(cd => existingDetailCodes.Contains(cd.DetailCode!))
                        .ExecuteCommandAsync();

                    response.SuccessCount = affectedRows;

                    if (affectedRows != existingDetails.Count)
                    {
                        var expectedCount = existingDetails.Count;
                        errors.Add($"预期删除 {expectedCount} 条记录，实际删除 {affectedRows} 条记录");
                        response.FailedCount += (expectedCount - affectedRows);
                    }

                    _logger.LogInformation($"批量删除货柜明细: 成功删除 {affectedRows} 条记录");
                }

                // 重新计算相关货柜的汇总信息
                foreach (var containerCode in containerCodes.OrderBy(
                    containerCode => containerCode,
                    StringComparer.Ordinal
                ))
                {
                    await RecalculateContainerSummaryCoreAsync(containerCode);
                }

                await _context.Db.Ado.CommitTranAsync();

                response.Success = response.FailedCount == 0;
                response.Errors = errors;
                response.Message = $"成功删除 {response.SuccessCount} 个明细，失败 {response.FailedCount} 个";

                return response;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "批量删除货柜明细失败");
                throw;
            }
        }

        /// <summary>
        /// 批量更新货柜明细 - 优化为真正的批量操作
        /// </summary>
        public async Task<BatchOperationResponse> BatchUpdateContainerDetailsAsync(List<YiwuContainerDetailDto> details)
        {
            var response = new BatchOperationResponse();
            var errors = new List<string>();
            var containerCodes = new HashSet<string>();

            if (!details.Any())
            {
                response.Success = true;
                response.Message = "没有需要更新的明细";
                return response;
            }

            try
            {
                var detailCodes = details.Select(d => d.DetailCode).ToList();
                var candidateContainerCodes = await _context.Db.Queryable<ContainerDetail>()
                    .Where(d => d.DetailCode != null && detailCodes.Contains(d.DetailCode) && !d.IsDeleted)
                    .Where(d => d.ContainerCode != null)
                    .Select(d => d.ContainerCode!)
                    .ToListAsync();
                candidateContainerCodes.AddRange(
                    details
                        .Select(detail => detail.ContainerCode)
                        .Where(containerCode => !string.IsNullOrWhiteSpace(containerCode))
                );
                await _context.Db.Ado.BeginTranAsync();
                ContainerMutationLockScope? mutationLock = null;
                if (candidateContainerCodes.Count > 0)
                {
                    mutationLock = await ContainerMutationLock.AcquireContainersAsync(
                        _context.Db,
                        candidateContainerCodes
                    );
                }

                // 第一步：批量查询所有需要更新的明细
                var existingDetails = await _context.Db.Queryable<ContainerDetail>()
                    .Where(d => d.DetailCode != null && detailCodes.Contains(d.DetailCode) && !d.IsDeleted)
                    .ToListAsync();
                if (mutationLock == null && existingDetails.Count > 0)
                {
                    throw new ContainerMutationScopeChangedException(
                        ContainerMutationLock.NormalizeContainerCodes(
                            existingDetails.Select(detail => detail.ContainerCode)
                        )
                    );
                }
                mutationLock?.EnsureCovers(existingDetails.Select(detail => detail.ContainerCode));

                var existingDetailDict = existingDetails.ToDictionary(d => d.DetailCode);

                // 第二步：准备批量更新的数据
                var validDetailsToUpdate = new List<ContainerDetail>();
                var updateTime = DateTime.Now;

                foreach (var detailDto in details)
                {
                    if (!existingDetailDict.TryGetValue(detailDto.DetailCode, out var existingDetail))
                    {
                        errors.Add($"明细 {detailDto.DetailCode} 不存在或已删除");
                        response.FailedCount++;
                        continue;
                    }

                    try
                    {
                        // 收集货柜编码用于后续汇总计算
                        containerCodes.Add(existingDetail.ContainerCode);

                        // 映射数据
                        _mapper.Map(detailDto, existingDetail);
                        containerCodes.Add(existingDetail.ContainerCode);
                        existingDetail.UpdatedAt = updateTime;
                        existingDetail.UpdateCalculatedFields();

                        validDetailsToUpdate.Add(existingDetail);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"准备更新明细 {detailDto.DetailCode} 失败: {ex.Message}");
                        response.FailedCount++;
                    }
                }

                // 第三步：执行批量更新
                if (validDetailsToUpdate.Any())
                {
                    mutationLock?.EnsureCovers(
                        validDetailsToUpdate.Select(detail => detail.ContainerCode)
                    );
                    try
                    {
                        // 使用SqlSugar的批量更新功能
                        var updateResult = await _context.Db.Updateable(validDetailsToUpdate)
                            .ExecuteCommandAsync();

                        response.SuccessCount = updateResult;

                        if (updateResult != validDetailsToUpdate.Count)
                        {
                            var failedCount = validDetailsToUpdate.Count - updateResult;
                            response.FailedCount += failedCount;
                            errors.Add($"批量更新时有 {failedCount} 条记录更新失败");
                        }
                    }
                    catch (Exception ex)
                        when (!ContainerMutationLock.TryResolveConflict(ex, out _))
                    {
                        _logger.LogError(ex, "批量更新货柜明细失败");
                        response.FailedCount += validDetailsToUpdate.Count;
                        errors.Add($"批量更新失败: {ex.Message}");
                    }
                }

                // 第四步：批量重新计算相关货柜的汇总信息
                if (containerCodes.Any())
                {
                    await BatchRecalculateContainerSummaryCoreAsync(containerCodes.ToList());
                }

                await _context.Db.Ado.CommitTranAsync();

                response.Success = response.FailedCount == 0;
                response.Errors = errors;
                response.Message = $"成功更新 {response.SuccessCount} 个明细，失败 {response.FailedCount} 个";

                return response;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                _logger.LogError(ex, "批量更新货柜明细失败");
                throw;
            }
        }

        #endregion

        #region 业务逻辑

        /// <summary>
        /// 批量重新计算货柜汇总信息 - 性能优化版本
        /// </summary>
        public async Task<bool> BatchRecalculateContainerSummaryAsync(List<string> containerCodes)
        {
            var validContainerCodes = (containerCodes ?? new List<string>())
                .Where(containerCode => !string.IsNullOrWhiteSpace(containerCode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(containerCode => containerCode, StringComparer.Ordinal)
                .ToList();
            if (validContainerCodes.Count == 0)
            {
                return true;
            }

            try
            {
                return await ExecuteLockedContainerMutationAsync(
                    validContainerCodes,
                    () => BatchRecalculateContainerSummaryCoreAsync(validContainerCodes)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量重新计算货柜汇总信息失败, ContainerCodes: {ContainerCodes}",
                    string.Join(", ", containerCodes ?? Enumerable.Empty<string>()));
                throw;
            }
        }

        /// <summary>
        /// 重新计算货柜汇总信息
        /// </summary>
        public async Task<bool> RecalculateContainerSummaryAsync(string containerCode)
        {
            try
            {
                return await ExecuteLockedContainerMutationAsync(
                    new[] { containerCode },
                    () => RecalculateContainerSummaryCoreAsync(containerCode)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新计算货柜汇总信息失败, ContainerCode: {ContainerCode}", containerCode);
                throw;
            }
        }

        /// <summary>
        /// 分摊运输成本到各个商品
        /// </summary>
        public async Task<bool> AllocateTransportCostAsync(string containerCode)
        {
            try
            {
                return await ExecuteLockedContainerMutationAsync(
                    new[] { containerCode },
                    () => AllocateTransportCostCoreAsync(containerCode)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分摊运输成本失败, ContainerCode: {ContainerCode}", containerCode);
                throw;
            }
        }

        private async Task<T> ExecuteLockedContainerMutationAsync<T>(
            IReadOnlyCollection<string> containerCodes,
            Func<Task<T>> action
        )
        {
            if (_context.Db.Ado.Transaction != null)
            {
                throw new InvalidOperationException("独立货柜汇总写入口不能复用调用方事务");
            }

            await _context.Db.Ado.BeginTranAsync();
            try
            {
                await ContainerMutationLock.AcquireContainersAsync(_context.Db, containerCodes);
                var result = await action();
                await _context.Db.Ado.CommitTranAsync();
                return result;
            }
            catch (Exception ex)
            {
                await RollbackContainerMutationTransactionSafelyAsync(ex);
                throw;
            }
        }

        private async Task RollbackContainerMutationTransactionSafelyAsync(
            Exception originalException
        )
        {
            if (_context.Db.Ado.Transaction == null)
            {
                return;
            }

            try
            {
                await _context.Db.Ado.RollbackTranAsync();
            }
            catch (Exception rollbackException)
            {
                // 回滚异常只能作为附加诊断，不能覆盖 1205 或应用锁繁忙等原始并发异常。
                ContainerMutationLock.ResetFailedTransaction(_context.Db);
                _logger.LogWarning(
                    rollbackException,
                    "货柜写事务回滚失败，保留原始异常 {OriginalExceptionType}",
                    originalException.GetType().Name
                );
            }
        }

        private async Task<bool> BatchRecalculateContainerSummaryCoreAsync(
            IReadOnlyCollection<string> validContainerCodes
        )
        {
            var allDetails = await _context.Db.Queryable<ContainerDetail>()
                .Where(cd =>
                    cd.ContainerCode != null
                    && validContainerCodes.Contains(cd.ContainerCode!)
                    && !cd.IsDeleted
                )
                .ToListAsync();
            var detailsByContainer = allDetails.ToLookup(
                detail => detail.ContainerCode,
                StringComparer.OrdinalIgnoreCase
            );
            var updateTime = DateTime.UtcNow;
            var successCount = 0;
            // 同一 SqlSugar 事务连接不得并发执行命令；按已排序的货柜键稳定更新。
            foreach (var containerCode in validContainerCodes.OrderBy(
                code => code,
                StringComparer.Ordinal
            ))
            {
                var details = detailsByContainer[containerCode];
                var totalPieces = details.Sum(d => d.LoadingPieces ?? 0);
                var totalQuantity = details.Sum(d => d.LoadingQuantity ?? 0);
                var totalAmount = details.Sum(d => d.TotalAmount ?? 0);
                var totalVolume = details.Sum(d =>
                    d.LoadingPieces.HasValue && d.UnitVolume.HasValue
                        ? Math.Round(d.LoadingPieces.Value * d.UnitVolume.Value, 3)
                        : d.TotalVolume ?? 0
                );
                var affectedRows = await _context.Db.Updateable<Container>()
                    .SetColumns(c => new Container
                    {
                        TotalPieces = totalPieces,
                        TotalQuantity = totalQuantity,
                        TotalAmount = totalAmount,
                        TotalVolume = totalVolume,
                        UpdatedAt = updateTime,
                    })
                    .Where(c => c.ContainerCode == containerCode)
                    .ExecuteCommandAsync();
                if (affectedRows > 0)
                {
                    successCount++;
                }
            }

            _logger.LogInformation(
                "批量重新计算货柜汇总信息完成，成功更新 {SuccessCount}/{TotalCount} 个货柜",
                successCount,
                validContainerCodes.Count
            );
            return successCount == validContainerCodes.Count;
        }

        private async Task<bool> RecalculateContainerSummaryCoreAsync(string containerCode)
        {
            var details = await _context.Db.Queryable<ContainerDetail>()
                .Where(cd => cd.ContainerCode == containerCode && !cd.IsDeleted)
                .ToListAsync();
            var totalPieces = details.Sum(d => d.LoadingPieces ?? 0);
            var totalQuantity = details.Sum(d => d.LoadingQuantity ?? 0);
            var totalAmount = details.Sum(d => d.TotalAmount ?? 0);
            var totalVolume = details.Sum(d => d.TotalVolume ?? 0);
            return await _context.Db.Updateable<Container>()
                .SetColumns(c => new Container
                {
                    TotalPieces = totalPieces,
                    TotalQuantity = totalQuantity,
                    TotalAmount = totalAmount,
                    TotalVolume = totalVolume,
                    UpdatedAt = DateTime.UtcNow,
                })
                .Where(c => c.ContainerCode == containerCode)
                .ExecuteCommandAsync() > 0;
        }

        private async Task<bool> AllocateTransportCostCoreAsync(string containerCode)
        {
            // 获取货柜信息
            var container = await _context.ContainerDb.GetByIdAsync(containerCode);
            if (container == null || !container.ShippingFee.HasValue || container.ShippingFee.Value <= 0)
            {
                return true; // 没有运费需要分摊
            }

            // 获取明细
            var details = await _context.Db.Queryable<ContainerDetail>()
                .Where(cd => cd.ContainerCode == containerCode && !cd.IsDeleted)
                .ToListAsync();

            if (!details.Any())
            {
                return true;
            }

            var totalVolume = details.Sum(d => d.TotalVolume ?? 0);
            if (totalVolume <= 0)
            {
                return true; // 没有体积，无法分摊
            }

            // 按体积比例分摊运费
            foreach (var detail in details)
            {
                if (detail.TotalVolume.HasValue && detail.TotalVolume.Value > 0)
                {
                    var volumeRatio = detail.TotalVolume.Value / totalVolume;
                    detail.TransportCost = Math.Round(container.ShippingFee.Value * volumeRatio, 2);
                    detail.UpdatedAt = DateTime.UtcNow;
                }
            }

            // 批量更新
            var updateResult = await _context.Db.Updateable(details).ExecuteCommandAsync();

            return updateResult > 0;
        }

        /// <summary>
        /// 根据商品信息查询相关货柜
        /// </summary>
        public async Task<List<YiwuContainerDto>> GetContainersByItemNumberAsync(string itemNumber)
        {
            try
            {
                var containerCodes = await _context.Db.Queryable<ContainerDetail>()
                    .LeftJoin<DomesticProduct>((cd, p) => cd.ProductCode == p.ProductCode)
                    .Where((cd, p) => !cd.IsDeleted && p.HBProductNo != null && p.HBProductNo.Contains(itemNumber))
                    .Select((cd, p) => cd.ContainerCode)
                    .Distinct()
                    .ToListAsync();

                if (!containerCodes.Any())
                {
                    return new List<YiwuContainerDto>();
                }

                var containers = await _context.Db.Queryable<Container>()
                    .Where(c => c.ContainerCode != null && containerCodes.Contains(c.ContainerCode) && !c.IsDeleted)
                    .OrderByDescending(c => c.LoadingDate)
                    .ToListAsync();

                return _mapper.Map<List<YiwuContainerDto>>(containers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据商品信息查询相关货柜失败, ItemNumber: {ItemNumber}", itemNumber);
                throw;
            }
        }

        /// <summary>
        /// 获取货柜状态选项
        /// </summary>
        public async Task<List<KeyValuePair<int, string>>> GetContainerStatusOptionsAsync()
        {
            await Task.CompletedTask; // 异步接口，但这里是静态数据

            return new List<KeyValuePair<int, string>>
            {
                new KeyValuePair<int, string>(0, "草稿"),
                new KeyValuePair<int, string>(1, "已确认"),
                new KeyValuePair<int, string>(2, "已装柜"),
                new KeyValuePair<int, string>(3, "运输中"),
                new KeyValuePair<int, string>(4, "已到港"),
                new KeyValuePair<int, string>(5, "已清关"),
                new KeyValuePair<int, string>(6, "已完成"),
                new KeyValuePair<int, string>(7, "已取消")
            };
        }

        /// <summary>
        /// 验证货柜明细数据
        /// </summary>
        public async Task<(bool IsValid, List<string> Errors)> ValidateContainerDetailsAsync(List<BatchYiwuContainerDetailItem> details)
        {
            var errors = new List<string>();

            if (!details.Any())
            {
                errors.Add("明细列表不能为空");
                return (false, errors);
            }

            var itemNumbers = details.Select(d => d.ItemNumber).Distinct().ToList();
            var existingProducts = await _context.Db.Queryable<DomesticProduct>()
                .Where(p => p.HBProductNo != null && itemNumbers.Contains(p.HBProductNo) && !p.IsDeleted)
                .Select(p => p.HBProductNo)
                .ToListAsync();

            foreach (var detail in details)
            {
                if (string.IsNullOrWhiteSpace(detail.ItemNumber))
                {
                    errors.Add("货号不能为空");
                    continue;
                }

                if (!existingProducts.Contains(detail.ItemNumber))
                {
                    errors.Add($"货号 {detail.ItemNumber} 对应的商品不存在");
                    continue;
                }

                if (detail.LoadingPieces <= 0)
                {
                    errors.Add($"货号 {detail.ItemNumber} 的装柜件数必须大于0");
                }
            }

            return (errors.Count == 0, errors);
        }

        /// <summary>
        /// 批量翻译商品名称
        /// </summary>
        public async Task<Dictionary<string, string>> BatchTranslateProductNamesAsync(List<string> chineseNames)
        {
            try
            {
                if (chineseNames == null || !chineseNames.Any())
                {
                    return new Dictionary<string, string>();
                }

                // 调用翻译服务
                var translationResult = await _translationService.BatchTranslateToEnglishAsync(chineseNames);

                _logger.LogInformation("批量翻译商品名称完成，翻译 {Count} 个名称", translationResult.Count);
                return translationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量翻译商品名称失败");
                // 返回空字典，让前端显示错误信息
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// 批量更新国内商品信息
        /// </summary>
        public async Task<BatchOperationResponse> BatchUpdateDomesticProductsAsync(List<DomesticProductDto> products)
        {
            var response = new BatchOperationResponse();

            try
            {
                if (products == null || !products.Any())
                {
                    response.Success = false;
                    response.Message = "没有提供需要更新的商品信息";
                    return response;
                }

                _logger.LogInformation("开始批量更新 {Count} 个国内商品信息", products.Count);

                var failedCount = 0;
                var errors = new List<string>();
                var productCodes = products
                    .Select(product => product.ProductCode)
                    .Where(productCode => !string.IsNullOrWhiteSpace(productCode))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var existingProducts = productCodes.Count == 0
                    ? new List<DomesticProduct>()
                    : await _context.Db.Queryable<DomesticProduct>()
                        .Where(product => productCodes.Contains(product.ProductCode))
                        .ToListAsync();
                var productMap = existingProducts
                    .GroupBy(product => product.ProductCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var validProducts = new List<(DomesticProductDto Request, DomesticProduct Product)>();
                var seenProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var productDto in products)
                {
                    // 参数错误保持逐条收集并继续，只有有效批次的写入错误才要求整体回滚。
                    if (string.IsNullOrWhiteSpace(productDto.ProductCode))
                    {
                        errors.Add("商品编码不能为空");
                        failedCount++;
                        continue;
                    }

                    if (!seenProductCodes.Add(productDto.ProductCode))
                    {
                        errors.Add($"商品 {productDto.ProductCode} 在本批次内重复");
                        failedCount++;
                        continue;
                    }

                    if (!productMap.TryGetValue(productDto.ProductCode, out var existingProduct))
                    {
                        errors.Add($"商品 {productDto.ProductCode} 不存在");
                        failedCount++;
                        continue;
                    }

                    validProducts.Add((productDto, existingProduct));
                }

                if (validProducts.Count == 0)
                {
                    response.Success = false;
                    response.SuccessCount = 0;
                    response.FailedCount = failedCount;
                    response.Errors = errors;
                    response.Message = $"成功更新 0 个商品，失败 {failedCount} 个";
                    return response;
                }

                var validProductCodes = validProducts
                    .Select(item => item.Product.ProductCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var auditBatchGuid = Guid.NewGuid();
                var auditActorGuid = _currentUserService.GetCurrentUserGuid();
                var auditActorName = _currentUserService.GetCurrentUsername();

                await _context.Db.Ado.BeginTranAsync();
                try
                {
                    // 统一前快照、商品写入和历史插入的事务边界，历史失败时商品也必须回滚。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(validProductCodes);
                    foreach (var (productDto, existingProduct) in validProducts)
                    {
                        existingProduct.ProductName = productDto.ProductName;
                        existingProduct.EnglishProductName = productDto.EnglishProductName;
                        existingProduct.OEMPrice = productDto.OEMPrice;
                        existingProduct.ImportPrice = productDto.ImportPrice;
                        existingProduct.UpdatedAt = DateTime.UtcNow;

                        await _context.Db.Updateable(existingProduct)
                            .UpdateColumns(product => new
                            {
                                product.ProductName,
                                product.EnglishProductName,
                                product.OEMPrice,
                                product.ImportPrice,
                                product.UpdatedAt,
                            })
                            .Where(product => product.ProductCode == existingProduct.ProductCode)
                            .ExecuteCommandAsync();
                    }

                    var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(validProductCodes);
                    await _changeHistoryService.RecordChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        new WarehouseProductChangeHistoryContextDto
                        {
                            Action = "BatchUpdate",
                            Source = "YiwuContainerBatch",
                            SourceReference = $"Batch:{auditBatchGuid:N}",
                            BatchGuid = auditBatchGuid,
                            ActorUserGuid = auditActorGuid,
                            ActorName = auditActorName,
                        }
                    );
                    await _context.Db.Ado.CommitTranAsync();
                }
                catch (Exception ex)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "批量更新国内商品信息的有效批次失败，已回滚");
                    response.Success = false;
                    response.SuccessCount = 0;
                    response.FailedCount = failedCount + validProducts.Count;
                    response.Errors = errors.Append($"有效商品批量更新失败: {ex.Message}").ToList();
                    response.Message = $"成功更新 0 个商品，失败 {response.FailedCount} 个";
                    return response;
                }

                response.Success = true;
                response.SuccessCount = validProducts.Count;
                response.FailedCount = failedCount;
                response.Errors = errors;
                response.Message = $"成功更新 {response.SuccessCount} 个商品，失败 {failedCount} 个";

                _logger.LogInformation("批量更新国内商品信息完成: 成功 {SuccessCount}，失败 {FailedCount}", response.SuccessCount, failedCount);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新国内商品信息时发生异常");
                response.Success = false;
                response.Message = $"批量更新失败: {ex.Message}";
                response.Errors = new List<string> { ex.Message };
                return response;
            }
        }

        #endregion

        #region 导出功能

        /// <summary>
        /// 导出货柜明细到Excel
        /// </summary>
        public async Task<ApiResponse<FileExportResponse>> ExportContainerDetailsToExcelAsync(ContainerDetailsExportRequest request)
        {
            try
            {
                // 获取货柜信息
                var container = await GetContainerAsync(request.ContainerCode);
                if (container == null)
                {
                    return new ApiResponse<FileExportResponse>
                    {
                        Success = false,
                        Message = "货柜不存在"
                    };
                }

                // 导出文件默认遮罩货柜编号（只露后 4 位），有「查看完整货柜编号」权限时由控制器放开。
                container.ContainerNumber = ContainerNumberMasking.Apply(
                    container.ContainerNumber,
                    request.ShowFullContainerNumber
                );

                // 获取明细数据
                var allDetails = await GetContainerDetailsAsync(request.ContainerCode);
                var detailsToExport = request.Details.Any()
                    ? allDetails.Where(d => request.Details.Contains(d.DetailCode)).ToList()
                    : allDetails;

                if (!detailsToExport.Any())
                {
                    return new ApiResponse<FileExportResponse>
                    {
                        Success = false,
                        Message = "没有找到要导出的明细数据"
                    };
                }

                // 生成Excel文件
                var excelBytes = await _exportService.GenerateExcelFileAsync(container, detailsToExport, request.ExportColumns);
                var base64Content = Convert.ToBase64String(excelBytes);

                return new ApiResponse<FileExportResponse>
                {
                    Success = true,
                    Data = new FileExportResponse
                    {
                        FileContent = base64Content,
                        ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        FileName = $"货柜明细_{ContainerNumberMasking.ToFileNamePart(container.ContainerNumber)}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                        FileSize = excelBytes.Length
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出Excel失败");
                return new ApiResponse<FileExportResponse>
                {
                    Success = false,
                    Message = $"导出失败: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 导出货柜明细到PDF
        /// </summary>
        public async Task<ApiResponse<FileExportResponse>> ExportContainerDetailsToPdfAsync(ContainerDetailsExportRequest request)
        {
            try
            {
                // 获取货柜信息
                var container = await GetContainerAsync(request.ContainerCode);
                if (container == null)
                {
                    return new ApiResponse<FileExportResponse>
                    {
                        Success = false,
                        Message = "货柜不存在"
                    };
                }

                // 导出文件默认遮罩货柜编号（只露后 4 位），有「查看完整货柜编号」权限时由控制器放开。
                container.ContainerNumber = ContainerNumberMasking.Apply(
                    container.ContainerNumber,
                    request.ShowFullContainerNumber
                );

                // 获取明细数据
                var allDetails = await GetContainerDetailsAsync(request.ContainerCode);
                var detailsToExport = request.Details.Any()
                    ? allDetails.Where(d => request.Details.Contains(d.DetailCode)).ToList()
                    : allDetails;

                if (!detailsToExport.Any())
                {
                    return new ApiResponse<FileExportResponse>
                    {
                        Success = false,
                        Message = "没有找到要导出的明细数据"
                    };
                }

                // 生成PDF文件
                var pdfBytes = await _exportService.GeneratePdfFileAsync(container, detailsToExport, request.ExportColumns);
                var base64Content = Convert.ToBase64String(pdfBytes);

                return new ApiResponse<FileExportResponse>
                {
                    Success = true,
                    Data = new FileExportResponse
                    {
                        FileContent = base64Content,
                        ContentType = "application/pdf",
                        FileName = $"货柜明细_{ContainerNumberMasking.ToFileNamePart(container.ContainerNumber)}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                        FileSize = pdfBytes.Length
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出PDF失败");
                return new ApiResponse<FileExportResponse>
                {
                    Success = false,
                    Message = $"导出失败: {ex.Message}"
                };
            }
        }

        #endregion
    }
}
