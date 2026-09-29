using System.Net;
using System.Net.Http.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.AppUpdates;
using Hbpos.Contracts.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

// 会修改进程级 HBPOS_APP_UPDATE_CENTER_BASE_URL / HBPOS_APP_UPDATE_CHECK_KEY；options 未配置时
// LocalAppUpdateService 回退读取它们，并行的其他更新测试会读到被改写的值。
[Collection(EnvironmentVariableTestCollection.Name)]
public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_without_center_base_url_returns_check_failed()
    {
        using var _ = TemporaryEnvironmentVariable(
            "HBPOS_APP_UPDATE_CENTER_BASE_URL",
            null);

        var service = new LocalAppUpdateService(
            new HttpClient(new CapturingHandler(_ => throw new InvalidOperationException("should not call center"))),
            Options.Create(new AppUpdateOptions()),
            NullLogger<LocalAppUpdateService>.Instance);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "APP_UPDATE_CENTER_NOT_CONFIGURED");
    }

    [Fact]
    public async Task CheckAsync_forwards_current_version_and_channel_to_center()
    {
        Uri? requestedUri = null;
        var centerResponse = new AppUpdateCheckResponse
        {
            UpdateAvailable = true,
            ForceUpdate = true,
            IsRollback = false,
            CurrentVersion = "1.0.0",
            TargetVersion = "1.1.0",
            MinimumSupportedVersion = "1.0.0",
            DownloadUrl = "https://downloads.example/hbpos-1.1.0.exe",
            FileName = "hbpos-1.1.0.exe",
            FileSize = 1024,
            Sha256 = new string('a', 64),
            InstallerType = "exe",
            InstallerArguments = "/quiet",
            ReleaseNotes = "安全更新"
        };
        var httpClient = new HttpClient(new CapturingHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(centerResponse)
            };
        }));
        var service = new LocalAppUpdateService(
            httpClient,
            Options.Create(new AppUpdateOptions { CenterBaseUrl = "https://center.example/base/" }),
            NullLogger<LocalAppUpdateService>.Instance);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.Equal("https://center.example/base/api/wpf-app-releases/check?channel=production&currentVersion=1.0.0", requestedUri?.ToString());
        Assert.True(result.UpdateAvailable);
        Assert.True(result.ForceUpdate);
        Assert.Equal("1.1.0", result.TargetVersion);
    }

    [Fact]
    public async Task CheckAsync_authenticated_device_forwards_identity_as_headers_without_disclosing_it_to_url_or_logs()
    {
        Uri? requestedUri = null;
        string? deviceId = null;
        string? authorizationCode = null;
        string? sharedUpdateKey = null;
        var logger = new ListLogger<LocalAppUpdateService>();
        var service = new LocalAppUpdateService(
            new HttpClient(new CapturingHandler(request =>
            {
                requestedUri = request.RequestUri;
                deviceId = request.Headers.TryGetValues("X-Device-Id", out var deviceValues)
                    ? deviceValues.SingleOrDefault()
                    : null;
                authorizationCode = request.Headers.TryGetValues("X-Auth-Code", out var authorizationValues)
                    ? authorizationValues.SingleOrDefault()
                    : null;
                sharedUpdateKey = request.Headers.TryGetValues(AppUpdateOptions.CenterApiKeyHeaderName, out var keyValues)
                    ? keyValues.SingleOrDefault()
                    : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(AppUpdateCheckResponse.NoUpdate("1.0.0"))
                };
            })),
            Options.Create(new AppUpdateOptions
            {
                CenterBaseUrl = "https://center.example/base/",
                CheckApiKey = "shared-update-key"
            }),
            logger);

        await service.CheckAsync(
            new AppUpdateCheckRequest { CurrentVersion = "1.0.0", Channel = "production" },
            new AppUpdateDeviceIdentity("HW-001", "device-auth-secret"));

        Assert.Equal("HW-001", deviceId);
        Assert.Equal("device-auth-secret", authorizationCode);
        Assert.Equal("shared-update-key", sharedUpdateKey);
        Assert.Equal(
            "https://center.example/base/api/wpf-app-releases/check?channel=production&currentVersion=1.0.0",
            requestedUri?.ToString());
        Assert.DoesNotContain("device-auth-secret", requestedUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("HW-001", requestedUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("device-auth-secret", StringComparison.Ordinal) ||
            entry.Message.Contains("HW-001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_adds_check_api_key_header_when_only_check_api_key_is_configured()
    {
        string? providedKey = null;
        var httpClient = new HttpClient(new CapturingHandler(request =>
        {
            providedKey = request.Headers.TryGetValues(AppUpdateOptions.CenterApiKeyHeaderName, out var values)
                ? values.SingleOrDefault()
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(AppUpdateCheckResponse.NoUpdate("1.0.0"))
            };
        }));
        var service = new LocalAppUpdateService(
            httpClient,
            Options.Create(new AppUpdateOptions
            {
                CenterBaseUrl = "https://center.example/base/",
                CheckApiKey = " terminal-secret "
            }),
            NullLogger<LocalAppUpdateService>.Instance);

        await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.Equal("terminal-secret", providedKey);
    }

    [Fact]
    public async Task CheckAsync_adds_center_api_key_header_when_only_legacy_center_api_key_is_configured()
    {
        string? providedKey = null;
        var httpClient = new HttpClient(new CapturingHandler(request =>
        {
            providedKey = request.Headers.TryGetValues(AppUpdateOptions.CenterApiKeyHeaderName, out var values)
                ? values.SingleOrDefault()
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(AppUpdateCheckResponse.NoUpdate("1.0.0"))
            };
        }));
        var service = new LocalAppUpdateService(
            httpClient,
            Options.Create(new AppUpdateOptions
            {
                CenterBaseUrl = "https://center.example/base/",
                CenterApiKey = " legacy-secret "
            }),
            NullLogger<LocalAppUpdateService>.Instance);

        await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.Equal("legacy-secret", providedKey);
    }

    [Fact]
    public async Task CheckAsync_prefers_check_api_key_when_both_keys_are_configured()
    {
        string? providedKey = null;
        var httpClient = new HttpClient(new CapturingHandler(request =>
        {
            providedKey = request.Headers.TryGetValues(AppUpdateOptions.CenterApiKeyHeaderName, out var values)
                ? values.SingleOrDefault()
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(AppUpdateCheckResponse.NoUpdate("1.0.0"))
            };
        }));
        var service = new LocalAppUpdateService(
            httpClient,
            Options.Create(new AppUpdateOptions
            {
                CenterBaseUrl = "https://center.example/base/",
                CheckApiKey = " preferred-secret ",
                CenterApiKey = " legacy-secret "
            }),
            NullLogger<LocalAppUpdateService>.Instance);

        await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.Equal("preferred-secret", providedKey);
    }

    [Fact]
    public async Task CheckAsync_adds_check_api_key_header_from_environment_when_options_do_not_provide_one()
    {
        using var _ = TemporaryEnvironmentVariable(
            "HBPOS_APP_UPDATE_CHECK_KEY",
            " env-secret ");

        string? providedKey = null;
        var httpClient = new HttpClient(new CapturingHandler(request =>
        {
            providedKey = request.Headers.TryGetValues(AppUpdateOptions.CenterApiKeyHeaderName, out var values)
                ? values.SingleOrDefault()
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(AppUpdateCheckResponse.NoUpdate("1.0.0"))
            };
        }));
        var service = new LocalAppUpdateService(
            httpClient,
            Options.Create(new AppUpdateOptions
            {
                CenterBaseUrl = "https://center.example/base/"
            }),
            NullLogger<LocalAppUpdateService>.Instance);

        await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.Equal("env-secret", providedKey);
    }

    [Theory]
    [InlineData("http://center.example/base/")]
    [InlineData("ftp://center.example/base/")]
    [InlineData("/center/base/")]
    [InlineData("not a url")]
    public async Task CheckAsync_invalid_configured_center_base_url_returns_check_failed(string centerBaseUrl)
    {
        var service = new LocalAppUpdateService(
            new HttpClient(new CapturingHandler(_ => throw new InvalidOperationException("should not call center"))),
            Options.Create(new AppUpdateOptions { CenterBaseUrl = centerBaseUrl }),
            NullLogger<LocalAppUpdateService>.Instance);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "APP_UPDATE_CENTER_INVALID_CONFIGURATION");
    }

    [Fact]
    public async Task CheckAsync_invalid_environment_center_base_url_returns_check_failed()
    {
        using var _ = TemporaryEnvironmentVariable(
            "HBPOS_APP_UPDATE_CENTER_BASE_URL",
            "http://center.example/base/");

        var service = new LocalAppUpdateService(
            new HttpClient(new CapturingHandler(_ => throw new InvalidOperationException("should not call center"))),
            Options.Create(new AppUpdateOptions()),
            NullLogger<LocalAppUpdateService>.Instance);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "APP_UPDATE_CENTER_INVALID_CONFIGURATION");
    }

    [Theory]
    [InlineData("http://localhost:5000/base/")]
    [InlineData("http://127.0.0.1:5000/base/")]
    [InlineData("http://[::1]:5000/base/")]
    public async Task CheckAsync_http_center_base_url_for_loopback_still_calls_center(string centerBaseUrl)
    {
        Uri? requestedUri = null;
        var httpClient = new HttpClient(new CapturingHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(AppUpdateCheckResponse.NoUpdate("1.0.0"))
            };
        }));
        var service = new LocalAppUpdateService(
            httpClient,
            Options.Create(new AppUpdateOptions { CenterBaseUrl = centerBaseUrl }),
            NullLogger<LocalAppUpdateService>.Instance);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.NotNull(requestedUri);
        Assert.Equal($"{centerBaseUrl}api/wpf-app-releases/check?channel=production&currentVersion=1.0.0", requestedUri!.ToString());
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_center_returns_no_update_contract_allows_missing_package_fields()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new AppUpdateCheckResponse
            {
                UpdateAvailable = false,
                CurrentVersion = "1.0.0"
            })
        });

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.False(result.UpdateAvailable);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.TargetVersion);
        Assert.Null(result.DownloadUrl);
        Assert.Null(result.Sha256);
        Assert.Null(result.InstallerType);
    }

    [Fact]
    public async Task CheckAsync_wrapped_center_error_returns_check_failed_contract()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                ApiResult<AppUpdateCheckResponse>.Fail(
                    "TARGET_RELEASE_NOT_FOUND",
                    "Target release is disabled."))
        });

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.True(result.CheckFailed);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("TARGET_RELEASE_NOT_FOUND", result.ErrorCode);
        Assert.Equal("Target release is disabled.", result.ErrorMessage);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.TargetVersion);
    }

    [Fact]
    public async Task CheckAsync_center_http_unauthorized_with_api_response_body_returns_backend_error_code()
    {
        const string backendBody = """
            {
              "success": false,
              "code": "APP_UPDATE_CHECK_UNAUTHORIZED",
              "message": "WPF update check is not authorized."
            }
            """;
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(backendBody)
        });

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.True(result.CheckFailed);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("APP_UPDATE_CHECK_UNAUTHORIZED", result.ErrorCode);
        Assert.Equal("WPF update check is not authorized.", result.ErrorMessage);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.TargetVersion);
    }

    [Fact]
    public async Task CheckAsync_center_http_failure_returns_check_failed_contract()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.True(result.CheckFailed);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("APP_UPDATE_CENTER_HTTP_ERROR", result.ErrorCode);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.TargetVersion);
    }

    [Fact]
    public async Task CheckAsync_center_http_failure_with_malformed_body_falls_back_to_generic_error()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{ not-json")
        });

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.True(result.CheckFailed);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("APP_UPDATE_CENTER_HTTP_ERROR", result.ErrorCode);
        Assert.Equal("App update center returned an unsuccessful status.", result.ErrorMessage);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.TargetVersion);
    }

    [Theory]
    [InlineData("http://downloads.example/hbpos-1.1.0.exe")]
    [InlineData("ftp://downloads.example/hbpos-1.1.0.exe")]
    [InlineData("/packages/hbpos-1.1.0.exe")]
    public async Task CheckAsync_invalid_download_url_contract_returns_check_failed_and_logs_warning(string downloadUrl)
    {
        var logger = new ListLogger<LocalAppUpdateService>();
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                DownloadUrl = downloadUrl
            })
        }, logger);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "INVALID_UPDATE_CONTRACT");
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("invalid app update contract", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task CheckAsync_invalid_sha256_contract_returns_check_failed_and_logs_warning(string? sha256)
    {
        var logger = new ListLogger<LocalAppUpdateService>();
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                Sha256 = sha256
            })
        }, logger);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "INVALID_UPDATE_CONTRACT");
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("invalid app update contract", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(536870913)]
    public async Task CheckAsync_invalid_file_size_contract_returns_check_failed_and_logs_warning(long fileSize)
    {
        var logger = new ListLogger<LocalAppUpdateService>();
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                FileSize = fileSize
            })
        }, logger);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "INVALID_UPDATE_CONTRACT");
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("invalid app update contract", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("bat")]
    public async Task CheckAsync_invalid_installer_type_contract_returns_check_failed_and_logs_warning(string installerType)
    {
        var logger = new ListLogger<LocalAppUpdateService>();
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                InstallerType = installerType
            })
        }, logger);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "INVALID_UPDATE_CONTRACT");
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("invalid app update contract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckAsync_installer_type_must_match_file_extension()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                FileName = "hbpos-1.1.0.msi",
                InstallerType = "exe"
            })
        });

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "INVALID_UPDATE_CONTRACT");
    }

    [Theory]
    [InlineData("CON.exe")]
    [InlineData("CON.any.exe")]
    [InlineData("NUL.v1.msi")]
    [InlineData("hbpos?.exe")]
    [InlineData("hbpos:1.2.3.exe")]
    [InlineData("hbpos.exe ")]
    [InlineData("../hbpos.exe")]
    [InlineData("folder/hbpos.exe")]
    public async Task CheckAsync_dangerous_file_name_contract_returns_check_failed(string fileName)
    {
        var logger = new ListLogger<LocalAppUpdateService>();
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                FileName = fileName,
                InstallerType = fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "msi" : "exe"
            })
        }, logger);

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        AssertCheckFailed(result, "INVALID_UPDATE_CONTRACT");
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("invalid app update contract", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CheckAsync_missing_installer_type_contract_still_returns_available_update(string? installerType)
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                InstallerType = installerType
            })
        });

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.True(result.UpdateAvailable);
        Assert.Equal("https://downloads.example/hbpos-1.1.0.exe", result.DownloadUrl);
        Assert.Equal(installerType, result.InstallerType);
    }

    [Theory]
    [InlineData("http://localhost:5000/packages/hbpos-1.1.0.exe")]
    [InlineData("http://127.0.0.1:5000/packages/hbpos-1.1.0.exe")]
    [InlineData("http://[::1]:5000/packages/hbpos-1.1.0.exe")]
    public async Task CheckAsync_loopback_http_download_url_contract_still_returns_available_update(string downloadUrl)
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CreateAvailableUpdateResponse() with
            {
                DownloadUrl = downloadUrl
            })
        });

        var result = await service.CheckAsync(new AppUpdateCheckRequest
        {
            CurrentVersion = "1.0.0",
            Channel = "production"
        });

        Assert.True(result.UpdateAvailable);
        Assert.Equal(downloadUrl, result.DownloadUrl);
    }

    private static LocalAppUpdateService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ILogger<LocalAppUpdateService>? logger = null,
        string centerBaseUrl = "https://center.example/base/")
    {
        return new LocalAppUpdateService(
            new HttpClient(new CapturingHandler(responder)),
            Options.Create(new AppUpdateOptions { CenterBaseUrl = centerBaseUrl }),
            logger ?? NullLogger<LocalAppUpdateService>.Instance);
    }

    private static AppUpdateCheckResponse CreateAvailableUpdateResponse()
    {
        return new AppUpdateCheckResponse
        {
            UpdateAvailable = true,
            ForceUpdate = true,
            IsRollback = false,
            CurrentVersion = "1.0.0",
            TargetVersion = "1.1.0",
            MinimumSupportedVersion = "1.0.0",
            DownloadUrl = "https://downloads.example/hbpos-1.1.0.exe",
            FileName = "hbpos-1.1.0.exe",
            FileSize = 1024,
            Sha256 = new string('a', 64),
            InstallerType = "exe",
            InstallerArguments = "/quiet",
            ReleaseNotes = "安全更新"
        };
    }

    private static void AssertNoUpdate(AppUpdateCheckResponse result)
    {
        Assert.False(result.UpdateAvailable);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.TargetVersion);
    }

    private static void AssertCheckFailed(AppUpdateCheckResponse result, string errorCode)
    {
        Assert.True(result.CheckFailed);
        Assert.False(result.UpdateAvailable);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.TargetVersion);
    }

    private static IDisposable TemporaryEnvironmentVariable(string name, string? value)
    {
        var previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new EnvironmentVariableScope(name, previousValue);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class EnvironmentVariableScope(string name, string? previousValue) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, previousValue);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
