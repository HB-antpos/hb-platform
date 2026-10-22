using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 下单人员授权（独立权限）：谁能替哪家店下单只认这里，不看人员管理里的可管理分店，也不看是否店长。
/// 每次变更写入 SupplierPreorderAuthLog。
/// </summary>
public sealed partial class SupplierPreorderService
{
    public async Task<SupplierPreorderOrderersOverviewDto> GetOrderersAsync()
    {
        var orderers = await _db.Queryable<SupplierPreorderOrderer>().ToListAsync();
        var ordererStores = await _db.Queryable<SupplierPreorderOrdererStore>().ToListAsync();
        var activeStores = await LoadActiveStoresAsync();
        var userGuids = orderers.Select(o => o.UserGuid).ToList();
        var users = await LoadUsersAsync(userGuids);
        var primaryStores = await LoadPrimaryStoreNamesAsync(userGuids);

        var result = new List<SupplierPreorderOrdererDto>(orderers.Count);
        foreach (var orderer in orderers)
        {
            users.TryGetValue(orderer.UserGuid, out var user);
            result.Add(new SupplierPreorderOrdererDto
            {
                UserGuid = orderer.UserGuid,
                UserName = user?.Username ?? "(已删除的用户)",
                FullName = user?.FullName,
                IsActive = user is { IsActive: true, IsDeleted: false },
                PrimaryStoreName = primaryStores.GetValueOrDefault(orderer.UserGuid),
                CanViewOtherStores = orderer.CanViewOtherStores,
                Stores = ordererStores
                    .Where(s => string.Equals(s.UserGuid, orderer.UserGuid, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.StoreCode)
                    .Select(s => ToStoreRef(s.StoreGuid, s.StoreCode, s.StoreName))
                    .ToList(),
                HasOrderEntryPermission = await HasExactPermissionAsync(orderer.UserGuid, Permissions.SupplierPreorder.Order),
                UpdatedAtUtc = AsUtc(orderer.UpdatedAtUtc)!.Value,
                UpdatedBy = orderer.UpdatedBy,
            });
        }

        var coveredStores = ordererStores.Select(s => s.StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SupplierPreorderOrderersOverviewDto
        {
            Orderers = result.OrderBy(o => o.FullName ?? o.UserName, StringComparer.OrdinalIgnoreCase).ToList(),
            StoresWithoutOrderer = activeStores
                .Where(s => !coveredStores.Contains(s.StoreGUID))
                .Select(s => ToStoreRef(s.StoreGUID, s.StoreCode, s.StoreName))
                .ToList(),
        };
    }

    /// <summary>入口权限只用于提示「看不到菜单」；查询失败按没有处理，不影响授权本身。</summary>
    private async Task<bool> HasExactPermissionAsync(string userGuid, string permission)
    {
        try
        {
            var response = await _roleService.UserHasExactPermissionAsync(userGuid, permission);
            return response.Success && response.Data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询用户入口权限失败：{UserGuid}", userGuid);
            return false;
        }
    }

    public async Task<SupplierPreorderOrdererDto> SaveOrdererAsync(string userGuid, SupplierPreorderSaveOrdererDto request)
    {
        var user = await _db.Queryable<User>().FirstAsync(u => u.UserGUID == userGuid && !u.IsDeleted);
        if (user is null)
        {
            throw new SupplierPreorderException("用户不存在", "USER_NOT_FOUND", 404);
        }

        var activeStores = (await LoadActiveStoresAsync()).ToDictionary(s => s.StoreGUID, StringComparer.OrdinalIgnoreCase);
        var target = (request.StoreGuids ?? new()).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var orderer = await _db.Queryable<SupplierPreorderOrderer>().FirstAsync(o => o.UserGuid == userGuid);
        var existingStores = await _db.Queryable<SupplierPreorderOrdererStore>().Where(s => s.UserGuid == userGuid).ToListAsync();
        var existingGuids = existingStores.Select(s => s.StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 原来就授权、后来分店被停用的允许原样保留（前端编辑时会带回来）；新加的必须是启用分店
        if (target.Any(g => !activeStores.ContainsKey(g) && !existingGuids.Contains(g)))
        {
            throw new SupplierPreorderException("包含不存在或已停用的分店", "STORE_INVALID");
        }
        var toAdd = target.Where(g => !existingGuids.Contains(g)).ToList();
        var toRemove = existingStores.Where(s => !target.Contains(s.StoreGuid, StringComparer.OrdinalIgnoreCase)).ToList();
        var now = NowUtc;
        var isNew = orderer is null;
        var viewChanged = !isNew && orderer!.CanViewOtherStores != request.CanViewOtherStores;

        await RunInTransactionAsync(async () =>
        {
            if (isNew)
            {
                await _db.Insertable(new SupplierPreorderOrderer
                {
                    UserGuid = userGuid,
                    CanViewOtherStores = request.CanViewOtherStores,
                    CreatedAtUtc = now,
                    CreatedBy = CurrentUserName,
                    UpdatedAtUtc = now,
                    UpdatedBy = CurrentUserName,
                }).ExecuteCommandAsync();
            }
            else
            {
                orderer!.CanViewOtherStores = request.CanViewOtherStores;
                orderer.UpdatedAtUtc = now;
                orderer.UpdatedBy = CurrentUserName;
                await _db.Updateable(orderer).ExecuteCommandAsync();
            }

            if (toAdd.Count > 0)
            {
                await _db.Insertable(toAdd.Select(g => new SupplierPreorderOrdererStore
                {
                    UserGuid = userGuid,
                    StoreGuid = activeStores[g].StoreGUID,
                    StoreCode = activeStores[g].StoreCode,
                    StoreName = activeStores[g].StoreName,
                    CreatedAtUtc = now,
                    CreatedBy = CurrentUserName,
                }).ToList()).ExecuteCommandAsync();
            }

            if (toRemove.Count > 0)
            {
                var ids = toRemove.Select(s => s.OrdererStoreGuid).ToList();
                await _db.Deleteable<SupplierPreorderOrdererStore>().Where(s => ids.Contains(s.OrdererStoreGuid)).ExecuteCommandAsync();
            }
        });

        var displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;
        var parts = new List<string>();
        if (isNew) parts.Add("加入下单人员");
        if (toAdd.Count > 0) parts.Add("加分店：" + string.Join("、", toAdd.Select(g => activeStores[g].StoreName)));
        if (toRemove.Count > 0) parts.Add("减分店：" + string.Join("、", toRemove.Select(s => s.StoreName)));
        if (isNew || viewChanged) parts.Add(request.CanViewOtherStores ? "可查看其他分店：开" : "可查看其他分店：关");
        if (parts.Count > 0)
        {
            await WriteAuthLogAsync("Orderer", isNew ? "OrdererAdded" : "OrdererUpdated", string.Join("；", parts), targetUserGuid: userGuid, targetUserName: displayName);
        }

        var overview = await GetOrderersAsync();
        return overview.Orderers.First(o => string.Equals(o.UserGuid, userGuid, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> RemoveOrdererAsync(string userGuid)
    {
        var orderer = await _db.Queryable<SupplierPreorderOrderer>().FirstAsync(o => o.UserGuid == userGuid);
        if (orderer is null)
        {
            throw new SupplierPreorderException("该人员不在下单人员名单里", "ORDERER_NOT_FOUND", 404);
        }

        var stores = await _db.Queryable<SupplierPreorderOrdererStore>().Where(s => s.UserGuid == userGuid).ToListAsync();
        await RunInTransactionAsync(async () =>
        {
            await _db.Deleteable<SupplierPreorderOrdererStore>().Where(s => s.UserGuid == userGuid).ExecuteCommandAsync();
            await _db.Deleteable<SupplierPreorderOrderer>().Where(o => o.UserGuid == userGuid).ExecuteCommandAsync();
        });

        var names = await LoadUserDisplayNamesAsync(new[] { userGuid });
        await WriteAuthLogAsync(
            "Orderer",
            "OrdererRemoved",
            stores.Count > 0 ? "移除；原分店：" + string.Join("、", stores.Select(s => s.StoreName)) : "移除",
            targetUserGuid: userGuid,
            targetUserName: names.GetValueOrDefault(userGuid));
        return true;
    }

    public async Task<List<SupplierPreorderUserCandidateDto>> SearchUserCandidatesAsync(string? keyword)
    {
        var k = keyword?.Trim();
        if (string.IsNullOrEmpty(k))
        {
            return new();
        }

        var users = await _db.Queryable<User>()
            .Where(u => !u.IsDeleted && u.IsActive && (u.Username.Contains(k) || (u.FullName != null && u.FullName.Contains(k)) || u.Email.Contains(k)))
            .OrderBy(u => u.Username)
            .Take(20)
            .Select(u => new { u.UserGUID, u.Username, u.FullName })
            .ToListAsync();
        var guids = users.Select(u => u.UserGUID).ToList();
        var primary = await LoadPrimaryStoreNamesAsync(guids);
        var existing = (await _db.Queryable<SupplierPreorderOrderer>().Where(o => guids.Contains(o.UserGuid)).Select(o => o.UserGuid).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return users.Select(u => new SupplierPreorderUserCandidateDto
        {
            UserGuid = u.UserGUID,
            UserName = u.Username,
            FullName = u.FullName,
            PrimaryStoreName = primary.GetValueOrDefault(u.UserGUID),
            AlreadyOrderer = existing.Contains(u.UserGUID),
        }).ToList();
    }

    public async Task<PagedResult<SupplierPreorderAuthLogDto>> GetAuthLogsAsync(SupplierPreorderAuthLogQueryDto query)
    {
        var size = NormalizePageSize(query.PageSize);
        var page = Math.Max(query.Page, 1);
        var source = _db.Queryable<SupplierPreorderAuthLog>()
            .WhereIF(!string.IsNullOrWhiteSpace(query.UserGuid), l => l.TargetUserGuid == query.UserGuid)
            .WhereIF(!string.IsNullOrWhiteSpace(query.FormGuid), l => l.FormGuid == query.FormGuid);
        var total = await source.CountAsync();
        var rows = await source.OrderByDescending(l => l.CreatedAtUtc).ToPageListAsync(page, size);
        return new PagedResult<SupplierPreorderAuthLogDto>
        {
            Total = total,
            Page = page,
            PageSize = size,
            Items = rows.Select(l => new SupplierPreorderAuthLogDto
            {
                LogGuid = l.LogGuid,
                Scope = l.Scope,
                TargetUserName = l.TargetUserName,
                FormGuid = l.FormGuid,
                Action = l.Action,
                Detail = l.Detail,
                OperatorName = l.OperatorName,
                CreatedAtUtc = AsUtc(l.CreatedAtUtc)!.Value,
            }).ToList(),
        };
    }

    private async Task<Dictionary<string, User>> LoadUsersAsync(IReadOnlyCollection<string> userGuids)
    {
        var result = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in userGuids.Chunk(500))
        {
            var list = chunk.ToList();
            var users = await _db.Queryable<User>()
                .Where(u => list.Contains(u.UserGUID))
                .Select(u => new User { UserGUID = u.UserGUID, Username = u.Username, FullName = u.FullName, IsActive = u.IsActive, IsDeleted = u.IsDeleted })
                .ToListAsync();
            foreach (var user in users)
            {
                result[user.UserGUID] = user;
            }
        }

        return result;
    }

    /// <summary>主分店名称（人员管理里的 IsPrimary），只用于显示参考，不参与授权。</summary>
    private async Task<Dictionary<string, string>> LoadPrimaryStoreNamesAsync(IReadOnlyCollection<string> userGuids)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (userGuids.Count == 0)
        {
            return result;
        }

        var stores = (await _db.Queryable<Store>().Where(s => !s.IsDeleted).Select(s => new { s.StoreGUID, s.StoreName }).ToListAsync())
            .ToDictionary(s => s.StoreGUID, s => s.StoreName, StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in userGuids.Chunk(500))
        {
            var list = chunk.ToList();
            var links = await _db.Queryable<UserStore>()
                .Where(us => !us.IsDeleted && us.IsPrimary && list.Contains(us.UserGUID))
                .Select(us => new { us.UserGUID, us.StoreGUID })
                .ToListAsync();
            foreach (var link in links)
            {
                if (stores.TryGetValue(link.StoreGUID, out var name))
                {
                    result[link.UserGUID] = name;
                }
            }
        }

        return result;
    }
}
