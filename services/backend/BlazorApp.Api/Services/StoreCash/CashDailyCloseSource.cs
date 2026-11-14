namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 一份现金日结存档（来自收银端日结保存后上传到后端的记录）。
/// 同一分店同一设备同一营业日可以有多份（重复保存、跨班）；CountedCash 是点钱得到的实点现金，
/// 点钱前备用金已取出，所以它就是当天要存银行的营业现金。
/// </summary>
public sealed record CashCloseArchive(
    string StoreCode,
    DateOnly BusinessDate,
    string DeviceCode,
    string CloseId,
    DateTime PeriodFromUtc,
    DateTime PeriodToUtc,
    DateTime SavedAtUtc,
    decimal CountedCash,
    decimal ExpectedCash,
    // 本期收银端现金存入 / 取出合计（冲正已抵消）；旧客户端、回填行为 0。
    decimal CashIn = 0m,
    decimal CashOut = 0m
)
{
    /// <summary>差异 = 实点 − 系统应有，与收银端日结口径一致。</summary>
    public decimal Variance => CountedCash - ExpectedCash;

    /// <summary>
    /// 入池金额 = 实点 + 取出 − 存入：交店长的钱还在店里要加回；小额支出加回后由自动生成的现金支出再扣一次；
    /// 补零钱来自保险柜（已在池中）要扣回。恒等于 现金净额 + 进位 + 差异。没有现金进出时等于实点。
    /// </summary>
    public decimal PoolInflow => CountedCash + CashOut - CashIn;
}

/// <summary>
/// 现金日结的取数口：现金池的流入只认日结实点现金。日结存档上传后端由另一条工作线实现，
/// 这里只约定「按分店、营业日区间取存档」的契约，真实实现接入前注册 <see cref="NotConnectedCashDailyCloseSource"/>。
/// </summary>
public interface ICashDailyCloseSource
{
    /// <summary>日结数据是否已接入。未接入时现金池余额不可算，页面要提示而不是显示 0。</summary>
    bool IsConnected { get; }

    /// <summary>取 [from, to]（含两端，门店本地营业日）内这些分店的全部日结存档，不做去重。</summary>
    Task<IReadOnlyList<CashCloseArchive>> GetArchivesAsync(
        IReadOnlyCollection<string> storeCodes,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken
    );
}

/// <summary>后端日结尚未接入时的占位实现：恒返回空，并声明未接入。</summary>
public sealed class NotConnectedCashDailyCloseSource : ICashDailyCloseSource
{
    public bool IsConnected => false;

    public Task<IReadOnlyList<CashCloseArchive>> GetArchivesAsync(
        IReadOnlyCollection<string> storeCodes,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<CashCloseArchive>>(Array.Empty<CashCloseArchive>());
}
