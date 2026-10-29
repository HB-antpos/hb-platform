using System.IO;
using System.Runtime.InteropServices;

namespace Hbpos.Updater;

internal interface IUpdateAgentTaskScheduler
{
    /// <summary>返回已注册代理任务要运行的程序路径；未注册或当前用户无权读取时返回 null。</summary>
    string? GetRegisteredAgentPath();

    /// <summary>以当前用户身份触发代理任务，把编码后的安装请求作为唯一参数传过去。</summary>
    bool TryRun(string encodedRequest, out string error);

    void Register(string agentExecutablePath);

    void Unregister();
}

/// <summary>
/// 通过 Windows 任务计划（COM）管理“HB POS Update Agent”：安装包以管理员身份注册，任务以 SYSTEM 运行、动作路径固定；
/// 普通用户只有读取和运行权限，运行时只能传入安装请求，由代理自己校验后下载安装。
/// </summary>
internal sealed class WindowsUpdateAgentTaskScheduler : IUpdateAgentTaskScheduler
{
    public const string TaskName = "HB POS Update Agent";
    public const string AgentArguments = "--agent $(Arg0)";

    // 中文注释：SYSTEM、Administrators 完全控制；已登录用户（AU）只能读取和运行，不能修改动作或删除任务。
    internal const string TaskSecurityDescriptor = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)";

    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonServiceAccount = 5;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;

    public string? GetRegisteredAgentPath()
    {
        try
        {
            dynamic task = GetRootFolder().GetTask(TaskName);
            if (!(bool)task.Enabled)
            {
                return null;
            }

            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count < 1)
            {
                return null;
            }

            dynamic action = actions.Item(1);
            return (int)action.Type == TaskActionExec ? (string)action.Path : null;
        }
        catch (Exception ex) when (IsTaskSchedulerFailure(ex))
        {
            return null;
        }
    }

    public bool TryRun(string encodedRequest, out string error)
    {
        try
        {
            dynamic task = GetRootFolder().GetTask(TaskName);
            task.Run(encodedRequest);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsTaskSchedulerFailure(ex))
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public void Register(string agentExecutablePath)
    {
        dynamic service = ConnectService();
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Author = "Hot Bargain";
        definition.RegistrationInfo.Description = "HB POS 更新代理：以系统权限静默安装已校验的 HB POS 安装包，避免收银机弹出 UAC 确认框。";
        definition.Principal.UserId = "S-1-5-18";
        definition.Principal.LogonType = TaskLogonServiceAccount;
        definition.Principal.RunLevel = TaskRunLevelHighest;
        definition.Settings.Enabled = true;
        definition.Settings.Hidden = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = false;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
        // 中文注释：任务进程校验请求后把 worker 拉起到数据目录就退出，但 worker 是任务的子进程，任务在它退出前一直算“运行中”；
        // 执行时限到点会把整棵进程树一起终止。下载最长 15 分钟 + 安装最长 30 分钟，时限必须留足余量，不能在安装中途杀掉安装器。
        definition.Settings.ExecutionTimeLimit = "PT2H";

        dynamic action = definition.Actions.Create(TaskActionExec);
        action.Path = agentExecutablePath;
        action.Arguments = AgentArguments;
        action.WorkingDirectory = Path.GetDirectoryName(agentExecutablePath) ?? string.Empty;

        GetRootFolder(service).RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreateOrUpdate,
            "SYSTEM",
            null,
            TaskLogonServiceAccount,
            TaskSecurityDescriptor);
    }

    public void Unregister()
    {
        try
        {
            GetRootFolder().DeleteTask(TaskName, 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 中文注释：任务本来就不存在时视为已卸载。
        }
    }

    private static dynamic ConnectService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(serviceType)!;
        service.Connect();
        return service;
    }

    private static dynamic GetRootFolder(dynamic? service = null)
    {
        return (service ?? ConnectService()).GetFolder("\\");
    }

    private static bool IsTaskSchedulerFailure(Exception ex)
    {
        return ex is COMException
            or UnauthorizedAccessException
            or FileNotFoundException
            or DirectoryNotFoundException
            or InvalidCastException
            or ArgumentException
            or NotSupportedException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
    }
}
