using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace BlazorApp.Shared.Helper
{
    /// <summary>
    /// 国内供应商（ChinaSupplier，代码如 HB196）敏感信息遮罩：名称、店铺号、联系人、电话、邮箱、门头照、备注。
    /// 没有「查看国内供应商名称」权限的账号，接口返回里这些字段一律为空，只保留供应商代码。
    /// 两个互不影响的 AsyncLocal 开关，都由全局过滤器设置：
    /// - 遮罩开关：只包住「序列化响应」这一段。接口方法执行期间不遮罩，服务层把对象写进缓存、同步 HQ、记审计时拿到的仍是原值。
    /// - 名称搜索开关：只包住「执行接口方法」这一段，查询据此决定能否按供应商名称匹配 / 排序。
    /// 请求之外（后台任务、内部序列化、单元测试直接调服务）两个开关都不生效：不遮罩、不限制搜索。
    /// </summary>
    public static class DomesticSupplierInfoMasking
    {
        private static readonly AsyncLocal<bool> MaskingActive = new();
        private static readonly AsyncLocal<bool> NameSearchBlocked = new();

        /// <summary>当前异步流程（一次 HTTP 响应序列化）里，带 <see cref="DomesticSupplierInfoJsonConverter"/> 的属性是否要输出空值。</summary>
        public static bool IsMaskingActive => MaskingActive.Value;

        /// <summary>
        /// 当前账号能否用国内供应商名称做搜索 / 排序条件。不可看名称的账号不能拿关键词去试探名称，
        /// 查询里凡是「按国内供应商名称匹配或排序」的分支都要先看这个开关；请求之外恒为 true。
        /// </summary>
        public static bool IsNameSearchAllowed => !NameSearchBlocked.Value;

        /// <summary>
        /// 接口方法执行期间，当前账号能否看到国内供应商名称。给「在接口方法里直接生成文件」的导出代码用
        /// （文件不经过 JSON 转换器）；与 <see cref="IsNameSearchAllowed"/> 同源，请求之外恒为 true。
        /// </summary>
        public static bool IsNameVisibleInCurrentAction => !NameSearchBlocked.Value;

        /// <summary>
        /// 不可能出现在任何供应商名称里的占位关键词（首尾是 Unicode 私用区字符）。
        /// 名称搜索被禁止时，用它替换真正的关键词，让「按名称匹配」这一支恒不命中。
        /// </summary>
        public const string BlockedNameKeyword = "domestic-supplier-name-search-blocked";

        /// <summary>
        /// 给「按国内供应商名称匹配」的查询分支用：允许时原样返回关键词，禁止时返回 <see cref="BlockedNameKeyword"/>。
        /// 这样查询的结构、参数个数都不变，只是名称这一支匹配不到任何行，不必为两种权限各写一份 SQL。
        /// 只包住名称（及店铺号、联系人等敏感字段）那一支；商品名、货号、供应商代码等分支仍用原关键词。
        /// </summary>
        public static string GuardNameKeyword(string keyword) =>
            IsNameSearchAllowed ? keyword : BlockedNameKeyword;

        /// <summary>按是否有权限返回原值或空值；用于不走 JSON 转换器的出口（导出文件、匿名对象、与本地供应商共用的 DTO）。</summary>
        public static string? Apply(string? value, bool canView) => canView ? value : null;

        /// <summary>
        /// 开启遮罩作用域：作用域内序列化带 <see cref="DomesticSupplierInfoJsonConverter"/> 的属性时输出 null。
        /// 只在结果过滤器里、包住响应序列化时使用；Dispose 后恢复原值。
        /// </summary>
        public static IDisposable BeginMaskingScope(bool mask)
        {
            var previous = MaskingActive.Value;
            MaskingActive.Value = mask;
            return new Scope(MaskingActive, previous);
        }

        /// <summary>
        /// 开启查询作用域：<paramref name="canView"/> 为 false 时，作用域内的查询不得按国内供应商名称匹配 / 排序。
        /// 只在动作过滤器里、包住接口方法执行时使用；Dispose 后恢复原值。
        /// </summary>
        public static IDisposable BeginQueryScope(bool canView)
        {
            var previous = NameSearchBlocked.Value;
            NameSearchBlocked.Value = !canView;
            return new Scope(NameSearchBlocked, previous);
        }

        private sealed class Scope(AsyncLocal<bool> target, bool previous) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                target.Value = previous;
            }
        }
    }

    /// <summary>
    /// 国内供应商敏感字段专用 JSON 转换器：只在遮罩作用域内输出 null，读取与作用域外原样。
    /// 遮罩发生在序列化阶段，不修改 DTO / 实体对象本身，因此内存缓存里被多个账号共用的对象不会被污染。
    /// 新增任何携带国内供应商名称的返回字段时，必须挂上本转换器（DomesticSupplierInfoMaskingTests 有反射门禁）。
    /// </summary>
    public sealed class DomesticSupplierInfoJsonConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null || DomesticSupplierInfoMasking.IsMaskingActive)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(value);
        }
    }
}
