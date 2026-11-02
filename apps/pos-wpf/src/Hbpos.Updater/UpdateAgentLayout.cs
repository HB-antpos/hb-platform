using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hbpos.Updater;

/// <summary>
/// 管理员更新代理的数据目录：{app}\UpdateAgent。{app} 在 Program Files 下，普通用户只能读，
/// 代理下载的安装包、状态和日志都放在这里，避免被本机其他程序替换或篡改。
/// </summary>
internal sealed record UpdateAgentLayout(string AppDirectory)
{
    public const string DirectoryName = "UpdateAgent";
    public const string UpdaterDirectoryName = "updater";
    public const string UpdaterExecutableName = "Hbpos.Updater.exe";

    public string Root => Path.Combine(AppDirectory, DirectoryName);

    public string PackagesDirectory => Path.Combine(Root, "packages");

    public string StatusDirectory => Path.Combine(Root, "status");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string RuntimeRootDirectory => Path.Combine(Root, "runtime");

    public string WorkerLockFile => Path.Combine(Root, "worker.lock");

    public string UpdaterDirectory => Path.Combine(AppDirectory, UpdaterDirectoryName);

    public string UpdaterExecutablePath => Path.Combine(UpdaterDirectory, UpdaterExecutableName);

    public string StatusFile(string requestId) => Path.Combine(StatusDirectory, requestId + ".json");

    public string ProgressFile(string requestId) => Path.Combine(StatusDirectory, requestId + ".progress");

    public string LogFile(string requestId) => Path.Combine(LogsDirectory, requestId + ".log");

    public string RuntimeDirectory(string requestId) => Path.Combine(RuntimeRootDirectory, requestId);

    public string PackageFile(string sha256) => Path.Combine(PackagesDirectory, sha256 + ".exe");

    public static UpdateAgentLayout ForAppExecutable(string appExePath)
    {
        return new UpdateAgentLayout(Path.GetDirectoryName(Path.GetFullPath(appExePath)) ?? string.Empty);
    }

    // 中文注释：任务计划从 {app}\updater 拉起代理；代理再从 {app}\UpdateAgent\runtime\<id> 以 worker 身份运行。
    // 两种位置都据此反推安装目录，不接受任何外部传入的目录，避免被引导到可写位置。
    public static bool TryFromAgentBaseDirectory(string baseDirectory, bool worker, out UpdateAgentLayout layout)
    {
        layout = null!;
        var current = new DirectoryInfo(Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar));
        DirectoryInfo? appDirectory;
        if (worker)
        {
            var runtimeRoot = current.Parent;
            var agentRoot = runtimeRoot?.Parent;
            if (runtimeRoot is null ||
                agentRoot is null ||
                !string.Equals(runtimeRoot.Name, "runtime", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(agentRoot.Name, DirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            appDirectory = agentRoot.Parent;
        }
        else
        {
            if (!string.Equals(current.Name, UpdaterDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            appDirectory = current.Parent;
        }

        if (appDirectory is null)
        {
            return false;
        }

        layout = new UpdateAgentLayout(appDirectory.FullName);
        return true;
    }

    // 中文注释：只有装在 Program Files 下时，{app} 才继承“普通用户只读”的权限；装在其他位置（例如 C:\ 根目录下的新建文件夹）
    // 普通用户可能有修改权限，就能替换任务计划要以 SYSTEM 运行的程序，所以这种安装不注册、也不运行代理。
    public bool IsUnderProgramFiles(string programFilesDirectory)
    {
        var programFiles = Path.GetFullPath(programFilesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var app = Path.GetFullPath(AppDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return app.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) &&
            app.Length > programFiles.Length;
    }
}

internal enum UpdateAgentState
{
    Accepted,
    Downloading,
    Installing,
    Succeeded,
    Failed
}

internal sealed record UpdateAgentStatus(
    [property: JsonPropertyName("id")] string RequestId,
    [property: JsonPropertyName("state")] UpdateAgentState State,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public bool IsFinal => State is UpdateAgentState.Succeeded or UpdateAgentState.Failed;

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static bool TryParse(string? json, string expectedRequestId, out UpdateAgentStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            status = JsonSerializer.Deserialize<UpdateAgentStatus>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        return status is not null &&
            string.Equals(status.RequestId, expectedRequestId, StringComparison.OrdinalIgnoreCase);
    }
}
