namespace BlazorApp.Api.Data.SchemaMigrations;

internal static class SchemaExitCodes
{
    public const int Success = 0;
    public const int ConfigurationError = 2;
    public const int SchemaNotReady = 20;
    public const int DatabaseFailure = 22;
    public const int MigrationLockUnavailable = 23;
    public const int Cancelled = 130;
}

internal static class SchemaDiagnosticCodes
{
    public const string Ready = "SCHEMA_READY";
    public const string MigrationSucceeded = "SCHEMA_MIGRATION_SUCCEEDED";
    public const string MainMigrationMissing = "SCHEMA_MAIN_MIGRATION_MISSING";
    public const string PosmMigrationMissing = "SCHEMA_POSM_MIGRATION_MISSING";
    public const string DeviceActivationIncompatible = "SCHEMA_DEVICE_ACTIVATION_INCOMPATIBLE";
    public const string ContainerDetailQueryIndexesIncompatible =
        "SCHEMA_CONTAINER_DETAIL_QUERY_INDEXES_INCOMPATIBLE";
    public const string ContainerDetailCollaborationIncompatible =
        "SCHEMA_CONTAINER_DETAIL_COLLABORATION_INCOMPATIBLE";
    public const string LinklyMultiTerminalIncompatible =
        "SCHEMA_LINKLY_MULTI_TERMINAL_INCOMPATIBLE";
    public const string PricingCurveIncompatible = "SCHEMA_PRICING_CURVE_INCOMPATIBLE";
    public const string MobileOtaRuntimeTargetsIncompatible =
        "SCHEMA_MOBILE_OTA_RUNTIME_TARGETS_INCOMPATIBLE";
    public const string LocalSupplierCategoryIncompatible =
        "SCHEMA_LOCAL_SUPPLIER_CATEGORY_INCOMPATIBLE";
    public const string ProviderUnsupported = "SCHEMA_PROVIDER_UNSUPPORTED";
    public const string DatabaseFailure = "SCHEMA_DATABASE_FAILURE";
    public const string MigrationFailure = "SCHEMA_MIGRATION_FAILURE";
    public const string MigrationLockUnavailable = "SCHEMA_MIGRATION_LOCK_UNAVAILABLE";
    public const string SalesDetailQueryProjectionIncompatible =
        "SCHEMA_SALES_DETAIL_QUERY_PROJECTION_INCOMPATIBLE";
    public const string SalesDetailProjectionConfigurationInvalid =
        "SALES_DETAIL_PROJECTION_CONFIGURATION_INVALID";
    public const string SalesDetailProjectionTargetMismatch =
        "SALES_DETAIL_PROJECTION_TARGET_MISMATCH";
    public const string SalesDetailProjectionDateFailed =
        "SALES_DETAIL_PROJECTION_DATE_FAILED";
    public const string SalesDetailProjectionCoverageMissing =
        "SALES_DETAIL_PROJECTION_COVERAGE_MISSING";
    public const string Cancelled = "SCHEMA_CANCELLED";
}

internal sealed record SchemaOperationResult(bool Success, int ExitCode, string DiagnosticCode)
{
    public static SchemaOperationResult Ready() =>
        new(true, SchemaExitCodes.Success, SchemaDiagnosticCodes.Ready);

    public static SchemaOperationResult MigrationSucceeded() =>
        new(true, SchemaExitCodes.Success, SchemaDiagnosticCodes.MigrationSucceeded);

    public static SchemaOperationResult Failure(int exitCode, string diagnosticCode) =>
        new(false, exitCode, diagnosticCode);
}
