using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>设备注册列表的版本归一化、比较、状态判断与汇总（纯函数，不依赖数据库）。</summary>
public sealed class DeviceVersionStateResolverTests
{
    [Theory]
    [InlineData("1.0.96", "1.0.96")]
    [InlineData("v1.0.96", "1.0.96")]
    [InlineData("1.0.96+abc123", "1.0.96")]
    [InlineData("1.0.96-beta", "1.0.96")]
    [InlineData("1.0.96.3", "1.0.96.3")]
    [InlineData(" 1.0.96 ", "1.0.96")]
    public void NormalizeVersion_忽略前缀和后缀并保留第四段(string raw, string expected)
    {
        Assert.Equal(expected, DeviceVersionStateResolver.NormalizeVersion(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.0")]
    public void NormalizeVersion_无法识别返回null(string? raw)
    {
        Assert.Null(DeviceVersionStateResolver.NormalizeVersion(raw));
    }

    [Fact]
    public void Compare_按数值比较而不是按字符串()
    {
        Assert.True(DeviceVersionStateResolver.Compare("1.0.10", "1.0.9") > 0);
        Assert.True(DeviceVersionStateResolver.Compare("1.0.95", "1.0.96") < 0);
        // 缺少第四段视为 0。
        Assert.Equal(0, DeviceVersionStateResolver.Compare("1.0.96", "1.0.96.0"));
        Assert.Null(DeviceVersionStateResolver.Compare("abc", "1.0.96"));
    }

    [Theory]
    [InlineData("POS", "Windows", true)]
    [InlineData("pos", "windows", true)]
    // 老记录设备系统为空，列表的 Windows 筛选也把它们算作 Windows。
    [InlineData("POS", "", true)]
    [InlineData("POS", null, true)]
    [InlineData("POS", "Android", false)]
    [InlineData("POS", "iPadOS", false)]
    [InlineData("Mobile", "Windows", false)]
    [InlineData("Admin", "Windows", false)]
    public void IsWpfPosDevice_只认Windows的POS(string? type, string? system, bool expected)
    {
        Assert.Equal(expected, DeviceVersionStateResolver.IsWpfPosDevice(type, system));
    }

    private static WpfPolicyTargetSnapshot StoresPolicy(string target = "1.0.96") =>
        new(target, "stores", storeCodes: ["BRI"]);

    [Fact]
    public void Resolve_非WindowsPOS不参与判断()
    {
        Assert.Null(DeviceVersionStateResolver.Resolve(StoresPolicy(), "Mobile", "Android", 1, "BRI", "1.0.1"));
        Assert.Null(DeviceVersionStateResolver.Resolve(StoresPolicy(), "POS", "Android", 1, "BRI", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void Resolve_没有可识别的版本_归为未上报_即使不在放量范围(string? version)
    {
        // 未上报优先于未放量：对排查「谁没报版本」更有用。
        Assert.Equal(
            DeviceVersionStateNames.Unreported,
            DeviceVersionStateResolver.Resolve(StoresPolicy(), "POS", "Windows", 1, "SYD", version)
        );
    }

    [Fact]
    public void Resolve_没有策略时只能区分未上报()
    {
        Assert.Null(DeviceVersionStateResolver.Resolve(null, "POS", "Windows", 1, "BRI", "1.0.95"));
        Assert.Equal(
            DeviceVersionStateNames.Unreported,
            DeviceVersionStateResolver.Resolve(null, "POS", "Windows", 1, "BRI", null)
        );
    }

    [Theory]
    [InlineData("1.0.96", DeviceVersionStateNames.Latest)]
    [InlineData("v1.0.96+sha", DeviceVersionStateNames.Latest)]
    [InlineData("1.0.95", DeviceVersionStateNames.Behind)]
    [InlineData("1.0.9", DeviceVersionStateNames.Behind)]
    [InlineData("1.0.97", DeviceVersionStateNames.Ahead)]
    public void Resolve_范围内的设备按版本比较(string version, string expected)
    {
        Assert.Equal(
            expected,
            DeviceVersionStateResolver.Resolve(StoresPolicy(), "POS", "Windows", 7, "BRI", version)
        );
    }

    [Fact]
    public void Resolve_分店范围_不在名单内是未放量_分店代码不分大小写()
    {
        var policy = StoresPolicy();
        Assert.Equal(
            DeviceVersionStateNames.NotTargeted,
            DeviceVersionStateResolver.Resolve(policy, "POS", "Windows", 7, "SYD", "1.0.95")
        );
        Assert.Equal(
            DeviceVersionStateNames.NotTargeted,
            DeviceVersionStateResolver.Resolve(policy, "POS", "Windows", 7, null, "1.0.95")
        );
        Assert.Equal(
            DeviceVersionStateNames.Behind,
            DeviceVersionStateResolver.Resolve(policy, "POS", "Windows", 7, " bri ", "1.0.95")
        );
    }

    [Fact]
    public void Resolve_设备范围按注册ID_全部范围人人在内()
    {
        var devices = new WpfPolicyTargetSnapshot("1.0.96", "devices", deviceIds: [7]);
        Assert.Equal(
            DeviceVersionStateNames.Behind,
            DeviceVersionStateResolver.Resolve(devices, "POS", "Windows", 7, "ANY", "1.0.95")
        );
        Assert.Equal(
            DeviceVersionStateNames.NotTargeted,
            DeviceVersionStateResolver.Resolve(devices, "POS", "Windows", 8, "ANY", "1.0.95")
        );

        var all = new WpfPolicyTargetSnapshot("1.0.96", "all");
        Assert.Equal(
            DeviceVersionStateNames.Behind,
            DeviceVersionStateResolver.Resolve(all, "POS", "Windows", 8, "ANY", "1.0.95")
        );
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("all", true, null)]
    [InlineData("BEHIND", true, "behind")]
    [InlineData("notTargeted", true, "notTargeted")]
    [InlineData("nonsense", false, null)]
    public void TryNormalizeState_空和all不筛选_未知取值被拒(string? raw, bool ok, string? expected)
    {
        Assert.Equal(ok, DeviceVersionStateResolver.TryNormalizeState(raw, out var state));
        Assert.Equal(expected, state);
    }

    [Fact]
    public void ParseVersionList_规范化去重并丢弃无法识别的片段()
    {
        var versions = DeviceVersionStateResolver.ParseVersionList("1.0.96, v1.0.96+x ,1.0.95,junk,,");
        Assert.Equal(new[] { "1.0.95", "1.0.96" }, versions.OrderBy(v => v));
        Assert.Empty(DeviceVersionStateResolver.ParseVersionList(null));
    }

    private static DeviceListItemDto Device(
        int id,
        string? version,
        string type = "POS",
        string system = "Windows",
        string? state = null
    ) =>
        new()
        {
            Id = id,
            AppVersion = version,
            DeviceType = type,
            DeviceSystem = system,
            VersionState = state,
        };

    [Fact]
    public void Matches_版本与状态同时给出时取交集()
    {
        var versions = new HashSet<string> { "1.0.95" };
        var device = Device(1, "1.0.95+x", state: DeviceVersionStateNames.Behind);

        Assert.True(DeviceVersionStateResolver.Matches(device, versions, null));
        Assert.True(DeviceVersionStateResolver.Matches(device, versions, DeviceVersionStateNames.Behind));
        Assert.False(DeviceVersionStateResolver.Matches(device, versions, DeviceVersionStateNames.Latest));
        Assert.False(DeviceVersionStateResolver.Matches(Device(2, "1.0.96"), versions, null));
        Assert.False(DeviceVersionStateResolver.Matches(Device(3, null), versions, null));
        Assert.True(DeviceVersionStateResolver.Matches(Device(3, null), new HashSet<string>(), null));
    }

    [Fact]
    public void BuildSummary_按版本从新到旧分组_未上报垫底_非WindowsPOS不进分组()
    {
        var policy = StoresPolicy();
        var devices = new List<DeviceListItemDto>
        {
            Device(1, "1.0.96"),
            Device(2, "1.0.95"),
            Device(3, "1.0.95+sha"),
            Device(4, "1.0.9"),
            Device(5, null),
            Device(6, null, type: "Mobile", system: "Android"),
        };
        foreach (var device in devices)
        {
            device.StoreCode = "BRI";
        }

        DeviceVersionStateResolver.Annotate(devices, policy);
        var summary = DeviceVersionStateResolver.BuildSummary(devices, policy);

        Assert.Equal("1.0.96", summary.TargetVersion);
        Assert.Equal("stores", summary.TargetScope);
        Assert.Equal(6, summary.Total);
        Assert.Equal(
            new (string?, int)[] { ("1.0.96", 1), ("1.0.95", 2), ("1.0.9", 1), (null, 1) },
            summary.Items.Select(item => (item.AppVersion, item.Total))
        );
        Assert.Equal(1, summary.States.Latest);
        Assert.Equal(3, summary.States.Behind);
        Assert.Equal(1, summary.States.Unreported);
        Assert.Equal(1, summary.States.NotApplicable);
        Assert.Equal(0, summary.States.NotTargeted);
    }

    [Fact]
    public void Annotate_只给Windows设备写目标版本()
    {
        var policy = StoresPolicy();
        var windows = Device(1, "1.0.95");
        var android = Device(2, "1.0.95", type: "Mobile", system: "Android");
        windows.StoreCode = "BRI";

        DeviceVersionStateResolver.Annotate([windows, android], policy);

        Assert.Equal("1.0.96", windows.TargetAppVersion);
        Assert.Equal(DeviceVersionStateNames.Behind, windows.VersionState);
        Assert.Null(android.TargetAppVersion);
        Assert.Null(android.VersionState);
    }
}
