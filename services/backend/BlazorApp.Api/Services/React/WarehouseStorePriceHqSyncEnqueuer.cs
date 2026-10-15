using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

public sealed class WarehouseStorePriceHqSyncEnqueuer : IWarehouseStorePriceHqSyncEnqueuer
{
    private readonly IProductMaintenanceHqProjectionWriter _writer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IConfiguration _configuration;

    public WarehouseStorePriceHqSyncEnqueuer(
        IProductMaintenanceHqProjectionWriter writer,
        ICurrentUserService currentUserService,
        IConfiguration configuration
    )
    {
        _writer = writer;
        _currentUserService = currentUserService;
        _configuration = configuration;
    }

    /// <summary>
    /// 与价格更新通知页共用同一个开关：后期停用 HQ 同步时只改配置，仓库改价与通知页改价一起停。
    /// </summary>
    private bool IsHqSyncEnabled =>
        _configuration.GetValue("StorePriceUpdateTasks:SyncToHq", true);

    public async Task<ProductHqSyncOperationStatusDto?> EnqueueAsync(
        ISqlSugarClient transactionDb,
        string productCode,
        IEnumerable<string?> storeCodes,
        IReadOnlyCollection<string> fieldMask,
        string updatedBy,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(transactionDb);
        if (!IsHqSyncEnabled || string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        var candidates = storeCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        // HQ 只有启用分店；HBweb 里停用的 20xx 系列分店不在 HQ 分店表中。
        // 写 HQ 的执行器遇到 HQ 没有的分店会把整条任务判为 Blocked（所有分店都写不进去），
        // 所以入队时就只带启用分店。
        var activeStoreCodes = (
            await transactionDb
                .Queryable<Store>()
                .Where(store =>
                    store.IsActive && !store.IsDeleted && candidates.Contains(store.StoreCode)
                )
                .Select(store => store.StoreCode)
                .ToListAsync(cancellationToken)
        )
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToList();
        if (activeStoreCodes.Count == 0)
        {
            return null;
        }

        // outbox 要求「用户」与「设备」二选一：后台任务等没有登录用户的入口用系统标识兜底。
        var userGuid = _currentUserService.GetCurrentUserGuid();
        var hasUser = !string.IsNullOrWhiteSpace(userGuid);

        // 入队失败（ProductMaintenanceHqEnqueueException）不吞掉：它跑在调用方事务内，
        // 抛出后价格改写一并回滚，避免 HBweb 改了价而 HQ 永远收不到。
        return await _writer.EnqueueAsync(
            transactionDb,
            new ProductMaintenanceHqMutationRequest
            {
                OperationKind = ProductMaintenanceHqOperationKinds.StorePriceUpdated,
                ProductCode = productCode.Trim(),
                TargetStoreCodes = activeStoreCodes,
                AuthorizedStoreCodes = activeStoreCodes,
                FieldMask = fieldMask,
                RequestedByUserGuid = hasUser ? userGuid : null,
                RequestedByDeviceId = hasUser ? null : $"system:{updatedBy}",
                Source = source,
                OccurredAtUtc = DateTime.UtcNow,
            },
            cancellationToken
        );
    }
}
