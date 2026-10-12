namespace BlazorApp.FaceIngest;

/// <summary>
/// 把 cameras.json「运行中重新加载失败」（JSON 语法错误、重复键等）转给日志。
/// 业务口径：
/// - 配置源在 Program 里、DI 容器建好之前就注册，拿不到 ILogger；所以先挂这个中转对象，
///   等 <see cref="FaceIngestSettingsMonitor"/> 构造时再接上日志处理器；
/// - 启动阶段处理器还没接上，什么都不做，异常照常抛出，服务启动失败（与字段校验不通过一致）；
/// - 运行中只记 Error、不设 <c>Ignore</c>：FileConfigurationProvider 在异常时不触发重新绑定，
///   上一份合法快照继续服务。若设 <c>Ignore=true</c>，它会按「已清空的数据」触发重新绑定，
///   空摄像机清单是合法配置，所有摄像机会立刻变成 401。
///   已知限制：框架此时已清空该文件在内存里的数据，修好 cameras.json 之前若别的可热更新配置源
///   （容器里只有镜像内的 appsettings.json，正常不会变）触发重新绑定，会按空摄像机清单生效。
/// - 管不到的情况：文件存在但运行用户读不到（权限不足）。框架打开文件这一步不在 OnLoadException 的范围内：
///   启动时直接抛 UnauthorizedAccessException、服务启动失败；运行中换进这样的文件则被监听回调静默吞掉，
///   不重新加载也不经过这里，旧快照继续服务（runbook 4.3 因此要求替换前用 uid 1000 核对读权限）。
/// 每个应用实例一份（不用静态字段），避免同进程内多个测试主机互相串日志。
/// </summary>
internal sealed class CamerasFileLoadErrorRelay
{
    private Action<Exception>? _handler;

    /// <summary>接上日志处理器（由 <see cref="FaceIngestSettingsMonitor"/> 调用）。</summary>
    public void Attach(Action<Exception> handler) => Volatile.Write(ref _handler, handler);

    /// <summary>断开日志处理器（服务停止时）。</summary>
    public void Detach() => Volatile.Write(ref _handler, null);

    /// <summary>挂到 JsonConfigurationSource.OnLoadException 上的回调。</summary>
    public void OnLoadException(FileLoadExceptionContext context)
        => Volatile.Read(ref _handler)?.Invoke(context.Exception);
}
