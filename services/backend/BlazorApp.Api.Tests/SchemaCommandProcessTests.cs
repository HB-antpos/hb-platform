using System.Diagnostics;
using BlazorApp.Api.Data.SchemaMigrations;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SchemaCommandProcessTests
{
    [Theory]
    [InlineData("--schema=check", SchemaDiagnosticCodes.DatabaseFailure)]
    [InlineData("--schema=migrate", SchemaDiagnosticCodes.MigrationFailure)]
    public async Task 显式Schema命令_数据库不可用时退出22且不启动HTTP(
        string argument,
        string expectedDiagnosticCode)
    {
        var result = await RunApiToExitAsync([argument], includeInvalidDatabaseConfiguration: true);

        Assert.Equal(22, result.ExitCode);
        // 诊断码经异步控制台日志写出，断言它可以防止 host 未释放导致日志丢失的回归。
        Assert.Contains(expectedDiagnosticCode, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening on:", result.CombinedOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--schema=sales-detail-projection-backfill")]
    [InlineData("--schema=sales-detail-projection-check")]
    public async Task 销售明细投影命令_缺少安全配置时退出2且不启动HTTP(string argument)
    {
        var result = await RunApiToExitAsync([argument], includeInvalidDatabaseConfiguration: false);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(
            SchemaDiagnosticCodes.SalesDetailProjectionConfigurationInvalid,
            result.CombinedOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening on:", result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 普通启动_数据库不可用时退出22且不开始监听()
    {
        var result = await RunApiToExitAsync([], includeInvalidDatabaseConfiguration: true);

        Assert.Equal(22, result.ExitCode);
        Assert.Contains(
            SchemaDiagnosticCodes.DatabaseFailure,
            result.CombinedOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening on:", result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema命令大小写错误_构建Host前退出2()
    {
        var result = await RunApiToExitAsync(
            ["--schema=CHECK"],
            includeInvalidDatabaseConfiguration: false
        );

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("SCHEMA_COMMAND_INVALID", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening on:", result.CombinedOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunApiToExitAsync(
        IReadOnlyList<string> arguments,
        bool includeInvalidDatabaseConfiguration
    )
    {
        var apiAssemblyPath = Path.Combine(AppContext.BaseDirectory, "BlazorApp.Api.dll");
        Assert.True(File.Exists(apiAssemblyPath), $"缺少 API 构建产物: {apiAssemblyPath}");

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"hb-schema-process-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory = AppContext.BaseDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            KeepOnlyRequiredProcessEnvironment(process.StartInfo);
            process.StartInfo.ArgumentList.Add(apiAssemblyPath);
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            process.StartInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            process.StartInfo.Environment["Database__InitializeOnStartup"] = "false";
            process.StartInfo.Environment["Database__CommandTimeoutSeconds"] = "1";
            process.StartInfo.Environment["Cache__EnableStoreOrderWarmUp"] = "false";
            process.StartInfo.Environment["ApplicationLogging__Enabled"] = "false";
            process.StartInfo.Environment["PerformanceMetrics__Enabled"] = "false";
            process.StartInfo.Environment["PerformanceMetrics__SentryReleaseHealth__Enabled"] =
                "false";
            process.StartInfo.Environment["ScheduledTasks__Enabled"] = "false";
            process.StartInfo.Environment["DataProtection__KeysPath"] =
                Path.Combine(temporaryRoot, "data-protection");
            process.StartInfo.Environment["AttendanceQrDataProtection__KeysPath"] =
                Path.Combine(temporaryRoot, "attendance-qr");
            process.StartInfo.Environment["Jwt__Key"] =
                "SchemaProcessTestsOnly-Key-With-At-Least-32-Bytes";
            process.StartInfo.Environment["Jwt__Issuer"] = "SchemaProcessTests";
            process.StartInfo.Environment["Jwt__Audience"] = "SchemaProcessTests";
            if (includeInvalidDatabaseConfiguration)
            {
                const string invalidConnection =
                    "Server=127.0.0.1,1;Database=SchemaProcessTests;User Id=sa;Password=SchemaProcessTestsOnly;Encrypt=False;TrustServerCertificate=True;Connect Timeout=1";
                process.StartInfo.Environment["ConnectionStrings__DefaultConnection"] =
                    invalidConnection;
                process.StartInfo.Environment["ConnectionStrings__HBPOSMConnection"] =
                    invalidConnection;
            }

            Assert.True(process.Start());
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                throw new TimeoutException("API schema 进程未在 30 秒内退出。");
            }

            return new ProcessResult(
                process.ExitCode,
                $"{await standardOutput}\n{await standardError}"
            );
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void KeepOnlyRequiredProcessEnvironment(ProcessStartInfo startInfo)
    {
        var requiredEnvironment = new[]
        {
            "PATH",
            "DOTNET_ROOT",
            "DOTNET_ROOT_X64",
            "TMPDIR",
            "LANG",
            "LC_ALL",
        }
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        // 关键位置：真实进程测试不得继承开发机的连接串、云凭据或遥测配置。
        startInfo.Environment.Clear();
        foreach (var item in requiredEnvironment)
        {
            startInfo.Environment[item.Name] = item.Value!;
        }
    }

    private sealed record ProcessResult(int ExitCode, string CombinedOutput);
}
