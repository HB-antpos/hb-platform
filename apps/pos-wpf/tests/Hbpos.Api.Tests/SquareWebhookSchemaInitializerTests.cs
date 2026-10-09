using System.Reflection;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;

namespace Hbpos.Api.Tests;

public sealed class SquareWebhookSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_checkout_session_and_webhook_event_ddl()
    {
        var executor = new CapturingSquareWebhookSchemaSqlExecutor();
        var initializer = new SqlSugarSquareWebhookSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(4, executor.SqlStatements.Count);
        var combinedSql = string.Join(Environment.NewLine, executor.SqlStatements);
        Assert.Contains("POSM_SquareCheckoutSession", combinedSql);
        Assert.Contains("POSM_SquareWebhookEvent", combinedSql);
        Assert.Contains("[CheckoutId] NVARCHAR(128) NOT NULL", combinedSql);
        Assert.Contains("[Status] NVARCHAR(64) NOT NULL", combinedSql);
        Assert.Contains("[Amount] BIGINT NULL", combinedSql);
        Assert.Contains("[Currency] NVARCHAR(16) NULL", combinedSql);
        Assert.Contains("[DeviceId] NVARCHAR(128) NULL", combinedSql);
        Assert.Contains("[LocationId] NVARCHAR(128) NULL", combinedSql);
        Assert.Contains("[OriginStoreCode] NVARCHAR(50) NULL", combinedSql);
        Assert.Contains("[OriginDeviceCode] NVARCHAR(128) NULL", combinedSql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_SquareCheckoutSession', N'OriginStoreCode') IS NULL", combinedSql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_SquareCheckoutSession', N'OriginDeviceCode') IS NULL", combinedSql);
        Assert.Contains("[PaymentId] NVARCHAR(128) NULL", combinedSql);
        Assert.Contains("[PaymentIdsJson] NVARCHAR(MAX) NULL", combinedSql);
        Assert.Contains("[RawCheckoutJson] NVARCHAR(MAX) NOT NULL", combinedSql);
        Assert.Contains("[LastEventId] NVARCHAR(128) NULL", combinedSql);
        Assert.Contains("[PayloadJson] NVARCHAR(MAX) NOT NULL", combinedSql);
        Assert.Contains("UX_POSM_SquareCheckoutSession_Environment_CheckoutId", combinedSql);
        Assert.Contains("UX_POSM_SquareWebhookEvent_Environment_EventId", combinedSql);
        Assert.Contains("CHECK ([Environment] IN (N'Production', N'Sandbox'))", combinedSql);
        Assert.Contains("POSM_SquareRefund", combinedSql);
        Assert.Contains("[RawRefundJson] NVARCHAR(MAX) NOT NULL", combinedSql);
        Assert.Contains("UX_POSM_SquareRefund_Environment_RefundId", combinedSql);
    }

    [Fact]
    public void Refund_upsert_keeps_terminal_status_and_ignores_older_events()
    {
        var sql = (string?)typeof(SqlSugarSquareRefundRepository)
            .GetField("UpsertRefundSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetRawConstantValue();

        Assert.NotNull(sql);
        Assert.Contains("@UpdatedAt >= target.[UpdatedAt]", sql);
        Assert.Contains("target.[Status] NOT IN (N'COMPLETED', N'REJECTED', N'FAILED', N'CANCELED')", sql);
        Assert.Contains("OR @Status IN (N'COMPLETED', N'REJECTED', N'FAILED', N'CANCELED')", sql);
    }

    [Fact]
    public void Checkout_upsert_preserves_existing_origin_when_webhook_has_no_origin()
    {
        var sql = (string?)typeof(SqlSugarSquareCheckoutSessionRepository)
            .GetField("UpsertCheckoutSessionSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetRawConstantValue();

        Assert.NotNull(sql);
        Assert.Contains("[OriginStoreCode] = COALESCE(target.[OriginStoreCode], @OriginStoreCode)", sql);
        Assert.Contains("[OriginDeviceCode] = COALESCE(target.[OriginDeviceCode], @OriginDeviceCode)", sql);
    }

    [Fact]
    public void Checkout_lookup_refresh_is_scope_bound_append_only_and_preserves_webhook_evidence()
    {
        var sql = (string?)typeof(SqlSugarSquareCheckoutSessionRepository)
            .GetField("RefreshCheckoutSessionFromLookupSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetRawConstantValue();

        Assert.NotNull(sql);
        Assert.Contains("CROSS APPLY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, CountOccurrences(sql!, "WHEN accepted.[Value] = 1"));
        Assert.Contains("target.[OriginStoreCode] = @OriginStoreCode", sql, StringComparison.Ordinal);
        Assert.Contains("target.[OriginDeviceCode] = @OriginDeviceCode", sql, StringComparison.Ordinal);
        Assert.Contains("OPENJSON", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STRING_ESCAPE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[RawCheckoutJson] =", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[LastEventId] =", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void IsUniqueConstraintViolation_returns_true_for_sql_unique_constraint_numbers(int number)
    {
        var exception = CreateSqlException(number);

        Assert.True(SquareWebhookSqlErrorClassifier.IsUniqueConstraintViolation(exception));
    }

    [Fact]
    public void IsUniqueConstraintViolation_returns_false_for_unique_text_without_sql_number()
    {
        var exception = new InvalidOperationException(
            "Failed to parse uniqueidentifier value while writing Square webhook event.");

        Assert.False(SquareWebhookSqlErrorClassifier.IsUniqueConstraintViolation(exception));
    }

    [Fact]
    public void IsUniqueConstraintViolation_returns_false_for_non_duplicate_errors()
    {
        var exception = new InvalidOperationException("Timeout while writing webhook event.");

        Assert.False(SquareWebhookSqlErrorClassifier.IsUniqueConstraintViolation(exception));
    }

    private static SqlException CreateSqlException(int number)
    {
        var errorCollection = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: null,
            culture: null)!;
        var error = CreateSqlError(number);
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(errorCollection, [error]);

        var createException = typeof(SqlException)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .First(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "CreateException" &&
                    parameters.Length >= 2 &&
                    parameters[0].ParameterType == typeof(SqlErrorCollection);
            });

        var args = createException.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(SqlErrorCollection)
                ? errorCollection
                : parameter.ParameterType == typeof(string)
                    ? "15.0.0"
                    : parameter.HasDefaultValue
                        ? parameter.DefaultValue
                        : parameter.ParameterType.IsValueType
                            ? Activator.CreateInstance(parameter.ParameterType)
                            : null)
            .ToArray();
        return (SqlException)createException.Invoke(null, args)!;
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }

    private static SqlError CreateSqlError(int number)
    {
        var constructor = typeof(SqlError)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        var args = constructor.GetParameters()
            .Select(parameter => CreateSqlErrorArgument(parameter, number))
            .ToArray();
        return (SqlError)constructor.Invoke(args);
    }

    private static object? CreateSqlErrorArgument(ParameterInfo parameter, int number)
    {
        if (parameter.ParameterType == typeof(int))
        {
            return string.Equals(parameter.Name, "infoNumber", StringComparison.OrdinalIgnoreCase)
                ? number
                : 0;
        }

        if (parameter.ParameterType == typeof(byte))
        {
            return (byte)0;
        }

        if (parameter.ParameterType == typeof(uint))
        {
            return 0u;
        }

        if (parameter.ParameterType == typeof(string))
        {
            return parameter.Name switch
            {
                "server" => "test-sql",
                "errorMessage" => $"SQL Server duplicate key error {number}.",
                "procedure" => string.Empty,
                _ => string.Empty
            };
        }

        return parameter.HasDefaultValue
            ? parameter.DefaultValue
            : parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null;
    }

    private sealed class CapturingSquareWebhookSchemaSqlExecutor : ISquareWebhookSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}
