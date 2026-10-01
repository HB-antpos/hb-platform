import { AppAsyncStorage } from "@/shared/storage/async-storage";
import type { LogSource } from "./types";

/** 记住上次查看的数据来源（老收银 / 新收银）；读写失败只是不记住，不影响页面。 */
const STORAGE_KEY = "hb.employeeLogs.source";

let cached: LogSource | null = null;

export async function readRememberedLogSource(): Promise<LogSource | null> {
  if (cached) return cached;
  try {
    const value = await AppAsyncStorage.getString(STORAGE_KEY);
    cached = value === "legacy" || value === "pos" ? value : null;
  } catch {
    cached = null;
  }
  return cached;
}

/** 同步读内存缓存：首屏渲染时用，避免先闪一下默认来源。 */
export function peekRememberedLogSource() {
  return cached;
}

export function rememberLogSource(source: LogSource) {
  cached = source;
  void AppAsyncStorage.setString(STORAGE_KEY, source).catch(() => undefined);
}
