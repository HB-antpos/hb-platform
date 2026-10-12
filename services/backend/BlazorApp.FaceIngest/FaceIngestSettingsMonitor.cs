using Microsoft.Extensions.Options;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 持有当前生效的已校验配置快照，并负责热更新（cameras.json 改动后无需重启容器）。
/// 业务口径：
/// - 启动时配置非法 → 构造即抛 <see cref="OptionsValidationException"/>，服务启动失败；
/// - 运行中热更新成非法配置 → 保留上一份合法快照继续服务，只记 Error 日志，接收端点不受影响；
///   cameras.json 改成坏 JSON（语法错误）时框架不会通知到这里，由 <see cref="CamerasFileLoadErrorRelay"/> 转来记 Error。
/// 不注册 IValidateOptions / ValidateOnStart：那样热更新到非法配置时 IOptionsMonitor 自己会抛异常，
/// 读配置的请求会 500，与「保留旧快照」的要求冲突。
/// </summary>
internal sealed class FaceIngestSettingsMonitor : IDisposable
{
    private readonly ILogger<FaceIngestSettingsMonitor> _logger;
    private readonly IDisposable? _changeSubscription;
    private readonly CamerasFileLoadErrorRelay _camerasFileLoadErrors;
    private FaceIngestSettings _current;

    public FaceIngestSettingsMonitor(
        IOptionsMonitor<FaceIngestOptions> options,
        CamerasFileLoadErrorRelay camerasFileLoadErrors,
        ILogger<FaceIngestSettingsMonitor> logger)
    {
        _logger = logger;
        _camerasFileLoadErrors = camerasFileLoadErrors;
        if (!FaceIngestSettings.TryCreate(options.CurrentValue, out var settings, out var errors))
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(FaceIngestOptions), errors);
        }

        _current = settings;
        LogLoaded("startup", settings);
        _changeSubscription = options.OnChange(OnOptionsChanged);
        // 中文注释：启动后才接上：启动阶段 cameras.json 坏掉应直接启动失败，不能只记日志。
        _camerasFileLoadErrors.Attach(OnCamerasFileLoadFailed);
    }

    /// <summary>当前生效的快照；读取无锁，请求内应只取一次。</summary>
    public FaceIngestSettings Current => Volatile.Read(ref _current);

    public void Dispose()
    {
        _camerasFileLoadErrors.Detach();
        _changeSubscription?.Dispose();
    }

    private void OnCamerasFileLoadFailed(Exception exception)
    {
        // 中文注释：异常里只有文件路径与解析位置（cameras.json 只存哈希，不含 token），可以整体记录。
        _logger.LogError(
            exception,
            "Face ingest cameras.json could not be loaded (invalid JSON?); change ignored, keeping previous valid snapshot. cameras={CameraCount}",
            Current.Cameras.Count);
    }

    private void OnOptionsChanged(FaceIngestOptions options, string? name)
    {
        if (name is not null && name != Options.DefaultName)
        {
            return;
        }

        // 中文注释：这里跑在配置文件监听线程上，任何异常都不能抛出去，否则可能影响后续热更新。
        try
        {
            if (FaceIngestSettings.TryCreate(options, out var settings, out var errors))
            {
                if (!string.Equals(settings.StorageRootPath, Current.StorageRootPath, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Face ingest StorageRootPath changed at runtime; the new path only takes effect after restart.");
                }

                Volatile.Write(ref _current, settings);
                LogLoaded("reload", settings);
                return;
            }

            _logger.LogError(
                "Face ingest configuration reload rejected; keeping previous valid snapshot. errors={ValidationErrors}",
                string.Join(" | ", errors));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Face ingest configuration reload failed; keeping previous valid snapshot.");
        }
    }

    private void LogLoaded(string trigger, FaceIngestSettings settings)
    {
        _logger.LogInformation(
            "Face ingest settings loaded trigger={Trigger} cameras={CameraCount} enabledCameras={EnabledCameraCount} captureEnabled={CaptureEnabled}",
            trigger,
            settings.Cameras.Count,
            settings.Cameras.Count(static camera => camera.Enabled),
            settings.CaptureEnabled);
    }
}
