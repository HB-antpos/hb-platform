namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 设备运行态时间（POSM_设备注册信息表 的「最后心跳时间」「收银员登录时间」）的 UTC 口径。
    /// <para>
    /// 这两列由 POS API 与主后端用 DateTime.Now 写入，而两个生产容器的系统时区都是 UTC，所以库里存的就是 UTC。
    /// 但从库里读出来 Kind 是 Unspecified，System.Text.Json 序列化时不带 Z，浏览器会按本地时区解析：
    /// 悉尼用户看到的时间偏差 10–11 小时，设备被判为离线、「在线」计数恒为 0。
    /// 返回给前端之前用 <see cref="AsUtc(DateTime?)"/> 显式标成 UTC（只改 Kind，不改数值）。
    /// </para>
    /// <para>
    /// 只适用于运行态列；同一张表的创建时间、最后修改时间等审计列历史上口径混杂（不同写入方、不同时期的容器时区不同），不要套用。
    /// </para>
    /// </summary>
    internal static class DeviceRuntimeTime
    {
        public static DateTime? AsUtc(DateTime? value) => value is null ? null : AsUtc(value.Value);

        public static DateTime AsUtc(DateTime value) =>
            value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
    }
}
