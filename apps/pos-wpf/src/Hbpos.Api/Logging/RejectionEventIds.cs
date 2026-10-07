namespace Hbpos.Api.Logging;

/// <summary>
/// 收银端上传接口拒绝请求时使用的日志 EventId。
/// 中心日志只上传消息模板、不上传参数值，但 EventId 名称会单独入库，用它带上拒绝码便于按码统计；
/// 后端 ApplicationLogService 只接受 [A-Za-z0-9._:/+-]，错误码里的下划线必须换成连字符，否则整个 EventId 会被丢弃。
/// </summary>
internal static class RejectionEventIds
{
    public static EventId Create(string source, int statusCode, string code)
    {
        return new EventId(statusCode, $"{source}:{code.Replace('_', '-')}");
    }
}
