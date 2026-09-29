namespace BlazorApp.Api.Services.LocalSupplierCategories;

public static class LocalSupplierCategoryErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string SupplierNotCapturable = "SUPPLIER_NOT_CAPTURABLE";
    public const string CategoryNotFound = "CATEGORY_NOT_FOUND";
    public const string CategorySupplierMismatch = "CATEGORY_SUPPLIER_MISMATCH";
    public const string FeatureDisabled = "FEATURE_DISABLED";
    public const string SupplierBusy = "SUPPLIER_CATEGORY_BUSY";
    public const string RateLimited = "SUPPLIER_CATEGORY_RATE_LIMITED";
}

/// <summary>
/// 业务校验失败，携带稳定错误码，由控制器映射为 400。
/// </summary>
public sealed class LocalSupplierCategoryValidationException : Exception
{
    public LocalSupplierCategoryValidationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// 同一供应商的采集写入正被其他请求占用（应用锁超时），扩展应退避重试。
/// </summary>
public sealed class LocalSupplierCategoryBusyException : Exception
{
    public LocalSupplierCategoryBusyException()
        : base("该供应商分类正在写入，请稍后重试。") { }
}

/// <summary>
/// 分类采集总开关已关闭。
/// </summary>
public sealed class LocalSupplierCategoryFeatureDisabledException : Exception
{
    public LocalSupplierCategoryFeatureDisabledException()
        : base("供应商分类采集已停用。") { }
}
