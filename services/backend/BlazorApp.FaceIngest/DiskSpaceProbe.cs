namespace BlazorApp.FaceIngest;

/// <summary>
/// 查询存储所在盘的剩余空间。抽成接口是为了测试能注入「盘快满」的场景，也能整体关掉真实检查。
/// </summary>
internal interface IDiskSpaceProbe
{
    /// <summary>返回 <paramref name="path"/> 所在文件系统对当前用户可用的剩余字节；查询失败返回 null。</summary>
    long? GetAvailableFreeBytes(string path);
}

/// <summary>
/// 基于 <see cref="DriveInfo"/> 的实现。Linux 上 DriveInfo 接受任意目录路径（底层 statvfs），
/// 返回的是该目录所在挂载点的剩余空间，正好对应宿主机绑定挂载的存储盘。
/// </summary>
internal sealed class DriveInfoDiskSpaceProbe : IDiskSpaceProbe
{
    public long? GetAvailableFreeBytes(string path)
    {
        try
        {
            return new DriveInfo(path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
