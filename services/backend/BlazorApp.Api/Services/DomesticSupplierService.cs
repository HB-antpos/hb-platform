using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 国内供应商服务实现
    /// </summary>
    public class DomesticSupplierService : IDomesticSupplierService
    {
        private readonly SqlSugarContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<DomesticSupplierService> _logger;

        public DomesticSupplierService(
            SqlSugarContext context,
            IMapper mapper,
            ILogger<DomesticSupplierService> logger
        )
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>
        /// 获取分页供应商列表
        /// </summary>
        public async Task<PagedResult<DomesticSupplierDto>> GetSuppliersAsync(
            DomesticSupplierQueryDto query
        )
        {
            try
            {
                var queryable = _context.ChinaSupplierDb.AsQueryable();

                // 没有「查看国内供应商名称」权限的账号只能按代码搜索和排序，不能拿名称片段去试探。
                var allowNameSearch = DomesticSupplierInfoMasking.IsNameSearchAllowed;

                // 关键词搜索
                if (!string.IsNullOrWhiteSpace(query.Keyword))
                {
                    queryable = allowNameSearch
                        ? queryable.Where(s =>
                            (s.SupplierCode ?? string.Empty).Contains(query.Keyword)
                            || (s.SupplierName ?? string.Empty).Contains(query.Keyword)
                        )
                        : queryable.Where(s => (s.SupplierCode ?? string.Empty).Contains(query.Keyword));
                }

                // 状态筛选
                if (query.Status.HasValue)
                {
                    queryable = queryable.Where(s => s.Status == query.Status.Value);
                }

                // 排序
                var sortField = query.SortField ?? "CreatedAt";
                if (!allowNameSearch && sortField.Equals("suppliername", StringComparison.OrdinalIgnoreCase))
                {
                    sortField = "suppliercode";
                }
                var sortDirection =
                    query.SortDirection?.ToLower() == "asc" ? OrderByType.Asc : OrderByType.Desc;

                queryable = sortField.ToLower() switch
                {
                    "suppliercode" => queryable.OrderBy(s => s.SupplierCode, sortDirection),
                    "suppliername" => queryable.OrderBy(s => s.SupplierName, sortDirection),
                    "createdat" => queryable.OrderBy(s => s.CreatedAt, sortDirection),
                    "updatedat" => queryable.OrderBy(s => s.UpdatedAt, sortDirection),
                    _ => queryable.OrderBy(s => s.CreatedAt, sortDirection),
                };

                // 分页
                var total = await queryable.CountAsync();
                var suppliers = await queryable
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToListAsync();

                var supplierDtos = _mapper.Map<List<DomesticSupplierDto>>(suppliers);

                return new PagedResult<DomesticSupplierDto>
                {
                    Items = supplierDtos,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取供应商列表时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 根据GUID获取供应商详情
        /// </summary>
        public async Task<DomesticSupplierDto?> GetSupplierByGuidAsync(string guid)
        {
            try
            {
                var supplier = await _context
                    .ChinaSupplierDb.AsQueryable()
                    .Where(s => s.Guid == guid)
                    .FirstAsync();

                return supplier != null ? _mapper.Map<DomesticSupplierDto>(supplier) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据GUID获取供应商详情时发生错误: {Guid}", guid);
                throw;
            }
        }

        /// <summary>
        /// 根据供应商编码获取供应商详情
        /// </summary>
        public async Task<DomesticSupplierDto?> GetSupplierByCodeAsync(string supplierCode)
        {
            try
            {
                var supplier = await _context
                    .ChinaSupplierDb.AsQueryable()
                    .Where(s => s.SupplierCode == supplierCode)
                    .FirstAsync();

                return supplier != null ? _mapper.Map<DomesticSupplierDto>(supplier) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "根据编码获取供应商详情时发生错误: {SupplierCode}",
                    supplierCode
                );
                throw;
            }
        }

        /// <summary>
        /// 创建新供应商
        /// </summary>
        public async Task<DomesticSupplierDto> CreateSupplierAsync(
            CreateDomesticSupplierDto dto,
            string currentUser
        )
        {
            try
            {
                // 检查供应商编码是否已存在
                if (await IsSupplierCodeExistsAsync(dto.SupplierCode))
                {
                    throw new InvalidOperationException($"供应商编码 {dto.SupplierCode} 已存在");
                }

                var supplier = _mapper.Map<ChinaSupplier>(dto);
                supplier.Guid = Guid.NewGuid().ToString();
                supplier.FGC_Creator = currentUser;
                supplier.FGC_LastModifier = currentUser;
                supplier.FGC_CreateDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                supplier.FGC_LastModifyDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                await _context.ChinaSupplierDb.InsertAsync(supplier);

                _logger.LogInformation(
                    "创建供应商成功: {SupplierCode} - {SupplierName}",
                    supplier.SupplierCode,
                    supplier.SupplierName
                );

                return _mapper.Map<DomesticSupplierDto>(supplier);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建供应商时发生错误: {SupplierCode}", dto.SupplierCode);
                throw;
            }
        }

        /// <summary>
        /// 更新供应商信息
        /// </summary>
        public async Task<DomesticSupplierDto?> UpdateSupplierAsync(
            string guid,
            UpdateDomesticSupplierDto dto,
            string currentUser
        )
        {
            try
            {
                var supplier = await _context
                    .ChinaSupplierDb.AsQueryable()
                    .Where(s => s.Guid == guid)
                    .FirstAsync();

                if (supplier == null)
                {
                    return null;
                }

                // 检查供应商编码是否已存在（排除当前供应商）
                if (
                    supplier.SupplierCode != dto.SupplierCode
                    && await IsSupplierCodeExistsAsync(dto.SupplierCode, guid)
                )
                {
                    throw new InvalidOperationException($"供应商编码 {dto.SupplierCode} 已存在");
                }

                // 更新字段
                supplier.SupplierCode = dto.SupplierCode;
                supplier.SupplierName = dto.SupplierName;
                supplier.ShopNumber = dto.ShopNumber;
                supplier.ContactPerson = dto.ContactPerson;
                supplier.Phone = dto.Phone;
                supplier.Email = dto.Email;
                supplier.Remarks = dto.Remarks;
                supplier.Status = dto.Status;
                supplier.FGC_LastModifier = currentUser;
                supplier.FGC_LastModifyDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                await _context.ChinaSupplierDb.UpdateAsync(supplier);

                _logger.LogInformation(
                    "更新供应商成功: {SupplierCode} - {SupplierName}",
                    supplier.SupplierCode,
                    supplier.SupplierName
                );

                return _mapper.Map<DomesticSupplierDto>(supplier);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新供应商时发生错误: {Guid}", guid);
                throw;
            }
        }

        /// <summary>
        /// 删除供应商
        /// </summary>
        public async Task<bool> DeleteSupplierAsync(string guid)
        {
            try
            {
                var result = await _context.ChinaSupplierDb.DeleteAsync(s => s.Guid == guid);

                if (result)
                {
                    _logger.LogInformation("删除供应商成功: {Guid}", guid);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除供应商时发生错误: {Guid}", guid);
                throw;
            }
        }

        /// <summary>
        /// 检查供应商编码是否已存在
        /// </summary>
        public async Task<bool> IsSupplierCodeExistsAsync(
            string supplierCode,
            string? excludeGuid = null
        )
        {
            try
            {
                var queryable = _context
                    .ChinaSupplierDb.AsQueryable()
                    .Where(s => s.SupplierCode == supplierCode);

                if (!string.IsNullOrEmpty(excludeGuid))
                {
                    queryable = queryable.Where(s => s.Guid != excludeGuid);
                }

                return await queryable.AnyAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查供应商编码是否存在时发生错误: {SupplierCode}",
                    supplierCode
                );
                throw;
            }
        }

        /// <summary>
        /// 生成下一个可用的供应商编码（HB+3位序号）
        /// </summary>
        public async Task<string> GenerateNextSupplierCodeAsync()
        {
            try
            {
                // 获取所有HB开头的供应商编码
                var hbSuppliers = await _context
                    .ChinaSupplierDb.AsQueryable()
                    .Where(s => s.SupplierCode != null && s.SupplierCode.StartsWith("HB"))
                    .Select(s => s.SupplierCode)
                    .ToListAsync();

                var maxNumber = 0;
                foreach (var code in hbSuppliers)
                {
                    if (!string.IsNullOrEmpty(code) && code.Length >= 5 && code.StartsWith("HB"))
                    {
                        var numberPart = code.Substring(2);
                        if (int.TryParse(numberPart, out var number))
                        {
                            maxNumber = Math.Max(maxNumber, number);
                        }
                    }
                }

                var nextNumber = maxNumber + 1;
                var nextCode = $"HB{nextNumber:D3}";

                // 确保生成的编码不存在
                while (await IsSupplierCodeExistsAsync(nextCode))
                {
                    nextNumber++;
                    nextCode = $"HB{nextNumber:D3}";
                }

                return nextCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成供应商编码时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 启用/禁用供应商
        /// </summary>
        public async Task<bool> UpdateSupplierStatusAsync(
            string guid,
            int status,
            string currentUser
        )
        {
            try
            {
                var supplier = await _context
                    .ChinaSupplierDb.AsQueryable()
                    .Where(s => s.Guid == guid)
                    .FirstAsync();

                if (supplier == null)
                {
                    return false;
                }

                supplier.Status = status;
                supplier.FGC_LastModifier = currentUser;
                supplier.FGC_LastModifyDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                var result = await _context.ChinaSupplierDb.UpdateAsync(supplier);

                if (result)
                {
                    _logger.LogInformation("更新供应商状态成功: {Guid} - {Status}", guid, status);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新供应商状态时发生错误: {Guid}", guid);
                throw;
            }
        }

        /// <summary>
        /// 获取所有启用的供应商列表（用于下拉选择）
        /// </summary>
        public async Task<List<DomesticSupplierDto>> GetActiveSupplierListAsync()
        {
            try
            {
                var suppliers = await _context
                    .ChinaSupplierDb.AsQueryable()
                    .Where(s => s.Status == 1)
                    .OrderBy(s => s.SupplierCode)
                    .ToListAsync();

                return _mapper.Map<List<DomesticSupplierDto>>(suppliers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启用供应商列表时发生错误");
                throw;
            }
        }
    }
}
