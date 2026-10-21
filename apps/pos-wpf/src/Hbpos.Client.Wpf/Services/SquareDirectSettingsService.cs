namespace Hbpos.Client.Wpf.Services;

/// <summary>设置页展示的直连 Square 状态：本地开关、服务端门店开关（最近一次领取结果）、本机令牌剩余有效期。</summary>
public sealed record SquareDirectStatus(
    bool LocalEnabled,
    SquareDirectServerState ServerState,
    bool HasCredential,
    DateTimeOffset? ValidUntil,
    TimeSpan? Remaining)
{
    /// <summary>本地开关、服务端开关都开且本机持有有效令牌——下一笔付款会尝试直连。</summary>
    public bool IsActive => LocalEnabled && ServerState == SquareDirectServerState.Enabled && HasCredential;
}

/// <summary>设置页用的直连开关服务：ViewModel 只依赖它，不碰存储、HTTP 与 DPAPI。</summary>
public interface ISquareDirectSettingsService
{
    Task<SquareDirectStatus> GetStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换本地开关。打开时立即向服务端领取一次凭据，以便马上显示服务端门店开关状态；
    /// 关闭时删除本机所有凭据。切换后的状态一并返回。
    /// </summary>
    Task<SquareDirectStatus> SetEnabledAsync(
        bool enabled,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    /// <summary>手动重新领取凭据并返回最新状态（本地开关关闭时不联网）。</summary>
    Task<SquareDirectStatus> RefreshAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);
}

public sealed class SquareDirectSettingsService(
    ISquareDirectCredentialStore store,
    ISquareDirectGateway gateway,
    TimeProvider? timeProvider = null) : ISquareDirectSettingsService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SquareDirectStatus> GetStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var enabled = await store.IsLocalEnabledAsync(cancellationToken);
        var serverState = await store.GetServerStateAsync(environment, cancellationToken);
        var credential = await store.LoadCredentialAsync(environment, cancellationToken);
        var remaining = credential is null ? (TimeSpan?)null : credential.ValidUntil - _timeProvider.GetUtcNow();
        return new SquareDirectStatus(
            enabled,
            serverState,
            credential is not null,
            credential?.ValidUntil,
            remaining is { } value && value > TimeSpan.Zero ? value : null);
    }

    public async Task<SquareDirectStatus> SetEnabledAsync(
        bool enabled,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        await store.SetLocalEnabledAsync(enabled, cancellationToken);
        if (enabled)
        {
            // 打开后立刻领一次：失败（断网 / 限流）不影响开关本身，状态里服务端开关保持「未知」。
            await gateway.RefreshCredentialAsync(environment, cancellationToken);
        }

        return await GetStatusAsync(environment, cancellationToken);
    }

    public async Task<SquareDirectStatus> RefreshAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (await store.IsLocalEnabledAsync(cancellationToken))
        {
            await gateway.RefreshCredentialAsync(environment, cancellationToken);
        }

        return await GetStatusAsync(environment, cancellationToken);
    }
}
