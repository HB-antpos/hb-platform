namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 老系统操作日志查询条件（数据源 POSM.dbo.EmployeeLogs）。
/// From / To 是门店本地墙钟时间，按 [From, To) 半开区间比较，不做时区换算。
/// </summary>
public sealed class LegacyEmployeeLogQueryDto
{
    /// <summary>单店旧参数，与 StoreCodes 合并。</summary>
    public string? StoreCode { get; set; }

    /// <summary>分店，可多选；至少一个。</summary>
    public List<string>? StoreCodes { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public string? DeviceCode { get; set; }

    public List<string>? EmployeeIds { get; set; }

    public List<string>? Operations { get; set; }

    /// <summary>在操作详情文本中做包含匹配，如订单号、条码、商品编码、金额。</summary>
    public string? Keyword { get; set; }

    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = 50;

    /// <summary>只支持按操作时间排序：asc / desc，其余值按 desc 处理。</summary>
    public string? SortOrder { get; set; }
}

public sealed class LegacyEmployeeLogItemDto
{
    public string Id { get; set; } = string.Empty;

    public string? EmployeeId { get; set; }

    public string? EmployeeName { get; set; }

    public string? Operation { get; set; }

    public string? OperationDetail { get; set; }

    /// <summary>门店本地墙钟时间（DateTimeKind.Unspecified，序列化不带时区后缀）。</summary>
    public DateTime OperationTime { get; set; }

    public string? DeviceCode { get; set; }

    public string? StoreCode { get; set; }

    /// <summary>收银机上传该条日志的时间，与 OperationTime 同为墙钟口径。</summary>
    public DateTime LastUploadTime { get; set; }
}

public sealed class LegacyEmployeeLogOperationCountDto
{
    public string? Operation { get; set; }

    public int Count { get; set; }
}

public sealed class LegacyEmployeeLogEmployeeOptionDto
{
    public string? EmployeeId { get; set; }

    public string? EmployeeName { get; set; }

    public int Count { get; set; }
}

public sealed class LegacyEmployeeLogDeviceOptionDto
{
    public string? DeviceCode { get; set; }

    public int Count { get; set; }
}

public sealed class LegacyEmployeeLogListResultDto
{
    public List<LegacyEmployeeLogItemDto> Items { get; set; } = new();

    /// <summary>符合全部条件的记录数。</summary>
    public int Total { get; set; }

    public int PageNumber { get; set; }

    public int PageSize { get; set; }

    /// <summary>按操作类型计数：套用除「操作类型」以外的全部条件，供汇总条与快捷筛选显示基数。</summary>
    public List<LegacyEmployeeLogOperationCountDto> OperationCounts { get; set; } = new();

    /// <summary>该分店、该时间范围内出现过的员工（不受员工、设备、关键字条件影响），供筛选下拉。</summary>
    public List<LegacyEmployeeLogEmployeeOptionDto> Employees { get; set; } = new();

    /// <summary>该分店、该时间范围内出现过的设备（不受员工、设备、关键字条件影响），供筛选下拉。</summary>
    public List<LegacyEmployeeLogDeviceOptionDto> Devices { get; set; } = new();
}

/// <summary>一条日志及其同设备前后若干分钟内的操作，供详情抽屉还原操作过程。</summary>
public sealed class LegacyEmployeeLogContextDto
{
    public LegacyEmployeeLogItemDto Target { get; set; } = new();

    public int WindowMinutes { get; set; }

    /// <summary>按操作时间升序，包含 Target 本身；超过上限时截断。</summary>
    public List<LegacyEmployeeLogItemDto> Neighbors { get; set; } = new();

    public bool Truncated { get; set; }
}
