using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>总部下发开关右侧状态标签的种类。</summary>
public enum PosSettingStatusKind
{
    /// <summary>总部没有下发这一项：不显示标签，开关按本机值正常使用。</summary>
    None,

    /// <summary>总部已锁定（在线）：显示锁图标 +「总部设置 · 全局 / 门店 / 设备」，开关置灰。</summary>
    Locked,

    /// <summary>总部下发了值、但当前离线而解锁：显示「离线可改」。</summary>
    OfflineEditable,

    /// <summary>离线期间在本机临时改过：显示「离线临时修改」（黄色），恢复联网同步后被总部值覆盖。</summary>
    OfflineOverride
}

/// <summary>
/// 「业务开关」页上的一行，同时也是旧页面（付款方式 / 小票打印机 / 系统维护 / Zeller）里同名开关的「锁定状态」载体：
/// 旧控件绑定 <see cref="IsUnlocked"/> 与 <see cref="StatusText"/> 即可与本页保持一致。
/// 状态全部来自 <see cref="IPosSettingsService"/>，行自己不保存任何业务值。
/// </summary>
public sealed class PosSettingRowViewModel : ObservableObject
{
    private readonly Func<string, object[], string> _text;
    private readonly string _textKeyPrefix;
    private readonly Func<PosSettingRowViewModel, bool, Task> _toggleAsync;

    private bool _isOn;
    private bool _isApplying;
    private bool _isManaged;
    private bool _isLocked;
    private bool _isOfflineOverride;
    private string? _source;

    public PosSettingRowViewModel(
        string key,
        string textKeyPrefix,
        Func<string, object[], string> text,
        Func<PosSettingRowViewModel, bool, Task> toggleAsync)
    {
        Key = key;
        _textKeyPrefix = textKeyPrefix;
        _text = text;
        _toggleAsync = toggleAsync;
    }

    public string Key { get; }

    public string TitleText => _text(_textKeyPrefix + ".title", []);

    public string DescriptionText => _text(_textKeyPrefix + ".description", []);

    /// <summary>
    /// 开关当前值（双向绑定）。用户拨动时先让界面立即跟手，再交给 ViewModel 调服务；
    /// 服务返回「被锁定 / 失败」时由 ViewModel 把它回滚到真实值。服务端推来的状态走 <see cref="ApplyState"/>，不会再次触发写入。
    /// </summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            _isOn = value;
            OnPropertyChanged();
            if (!_isApplying)
            {
                _ = _toggleAsync(this, value);
            }
        }
    }

    public bool IsLocked => _isLocked;

    /// <summary>开关是否可点：锁定时置灰；离线（未锁定）时可改。</summary>
    public bool IsUnlocked => !_isLocked;

    public bool IsManaged => _isManaged;

    public bool IsOfflineOverride => _isOfflineOverride;

    public PosSettingStatusKind StatusKind
    {
        get
        {
            // 「离线临时修改」比「离线可改」更具体，优先显示；锁定与离线互斥（离线自动解锁）。
            if (_isOfflineOverride)
            {
                return PosSettingStatusKind.OfflineOverride;
            }

            if (_isLocked)
            {
                return PosSettingStatusKind.Locked;
            }

            return _isManaged ? PosSettingStatusKind.OfflineEditable : PosSettingStatusKind.None;
        }
    }

    public bool HasStatus => StatusKind != PosSettingStatusKind.None;

    public string StatusText => StatusKind switch
    {
        PosSettingStatusKind.OfflineOverride => _text("settings.posSettings.status.offlineOverride", []),
        PosSettingStatusKind.OfflineEditable => _text("settings.posSettings.status.offlineEditable", []),
        PosSettingStatusKind.Locked => LockedText(),
        _ => string.Empty
    };

    private string LockedText() => _source switch
    {
        PosSettingScopes.Global => _text("settings.posSettings.status.locked", [_text("settings.posSettings.scope.global", [])]),
        PosSettingScopes.Store => _text("settings.posSettings.status.locked", [_text("settings.posSettings.scope.store", [])]),
        PosSettingScopes.Device => _text("settings.posSettings.status.locked", [_text("settings.posSettings.scope.device", [])]),
        // 三层都没设置、由服务端给的缺省值（例如「现金存入 / 取出」默认关）：显示「总部默认」。
        _ => _text("settings.posSettings.status.lockedDefault", [])
    };

    /// <summary>用服务给出的状态刷新本行（含开关值），不触发写入。</summary>
    public void ApplyState(PosSettingState state)
    {
        _isApplying = true;
        try
        {
            _source = state.Source;
            _isManaged = state.IsManaged;
            _isLocked = state.IsLocked;
            _isOfflineOverride = state.IsOfflineOverride;
            _isOn = state.Value;
        }
        finally
        {
            _isApplying = false;
        }

        RaiseAll();
    }

    /// <summary>只回滚开关值（服务没有给出状态时的兜底），不触发写入。</summary>
    public void SetValueSilently(bool value)
    {
        _isApplying = true;
        try
        {
            _isOn = value;
        }
        finally
        {
            _isApplying = false;
        }

        // 控件已经自己翻过去了：无论值是否变化都重新通知，让它回到真实状态。
        OnPropertyChanged(nameof(IsOn));
    }

    /// <summary>切换语言后刷新文案。</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsUnlocked));
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(IsOfflineOverride));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusText));
    }
}
