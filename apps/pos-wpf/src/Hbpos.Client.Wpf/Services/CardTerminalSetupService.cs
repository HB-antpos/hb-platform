using System.Net.Http;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Wpf.Services;

public sealed record LinklyTerminalAssignmentResult(
    bool Succeeded,
    string Message,
    LinklyCloudTerminalListResponse? Directory = null,
    bool Reconciled = false);

public interface ICardTerminalSetupService
{
    Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default);

    Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
        CancellationToken cancellationToken = default);

    Task SaveSquareAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default);

    Task SaveLinklyAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<LinklyLogonResult> LogonLinklyAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyLogonResult(
            false,
            "Linkly bank network logon is not supported by this setup service."));

    Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
        CardTerminalEnvironment environment,
        string pairCode,
        string? username,
        string? password,
        bool syncBackendTerminalCredential = false,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task SaveLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        bool syncBackendCredential = false,
        CancellationToken cancellationToken = default);

    Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudTerminalListResponse> ListLinklyCloudBackendTerminalsAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyCloudTerminalListResponse(
            environment.ToString(),
            SelectedTerminalId: null,
            SelectionRevision: null,
            Terminals: []));

    Task<LinklyCloudTerminalSelectionResponse> SelectLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        long? expectedRevision,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudTerminalSelectionResponse>(
            new NotSupportedException("Linkly Cloud terminal selection is unavailable."));

    Task<LinklyCloudTerminalPairResponse> PairLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        string pairCode,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudTerminalPairResponse>(
            new NotSupportedException("Linkly Cloud terminal pairing is unavailable."));

    Task<LinklyCloudTerminalConnectionTestResponse> TestLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        LinklyCloudTerminalSummary terminal,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudTerminalConnectionTestResponse>(
            new NotSupportedException("Linkly Cloud terminal connection testing is unavailable."));

    Task<LinklyTerminalAssignmentResult> AssignLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        LinklyCloudTerminalSummary terminal,
        LinklyCloudAssignableDevice? targetDevice,
        IReadOnlyList<LinklyCloudAssignableDevice> devices,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyTerminalAssignmentResult(false, "Linkly Cloud terminal assignment is unavailable."));

    Task<LinklyConnectionTestResult> TestLinklyCloudBackendTransactionStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<bool> HasLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task SaveLinklyCloudAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public sealed class CardTerminalSetupService(
    ICardTerminalSettingsStore settingsStore,
    ISquareTerminalSetupClient squareSetupClient,
    ILinklyTerminalClient linklyTerminalClient,
    ILinklyCloudApiClient? linklyCloudApiClient = null,
    ILinklyCloudCredentialApiClient? linklyCloudCredentialApiClient = null,
    ILinklyCloudTerminalClient? linklyCloudTerminalClient = null,
    ILinklyBackendTerminalClient? linklyBackendTerminalClient = null,
    DeviceAuthorizationState? deviceAuthorizationState = null,
    ILocalizationService? localization = null,
    ICardPaymentRecoveryService? cardPaymentRecoveryService = null,
    ILinklyPaymentAttemptContextAccessor? linklyPaymentAttemptContextAccessor = null,
    ILinklyTerminalSelectionTransitionGate? linklyTerminalSelectionTransitionGate = null,
    ILinklyUnresolvedSettlementReader? linklySettlementRepository = null) : ICardTerminalSetupService
{
    public Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        LogSquareSetup("load configuration requested");
        return settingsStore.LoadAsync(cancellationToken);
    }

    public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        LogSquareSetup("get cached square token requested but client-side square token access is disabled");
        // Square token 已后端化，客户端 setup service 不再读取本地 store 或返回 access token。
        return Task.FromResult<string?>(null);
    }

    public async Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        LogSquareSetup($"list locations requested environment={environment} tokenSource={DescribeTokenSource(accessToken)}");
        var setupAccessToken = ResolveSetupAccessToken(accessToken, environment);
        var locations = await squareSetupClient.ListLocationsAsync(setupAccessToken, environment, cancellationToken);
        LogSquareSetup($"list locations succeeded environment={environment} count={locations.Count}");
        return locations;
    }

    public async Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        LogSquareSetup($"list devices requested environment={environment} locationId={LogValue(locationId)} tokenSource={DescribeTokenSource(accessToken)}");
        var setupAccessToken = ResolveSetupAccessToken(accessToken, environment);
        var devices = await squareSetupClient.ListDevicesAsync(setupAccessToken, environment, locationId, cancellationToken);
        LogSquareSetup($"list devices succeeded environment={environment} locationId={LogValue(locationId)} count={devices.Count}");
        return devices.Select(WithLocalizedSquareDeviceStatusDisplayName).ToArray();
    }

    public Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceCodesSupported(environment);
        return ExecuteSquareSetupAsync(
            accessToken,
            environment,
            token => squareSetupClient.ListDeviceCodesAsync(token, environment, locationId, cancellationToken));
    }

    public Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceCodesSupported(environment);
        return ExecuteSquareSetupAsync(
            accessToken,
            environment,
            token => squareSetupClient.CreateDeviceCodeAsync(token, environment, locationId, name, cancellationToken));
    }

    public Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceCodesSupported(environment);
        return ExecuteSquareSetupAsync(
            accessToken,
            environment,
            token => squareSetupClient.GetDeviceCodeAsync(token, environment, deviceCodeId, cancellationToken));
    }

    public async Task SaveSquareAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedConfiguration = configuration with
        {
            SquareDeviceId = SquareDeviceIdNormalizer.NormalizeForTerminalCheckout(configuration.SquareDeviceId)
        };

        LogSquareSetup(
            $"save square requested environment={normalizedConfiguration.Environment} locationId={LogValue(normalizedConfiguration.SquareLocationId)} storedDeviceId={LogValue(configuration.SquareDeviceId)} savedDeviceId={LogValue(normalizedConfiguration.SquareDeviceId)} tokenSource={DescribeTokenSource(squareAccessToken)}");
        await settingsStore.SaveAsync(normalizedConfiguration, squareAccessToken, cancellationToken);
        LogSquareSetup(
            $"save square succeeded environment={normalizedConfiguration.Environment} locationId={LogValue(normalizedConfiguration.SquareLocationId)} savedDeviceId={LogValue(normalizedConfiguration.SquareDeviceId)}");
    }

    public Task SaveLinklyAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        return settingsStore.SaveAsync(configuration with { LinklyConnectionMode = LinklyConnectionMode.Local }, squareAccessToken: null, cancellationToken);
    }

    public Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return linklyTerminalClient.TestConnectionAsync(host, port, timeout, cancellationToken);
    }

    public Task<LinklyLogonResult> LogonLinklyAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return linklyTerminalClient.LogonAsync(host, port, timeout, cancellationToken);
    }

    public async Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
        CardTerminalEnvironment environment,
        string pairCode,
        string? username,
        string? password,
        bool syncBackendTerminalCredential = false,
        CancellationToken cancellationToken = default)
    {
        if (linklyCloudApiClient is null)
        {
            LogLinklyCloudSetup($"pair blocked environment={environment} reason=missing-dependencies");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        if (string.IsNullOrWhiteSpace(pairCode))
        {
            LogLinklyCloudSetup($"pair blocked environment={environment} reason=missing-pair-code");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.pairCodeRequired", "Pair code is required."));
        }

        var savedCredential = await settingsStore.GetLinklyCloudCredentialAsync(environment, cancellationToken);
        var resolvedUsername = ResolveCredentialPart(username, savedCredential.Username);
        var resolvedPassword = ResolveCredentialPart(password, savedCredential.Password);
        if (string.IsNullOrWhiteSpace(resolvedUsername) || string.IsNullOrWhiteSpace(resolvedPassword))
        {
            LogLinklyCloudSetup($"pair blocked environment={environment} reason=missing-local-credential hasUsername={!string.IsNullOrWhiteSpace(resolvedUsername)} hasPassword=REDACTED");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.localCredentialMissing", "Save the Linkly Cloud API username and password first."));
        }

        try
        {
            LogLinklyCloudSetup($"pair start environment={environment} hasPairCode=true credentialSource=local hasUsername=true");
            var authBaseUrl = CardTerminalSettings.ResolveLinklyCloudAuthBaseUrl(environment);
            var authValidationMessage = CardTerminalSettings.ValidateLinklyCloudAuthBaseUrl(environment, authBaseUrl);
            if (!string.IsNullOrWhiteSpace(authValidationMessage))
            {
                LogLinklyCloudSetup($"pair blocked environment={environment} reason=invalid-auth-endpoint");
                return new LinklyConnectionTestResult(false, authValidationMessage);
            }

            var secret = await linklyCloudApiClient.PairAsync(
                authBaseUrl,
                resolvedUsername,
                resolvedPassword,
                pairCode,
                cancellationToken);
            // Backend Async 依赖 API 侧终端 secret/POS ID；先写本地，再补后台，确保两边状态一致。
            await settingsStore.SaveLinklyCloudSecretAsync(environment, secret, cancellationToken);
            if (syncBackendTerminalCredential)
            {
                if (linklyCloudCredentialApiClient is null)
                {
                    LogLinklyCloudSetup($"pair failed environment={environment} reason=missing-backend-credential-client");
                    return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
                }

                var deviceContext = deviceAuthorizationState?.Current;
                if (deviceContext is null)
                {
                    LogLinklyCloudSetup($"pair failed environment={environment} reason=missing-device-context");
                    return new LinklyConnectionTestResult(false, T("settings.linklyCloud.deviceContextMissing", "Device registration context is unavailable. Reopen settings and try again."));
                }

                var posId = await settingsStore.GetOrCreateLinklyCloudPosIdAsync(
                    environment,
                    deviceContext.StoreCode,
                    deviceContext.DeviceCode,
                    cancellationToken);
                await linklyCloudCredentialApiClient.UpsertBackendTerminalCredentialAsync(
                    environment,
                    secret,
                    posId,
                    cancellationToken);
                LogLinklyCloudSetup(
                    $"pair backend terminal credential saved environment={environment} store={LogValue(deviceContext.StoreCode)} device={LogValue(deviceContext.DeviceCode)} posId={ShortId(posId)}");
            }

            LogLinklyCloudSetup($"pair succeeded environment={environment} secretSaved=true");
            return new LinklyConnectionTestResult(true, T("settings.linklyCloud.paired", "Linkly Cloud terminal paired."));
        }
        catch (LinklyCloudApiException ex)
        {
            LogLinklyCloudSetup($"pair failed environment={environment} source=linkly authFailure={ex.IsAuthenticationFailure} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, ex.IsAuthenticationFailure
                ? T("settings.linklyCloud.pairAuthFailed", "Linkly Cloud pairing failed. Check the Sandbox VPP pair code and Cloud test account username/password.")
                : ex.Message);
        }
    }

    public async Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var credential = await settingsStore.GetLinklyCloudCredentialAsync(environment, cancellationToken);
        LogLinklyCloudSetup($"load local credential environment={environment} hasUsername={!string.IsNullOrWhiteSpace(credential.Username)} hasPassword=REDACTED");
        return credential with { Password = null };
    }

    public Task SaveLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        bool syncBackendCredential = false,
        CancellationToken cancellationToken = default)
    {
        LogLinklyCloudSetup($"save local credential requested environment={environment} hasUsername={!string.IsNullOrWhiteSpace(username)} hasPassword=REDACTED");
        return SaveLinklyCloudCredentialCoreAsync(environment, username, password, syncBackendCredential, cancellationToken);
    }

    public async Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (linklyCloudTerminalClient is null || deviceAuthorizationState?.Current is null)
        {
            LogLinklyCloudSetup($"test blocked environment={environment} reason=missing-dependencies-or-device-state");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        LogLinklyCloudSetup($"test start environment={environment} store={LogValue(deviceAuthorizationState.Current.StoreCode)} device={LogValue(deviceAuthorizationState.Current.DeviceCode)}");
        var authBaseUrl = CardTerminalSettings.ResolveLinklyCloudAuthBaseUrl(environment);
        var restBaseUrl = CardTerminalSettings.ResolveLinklyCloudRestBaseUrl(environment);
        var authValidationMessage = CardTerminalSettings.ValidateLinklyCloudAuthBaseUrl(environment, authBaseUrl);
        if (!string.IsNullOrWhiteSpace(authValidationMessage))
        {
            LogLinklyCloudSetup($"test blocked environment={environment} reason=invalid-auth-endpoint");
            return new LinklyConnectionTestResult(false, authValidationMessage);
        }

        var restValidationMessage = CardTerminalSettings.ValidateLinklyCloudRestBaseUrl(environment, restBaseUrl);
        if (!string.IsNullOrWhiteSpace(restValidationMessage))
        {
            LogLinklyCloudSetup($"test blocked environment={environment} reason=invalid-rest-endpoint");
            return new LinklyConnectionTestResult(false, restValidationMessage);
        }

        var settings = await settingsStore.GetSettingsAsync(cancellationToken);
        var secret = await settingsStore.GetLinklyCloudSecretAsync(environment, cancellationToken);
        settings = settings with
        {
            Environment = environment,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            LinklyCloudSecret = secret,
            // 统一使用按环境解析后的 endpoint，避免调用链里新旧变量优先级不一致。
            LinklyCloudAuthBaseUrl = authBaseUrl,
            LinklyCloudRestBaseUrl = restBaseUrl,
            LinklyPosVendorId = CardTerminalSettings.ResolveLinklyPosVendorId(environment)
        };
        var result = await linklyCloudTerminalClient.TestConnectionAsync(
            settings,
            deviceAuthorizationState.Current.StoreCode,
            deviceAuthorizationState.Current.DeviceCode,
            cancellationToken);
        LogLinklyCloudSetup($"test completed environment={environment} store={LogValue(deviceAuthorizationState.Current.StoreCode)} device={LogValue(deviceAuthorizationState.Current.DeviceCode)} success={result.Succeeded}");
        return result;
    }

    public async Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null)
        {
            LogLinklyCloudSetup($"backend test blocked environment={environment} reason=missing-backend-client");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        LogLinklyCloudSetup($"backend test start environment={environment}");
        var result = await linklyBackendTerminalClient.TestConnectionAsync(environment, cancellationToken);
        LogLinklyCloudSetup($"backend test completed environment={environment} success={result.Succeeded}");
        return result;
    }

    public Task<LinklyCloudTerminalListResponse> ListLinklyCloudBackendTerminalsAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null)
        {
            return Task.FromException<LinklyCloudTerminalListResponse>(
                new InvalidOperationException(T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable.")));
        }

        return linklyBackendTerminalClient.GetTerminalsAsync(environment, cancellationToken);
    }

    public Task<LinklyCloudTerminalSelectionResponse> SelectLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        long? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null)
        {
            return Task.FromException<LinklyCloudTerminalSelectionResponse>(
                new InvalidOperationException(T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable.")));
        }

        return linklyBackendTerminalClient.SelectTerminalAsync(
            environment,
            terminalId,
            expectedRevision,
            cancellationToken);
    }

    public Task<LinklyCloudTerminalPairResponse> PairLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        string pairCode,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null)
        {
            return Task.FromException<LinklyCloudTerminalPairResponse>(
                new InvalidOperationException(T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable.")));
        }

        return linklyBackendTerminalClient.PairTerminalAsync(
            environment,
            terminalId,
            pairCode,
            cancellationToken);
    }

    public Task<LinklyCloudTerminalConnectionTestResponse> TestLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        LinklyCloudTerminalSummary terminal,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null || string.IsNullOrWhiteSpace(terminal.TerminalVersion))
        {
            return Task.FromException<LinklyCloudTerminalConnectionTestResponse>(
                new NotSupportedException(T("settings.linkly.cloudBackend.managementUnavailable", "Update the POS API before managing individual Linkly lines.")));
        }

        // 连接测试只验证指定线路，不读取或改变本机付款选择，也不执行金融 Status/Logon。
        return linklyBackendTerminalClient.TestTerminalConnectionAsync(
            terminal.TerminalId,
            new LinklyCloudTerminalConnectionTestRequest(
                environment.ToString(),
                terminal.TerminalVersion,
                terminal.AssignedDeviceCode,
                terminal.AssignmentRevision),
            cancellationToken);
    }

    public async Task<LinklyTerminalAssignmentResult> AssignLinklyCloudBackendTerminalAsync(
        CardTerminalEnvironment environment,
        LinklyCloudTerminalSummary terminal,
        LinklyCloudAssignableDevice? targetDevice,
        IReadOnlyList<LinklyCloudAssignableDevice> devices,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null || string.IsNullOrWhiteSpace(terminal.TerminalVersion))
        {
            return new LinklyTerminalAssignmentResult(
                false,
                T("settings.linkly.cloudBackend.managementUnavailable", "Update the POS API before managing individual Linkly lines."));
        }

        await using var assignmentLease = linklyTerminalSelectionTransitionGate is null
            ? null
            : await linklyTerminalSelectionTransitionGate.TryEnterAssignmentAsync(cancellationToken);
        if (linklyTerminalSelectionTransitionGate is not null && assignmentLease is null)
        {
            return new LinklyTerminalAssignmentResult(
                false,
                T("settings.linkly.cloudBackend.assignmentActivePayment", "A card payment is currently running. Finish it before changing this POS line."));
        }

        var currentDeviceCode = deviceAuthorizationState?.Current?.DeviceCode ?? session.DeviceCode;
        var changesCurrentDevice = string.Equals(terminal.AssignedDeviceCode, currentDeviceCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetDevice?.DeviceCode, currentDeviceCode, StringComparison.OrdinalIgnoreCase);
        if (changesCurrentDevice)
        {
            if (linklyPaymentAttemptContextAccessor?.Current is not null)
            {
                return new LinklyTerminalAssignmentResult(
                    false,
                    T("settings.linkly.cloudBackend.assignmentActivePayment", "A card payment is currently running. Finish it before changing this POS line."));
            }

            if (cardPaymentRecoveryService is not null)
            {
                var openAttempts = await cardPaymentRecoveryService.ListOpenAsync(session, cancellationToken);
                if (openAttempts.Any(item => item.Processor == CardProcessorKind.Linkly))
                {
                    return new LinklyTerminalAssignmentResult(
                        false,
                        T("settings.linkly.cloudBackend.assignmentRecoveryRequired", "This POS has an unfinished Linkly payment. Resolve it in Card Recovery before changing the line."));
                }
            }

            if (linklySettlementRepository is not null &&
                await linklySettlementRepository.HasUnresolvedAsync(
                    session.StoreCode,
                    currentDeviceCode,
                    environment.ToString(),
                    cancellationToken))
            {
                return new LinklyTerminalAssignmentResult(
                    false,
                    T("settings.linkly.cloudBackend.assignmentSettlementRequired", "This POS has an unfinished Linkly settlement. Resolve it in Daily Close before changing the line."));
            }
        }

        if (terminal.IsBusy)
        {
            return new LinklyTerminalAssignmentResult(
                false,
                T("settings.linkly.cloudBackend.assignmentBusy", "This line has an unfinished transaction. Finish or recover it before changing the binding."));
        }

        var targetSnapshot = targetDevice is null
            ? null
            : devices.FirstOrDefault(item => string.Equals(item.DeviceCode, targetDevice.DeviceCode, StringComparison.OrdinalIgnoreCase));
        if (targetDevice is not null && (targetSnapshot is null || !targetSnapshot.IsAvailable))
        {
            return new LinklyTerminalAssignmentResult(
                false,
                T("settings.linkly.cloudBackend.assignmentTargetUnavailable", "The selected POS device is no longer available. Refresh the list and choose another device."));
        }

        var request = new LinklyCloudTerminalAssignmentRequest(
            environment.ToString(),
            terminal.TerminalVersion,
            terminal.AssignedDeviceCode,
            terminal.AssignmentRevision,
            targetSnapshot?.DeviceCode,
            targetSnapshot?.SelectedTerminalId,
            targetSnapshot?.SelectionRevision ?? 0);
        LinklyCloudTerminalListResponse directory;
        try
        {
            directory = await linklyBackendTerminalClient.AssignTerminalAsync(
                terminal.TerminalId,
                request,
                cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogAssignmentAmbiguous(terminal.TerminalId, "timeout", ex);
            return await ReconcileAmbiguousAssignmentAsync(
                linklyBackendTerminalClient, environment, terminal, targetSnapshot, devices, cancellationToken);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is null ||
            ex.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
            (int)ex.StatusCode >= 500 ||
            (int)ex.StatusCode is >= 200 and <= 299)
        {
            LogAssignmentAmbiguous(terminal.TerminalId, ex.StatusCode is null ? "network-error" : "http-error", ex);
            return await ReconcileAmbiguousAssignmentAsync(
                linklyBackendTerminalClient, environment, terminal, targetSnapshot, devices, cancellationToken);
        }
        catch (JsonException ex)
        {
            LogAssignmentAmbiguous(terminal.TerminalId, "invalid-response", ex);
            return await ReconcileAmbiguousAssignmentAsync(
                linklyBackendTerminalClient, environment, terminal, targetSnapshot, devices, cancellationToken);
        }
        if (AssignmentConfirmed(directory, terminal, targetSnapshot, devices))
        {
            return new LinklyTerminalAssignmentResult(
                true,
                T("settings.linkly.cloudBackend.assignmentSaved", "The Linkly line binding was updated."),
                directory);
        }

        // 2xx 也必须核验完整后置条件；回包不完整时只读一次权威目录，绝不重放 PUT。
        LogAssignmentAmbiguous(terminal.TerminalId, "postcondition-unconfirmed", exception: null);
        return await ReconcileAmbiguousAssignmentAsync(
            linklyBackendTerminalClient, environment, terminal, targetSnapshot, devices, cancellationToken);
    }

    private async Task<LinklyTerminalAssignmentResult> ReconcileAmbiguousAssignmentAsync(
        ILinklyBackendTerminalClient backendClient,
        CardTerminalEnvironment environment,
        LinklyCloudTerminalSummary submittedTerminal,
        LinklyCloudAssignableDevice? targetDevice,
        IReadOnlyList<LinklyCloudAssignableDevice> submittedDevices,
        CancellationToken cancellationToken)
    {
        // PUT 可能已在服务器提交；只读一次权威目录，绝不自动重放写请求。
        try
        {
            var directory = await backendClient.GetTerminalsAsync(environment, cancellationToken);
            var applied = AssignmentConfirmed(directory, submittedTerminal, targetDevice, submittedDevices);
            // 对账确认已生效记 Information；仍无法确认则需要人工核对当前归属，记 Warning。
            LinklyJsonLog.Write(
                "LinklyCloud",
                "settings-service",
                "terminal-assignment",
                applied ? "reconciled" : "unknown",
                success: applied,
                reason: applied ? "directory-confirms-binding" : "directory-does-not-confirm-binding",
                details: new
                {
                    terminalId = submittedTerminal.TerminalId,
                    environment = environment.ToString(),
                    targetDeviceCode = targetDevice?.DeviceCode
                });
            return new LinklyTerminalAssignmentResult(
                applied,
                applied
                    ? T("settings.linkly.cloudBackend.assignmentReconciled", "The response was interrupted, but the refreshed directory confirms that the binding was updated.")
                    : T("settings.linkly.cloudBackend.assignmentUnconfirmed", "The write response was interrupted and the refreshed directory does not confirm the requested binding. Review the current owner before retrying."),
                directory,
                Reconciled: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LinklyJsonLog.Write(
                "LinklyCloud",
                "settings-service",
                "terminal-assignment",
                "unknown",
                success: false,
                reason: "reconcile-failed",
                details: new
                {
                    terminalId = submittedTerminal.TerminalId,
                    environment = environment.ToString()
                },
                exception: ex);
            return new LinklyTerminalAssignmentResult(
                false,
                T("settings.linkly.cloudBackend.assignmentReconcileFailed", "The write result is unknown and the directory could not be refreshed. Check the current binding before trying again."),
                Reconciled: true);
        }
    }

    /// <summary>
    /// 分配写请求结果不确定（超时/5xx/解析失败/后置条件不符），随后只读对账；先记一条 Warning 便于追溯。
    /// </summary>
    private static void LogAssignmentAmbiguous(Guid terminalId, string reason, Exception? exception)
    {
        LinklyJsonLog.Write(
            "LinklyCloud",
            "settings-service",
            "terminal-assignment",
            "unknown",
            httpStatus: (exception as HttpRequestException)?.StatusCode,
            success: false,
            reason: reason,
            details: new { terminalId },
            exception: exception);
    }

    private static bool AssignmentConfirmed(
        LinklyCloudTerminalListResponse directory,
        LinklyCloudTerminalSummary submittedTerminal,
        LinklyCloudAssignableDevice? targetDevice,
        IReadOnlyList<LinklyCloudAssignableDevice> submittedDevices)
    {
        var currentTerminal = directory.Terminals.FirstOrDefault(item => item.TerminalId == submittedTerminal.TerminalId);
        if (currentTerminal is null)
        {
            return false;
        }

        var requestedNoOp = targetDevice is null
            ? string.IsNullOrWhiteSpace(submittedTerminal.AssignedDeviceCode)
            : string.Equals(submittedTerminal.AssignedDeviceCode, targetDevice.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
                targetDevice.SelectedTerminalId == submittedTerminal.TerminalId &&
                targetDevice.SelectionRevision == submittedTerminal.AssignmentRevision;
        if (requestedNoOp)
        {
            var unchangedTerminal =
                string.Equals(currentTerminal.AssignedDeviceCode, submittedTerminal.AssignedDeviceCode, StringComparison.OrdinalIgnoreCase) &&
                currentTerminal.AssignmentRevision == submittedTerminal.AssignmentRevision &&
                string.Equals(currentTerminal.TerminalVersion, submittedTerminal.TerminalVersion, StringComparison.Ordinal) &&
                string.Equals(currentTerminal.LastHealthStatus, submittedTerminal.LastHealthStatus, StringComparison.Ordinal) &&
                currentTerminal.LastHealthAt == submittedTerminal.LastHealthAt;
            if (!unchangedTerminal || directory.Devices is null)
            {
                return false;
            }

            if (targetDevice is null)
            {
                return true;
            }

            var unchangedTarget = directory.Devices.FirstOrDefault(item =>
                string.Equals(item.DeviceCode, targetDevice.DeviceCode, StringComparison.OrdinalIgnoreCase));
            return unchangedTarget is not null &&
                unchangedTarget.SelectedTerminalId == targetDevice.SelectedTerminalId &&
                unchangedTarget.SelectionRevision == targetDevice.SelectionRevision;
        }

        if (!string.Equals(currentTerminal.AssignedDeviceCode, targetDevice?.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentTerminal.TerminalVersion, submittedTerminal.TerminalVersion, StringComparison.Ordinal) ||
            currentTerminal.LastHealthStatus is not null || currentTerminal.LastHealthAt is not null)
        {
            return false;
        }

        var currentDevices = directory.Devices;
        if (currentDevices is null)
        {
            return false;
        }

        var oldOwner = string.IsNullOrWhiteSpace(submittedTerminal.AssignedDeviceCode)
            ? null
            : submittedDevices.FirstOrDefault(item =>
                string.Equals(item.DeviceCode, submittedTerminal.AssignedDeviceCode, StringComparison.OrdinalIgnoreCase));
        if (oldOwner is not null &&
            !string.Equals(oldOwner.DeviceCode, targetDevice?.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            var releasedOwner = currentDevices.FirstOrDefault(item =>
                string.Equals(item.DeviceCode, oldOwner.DeviceCode, StringComparison.OrdinalIgnoreCase));
            // 历史设备可能已不在注册目录；若仍存在，释放后的选择事实必须归零。
            if (releasedOwner is not null &&
                (releasedOwner.SelectedTerminalId is not null || releasedOwner.SelectionRevision != 0))
            {
                return false;
            }
        }

        if (targetDevice is null)
        {
            return currentTerminal.AssignmentRevision == 0;
        }

        var assignedTarget = currentDevices.FirstOrDefault(item =>
            string.Equals(item.DeviceCode, targetDevice.DeviceCode, StringComparison.OrdinalIgnoreCase));
        var targetRevisionConfirmed = targetDevice.SelectedTerminalId is null
            ? assignedTarget is { SelectionRevision: > 0 }
            : assignedTarget?.SelectionRevision == targetDevice.SelectionRevision + 1;
        if (assignedTarget is null || assignedTarget.SelectedTerminalId != submittedTerminal.TerminalId ||
            !targetRevisionConfirmed || currentTerminal.AssignmentRevision != assignedTarget.SelectionRevision)
        {
            return false;
        }

        if (targetDevice.SelectedTerminalId is not Guid replacedTerminalId || replacedTerminalId == submittedTerminal.TerminalId)
        {
            return true;
        }

        var replacedTerminal = directory.Terminals.FirstOrDefault(item => item.TerminalId == replacedTerminalId);
        return replacedTerminal is not null &&
            string.IsNullOrWhiteSpace(replacedTerminal.AssignedDeviceCode) &&
            replacedTerminal.AssignmentRevision == 0 &&
            replacedTerminal.LastHealthStatus is null && replacedTerminal.LastHealthAt is null;
    }

    public async Task<LinklyConnectionTestResult> TestLinklyCloudBackendTransactionStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null)
        {
            LogLinklyCloudSetup($"backend status test blocked environment={environment} reason=missing-backend-client");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        LogLinklyCloudSetup($"backend status test start environment={environment}");
        var result = await linklyBackendTerminalClient.TestTransactionStatusAsync(environment, cancellationToken);
        LogLinklyCloudSetup($"backend status test completed environment={environment} success={result.Succeeded}");
        return result;
    }

    public async Task<bool> HasLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var hasSecret = !string.IsNullOrWhiteSpace(await settingsStore.GetLinklyCloudSecretAsync(
            environment,
            cancellationToken));
        LogLinklyCloudSetup($"secret status environment={environment} hasSecret={hasSecret}");
        return hasSecret;
    }

    public Task SaveLinklyCloudAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = CardTerminalSettings.NormalizeLinklyConnectionMode(configuration.LinklyConnectionMode);
        var cloudMode = normalizedMode == LinklyConnectionMode.CloudBackendAsync
            ? LinklyConnectionMode.CloudBackendAsync
            : LinklyConnectionMode.CloudDirectSync;
        LogLinklyCloudSetup($"save configuration environment={configuration.Environment} mode={cloudMode}");
        return settingsStore.SaveAsync(configuration with { LinklyConnectionMode = cloudMode }, squareAccessToken: null, cancellationToken);
    }

    private static string ResolveSetupAccessToken(
        string? accessToken,
        CardTerminalEnvironment environment)
    {
        // setup API 已后移到 Hbpos API；客户端保留旧签名兼容调用方，但这里不再读取或刷新本地 Square token。
        _ = accessToken;
        LogSquareSetup($"setup api uses backend-managed square token environment={environment}");
        return string.Empty;
    }

    private async Task<T> ExecuteSquareSetupAsync<T>(
        string? accessToken,
        CardTerminalEnvironment environment,
        Func<string, Task<T>> operation)
    {
        LogSquareSetup($"execute square operation requested environment={environment} tokenSource={DescribeTokenSource(accessToken)}");
        var setupAccessToken = ResolveSetupAccessToken(accessToken, environment);
        var result = await operation(setupAccessToken);
        LogSquareSetup($"execute square operation succeeded environment={environment}");
        return result;
    }

    private static void EnsureDeviceCodesSupported(CardTerminalEnvironment environment)
    {
        if (environment == CardTerminalEnvironment.Sandbox)
        {
            throw new InvalidOperationException("Square Device Codes are only supported in Production.");
        }
    }

    private static void LogSquareSetup(string message)
    {
        ConsoleLog.Write("Square", $"settings {message}");
    }

    private static void LogLinklyCloudSetup(string message)
    {
        LinklyJsonLog.WriteMessage("LinklyCloud", "settings-service", $"settings {message}");
    }

    private static string DescribeTokenSource(string? accessToken)
    {
        return string.IsNullOrWhiteSpace(accessToken) ? "stored" : "provided";
    }

    private async Task SaveLinklyCloudCredentialCoreAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        bool syncBackendCredential,
        CancellationToken cancellationToken)
    {
        // 本地仍然保存受保护凭据，Backend Async 再额外同步到 API 侧门店凭据。
        await settingsStore.SaveLinklyCloudCredentialAsync(environment, username, password, cancellationToken);
        if (!syncBackendCredential)
        {
            return;
        }

        if (linklyCloudCredentialApiClient is null)
        {
            throw new InvalidOperationException(T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        await linklyCloudCredentialApiClient.UpsertCredentialAsync(
            environment,
            username,
            password,
            cancellationToken);
        LogLinklyCloudSetup($"save backend credential succeeded environment={environment} hasUsername={!string.IsNullOrWhiteSpace(username)}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }

    private static string ShortId(string value)
    {
        return IsUuidV4(value)
            ? value[..8]
            : LogValue(value);
    }

    private static string? ResolveCredentialPart(string? currentValue, string? savedValue)
    {
        return string.IsNullOrWhiteSpace(currentValue) ? savedValue?.Trim() : currentValue.Trim();
    }

    private static bool IsUuidV4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return Guid.TryParse(trimmed, out _) &&
            trimmed.Length == 36 &&
            trimmed[14] == '4' &&
            trimmed[19] is '8' or '9' or 'a' or 'A' or 'b' or 'B';
    }

    private SquareDeviceOption WithLocalizedSquareDeviceStatusDisplayName(SquareDeviceOption device)
    {
        if (string.Equals(device.Status, SquareSandboxTerminalDeviceIds.TestDeviceStatus, StringComparison.OrdinalIgnoreCase))
        {
            // Sandbox 测试设备保留原始状态码，同时给设置页一个本地化显示名。
            return device with
            {
                StatusDisplayName = T("settings.square.deviceStatus.sandboxTest", "Square sandbox test")
            };
        }

        return string.IsNullOrWhiteSpace(device.StatusDisplayName) && !string.IsNullOrWhiteSpace(device.Status)
            ? device with { StatusDisplayName = device.Status }
            : device;
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}
