import type { NativeAppDownloadProgress } from "./native-app-update";

const BYTES_PER_MEGABYTE = 1024 * 1024;

/** 进度文案与语言无关：「27% · 12.3 / 45.6 MB」。用 toFixed 避免依赖 Hermes Intl。 */
export function describeNativeAppDownloadProgress(progress: NativeAppDownloadProgress) {
  const total = Math.max(progress.totalBytes, 0);
  const written = Math.min(Math.max(progress.bytesWritten, 0), total);
  const percent = total > 0 ? Math.floor((written * 100) / total) : 0;
  const toMegabytes = (bytes: number) => (bytes / BYTES_PER_MEGABYTE).toFixed(1);
  return {
    percent,
    label: `${percent}% · ${toMegabytes(written)} / ${toMegabytes(total)} MB`,
  };
}
