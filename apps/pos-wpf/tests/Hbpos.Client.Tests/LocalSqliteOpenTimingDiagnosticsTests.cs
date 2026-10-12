using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class LocalSqliteOpenTimingDiagnosticsTests
{
    private static (LocalSqliteOpenTimingDiagnostics Diagnostics, List<(string Message, bool IsWarning)> Lines, FakeTimeProvider Clock) Create()
    {
        var lines = new List<(string Message, bool IsWarning)>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        return (new LocalSqliteOpenTimingDiagnostics((message, isWarning) => lines.Add((message, isWarning)), clock), lines, clock);
    }

    [Fact]
    public void Fast_opens_stay_silent_until_the_summary_count_is_reached()
    {
        var (diagnostics, lines, _) = Create();

        for (var i = 0; i < 49; i++)
        {
            diagnostics.Record(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        }

        Assert.Empty(lines);

        diagnostics.Record(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));

        var summary = Assert.Single(lines);
        Assert.False(summary.IsWarning);
        Assert.Contains("local sqlite open summary count=50", summary.Message);
        Assert.Contains("slowOver50ms=0", summary.Message);
    }

    [Fact]
    public void Summary_is_emitted_after_the_time_window_even_with_few_opens()
    {
        var (diagnostics, lines, clock) = Create();

        diagnostics.Record(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(4));
        clock.Advance(TimeSpan.FromSeconds(61));
        diagnostics.Record(TimeSpan.FromMilliseconds(6), TimeSpan.FromMilliseconds(8));

        var summary = Assert.Single(lines);
        Assert.Contains("count=2", summary.Message);
        Assert.Contains("openAvgMs=4.00", summary.Message);
        Assert.Contains("openMaxMs=6.0", summary.Message);
        Assert.Contains("configureAvgMs=6.00", summary.Message);
        Assert.Contains("configureMaxMs=8.0", summary.Message);
    }

    [Fact]
    public void Slow_open_logs_a_warning_but_is_rate_limited()
    {
        var (diagnostics, lines, clock) = Create();

        diagnostics.Record(TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(5));
        diagnostics.Record(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5));

        var slow = Assert.Single(lines);
        Assert.True(slow.IsWarning);
        Assert.Contains("openMs=70.0", slow.Message);

        clock.Advance(TimeSpan.FromSeconds(11));
        diagnostics.Record(TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(5));

        Assert.Equal(2, lines.Count(line => line.IsWarning));
    }

    [Fact]
    public void Summary_counters_reset_after_each_summary()
    {
        var (diagnostics, lines, clock) = Create();

        diagnostics.Record(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        diagnostics.Record(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        lines.Clear();

        clock.Advance(TimeSpan.FromSeconds(61));
        diagnostics.Record(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2));

        var summary = Assert.Single(lines);
        Assert.Contains("count=1", summary.Message);
        Assert.Contains("openMaxMs=2.0", summary.Message);
        Assert.Contains("slowOver50ms=0", summary.Message);
    }

    [Fact]
    public void A_throwing_log_sink_never_reaches_the_caller()
    {
        var diagnostics = new LocalSqliteOpenTimingDiagnostics(
            (_, _) => throw new InvalidOperationException("sink down"),
            TimeProvider.System);

        var exception = Record.Exception(() =>
            diagnostics.Record(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)));

        Assert.Null(exception);
    }
}
