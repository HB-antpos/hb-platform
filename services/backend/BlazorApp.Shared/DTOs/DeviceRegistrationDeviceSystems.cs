namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 设备注册平台的兼容显示和筛选常量。
    /// </summary>
    public static class DeviceRegistrationDeviceSystems
    {
        public const string Windows = "Windows";
        public const string IpadOs = "iPadOS";

        /// <summary>安卓平板上运行的大屏收银 App，业务类别等同 iPadOS（不是手持端）。</summary>
        public const string AndroidTablet = "AndroidTablet";

        public const string Other = "Other";

        /// <summary>
        /// 旧设备没有记录平台时按 Windows 展示；未知非空平台保留原值。
        /// </summary>
        public static string NormalizeForDisplay(string? deviceSystem)
        {
            var normalized = deviceSystem?.Trim();
            return string.IsNullOrEmpty(normalized) ? Windows : normalized;
        }
    }
}
