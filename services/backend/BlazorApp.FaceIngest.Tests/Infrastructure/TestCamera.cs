using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>
/// 测试用摄像机。token 每次运行随机生成（假数据），保证「文件 / 日志里不含 token 原文」的断言有意义。
/// </summary>
internal sealed record TestCamera(string CameraId, string StoreCode, string Token, bool Enabled = true)
{
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static TestCamera Create(string cameraId, string storeCode = "S1001", bool enabled = true)
        => new(cameraId, storeCode, NewToken(), enabled);

    public string TokenSha256 => HashOf(Token);

    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>生成 FaceIngest:Cameras:{index}:* 配置键。</summary>
    public IEnumerable<KeyValuePair<string, string?>> ToSettings(int index, string? tokenSha256Override = null)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"FaceIngest:Cameras:{index}:");
        yield return new(prefix + "CameraId", CameraId);
        yield return new(prefix + "StoreCode", StoreCode);
        yield return new(prefix + "TokenSha256", tokenSha256Override ?? TokenSha256);
        yield return new(prefix + "Enabled", Enabled ? "true" : "false");
    }

    /// <summary>把一组摄像机转成配置字典。</summary>
    public static Dictionary<string, string?> ToSettings(params TestCamera[] cameras)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cameras.Length; i++)
        {
            foreach (var pair in cameras[i].ToSettings(i))
            {
                settings[pair.Key] = pair.Value;
            }
        }

        return settings;
    }
}
