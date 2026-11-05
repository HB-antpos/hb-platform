namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 登录响应数据传输对象，用于返回登录结果和用户信息
    /// </summary>
    public class LoginResponse
    {
        /// <summary>
        /// 登录是否成功
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// 访问令牌
        /// </summary>
        public string Token { get; set; } = string.Empty;
        
        /// <summary>
        /// 响应消息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 失败原因代码（见 <see cref="LoginErrorCodes"/>）。成功时为 null；旧调用方不读它，行为不变。
        /// </summary>
        public string? ErrorCode { get; set; }

        /// <summary>
        /// 登录用户信息
        /// </summary>
        public LoginUserDto? User { get; set; }
    }

    /// <summary>登录失败的原因代码。控制器据此决定对外说什么、哪些失败计入限流。</summary>
    public static class LoginErrorCodes
    {
        /// <summary>用户名或密码错误（含账号不存在）：只有这一类计入登录限流。</summary>
        public const string InvalidCredentials = "INVALID_CREDENTIALS";

        /// <summary>密码正确但账号未开通登录。</summary>
        public const string LoginRestricted = "LOGIN_RESTRICTED";

        /// <summary>密码正确，但这是邮件发出的临时密码且已超过有效期，须由店长重新发送。</summary>
        public const string TempPasswordExpired = "TEMP_PASSWORD_EXPIRED";

        /// <summary>登录失败次数过多，处于锁定期（由控制器在验证密码之前返回）。</summary>
        public const string RateLimited = "LOGIN_RATE_LIMITED";

        /// <summary>登录过程中出现未预期的异常。</summary>
        public const string Error = "LOGIN_ERROR";
    }

    /// <summary>
    /// 登录用户信息数据传输对象，包含登录成功后返回的基本用户信息
    /// </summary>
    public class LoginUserDto
    {
        /// <summary>
        /// 用户唯一标识符
        /// </summary>
        public string UserGUID { get; set; } = string.Empty;
        
        /// <summary>
        /// 用户名
        /// </summary>
        public string Username { get; set; } = string.Empty;
        
        /// <summary>
        /// 邮箱地址
        /// </summary>
        public string Email { get; set; } = string.Empty;
        
        /// <summary>
        /// 用户全名
        /// </summary>
        public string? FullName { get; set; }
    }
}
