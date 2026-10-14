using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Tests;

/// <summary>
/// 记录每次授权请求及其是否要求「再次扫码」的授权替身（无交易开钱箱、退货的「再次扫码」开关测试共用）：
/// approve = true 恒批准；false 模拟没扫码确认（返回 null）。
/// 旧方法与带 requireFreshScan 的新重载都实现，才能区分调用方走的是哪一个。
/// </summary>
internal sealed class RescanAwareAuthorizationService(bool approve) : IOperationAuthorizationService
{
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public string ScannerPageId => "RescanAwareAuthorizationTest";

    public bool IsPromptOpen => false;

    public bool IsBusy => false;

    public string PromptMessage => string.Empty;

    public string StatusMessage => string.Empty;

    public string PermissionCode => string.Empty;

    public string Screen => string.Empty;

    public string Action => string.Empty;

    public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

    public List<(string Permission, bool RequireFreshScan)> Requests { get; } = [];

    public Task<OperationAuthorizationScope?> AuthorizeAsync(
        string permissionCode,
        string screen,
        string action,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        Record(permissionCode, screen, action, session, requireFreshScan: false);

    public Task<OperationAuthorizationScope?> AuthorizeAsync(
        string permissionCode,
        string screen,
        string action,
        PosSessionState session,
        bool requireFreshScan,
        CancellationToken cancellationToken = default) =>
        Record(permissionCode, screen, action, session, requireFreshScan);

    private Task<OperationAuthorizationScope?> Record(
        string permissionCode,
        string screen,
        string action,
        PosSessionState session,
        bool requireFreshScan)
    {
        Requests.Add((permissionCode, requireFreshScan));
        if (!approve)
        {
            return Task.FromResult<OperationAuthorizationScope?>(null);
        }

        var requestingSession = new CashierSessionDto(
            session.CashierId,
            session.CashierId,
            session.CashierName,
            session.StoreCode,
            session.DeviceCode,
            [],
            [],
            [session.StoreCode],
            false,
            false,
            false);
        return Task.FromResult<OperationAuthorizationScope?>(
            new OperationAuthorizationScope(requestingSession, permissionCode, screen, action));
    }

    public bool ProcessScannerBarcode(string barcode) => false;

    public void Cancel()
    {
    }

    public void RevokeAll()
    {
    }
}
