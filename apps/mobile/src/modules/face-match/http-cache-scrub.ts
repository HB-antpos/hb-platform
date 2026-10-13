// 清理系统 HTTP 缓存里的复核图片字节（仅 iOS，尽力而为的缓解，不是根治）。
//
// 背景（2026-10-13 在 iOS 模拟器实测）：RN 的网络层走 NSURLSession 默认配置，响应会被 NSURLCache 写进
// Library/Caches/<bundle id>/Cache.db 与 fsCachedData/ —— 即使请求和响应都带 Cache-Control: no-store、请求还带 Authorization，
// 也照样落盘（no-store 只约束「能否复用」，并不阻止写入）。JS 层没有任何选项能关掉它；根治需要原生改动
// （例如把 NSURLCache 的磁盘容量设为 0），而本次约定不动原生配置，所以这里先做缓解：
// 图片读取成功后，把 fsCachedData 里「读取时间窗内新写入、文件头是 JPEG / PNG」的缓存数据文件删掉。
// 已知局限（须在上线说明里讲清楚）：
// 1. Cache.db 里仍会留下请求地址（含任务编号）和响应头，没有图片字节；很小的响应（约 16KB 以内）系统直接内联进 Cache.db，删不到；
// 2. 清理是异步定时的，应用在清理前被强杀时，该次写入的文件会留到下一次系统回收缓存；
// 3. Android 的 OkHttp 默认没有磁盘缓存，不需要处理（未在真机核实）。
import { toByteArray } from "base64-js";

export interface CacheFsPort {
  /** 应用缓存目录（以 / 结尾）；取不到时什么都不删。 */
  getCacheDirectory: () => Promise<string | null>;
  /** 目录下的名字；目录不存在时抛错。 */
  listDirectory: (uri: string) => Promise<string[]>;
  getFileInfo: (uri: string) => Promise<{ exists: boolean; isDirectory?: boolean; modifiedAtMs?: number }>;
  /** 读文件头部 length 个字节，返回 base64。 */
  readHeadBase64: (uri: string, length: number) => Promise<string>;
  deleteFile: (uri: string) => Promise<void>;
}

/** 一次图片读取的时间窗（毫秒时间戳）：从发请求到拿到响应。 */
export interface ImageFetchWindow {
  startedAtMs: number;
  endedAtMs: number;
}

/** 文件修改时间与时间窗之间允许的误差：文件系统时间精度、系统异步写缓存的延迟。 */
export const CLOCK_SLACK_BEFORE_MS = 2_000;
export const CLOCK_SLACK_AFTER_MS = 10_000;
const CACHE_DATA_DIRECTORY = "fsCachedData";

function looksLikeImage(head: Uint8Array): boolean {
  const jpeg = head.length >= 3 && head[0] === 0xff && head[1] === 0xd8 && head[2] === 0xff;
  const png = head.length >= 4 && head[0] === 0x89 && head[1] === 0x50 && head[2] === 0x4e && head[3] === 0x47;
  return jpeg || png;
}

function inWindow(modifiedAtMs: number, window: ImageFetchWindow): boolean {
  return modifiedAtMs >= window.startedAtMs - CLOCK_SLACK_BEFORE_MS && modifiedAtMs <= window.endedAtMs + CLOCK_SLACK_AFTER_MS;
}

/** 删除时间窗内新写入的图片缓存文件，返回删除个数；任何文件系统错误都吞掉（缓存目录里的文件随时可能被系统回收）。 */
export async function scrubCachedImageBytes(fs: CacheFsPort, windows: readonly ImageFetchWindow[]): Promise<number> {
  if (windows.length === 0) return 0;
  let deleted = 0;
  let root = "";
  let names: string[];
  try {
    root = (await fs.getCacheDirectory()) ?? "";
    if (!root) return 0;
    names = await fs.listDirectory(root);
  } catch {
    return 0;
  }
  for (const name of names) {
    const directory = `${root}${name}/${CACHE_DATA_DIRECTORY}`;
    let files: string[];
    try {
      files = await fs.listDirectory(directory);
    } catch {
      continue;
    }
    for (const file of files) {
      const uri = `${directory}/${file}`;
      try {
        const info = await fs.getFileInfo(uri);
        if (!info.exists || info.isDirectory || typeof info.modifiedAtMs !== "number") continue;
        const modifiedAtMs = info.modifiedAtMs;
        if (!windows.some((window) => inWindow(modifiedAtMs, window))) continue;
        if (!looksLikeImage(toByteArray(await fs.readHeadBase64(uri, 8)))) continue;
        await fs.deleteFile(uri);
        deleted += 1;
      } catch {
        // 单个文件读不了 / 已被系统删掉：跳过。
      }
    }
  }
  return deleted;
}

/** 读取成功后的清理节奏：系统写缓存是异步的，所以在 0.5 / 2 / 6 秒各扫一遍；关闭时立即扫一遍，并在 1.5 秒后做最后一次。 */
export const SCRUB_DELAYS_MS = [500, 2_000, 6_000] as const;
export const FINAL_SCRUB_DELAY_MS = 1_500;

export interface ScrubTimers {
  setTimeout: (callback: () => void, ms: number) => unknown;
  clearTimeout: (handle: unknown) => void;
  now: () => number;
}

export interface ImageCacheScrubber {
  /** 包住一次图片读取：成功后登记时间窗并安排清理；失败（含取消）原样抛出，不登记。 */
  wrap: (fetcher: () => Promise<string>) => Promise<string>;
  /** 会话结束：取消未触发的定时清理，立即扫一遍，再在稍后做最后一次；之后不再持有任何登记信息。 */
  release: () => Promise<void>;
  pendingCount: () => number;
}

export function createImageCacheScrubber(fs: CacheFsPort, timers: ScrubTimers): ImageCacheScrubber {
  const windows: ImageFetchWindow[] = [];
  const handles = new Set<unknown>();
  let released = false;

  const schedule = (callback: () => void, delay: number) => {
    const handle = timers.setTimeout(() => {
      handles.delete(handle);
      callback();
    }, delay);
    handles.add(handle);
  };
  const sweep = (target: readonly ImageFetchWindow[]) => scrubCachedImageBytes(fs, target).catch(() => 0);

  return {
    async wrap(fetcher) {
      const startedAtMs = timers.now();
      const uri = await fetcher();
      const window: ImageFetchWindow = { startedAtMs, endedAtMs: timers.now() };
      if (released) {
        // 会话已结束后才返回的响应（调用方会丢弃它）：不再登记，只补一次清理，防止它的缓存文件漏掉。
        schedule(() => void sweep([window]), FINAL_SCRUB_DELAY_MS);
        return uri;
      }
      windows.push(window);
      for (const delay of SCRUB_DELAYS_MS) schedule(() => void sweep(windows), delay);
      return uri;
    },
    async release() {
      if (released) return;
      released = true;
      for (const handle of handles) timers.clearTimeout(handle);
      handles.clear();
      const pending = windows.splice(0, windows.length);
      if (pending.length === 0) return;
      // 弹框刚打开就关掉时系统可能还没写完缓存，所以立即扫一遍之后再补扫一次；补扫只持有时间戳，不含任何图片信息。
      schedule(() => void sweep(pending), FINAL_SCRUB_DELAY_MS);
      await sweep(pending);
    },
    pendingCount: () => windows.length,
  };
}
