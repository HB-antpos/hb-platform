/** 已写入字节与服务端已验证总大小；只在 APK 下载中有意义。 */
export type ApkDownloadProgress = Readonly<{
  bytesWritten: number;
  totalBytes: number;
}>;

const BYTES_PER_MEGABYTE = 1024 * 1024;

/**
 * 只在整数百分比前进时转发，写满必转发一次。原生侧已按时间节流，
 * 这里再兜一层，保证一次下载最多约 101 次界面更新。
 */
export function createApkDownloadProgressForwarder(
  totalBytes: number,
  onProgress: (progress: ApkDownloadProgress) => void,
): (bytesWritten: number) => void {
  let lastPercent = -1;
  return (bytesWritten) => {
    if (!Number.isFinite(bytesWritten) || !(totalBytes > 0)) return;
    const written = Math.min(Math.max(Math.floor(bytesWritten), 0), totalBytes);
    const percent = Math.floor((written * 100) / totalBytes);
    if (percent <= lastPercent) return;
    lastPercent = percent;
    onProgress(Object.freeze({ bytesWritten: written, totalBytes }));
  };
}

/** 进度文案与语言无关：「27% · 12.3 / 45.6 MB」。用 toFixed 避免依赖 Hermes Intl。 */
export function describeApkDownloadProgress(progress: ApkDownloadProgress): Readonly<{
  percent: number;
  label: string;
}> {
  const total = Math.max(progress.totalBytes, 0);
  const written = Math.min(Math.max(progress.bytesWritten, 0), total);
  const percent = total > 0 ? Math.floor((written * 100) / total) : 0;
  const toMegabytes = (bytes: number) => (bytes / BYTES_PER_MEGABYTE).toFixed(1);
  return Object.freeze({
    percent,
    label: `${percent}% · ${toMegabytes(written)} / ${toMegabytes(total)} MB`,
  });
}
