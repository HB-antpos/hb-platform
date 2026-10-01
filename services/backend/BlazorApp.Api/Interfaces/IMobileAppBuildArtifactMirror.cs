using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Interfaces
{
    public sealed class MobileAppBuildArtifactMirrorResult
    {
        public string ArtifactUrl { get; set; } = string.Empty;

        public string ObjectKey { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public long FileSize { get; set; }

        public DateTime MirroredAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class MobileAppBuildArtifactChecksum
    {
        public string Sha256 { get; set; } = string.Empty;

        public long FileSize { get; set; }
    }

    public sealed class MobileAppBuildArtifactMirrorException : InvalidOperationException
    {
        public MobileAppBuildArtifactMirrorException(string message, bool isDownloadUnsafe = false)
            : base(message)
        {
            IsDownloadUnsafe = isDownloadUnsafe;
        }

        public bool IsDownloadUnsafe { get; }
    }

    public interface IMobileAppBuildArtifactMirror
    {
        Task<MobileAppBuildArtifactMirrorResult> MirrorAsync(
            MobileAppBuild build,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 读取已镜像到 COS 的对象并计算 SHA-256 与字节数，用于补齐“镜像成功但缺校验值”的旧记录。
        /// </summary>
        Task<MobileAppBuildArtifactChecksum> ComputeMirroredChecksumAsync(
            MobileAppBuild build,
            CancellationToken cancellationToken = default
        );
    }
}
