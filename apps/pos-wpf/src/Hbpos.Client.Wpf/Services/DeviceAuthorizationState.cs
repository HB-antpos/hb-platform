namespace Hbpos.Client.Wpf.Services;

public sealed class DeviceAuthorizationState
{
    private readonly object _gate = new();
    private DeviceAuthorizationContext? _current;

    /// <summary>
    /// 设备授权写入内存后触发（在锁外、调用 <see cref="Set"/> 的线程上同步执行）。
    /// 启动时设备授权要等服务端校验完才写入，依赖设备授权的后台任务可据此立即重试，而不是等下一轮轮询。
    /// </summary>
    public event EventHandler? Authorized;

    public DeviceAuthorizationContext? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Set(DeviceAuthorizationContext context)
    {
        lock (_gate)
        {
            _current = context;
        }

        // 关键逻辑：在锁外通知，订阅方读取 Current 不会与这里的锁互相等待。
        Authorized?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }
}

public sealed record DeviceAuthorizationContext(
    string DeviceCode,
    string StoreCode,
    string HardwareId,
    string AuthorizationCode);
