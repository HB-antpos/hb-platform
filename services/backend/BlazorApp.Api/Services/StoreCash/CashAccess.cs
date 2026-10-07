using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 当前账号在现金管理里的权限与分店范围，一次请求解析一次，再传给所有服务方法。
/// AllStores（持有 Cash.AllStores.View，管理员隐含）：全部分店、全部历史 T2、补录不受回溯天数限制；
/// 否则只能访问 StoreCodes（账号全部关联分店）并受 T2 14 天窗口约束。
/// </summary>
public sealed record CashAccess(
    string UserGuid,
    string UserName,
    bool AllStores,
    IReadOnlySet<string> StoreCodes,
    bool CanViewOverview,
    bool CanCreateDeposit,
    bool CanCreateExpense,
    bool CanVoid
)
{
    public bool CanAccessStore(string storeCode) => AllStores || StoreCodes.Contains(storeCode);
}

public interface ICashAccessResolver
{
    Task<CashAccess> ResolveAsync(CancellationToken cancellationToken);
}

public sealed class CashAccessResolver : ICashAccessResolver
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthorizationService _authorizationService;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUserService;

    public CashAccessResolver(
        IHttpContextAccessor httpContextAccessor,
        IAuthorizationService authorizationService,
        IUserService userService,
        ICurrentUserService currentUserService
    )
    {
        _httpContextAccessor = httpContextAccessor;
        _authorizationService = authorizationService;
        _userService = userService;
        _currentUserService = currentUserService;
    }

    public async Task<CashAccess> ResolveAsync(CancellationToken cancellationToken)
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        var userGuid = _currentUserService.GetCurrentUserGuid() ?? string.Empty;
        var userName = _currentUserService.GetCurrentUsername() ?? string.Empty;
        if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userGuid))
        {
            return new CashAccess(
                userGuid,
                userName,
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                false,
                false,
                false,
                false
            );
        }

        async Task<bool> HasAsync(string permission) =>
            (await _authorizationService.AuthorizeAsync(principal, permission)).Succeeded;

        var allStores = await HasAsync(Permissions.Cash.AllStoresView);
        var storeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!allStores)
        {
            // 与月度日销售等只读页同一口径：非全部分店账号取 UserStore 的全部关联分店，而不只是主分店。
            var userStores = await _userService.GetUserStoresAsync(userGuid);
            if (userStores?.Success == true && userStores.Data is not null)
            {
                foreach (var store in userStores.Data)
                {
                    if (!string.IsNullOrWhiteSpace(store.StoreCode))
                    {
                        storeCodes.Add(store.StoreCode.Trim());
                    }
                }
            }
        }

        return new CashAccess(
            userGuid,
            userName,
            allStores,
            storeCodes,
            await HasAsync(Permissions.Cash.OverviewView),
            await HasAsync(Permissions.Cash.DepositCreate),
            await HasAsync(Permissions.Cash.ExpenseCreate),
            await HasAsync(Permissions.Cash.Void)
        );
    }
}
