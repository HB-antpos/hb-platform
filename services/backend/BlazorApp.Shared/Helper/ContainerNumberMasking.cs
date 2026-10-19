using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace BlazorApp.Shared.Helper
{
    /// <summary>
    /// 货柜编号（如 FFAU8228793）遮罩：默认只露后 4 位，其余用 * 覆盖。
    /// 没有「查看完整货柜编号」权限的账号，接口返回与导出文件里一律是遮罩值。
    /// </summary>
    public static class ContainerNumberMasking
    {
        public const int VisibleTailLength = 4;
        public const char MaskChar = '*';

        private static readonly AsyncLocal<bool> MaskingActive = new();

        /// <summary>当前异步流程（一次 HTTP 响应序列化）是否要遮罩货柜编号。</summary>
        public static bool IsMaskingActive => MaskingActive.Value;

        /// <summary>
        /// 只保留后 4 位，前面每个字符换成 *（保持原长度）；4 位及以内原样返回，空值原样返回。
        /// 已经遮罩过的值再遮罩结果不变，前端重复遮罩也安全。
        /// </summary>
        public static string? Mask(string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= VisibleTailLength)
            {
                return value;
            }

            return new string(MaskChar, value.Length - VisibleTailLength)
                + value[^VisibleTailLength..];
        }

        /// <summary>按是否有权限返回完整值或遮罩值。</summary>
        public static string? Apply(string? value, bool showFull) => showFull ? value : Mask(value);

        /// <summary>
        /// 文件名 / 工作表名里用的编号：去掉遮罩字符 *（Windows 文件名与 Excel 工作表名都不允许 *），
        /// 遮罩值只剩可见的后 4 位，完整编号保持不变。
        /// </summary>
        public static string? ToFileNamePart(string? value) =>
            value?.Replace(MaskChar.ToString(), string.Empty, StringComparison.Ordinal);

        /// <summary>是否是遮罩值（真实货柜编号不含 *）；用于拒绝把遮罩值当成新编号写回数据库。</summary>
        public static bool IsMasked(string? value) =>
            !string.IsNullOrEmpty(value) && value.Contains(MaskChar);

        /// <summary>
        /// 开启遮罩作用域：作用域内序列化带 <see cref="ContainerNumberJsonConverter"/> 的属性时输出遮罩值。
        /// 只在接口结果过滤器里、包住响应序列化时使用；Dispose 后恢复原值。
        /// </summary>
        public static IDisposable BeginMaskingScope(bool mask)
        {
            var previous = MaskingActive.Value;
            MaskingActive.Value = mask;
            return new Scope(previous);
        }

        private sealed class Scope(bool previous) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                MaskingActive.Value = previous;
            }
        }
    }

    /// <summary>
    /// 货柜编号属性专用 JSON 转换器：只在遮罩作用域内输出遮罩值，读取与作用域外（后台任务、内部序列化）原样。
    /// 遮罩发生在序列化阶段，不修改 DTO 对象本身，因此内存缓存里的共享对象不会被污染。
    /// </summary>
    public sealed class ContainerNumberJsonConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            var output = ContainerNumberMasking.IsMaskingActive ? ContainerNumberMasking.Mask(value) : value;
            if (output == null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(output);
        }
    }
}
