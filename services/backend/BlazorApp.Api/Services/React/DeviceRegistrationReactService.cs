using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 设备注册 React 服务实现
    /// </summary>
    public class DeviceRegistrationReactService : IDeviceRegistrationReactService
    {
        private static readonly HashSet<string> AllowedDeviceTypes =
            new(StringComparer.OrdinalIgnoreCase) { "PDA", "Mobile", "POS", "Admin" };
        private static readonly HashSet<string> AllowedDeviceSystems =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Android",
                "iOS",
                DeviceRegistrationDeviceSystems.IpadOs,
                DeviceRegistrationDeviceSystems.AndroidTablet,
                "Mac",
                DeviceRegistrationDeviceSystems.Windows,
            };

        private readonly POSMSqlSugarContext _posmDb;
        private readonly SqlSugarContext _mainDb;
        private readonly ILogger<DeviceRegistrationReactService> _logger;

        public DeviceRegistrationReactService(
            POSMSqlSugarContext posmDb,
            SqlSugarContext mainDb,
            ILogger<DeviceRegistrationReactService> logger
        )
        {
            _posmDb = posmDb;
            _mainDb = mainDb;
            _logger = logger;
        }

        /// <summary>
        /// 获取设备网格数据
        /// </summary>
        public async Task<GridResponseDto<DeviceRegistrationListDto>> GetGridDataAsync(
            GridRequestDto request
        )
        {
            try
            {
                var pageIndex = (request.StartRow / request.PageSize) + 1;
                var pageSize = request.PageSize;

                var baseQuery = _posmDb.Db.Queryable<POSM_设备注册信息表>();

                if (request.FilterModel != null && request.FilterModel.Any())
                {
                    foreach (var kv in request.FilterModel)
                    {
                        var col = kv.Key;
                        var f = kv.Value;
                        if (f == null || f.FilterType == null)
                            continue;
                        var type = f.FilterType.ToLower();
                        if (type == "text" && f.Filter != null)
                        {
                            var v = f.Filter?.ToString()?.Trim();
                            if (string.IsNullOrEmpty(v))
                                continue;
                            var op = (f.Type ?? "contains").ToLower();
                            switch (col)
                            {
                                case "设备硬件识别码":
                                    if (op == "equals")
                                        baseQuery = baseQuery.Where(d => d.设备硬件识别码 == v);
                                    else if (op == "contains")
                                        baseQuery = baseQuery.Where(d =>
                                            d.设备硬件识别码.Contains(v)
                                        );
                                    break;
                                case "系统设备编号":
                                    if (op == "equals")
                                        baseQuery = baseQuery.Where(d => d.系统设备编号 == v);
                                    else if (op == "contains")
                                        baseQuery = baseQuery.Where(d =>
                                            d.系统设备编号.Contains(v)
                                        );
                                    break;
                                case "分店代码":
                                    if (op == "equals")
                                        baseQuery = baseQuery.Where(d => d.分店代码 == v);
                                    else if (op == "contains")
                                        baseQuery = baseQuery.Where(d =>
                                            d.分店代码 != null && d.分店代码.Contains(v)
                                        );
                                    break;
                                case "设备类型":
                                    if (op == "equals")
                                        baseQuery = baseQuery.Where(d => d.设备类型 == v);
                                    break;
                                case "设备系统":
                                    if (op == "equals")
                                    {
                                        if (
                                            string.Equals(
                                                v,
                                                DeviceRegistrationDeviceSystems.Windows,
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        )
                                        {
                                            baseQuery = baseQuery.Where(d =>
                                                d.设备系统 == null
                                                || d.设备系统.Trim() == string.Empty
                                                || d.设备系统.Trim()
                                                    == DeviceRegistrationDeviceSystems.Windows
                                            );
                                        }
                                        else if (
                                            string.Equals(
                                                v,
                                                DeviceRegistrationDeviceSystems.Other,
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        )
                                        {
                                            baseQuery = baseQuery.Where(d =>
                                                d.设备系统 != null
                                                && d.设备系统.Trim() != string.Empty
                                                && d.设备系统.Trim()
                                                    != DeviceRegistrationDeviceSystems.Windows
                                                && d.设备系统.Trim()
                                                    != DeviceRegistrationDeviceSystems.IpadOs
                                                // 安卓平板有独立筛选项，不再归入「其他」。
                                                && d.设备系统.Trim()
                                                    != DeviceRegistrationDeviceSystems.AndroidTablet
                                            );
                                        }
                                        else
                                        {
                                            baseQuery = baseQuery.Where(d =>
                                                d.设备系统 != null
                                                && d.设备系统.Trim() == v
                                            );
                                        }
                                    }
                                    break;
                                case "设备状态":
                                    if (int.TryParse(v, out var statusValue))
                                        baseQuery = baseQuery.Where(d => d.设备状态 == statusValue);
                                    break;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
                {
                    var keyword = request.GlobalSearch.Trim();
                    baseQuery = baseQuery.Where(d =>
                        d.设备硬件识别码.Contains(keyword)
                        || d.系统设备编号.Contains(keyword)
                        || (d.分店代码 != null && d.分店代码.Contains(keyword))
                        || d.设备类型.Contains(keyword)
                        || d.设备系统.Contains(keyword)
                        || (d.备注 != null && d.备注.Contains(keyword))
                    );
                }

                var totalCount = await baseQuery.CountAsync();

                if (request.SortModel != null && request.SortModel.Any())
                {
                    var sort = request.SortModel.First();
                    var sortField = sort.ColId;
                    var sortDirection = sort.Sort?.ToLower() == "desc";
                    switch (sortField)
                    {
                        case "设备硬件识别码":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.设备硬件识别码)
                                : baseQuery.OrderBy(d => d.设备硬件识别码);
                            break;
                        case "系统设备编号":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.系统设备编号)
                                : baseQuery.OrderBy(d => d.系统设备编号);
                            break;
                        case "分店代码":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.分店代码)
                                : baseQuery.OrderBy(d => d.分店代码);
                            break;
                        case "设备类型":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.设备类型)
                                : baseQuery.OrderBy(d => d.设备类型);
                            break;
                        case "设备系统":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.设备系统)
                                : baseQuery.OrderBy(d => d.设备系统);
                            break;
                        case "设备状态":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.设备状态)
                                : baseQuery.OrderBy(d => d.设备状态);
                            break;
                        case "创建时间":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.创建时间)
                                : baseQuery.OrderBy(d => d.创建时间);
                            break;
                        case "最后修改时间":
                            baseQuery = sortDirection
                                ? baseQuery.OrderByDescending(d => d.最后修改时间)
                                : baseQuery.OrderBy(d => d.最后修改时间);
                            break;
                        default:
                            baseQuery = baseQuery.OrderByDescending(d => d.创建时间);
                            break;
                    }
                }
                else
                {
                    baseQuery = baseQuery.OrderByDescending(d => d.创建时间);
                }

                var items = await baseQuery
                    .Skip((pageIndex - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var resultList = items
                    .Select(d => new DeviceRegistrationListDto
                    {
                        ID = d.ID,
                        设备硬件识别码 = d.设备硬件识别码,
                        系统设备编号 = d.系统设备编号,
                        分店代码 = d.分店代码,
                        设备类型 = d.设备类型,
                        设备系统 = DeviceRegistrationDeviceSystems.NormalizeForDisplay(d.设备系统),
                        设备状态 = d.设备状态,
                        设备状态描述 = GetStatusDescription(d.设备状态),
                        是否允许交易 = d.是否允许交易,
                        备注 = d.备注,
                        创建时间 = d.创建时间,
                        最后修改时间 = d.最后修改时间,
                        最后修改人 = d.最后修改人,
                    })
                    .ToList();

                var storeCodes = resultList
                    .Where(r => !string.IsNullOrEmpty(r.分店代码))
                    .Select(r => r.分店代码)
                    .Distinct()
                    .ToList();

                if (storeCodes.Any())
                {
                    var stores = await _mainDb
                        .Db.Queryable<Store>()
                        .Where(s => storeCodes.Contains(s.StoreCode))
                        .ToListAsync();
                    var storeDict = stores.ToDictionary(s => s.StoreCode, s => s.StoreName);
                    foreach (var item in resultList)
                    {
                        if (
                            !string.IsNullOrEmpty(item.分店代码)
                            && storeDict.TryGetValue(item.分店代码, out var storeName)
                        )
                        {
                            item.分店名称 = storeName;
                        }
                    }
                }

                return GridResponseDto<DeviceRegistrationListDto>.OK(resultList, totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备网格数据失败");
                return GridResponseDto<DeviceRegistrationListDto>.Error(ex.Message);
            }
        }

        /// <summary>
        /// 根据 ID 获取设备详情
        /// </summary>
        public async Task<ApiResponse<DeviceRegistrationDetailDto>> GetByIdAsync(int id)
        {
            try
            {
                var entity = await _posmDb
                    .Db.Queryable<POSM_设备注册信息表>()
                    .Where(d => d.ID == id)
                    .FirstAsync();

                if (entity == null)
                {
                    return ApiResponse<DeviceRegistrationDetailDto>.Error("设备不存在");
                }

                string? storeName = null;
                if (!string.IsNullOrEmpty(entity.分店代码))
                {
                    var store = await _mainDb
                        .Db.Queryable<Store>()
                        .Where(s => s.StoreCode == entity.分店代码)
                        .FirstAsync();
                    storeName = store?.StoreName;
                }

                var result = new DeviceRegistrationDetailDto
                {
                    ID = entity.ID,
                    设备硬件识别码 = entity.设备硬件识别码,
                    系统设备编号 = entity.系统设备编号,
                    分店代码 = entity.分店代码,
                    分店名称 = storeName,
                    设备类型 = entity.设备类型,
                    设备系统 = DeviceRegistrationDeviceSystems.NormalizeForDisplay(entity.设备系统),
                    设备状态 = entity.设备状态,
                    设备状态描述 = GetStatusDescription(entity.设备状态),
                    是否允许交易 = entity.是否允许交易,
                    备注 = entity.备注,
                    创建时间 = entity.创建时间,
                    最后修改时间 = entity.最后修改时间,
                    最后修改人 = entity.最后修改人,
                    创建人 = entity.创建人,
                };

                return ApiResponse<DeviceRegistrationDetailDto>.OK(result, "获取成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备详情失败，ID: {ID}", id);
                return ApiResponse<DeviceRegistrationDetailDto>.Error("获取详情失败");
            }
        }

        /// <summary>
        /// 更新设备信息
        /// </summary>
        public async Task<ApiResponse<DeviceRegistrationDetailDto>> UpdateAsync(
            int id,
            UpdateDeviceRegistrationDto dto,
            string updatedBy
        )
        {
            try
            {
                var entity = await _posmDb
                    .Db.Queryable<POSM_设备注册信息表>()
                    .Where(d => d.ID == id)
                    .FirstAsync();

                if (entity == null)
                {
                    return ApiResponse<DeviceRegistrationDetailDto>.Error("设备不存在");
                }

                var requestedDeviceType = dto.设备类型?.Trim();
                if (
                    !string.IsNullOrWhiteSpace(requestedDeviceType)
                    && !AllowedDeviceTypes.Contains(requestedDeviceType)
                )
                {
                    return ApiResponse<DeviceRegistrationDetailDto>.Error("设备类型无效");
                }
                var requestedDeviceSystem = dto.设备系统?.Trim();
                if (
                    !string.IsNullOrWhiteSpace(requestedDeviceSystem)
                    && !AllowedDeviceSystems.Contains(requestedDeviceSystem)
                )
                {
                    return ApiResponse<DeviceRegistrationDetailDto>.Error("设备系统无效");
                }
                var currentDeviceSystem =
                    DeviceRegistrationDeviceSystems.NormalizeForDisplay(entity.设备系统);
                var currentDeviceType = entity.设备类型?.Trim() ?? string.Empty;
                var effectiveRequestedDeviceType = string.IsNullOrWhiteSpace(requestedDeviceType)
                    ? currentDeviceType
                    : requestedDeviceType;
                // 历史 iPad 曾写入 iOS；该等价值只允许单向规范化，不能借此切换认证平台。
                var isLegacyIosCanonicalization =
                    entity.设备状态 == (int)DeviceStatus.启用
                    && string.Equals(currentDeviceType, "POS", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        effectiveRequestedDeviceType,
                        "POS",
                        StringComparison.OrdinalIgnoreCase
                    )
                    && string.Equals(currentDeviceSystem, "iOS", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        requestedDeviceSystem,
                        DeviceRegistrationDeviceSystems.IpadOs,
                        StringComparison.OrdinalIgnoreCase
                    );
                if (
                    !string.IsNullOrWhiteSpace(requestedDeviceSystem)
                    && !string.Equals(
                        currentDeviceSystem,
                        requestedDeviceSystem,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && entity.设备状态 != (int)DeviceStatus.待确认
                    && !isLegacyIosCanonicalization
                )
                {
                    // 关键逻辑：平台决定设备认证强度；获批后禁止保留授权码跨平台改写。
                    return ApiResponse<DeviceRegistrationDetailDto>.Error(
                        "仅待确认设备或已启用的旧 iPad POS 可修改设备系统"
                    );
                }

                if (!string.IsNullOrWhiteSpace(requestedDeviceType))
                {
                    entity.设备类型 = requestedDeviceType;
                }
                if (!string.IsNullOrWhiteSpace(requestedDeviceSystem))
                {
                    entity.设备系统 = requestedDeviceSystem;
                }
                if (dto.是否允许交易.HasValue)
                {
                    entity.是否允许交易 = dto.是否允许交易.Value;
                }
                entity.备注 = dto.备注 ?? string.Empty;
                entity.最后修改人 = updatedBy;
                entity.最后修改时间 = DateTime.Now;

                await _posmDb.Db.Updateable(entity).ExecuteCommandAsync();

                string? storeName = null;
                if (!string.IsNullOrEmpty(entity.分店代码))
                {
                    var store = await _mainDb
                        .Db.Queryable<Store>()
                        .Where(s => s.StoreCode == entity.分店代码)
                        .FirstAsync();
                    storeName = store?.StoreName;
                }

                var result = new DeviceRegistrationDetailDto
                {
                    ID = entity.ID,
                    设备硬件识别码 = entity.设备硬件识别码,
                    系统设备编号 = entity.系统设备编号,
                    分店代码 = entity.分店代码,
                    分店名称 = storeName,
                    设备类型 = entity.设备类型 ?? string.Empty,
                    设备系统 = DeviceRegistrationDeviceSystems.NormalizeForDisplay(entity.设备系统),
                    设备状态 = entity.设备状态,
                    设备状态描述 = GetStatusDescription(entity.设备状态),
                    是否允许交易 = entity.是否允许交易,
                    备注 = entity.备注,
                    创建时间 = entity.创建时间,
                    最后修改时间 = entity.最后修改时间,
                    最后修改人 = entity.最后修改人,
                    创建人 = entity.创建人,
                };

                return ApiResponse<DeviceRegistrationDetailDto>.OK(result, "更新成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新设备失败，ID: {ID}", id);
                return ApiResponse<DeviceRegistrationDetailDto>.Error("更新失败");
            }
        }

        private string GetStatusDescription(int status)
        {
            return status switch
            {
                -1 => "待确认",
                0 => "禁用",
                1 => "启用",
                2 => "锁定",
                3 => "未注册",
                _ => "未知状态",
            };
        }
    }
}
