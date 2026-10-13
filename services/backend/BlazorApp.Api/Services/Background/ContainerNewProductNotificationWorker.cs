using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.Background;

/// <summary>
/// 新品到店每日推送的配置（配置节 ContainerNewProductNotifications）。默认值写在这里，不配置也按下面的值运行。
/// </summary>
public sealed class ContainerNewProductNotificationOptions
{
    public const string SectionName = "ContainerNewProductNotifications";

    /// <summary>总开关；关闭后后台任务整轮跳过，不读也不写任何数据。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔（分钟），下限 1。</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>门店本地几点起处理（含），默认 8 点。</summary>
    public int LocalHour { get; set; } = 8;

    /// <summary>门店本地几点起当天不再补发（不含），默认 20 点；起点之后、此刻之前漏掉的（如服务重启、单店失败）会在轮询里补上。</summary>
    public int LatestLocalHour { get; set; } = 20;
}

/// <summary>
/// 新品到店每日推送：每个门店在「本地 8 点」之后推送一条「比昨天新增」的新品通知，只发给该店持有
/// <see cref="Permissions.Container.MobileNewProductsView"/> 的人（店长、仓库经理）。
///
/// 口径与移动端「HB new arrivals」列表一致（<see cref="IContainerNewProductsQuery"/>，includeExisting=false），
/// 「比昨天新增」的基线是每店累计已见过的新品集合（<see cref="ContainerNewProductSeen"/>），不是昨天推送的增量。
/// 每店每个本地日期只处理一次（<see cref="ContainerNewProductRun"/>）；首次启用只登记当前列表、不发通知，避免第一天把存量全推一遍。
/// 通知由 <see cref="INotificationPublisher"/> 发布：必须在任何业务事务之外调用（它自己开事务，嵌套会破坏外层回滚），
/// 所以这里先发布，再单独用一个只包住 Seen / Run 写入的短事务落记录。
///
/// 多实例安全靠数据库兜底而不是租约：同店同日的幂等键（<c>container-new-products:{门店}:{日期}</c>）有唯一索引，
/// Seen / Run 有主键，并发写入冲突时整个事务回滚，下一轮看到对方已写的 Run 就跳过。
/// </summary>
public sealed class ContainerNewProductNotificationWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<ContainerNewProductNotificationOptions> options,
    TimeProvider timeProvider,
    ILogger<ContainerNewProductNotificationWorker> logger
) : BackgroundService
{
    internal const string NotificationTitle = "New products arriving soon";
    internal const string DedupeKeyPrefix = "container-new-products:";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    // Seen 单条 INSERT 控制在 200 行内，避免语句过大（SQL Server 单条语句参数上限 2100，每行 6 列；SqlSugar 自身也会分页，这里是双保险）。
    private const int InsertBatchSize = 200;

    // 与表列宽一致（nvarchar(50)）。商品编码实际是 32～36 位 GUID/UUID，超长只可能是脏数据。
    private const int MaxCodeLength = 50;

    // 状态不明门店每个轮询都会重新判定（地址可能被修正），但日志每店每个本地日期只记一条，避免每 5 分钟刷屏。
    private readonly Dictionary<string, DateTime> _stateUnknownLoggedOn = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后稍等，避开启动迁移与缓存预热的高峰。
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "新品到店每日推送本轮失败，下一轮重试"); }

            var interval = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.IntervalMinutes));
            try { await Task.Delay(interval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>跑一轮：公开给测试直接驱动（配合固定的当前时间）。<paramref name="nowUtc"/> 必须是 UTC。</summary>
    internal async Task RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled)
        {
            return;
        }

        var (startHour, endHour) = ResolveHourRange(settings);
        nowUtc = nowUtc.Kind == DateTimeKind.Local
            ? nowUtc.ToUniversalTime()
            : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        List<Store> stores;
        using (var scope = scopes.CreateScope())
        {
            // 只认未删除且启用的门店（与改价任务、设备激活码等按店业务同一口径）。
            stores = await scope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db.Queryable<Store>()
                .Where(store => !store.IsDeleted && store.IsActive)
                .ToListAsync(cancellationToken);
        }

        foreach (var store in stores
                     .Where(store => !string.IsNullOrWhiteSpace(store.StoreCode))
                     .OrderBy(store => store.StoreCode, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 「本地 8 点」按各门店自己的时区算（配置时区 > 邮编 > 名称 > 悉尼），Brisbane 与 Sydney 在夏令时期间差 1 小时。
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, FindTimeZone(InstallmentOrderStoreTimeZoneResolver.Resolve(store)));
            if (localNow.Hour < startHour || localNow.Hour >= endHour)
            {
                continue;
            }

            try
            {
                await ProcessStoreAsync(store, localNow.Date, nowUtc, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单店失败不影响其他门店；没有写 Run，窗口内的下一轮会重试，发布靠幂等键去重。
                logger.LogWarning(ex, "新品到店推送处理失败，窗口内下一轮重试: Store={StoreCode}, LocalDate={LocalDate:yyyy-MM-dd}",
                    store.StoreCode, localNow.Date);
            }
        }
    }

    private async Task ProcessStoreAsync(Store store, DateTime localDate, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var storeCode = store.StoreCode.Trim();

        // 每店独立作用域：独立的数据库连接与事务边界，一个门店出错不会留下脏状态给下一个。
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<SqlSugarContext>().Db;

        if (await db.Queryable<ContainerNewProductRun>()
                .Where(run => run.StoreCode == storeCode && run.LocalDate == localDate)
                .AnyAsync())
        {
            return;
        }

        ContainerNewProductsResponseDto list;
        try
        {
            list = await services.GetRequiredService<IContainerNewProductsQuery>()
                .GetForStoreAsync(store, includeExisting: false, nowUtc, cancellationToken);
        }
        catch (ContainerNewProductsStateUnknownException)
        {
            // 不是 NSW / QLD 的门店算不出到店区间：不写 Run，窗口内每轮重新判定（地址修正后当天就能补上）。
            if (!_stateUnknownLoggedOn.TryGetValue(storeCode, out var loggedOn) || loggedOn != localDate)
            {
                _stateUnknownLoggedOn[storeCode] = localDate;
                logger.LogInformation("门店所在州无法判定（非 NSW / QLD），跳过新品到店推送: Store={StoreCode}", storeCode);
            }

            return;
        }

        // 列表已按到店日、货号排序，同商品多柜时先出现的是到店最早的一柜；按编码去重（与库的大小写不敏感主键一致）。
        var current = new List<(string ProductCode, string ContainerCode)>();
        var currentCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tooLong = 0;
        foreach (var item in list.Items)
        {
            var productCode = item.ProductCode?.Trim();
            if (string.IsNullOrEmpty(productCode))
            {
                continue;
            }

            if (productCode.Length > MaxCodeLength)
            {
                tooLong++;
                continue;
            }

            if (currentCodes.Add(productCode))
            {
                current.Add((productCode, Truncate(item.ContainerCode?.Trim(), MaxCodeLength)));
            }
        }

        if (tooLong > 0)
        {
            logger.LogWarning("新品到店推送忽略 {Count} 个超过 {Max} 位的商品编码: Store={StoreCode}", tooLong, MaxCodeLength, storeCode);
        }

        var hasSeen = await db.Queryable<ContainerNewProductSeen>().Where(seen => seen.StoreCode == storeCode).AnyAsync();
        var hasRun = await db.Queryable<ContainerNewProductRun>().Where(run => run.StoreCode == storeCode).AnyAsync();
        if (!hasSeen && !hasRun)
        {
            // 首次基线：把当前列表整体登记为「已见过」，不发通知。
            if (await PersistAsync(db, storeCode, localDate, nowUtc, current, notificationId: null, newCount: 0, isBaseline: true, cancellationToken))
            {
                logger.LogInformation("新品到店推送首次基线已登记（不发通知）: Store={StoreCode}, LocalDate={LocalDate:yyyy-MM-dd}, Products={Count}",
                    storeCode, localDate, current.Count);
            }

            return;
        }

        // 累计已见集合是「比昨天新增」的基线：前天已推、今天仍在列表里的商品不会再算新增。
        var seenCodes = new HashSet<string>(
            await db.Queryable<ContainerNewProductSeen>()
                .Where(seen => seen.StoreCode == storeCode)
                .Select(seen => seen.ProductCode)
                .ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var fresh = current.Where(entry => !seenCodes.Contains(entry.ProductCode)).ToList();

        string? notificationId = null;
        if (fresh.Count > 0)
        {
            // 先发布、再落记录：发布在任何业务事务之外。发布后、落记录前崩溃，下一轮重算出同一集合，
            // 发布被幂等键去重，随后补写 Seen / Run；发布前崩溃则整体重来。没有接收人时 NotificationId 为空，也照常落记录。
            var result = await services.GetRequiredService<INotificationPublisher>()
                .PublishAsync(BuildRequest(storeCode, localDate, fresh.Count), cancellationToken);
            notificationId = result.NotificationId;
            logger.LogInformation(
                "新品到店推送已发布: Store={StoreCode}, LocalDate={LocalDate:yyyy-MM-dd}, NewCount={Count}, NotificationId={NotificationId}, Recipients={Recipients}, Deduplicated={Deduplicated}",
                storeCode, localDate, fresh.Count, notificationId, result.RecipientCount, result.Deduplicated);
        }

        await PersistAsync(db, storeCode, localDate, nowUtc, fresh, notificationId, fresh.Count, isBaseline: false, cancellationToken);
    }

    /// <summary>
    /// 把本次新增写入 Seen 并落当天的 Run，两者在同一个短事务里（调用方此时没有任何其他事务，发布已经结束）。
    /// 返回 false 表示并发实例抢先写入（主键冲突）：事务已整体回滚，对方的 Run 已落库，下一轮会跳过。
    /// </summary>
    private async Task<bool> PersistAsync(
        ISqlSugarClient db,
        string storeCode,
        DateTime localDate,
        DateTime nowUtc,
        List<(string ProductCode, string ContainerCode)> entries,
        string? notificationId,
        int newCount,
        bool isBaseline,
        CancellationToken cancellationToken
    )
    {
        var seenRows = entries
            .Select(entry => new ContainerNewProductSeen
            {
                StoreCode = storeCode,
                ProductCode = entry.ProductCode,
                FirstLocalDate = localDate,
                ContainerCode = entry.ContainerCode,
                NotificationId = notificationId,
                CreatedAtUtc = nowUtc,
            })
            .ToList();
        var runRow = new ContainerNewProductRun
        {
            StoreCode = storeCode,
            LocalDate = localDate,
            ProcessedAtUtc = nowUtc,
            NewCount = newCount,
            IsBaseline = isBaseline,
            NotificationId = notificationId,
        };

        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            foreach (var batch in seenRows.Chunk(InsertBatchSize))
            {
                await db.Insertable(batch.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            await db.Insertable(runRow).ExecuteCommandAsync(cancellationToken);
        });
        if (transaction.IsSuccess)
        {
            return true;
        }

        var error = transaction.ErrorException ?? new InvalidOperationException("保存新品到店推送记录失败");
        if (IsUniqueViolation(error))
        {
            logger.LogWarning("新品到店推送记录被并发实例抢先写入，本次回滚: Store={StoreCode}, LocalDate={LocalDate:yyyy-MM-dd}", storeCode, localDate);
            return false;
        }

        throw error;
    }

    internal static NotificationPublishRequest BuildRequest(string storeCode, DateTime localDate, int count)
    {
        var date = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var number = count.ToString(CultureInfo.InvariantCulture);
        return new NotificationPublishRequest
        {
            Type = AppNotificationTypes.ContainerNewProducts,
            Title = NotificationTitle,
            // 通知文案只用英文；屏幕名与移动端英文文案一致（HB new arrivals）。
            Body = count == 1
                ? "1 new product is on its way to your store. Open HB new arrivals to see it."
                : $"{number} new products are on their way to your store. Open HB new arrivals to see them.",
            // 只发给该店持有「移动端查看新品到店」权限的人；Data 只放标识与数量。
            Audience = NotificationAudience.ForStore(storeCode, Permissions.Container.MobileNewProductsView),
            StoreCode = storeCode,
            Data = new Dictionary<string, string>
            {
                ["storeCode"] = storeCode,
                ["localDate"] = date,
                ["count"] = number,
            },
            // 幂等键带门店与本地日期：同店同日只会发一条；撤回后同键也不可再发（发布器按键查全部通知，含已撤回的）。
            DedupeKey = $"{DedupeKeyPrefix}{storeCode}:{date}",
            CreatedBy = "system:container-new-products",
        };
    }

    internal static (int Start, int End) ResolveHourRange(ContainerNewProductNotificationOptions settings)
    {
        var start = Math.Clamp(settings.LocalHour, 0, 23);
        return (start, Math.Clamp(settings.LatestLocalHour, start + 1, 24));
    }

    private static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // 与门店时区解析器的回退一致：认不出的时区按悉尼处理。
            return TimeZoneInfo.FindSystemTimeZoneById(InstallmentOrderStoreTimeZoneResolver.Sydney);
        }
    }

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
