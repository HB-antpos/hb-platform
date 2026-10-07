using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 截获 ConsoleLog 投递到中心日志的条目。ConsoleLog 是全局静态状态，使用它的测试类必须加入
/// <see cref="ConsoleLogGlobalStateTestCollection"/>，并在 finally 里把 sink 复位为 null。
/// </summary>
internal sealed class RecordingApplicationLogSink : IApplicationLogSink
{
    private readonly List<ApplicationLogEntry> entries = [];

    public IReadOnlyList<ApplicationLogEntry> Entries
    {
        get
        {
            lock (entries)
            {
                return entries.ToArray();
            }
        }
    }

    public void Enqueue(ApplicationLogEntry entry)
    {
        lock (entries)
        {
            entries.Add(entry);
        }
    }
}
