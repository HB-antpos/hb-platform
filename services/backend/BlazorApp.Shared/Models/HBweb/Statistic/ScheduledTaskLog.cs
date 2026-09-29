using System.Text.Json;
using SqlSugar;

namespace BlazorApp.Shared.Models.HBweb
{
    public class ScheduledTaskLog : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [SugarColumn(Length = 100, IsNullable = false)]
        public string TaskType { get; set; } = string.Empty;

        [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
        public string? TaskParameters { get; set; }

        [SugarColumn(Length = 50, IsNullable = false)]
        public string Status { get; set; } = "Pending";

        [SugarColumn(IsNullable = false)]
        public DateTime StartedAt { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? CompletedAt { get; set; }

        [SugarColumn(IsNullable = true)]
        public int? DurationMs { get; set; }

        [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
        public string? ErrorMessage { get; set; }

        [SugarColumn(IsNullable = false)]
        public int RetryCount { get; set; } = 0;

        [SugarColumn(IsNullable = false)]
        public bool CanRetry { get; set; } = true;

        [SugarColumn(IsNullable = false)]
        public DateTime ScheduledTime { get; set; } = DateTime.UtcNow;

        [SugarColumn(IsNullable = true)]
        public string? TriggeredBy { get; set; }

        public TaskParameters GetParameters()
        {
            if (string.IsNullOrEmpty(TaskParameters))
                return new TaskParameters();

            try
            {
                return JsonSerializer.Deserialize<TaskParameters>(TaskParameters)
                    ?? new TaskParameters();
            }
            catch
            {
                return new TaskParameters();
            }
        }

        public void SetParameters(TaskParameters parameters)
        {
            TaskParameters = JsonSerializer.Serialize(parameters);
        }
    }

    public class TaskParameters
    {
        public string? Date { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public List<string>? BranchCodes { get; set; }
        public List<string>? SupplierCodes { get; set; }
        public int? Hour { get; set; }
        public string? StartYearMonth { get; set; }
        public string? EndYearMonth { get; set; }
        public int? MaxMonths { get; set; }
        public int? MaxConcurrency { get; set; }
        public List<string>? MasterGuids { get; set; }
        public Dictionary<string, object>? CustomParameters { get; set; }
    }

    public static class TaskType
    {
        public const string UpdateCurrentHourStatistics = "UpdateCurrentHourStatistics";
        public const string UpdateDailyStatistics = "UpdateDailyStatistics";
        public const string UpdateDailyStatisticsBatch = "UpdateDailyStatisticsBatch";
        public const string UpdateHourlyStatistics = "UpdateHourlyStatistics";
        public const string UpdateHourlyStatisticsBatch = "UpdateHourlyStatisticsBatch";
        public const string UpdateStoreStatistics = "UpdateStoreStatistics";
        public const string UpdateStoreStatisticsBatch = "UpdateStoreStatisticsBatch";
        public const string UpdateSupplierStatistics = "UpdateSupplierStatistics";
        public const string UpdateSupplierStatisticsBatch = "UpdateSupplierStatisticsBatch";
        public const string UpdateStoreSupplierStatistics = "UpdateStoreSupplierStatistics";
        public const string UpdateStoreSupplierStatisticsBatch = "UpdateStoreSupplierStatisticsBatch";
        public const string UpdateProductStoreDailyStatistics = "UpdateProductStoreDailyStatistics";
        public const string UpdateProductStoreDailyStatisticsBatch = "UpdateProductStoreDailyStatisticsBatch";
        public const string FullRefreshPreviousDay = "FullRefreshPreviousDay";
        public const string FullRefreshCurrentDay = "FullRefreshCurrentDay";
        public const string FullRefreshCurrentWeek = "FullRefreshCurrentWeek";
        public const string FullRefreshPreviousMonth = "FullRefreshPreviousMonth";
        public const string FullRefreshCurrentMonth = "FullRefreshCurrentMonth";
        public const string FullRefreshCurrentQuarter = "FullRefreshCurrentQuarter";
        public const string BatchFullRefreshByMonths = "BatchFullRefreshByMonths";
        public const string BatchFullRefreshConcurrent = "BatchFullRefreshConcurrent";
        public const string RecalculateDailyStatisticsAlignment = "RecalculateDailyStatisticsAlignment";
        public const string RecalculateProductStoreDaily = "RecalculateProductStoreDaily";
        public const string SyncPosmProductSupplierMappingsIncremental = "SyncPosmProductSupplierMappingsIncremental";
        public const string WarmUpStoreOrderCache = "WarmUpStoreOrderCache";
        public const string ResolveLocalSupplierCategories = "ResolveLocalSupplierCategories";
        public const string SyncStoreLocalSupplierInvoices = "SyncStoreLocalSupplierInvoices";
        public const string SyncStoreLocalSupplierInvoiceDetails = "SyncStoreLocalSupplierInvoiceDetails";
        public const string SyncStoreLocalSupplierInvoicesAll = "SyncStoreLocalSupplierInvoicesAll";
        public const string SyncContainers = "SyncContainers";
        public const string SyncContainerDetails = "SyncContainerDetails";
        public const string SyncWareHouseOrders = "SyncWareHouseOrders";
        public const string SyncWareHouseOrderDetails = "SyncWareHouseOrderDetails";
        public const string SyncWareHouseOrdersAll = "SyncWareHouseOrdersAll";
        public const string SyncStoreLocalSupplierInvoicesIncremental = "SyncStoreLocalSupplierInvoicesIncremental";
        public const string SyncContainersIncremental = "SyncContainersIncremental";
        public const string SyncContainerDetailsIncremental = "SyncContainerDetailsIncremental";
        public const string SyncWareHouseOrdersIncremental = "SyncWareHouseOrdersIncremental";
    }

    public static class TaskStatus
    {
        public const string Pending = "Pending";
        public const string Running = "Running";
        public const string Success = "Success";
        public const string Skipped = "Skipped";
        public const string Failed = "Failed";
    }

    public static class TaskTrigger
    {
        public const string Scheduled = "Scheduled";
        public const string Manual = "Manual";
        public const string Retry = "Retry";
    }
}
