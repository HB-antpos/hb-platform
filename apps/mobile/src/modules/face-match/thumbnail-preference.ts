// 「抓拍列表缩略图是否显示」的本机偏好（眼睛按钮一键隐藏 / 显示，记住上次的选择）。
// 只存一个布尔值（"1" / "0"），不含任何人脸数据；这是本模块里唯一允许使用 AsyncStorage 的文件（source-contract 守住）。
// 读写失败只是不记住，不影响页面；默认显示。
import { AppAsyncStorage } from "@/shared/storage/async-storage";

export const THUMBNAILS_VISIBLE_STORAGE_KEY = "faceMatch.thumbnails.visible";

let cached: boolean | null = null;

/** 只有明确存了 "0" 才是隐藏；没存过、存坏了都按默认「显示」。 */
export function parseThumbnailsVisible(raw: string | null | undefined): boolean {
  return raw !== "0";
}

export async function readThumbnailsVisible(): Promise<boolean> {
  if (cached !== null) return cached;
  let loaded: boolean;
  try {
    loaded = parseThumbnailsVisible(await AppAsyncStorage.getString(THUMBNAILS_VISIBLE_STORAGE_KEY));
  } catch {
    loaded = true;
  }
  // 读取期间用户可能已经点过眼睛（remember 写了缓存），以最新选择为准，不被旧值覆盖。
  cached ??= loaded;
  return cached;
}

/** 同步读内存缓存：再次进入页面时直接用；从未读过返回 null（此时页面要等读完再决定要不要加载缩略图）。 */
export function peekThumbnailsVisible(): boolean | null {
  return cached;
}

export function rememberThumbnailsVisible(visible: boolean): void {
  cached = visible;
  void AppAsyncStorage.setString(THUMBNAILS_VISIBLE_STORAGE_KEY, visible ? "1" : "0").catch(() => undefined);
}

/** 仅供测试：清空内存缓存，模拟冷启动。 */
export function resetThumbnailsVisibleCache(): void {
  cached = null;
}
