using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

internal static class ViewModelOperationAuthorization
{
    public static async Task<ViewModelAuthorizationGrant?> AuthorizeAsync(
        IOperationAuthorizationService? service,
        Func<string, bool> fallbackPermissionCheck,
        string permissionCode,
        string screen,
        string action,
        PosSessionState session,
        CancellationToken cancellationToken = default,
        bool requireFreshScan = false)
    {
        if (service is null)
        {
            // 没有授权服务（单元测试 / 未接入授权的场景）时无从扫码，只能按原来的权限检查放行。
            return fallbackPermissionCheck(permissionCode) ? new ViewModelAuthorizationGrant(null) : null;
        }

        // requireFreshScan 放在末尾且默认 false：现有二十多个调用方都不用改，行为与原来完全一致。
        var scope = requireFreshScan
            ? await service.AuthorizeAsync(permissionCode, screen, action, session, requireFreshScan: true, cancellationToken)
            : await service.AuthorizeAsync(permissionCode, screen, action, session, cancellationToken);
        if (scope is null)
        {
            return null;
        }

        return new ViewModelAuthorizationGrant(scope);
    }
}

internal sealed class ViewModelAuthorizationGrant(OperationAuthorizationScope? scope) : IDisposable
{
    // 中文注释：必须由业务方法在 await 授权返回后激活，AsyncLocal 才会进入实际 HTTP 调用链。
    public IDisposable Activate() => scope?.Activate() ?? NoopDisposable.Instance;

    public void Dispose() => scope?.Dispose();

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
