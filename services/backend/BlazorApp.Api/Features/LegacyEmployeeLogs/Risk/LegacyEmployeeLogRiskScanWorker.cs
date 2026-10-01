using System.Data.Common;
using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;

/// <summary>
/// 老收银操作日志异常扫描。常规轮每 15 分钟回看 6 小时；每天另跑一次深度轮，按天回看 7 天，
/// 兜住上传滞后很久的日志与阈值调整。由分布式租约保证多实例下只有一个在扫；
/// 配置总开关、计划任务总开关任一关闭，或风险表未迁移时直接跳过。
/// </summary>
public sealed class LegacyEmployeeLogRiskScanWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<LegacyEmployeeLogRiskOptions> options,
    ILogger<LegacyEmployeeLogRiskScanWorker> logger
) : BackgroundService
{
    private const string LeaseTaskType = nameof(LegacyEmployeeLogRiskScanWorker);
    private const string LeaseScope = "posm-employee-logs";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 门店墙钟与 UTC 的换算：分店都在 NSW / QLD（UTC+10 / +11）。窗口下界按 +10、上界按 +12 取，
    /// 只会多看不会漏看；墙钟时间不会出现在未来，上界放宽没有代价。
    /// </summary>
    private static readonly TimeSpan WallClockLowerOffset = TimeSpan.FromHours(10);
    private static readonly TimeSpan WallClockUpperOffset = TimeSpan.FromHours(12);

    private bool _schemaMissingLogged;
    private DateTime? _lastDeepScanUtcDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "老收银操作日志异常扫描暂不可用"); }

            var interval = TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.QuickIntervalMinutes, 5, 120));
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task RunOnceAsync(DateTime nowUtc, CancellationToken stoppingToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled)
        {
            return;
        }
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await services.GetRequiredService<ScheduledTaskRuntimeControlService>().IsLeaseManagedWorkerEnabledAsync())
        {
            return;
        }

        var posm = services.GetRequiredService<POSMSqlSugarContext>().Db;
        if (posm.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer)
        {
            return;
        }
        var connection = (DbConnection)posm.Ado.Connection;
        if (!await LegacyEmployeeLogRiskScanner.SchemaReadyAsync(connection, stoppingToken))
        {
            if (!_schemaMissingLogged)
            {
                logger.LogWarning("老收银异常标记表尚未迁移，扫描跳过；请先执行 --schema=migrate（POSM 迁移 20261001.001-legacy-employee-log-risk）");
                _schemaMissingLogged = true;
            }
            return;
        }

        var leases = services.GetRequiredService<ScheduledTaskLeaseService>();
        var lease = await leases.TryAcquireAsync(LeaseTaskType, LeaseScope, LeaseDuration);
        if (!lease.Acquired || string.IsNullOrWhiteSpace(lease.Lease?.LeaseToken))
        {
            return;
        }

        var leaseToken = lease.Lease.LeaseToken;
        var success = false;
        try
        {
            var stores = await LegacyEmployeeLogRiskScanner.GetStoreCodesAsync(connection, stoppingToken);
            var windows = BuildWindows(nowUtc, settings, _lastDeepScanUtcDate, out var deep);
            var total = new LegacyRiskScanResult(0, 0, 0, 0, 0);
            var elapsed = Stopwatch.StartNew();
            foreach (var storeCode in stores)
            {
                await leases.EnsureActiveAsync(LeaseTaskType, LeaseScope, leaseToken, LeaseDuration, "老收银异常扫描");
                foreach (var (from, to) in windows)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        var result = await LegacyEmployeeLogRiskScanner.ScanAsync(connection, storeCode, from, to, settings, DateTime.UtcNow, stoppingToken);
                        total = new LegacyRiskScanResult(
                            total.Inserted + result.Inserted,
                            total.Updated + result.Updated,
                            total.Retracted + result.Retracted,
                            total.ImpactsWritten + result.ImpactsWritten,
                            total.RowsRead + result.RowsRead);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 单店单窗口失败不影响其他门店，下一轮会重新覆盖这段时间。
                        logger.LogWarning(ex, "老收银异常扫描失败: Store={StoreCode}, From={From:yyyy-MM-dd HH:mm}, To={To:yyyy-MM-dd HH:mm}", storeCode, from, to);
                    }
                }
            }
            if (deep)
            {
                _lastDeepScanUtcDate = nowUtc.Date;
            }
            logger.LogInformation(
                "老收银异常扫描完成: Mode={Mode}, Stores={Stores}, Rows={Rows}, Inserted={Inserted}, Updated={Updated}, Retracted={Retracted}, Impacts={Impacts}, ElapsedMs={ElapsedMs}",
                deep ? "deep" : "quick", stores.Count, total.RowsRead, total.Inserted, total.Updated, total.Retracted, total.ImpactsWritten, elapsed.ElapsedMilliseconds);
            success = true;
        }
        finally
        {
            await leases.CompleteAsync(LeaseTaskType, LeaseScope, leaseToken, success);
        }
    }

    /// <summary>
    /// 本轮要评估的墙钟窗口。到了深度扫描时刻且今天还没跑过：按天切成 DeepLookbackDays+1 个整天窗口
    /// （按天切是为了让单次读取量有上限）；否则只回看 QuickLookbackHours。
    /// </summary>
    internal static List<(DateTime From, DateTime To)> BuildWindows(
        DateTime nowUtc,
        LegacyEmployeeLogRiskOptions settings,
        DateTime? lastDeepScanUtcDate,
        out bool deep
    )
    {
        var upper = DateTime.SpecifyKind(nowUtc + WallClockUpperOffset, DateTimeKind.Unspecified);
        deep = nowUtc.Hour >= Math.Clamp(settings.DeepScanUtcHour, 0, 23) && lastDeepScanUtcDate != nowUtc.Date;
        if (!deep)
        {
            var lookback = TimeSpan.FromHours(Math.Clamp(settings.QuickLookbackHours, 1, 48));
            var lower = DateTime.SpecifyKind(nowUtc + WallClockLowerOffset - lookback, DateTimeKind.Unspecified);
            return [(lower, upper)];
        }
        var today = (nowUtc + WallClockLowerOffset).Date;
        var days = Math.Clamp(settings.DeepLookbackDays, 1, 31);
        var windows = new List<(DateTime, DateTime)>();
        for (var day = today.AddDays(-days); day <= today; day = day.AddDays(1))
        {
            var end = day.AddDays(1);
            windows.Add((DateTime.SpecifyKind(day, DateTimeKind.Unspecified), DateTime.SpecifyKind(end < upper ? end : upper, DateTimeKind.Unspecified)));
        }
        return windows;
    }
}
