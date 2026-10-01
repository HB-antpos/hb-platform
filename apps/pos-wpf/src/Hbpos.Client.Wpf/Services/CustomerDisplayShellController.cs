using System.Diagnostics;
using System.Windows;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 客显壳层控制器：承接客显模式切换、窗口打开/关闭、关闭事件回写、日志记录与状态结果应用。
/// MainViewModel 只保留绑定属性和命令转发，客显业务逻辑集中在此类中。
/// </summary>
internal sealed class CustomerDisplayShellController
{
    private readonly ICustomerDisplayOrchestrator _orchestrator;
    private readonly ILocalizationService _localization;
    private readonly Func<CustomerDisplayViewModel> _getCustomerDisplay;
    private readonly Func<PosSessionState> _getSession;
    private readonly Func<PosCartService> _getCart;
    private readonly Action<CustomerDisplayWindowMode> _setMode;
    private readonly Func<CustomerDisplayWindowMode> _getCurrentMode;
    private readonly Action<string> _setStatusMessage;

    private bool _prewarmed;

    public CustomerDisplayShellController(
        ICustomerDisplayOrchestrator orchestrator,
        ILocalizationService localization,
        Func<CustomerDisplayViewModel> getCustomerDisplay,
        Func<PosSessionState> getSession,
        Func<PosCartService> getCart,
        Action<CustomerDisplayWindowMode> setMode,
        Func<CustomerDisplayWindowMode> getCurrentMode,
        Action<string> setStatusMessage)
    {
        _orchestrator = orchestrator;
        _localization = localization;
        _getCustomerDisplay = getCustomerDisplay;
        _getSession = getSession;
        _getCart = getCart;
        _setMode = setMode;
        _getCurrentMode = getCurrentMode;
        _setStatusMessage = setStatusMessage;
    }

    public void Toggle(Window? owner)
    {
        var targetMode = _orchestrator.GetNextMode(_getCurrentMode());
        SetMode(targetMode, owner, rememberChoice: true);
    }

    public void Close(Window? owner) => SetMode(CustomerDisplayWindowMode.Closed, owner, rememberChoice: true);

    public void ShowNormal(Window? owner) => SetMode(CustomerDisplayWindowMode.Normal, owner, rememberChoice: true);

    public void ShowFullscreen(Window? owner) => SetMode(CustomerDisplayWindowMode.Fullscreen, owner, rememberChoice: true);

    /// <param name="rememberChoice">
    /// 收银员手动切换时为 true，记住结果供下次启动恢复；程序退出、断开第二屏、重新注册设备等系统关闭保持 false，不改上次设置。
    /// </param>
    public void SetMode(CustomerDisplayWindowMode mode, Window? owner, bool rememberChoice = false)
    {
        var session = _getSession();
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"viewmodel set-mode start requestedMode={mode} currentMode={_getCurrentMode()} ownerPresent={owner is not null} store={session.StoreCode} device={session.DeviceCode}");
        var result = _orchestrator.SetMode(mode, _getCustomerDisplay(), session, _getCart(), owner);
        ApplyResult(result);
        // 只记住真正生效的模式：例如想打开但没有第二屏而被关闭时，保留原来的上次设置。
        if (rememberChoice && result.Mode == mode)
        {
            _orchestrator.RememberMode(mode);
        }
        stopwatch.Stop();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"viewmodel set-mode completed requestedMode={mode} resultMode={result.Mode} open={result.Mode != CustomerDisplayWindowMode.Closed} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    /// <summary>主窗口与客显互换屏幕；客显模式不变，只更新状态提示。</summary>
    public void SwapScreens(Window owner)
    {
        var session = _getSession();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"viewmodel swap-screens start currentMode={_getCurrentMode()} store={session.StoreCode} device={session.DeviceCode}");
        var result = _orchestrator.SwapDisplays(_getCustomerDisplay(), owner);
        if (!string.IsNullOrWhiteSpace(result.StatusMessageKey))
        {
            _setStatusMessage(_localization.T(result.StatusMessageKey));
        }
    }

    /// <summary>
    /// 启动后按本机记住的上次模式恢复客显；上次是关闭或从未记录时不打开。
    /// 恢复本身不改记录，没有第二屏时下次启动仍会再试。
    /// </summary>
    public async Task RestoreRememberedModeAsync(Window? owner)
    {
        var rememberedMode = await _orchestrator.LoadRememberedModeAsync();
        var session = _getSession();
        if (rememberedMode == CustomerDisplayWindowMode.Closed)
        {
            ConsoleLog.Write(
                "CustomerDisplay",
                $"startup restore skipped store={session.StoreCode} device={session.DeviceCode} reason=remembered-closed");
            return;
        }

        ConsoleLog.Write(
            "CustomerDisplay",
            $"startup restore start store={session.StoreCode} device={session.DeviceCode} rememberedMode={rememberedMode} ownerPresent={owner is not null}");
        SetMode(rememberedMode, owner);
    }

    public void Open(Window? owner)
    {
        var session = _getSession();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"startup open-window request store={session.StoreCode} device={session.DeviceCode} ownerPresent={owner is not null}");
        SetMode(CustomerDisplayWindowMode.Fullscreen, owner);
    }

    /// <summary>
    /// 预热客显 ViewModel，避免首次打开窗口时阻塞 UI。
    /// 启动阶段默认跳过预热以加快启动速度。
    /// </summary>
    public void Prewarm()
    {
        if (_prewarmed)
        {
            var session = _getSession();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"startup prewarm skipped store={session.StoreCode} device={session.DeviceCode} reason=already-prewarmed");
            return;
        }

        var session2 = _getSession();
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"startup prewarm start store={session2.StoreCode} device={session2.DeviceCode} currentMode={_getCurrentMode()}");
        try
        {
            _orchestrator.Prewarm(_getCustomerDisplay(), session2, _getCart());
            _prewarmed = true;
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"startup prewarm completed store={session2.StoreCode} device={session2.DeviceCode} currentMode={_getCurrentMode()} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"startup prewarm failed store={session2.StoreCode} device={session2.DeviceCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 将 orchestrator 返回的结果应用到绑定属性和状态消息。
    /// </summary>
    private void ApplyResult(CustomerDisplayWindowResult result)
    {
        _setMode(result.Mode);
        if (!string.IsNullOrWhiteSpace(result.StatusMessageKey))
        {
            _setStatusMessage(_localization.T(result.StatusMessageKey));
        }
    }
}
