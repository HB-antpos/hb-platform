using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class MonitorIdentityTests
{
    [Fact]
    public void Format_and_parse_round_trip_including_negative_coordinates()
    {
        var monitor = new MonitorIdentity(-1280, -200, 1280, 1024);

        var parsed = MonitorIdentity.Parse(monitor.Format());

        Assert.Equal("-1280,-200,1280,1024", monitor.Format());
        Assert.Equal(monitor, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1920,0,1280")]
    [InlineData("1920,0,1280,800,1")]
    [InlineData("a,0,1280,800")]
    [InlineData("1920,0,0,800")]
    [InlineData("1920,0,1280,-800")]
    [InlineData("1920.5,0,1280,800")]
    public void Parse_rejects_malformed_values(string? value)
    {
        Assert.Null(MonitorIdentity.Parse(value));
    }
}
