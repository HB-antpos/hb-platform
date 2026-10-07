using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 分店现金管理：现金池总览、日结存档纳入、存银行、现金支出、期初与盘点。
/// 每个方法都接收已解析好的 <see cref="CashAccess"/>，由服务统一做分店范围、T2 可见性、补录范围与作废时限校验，
/// 控制器只负责取权限和转 HTTP，不重复写业务规则。
/// </summary>
public interface IStoreCashService
{
    Task<ApiResponse<CashContextDto>> GetContextAsync(CashAccess access, CancellationToken cancellationToken);

    Task<ApiResponse<CashStoreSummaryDto>> GetSummaryAsync(
        CashAccess access,
        string? storeCode,
        CancellationToken cancellationToken
    );

    /// <summary>Web 多店总览：每个可见分店一行，区间默认本月 1 日到今天。</summary>
    Task<ApiResponse<CashOverviewDto>> GetOverviewAsync(
        CashAccess access,
        DateOnly? from,
        DateOnly? to,
        IReadOnlyList<string>? storeCodes,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashDailyDto>> GetDailyAsync(
        CashAccess access,
        string? storeCode,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashDailyDeviceDto>> SetCloseSelectionAsync(
        CashAccess access,
        CashCloseSelectionRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashAttachmentUploadSignatureDto>> CreateAttachmentUploadAsync(
        CashAccess access,
        CashAttachmentUploadRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashDepositDetailDto>> CreateDepositAsync(
        CashAccess access,
        CreateCashDepositRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashPagedDto<CashDepositListItemDto>>> ListDepositsAsync(
        CashAccess access,
        string? storeCode,
        DateOnly? from,
        DateOnly? to,
        bool includeVoided,
        int limit,
        int offset,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashDepositDetailDto>> GetDepositAsync(
        CashAccess access,
        string depositGuid,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashDepositDetailDto>> VoidDepositAsync(
        CashAccess access,
        string depositGuid,
        CashVoidRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashExpenseDetailDto>> CreateExpenseAsync(
        CashAccess access,
        CreateCashExpenseRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashPagedDto<CashExpenseListItemDto>>> ListExpensesAsync(
        CashAccess access,
        string? storeCode,
        DateOnly? from,
        DateOnly? to,
        string? category,
        string? reviewStatus,
        bool includeVoided,
        int limit,
        int offset,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashExpenseDetailDto>> GetExpenseAsync(
        CashAccess access,
        string expenseGuid,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashExpenseDetailDto>> ReviewExpenseAsync(
        CashAccess access,
        string expenseGuid,
        CashExpenseReviewRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashExpenseDetailDto>> VoidExpenseAsync(
        CashAccess access,
        string expenseGuid,
        CashVoidRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<List<CashBalanceEntryDto>>> ListEntriesAsync(
        CashAccess access,
        string? storeCode,
        bool includeVoided,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashBalanceEntryDto>> SetOpeningAsync(
        CashAccess access,
        SetCashOpeningRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashBalanceEntryDto>> CreateCountAsync(
        CashAccess access,
        CreateCashCountRequest request,
        CancellationToken cancellationToken
    );

    Task<ApiResponse<CashBalanceEntryDto>> VoidEntryAsync(
        CashAccess access,
        string entryGuid,
        CashVoidRequest request,
        CancellationToken cancellationToken
    );
}
