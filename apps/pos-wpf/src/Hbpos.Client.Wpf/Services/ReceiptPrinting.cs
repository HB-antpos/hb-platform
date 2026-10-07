using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public enum ReceiptPrintReason
{
    Manual,
    LastReceipt,
    Reprint,
    CardAuto,
    InstallmentAuto,
    VoucherRefundAuto,
    VoucherBalanceAuto,
    Test
}

public enum ReceiptPrintElementKind
{
    Text,
    Separator,
    Barcode,
    QrCode
}

public enum ReceiptPrintAlignment
{
    Left = 0,
    Center = 1,
    Right = 2
}

public enum ReceiptPreviewRowKind
{
    Text,
    Separator,
    Barcode,
    QrCode
}

public sealed record ReceiptPrinterSettings(
    string PrinterPort,
    string BrandName,
    string StoreName,
    string StoreAddress,
    string StorePhone,
    string Abn,
    string ReturnPolicy,
    int CutDistance,
    // 中文注释：客人小票是否附带 Linkly 银行收据原文；默认打印，保持旧行为。
    // 关闭后仍保留付款行的卡类型/卡号后四位，签名单、拒付单、结算单等独立银行单据不受影响。
    bool PrintBankReceiptText = true,
    // 中文注释：本机已应用的总部下发版本，0 = 从未应用过下发（资料可手工编辑）。
    // 仅供设置页决定六个资料字段是否只读；打印路径不读取它，保存时存储层也忽略它（版本只由同步写入）。
    int ProfileVersion = 0)
{
    public const string DefaultPrinterPort = "USB,";

    public static ReceiptPrinterSettings Default { get; } = new(
        DefaultPrinterPort,
        "HotBargain",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        60);
}

public sealed record ReceiptPrintElement(
    ReceiptPrintElementKind Kind,
    string Text,
    ReceiptPrintAlignment Alignment = ReceiptPrintAlignment.Left,
    bool IsEmphasized = false);

public sealed record ReceiptPreviewRow(
    ReceiptPreviewRowKind Kind,
    string Text,
    ReceiptPrintAlignment Alignment = ReceiptPrintAlignment.Left,
    bool IsEmphasized = false)
{
    public string? QrCodeValue { get; init; }

    public bool IsSeparator => Kind == ReceiptPreviewRowKind.Separator;

    public bool IsBarcode => Kind == ReceiptPreviewRowKind.Barcode;

    public bool IsQrCode => Kind == ReceiptPreviewRowKind.QrCode;

    public bool IsCentered => Alignment == ReceiptPrintAlignment.Center;

    public bool IsRightAligned => Alignment == ReceiptPrintAlignment.Right;

    public bool IsMachineCode => IsBarcode || IsQrCode;
}

public sealed record ReceiptPrintDocument(
    IReadOnlyList<ReceiptPrintElement> Elements,
    IReadOnlyList<ReceiptPreviewRow> PreviewRows)
{
    public string PlainText => string.Join(
        Environment.NewLine,
        Elements
            .Where(element => element.Kind is ReceiptPrintElementKind.Text or ReceiptPrintElementKind.Separator)
            .Select(element => element.Text));
}

public sealed record ReceiptPrinterDriverResult(bool Succeeded, string Message);

public sealed record ReceiptPrintResult(bool Succeeded, string Message, Guid? OrderGuid = null);

public interface IReceiptPrinterSettingsStore
{
    Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default);
}

public interface IReceiptTextFormatter
{
    ReceiptPrintDocument Build(
        ReceiptDetails receipt,
        ReceiptPrinterSettings settings,
        DateTimeOffset? printTime = null);
}

public interface IReceiptPrinterDriver
{
    Task<ReceiptPrinterDriverResult> PrintAsync(
        ReceiptPrintDocument document,
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrinterDriverResult> TestAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default);
}

public interface IReceiptPrintService
{
    Task<ReceiptPrintResult> PrintLatestReceiptAsync(
        ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> PrintReceiptAsync(
        Guid orderGuid,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> PrintReceiptAsync(
        ReceiptDetails receipt,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default);
}

public interface ICardReceiptPrintedNotifier
{
    Task MarkReceiptPrintedAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken = default);
}

public enum LinklyBankReceiptKind
{
    SignatureRequired,
    Declined,
    RecoveredApproved,
    RecoveredFailed,
    Settlement
}

public interface ILinklyBankReceiptPrinter
{
    Task<ReceiptPrintResult> PrintAsync(
        string environment,
        string sessionId,
        string receiptText,
        LinklyBankReceiptKind kind = LinklyBankReceiptKind.SignatureRequired,
        string? cardType = null,
        string? maskedCardNumber = null,
        string? responseCode = null,
        string? responseText = null,
        CancellationToken cancellationToken = default);
}

public interface ICashDrawerService
{
    Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed class ReceiptPrinterSettingsStore : IReceiptPrinterSettingsStore, IReceiptProfileLocalStore
{
    private const string Prefix = "ReceiptPrinter:";
    private const string PrinterPortKey = Prefix + "Port";
    private const string BrandNameKey = Prefix + "BrandName";
    private const string StoreNameKey = Prefix + "StoreName";
    private const string StoreAddressKey = Prefix + "StoreAddress";
    private const string StorePhoneKey = Prefix + "StorePhone";
    private const string AbnKey = Prefix + "Abn";
    private const string ReturnPolicyKey = Prefix + "ReturnPolicy";
    private const string CutDistanceKey = Prefix + "CutDistance";
    // 中文注释：与端口、切纸距离一样按设备保存，不随门店资料作用域迁移。
    private const string PrintBankReceiptTextKey = Prefix + "PrintBankReceiptText";
    private const string ProfileStoreCodeKey = Prefix + "ProfileStoreCode";
    // 中文注释：总部下发版本与已回执版本。两个键不带门店作用域，有效性完全取决于 ProfileStoreCode
    // 是否等于当前门店：换店后旧值读出来一律按 0，重新绑定时在同一次批量写里清零。
    private const string ProfileVersionKey = Prefix + "ProfileVersion";
    private const string ProfileAckedVersionKey = Prefix + "ProfileAckedVersion";

    // 中文注释：设置页保存、测试打印前保存与后台同步写入都会改资料键；用进程内互斥串行化，
    // 避免「保存读到版本 0 → 同步写入新版本 → 保存用旧草稿覆盖六个字段」造成字段与版本不一致。
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ILocalAppSettingsRepository _settingsRepository;
    private readonly DeviceAuthorizationState? _deviceAuthorizationState;
    private readonly ILocalDeviceRepository? _localDeviceRepository;

    public ReceiptPrinterSettingsStore(
        ILocalAppSettingsRepository settingsRepository,
        DeviceAuthorizationState? deviceAuthorizationState = null,
        ILocalDeviceRepository? localDeviceRepository = null)
    {
        _settingsRepository = settingsRepository;
        _deviceAuthorizationState = deviceAuthorizationState;
        _localDeviceRepository = localDeviceRepository;
    }

    public async Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var port = NormalizePort(await _settingsRepository.GetValueAsync(PrinterPortKey, cancellationToken));
        var cutDistance = NormalizeCutDistance(
            await _settingsRepository.GetValueAsync(CutDistanceKey, cancellationToken),
            ReceiptPrinterSettings.Default.CutDistance);

        var printBankReceiptText = NormalizeFlag(
            await _settingsRepository.GetValueAsync(PrintBankReceiptTextKey, cancellationToken),
            ReceiptPrinterSettings.Default.PrintBankReceiptText);

        var storeCode = CurrentStoreCode;
        var settings = storeCode is null
            ? await LoadLegacyUnscopedAsync(port, cutDistance, cancellationToken)
            : await LoadScopedAsync(storeCode, port, cutDistance, cancellationToken);
        return settings with { PrintBankReceiptText = printBankReceiptText };
    }

    public async Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var storeCode = CurrentStoreCode;
            var values = storeCode is null
                ? BuildLegacyWrite(settings)
                : await BuildScopedSaveAsync(storeCode, settings, cancellationToken);
            await _settingsRepository.SetValuesAsync(values, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ReceiptProfileLocalState> LoadProfileStateAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        return await ReadProfileStateAsync(storeCode, cancellationToken);
    }

    public async Task<bool> ApplyHeadquartersProfileAsync(
        string storeCode,
        ReceiptProfileFields fields,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // 设备在请求期间换了店：旧店的下发资料不能写进新店，调用方丢弃本次结果即可。
            if (CurrentStoreCode is { } currentStoreCode &&
                !string.Equals(currentStoreCode, storeCode, StringComparison.Ordinal))
            {
                return false;
            }

            // 中文注释：六个字段 + 版本 + 绑定门店 + 回执版本清零放进同一次批量写（本地库里是一个事务），
            // 要么全部生效要么全部不生效，不会出现「字段是新的、版本是旧的」的半截状态。
            // 回执版本清零：新版本写入后必须重新回执，哪怕旧回执的版本号更大（服务端重建后版本回退）。
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProfileStoreCodeKey] = storeCode,
                [ProfileKey(storeCode, "BrandName")] = NormalizeText(fields.BrandName, string.Empty),
                [ProfileKey(storeCode, "StoreName")] = NormalizeText(fields.StoreName, string.Empty),
                [ProfileKey(storeCode, "StoreAddress")] = NormalizeText(fields.Address, string.Empty),
                [ProfileKey(storeCode, "StorePhone")] = NormalizeText(fields.Phone, string.Empty),
                [ProfileKey(storeCode, "Abn")] = NormalizeText(fields.Abn, string.Empty),
                [ProfileKey(storeCode, "ReturnPolicy")] = NormalizeText(fields.ReturnPolicy, string.Empty),
                [ProfileVersionKey] = version.ToString(CultureInfo.InvariantCulture),
                [ProfileAckedVersionKey] = "0",
            };
            await _settingsRepository.SetValuesAsync(values, cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> MarkProfileAckedAsync(
        string storeCode,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // 只有「本机仍绑定这家店、且当前版本正是被回执的版本」才记已回执，
            // 否则回执期间资料已被替换/换店，旧回执不能算在新版本头上。
            var state = await ReadProfileStateAsync(storeCode, cancellationToken);
            if (state.Version != version)
            {
                return false;
            }

            await _settingsRepository.SetValueAsync(
                ProfileAckedVersionKey,
                version.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string? CurrentStoreCode =>
        _deviceAuthorizationState?.Current?.StoreCode is { Length: > 0 } code ? code : null;

    private async Task<ReceiptPrinterSettings> LoadLegacyUnscopedAsync(
        string port,
        int cutDistance,
        CancellationToken cancellationToken)
    {
        // 无设备授权上下文时保持旧版无作用域读取行为，避免启动早期破坏既有配置。
        var fallback = ReceiptPrinterSettings.Default;
        return new ReceiptPrinterSettings(
            port,
            NormalizeText(await _settingsRepository.GetValueAsync(BrandNameKey, cancellationToken), fallback.BrandName),
            NormalizeText(await _settingsRepository.GetValueAsync(StoreNameKey, cancellationToken), fallback.StoreName),
            NormalizeText(await _settingsRepository.GetValueAsync(StoreAddressKey, cancellationToken), fallback.StoreAddress),
            NormalizeText(await _settingsRepository.GetValueAsync(StorePhoneKey, cancellationToken), fallback.StorePhone),
            NormalizeText(await _settingsRepository.GetValueAsync(AbnKey, cancellationToken), fallback.Abn),
            NormalizeText(await _settingsRepository.GetValueAsync(ReturnPolicyKey, cancellationToken), fallback.ReturnPolicy),
            cutDistance);
    }

    private async Task<ReceiptPrinterSettings> LoadScopedAsync(
        string storeCode,
        string port,
        int cutDistance,
        CancellationToken cancellationToken)
    {
        var boundCode = await _settingsRepository.GetValueAsync(ProfileStoreCodeKey, cancellationToken);

        if (boundCode is null)
        {
            // 首次升级：仅有旧的、无作用域 profile 值时才绑定当前店，避免把纯硬件配置误判为 profile。
            var legacy = await ReadProfileAsync(null, cancellationToken);
            if (!legacy.HasAnyValue)
            {
                return CreateScopedSettings(port, cutDistance, legacy, await ResolveCurrentStoreNameAsync(storeCode, cancellationToken));
            }

            await _settingsRepository.SetValuesAsync(BuildMigrationWrite(storeCode, legacy), cancellationToken);
            return CreateScopedSettings(port, cutDistance, legacy, await ResolveCurrentStoreNameAsync(storeCode, cancellationToken));
        }

        if (!string.Equals(boundCode, storeCode, StringComparison.Ordinal))
        {
            // 设备改店：旧店资料不得用于打印，仅保留硬件设置并安全回退当前店名/店号。
            // 旧店的下发版本同样不属于新店，ProfileVersion 保持 0（资料可编辑，同步会重新拉取新店资料）。
            var empty = new ProfileSnapshot(null, null, null, null, null, null);
            return CreateScopedSettings(port, cutDistance, empty, await ResolveCurrentStoreNameAsync(storeCode, cancellationToken));
        }

        var scoped = await ReadProfileAsync(storeCode, cancellationToken);
        var profileVersion = ParseVersion(await _settingsRepository.GetValueAsync(ProfileVersionKey, cancellationToken));
        return CreateScopedSettings(
            port,
            cutDistance,
            scoped,
            await ResolveCurrentStoreNameAsync(storeCode, cancellationToken),
            profileVersion);
    }

    private static ReceiptPrinterSettings CreateScopedSettings(
        string port,
        int cutDistance,
        ProfileSnapshot snapshot,
        string storeNameFallback,
        int profileVersion = 0)
    {
        return new ReceiptPrinterSettings(
            port,
            NormalizeStored(snapshot.BrandName, string.Empty),
            NormalizeStored(snapshot.StoreName, storeNameFallback),
            NormalizeStored(snapshot.StoreAddress, string.Empty),
            NormalizeStored(snapshot.StorePhone, string.Empty),
            NormalizeStored(snapshot.Abn, string.Empty),
            NormalizeStored(snapshot.ReturnPolicy, string.Empty),
            cutDistance,
            ProfileVersion: profileVersion);
    }

    /// <summary>
    /// 读取指定门店的下发版本状态：本机没有绑定这家店（含从未绑定、已换店）一律视为 (0, 0)。
    /// </summary>
    private async Task<ReceiptProfileLocalState> ReadProfileStateAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var boundCode = await _settingsRepository.GetValueAsync(ProfileStoreCodeKey, cancellationToken);
        if (!string.Equals(boundCode, storeCode, StringComparison.Ordinal))
        {
            return ReceiptProfileLocalState.None;
        }

        var version = ParseVersion(await _settingsRepository.GetValueAsync(ProfileVersionKey, cancellationToken));
        var acked = ParseVersion(await _settingsRepository.GetValueAsync(ProfileAckedVersionKey, cancellationToken));
        return new ReceiptProfileLocalState(version, acked);
    }

    private static int ParseVersion(string? value)
    {
        // 损坏、缺失或非正数都按 0（从未应用过下发），不让脏值把资料锁成只读。
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : 0;
    }

    private async Task<ProfileSnapshot> ReadProfileAsync(string? storeCode, CancellationToken cancellationToken)
    {
        return new ProfileSnapshot(
            await _settingsRepository.GetValueAsync(ProfileKey(storeCode, "BrandName"), cancellationToken),
            await _settingsRepository.GetValueAsync(ProfileKey(storeCode, "StoreName"), cancellationToken),
            await _settingsRepository.GetValueAsync(ProfileKey(storeCode, "StoreAddress"), cancellationToken),
            await _settingsRepository.GetValueAsync(ProfileKey(storeCode, "StorePhone"), cancellationToken),
            await _settingsRepository.GetValueAsync(ProfileKey(storeCode, "Abn"), cancellationToken),
            await _settingsRepository.GetValueAsync(ProfileKey(storeCode, "ReturnPolicy"), cancellationToken));
    }

    private async Task<string> ResolveCurrentStoreNameAsync(string storeCode, CancellationToken cancellationToken)
    {
        if (_localDeviceRepository is not null)
        {
            try
            {
                var device = await _localDeviceRepository.GetLatestAsync(cancellationToken);
                if (device is not null &&
                    string.Equals(device.StoreCode?.Trim(), storeCode, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(device.StoreName))
                {
                    return device.StoreName.Trim();
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                // 本地设备缓存读取失败时退化为店号，避免阻塞小票设置加载。
            }
        }

        return storeCode;
    }

    private async Task<Dictionary<string, string>> BuildScopedSaveAsync(
        string storeCode,
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken)
    {
        var boundCode = await _settingsRepository.GetValueAsync(ProfileStoreCodeKey, cancellationToken);
        var sameStore = string.Equals(boundCode, storeCode, StringComparison.Ordinal);
        if (sameStore &&
            ParseVersion(await _settingsRepository.GetValueAsync(ProfileVersionKey, cancellationToken)) > 0)
        {
            // 中文注释：资料来自总部下发（只读）时，保存/测试打印只落硬件设置，
            // 绝不用设置页里可能已过期的草稿覆盖六个资料字段，资料只由同步写入。
            return BuildHardwareOnlyWrite(settings);
        }

        var values = BuildScopedWrite(storeCode, settings);
        if (!sameStore)
        {
            // 换店后重新绑定：旧店的下发版本不能被新店继承，与绑定写入放在同一次批量写里清零。
            values[ProfileVersionKey] = "0";
            values[ProfileAckedVersionKey] = "0";
        }

        return values;
    }

    private static Dictionary<string, string> BuildHardwareOnlyWrite(ReceiptPrinterSettings settings)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrinterPortKey] = NormalizePort(settings.PrinterPort),
            [CutDistanceKey] = Math.Max(1, settings.CutDistance).ToString(CultureInfo.InvariantCulture),
            [PrintBankReceiptTextKey] = FormatFlag(settings.PrintBankReceiptText),
        };
    }

    private static Dictionary<string, string> BuildLegacyWrite(ReceiptPrinterSettings settings)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrinterPortKey] = NormalizePort(settings.PrinterPort),
            [BrandNameKey] = NormalizeText(settings.BrandName, string.Empty),
            [StoreNameKey] = NormalizeText(settings.StoreName, string.Empty),
            [StoreAddressKey] = NormalizeText(settings.StoreAddress, string.Empty),
            [StorePhoneKey] = NormalizeText(settings.StorePhone, string.Empty),
            [AbnKey] = NormalizeText(settings.Abn, string.Empty),
            [ReturnPolicyKey] = NormalizeText(settings.ReturnPolicy, string.Empty),
            [CutDistanceKey] = Math.Max(1, settings.CutDistance).ToString(CultureInfo.InvariantCulture),
            [PrintBankReceiptTextKey] = FormatFlag(settings.PrintBankReceiptText),
        };
    }

    private static Dictionary<string, string> BuildScopedWrite(string storeCode, ReceiptPrinterSettings settings)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrinterPortKey] = NormalizePort(settings.PrinterPort),
            [CutDistanceKey] = Math.Max(1, settings.CutDistance).ToString(CultureInfo.InvariantCulture),
            [PrintBankReceiptTextKey] = FormatFlag(settings.PrintBankReceiptText),
            [ProfileStoreCodeKey] = storeCode,
            [ProfileKey(storeCode, "BrandName")] = NormalizeText(settings.BrandName, string.Empty),
            [ProfileKey(storeCode, "StoreName")] = NormalizeText(settings.StoreName, string.Empty),
            [ProfileKey(storeCode, "StoreAddress")] = NormalizeText(settings.StoreAddress, string.Empty),
            [ProfileKey(storeCode, "StorePhone")] = NormalizeText(settings.StorePhone, string.Empty),
            [ProfileKey(storeCode, "Abn")] = NormalizeText(settings.Abn, string.Empty),
            [ProfileKey(storeCode, "ReturnPolicy")] = NormalizeText(settings.ReturnPolicy, string.Empty),
        };
    }

    private static Dictionary<string, string> BuildMigrationWrite(string storeCode, ProfileSnapshot snapshot)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProfileStoreCodeKey] = storeCode,
            [ProfileKey(storeCode, "BrandName")] = NormalizeStored(snapshot.BrandName, string.Empty),
            [ProfileKey(storeCode, "StoreName")] = NormalizeStored(snapshot.StoreName, string.Empty),
            [ProfileKey(storeCode, "StoreAddress")] = NormalizeStored(snapshot.StoreAddress, string.Empty),
            [ProfileKey(storeCode, "StorePhone")] = NormalizeStored(snapshot.StorePhone, string.Empty),
            [ProfileKey(storeCode, "Abn")] = NormalizeStored(snapshot.Abn, string.Empty),
            [ProfileKey(storeCode, "ReturnPolicy")] = NormalizeStored(snapshot.ReturnPolicy, string.Empty),
        };
    }

    private static string ProfileKey(string? storeCode, string suffix)
    {
        return storeCode is null ? Prefix + suffix : $"{Prefix}{storeCode}:{suffix}";
    }

    private static string NormalizePort(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? ReceiptPrinterSettings.DefaultPrinterPort : value.Trim();
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeStored(string? value, string fallback)
    {
        // 区分 key 缺失（null）与显式空串：显式空串必须原样保留，不得被默认值覆盖。
        return value is null ? fallback : value.Trim();
    }

    private static int NormalizeCutDistance(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var distance) && distance > 0
            ? distance
            : fallback;
    }

    private static bool NormalizeFlag(string? value, bool fallback)
    {
        // 中文注释：旧设备没有该键或值损坏时回退默认值（打印），避免升级后静默改变小票内容。
        return bool.TryParse(value, out var flag) ? flag : fallback;
    }

    private static string FormatFlag(bool value)
    {
        return value ? "true" : "false";
    }

    private sealed record ProfileSnapshot(
        string? BrandName,
        string? StoreName,
        string? StoreAddress,
        string? StorePhone,
        string? Abn,
        string? ReturnPolicy)
    {
        public bool HasAnyValue =>
            BrandName is not null ||
            StoreName is not null ||
            StoreAddress is not null ||
            StorePhone is not null ||
            Abn is not null ||
            ReturnPolicy is not null;
    }
}

public sealed class ReceiptTextFormatter : IReceiptTextFormatter
{
    private const int LineWidth = 42;
    private const int AddressLineWidth = 35;

    public ReceiptPrintDocument Build(
        ReceiptDetails receipt,
        ReceiptPrinterSettings settings,
        DateTimeOffset? printTime = null)
    {
        var builder = new ReceiptDocumentBuilder();
        var printedAt = printTime ?? DateTimeOffset.Now;
        var orderId = receipt.OrderGuid.ToString();

        var brandName = FirstNonBlank(settings.BrandName, settings.StoreName, receipt.StoreCode);
        builder.Text(brandName, ReceiptPrintAlignment.Center, isEmphasized: true);
        if (!string.IsNullOrWhiteSpace(settings.StoreName) &&
            !string.Equals(settings.StoreName.Trim(), brandName, StringComparison.OrdinalIgnoreCase))
        {
            builder.Text(settings.StoreName.Trim(), ReceiptPrintAlignment.Center);
        }

        foreach (var addressLine in WrapByWord(settings.StoreAddress, AddressLineWidth))
        {
            builder.Text(addressLine, ReceiptPrintAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(settings.StorePhone))
        {
            builder.Text($"Tel: {settings.StorePhone.Trim()}", ReceiptPrintAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(settings.Abn))
        {
            builder.Text($"ABN: {settings.Abn.Trim()}", ReceiptPrintAlignment.Center);
        }

        if (receipt.RefundVoucher is { } refundVoucher)
        {
            // 中文注释：退款代金券必须是独立券面，避免商品和支付明细被误当作普通退款收据打印。
            return BuildRefundVoucherDocument(builder, receipt, refundVoucher, printedAt);
        }

        if (receipt.VoucherBalance is { } voucherBalance)
        {
            // 中文注释：余额凭证独立出票，不混入商品、付款明细或 Linkly 银行原文。
            return BuildVoucherBalanceDocument(builder, receipt, voucherBalance, settings, printedAt);
        }

        builder.Blank();
        builder.Text(string.IsNullOrWhiteSpace(receipt.DocumentTitle)
            ? "===== TAX INVOICE ====="
            : receipt.DocumentTitle.Trim(), ReceiptPrintAlignment.Center);
        builder.Blank();
        var statusText = string.IsNullOrWhiteSpace(receipt.StatusText)
            ? "*** Paid ***"
            : receipt.StatusText.Trim();
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            builder.Text(statusText, ReceiptPrintAlignment.Center, isEmphasized: true);
            builder.Blank();
        }

        var displayOrderId = string.IsNullOrWhiteSpace(receipt.OrderDisplay)
            ? orderId
            : receipt.OrderDisplay.Trim();
        builder.Text($"Order: {displayOrderId}");
        builder.Text($"Date: {receipt.SoldAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.Text($"Cashier: {receipt.CashierName}");
        // 中文注释：门店名称与代码共用显示规则，并按纸宽换行，保证打印和预览保持一致。
        foreach (var storeLine in WrapByWord(
                     $"Store: {FormatStoreDisplay(settings.StoreName, receipt.StoreCode)}",
                     LineWidth))
        {
            builder.Text(storeLine);
        }
        builder.Text($"Device: {receipt.DeviceCode}");
        foreach (var infoLine in receipt.ExtraInfoLines ?? [])
        {
            if (!string.IsNullOrWhiteSpace(infoLine))
            {
                builder.Text(infoLine.Trim());
            }
        }

        builder.Separator();
        builder.Text(FitColumns("ITEM", "QTY", "PRICE", 25, 5, 12));
        builder.Separator();

        foreach (var line in receipt.Lines)
        {
            foreach (var nameLine in WrapByWord(line.DisplayName, LineWidth))
            {
                builder.Text(nameLine);
            }

            builder.Text(FitColumns(
                TrimTo(line.LookupCode, 25),
                line.QuantityDisplay,
                Money(line.ActualAmount),
                25,
                5,
                12));

            if (line.DiscountAmount != 0m)
            {
                builder.Text(FitTwoColumns("Dis", $"-{Money(line.DiscountAmount)}"));
            }
        }

        builder.Separator();
        builder.Text(FitTwoColumns("Subtotal", Money(receipt.TotalAmount)));
        if (receipt.DiscountAmount != 0m)
        {
            builder.Text(FitTwoColumns("Discount", $"-{Money(receipt.DiscountAmount)}"));
        }

        var gst = decimal.Round(receipt.ActualAmount / 11m, 2, MidpointRounding.AwayFromZero);
        builder.Text(FitTwoColumns("GST", Money(gst)));
        builder.Text(FitTwoColumns("Total(inc GST)", Money(receipt.ActualAmount)), isEmphasized: true);
        builder.Separator();
        builder.Text("Payment:");

        foreach (var payment in receipt.Payments)
        {
            builder.Text(FitTwoColumns(payment.MethodLabel, Money(payment.Amount)));
            if (!string.IsNullOrWhiteSpace(payment.DisplayReference))
            {
                builder.Text($"  {payment.DisplayReference}");
            }

            if (!string.IsNullOrWhiteSpace(payment.CardSummary))
            {
                builder.Text($"  {payment.CardSummary}");
            }
        }

        // 中文注释：设置关闭时整段跳过银行原文，只保留上方付款行里的卡类型/后四位，缩短客人小票。
        var receiptTexts = settings.PrintBankReceiptText
            ? receipt.Payments
                .Select(payment => payment.ReceiptText)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToList()
            : [];
        if (receiptTexts.Count > 0)
        {
            builder.Separator();
            foreach (var receiptText in receiptTexts)
            {
                foreach (var line in receiptText.Replace("\r\n", "\n").Split('\n'))
                {
                    builder.Text(LinklyBankReceiptTextSanitizer.Sanitize(line));
                }
                builder.Blank();
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ReturnPolicy))
        {
            builder.Separator();
            builder.Text("Refunds and returns", ReceiptPrintAlignment.Center, isEmphasized: true);
            foreach (var line in WrapByDisplayWidth(settings.ReturnPolicy, LineWidth))
            {
                builder.Text(line, ReceiptPrintAlignment.Center);
            }
        }

        builder.Separator();
        builder.Barcode(orderId);
        builder.QrCode(orderId);
        builder.Text($"Print Time: {printedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.Text($"Device: {receipt.DeviceCode}");
        builder.Blank();
        builder.Text("Thank you for your purchase!", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Blank();

        return builder.Build();
    }

    private static ReceiptPrintDocument BuildRefundVoucherDocument(
        ReceiptDocumentBuilder builder,
        ReceiptDetails receipt,
        RefundVoucherReceipt refundVoucher,
        DateTimeOffset printedAt)
    {
        var displayOrderId = string.IsNullOrWhiteSpace(receipt.OrderDisplay)
            ? receipt.OrderGuid.ToString()
            : receipt.OrderDisplay.Trim();

        builder.Blank();
        builder.Text("===== REFUND VOUCHER =====", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Blank();
        builder.Text($"Voucher: {refundVoucher.VoucherCode}", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Text($"Amount: {Money(refundVoucher.Amount)}", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Separator();
        builder.Text($"Order: {displayOrderId}");
        builder.Text($"Print Time: {printedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.Barcode(refundVoucher.VoucherCode);
        builder.QrCode(refundVoucher.VoucherCode);
        builder.Blank();

        return builder.Build();
    }

    private static ReceiptPrintDocument BuildVoucherBalanceDocument(
        ReceiptDocumentBuilder builder,
        ReceiptDetails receipt,
        VoucherBalanceReceipt voucherBalance,
        ReceiptPrinterSettings settings,
        DateTimeOffset printedAt)
    {
        var voucherCode = voucherBalance.VoucherCode.Trim();
        var displayOrderId = string.IsNullOrWhiteSpace(receipt.OrderDisplay)
            ? receipt.OrderGuid.ToString()
            : receipt.OrderDisplay.Trim();

        builder.Blank();
        builder.Text("===== VOUCHER BALANCE =====", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Blank();
        // 中文注释：余额凭证沿用普通小票的门店名称与代码规则，并保证所有文本不超过纸宽。
        foreach (var storeLine in WrapByWord(
                     $"Store: {FormatStoreDisplay(settings.StoreName, receipt.StoreCode)}",
                     LineWidth))
        {
            builder.Text(storeLine);
        }
        foreach (var voucherLine in WrapByWord($"Voucher: {voucherCode}", LineWidth))
        {
            builder.Text(voucherLine, ReceiptPrintAlignment.Center, isEmphasized: true);
        }
        builder.Text($"Balance: {Money(voucherBalance.RemainingBalance)}", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Separator();
        foreach (var orderLine in WrapByWord($"Order: {displayOrderId}", LineWidth))
        {
            builder.Text(orderLine);
        }
        builder.Text($"Print Time: {printedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.Barcode(voucherCode);
        builder.QrCode(voucherCode);
        builder.Blank();

        return builder.Build();
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string FormatStoreDisplay(string? storeName, string? storeCode)
    {
        var name = storeName?.Trim() ?? string.Empty;
        var code = storeCode?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return code.Length == 0 ? "-" : code;
        }

        if (code.Length == 0)
        {
            return name;
        }

        return string.Equals(name, code, StringComparison.OrdinalIgnoreCase)
            ? code
            : $"{name} ({code})";
    }

    private static IReadOnlyList<string> WrapByWord(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new StringBuilder();
            foreach (var word in words)
            {
                if (word.Length > maxChars)
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    for (var index = 0; index < word.Length; index += maxChars)
                    {
                        lines.Add(word.Substring(index, Math.Min(maxChars, word.Length - index)));
                    }

                    continue;
                }

                var nextLength = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
                if (nextLength > maxChars)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines;
    }

    private static IReadOnlyList<string> WrapByDisplayWidth(string? text, int maxColumns)
    {
        if (string.IsNullOrWhiteSpace(text) || maxColumns <= 0)
        {
            return [];
        }

        var lines = new List<string>();
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\t', ' ');
        foreach (var paragraph in normalized.Split('\n'))
        {
            var words = paragraph.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new StringBuilder();
            var currentWidth = 0;

            foreach (var word in words)
            {
                var wordWidth = ReceiptDisplayWidth(word);
                if (currentWidth > 0 && currentWidth + 1 + wordWidth <= maxColumns)
                {
                    current.Append(' ').Append(word);
                    currentWidth += 1 + wordWidth;
                    continue;
                }

                if (currentWidth > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    currentWidth = 0;
                }

                if (wordWidth <= maxColumns)
                {
                    current.Append(word);
                    currentWidth = wordWidth;
                    continue;
                }

                foreach (var rune in word.EnumerateRunes())
                {
                    var runeWidth = ReceiptDisplayWidth(rune);
                    if (currentWidth > 0 && currentWidth + runeWidth > maxColumns)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        currentWidth = 0;
                    }

                    current.Append(rune.ToString());
                    currentWidth += runeWidth;
                }
            }

            if (currentWidth > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines;
    }

    private static int ReceiptDisplayWidth(string value)
    {
        return value.EnumerateRunes().Sum(ReceiptDisplayWidth);
    }

    private static int ReceiptDisplayWidth(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x1100 and <= 0x115F
            or 0x2329 or 0x232A
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD
            ? 2
            : 1;
    }

    private static string Money(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }

    private static string FitColumns(string left, string middle, string right, int leftWidth, int middleWidth, int rightWidth)
    {
        return TrimTo(left, leftWidth).PadRight(leftWidth) +
            TrimTo(middle, middleWidth).PadLeft(middleWidth) +
            TrimTo(right, rightWidth).PadLeft(rightWidth);
    }

    private static string FitTwoColumns(string left, string right)
    {
        left = TrimTo(left, 24);
        right = TrimTo(right, 16);
        return left + new string(' ', Math.Max(1, LineWidth - left.Length - right.Length)) + right;
    }

    private static string TrimTo(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return maxChars <= 3 ? value[..maxChars] : value[..(maxChars - 3)] + "...";
    }

    private sealed class ReceiptDocumentBuilder
    {
        private readonly List<ReceiptPrintElement> _elements = [];
        private readonly List<ReceiptPreviewRow> _previewRows = [];

        public void Text(
            string text,
            ReceiptPrintAlignment alignment = ReceiptPrintAlignment.Left,
            bool isEmphasized = false)
        {
            var normalized = text ?? string.Empty;
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Text, normalized, alignment, isEmphasized));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, normalized, alignment, isEmphasized));
        }

        public void Blank()
        {
            Text(string.Empty);
        }

        public void Separator()
        {
            var text = new string('-', LineWidth);
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Separator, text));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, text));
        }

        public void Barcode(string text)
        {
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Barcode, text, ReceiptPrintAlignment.Center));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Barcode, $"BARCODE {text}", ReceiptPrintAlignment.Center));
        }

        public void QrCode(string text)
        {
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.QrCode, text, ReceiptPrintAlignment.Center));
            _previewRows.Add(new ReceiptPreviewRow(
                ReceiptPreviewRowKind.QrCode,
                $"QR {text}",
                ReceiptPrintAlignment.Center)
            {
                QrCodeValue = text
            });
        }

        public ReceiptPrintDocument Build()
        {
            return new ReceiptPrintDocument(_elements, _previewRows);
        }
    }
}

public sealed class ReceiptPrintService(
    IReceiptQueryService receiptQueryService,
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptTextFormatter formatter,
    IReceiptPrinterDriver driver,
    IEnumerable<ICardReceiptPrintedNotifier>? cardReceiptPrintedNotifiers = null,
    ILocalizationService? localization = null,
    DeviceAuthorizationState? deviceAuthorizationState = null) : IReceiptPrintService, IDisposable
{
    private readonly SemaphoreSlim _printLock = new(1, 1);
    private readonly IReadOnlyList<ICardReceiptPrintedNotifier> _cardReceiptPrintedNotifiers =
        (cardReceiptPrintedNotifiers ?? []).ToArray();

    public async Task<ReceiptPrintResult> PrintLatestReceiptAsync(
        ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
        CancellationToken cancellationToken = default)
    {
        var receipt = await receiptQueryService.GetLatestReceiptAsync(cancellationToken);
        return receipt is null
            ? new ReceiptPrintResult(false, T("receipt.print.noReceiptFound", "No receipt found."))
            : await PrintReceiptAsync(receipt, reason, cancellationToken);
    }

    public async Task<ReceiptPrintResult> PrintReceiptAsync(
        Guid orderGuid,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        var receipt = await receiptQueryService.GetReceiptAsync(orderGuid, cancellationToken);
        return receipt is null
            ? new ReceiptPrintResult(false, T("receipt.print.noReceiptFound", "No receipt found."), orderGuid)
            : await PrintReceiptAsync(receipt, reason, cancellationToken);
    }

    public async Task<ReceiptPrintResult> PrintReceiptAsync(
        ReceiptDetails receipt,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        await _printLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            // 中文注释：退款券和余额凭证都是即时新出票，需记录实际打印时间。
            DateTimeOffset? printTime = reason is ReceiptPrintReason.VoucherRefundAuto or ReceiptPrintReason.VoucherBalanceAuto
                ? null
                : receipt.SoldAt;
            var document = formatter.Build(receipt, settings, printTime);
            var result = await driver.PrintAsync(document, settings, cancellationToken);
            if (result.Succeeded && reason != ReceiptPrintReason.VoucherBalanceAuto)
            {
                await MarkCardReceiptsPrintedAsync(receipt, cancellationToken);
            }

            return new ReceiptPrintResult(
                result.Succeeded,
                result.Succeeded ? T("receipt.print.success", "Receipt printed.") : result.Message,
                receipt.OrderGuid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message, receipt.OrderGuid);
        }
        finally
        {
            _printLock.Release();
        }
    }

    private async Task MarkCardReceiptsPrintedAsync(
        ReceiptDetails receipt,
        CancellationToken cancellationToken)
    {
        if (_cardReceiptPrintedNotifiers.Count == 0)
        {
            return;
        }

        var markers = receipt.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Card)
            .Select(payment => payment.Reference)
            .Select(reference => LinklyBackendPaymentReference.TryGetPrintMarker(reference, out var environment, out var sessionId)
                ? (Environment: environment, SessionId: sessionId)
                : (Environment: string.Empty, SessionId: string.Empty))
            .Where(marker => !string.IsNullOrWhiteSpace(marker.Environment) && !string.IsNullOrWhiteSpace(marker.SessionId))
            .Distinct()
            .ToArray();
        foreach (var marker in markers)
        {
            foreach (var notifier in _cardReceiptPrintedNotifiers)
            {
                // 小票已经实际打印成功；后端标记失败不能反向改写打印结果，只记录并允许后续恢复再补打。
                try
                {
                    await notifier.MarkReceiptPrintedAsync(marker.Environment, marker.SessionId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[HBPOS][Client][Receipt] {DateTimeOffset.Now:O} card receipt printed marker failed session={marker.SessionId} error={ex.GetType().Name}");
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"[HBPOS][Client][Receipt] {DateTimeOffset.Now:O} card receipt printed marker timed out session={marker.SessionId} error={ex.GetType().Name}");
                }
            }
        }
    }

    public async Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
    {
        await _printLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            // 中文注释：测试打印复用正式格式器与驱动，仅构造样例小票；不建订单、不触发支付通知或业务审计。
            var storeCode = deviceAuthorizationState?.Current?.StoreCode?.Trim();
            var receipt = CreateTestReceipt(
                string.IsNullOrWhiteSpace(storeCode) ? "TEST" : storeCode);
            var document = formatter.Build(receipt, settings);
            var result = await driver.PrintAsync(document, settings, cancellationToken);
            return new ReceiptPrintResult(result.Succeeded, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
        finally
        {
            _printLock.Release();
        }
    }

    private static ReceiptDetails CreateTestReceipt(string storeCode)
    {
        return new ReceiptDetails(
            Guid.Empty,
            storeCode,
            "POS-01",
            "Test",
            DateTimeOffset.Now,
            9.20m,
            0.20m,
            9.00m,
            [new ReceiptPreviewLine("Test Item", "TEST-001", 1m, 9.00m, 0m, 9.00m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 9.00m, null)],
            DocumentTitle: "===== TEST =====",
            StatusText: "*** NOT A SALE ***");
    }

    public void Dispose()
    {
        _printLock.Dispose();
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}

internal static class LinklyBankReceiptTextSanitizer
{
    private static readonly Regex FullPanRegex = new(@"(?<!\d)(?:\d[ \t\u00A0.\-]*){11,18}\d(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ReferenceLineRegex = new(
        @"^\s*(?:TXN\s*REF|RRN|RETRIEVAL\s*REF(?:ERENCE)?|STAN|TRACE(?:\s*NO)?|INVOICE(?:\s*NO)?|INV\s*NO)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (ReferenceLineRegex.IsMatch(text))
        {
            // Linkly 对账号不是卡号；保留这些行，方便收银员按银行小票核验交易。
            return text;
        }

        return FullPanRegex.Replace(text, match =>
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            return digits.Length is >= 12 and <= 19
                ? "****" + digits[^4..]
                : match.Value;
        });
    }

    public static string? BuildCardSummary(string? cardType, string? maskedCardNumber)
    {
        var type = Normalize(cardType);
        var masked = Normalize(maskedCardNumber);
        masked = masked is null ? null : Sanitize(masked);

        return (type, masked) switch
        {
            (null, null) => null,
            (null, _) => masked,
            (_, null) => type,
            _ => $"{type} {masked}"
        };
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed class LinklyBankReceiptPrinter(
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptPrinterDriver driver,
    IEnumerable<ICardReceiptPrintedNotifier>? cardReceiptPrintedNotifiers = null,
    ILocalizationService? localization = null) : ILinklyBankReceiptPrinter
{
    private const int LineWidth = 42;
    private readonly SemaphoreSlim _printLock = new(1, 1);
    private readonly IReadOnlyList<ICardReceiptPrintedNotifier> _cardReceiptPrintedNotifiers =
        (cardReceiptPrintedNotifiers ?? []).ToArray();

    public async Task<ReceiptPrintResult> PrintAsync(
        string environment,
        string sessionId,
        string receiptText,
        LinklyBankReceiptKind kind = LinklyBankReceiptKind.SignatureRequired,
        string? cardType = null,
        string? maskedCardNumber = null,
        string? responseCode = null,
        string? responseText = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(receiptText))
        {
            return new ReceiptPrintResult(false, T("linkly.signatureSlip.empty", "Signature receipt is empty."));
        }

        await _printLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            var document = BuildDocument(
                receiptText,
                kind,
                LinklyBankReceiptTextSanitizer.BuildCardSummary(cardType, maskedCardNumber),
                responseCode,
                responseText);
            var result = await driver.PrintAsync(document, settings, cancellationToken);
            if (!result.Succeeded)
            {
                return new ReceiptPrintResult(false, result.Message);
            }

            // 只有签名确认小票用后端 printed marker 去重；Declined 顾客银行小票不能复用这个状态。
            if (kind == LinklyBankReceiptKind.SignatureRequired)
            {
                foreach (var notifier in _cardReceiptPrintedNotifiers)
                {
                    try
                    {
                        await notifier.MarkReceiptPrintedAsync(environment, sessionId, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }
            }
            return new ReceiptPrintResult(true, T("receipt.print.success", "Receipt printed."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
        finally
        {
            _printLock.Release();
        }
    }

    private static ReceiptPrintDocument BuildDocument(
        string receiptText,
        LinklyBankReceiptKind kind,
        string? cardSummary,
        string? responseCode,
        string? responseText)
    {
        var elements = new List<ReceiptPrintElement>();
        var previewRows = new List<ReceiptPreviewRow>();

        void AddText(string text, ReceiptPrintAlignment alignment = ReceiptPrintAlignment.Left, bool isEmphasized = false)
        {
            elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Text, text, alignment, isEmphasized));
            previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, text, alignment, isEmphasized));
        }

        void AddSeparator()
        {
            var separator = new string('-', LineWidth);
            elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Separator, separator));
            previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, separator));
        }

        var heading = kind switch
        {
            LinklyBankReceiptKind.Declined => "*** DECLINED ***",
            LinklyBankReceiptKind.RecoveredApproved => "*** APPROVED RECOVERY ***",
            LinklyBankReceiptKind.RecoveredFailed => "*** NOT PAID ***",
            LinklyBankReceiptKind.Settlement => "*** SETTLEMENT ***",
            _ => "*** SIGNATURE REQUIRED ***"
        };
        AddText(heading, ReceiptPrintAlignment.Center, isEmphasized: true);
        AddSeparator();

        if (!string.IsNullOrWhiteSpace(cardSummary))
        {
            AddText($"Card: {cardSummary}");
        }

        if (!string.IsNullOrWhiteSpace(responseCode))
        {
            AddText($"Code: {LinklyBankReceiptTextSanitizer.Sanitize(responseCode)}");
        }

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            AddText($"Result: {LinklyBankReceiptTextSanitizer.Sanitize(responseText)}");
        }

        // 这里仅打印 Linkly 返回的刷卡签名内容，不能拼接订单商品明细。
        foreach (var line in receiptText.Replace("\r\n", "\n").Split('\n'))
        {
            AddText(LinklyBankReceiptTextSanitizer.Sanitize(line));
        }

        AddSeparator();
        if (kind == LinklyBankReceiptKind.SignatureRequired)
        {
            AddText("CUSTOMER SIGNATURE", ReceiptPrintAlignment.Center, isEmphasized: true);
            AddText(string.Empty);
            AddText(string.Empty);
            AddText(new string('_', 32), ReceiptPrintAlignment.Center, isEmphasized: true);
            AddText(string.Empty);
        }

        return new ReceiptPrintDocument(elements, previewRows);
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}

public sealed class CashDrawerService(
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptPrinterDriver driver,
    ILocalizationService? localization = null) : ICashDrawerService
{
    public async Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            var result = await driver.OpenCashDrawerAsync(settings, cancellationToken);
            return new ReceiptPrintResult(result.Succeeded, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}

public sealed class NoopCashDrawerService(ILocalizationService? localization = null) : ICashDrawerService
{
    public Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.drawer.notConfigured") ?? "Cash drawer is not configured."));
    }
}

public sealed class NoopReceiptPrintService(ILocalizationService? localization = null) : IReceiptPrintService
{
    public Task<ReceiptPrintResult> PrintLatestReceiptAsync(
        ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured."));
    }

    public Task<ReceiptPrintResult> PrintReceiptAsync(
        Guid orderGuid,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured.", orderGuid));
    }

    public Task<ReceiptPrintResult> PrintReceiptAsync(
        ReceiptDetails receipt,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured.", receipt.OrderGuid));
    }

    public Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured."));
    }
}

public sealed class XpReceiptPrinterDriver(ILocalizationService? localization = null) : IReceiptPrinterDriver, IDisposable
{
    private const int CashDrawerPinMode = 0;
    private const int CashDrawerOnTime = 25;
    private const int CashDrawerOffTime = 250;

    private readonly SemaphoreSlim _printerLock = new(1, 1);
    private bool _disposed;

    public async Task<ReceiptPrinterDriverResult> PrintAsync(
        ReceiptPrintDocument document,
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _printerLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => PrintCore(document, settings), cancellationToken);
        }
        finally
        {
            _printerLock.Release();
        }
    }

    public async Task<ReceiptPrinterDriverResult> TestAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default)
    {
        var document = new ReceiptPrintDocument(
            [
                new ReceiptPrintElement(ReceiptPrintElementKind.Text, "HBPOS Printer Test", ReceiptPrintAlignment.Center, true),
                new ReceiptPrintElement(ReceiptPrintElementKind.Text, $"Port: {settings.PrinterPort}"),
                new ReceiptPrintElement(ReceiptPrintElementKind.Text, $"Print Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}")
            ],
            []);
        return await PrintAsync(document, settings, cancellationToken);
    }

    public async Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _printerLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => OpenCashDrawerCore(settings), cancellationToken);
        }
        finally
        {
            _printerLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _printerLock.Dispose();
        _disposed = true;
    }

    private ReceiptPrinterDriverResult PrintCore(ReceiptPrintDocument document, ReceiptPrinterSettings settings)
    {
        var printer = InitPrinter(string.Empty);
        if (printer == IntPtr.Zero)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.initFailed", "Printer could not be initialized."));
        }

        var opened = false;
        try
        {
            var openResult = OpenPort(printer, string.IsNullOrWhiteSpace(settings.PrinterPort)
                ? ReceiptPrinterSettings.DefaultPrinterPort
                : settings.PrinterPort.Trim());
            if (openResult != 0)
            {
                return new ReceiptPrinterDriverResult(false, T("receipt.printer.portOpenFailed", "Printer port could not be opened."));
            }

            opened = true;
            var initializeResult = GetSdkFailure(PrinterInitialize(printer), T("receipt.printer.initFailed", "Printer could not be initialized."));
            if (initializeResult is not null)
            {
                return initializeResult;
            }

            var lineSpaceResult = GetSdkFailure(SetTextLineSpace(printer, 30), T("receipt.printer.lineSpacingFailed", "Printer line spacing could not be set."));
            if (lineSpaceResult is not null)
            {
                return lineSpaceResult;
            }

            var statusResult = GetPrinterNotReadyResult(printer);
            if (statusResult is not null)
            {
                return statusResult;
            }

            foreach (var element in document.Elements)
            {
                switch (element.Kind)
                {
                    case ReceiptPrintElementKind.Barcode:
                        var barcodeResult = GetSdkFailure(
                            PrintBarCode(printer, 8, element.Text, 2, 100, (int)ReceiptPrintAlignment.Center, 2),
                            T("receipt.printer.barcodeFailed", "Printer barcode could not be printed."));
                        if (barcodeResult is not null)
                        {
                            return barcodeResult;
                        }

                        break;
                    case ReceiptPrintElementKind.QrCode:
                        var qrResult = GetSdkFailure(
                            PrintSymbol(printer, 49, element.Text, 48, 7, 7, (int)ReceiptPrintAlignment.Center),
                            T("receipt.printer.qrCodeFailed", "Printer QR code could not be printed."));
                        if (qrResult is not null)
                        {
                            return qrResult;
                        }

                        break;
                    default:
                        var textResult = GetSdkFailure(
                            PrintText(printer, element.Text + "\r\n", (int)element.Alignment, element.IsEmphasized ? 1 : 0),
                            T("receipt.printer.textFailed", "Printer text could not be printed."));
                        if (textResult is not null)
                        {
                            return textResult;
                        }

                        break;
                }
            }

            var cutResult = GetSdkFailure(
                CutPaperWithDistance(printer, Math.Max(1, settings.CutDistance)),
                T("receipt.printer.cutPaperFailed", "Printer paper could not be cut."));
            if (cutResult is not null)
            {
                return cutResult;
            }

            return new ReceiptPrinterDriverResult(true, T("receipt.print.success", "Receipt printed."));
        }
        finally
        {
            if (opened)
            {
                ClosePort(printer);
            }

            ReleasePrinter(printer);
        }
    }

    private ReceiptPrinterDriverResult OpenCashDrawerCore(ReceiptPrinterSettings settings)
    {
        var printer = InitPrinter(string.Empty);
        if (printer == IntPtr.Zero)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.initFailed", "Printer could not be initialized."));
        }

        var opened = false;
        try
        {
            var openResult = OpenPort(printer, string.IsNullOrWhiteSpace(settings.PrinterPort)
                ? ReceiptPrinterSettings.DefaultPrinterPort
                : settings.PrinterPort.Trim());
            if (openResult != 0)
            {
                return new ReceiptPrinterDriverResult(false, T("receipt.printer.portOpenFailed", "Printer port could not be opened."));
            }

            opened = true;
            var initializeResult = GetSdkFailure(PrinterInitialize(printer), T("receipt.printer.initFailed", "Printer could not be initialized."));
            if (initializeResult is not null)
            {
                return initializeResult;
            }

            var statusResult = GetPrinterNotReadyResult(printer);
            if (statusResult is not null)
            {
                return statusResult;
            }

            // 通过打印机 DK 钱箱口发送脉冲，不打印小票。
            var drawerResult = GetSdkFailure(
                OpenCashDrawer(printer, CashDrawerPinMode, CashDrawerOnTime, CashDrawerOffTime),
                T("receipt.drawer.openFailed", "Cash drawer could not be opened."));
            if (drawerResult is not null)
            {
                return drawerResult;
            }

            return new ReceiptPrinterDriverResult(true, T("cashDrawer.opened", "Cash drawer opened."));
        }
        finally
        {
            if (opened)
            {
                ClosePort(printer);
            }

            ReleasePrinter(printer);
        }
    }

    private ReceiptPrinterDriverResult? GetSdkFailure(int result, string message)
    {
        return result == 0 ? null : new ReceiptPrinterDriverResult(false, $"{message} SDK result: {result}.");
    }

    private ReceiptPrinterDriverResult? GetPrinterNotReadyResult(IntPtr printer)
    {
        var status = 2;
        var result = GetPrinterState(printer, ref status);
        if (result != 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.statusReadFailed", "Printer status could not be read."));
        }

        if (status == 0x12)
        {
            return null;
        }

        if ((status & 0b100) > 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.coverOpen", "Printer cover is open."));
        }

        if ((status & 0b100000) > 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.outOfPaper", "Printer is out of paper."));
        }

        if ((status & 0b1000000) > 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.error", "Printer is reporting an error."));
        }

        return new ReceiptPrinterDriverResult(
            false,
            string.Format(
                localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
                T("receipt.printer.notReady", "Printer is not ready. Status: {0}."),
                status));
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern IntPtr InitPrinter(string model);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern int ReleasePrinter(IntPtr intPtr);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern int OpenPort(IntPtr intPtr, string port);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern int ClosePort(IntPtr intPtr);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrinterInitialize(IntPtr intPtr);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int SetTextLineSpace(IntPtr intPtr, int lineSpace);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int GetPrinterState(IntPtr intPtr, ref int printerStatus);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrintText(IntPtr intPtr, string data, int alignment, int textSize);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrintBarCode(IntPtr intPtr, int bcType, string bcData, int width, int height, int alignment, int hriPosition);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrintSymbol(IntPtr intPtr, int type, string data, int errLevel, int width, int height, int alignment);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int CutPaperWithDistance(IntPtr intPtr, int distance);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int OpenCashDrawer(IntPtr intPtr, int pinMode, int onTime, int offTime);
}
