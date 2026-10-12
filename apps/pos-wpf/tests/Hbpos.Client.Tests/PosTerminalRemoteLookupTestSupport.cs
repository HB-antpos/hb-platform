using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 本地未命中后「远程补查」相关测试共用的小工具：
/// 可控的远程查询替身（按码挂起、手动放行/超时）与商品、会话构造。
/// </summary>
internal static class PosTerminalRemoteLookupTestSupport
{
    public static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    public static SellableItemDto CreateItem(string productCode, string name, string barcode, decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: barcode,
            ItemNumber: productCode,
            Barcode: barcode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// 远程补查替身：每次调用按商品码挂起，直到测试显式放行；取消令牌触发（超时/外部取消）时任务随之取消，
/// 与真实 <c>RemoteLookupRefreshService</c> 在超时下抛出取消异常的行为一致。
/// </summary>
internal sealed class ControlledRemoteLookup(bool ignoreCancellation = false)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<RemoteLookupRefreshResult>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    public Task<RemoteLookupRefreshResult> LookupAsync(string storeCode, string lookupCode, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _calls[lookupCode] = _calls.GetValueOrDefault(lookupCode) + 1;
            _pending[lookupCode] = completion;
        }

        if (!ignoreCancellation)
        {
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        return completion.Task;
    }

    public int Calls(string lookupCode)
    {
        lock (_gate)
        {
            return _calls.GetValueOrDefault(lookupCode);
        }
    }

    public Task WaitForCallAsync(string lookupCode, int expectedCalls = 1)
    {
        return AsyncTestWaitSupport.WaitUntilAsync(() => Calls(lookupCode) >= expectedCalls);
    }

    public void Found(string lookupCode, SellableItemDto item)
    {
        Pending(lookupCode).TrySetResult(new RemoteLookupRefreshResult(item.StoreCode, lookupCode, Found: true, item, DeletedCount: 0));
    }

    public void NotFound(string lookupCode)
    {
        Pending(lookupCode).TrySetResult(new RemoteLookupRefreshResult("S001", lookupCode, Found: false, Item: null, DeletedCount: 1));
    }

    private TaskCompletionSource<RemoteLookupRefreshResult> Pending(string lookupCode)
    {
        lock (_gate)
        {
            return _pending[lookupCode];
        }
    }
}

/// <summary>
/// 收银页测试夹具：真实的工作流、购物车与收银页 ViewModel，远程补查用 <see cref="ControlledRemoteLookup"/> 挂起，
/// 同时记录提示音、审计事件以及「打开付款页」那一刻的购物车行数与线程。
/// </summary>
internal sealed class TerminalHarness : IDisposable
{
    public const string Device = "scanner-1";

    private readonly object _paymentGate = new();
    private readonly List<int> _linesAtPayment = [];
    private readonly List<int> _paymentThreadIds = [];

    public TerminalHarness(
        TimeProvider? timeProvider = null,
        TimeProvider? viewModelTimeProvider = null,
        bool remoteIgnoresCancellation = false)
    {
        Cart = new PosCartService();
        Index = new LocalSellableItemIndex();
        Remote = new ControlledRemoteLookup(remoteIgnoresCancellation);
        Feedback = new RecordingUserFeedbackService();
        Audit = new RecordingOperationAuditLogger();
        Localization = new LocalizationService();
        var workflow = new PosTerminalWorkflowService(
            Index,
            Cart,
            Remote.LookupAsync,
            timeProvider: timeProvider);
        ViewModel = new PosTerminalViewModel(
            Index,
            Cart,
            PosTerminalRemoteLookupTestSupport.Session,
            onOpenPayment: OnPaymentOpened,
            localization: Localization,
            userFeedbackService: Feedback,
            workflowService: workflow,
            operationAuditLogger: Audit,
            onHoldOrderAsync: () => Task.CompletedTask,
            onRecallOrderAsync: () => Task.CompletedTask,
            onOpenReturns: () => { },
            timeProvider: viewModelTimeProvider);
    }

    public PosCartService Cart { get; }

    public LocalSellableItemIndex Index { get; }

    public ControlledRemoteLookup Remote { get; }

    public RecordingUserFeedbackService Feedback { get; }

    public RecordingOperationAuditLogger Audit { get; }

    public LocalizationService Localization { get; }

    public PosTerminalViewModel ViewModel { get; }

    public int PaymentOpenCount
    {
        get
        {
            lock (_paymentGate)
            {
                return _linesAtPayment.Count;
            }
        }
    }

    /// <summary>每次打开付款页时购物车里的行数，用来断言付款页打开时刚扫的商品是否已经在购物车里。</summary>
    public IReadOnlyList<int> LinesAtPayment
    {
        get
        {
            lock (_paymentGate)
            {
                return _linesAtPayment.ToArray();
            }
        }
    }

    public IReadOnlyList<int> PaymentThreadIds
    {
        get
        {
            lock (_paymentGate)
            {
                return _paymentThreadIds.ToArray();
            }
        }
    }

    public void Scan(string barcode)
    {
        Assert.True(ViewModel.ProcessScannerBarcode(barcode, Device, "raw"));
    }

    public Task OpenPaymentAsync()
    {
        return ((IAsyncRelayCommand)ViewModel.OpenPaymentCommand).ExecuteAsync(null);
    }

    public Task ClearCartAsync()
    {
        return ((IAsyncRelayCommand)ViewModel.ClearCartCommand).ExecuteAsync(null);
    }

    public void Dispose()
    {
        ViewModel.Dispose();
    }

    private void OnPaymentOpened()
    {
        lock (_paymentGate)
        {
            _linesAtPayment.Add(Cart.Lines.Count);
            _paymentThreadIds.Add(Environment.CurrentManagedThreadId);
        }
    }
}

internal sealed class RecordingUserFeedbackService : IUserFeedbackService
{
    private readonly ConcurrentQueue<UserFeedbackCue> _cues = new();

    public IReadOnlyList<UserFeedbackCue> Cues => _cues.ToArray();

    public void Play(UserFeedbackCue cue)
    {
        _cues.Enqueue(cue);
    }
}

internal sealed class RecordingOperationAuditLogger : IOperationAuditLogger
{
    private readonly ConcurrentQueue<BlazorApp.Shared.DTOs.OperationAuditEventDto> _events = new();

    public IReadOnlyList<BlazorApp.Shared.DTOs.OperationAuditEventDto> Events => _events.ToArray();

    public void Record(BlazorApp.Shared.DTOs.OperationAuditEventDto auditEvent)
    {
        _events.Enqueue(auditEvent);
    }
}
