namespace BlazorApp.Shared.Constants;

public static class ServiceApiScopes
{
    public const string ReadAppUpdateDecisions = "Service.ReadAppUpdateDecisions";
    public const string WritePerformanceMetrics = "Service.WritePerformanceMetrics";
    public const string WriteReleaseEvents = "Service.WriteReleaseEvents";
    public const string RemoteMaintenance = "Service.RemoteMaintenance";

    /// <summary>POS API 为已登记的大屏收银设备换取「订货助手」短期设备令牌。</summary>
    public const string IssuePosDeviceSupplierAssistantTokens =
        "Service.IssuePosDeviceSupplierAssistantTokens";
}

public static class ServiceApiTokenPurposes
{
    public const string MobileOtaPublisher = "mobile-ota-publisher";
    public const string PosIpadUpdateDecisionReader = "pos-ipad-update-decision-reader";
    public const string QualityCiReporter = "quality-ci-reporter";
    public const string DeploymentAcceptanceReporter = "deployment-acceptance-reporter";
    public const string RemoteMaintenanceGateway = "remote-maintenance-gateway";
    public const string PosSupplierAssistantTokenIssuer = "pos-supplier-assistant-token-issuer";
}
