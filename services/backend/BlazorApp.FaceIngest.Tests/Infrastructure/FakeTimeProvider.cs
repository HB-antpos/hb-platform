namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>
/// 手写的可控时钟（仓库不引用 Microsoft.Extensions.TimeProvider.Testing）。
/// UTC 时间与单调时间戳同源，便于同时控制落盘日期、清理截止时间与日志节流窗口；定时器沿用真实实现。
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _utcNow.UtcTicks;
        }
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            _utcNow = utcNow;
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _utcNow += delta;
        }
    }
}
