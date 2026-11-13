using BlazorApp.Shared.Constants;
using Hbpos.Api.Services;
using Hbpos.Contracts.PosSettings;

namespace Hbpos.Api.Tests;

/// <summary>
/// 收银端设置下发（设备侧）：三层合并后按端过滤、指纹未变只回「没变化」、回执只收认识的键。
/// 合并与指纹是后台与 POS API 共用的 PosSettingMerger，这里验证 POS API 如何使用它。
/// </summary>
public sealed class PosSettingsSyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 1, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, bool> Layer(params (string Key, bool Value)[] items) =>
        items.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    [Fact]
    public async Task GetSync_首次同步返回完整设置_现金进出缺省关_来源标为default()
    {
        var service = CreateService(new FakeRepository());

        var result = await service.GetSyncAsync("S001", "ORI-01", "Windows", knownHash: null, CancellationToken.None);

        var sync = Assert.IsType<PosSettingsSyncDto>(result.Sync);
        Assert.True(sync.Changed);
        var cash = sync.Settings![PosSettingKeys.CashMovementEnabled];
        Assert.False(cash.Value);
        Assert.Equal(PosSettingSources.Default, cash.Source);
        // 没有任何层设置的其它项不下发：收银机沿用本机值。
        Assert.Equal([PosSettingKeys.CashMovementEnabled], sync.Settings.Keys);
    }

    [Fact]
    public async Task GetSync_设备优先于门店优先于全局_并带来源()
    {
        var repository = new FakeRepository
        {
            Layers = new PosSettingLayers(
                Layer((PosSettingKeys.ManualCard, false), (PosSettingKeys.Voucher, false), (PosSettingKeys.OrderList, true)),
                Layer((PosSettingKeys.ManualCard, true), (PosSettingKeys.Voucher, true)),
                Layer((PosSettingKeys.Voucher, false)))
        };
        var service = CreateService(repository);

        var sync = (await service.GetSyncAsync("S001", "ORI-01", "Windows", null, CancellationToken.None)).Sync!;

        Assert.Equal(new PosSettingValueDto(true, PosSettingScopes.Store), sync.Settings![PosSettingKeys.ManualCard]);
        Assert.Equal(new PosSettingValueDto(false, PosSettingScopes.Device), sync.Settings[PosSettingKeys.Voucher]);
        Assert.Equal(new PosSettingValueDto(true, PosSettingScopes.Global), sync.Settings[PosSettingKeys.OrderList]);
        // 读取层时用的就是认证声明里的门店与设备。
        Assert.Equal(("S001", "ORI-01"), repository.LastScope);
    }

    [Fact]
    public async Task GetSync_指纹没变只回没变化_指纹过期则回完整设置()
    {
        var repository = new FakeRepository { Layers = new PosSettingLayers(Layer((PosSettingKeys.ManualCard, true)), Layer(), Layer()) };
        var service = CreateService(repository);
        var first = (await service.GetSyncAsync("S001", "ORI-01", "Windows", null, CancellationToken.None)).Sync!;

        var unchanged = (await service.GetSyncAsync("S001", "ORI-01", "Windows", first.Hash, CancellationToken.None)).Sync!;
        Assert.False(unchanged.Changed);
        Assert.Null(unchanged.Settings);
        Assert.Equal(first.Hash, unchanged.Hash);

        repository.Layers = new PosSettingLayers(Layer((PosSettingKeys.ManualCard, false)), Layer(), Layer());
        var changed = (await service.GetSyncAsync("S001", "ORI-01", "Windows", first.Hash, CancellationToken.None)).Sync!;
        Assert.True(changed.Changed);
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.False(changed.Settings![PosSettingKeys.ManualCard].Value);
    }

    [Fact]
    public async Task GetSync_按设备系统过滤_WPF专属项不下发给iPad_且不影响iPad指纹()
    {
        var repository = new FakeRepository { Layers = new PosSettingLayers(Layer((PosSettingKeys.ManualCard, true)), Layer(), Layer()) };
        var service = CreateService(repository);
        var ipadBefore = (await service.GetSyncAsync("S001", "IPAD-02", "iPadOS", null, CancellationToken.None)).Sync!;

        repository.Layers = new PosSettingLayers(
            Layer((PosSettingKeys.ManualCard, true), (PosSettingKeys.ReturnsRescan, true)), Layer(), Layer());
        var ipadAfter = (await service.GetSyncAsync("S001", "IPAD-02", "iPadOS", ipadBefore.Hash, CancellationToken.None)).Sync!;
        var wpf = (await service.GetSyncAsync("S001", "ORI-01", "Windows", null, CancellationToken.None)).Sync!;

        Assert.False(ipadAfter.Changed);
        Assert.DoesNotContain(PosSettingKeys.ReturnsRescan, ipadBefore.Settings!.Keys);
        Assert.Contains(PosSettingKeys.ReturnsRescan, wpf.Settings!.Keys);
    }

    [Fact]
    public async Task GetSync_缺少门店或设备声明时拒绝()
    {
        var service = CreateService(new FakeRepository());

        var noStore = await service.GetSyncAsync(" ", "ORI-01", "Windows", null, CancellationToken.None);
        var noDevice = await service.GetSyncAsync("S001", "", "Windows", null, CancellationToken.None);

        Assert.Equal(PosSettingsSyncService.StoreCodeRequiredCode, noStore.ErrorCode);
        Assert.Equal(PosSettingsSyncService.DeviceCodeRequiredCode, noDevice.ErrorCode);
    }

    [Fact]
    public async Task Ack_只收认识的键_离线次数夹在范围内_端类型取自认证声明()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var hash = new string('a', 64);

        var result = await service.AckAsync(
            "S001",
            "ORI-01",
            "Windows",
            new PosSettingsAckRequest(
                hash,
                new Dictionary<string, bool> { [PosSettingKeys.ManualCard] = true, ["evil.key"] = true },
                new string('v', 100),
                OfflineOverrideCount: -5),
            CancellationToken.None);

        Assert.Equal(hash, result.Result!.AppliedHash);
        var write = Assert.Single(repository.Acks);
        Assert.Equal("ORI-01", write.DeviceCode);
        Assert.Equal("S001", write.StoreCode);
        Assert.Equal("wpf", write.ClientKind);
        Assert.Equal(64, write.AppVersion!.Length);
        Assert.Equal(0, write.OfflineOverrideCount);
        Assert.Equal("{\"payment.manualCard\":true}", write.EffectiveJson);
        Assert.Equal(Now.UtcDateTime, write.NowUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public async Task Ack_指纹格式不对就拒绝_不写库(string hash)
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        var result = await service.AckAsync("S001", "ORI-01", "Windows", new PosSettingsAckRequest(hash, null, null), CancellationToken.None);

        Assert.Equal(PosSettingsSyncService.HashInvalidCode, result.ErrorCode);
        Assert.Empty(repository.Acks);
    }

    [Fact]
    public void Sql_读写前都先判断表是否存在_部署顺序出错不报500()
    {
        Assert.Contains("IF OBJECT_ID(N'[dbo].[PosSettingValue]', N'U') IS NOT NULL", SqlSugarPosSettingsRepository.LayersSql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1 = 0", SqlSugarPosSettingsRepository.LayersSql, StringComparison.Ordinal);
        // 层查询只取全局 + 本店 + 本设备，不会读到别的门店 / 设备的值。
        Assert.Contains("[ScopeKey] = @StoreCode", SqlSugarPosSettingsRepository.LayersSql, StringComparison.Ordinal);
        Assert.Contains("[ScopeKey] = @DeviceCode", SqlSugarPosSettingsRepository.LayersSql, StringComparison.Ordinal);
    }

    private static PosSettingsSyncService CreateService(FakeRepository repository) => new(repository, new FixedTime(Now));

    private sealed class FakeRepository : IPosSettingsRepository
    {
        public PosSettingLayers Layers { get; set; } = new(Layer(), Layer(), Layer());
        public (string Store, string Device)? LastScope { get; private set; }
        public List<PosSettingsAckWrite> Acks { get; } = [];

        public Task<PosSettingLayers> GetLayersAsync(string storeCode, string deviceCode, CancellationToken cancellationToken)
        {
            LastScope = (storeCode, deviceCode);
            return Task.FromResult(Layers);
        }

        public Task UpsertAckAsync(PosSettingsAckWrite write, CancellationToken cancellationToken)
        {
            Acks.Add(write);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
