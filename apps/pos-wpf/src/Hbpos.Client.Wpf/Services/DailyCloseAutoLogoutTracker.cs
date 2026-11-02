namespace Hbpos.Client.Wpf.Services;

public enum DailyCloseAutoLogoutDecision
{
    /// <summary>没有待执行的自动登出。</summary>
    None,

    /// <summary>还没到时间，或日结页仍在忙，继续等。</summary>
    Wait,

    /// <summary>收银员已换人 / 已登出，或又开始收银：本次自动登出作废。</summary>
    Cancelled,

    /// <summary>到时间了，现在登出。</summary>
    Logout
}

/// <summary>
/// 「日结完成后自动登出收银员」的判定（纯逻辑，便于单测）：日结保存成功后排一次，等待 <see cref="Delay"/>
/// 让收银员看清日结 / 结算结果；期间收银员又开始收银（购物车有变化、付款进行中）或已换人就作废，绝不打断正在进行的销售。
/// 由主界面的 1 秒时钟驱动，不持久化：应用重开本来就需要重新登录。
/// </summary>
public sealed class DailyCloseAutoLogoutTracker(TimeSpan? delay = null)
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(30);

    private string? _cashierKey;
    private int _cartLineCount;
    private DateTimeOffset _dueAtUtc;

    public TimeSpan Delay { get; } = delay ?? DefaultDelay;

    public bool IsPending => _cashierKey is not null;

    /// <summary>日结后排定的凌晨重启时间（只用于登出后的提示文案）。</summary>
    public DateTimeOffset? RestartAt { get; private set; }

    public TimeSpan Schedule(string cashierKey, int cartLineCount, DateTimeOffset nowUtc, DateTimeOffset? restartAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(cashierKey);
        _cashierKey = cashierKey;
        _cartLineCount = cartLineCount;
        _dueAtUtc = nowUtc + Delay;
        RestartAt = restartAt;
        return Delay;
    }

    public DailyCloseAutoLogoutDecision Evaluate(
        DateTimeOffset nowUtc,
        string? currentCashierKey,
        int cartLineCount,
        bool isPaymentActive,
        bool isScreenBusy)
    {
        if (!IsPending)
        {
            return DailyCloseAutoLogoutDecision.None;
        }

        if (currentCashierKey != _cashierKey || cartLineCount != _cartLineCount || isPaymentActive)
        {
            Cancel();
            return DailyCloseAutoLogoutDecision.Cancelled;
        }

        if (isScreenBusy)
        {
            // 日结页又在忙（重打 / 结算 / 查历史）：忙完后再给完整的等待时间。
            _dueAtUtc = nowUtc + Delay;
            return DailyCloseAutoLogoutDecision.Wait;
        }

        if (nowUtc < _dueAtUtc)
        {
            return DailyCloseAutoLogoutDecision.Wait;
        }

        _cashierKey = null;
        return DailyCloseAutoLogoutDecision.Logout;
    }

    public void Cancel()
    {
        _cashierKey = null;
        RestartAt = null;
    }

    public static string CreateCashierKey(string cashierId, string userGuid) => $"{cashierId}|{userGuid}";
}
