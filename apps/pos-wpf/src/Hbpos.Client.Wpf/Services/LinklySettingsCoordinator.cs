using System.Collections.ObjectModel;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Services;

public sealed class LinklySettingsState
{
    public LinklySettingsMode SelectedMode = LinklySettingsMode.LocalIp;
    public string HostText = CardTerminalConfiguration.Default.LinklyHost;
    public string PortText = CardTerminalConfiguration.Default.LinklyPort.ToString();
    public string TimeoutSecondsText = CardTerminalConfiguration.Default.TerminalTimeoutSeconds.ToString();
    public string PairCodeText = string.Empty;
    public string CloudUsernameText = string.Empty;
    public string CloudPasswordText = string.Empty;
    public bool HasSavedCloudPassword;
    public bool HasSavedCloudSecret;
    public bool IsSandbox;
    public bool ConnectionSucceeded;
    public bool? PinPadLoggedOn;
    public CardTerminalConfiguration LoadedConfiguration = CardTerminalConfiguration.Default;
    public int SecretStatusVersion;
    public int CredentialStatusVersion;
    public int CredentialEditVersion;
    public bool HasCloudPasswordInput;
}

public sealed class LinklySettingsCoordinator
{
    private readonly ICardTerminalSetupService _setup;
    private readonly ICardRecoveryResultDialogService? _cardRecoveryDialog;
    private readonly Func<CardTerminalEnvironment> _getEnv;
    private readonly Func<Func<Task>, string?, Task> _runBusy;
    private readonly Action<string, object[]> _setStatus;
    private readonly Action<string> _setStatusOverride;
    private readonly Action<string, object[]> _setLinklyTestStatus;
    private readonly Action<string> _setLinklyTestStatusOverride;
    private readonly Action _clearLinklyTestStatus;
    private readonly Action _resetConnectionTest;
    private readonly Action _raiseCommandStates;
    private readonly Func<string, string> _T;
    private readonly Func<LinklySettingsMode> _getPrimaryMode;
    private readonly Action<LinklyModePriorityItem?> _selectPriorityMode;

    public LinklySettingsCoordinator(
        ICardTerminalSetupService setup,
        ICardRecoveryResultDialogService? cardRecoveryDialog,
        Func<CardTerminalEnvironment> getEnv,
        Func<Func<Task>, string?, Task> runBusy,
        Action<string, object[]> setStatus,
        Action<string> setStatusOverride,
        Action<string, object[]> setLinklyTestStatus,
        Action<string> setLinklyTestStatusOverride,
        Action clearLinklyTestStatus,
        Action resetConnectionTest,
        Action raiseCommandStates,
        Func<string, string> t,
        Func<LinklySettingsMode> getPrimaryMode,
        Action<LinklyModePriorityItem?> selectPriorityMode)
    {
        _setup = setup;
        _cardRecoveryDialog = cardRecoveryDialog;
        _getEnv = getEnv;
        _runBusy = runBusy;
        _setStatus = setStatus;
        _setStatusOverride = setStatusOverride;
        _setLinklyTestStatus = setLinklyTestStatus;
        _setLinklyTestStatusOverride = setLinklyTestStatusOverride;
        _clearLinklyTestStatus = clearLinklyTestStatus;
        _resetConnectionTest = resetConnectionTest;
        _raiseCommandStates = raiseCommandStates;
        _T = t;
        _getPrimaryMode = getPrimaryMode;
        _selectPriorityMode = selectPriorityMode;
    }

    // ── Test ──

    public async Task TestAsync(LinklySettingsState s)
    {
        await _runBusy(async () =>
        {
            try
            {
                s.ConnectionSucceeded = false;
                s.PinPadLoggedOn = null;
                _clearLinklyTestStatus();
                var testMode = _getPrimaryMode();
                LinklyConnectionTestResult result;
                if (testMode == LinklySettingsMode.CloudBackendAsync)
                    result = await _setup.TestLinklyCloudBackendConnectionAsync(_getEnv());
                else if (testMode == LinklySettingsMode.CloudDirectSync)
                    result = await _setup.TestLinklyCloudConnectionAsync(_getEnv());
                else
                {
                    var host = NormalizeHost(s.HostText);
                    var port = ParsePort(s.PortText);
                    _setLinklyTestStatus("settings.linkly.localIp.testing", [host, port]);
                    result = await _setup.TestLinklyConnectionAsync(
                        host, port, TimeSpan.FromSeconds(ParseTimeoutSeconds(s.TimeoutSecondsText)));
                }

                s.ConnectionSucceeded = result.Succeeded;
                s.PinPadLoggedOn = result.PinPadLoggedOn;
                if (string.IsNullOrWhiteSpace(result.Message))
                    SetLinklyTestResult(result.Succeeded);
                else
                {
                    _setLinklyTestStatusOverride(result.Message);
                    _setStatusOverride(result.Message);
                }
            }
            catch (Exception ex)
            {
                _setLinklyTestStatusOverride(ex.Message);
                _setStatusOverride(ex.Message);
                throw;
            }
        }, "linkly test");
    }

    public async Task LogonAsync(LinklySettingsState s)
    {
        await _runBusy(async () =>
        {
            try
            {
                var host = NormalizeHost(s.HostText);
                var port = ParsePort(s.PortText);
                _setLinklyTestStatus("settings.linkly.localIp.loggingOn", [host, port]);
                var result = await _setup.LogonLinklyAsync(
                    host,
                    port,
                    TimeSpan.FromSeconds(ParseTimeoutSeconds(s.TimeoutSecondsText)));

                // 登录失败不撤销已通过的连通性测试，保存配置仍应保持可用。
                s.PinPadLoggedOn = result.Succeeded;
                if (string.IsNullOrWhiteSpace(result.Message))
                {
                    var key = result.Succeeded
                        ? "settings.linkly.localIp.logonSuccess"
                        : result.ResultUnknown
                            ? "settings.linkly.localIp.logonUnknown"
                            : "settings.linkly.localIp.logonFailed";
                    _setLinklyTestStatus(key, Arg());
                    _setStatus(key, Arg());
                }
                else
                {
                    _setLinklyTestStatusOverride(result.Message);
                    _setStatusOverride(result.Message);
                }
            }
            catch (Exception ex)
            {
                s.PinPadLoggedOn = false;
                _setLinklyTestStatusOverride(ex.Message);
                _setStatusOverride(ex.Message);
                throw;
            }
        }, "linkly logon");
    }

    public async Task TestTransactionStatusAsync(LinklySettingsState s)
    {
        await _runBusy(async () =>
        {
            try
            {
                _clearLinklyTestStatus();
                var result = await _setup.TestLinklyCloudBackendTransactionStatusAsync(_getEnv());
                if (string.IsNullOrWhiteSpace(result.Message))
                    SetLinklyTestResult(result.Succeeded);
                else
                {
                    _setLinklyTestStatusOverride(result.Message);
                    _setStatusOverride(result.Message);
                }
                ShowFailedLastTransactionDialogIfNeeded(result);
            }
            catch (Exception ex)
            {
                _setLinklyTestStatusOverride(ex.Message);
                _setStatusOverride(ex.Message);
                throw;
            }
        }, "linkly transaction status test");
    }

    // ── Cloud pairing ──

    public async Task PairCloudAsync(LinklySettingsState s) => await PairCloudAsync(s.CloudPasswordText, s);

    public async Task PairCloudAsync(string password, LinklySettingsState s)
    {
        var env = _getEnv();
        var pairCode = s.PairCodeText;
        var username = s.CloudUsernameText;
        Log($"pair clicked environment={env} hasUsername={!string.IsNullOrWhiteSpace(username)} hasCurrentPassword=REDACTED hasSavedPassword=REDACTED hasPairCode={!string.IsNullOrWhiteSpace(pairCode)}");
        if (!ValidatePairingInput(password, s))
            return;

        await _runBusy(async () =>
        {
            try
            {
                s.ConnectionSucceeded = false;
                s.PinPadLoggedOn = null;
                _clearLinklyTestStatus();
                var result = await _setup.PairLinklyCloudAsync(env, pairCode, username, password,
                    syncBackendTerminalCredential: s.SelectedMode == LinklySettingsMode.CloudBackendAsync);
                Log($"pair completed environment={env} currentEnvironment={_getEnv()} success={result.Succeeded} hasMessage={!string.IsNullOrWhiteSpace(result.Message)}");
                if (_getEnv() == env)
                    s.HasSavedCloudSecret = result.Succeeded || s.HasSavedCloudSecret;

                if (string.IsNullOrWhiteSpace(result.Message))
                    SetLinklyTestResult(result.Succeeded, "settings.status.linklyCloudPaired", "settings.status.linklyCloudPairFailed");
                else
                {
                    _setLinklyTestStatusOverride(result.Message);
                    _setStatusOverride(result.Message);
                }
            }
            catch (Exception ex)
            {
                _setLinklyTestStatusOverride(ex.Message);
                _setStatusOverride(ex.Message);
                throw;
            }
        }, "pair linkly cloud");
    }

    public bool ValidatePairingInput(string password, LinklySettingsState s)
    {
        if (string.IsNullOrWhiteSpace(s.CloudUsernameText))
        {
            Log($"pair validation blocked environment={_getEnv()} reason=missing-username");
            _setLinklyTestStatus("settings.linklyCloud.usernameRequired", Arg());
            _setStatus("settings.linklyCloud.usernameRequired", Arg());
            return false;
        }
        if (string.IsNullOrWhiteSpace(password) && !s.HasSavedCloudPassword)
        {
            Log($"pair validation blocked environment={_getEnv()} reason=missing-password");
            _setLinklyTestStatus("settings.linklyCloud.passwordRequired", Arg());
            _setStatus("settings.linklyCloud.passwordRequired", Arg());
            return false;
        }
        if (string.IsNullOrWhiteSpace(s.PairCodeText))
        {
            Log($"pair validation blocked environment={_getEnv()} reason=missing-pair-code");
            _setLinklyTestStatus("settings.linklyCloud.pairCodeRequired", Arg());
            _setStatus("settings.linklyCloud.pairCodeRequired", Arg());
            return false;
        }
        return true;
    }

    public async Task SaveCloudCredentialAsync(LinklySettingsState s) => await SaveCloudCredentialAsync(s.CloudPasswordText, s);

    public async Task SaveCloudCredentialAsync(string password, LinklySettingsState s)
    {
        var env = _getEnv();
        var syncBackend = s.SelectedMode == LinklySettingsMode.CloudBackendAsync;
        var username = s.CloudUsernameText;
        await _runBusy(async () =>
        {
            try
            {
                Log($"save credential clicked environment={env} hasUsername={!string.IsNullOrWhiteSpace(username)} hasPassword=REDACTED");
                await _setup.SaveLinklyCloudCredentialAsync(env, username, password, syncBackendCredential: syncBackend);
                if (_getEnv() == env)
                {
                    s.HasSavedCloudPassword = true;
                    s.CloudPasswordText = string.Empty;
                }
                Log($"save credential completed environment={env} currentEnvironment={_getEnv()} success=true");
                var key = syncBackend ? "settings.status.linklyCloudCredentialSynced" : "settings.status.linklyCloudCredentialSaved";
                _setLinklyTestStatus(key, Arg());
                _setStatus(key, Arg());
            }
            catch (Exception ex)
            {
                _setLinklyTestStatusOverride(ex.Message);
                _setStatusOverride(ex.Message);
                throw;
            }
        }, "save linkly cloud credential");
    }

    public void CancelCloudPairing(LinklySettingsState s)
    {
        Log($"pair cancel clicked environment={_getEnv()} hadPassword=REDACTED hadPairCode={!string.IsNullOrWhiteSpace(s.PairCodeText)}");
        s.PairCodeText = string.Empty;
        s.CloudPasswordText = string.Empty;
        s.HasCloudPasswordInput = false;
        _resetConnectionTest();
        _raiseCommandStates();
    }

    public async Task SaveAsync(LinklySettingsState s, ObservableCollection<LinklyModePriorityItem> modePriorityItems)
    {
        if (!s.ConnectionSucceeded) { St("settings.status.testLinklyBeforeSave"); return; }

        await _runBusy(async () =>
        {
            var config = s.LoadedConfiguration with
            {
                Processor = CardProcessorKind.Linkly,
                Environment = _getEnv(),
                LinklyConnectionMode = ToConnectionMode(_getPrimaryMode()),
                LinklyConnectionModePriority = GetConnectionModePriority(modePriorityItems),
                LinklyHost = NormalizeHost(s.HostText),
                LinklyPort = ParsePort(s.PortText),
                TerminalTimeoutSeconds = ParseTimeoutSeconds(s.TimeoutSecondsText),
                HasProtectedLinklyCloudSecret = s.HasSavedCloudSecret
            };

            if (IsCloudMode(s))
                await _setup.SaveLinklyCloudAsync(config);
            else
                await _setup.SaveLinklyAsync(config);

            s.LoadedConfiguration = config;
            St("settings.status.linklySaved");
        }, "save linkly settings");
    }

    // ── Priority ──

    public void ResetModePriority(IReadOnlyList<LinklyConnectionMode>? priority, ObservableCollection<LinklyModePriorityItem> items, LinklySettingsState s)
    {
        items.Clear();
        var normalized = CardTerminalSettings.NormalizeLinklyConnectionModePriority(priority, s.LoadedConfiguration.LinklyConnectionMode);
        foreach (var mode in normalized)
            items.Add(new LinklyModePriorityItem(ToSettingsMode(mode)));
        RefreshPriorityRanks(items);
    }

    public void PromoteModeToPrimary(LinklySettingsMode mode, ObservableCollection<LinklyModePriorityItem> items)
    {
        var item = items.FirstOrDefault(c => c.Mode == mode);
        if (item is null) return;
        var idx = items.IndexOf(item);
        if (idx <= 0) return;
        items.Move(idx, 0);
        RefreshPriorityRanks(items);
    }

    public IReadOnlyList<LinklyConnectionMode> GetConnectionModePriority(ObservableCollection<LinklyModePriorityItem> items) =>
        items.Select(item => ToConnectionMode(item.Mode)).ToArray();

    public void RefreshPriorityRanks(ObservableCollection<LinklyModePriorityItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].Rank = i + 1;
    }

    public bool CanMovePriorityUp(LinklyModePriorityItem? item, ObservableCollection<LinklyModePriorityItem> items, bool isBusy) =>
        !isBusy && item is not null && items.IndexOf(item) > 0;

    public bool CanMovePriorityDown(LinklyModePriorityItem? item, ObservableCollection<LinklyModePriorityItem> items, bool isBusy) =>
        !isBusy && item is not null && items.IndexOf(item) is var i && i >= 0 && i < items.Count - 1;

    public void MovePriorityUp(LinklyModePriorityItem? item, ObservableCollection<LinklyModePriorityItem> items, LinklySettingsState s) =>
        MovePriority(item, -1, items, s);

    public void MovePriorityDown(LinklyModePriorityItem? item, ObservableCollection<LinklyModePriorityItem> items, LinklySettingsState s) =>
        MovePriority(item, 1, items, s);

    private void MovePriority(LinklyModePriorityItem? item, int offset, ObservableCollection<LinklyModePriorityItem> items, LinklySettingsState s)
    {
        if (item is null) return;
        var old = items.IndexOf(item);
        var next = old + offset;
        if (old < 0 || next < 0 || next >= items.Count) return;
        items.Move(old, next);
        RefreshPriorityRanks(items);
        s.SelectedMode = _getPrimaryMode();
        _resetConnectionTest();
        _raiseCommandStates();
    }

    public void SelectPriorityMode(LinklyModePriorityItem? item, LinklySettingsState s)
    {
        if (item is null) return;
        s.SelectedMode = item.Mode;
    }

    // ── Cloud secret / credential ──

    public async Task RefreshCloudSecretStatusAsync(CardTerminalEnvironment environment, LinklySettingsState s)
    {
        var version = Interlocked.Increment(ref s.SecretStatusVersion);
        try
        {
            var hasSecret = await _setup.HasLinklyCloudSecretAsync(environment);
            if (version == s.SecretStatusVersion && _getEnv() == environment)
                s.HasSavedCloudSecret = hasSecret;
        }
        catch (Exception ex) { Log($"refresh linkly cloud secret status failed environment={environment} message={LogVal(ex.Message)}"); }
    }

    public async Task LoadCloudCredentialFieldsAsync(CardTerminalEnvironment environment, LinklySettingsState s)
    {
        try
        {
            var credential = await _setup.LoadLinklyCloudCredentialAsync(environment);
            ApplyCredentialFields(credential, hasPwd => s.HasSavedCloudPassword = hasPwd);
        }
        catch (Exception ex)
        {
            Log($"load linkly cloud credential failed environment={environment} message={LogVal(ex.Message)}");
            ClearCloudCredentialFields(s);
        }
    }

    public async Task RefreshCloudCredentialStatusAsync(CardTerminalEnvironment environment, LinklySettingsState s)
    {
        var version = Interlocked.Increment(ref s.CredentialStatusVersion);
        var editVer = s.CredentialEditVersion;
        try
        {
            var credential = await _setup.LoadLinklyCloudCredentialAsync(environment);
            if (version == s.CredentialStatusVersion && editVer == s.CredentialEditVersion && _getEnv() == environment)
                ApplyCredentialFields(credential, hasPwd => s.HasSavedCloudPassword = hasPwd);
        }
        catch (Exception ex)
        {
            Log($"refresh linkly cloud credential status failed environment={environment} message={LogVal(ex.Message)}");
            if (version == s.CredentialStatusVersion && editVer == s.CredentialEditVersion && _getEnv() == environment)
                ClearCloudCredentialFields(s);
        }
    }

    public void ClearCloudCredentialFields(LinklySettingsState s)
    {
        s.CloudUsernameText = string.Empty;
        s.CloudPasswordText = string.Empty;
        s.HasSavedCloudPassword = false;
    }

    // ── Helpers ──

    private void St(string key) => _setStatus(key, Arg());
    private void SetLinklyTestResult(bool ok, string okKey = "settings.status.linklyTestSuccess", string failKey = "settings.status.linklyTestFailed")
    {
        var key = ok ? okKey : failKey;
        _setLinklyTestStatus(key, Arg());
        _setStatus(key, Arg());
    }

    private void ShowFailedLastTransactionDialogIfNeeded(LinklyConnectionTestResult result)
    {
        if (result.Succeeded || result.StatusTest is not { } status || !IsFailedLastTransactionStatus(status, result.Message))
            return;

        _cardRecoveryDialog?.Show(new CardRecoveryResultDialogViewModel(
            _T("cardRecovery.dialog.title.failedLastTransaction"),
            _T("cardRecovery.dialog.message.failedLastTransaction"),
            CardRecoveryResultSeverity.Warning,
            orderGuid: null, amount: null,
            sessionId: status.TransactionReference,
            txnRef: status.TxnRef,
            responseCode: status.ResponseCode,
            responseText: status.ResponseText ?? result.Message,
            timestamp: status.Timestamp ?? DateTimeOffset.Now));
    }

    private static bool IsFailedLastTransactionStatus(LinklyStatusTestDetails status, string? message)
    {
        var code = status.ResponseCode?.Trim();
        if (string.Equals(code, "00", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "T0", StringComparison.OrdinalIgnoreCase))
            return false;
        return ContainsFailureText(status.ResponseText) || ContainsFailureText(message);
    }

    private static bool ContainsFailureText(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("DECLINED", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("FAILED", StringComparison.OrdinalIgnoreCase));

    private static void ApplyCredentialFields(LinklyCloudCredentialSettings c, Action<bool> setHasPwd) =>
        setHasPwd(c.HasProtectedPassword);

    private static bool IsCloudMode(LinklySettingsState s) =>
        s.SelectedMode is LinklySettingsMode.CloudDirectSync or LinklySettingsMode.CloudBackendAsync;

    private static LinklySettingsMode ToSettingsMode(LinklyConnectionMode m) =>
        CardTerminalSettings.NormalizeLinklyConnectionMode(m) switch
        {
            LinklyConnectionMode.CloudDirectSync => LinklySettingsMode.CloudDirectSync,
            LinklyConnectionMode.CloudBackendAsync => LinklySettingsMode.CloudBackendAsync,
            _ => LinklySettingsMode.LocalIp
        };

    private static LinklyConnectionMode ToConnectionMode(LinklySettingsMode m) =>
        m switch
        {
            LinklySettingsMode.CloudDirectSync => LinklyConnectionMode.CloudDirectSync,
            LinklySettingsMode.CloudBackendAsync => LinklyConnectionMode.CloudBackendAsync,
            _ => LinklyConnectionMode.LocalIp
        };

    private static int ParsePort(string? t) => int.TryParse(t, out var p) && p is > 0 and <= 65535 ? p : CardTerminalConfiguration.Default.LinklyPort;
    private static int ParseTimeoutSeconds(string? t) => int.TryParse(t, out var s) && s > 0 ? s : CardTerminalConfiguration.Default.TerminalTimeoutSeconds;
    private static string NormalizeHost(string? h) => string.IsNullOrWhiteSpace(h) ? CardTerminalConfiguration.Default.LinklyHost : h.Trim();

    private static object[] Arg() => Array.Empty<object>();
    private static void Log(string msg) => LinklyJsonLog.WriteMessage("LinklyCloud", "settings-ui", $"settings ui {msg}");
    private static string LogVal(string? v) => string.IsNullOrWhiteSpace(v) ? "<null>" : v;

    // Public helpers for external callers (ViewModel On*Changed, LoadAsync)
    public void ResetConnectionTest(LinklySettingsState s)
    {
        s.ConnectionSucceeded = false;
        s.PinPadLoggedOn = null;
        _clearLinklyTestStatus();
    }
    public void ClearTestStatus() => _clearLinklyTestStatus();
    public void SetTestStatus(string key) => _setLinklyTestStatus(key, Arg());
    public void SetTestStatus(string key, object a1) => _setLinklyTestStatus(key, new[] { a1 });

    // Called from SettingsViewModel after LoadAsync / On*Changed to sync UI → state
    public void ApplyInitialState(LinklySettingsState s, CardTerminalConfiguration loadedConfig, CardTerminalEnvironment env)
    {
        s.LoadedConfiguration = loadedConfig;
        s.IsSandbox = env == CardTerminalEnvironment.Sandbox;
    }
}
