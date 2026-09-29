using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.Background
{
    /// <summary>
    /// 定时任务配置选项
    /// </summary>
    public class ScheduledTaskOptions
    {
        /// <summary>
        /// 是否允许当前进程参与定时任务调度（默认 true）。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 当前 API 实例标识；为空时使用机器名和进程号。
        /// </summary>
        public string? InstanceId { get; set; }

        /// <summary>
        /// 同步和缓存预热任务间隔分钟数（默认 20 分钟）
        /// </summary>
        public int HourlyTaskIntervalMinutes { get; set; } = 20;

        /// <summary>
        /// 每日任务执行小时（默认 3 点）
        /// </summary>
        public int DailyTaskHour { get; set; } = 23;

        /// <summary>
        /// 每日任务执行分钟（默认 0 分）
        /// </summary>
        public int DailyTaskMinute { get; set; } = 0;

        /// <summary>
        /// 每周任务执行星期几（默认 0 = 星期日）
        /// </summary>
        public int WeeklyTaskDayOfWeek { get; set; } = 0;

        /// <summary>
        /// 每周任务执行小时（默认 23 点）
        /// </summary>
        public int WeeklyTaskHour { get; set; } = 23;

        /// <summary>
        /// 每周任务执行分钟（默认 59 分）
        /// </summary>
        public int WeeklyTaskMinute { get; set; } = 59;

        /// <summary>
        /// 每月任务执行日（默认 31 日；不足 31 天的月份自动取月末）
        /// </summary>
        public int MonthlyTaskDay { get; set; } = 31;

        /// <summary>
        /// 每月任务执行小时（默认 23 点）
        /// </summary>
        public int MonthlyTaskHour { get; set; } = 23;

        /// <summary>
        /// 每月任务执行分钟（默认 59 分）
        /// </summary>
        public int MonthlyTaskMinute { get; set; } = 59;

        /// <summary>
        /// 是否启用随机冗余（默认 true）
        /// </summary>
        public bool EnableJitter { get; set; } = true;

        /// <summary>
        /// 随机冗余最大偏移分钟数（默认 5 分钟，即 -5 到 +5 分钟）
        /// </summary>
        public int JitterMaxMinutes { get; set; } = 5;

        /// <summary>最近业务日 Fresh 快照的元数据巡检周期。</summary>
        public int DiscountSnapshotRecentCheckMinutes { get; set; } = 5;

        /// <summary>优先保持折扣快照最新的业务日数。</summary>
        public int DiscountSnapshotRecentDays { get; set; } = 3;

        /// <summary>自动回填从业务当天向前覆盖的完整年数，含首尾日期。</summary>
        public int DiscountSnapshotHistoricalYears { get; set; } = 1;

        /// <summary>单轮最多处理的折扣业务日数，防止持续回填占用其他后台工作。</summary>
        public int DiscountSnapshotBatchSize { get; set; } = 12;

        /// <summary>
        /// 每日任务结束后是否按已有采集记录为全部供应商重新归类（默认开启，可用配置热关闭）。
        /// </summary>
        public bool LocalSupplierCategoryNightlyResolveEnabled { get; set; } = true;
    }

    /// <summary>
    /// 定时任务调度服务
    /// 作为后台服务运行，负责按照固定时间间隔自动触发统计任务
    /// 包括每小时统计任务和每日全量刷新任务
    /// </summary>
    public class ScheduledTaskService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScheduledTaskService> _logger;
        private readonly ScheduledTaskOptions _options;
        private readonly TimeZoneInfo _sydneyTimeZone;
        private Timer? _hourlyTimer;
        private Timer? _salesStatisticsTimer;
        private readonly object _salesStatisticsTimerLock = new();
        private Timer? _dailyTimer;
        private Timer? _weeklyTimer;
        private Timer? _monthlyTimer;
        private bool _stopping;

        private sealed record TaskExecutionOutcome(bool IsSkipped, string? SkipReason = null)
        {
            public static TaskExecutionOutcome Completed() => new(false);
            public static TaskExecutionOutcome Skipped(string? reason) => new(true, reason);
        }

        public ScheduledTaskService(
            IServiceScopeFactory scopeFactory,
            ILogger<ScheduledTaskService> logger,
            IOptions<ScheduledTaskOptions> options
        )
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options.Value;
            _sydneyTimeZone = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
        }

        /// <summary>
        /// 后台服务执行入口
        /// 初始化每小时和每日定时器
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("定时任务服务启动");

            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _sydneyTimeZone);

            var baseInterval =
                _options.HourlyTaskIntervalMinutes
                - (now.Minute % _options.HourlyTaskIntervalMinutes);

            double nextHourlyMinutes;
            if (_options.EnableJitter)
            {
                var jitter = Random.Shared.Next(
                    -_options.JitterMaxMinutes,
                    _options.JitterMaxMinutes + 1
                );
                nextHourlyMinutes = Math.Max(1, baseInterval + jitter);
            }
            else
            {
                nextHourlyMinutes = baseInterval;
            }

            _hourlyTimer = new Timer(
                async _ => await ExecuteAutomaticMaintenanceTask(),
                null,
                TimeSpan.FromMinutes(nextHourlyMinutes),
                TimeSpan.FromMinutes(_options.HourlyTaskIntervalMinutes)
            );

            // 销售统计单独按悉尼时间整点、半点运行；使用一次性 Timer，完成后再计算下一边界，避免漂移和重叠。
            lock (_salesStatisticsTimerLock)
            {
                if (!_stopping)
                {
                    _salesStatisticsTimer = new Timer(
                        async _ => await ExecuteScheduledSalesStatisticsTask(),
                        null,
                        Timeout.InfiniteTimeSpan,
                        Timeout.InfiniteTimeSpan
                    );
                }
            }
            ScheduleNextSalesStatisticsRun();

            // 计算到下一个每日任务执行时间的时间间隔
            var nextDaily = CalculateNextDailyRun(now);
            _dailyTimer = new Timer(
                async _ => await ExecuteDailyTask(automatic: true),
                null,
                nextDaily,
                TimeSpan.FromDays(1)
            );

            // 计算到下一个每周任务执行时间的时间间隔
            var nextWeekly = CalculateNextWeeklyRun(now);
            _weeklyTimer = new Timer(
                async _ => await ExecuteWeeklyTask(),
                null,
                nextWeekly,
                TimeSpan.FromDays(7)
            );

            // 计算到下一个每月任务执行时间的时间间隔
            var nextMonthly = CalculateNextMonthlyRun(now);
            _monthlyTimer = new Timer(
                async _ => await ExecuteMonthlyTask(false),
                null,
                nextMonthly,
                TimeSpan.FromDays(1)
            );

            _logger.LogInformation(
                "定时任务已注册 - 维护任务将在 {Minutes} 分钟后开始{EnableJitter}，销售统计按整点和半点执行，"
                    + "每日任务将在 {Time} 开始，每周任务将在 {WeeklyTime} 开始，每月任务将在 {MonthlyTime} 开始",
                (int)nextHourlyMinutes,
                _options.EnableJitter ? $"（冗余偏移: ±{_options.JitterMaxMinutes}分钟）" : "",
                now.Add(nextDaily).ToString("HH:mm:ss"),
                now.Add(nextWeekly).ToString("yyyy-MM-dd HH:mm:ss"),
                now.Add(nextMonthly).ToString("yyyy-MM-dd HH:mm:ss")
            );

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        private static TimeSpan CalculateNextSalesStatisticsRun(DateTime now)
        {
            var nextMinute = now.Minute < 30 ? 30 : 60;
            var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0)
                .AddMinutes(nextMinute);
            return next - now;
        }

        private void ScheduleNextSalesStatisticsRun()
        {
            lock (_salesStatisticsTimerLock)
            {
                if (_stopping || _salesStatisticsTimer == null)
                    return;

                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _sydneyTimeZone);
                _salesStatisticsTimer.Change(
                    CalculateNextSalesStatisticsRun(now),
                    Timeout.InfiniteTimeSpan
                );
            }
        }

        private async Task ExecuteScheduledSalesStatisticsTask()
        {
            try
            {
                await ExecuteAutomaticSalesStatisticsTask();
            }
            finally
            {
                ScheduleNextSalesStatisticsRun();
            }
        }

        /// <summary>
        /// 计算下一次每日任务的执行时间
        /// </summary>
        /// <param name="now">当前时间</param>
        /// <returns>距离下次执行的时间间隔</returns>
        private TimeSpan CalculateNextDailyRun(DateTime now)
        {
            var nextRun = new DateTime(
                now.Year,
                now.Month,
                now.Day,
                _options.DailyTaskHour,
                _options.DailyTaskMinute,
                0
            );

            if (now >= nextRun)
            {
                nextRun = nextRun.AddDays(1);
            }

            if (_options.EnableJitter)
            {
                var jitter = Random.Shared.Next(
                    -_options.JitterMaxMinutes,
                    _options.JitterMaxMinutes + 1
                );
                nextRun = nextRun.AddMinutes(jitter);

                if (nextRun <= now)
                {
                    nextRun = now.AddMinutes(1);
                }
            }

            return nextRun - now;
        }

        /// <summary>
        /// 计算下一次每周任务的执行时间
        /// </summary>
        /// <param name="now">当前时间</param>
        /// <returns>距离下次执行的时间间隔</returns>
        private TimeSpan CalculateNextWeeklyRun(DateTime now)
        {
            var targetDayOfWeek = (DayOfWeek)_options.WeeklyTaskDayOfWeek;
            var daysUntil = ((int)targetDayOfWeek - (int)now.DayOfWeek + 7) % 7;
            var next = now
                .Date.AddDays(daysUntil)
                .AddHours(_options.WeeklyTaskHour)
                .AddMinutes(_options.WeeklyTaskMinute);

            if (next <= now)
            {
                next = next.AddDays(7);
            }

            // 周复查默认卡在周日 23:59，不能加正向 jitter 跨到周一后误判为下一周。

            return next - now;
        }

        /// <summary>
        /// 计算下一次每月任务的执行时间
        /// </summary>
        /// <param name="now">当前时间</param>
        /// <returns>距离下次执行的时间间隔</returns>
        private TimeSpan CalculateNextMonthlyRun(DateTime now)
        {
            var year = now.Year;
            var month = now.Month;
            var next = BuildMonthlyRunTime(year, month);

            if (next <= now)
            {
                var followingMonth = new DateTime(year, month, 1).AddMonths(1);
                next = BuildMonthlyRunTime(followingMonth.Year, followingMonth.Month);
            }

            // 月末/季度末复查不能被 jitter 推到次月，否则会跳过本月复查。

            return next - now;
        }

        private DateTime BuildMonthlyRunTime(int year, int month)
        {
            var day = Math.Min(
                Math.Max(_options.MonthlyTaskDay, 1),
                DateTime.DaysInMonth(year, month)
            );
            return new DateTime(
                year,
                month,
                day,
                _options.MonthlyTaskHour,
                _options.MonthlyTaskMinute,
                0
            );
        }

        /// <summary>
        /// 执行每小时统计任务
        /// 更新当前小时的统计数据
        /// </summary>
        private async Task ExecuteHourlyTask()
        {
            if (!await CanRunHourlyTaskAsync("每小时定时任务"))
            {
                return;
            }

            await ExecuteMappingSyncTask();
            await ExecuteSalesStatisticsTask();
            await ExecuteStoreOrderCacheWarmUpTask();
        }

        private async Task ExecuteAutomaticMaintenanceTask()
        {
            if (!await CanRunHourlyTaskAsync("20分钟维护任务"))
                return;

            await ExecuteMappingSyncTask();
            await ExecuteStoreOrderCacheWarmUpTask();
        }

        private async Task ExecuteAutomaticSalesStatisticsTask()
        {
            if (!await CanRunHourlyTaskAsync("整点半点销售统计任务"))
                return;

            await ExecuteSalesStatisticsTask();
        }

        private async Task<bool> CanRunHourlyTaskAsync(string taskName)
        {
            if (!await IsCurrentInstanceSchedulerEnabledAsync(taskName))
                return false;

            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _sydneyTimeZone);
            var startHour = 7;
            var endHour = now.DayOfWeek == DayOfWeek.Thursday ? 23 : 20;

            // 检查是否在允许的时间段内 (8:00 - endHour:00)
            // 允许稍微超过 endHour 一点点（比如5分钟），以容忍 Timer 的微小延迟，确保整点任务能执行
            // 但主要是为了拦截如 18:20, 18:40 这种超出范围的任务
            if (
                now.Hour < startHour
                || now.Hour > endHour
                || (now.Hour == endHour && now.Minute > 5)
            )
            {
                _logger.LogInformation(
                    "当前时间 {Time} 不在定时任务执行范围内 ({Start}:00 - {End}:00)，跳过执行",
                    now.ToString("HH:mm"),
                    startHour,
                    endHour
                );
                return false;
            }

            return true;
        }

        private Task ExecuteMappingSyncTask()
        {
            return ExecuteHourlyTaskWithIndependentScopeAsync(
                TaskType.SyncPosmProductSupplierMappingsIncremental,
                "商品-供应商映射增量同步",
                async serviceProvider =>
                {
                    var dataSyncService =
                        serviceProvider.GetRequiredService<IDataSyncReactService>();
                    var result =
                        await dataSyncService.SyncPosmProductSupplierMappingsIncrementalAsync();
                    if (!result.IsSuccess)
                    {
                        // 同步服务以结果对象表达业务失败，调度层必须转成异常，避免随后把失败任务覆盖成成功。
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(result.Message)
                                ? "商品-供应商映射增量同步返回失败"
                                : result.Message
                        );
                    }
                    return TaskExecutionOutcome.Completed();
                }
            );
        }

        private Task ExecuteSalesStatisticsTask()
        {
            return ExecuteHourlyTaskWithIndependentScopeAsync(
                TaskType.UpdateCurrentHourStatistics,
                "每小时统计任务",
                async serviceProvider =>
                {
                    var statisticsJobService =
                        serviceProvider.GetRequiredService<SalesStatisticsJobService>();
                    // 半小时刷新只处理当天；最近七日历史由每日夜间任务补算，避免重复占用商品成本锁。
                    var refreshResult = await statisticsJobService.FullRefreshCurrentDay(
                        automatic: true,
                        includeHistorical: false
                    );
                    if (refreshResult.IsSkipped)
                    {
                        // 跳过表示另一实例仍持有日期租约；既不能伪造成功，也不能清理有效快照缓存。
                        return TaskExecutionOutcome.Skipped(refreshResult.Message);
                    }

                    // 只有实际完成并发布快照后才清理本实例缓存。
                    var dashboardCacheWarmer =
                        serviceProvider.GetRequiredService<ISalesDashboardCacheWarmer>();
                    await dashboardCacheWarmer.ClearCacheAsync();
                    return TaskExecutionOutcome.Completed();
                }
            );
        }

        private Task ExecuteStoreOrderCacheWarmUpTask()
        {
            // 每个子任务都使用独立 scope，避免前一个任务的连接/上下文异常污染后续任务。
            return ExecuteHourlyTaskWithIndependentScopeAsync(
                TaskType.WarmUpStoreOrderCache,
                "商品列表缓存预热任务",
                async serviceProvider =>
                {
                    var cacheWarmer =
                        serviceProvider.GetRequiredService<BlazorApp.Api.Interfaces.IStoreOrderCacheWarmer>();
                    await cacheWarmer.WarmUpHomePageAsync();
                    return TaskExecutionOutcome.Completed();
                }
            );
        }

        /// <summary>
        /// 在独立作用域中执行每小时子任务
        /// </summary>
        private async Task ExecuteHourlyTaskWithIndependentScopeAsync(
            string taskType,
            string taskName,
            Func<IServiceProvider, Task<TaskExecutionOutcome>> taskAction
        )
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var serviceProvider = scope.ServiceProvider;

                // 任务日志不是业务前置条件，日志服务拿不到时也要继续执行业务。
                var taskLogService = serviceProvider.GetService<ScheduledTaskLogService>();
                if (taskLogService == null)
                {
                    _logger.LogWarning("{TaskName} 未获取到任务日志服务，将继续执行业务任务", taskName);
                }

                ScheduledTaskLog? taskLog = null;
                if (taskLogService != null)
                {
                    try
                    {
                        taskLog = await taskLogService.LogTaskStartAsync(
                            taskType,
                            new TaskParameters()
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "{TaskName} 记录任务开始日志失败，继续执行业务任务", taskName);
                    }
                }

                try
                {
                    _logger.LogInformation("开始执行{TaskName}", taskName);
                    var outcome = await taskAction(serviceProvider);

                    if (taskLog != null && taskLogService != null)
                    {
                        if (taskType == TaskType.UpdateCurrentHourStatistics)
                        {
                            if (outcome.IsSkipped)
                            {
                                await taskLogService.LogTaskSkippedStrictAsync(
                                    taskLog.Id,
                                    outcome.SkipReason ?? "统计日期租约仍由其他执行者持有"
                                );
                            }
                            else
                            {
                                await taskLogService.LogTaskSuccessStrictAsync(taskLog.Id);
                            }
                        }
                        else
                        {
                            await taskLogService.LogTaskSuccessAsync(taskLog.Id);
                        }
                    }

                    _logger.LogInformation(
                        outcome.IsSkipped ? "{TaskName}跳过执行" : "{TaskName}执行完成",
                        taskName
                    );
                }
                catch (Exception ex)
                {
                    if (taskLog != null && taskLogService != null)
                    {
                        if (taskType == TaskType.UpdateCurrentHourStatistics)
                        {
                            // 成功版本发布失败时也必须严格落为 Failed，避免留下永久 Running 状态。
                            await taskLogService.LogTaskFailureStrictAsync(
                                taskLog.Id,
                                BuildTaskFailureMessage(ex)
                            );
                        }
                        else
                        {
                            await TryLogTaskFailureAsync(
                                () =>
                                    taskLogService.LogTaskFailureAsync(
                                        taskLog.Id,
                                        BuildTaskFailureMessage(ex)
                                    ),
                                _logger,
                                taskName
                            );
                        }
                    }

                    _logger.LogError(ex, "{TaskName}执行失败", taskName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{TaskName} 创建独立作用域或解析依赖失败", taskName);
            }
        }

        /// <summary>
        /// 统一格式化任务异常信息
        /// </summary>
        private static string BuildTaskFailureMessage(Exception ex)
        {
            return ex.Message + "\n" + ex.StackTrace;
        }

        /// <summary>
        /// 失败日志写入只能作为附加信息，不能反过来打断业务异常处理链路
        /// </summary>
        private static async Task TryLogTaskFailureAsync(
            Func<Task> logFailureAsync,
            ILogger logger,
            string taskName
        )
        {
            try
            {
                await logFailureAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "记录任务失败日志时发生异常，任务：{TaskName}", taskName);
            }
        }

        /// <summary>
        /// 执行每日全量刷新任务
        /// 全量刷新当天的统计数据
        /// </summary>
        private async Task ExecuteDailyTask(bool automatic)
        {
            if (!await IsCurrentInstanceSchedulerEnabledAsync("每日全量刷新任务"))
            {
                return;
            }

            using (var scope = _scopeFactory.CreateScope())
            {
                var taskLogService =
                    scope.ServiceProvider.GetRequiredService<ScheduledTaskLogService>();

                ScheduledTaskLog? taskLog = null;
                try
                {
                    taskLog = await taskLogService.LogTaskStartAsync(
                        TaskType.FullRefreshPreviousDay,
                        new TaskParameters()
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "记录任务开始日志失败，继续执行业务任务");
                }

                try
                {
                    _logger.LogInformation("开始执行每日全量刷新任务");

                    var statisticsJobService =
                        scope.ServiceProvider.GetRequiredService<SalesStatisticsJobService>();
                    var previousDayResult = await statisticsJobService.FullRefreshPreviousDay();
                    // 昨天的完整统计刚完成，滚动补算从前天开始，避免重复更新同一批商品。
                    var currentDayResult = await statisticsJobService.FullRefreshCurrentDay(
                        automatic,
                        firstHistoricalDayOffset: 2
                    );

                    if (taskLog != null)
                    {
                        if (previousDayResult.IsSkipped || currentDayResult.IsSkipped)
                        {
                            await taskLogService.LogTaskSkippedStrictAsync(
                                taskLog.Id,
                                previousDayResult.IsSkipped
                                    ? previousDayResult.Message ?? "前一天统计已有运行中的日期租约"
                                    : currentDayResult.Message ?? "当天统计已有运行中的日期租约"
                            );
                        }
                        else
                        {
                            await taskLogService.LogTaskSuccessAsync(taskLog.Id);
                        }
                    }
                    _logger.LogInformation("每日全量刷新任务执行完成");
                }
                catch (Exception ex)
                {
                    if (taskLog != null)
                        await TryLogTaskFailureAsync(
                            () =>
                                taskLogService.LogTaskFailureAsync(
                                    taskLog.Id,
                                    BuildTaskFailureMessage(ex)
                                ),
                            _logger,
                            "每日全量刷新任务"
                        );
                    _logger.LogError(ex, "每日全量刷新任务执行失败");
                }

                try
                {
                    _logger.LogInformation("开始执行公共假期自动同步任务");
                    var holidaySyncService =
                        scope.ServiceProvider.GetRequiredService<IAttendancePublicHolidaySyncService>();
                    var result = await holidaySyncService.SyncAllActiveStoresAsync();
                    _logger.LogInformation(
                        "公共假期自动同步完成：同步 {SyncedCount} 条，新增 {CreatedCount} 条，更新 {UpdatedCount} 条，跳过 {SkippedCount} 个分店",
                        result.SyncedCount,
                        result.CreatedCount,
                        result.UpdatedCount,
                        result.SkippedCount
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "公共假期自动同步任务执行失败");
                }
            }

            // 放在统计刷新之后串行执行，错开数据库负载；统计失败或跳过都不影响重新归类。
            await ExecuteLocalSupplierCategoryResolveTask();
        }

        /// <summary>
        /// 每晚供应商分类重新归类：只读写本库，不访问供应商网站。
        /// 为白天 HQ 同步进来的新商品按已有采集记录补上归类，并清理换供应商留下的陈旧归属。
        /// </summary>
        private async Task ExecuteLocalSupplierCategoryResolveTask()
        {
            if (!_options.LocalSupplierCategoryNightlyResolveEnabled)
            {
                _logger.LogInformation("供应商分类每晚重新归类已通过配置关闭，跳过执行");
                return;
            }

            await ExecuteHourlyTaskWithIndependentScopeAsync(
                TaskType.ResolveLocalSupplierCategories,
                "供应商分类每晚重新归类",
                async serviceProvider =>
                {
                    var categoryService =
                        serviceProvider.GetRequiredService<ILocalSupplierCategoryReactService>();
                    var result = await categoryService.ResolveAllSuppliersAsync("System");
                    _logger.LogInformation(
                        "供应商分类重新归类：供应商 {SupplierCount} 家，扫描商品 {ProductsScanned}，新归类 {Assigned}，"
                            + "改归类 {Updated}，清除 {Cleared}，人工锁定跳过 {ManualSkipped}，陈旧归属清理 {StaleRemoved}",
                        result.SupplierCount,
                        result.ProductsScanned,
                        result.Assigned,
                        result.Updated,
                        result.Cleared,
                        result.ManualSkipped,
                        result.StaleRemoved
                    );
                    if (result.FailedSuppliers.Count > 0)
                    {
                        // 其余供应商已各自提交；这里转成异常，让任务日志如实记为失败并列出失败的供应商。
                        throw new InvalidOperationException(
                            "部分供应商重新归类失败：" + string.Join("；", result.FailedSuppliers)
                        );
                    }

                    return TaskExecutionOutcome.Completed();
                }
            );
        }

        /// <summary>
        /// 执行每周全量刷新任务
        /// 全量刷新本周的统计数据
        /// </summary>
        private async Task ExecuteWeeklyTask()
        {
            if (!await IsCurrentInstanceSchedulerEnabledAsync("每周全量刷新任务"))
            {
                return;
            }

            using (var scope = _scopeFactory.CreateScope())
            {
                var taskLogService =
                    scope.ServiceProvider.GetRequiredService<ScheduledTaskLogService>();

                ScheduledTaskLog? taskLog = null;
                try
                {
                    taskLog = await taskLogService.LogTaskStartAsync(
                        TaskType.FullRefreshCurrentWeek,
                        new TaskParameters()
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "记录任务开始日志失败，继续执行业务任务");
                }

                try
                {
                    _logger.LogInformation("开始执行每周全量刷新任务");

                    var statisticsJobService =
                        scope.ServiceProvider.GetRequiredService<SalesStatisticsJobService>();

                    var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _sydneyTimeZone);
                    var monday = GetCurrentWeekMonday(now);
                    var sunday = monday.AddDays(6);

                    _logger.LogInformation(
                        "每周统计任务将刷新 {StartDate} 至 {EndDate} 的数据",
                        monday.ToString("yyyy-MM-dd"),
                        sunday.ToString("yyyy-MM-dd")
                    );

                    var result = await statisticsJobService.BatchFullRefreshConcurrent(monday, sunday);
                    if (result.HasSkippedDates && !result.HasFailedDates)
                    {
                        if (taskLog != null)
                            await taskLogService.LogTaskSkippedStrictAsync(taskLog.Id, result.Message);
                        _logger.LogInformation("每周全量刷新存在跳过日期，不写 Success: {Message}", result.Message);
                        return;
                    }
                    if (!result.Success)
                    {
                        throw new Exception($"每周统计任务失败: {result.Message}");
                    }

                    if (taskLog != null)
                        await taskLogService.LogTaskSuccessAsync(taskLog.Id);
                    _logger.LogInformation("每周全量刷新任务执行完成");
                }
                catch (Exception ex)
                {
                    if (taskLog != null)
                        await TryLogTaskFailureAsync(
                            () =>
                                taskLogService.LogTaskFailureAsync(
                                    taskLog.Id,
                                    BuildTaskFailureMessage(ex)
                                ),
                            _logger,
                            "每周全量刷新任务"
                        );
                    _logger.LogError(ex, "每周全量刷新任务执行失败");
                }
            }
        }

        private static DateTime GetCurrentWeekMonday(DateTime now)
        {
            var daysSinceMonday =
                ((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return now.Date.AddDays(-daysSinceMonday);
        }

        /// <summary>
        /// 执行每月全量刷新任务
        /// 全量刷新前一个月的统计数据
        /// </summary>
        private async Task ExecuteMonthlyTask(bool force)
        {
            if (!await IsCurrentInstanceSchedulerEnabledAsync("每月全量刷新任务"))
            {
                return;
            }

            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _sydneyTimeZone);
            if (!force && now.Date != BuildMonthlyRunTime(now.Year, now.Month).Date)
            {
                return;
            }

            var isQuarterEnd = now.Month % 3 == 0;
            var currentMonth = new DateTime(now.Year, now.Month, 1);
            var startMonth = isQuarterEnd ? currentMonth.AddMonths(-2) : currentMonth;
            var startYearMonth = startMonth.ToString("yyyy-MM");
            var endYearMonth = currentMonth.ToString("yyyy-MM");

            using (var scope = _scopeFactory.CreateScope())
            {
                var taskLogService =
                    scope.ServiceProvider.GetRequiredService<ScheduledTaskLogService>();

                ScheduledTaskLog? taskLog = null;
                try
                {
                    taskLog = await taskLogService.LogTaskStartAsync(
                        isQuarterEnd
                            ? TaskType.FullRefreshCurrentQuarter
                            : TaskType.FullRefreshCurrentMonth,
                        new TaskParameters
                        {
                            StartYearMonth = startYearMonth,
                            EndYearMonth = endYearMonth,
                            MaxMonths = isQuarterEnd ? 3 : 1,
                        }
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "记录任务开始日志失败，继续执行业务任务");
                }

                try
                {
                    _logger.LogInformation("开始执行每月全量刷新任务");

                    var statisticsJobService =
                        scope.ServiceProvider.GetRequiredService<SalesStatisticsJobService>();

                    _logger.LogInformation(
                        "{TaskName}将刷新 {StartYearMonth} 至 {EndYearMonth} 的数据",
                        isQuarterEnd ? "季度末统计任务" : "每月统计任务",
                        startYearMonth,
                        endYearMonth
                    );

                    var result = await statisticsJobService.BatchFullRefreshByMonths(
                        startYearMonth,
                        endYearMonth,
                        isQuarterEnd ? 3 : 1
                    );

                    if (result.HasSkippedDates && !result.HasFailedDates)
                    {
                        if (taskLog != null)
                            await taskLogService.LogTaskSkippedStrictAsync(taskLog.Id, result.Message);
                        _logger.LogInformation("每月全量刷新存在跳过日期，不写 Success: {Message}", result.Message);
                        return;
                    }
                    if (!result.Success)
                    {
                        throw new Exception($"每月统计任务失败: {result.Message}");
                    }

                    if (taskLog != null)
                        await taskLogService.LogTaskSuccessAsync(taskLog.Id);
                    _logger.LogInformation("每月全量刷新任务执行完成");
                }
                catch (Exception ex)
                {
                    if (taskLog != null)
                        await TryLogTaskFailureAsync(
                            () =>
                                taskLogService.LogTaskFailureAsync(
                                    taskLog.Id,
                                    BuildTaskFailureMessage(ex)
                                ),
                            _logger,
                            "每月全量刷新任务"
                        );
                    _logger.LogError(ex, "每月全量刷新任务执行失败");
                }
            }
        }

        /// <summary>
        /// 手动触发每小时统计任务
        /// </summary>
        public async Task TriggerHourlyTaskManually()
        {
            _logger.LogInformation("手动触发每小时统计任务");
            await ExecuteHourlyTask();
        }

        /// <summary>
        /// 手动触发每日全量刷新任务
        /// </summary>
        public async Task TriggerDailyTaskManually()
        {
            _logger.LogInformation("手动触发每日全量刷新任务");
            await ExecuteDailyTask(automatic: false);
        }

        /// <summary>
        /// 手动触发每周全量刷新任务
        /// </summary>
        public async Task TriggerWeeklyTaskManually()
        {
            _logger.LogInformation("手动触发每周全量刷新任务");
            await ExecuteWeeklyTask();
        }

        /// <summary>
        /// 手动触发每月全量刷新任务
        /// </summary>
        public async Task TriggerMonthlyTaskManually()
        {
            _logger.LogInformation("手动触发每月全量刷新任务");
            await ExecuteMonthlyTask(true);
        }

        /// <summary>
        /// 停止定时任务服务
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("定时任务服务停止");
            lock (_salesStatisticsTimerLock)
            {
                _stopping = true;
                _salesStatisticsTimer?.Dispose();
                _salesStatisticsTimer = null;
            }
            _hourlyTimer?.Dispose();
            _dailyTimer?.Dispose();
            _weeklyTimer?.Dispose();
            _monthlyTimer?.Dispose();
            await base.StopAsync(cancellationToken);
        }

        private async Task<bool> IsCurrentInstanceSchedulerEnabledAsync(string taskName)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var controlService =
                    scope.ServiceProvider.GetService<ScheduledTaskRuntimeControlService>();
                if (controlService == null)
                {
                    return _options.Enabled;
                }

                var enabled = await controlService.IsCurrentInstanceSchedulerEnabledAsync();
                if (!enabled)
                {
                    _logger.LogInformation("{TaskName} 已被后台运行时控制关闭，当前实例跳过执行", taskName);
                }

                return enabled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{TaskName} 检查运行时控制失败，当前实例跳过执行", taskName);
                return false;
            }
        }
    }
}
