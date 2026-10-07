using System.Collections.ObjectModel;
using System.Threading;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class SettingsViewModel
{
    private delegate void SetLocalizedStatus(string key, params object[] args);

    private sealed record DataMaintenanceContext(
        Func<bool> IsBusy,
        Func<CancellationToken, Task>? DownloadCatalogAsync,
        Func<CancellationToken, Task>? ResetCatalogAsync,
        Func<CancellationToken, Task>? ResetTestSalesDataAsync,
        Func<Task<bool>>? ConfirmResetTestSalesDataAsync,
        Func<Task<DeviceReregistrationStartResult>>? ReregisterDeviceAsync,
        Func<CancellationToken, Task<AppUpdateCoordinatorResult>>? CheckForAppUpdateAsync,
        Func<Func<Task>, string?, Task> RunBusyAsync,
        SetLocalizedStatus SetStatus,
        Action<string> SetStatusOverride);

    private sealed class DataMaintenanceSection
    {
        private readonly DataMaintenanceContext _context;

        public DataMaintenanceSection(DataMaintenanceContext context)
        {
            _context = context;
        }

        public bool CanDownloadCatalog()
        {
            return !_context.IsBusy() && _context.DownloadCatalogAsync is not null;
        }

        public bool CanResetCatalog()
        {
            return !_context.IsBusy() && _context.ResetCatalogAsync is not null;
        }

        public bool CanResetTestSalesData()
        {
#if DEBUG
            return !_context.IsBusy() && _context.ResetTestSalesDataAsync is not null;
#else
            return false;
#endif
        }

        public bool CanReregisterDevice()
        {
            return !_context.IsBusy() && _context.ReregisterDeviceAsync is not null;
        }

        public bool CanCheckForAppUpdate()
        {
            return !_context.IsBusy() && _context.CheckForAppUpdateAsync is not null;
        }

        public async Task DownloadCatalogAsync(CancellationToken cancellationToken)
        {
            if (_context.DownloadCatalogAsync is null)
            {
                _context.SetStatus("settings.status.catalogDownloadNotConfigured");
                return;
            }

            await _context.RunBusyAsync(async () =>
            {
                // 保留原有状态切换顺序，避免影响现有提示文案和测试断言。
                _context.SetStatus("settings.status.catalogDownloading");
                await _context.DownloadCatalogAsync(cancellationToken);
                _context.SetStatus("settings.status.catalogDownloadCompleted");
            }, null);
        }

        public async Task ResetCatalogAsync(CancellationToken cancellationToken)
        {
            if (_context.ResetCatalogAsync is null)
            {
                _context.SetStatus("settings.status.catalogResetNotConfigured");
                return;
            }

            await _context.RunBusyAsync(async () =>
            {
                // 继续复用 ViewModel 的 busy 包装，保持取消和异常处理一致。
                _context.SetStatus("settings.status.catalogResetting");
                await _context.ResetCatalogAsync(cancellationToken);
                _context.SetStatus("settings.status.catalogResetCompleted");
            }, null);
        }

        public async Task ResetTestSalesDataAsync(CancellationToken cancellationToken)
        {
#if DEBUG
            if (_context.ResetTestSalesDataAsync is null)
            {
                _context.SetStatus("settings.status.testSalesDataResetNotConfigured");
                return;
            }

            if (_context.ConfirmResetTestSalesDataAsync is null ||
                !await _context.ConfirmResetTestSalesDataAsync())
            {
                return;
            }

            await _context.RunBusyAsync(async () =>
            {
                _context.SetStatus("settings.status.testSalesDataResetting");
                await _context.ResetTestSalesDataAsync(cancellationToken);
                _context.SetStatus("settings.status.testSalesDataResetCompleted");
            }, null);
#else
            await Task.CompletedTask;
            _context.SetStatus("settings.status.testSalesDataResetNotConfigured");
#endif
        }

        public async Task ReregisterDeviceAsync()
        {
            if (_context.ReregisterDeviceAsync is null)
            {
                _context.SetStatus("settings.status.reregisterNotConfigured");
                return;
            }

            await _context.RunBusyAsync(async () =>
            {
                _context.SetStatus("settings.status.reregisterStarting");
                var result = await _context.ReregisterDeviceAsync();

                // 业务阻断时继续把阻断原因透传到设置页状态栏。
                if (!result.Started && !string.IsNullOrWhiteSpace(result.StatusMessage))
                {
                    _context.SetStatusOverride(result.StatusMessage);
                }
            }, null);
        }

        public async Task CheckForAppUpdateAsync(CancellationToken cancellationToken)
        {
            if (_context.CheckForAppUpdateAsync is null)
            {
                _context.SetStatus("settings.status.appUpdateNotConfigured");
                return;
            }

            await _context.RunBusyAsync(async () =>
            {
                // 手动检查只触发更新协调器，不直接安装，避免设置页绕过强制/普通更新状态流。
                _context.SetStatus("settings.status.appUpdateChecking");
                var result = await _context.CheckForAppUpdateAsync(cancellationToken);
                _context.SetStatus(result.StatusKey, result.StatusArgs);
            }, null);
        }
    }

    private sealed record ReceiptPrinterContext(
        IReceiptPrinterSettingsStore? SettingsStore,
        IReceiptPrintService? PrintService,
        Func<bool> IsBusy,
        Func<ReceiptPrinterSettings> CreateSettingsFromFields,
        Action<ReceiptPrinterSettings> ApplySettings,
        Func<Func<Task>, string?, Task> RunBusyAsync,
        Action<string> SetStatus,
        Action<string> SetStatusOverride,
        Action ClearReceiptPrinterTestStatus,
        Action<string> SetReceiptPrinterTestStatus);

    private sealed class ReceiptPrinterSection
    {
        private readonly ReceiptPrinterContext _context;

        public ReceiptPrinterSection(ReceiptPrinterContext context)
        {
            _context = context;
        }

        public bool CanSave()
        {
            return !_context.IsBusy() && _context.SettingsStore is not null;
        }

        public bool CanTest()
        {
            return !_context.IsBusy() && _context.PrintService is not null;
        }

        public async Task LoadAsync()
        {
            if (_context.SettingsStore is null)
            {
                _context.ApplySettings(ReceiptPrinterSettings.Default);
                return;
            }

            _context.ApplySettings(await _context.SettingsStore.LoadAsync());
        }

        public async Task SaveAsync()
        {
            if (_context.SettingsStore is null)
            {
                _context.SetStatus("settings.status.receiptPrinterNotConfigured");
                return;
            }

            await _context.RunBusyAsync(async () =>
            {
                // 字段仍由外层 ViewModel 持有，helper 只负责保存流程编排。
                var settings = _context.CreateSettingsFromFields();
                await _context.SettingsStore.SaveAsync(settings);
                // 中文注释：资料由总部下发（只读）时，存储层只保存了硬件设置，六个字段以存储里的真实内容为准，
                // 不能把设置页里可能已过期的草稿当作「已保存」回显。
                _context.ApplySettings(
                    settings.ProfileVersion > 0 ? await _context.SettingsStore.LoadAsync() : settings);
                _context.SetStatus("settings.status.receiptPrinterSaved");
            }, null);
        }

        public async Task TestAsync()
        {
            if (_context.PrintService is null)
            {
                _context.SetStatus("settings.status.receiptPrinterNotConfigured");
                return;
            }

            await _context.RunBusyAsync(async () =>
            {
                _context.ClearReceiptPrinterTestStatus();
                if (_context.SettingsStore is not null)
                {
                    await _context.SettingsStore.SaveAsync(_context.CreateSettingsFromFields());
                }

                var result = await _context.PrintService.TestPrinterAsync();

                // 测试结果同时回写局部提示和全局状态，保持现有 XAML 显示一致。
                _context.SetReceiptPrinterTestStatus(result.Message);
                _context.SetStatusOverride(result.Message);
            }, null);
        }
    }

    // ── Square payment terminal ──
}
