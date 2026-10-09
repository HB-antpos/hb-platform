using Hbpos.Api.Logging;
using Microsoft.Extensions.Logging;

namespace Hbpos.Api.Tests;

// M28：hbpos-api.log 不轮转，生产上长到约 600 MB，磁盘写满后写入被静默吞掉。
public sealed class HbposFileLoggerRotationTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "hbpos-file-logger-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Log_file_rolls_over_when_it_reaches_the_size_limit()
    {
        var path = Path.Combine(directory, "hbpos-api.log");
        using var provider = new HbposFileLoggerProvider(path, LogLevel.Information, maxFileBytes: 2_000, retainedFiles: 3);
        var logger = provider.CreateLogger("test");

        for (var index = 0; index < 60; index++)
        {
            logger.LogInformation("line {Index} {Padding}", index, new string('x', 100));
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        // 单个文件不会无限变大（允许最后一条越过上限一行）。
        Assert.All(Directory.GetFiles(directory), file => Assert.True(new FileInfo(file).Length < 2_000 + 400, file));
        // 最新的行在当前文件里，没有丢。
        Assert.Contains("line 59 ", File.ReadAllText(path));
    }

    [Fact]
    public void Rotation_keeps_only_the_configured_number_of_old_files()
    {
        var path = Path.Combine(directory, "hbpos-api.log");
        using var provider = new HbposFileLoggerProvider(path, LogLevel.Information, maxFileBytes: 500, retainedFiles: 2);
        var logger = provider.CreateLogger("test");

        for (var index = 0; index < 200; index++)
        {
            logger.LogInformation("line {Index} {Padding}", index, new string('x', 100));
        }

        var names = Directory.GetFiles(directory).Select(file => Path.GetFileName(file)!).OrderBy(name => name).ToArray();
        Assert.Equal(["hbpos-api.log", "hbpos-api.log.1", "hbpos-api.log.2"], names);
        Assert.False(File.Exists(path + ".3"));
    }

    [Fact]
    public void Existing_oversized_log_is_rolled_on_the_first_write_after_deploy()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hbpos-api.log");
        File.WriteAllText(path, new string('o', 5_000));
        using var provider = new HbposFileLoggerProvider(path, LogLevel.Information, maxFileBytes: 1_000, retainedFiles: 2);

        provider.CreateLogger("test").LogInformation("after deploy");

        Assert.Equal(5_000, new FileInfo(path + ".1").Length);
        Assert.Contains("after deploy", File.ReadAllText(path));
    }
}
