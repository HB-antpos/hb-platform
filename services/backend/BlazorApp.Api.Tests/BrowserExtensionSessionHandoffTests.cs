using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Middleware;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class BrowserExtensionSessionHandoffTests : IDisposable
{
    private static readonly DateTimeOffset InitialNow = new(
        2026,
        8,
        30,
        1,
        0,
        0,
        TimeSpan.Zero
    );

    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;
    private readonly SqlSugarContext _dbContext;
    private readonly MutableTimeProvider _clock = new(InitialNow);
    private readonly AuthService _authService;
    private readonly BrowserExtensionSessionGrantService _grantService;

    public BrowserExtensionSessionHandoffTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _sqliteConnection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.Ado.ExecuteCommand("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        _db.CodeFirst.InitTables<User, Role, UserRole, RefreshToken, UserLoginRestriction>();
        _db.CodeFirst.InitTables<BrowserExtensionSessionGrantEntity>();

        _dbContext = CreateSqlSugarContext(_db);
        _authService = new AuthService(
            _dbContext,
            CreateJwtConfiguration(),
            new HttpContextAccessor()
        );
        _grantService = new BrowserExtensionSessionGrantService(
            _dbContext,
            _authService,
            _clock
        );
    }

    [Fact]
    public async Task AuthorizeAndExchange_HappyPath_IssuesFiveMinuteParentBoundTokenWithoutRefreshToken()
    {
        var (user, parentSession) = await SeedActiveParentSessionAsync("happy");
        var verifier = CreateVerifier('A');
        var state = CreateState('s');

        var authorize = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            CreateAuthorizeRequest(verifier, state)
        );
        Assert.True(authorize.Success, authorize.Message);
        Assert.Equal(state, authorize.Data!.State);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", authorize.Data.Code);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddSeconds(60), authorize.Data.ExpiresAtUtc);

        var storedGrant = await _db.Queryable<BrowserExtensionSessionGrantEntity>().SingleAsync();
        Assert.NotEqual(authorize.Data.Code, storedGrant.CodeHash);
        Assert.Equal(64, storedGrant.CodeHash.Length);

        var exchange = await _grantService.ExchangeAsync(
            new BrowserExtensionTokenRequest
            {
                Code = authorize.Data.Code,
                CodeVerifier = verifier,
                State = state,
                ClientId = BrowserExtensionSessionGrantService.ClientId,
            }
        );
        Assert.True(exchange.Success, exchange.Message);
        Assert.Null(typeof(BrowserExtensionTokenResponse).GetProperty("RefreshToken"));
        Assert.Equal(user.UserGUID, exchange.Data!.UserGuid);
        Assert.Equal(user.Username, exchange.Data.Username);
        Assert.Equal(user.FullName, exchange.Data.FullName);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(exchange.Data.AccessToken);
        Assert.Equal(parentSession.RefreshTokenGUID, jwt.Claims.Single(c => c.Type == "sessionId").Value);
        Assert.Equal("browser_extension", jwt.Claims.Single(c => c.Type == "token_use").Value);
        Assert.InRange(
            jwt.ValidTo,
            _clock.GetUtcNow().UtcDateTime.AddMinutes(5).AddSeconds(-2),
            _clock.GetUtcNow().UtcDateTime.AddMinutes(5).AddSeconds(2)
        );
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(5), exchange.Data.AccessTokenExpiry);

        var normal = await _authService.GenerateTokensAsync(user, "127.0.0.1", "xunit");
        var normalJwt = new JwtSecurityTokenHandler().ReadJwtToken(normal.AccessToken);
        Assert.InRange(
            normalJwt.ValidTo,
            DateTime.UtcNow.AddMinutes(15).AddSeconds(-5),
            DateTime.UtcNow.AddMinutes(15).AddSeconds(5)
        );
        Assert.DoesNotContain(normalJwt.Claims, claim => claim.Type == "token_use");
    }

    [Fact]
    public async Task Exchange_ReplayAndConcurrentUse_AllowsExactlyOneConsumer()
    {
        var (user, parentSession) = await SeedActiveParentSessionAsync("replay");
        var verifier = CreateVerifier('B');
        var state = CreateState('r');
        var authorize = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            CreateAuthorizeRequest(verifier, state)
        );
        var request = new BrowserExtensionTokenRequest
        {
            Code = authorize.Data!.Code,
            CodeVerifier = verifier,
            State = state,
            ClientId = BrowserExtensionSessionGrantService.ClientId,
        };

        using var secondConnection = new SqliteConnection($"Data Source={_dbPath}");
        secondConnection.Open();
        using var secondDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = secondConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        secondDb.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var secondContext = CreateSqlSugarContext(secondDb);
        var secondService = new BrowserExtensionSessionGrantService(
            secondContext,
            new AuthService(
                secondContext,
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            ),
            _clock
        );

        var results = await Task.WhenAll(
            _grantService.ExchangeAsync(request),
            secondService.ExchangeAsync(request)
        );

        Assert.Single(results, result => result.Success);
        Assert.Single(results, result => !result.Success);
        Assert.Equal(
            BrowserExtensionSessionGrantService.InvalidGrantErrorCode,
            results.Single(result => !result.Success).ErrorCode
        );
        var storedGrant = await _db.Queryable<BrowserExtensionSessionGrantEntity>().SingleAsync();
        Assert.NotNull(storedGrant.ConsumedAtUtc);
    }

    [Fact]
    public async Task Exchange_WrongVerifierStateOrClient_DoesNotConsumeGrant()
    {
        var (user, parentSession) = await SeedActiveParentSessionAsync("wrong-input");
        var verifier = CreateVerifier('C');
        var state = CreateState('w');
        var authorize = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            CreateAuthorizeRequest(verifier, state)
        );
        var code = authorize.Data!.Code;

        await AssertInvalidExchangeAsync(code, CreateVerifier('D'), state, BrowserExtensionSessionGrantService.ClientId);
        await AssertInvalidExchangeAsync(code, verifier, CreateState('x'), BrowserExtensionSessionGrantService.ClientId);
        await AssertInvalidExchangeAsync(code, verifier, state, "another-client");

        var success = await _grantService.ExchangeAsync(new BrowserExtensionTokenRequest
        {
            Code = code,
            CodeVerifier = verifier,
            State = state,
            ClientId = BrowserExtensionSessionGrantService.ClientId,
        });
        Assert.True(success.Success, success.Message);
    }

    [Fact]
    public async Task Exchange_ExpiredOrParentRevoked_FailsClosed()
    {
        var (expiredUser, expiredParent) = await SeedActiveParentSessionAsync("expired");
        var expiredVerifier = CreateVerifier('E');
        var expiredState = CreateState('e');
        var expiredGrant = await _grantService.AuthorizeAsync(
            expiredUser.UserGUID,
            expiredParent.RefreshTokenGUID,
            CreateAuthorizeRequest(expiredVerifier, expiredState)
        );
        _clock.Advance(TimeSpan.FromSeconds(61));

        await AssertInvalidExchangeAsync(
            expiredGrant.Data!.Code,
            expiredVerifier,
            expiredState,
            BrowserExtensionSessionGrantService.ClientId
        );

        var (revokedUser, revokedParent) = await SeedActiveParentSessionAsync("revoked");
        var revokedVerifier = CreateVerifier('F');
        var revokedState = CreateState('v');
        var revokedGrant = await _grantService.AuthorizeAsync(
            revokedUser.UserGUID,
            revokedParent.RefreshTokenGUID,
            CreateAuthorizeRequest(revokedVerifier, revokedState)
        );
        revokedParent.IsRevoked = true;
        await _db.Updateable(revokedParent)
            .UpdateColumns(token => token.IsRevoked)
            .ExecuteCommandAsync();

        await AssertInvalidExchangeAsync(
            revokedGrant.Data!.Code,
            revokedVerifier,
            revokedState,
            BrowserExtensionSessionGrantService.ClientId
        );

        var (inactiveUser, inactiveParent) = await SeedActiveParentSessionAsync("inactive");
        var inactiveVerifier = CreateVerifier('I');
        var inactiveState = CreateState('i');
        var inactiveGrant = await _grantService.AuthorizeAsync(
            inactiveUser.UserGUID,
            inactiveParent.RefreshTokenGUID,
            CreateAuthorizeRequest(inactiveVerifier, inactiveState)
        );
        inactiveUser.IsActive = false;
        await _db.Updateable(inactiveUser)
            .UpdateColumns(user => user.IsActive)
            .ExecuteCommandAsync();

        await AssertInvalidExchangeAsync(
            inactiveGrant.Data!.Code,
            inactiveVerifier,
            inactiveState,
            BrowserExtensionSessionGrantService.ClientId
        );

        var (deletedUser, deletedParent) = await SeedActiveParentSessionAsync("deleted");
        var deletedVerifier = CreateVerifier('J');
        var deletedState = CreateState('d');
        var deletedGrant = await _grantService.AuthorizeAsync(
            deletedUser.UserGUID,
            deletedParent.RefreshTokenGUID,
            CreateAuthorizeRequest(deletedVerifier, deletedState)
        );
        deletedUser.IsDeleted = true;
        await _db.Updateable(deletedUser)
            .UpdateColumns(user => user.IsDeleted)
            .ExecuteCommandAsync();

        await AssertInvalidExchangeAsync(
            deletedGrant.Data!.Code,
            deletedVerifier,
            deletedState,
            BrowserExtensionSessionGrantService.ClientId
        );
    }

    [Fact]
    public async Task Authorize_InvalidChallengeStateClientOrParentSession_FailsClosed()
    {
        var (user, parentSession) = await SeedActiveParentSessionAsync("authorize-invalid");
        var verifier = CreateVerifier('G');
        var valid = CreateAuthorizeRequest(verifier, CreateState('a'));

        var wrongClient = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            new BrowserExtensionAuthorizeRequest
            {
                CodeChallenge = valid.CodeChallenge,
                State = valid.State,
                ClientId = "another-client",
            }
        );
        var wrongChallenge = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            new BrowserExtensionAuthorizeRequest
            {
                CodeChallenge = "short",
                State = valid.State,
                ClientId = valid.ClientId,
            }
        );
        var wrongState = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            new BrowserExtensionAuthorizeRequest
            {
                CodeChallenge = valid.CodeChallenge,
                State = "short",
                ClientId = valid.ClientId,
            }
        );
        var wrongParent = await _grantService.AuthorizeAsync(
            user.UserGUID,
            "another-session",
            valid
        );

        Assert.False(wrongClient.Success);
        Assert.False(wrongChallenge.Success);
        Assert.False(wrongState.Success);
        Assert.False(wrongParent.Success);
        Assert.Equal(0, await _db.Queryable<BrowserExtensionSessionGrantEntity>().CountAsync());
    }

    [Fact]
    public async Task ControllerAuthorize_RequiresCookieAndRejectsAnyAuthorizationHeader()
    {
        var (user, parentSession) = await SeedActiveParentSessionAsync("controller");
        var request = CreateAuthorizeRequest(CreateVerifier('H'), CreateState('c'));

        var bearerOnly = CreateController(user.UserGUID, parentSession.RefreshTokenGUID);
        bearerOnly.Request.Headers.Authorization = "Bearer extension-token";
        var bearerResult = await bearerOnly.ExtensionAuthorize(request, _grantService);
        Assert.False(bearerResult.Success);
        Assert.Equal(
            BrowserExtensionSessionGrantService.CookieSessionRequiredErrorCode,
            bearerResult.ErrorCode
        );

        var cookieAndBearer = CreateController(user.UserGUID, parentSession.RefreshTokenGUID);
        cookieAndBearer.Request.Headers.Cookie = "access_token=website-token";
        cookieAndBearer.Request.Headers.Authorization = "Bearer extension-token";
        var mixedResult = await cookieAndBearer.ExtensionAuthorize(request, _grantService);
        Assert.False(mixedResult.Success);

        var extensionBearerInCookie = CreateController(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            browserExtensionToken: true
        );
        extensionBearerInCookie.Request.Headers.Cookie = "access_token=extension-token";
        var extensionCookieResult = await extensionBearerInCookie.ExtensionAuthorize(
            request,
            _grantService
        );
        Assert.False(extensionCookieResult.Success);

        var cookieOnly = CreateController(user.UserGUID, parentSession.RefreshTokenGUID);
        cookieOnly.Request.Headers.Cookie = "access_token=website-token";
        var cookieResult = await cookieOnly.ExtensionAuthorize(request, _grantService);
        Assert.True(cookieResult.Success, cookieResult.Message);
    }

    [Fact]
    public void ControllerRoutes_HaveRequiredAuthorizationMetadata()
    {
        var authorize = typeof(AuthController).GetMethod(nameof(AuthController.ExtensionAuthorize));
        var token = typeof(AuthController).GetMethod(nameof(AuthController.ExtensionToken));

        Assert.NotNull(authorize);
        Assert.NotNull(token);
        Assert.NotNull(authorize!.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(authorize.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(token!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(
            BrowserExtensionSessionGrantRateLimits.AuthorizePolicyName,
            authorize.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
        );
        Assert.Equal(
            BrowserExtensionSessionGrantRateLimits.ExchangePolicyName,
            token.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
        );
    }

    [Fact]
    public async Task RateLimiterOptions_拒绝处理仅覆盖浏览器扩展策略且不污染Mobile语义()
    {
        var options = new RateLimiterOptions();
        var defaultRejectionStatusCode = options.RejectionStatusCode;
        MobileDeviceActivationRateLimits.Configure(options);
        BrowserExtensionSessionGrantRateLimits.Configure(options);

        Assert.Equal(defaultRejectionStatusCode, options.RejectionStatusCode);
        Assert.NotNull(options.OnRejected);

        static DefaultHttpContext CreateContext(string policyName, int statusCode)
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Response.StatusCode = statusCode;
            context.SetEndpoint(
                new Endpoint(
                    _ => Task.CompletedTask,
                    new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
                    policyName
                )
            );
            return context;
        }

        var mobileContext = CreateContext(
            MobileDeviceActivationRateLimits.SessionExchangePolicy,
            defaultRejectionStatusCode
        );
        await options.OnRejected!(
            new OnRejectedContext
            {
                HttpContext = mobileContext,
                Lease = Mock.Of<System.Threading.RateLimiting.RateLimitLease>(),
            },
            CancellationToken.None
        );
        Assert.Equal(defaultRejectionStatusCode, mobileContext.Response.StatusCode);
        Assert.Equal(0, mobileContext.Response.Body.Length);

        foreach (
            var policyName in new[]
            {
                BrowserExtensionSessionGrantRateLimits.AuthorizePolicyName,
                BrowserExtensionSessionGrantRateLimits.ExchangePolicyName,
            }
        )
        {
            var extensionContext = CreateContext(policyName, defaultRejectionStatusCode);
            await options.OnRejected(
                new OnRejectedContext
                {
                    HttpContext = extensionContext,
                    Lease = Mock.Of<System.Threading.RateLimiting.RateLimitLease>(),
                },
                CancellationToken.None
            );

            var responseBody = Encoding.UTF8.GetString(
                ((MemoryStream)extensionContext.Response.Body).ToArray()
            );
            Assert.Equal(StatusCodes.Status429TooManyRequests, extensionContext.Response.StatusCode);
            Assert.Contains(
                BrowserExtensionSessionGrantRateLimits.RateLimitedErrorCode,
                responseBody,
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public async Task Authorize_CleansOnlyOneBoundedBatchOutsideRetentionWindow()
    {
        var (user, parentSession) = await SeedActiveParentSessionAsync("cleanup");
        var now = _clock.GetUtcNow().UtcDateTime;
        var cleanupCutoff = now - BrowserExtensionSessionGrantService.CleanupRetention;
        var oldRows = Enumerable.Range(
                0,
                BrowserExtensionSessionGrantService.CleanupBatchSize + 1
            )
            .Select(index => new BrowserExtensionSessionGrantEntity
            {
                GrantId = Guid.NewGuid(),
                CodeHash = index.ToString("D64"),
                CodeChallenge = new string('C', 43),
                State = new string('s', 22),
                ParentSessionId = "old-parent",
                UserGuid = "old-user",
                ClientId = BrowserExtensionSessionGrantService.ClientId,
                IssuedAtUtc = cleanupCutoff.AddMinutes(-2),
                ExpiresAtUtc = cleanupCutoff.AddMinutes(-1),
                ConsumedAtUtc = index % 2 == 0 ? cleanupCutoff.AddMinutes(-1) : null,
            })
            .ToList();
        var recentlyExpired = new BrowserExtensionSessionGrantEntity
        {
            GrantId = Guid.NewGuid(),
            CodeHash = new string('e', 64),
            CodeChallenge = new string('D', 43),
            State = new string('r', 22),
            ParentSessionId = "recent-parent",
            UserGuid = "recent-user",
            ClientId = BrowserExtensionSessionGrantService.ClientId,
            IssuedAtUtc = now.AddMinutes(-2),
            ExpiresAtUtc = now.AddMinutes(-1),
        };
        await _db.Insertable(oldRows.Append(recentlyExpired).ToList()).ExecuteCommandAsync();

        var first = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            CreateAuthorizeRequest(CreateVerifier('Z'), CreateState('z'))
        );

        Assert.True(first.Success);
        Assert.Equal(
            1,
            await _db.Queryable<BrowserExtensionSessionGrantEntity>()
                .CountAsync(item => item.ExpiresAtUtc < cleanupCutoff)
        );
        Assert.True(await _db.Queryable<BrowserExtensionSessionGrantEntity>()
            .AnyAsync(item => item.GrantId == recentlyExpired.GrantId));

        var second = await _grantService.AuthorizeAsync(
            user.UserGUID,
            parentSession.RefreshTokenGUID,
            CreateAuthorizeRequest(CreateVerifier('Y'), CreateState('y'))
        );

        Assert.True(second.Success);
        Assert.Equal(
            0,
            await _db.Queryable<BrowserExtensionSessionGrantEntity>()
                .CountAsync(item => item.ExpiresAtUtc < cleanupCutoff)
        );
    }

    [Fact]
    public void ExtensionRateLimitPartitions_AreSessionAndTrustedClientIpScoped()
    {
        var resolver = Mock.Of<IClientIpResolver>(item => item.Resolve(It.IsAny<HttpContext>()) == "203.0.113.10");
        var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sessionId", "parent-session")
        ], "test"));

        Assert.Equal(
            "session:parent-session",
            BrowserExtensionSessionGrantRateLimits.ResolveAuthorizePartitionKey(context)
        );
        Assert.Equal(
            "ip:203.0.113.10",
            BrowserExtensionSessionGrantRateLimits.ResolveExchangePartitionKey(context)
        );
    }

    [Fact]
    public async Task RefreshRotation_TwoIndependentClients_OnlyOneCanConsumeParentSession()
    {
        var userGuid = Guid.NewGuid().ToString();
        const string refreshToken = "concurrent-parent-refresh";
        await _db.Insertable(new User
        {
            UserGUID = userGuid,
            Username = "concurrent-refresh-user",
            Email = "concurrent-refresh@example.test",
            PasswordHash = "not-used",
            FullName = "Concurrent Refresh",
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _db.Insertable(new RefreshToken
        {
            RefreshTokenGUID = Guid.NewGuid().ToString(),
            UserGUID = userGuid,
            Token = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            IsRevoked = false,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        using var firstDb = CreateIndependentSqliteClient();
        using var secondDb = CreateIndependentSqliteClient();
        var firstService = new AuthService(
            CreateSqlSugarContext(firstDb),
            CreateJwtConfiguration(),
            new HttpContextAccessor()
        );
        var secondService = new AuthService(
            CreateSqlSugarContext(secondDb),
            CreateJwtConfiguration(),
            new HttpContextAccessor()
        );
        using var revokeBarrier = new Barrier(2);
        var firstWaited = 0;
        var secondWaited = 0;
        firstDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsRefreshRevocationUpdate(sql) && Interlocked.Exchange(ref firstWaited, 1) == 0)
            {
                Assert.True(revokeBarrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            }
        };
        secondDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsRefreshRevocationUpdate(sql) && Interlocked.Exchange(ref secondWaited, 1) == 0)
            {
                Assert.True(revokeBarrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            }
        };

        var results = await Task.WhenAll(
            Task.Run(() => firstService.RefreshTokensAsync(
                string.Empty,
                refreshToken,
                "8.8.8.8",
                "concurrent-a"
            )),
            Task.Run(() => secondService.RefreshTokensAsync(
                string.Empty,
                refreshToken,
                "8.8.8.8",
                "concurrent-b"
            ))
        );

        Assert.Equal(1, Volatile.Read(ref firstWaited));
        Assert.Equal(1, Volatile.Read(ref secondWaited));
        var successfulRefresh = Assert.Single(results, result => result != null)!;
        var activeSessions = await _db.Queryable<RefreshToken>()
            .Where(item =>
                item.UserGUID == userGuid
                && !item.IsRevoked
                && !item.IsDeleted
            )
            .ToListAsync();
        Assert.Single(activeSessions);
        Assert.Equal(successfulRefresh.RefreshToken, activeSessions[0].Token);
    }

    [Fact]
    public void SchemaMigrator_DefinesPersistentUniqueGrantAndStartupWiring()
    {
        var sql = string.Join("\n", BrowserExtensionSessionGrantSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("CREATE TABLE [dbo].[BrowserExtensionSessionGrant]", sql);
        Assert.Contains("[CodeHash] NVARCHAR(64) NOT NULL", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UX_BrowserExtensionSessionGrant_CodeHash]", sql);
        Assert.Contains("CREATE INDEX [IX_BrowserExtensionSessionGrant_ExpiresAtUtc]", sql);
        Assert.Contains("CREATE INDEX [IX_BrowserExtensionSessionGrant_ParentSessionId]", sql);
        Assert.Contains("[ConsumedAtUtc] DATETIME2(7) NULL", sql);
        Assert.DoesNotContain("CodeVerifier", sql, StringComparison.OrdinalIgnoreCase);

        var startupPath = Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Data/StartupSchemaMigrator.cs"
        );
        var startup = File.ReadAllText(startupPath);
        Assert.Contains(
            "await BrowserExtensionSessionGrantSchemaMigrator.EnsureAsync(db, logger);",
            startup
        );
    }

    [Theory]
    [InlineData("GET", "/api/react/v1/browser-extension/stores")]
    [InlineData("POST", "/api/react/v1/browser-extension/product-purchase-cycles")]
    public async Task ExtensionTokenScope_OnlyAllowsExtensionApis(
        string method,
        string path
    )
    {
        var nextCalled = false;
        var middleware = new BrowserExtensionTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateScopeContext(method, path, browserExtensionToken: true);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/react/v1/browser-extension-evil")]
    [InlineData("GET", "/api/Auth/current")]
    [InlineData("HEAD", "/api/auth/current")]
    [InlineData("POST", "/api/Auth/current")]
    public async Task ExtensionTokenScope_DeniesOtherRoutesWithoutEnteringNext(
        string method,
        string path
    )
    {
        var nextCalled = false;
        var middleware = new BrowserExtensionTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateScopeContext(method, path, browserExtensionToken: true);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(
            BrowserExtensionTokenScopeMiddleware.ScopeDeniedErrorCode,
            document.RootElement.GetProperty("errorCode").GetString()
        );
    }

    [Fact]
    public async Task ExtensionTokenScope_NormalWebsiteTokenStillPasses()
    {
        var nextCalled = false;
        var middleware = new BrowserExtensionTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateScopeContext("GET", "/api/users", browserExtensionToken: false);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ExtensionTokenScope_RejectsExpiredTokenDespiteJwtDefaultClockSkew()
    {
        var nextCalled = false;
        var middleware = new BrowserExtensionTokenScopeMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            _clock
        );
        var context = CreateScopeContext(
            "GET",
            "/api/react/v1/browser-extension/stores",
            browserExtensionToken: true,
            expiresAtUnixSeconds: _clock.GetUtcNow().AddSeconds(-1).ToUnixTimeSeconds()
        );

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(
            BrowserExtensionTokenScopeMiddleware.ExtensionTokenExpiredErrorCode,
            document.RootElement.GetProperty("errorCode").GetString()
        );
    }

    [Fact]
    public async Task ExtensionTokenScope_MissingExpirationFailsClosed()
    {
        var nextCalled = false;
        var middleware = new BrowserExtensionTokenScopeMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            _clock
        );
        var context = CreateScopeContext(
            "GET",
            "/api/react/v1/browser-extension/stores",
            browserExtensionToken: true,
            expiresAtUnixSeconds: null,
            includeExpiration: false
        );

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public void Program_ExtensionTokenScopeRunsAfterAuthenticationBeforeAuthorization()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Program.cs"
        ));
        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var rateLimit = program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        var scope = program.IndexOf(
            "app.UseMiddleware<BrowserExtensionTokenScopeMiddleware>();",
            StringComparison.Ordinal
        );
        var authorization = program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);

        Assert.True(authentication >= 0);
        Assert.Contains("AddRateLimiter", program);
        Assert.True(rateLimit > authentication);
        Assert.True(scope > rateLimit);
        Assert.True(authorization > scope);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqliteConnection.Dispose();
        if (File.Exists(_dbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
        }
    }

    private async Task AssertInvalidExchangeAsync(
        string code,
        string verifier,
        string state,
        string clientId
    )
    {
        var result = await _grantService.ExchangeAsync(new BrowserExtensionTokenRequest
        {
            Code = code,
            CodeVerifier = verifier,
            State = state,
            ClientId = clientId,
        });
        Assert.False(result.Success);
        Assert.Equal(BrowserExtensionSessionGrantService.InvalidGrantErrorCode, result.ErrorCode);
    }

    private async Task<(User User, RefreshToken ParentSession)> SeedActiveParentSessionAsync(
        string suffix
    )
    {
        var user = new User
        {
            UserGUID = $"user-{suffix}",
            Username = $"user-{suffix}",
            Email = $"{suffix}@example.test",
            PasswordHash = "not-used",
            FullName = suffix,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        var session = new RefreshToken
        {
            RefreshTokenGUID = $"session-{suffix}",
            UserGUID = user.UserGUID,
            Token = $"refresh-{suffix}",
            ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(1),
            IsRevoked = false,
            IsDeleted = false,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        };

        await _db.Insertable(user).ExecuteCommandAsync();
        await _db.Insertable(session).ExecuteCommandAsync();
        return (user, session);
    }

    private AuthController CreateController(
        string userGuid,
        string sessionId,
        bool browserExtensionToken = false
    )
    {
        var controller = new AuthController(
            _authService,
            _dbContext,
            Mock.Of<IRoleService>(),
            Mock.Of<IUserService>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<AuthController>>()
        );
        var claims = new List<Claim>
        {
            new("userId", userGuid),
            new("sessionId", sessionId),
        };
        if (browserExtensionToken)
        {
            claims.Add(new Claim("token_use", "browser_extension"));
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(claims, "test")
            ),
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = context,
        };
        return controller;
    }

    private static BrowserExtensionAuthorizeRequest CreateAuthorizeRequest(
        string verifier,
        string state
    ) => new()
    {
        CodeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
        State = state,
        ClientId = BrowserExtensionSessionGrantService.ClientId,
    };

    private static string CreateVerifier(char value) => new(value, 43);

    private static string CreateState(char value) => new(value, 22);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static DefaultHttpContext CreateScopeContext(
        string method,
        string path,
        bool browserExtensionToken,
        long? expiresAtUnixSeconds = null,
        bool includeExpiration = true
    )
    {
        var claims = new List<Claim> { new("userId", "scope-user") };
        if (browserExtensionToken)
        {
            claims.Add(new Claim("token_use", "browser_extension"));
            if (includeExpiration)
            {
                claims.Add(new Claim(
                    JwtRegisteredClaimNames.Exp,
                    (expiresAtUnixSeconds ?? DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())
                        .ToString()
                ));
            }
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static IConfiguration CreateJwtConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "browser-extension-test-signing-key-at-least-32-bytes",
                ["Jwt:Issuer"] = "hb-tests",
                ["Jwt:Audience"] = "hb-tests",
                ["Jwt:ExpireMinutes"] = "60",
            })
            .Build();

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext)
        );
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);
        return context;
    }

    private SqlSugarClient CreateIndependentSqliteClient()
    {
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=10000;");
        return client;
    }

    private static bool IsRefreshRevocationUpdate(string sql) =>
        sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("RefreshToken", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("IsRevoked", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "services", "backend")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("无法定位仓库根目录");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
