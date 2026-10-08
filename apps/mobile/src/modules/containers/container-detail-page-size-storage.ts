import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { CONTAINER_DETAIL_DEFAULT_PAGE_SIZE, normalizeContainerDetailPageSize } from "./query";
import type { ContainerDetailPageSize } from "./types";

/** 记住货柜明细每页条数；读写失败只是不记住，不影响页面。 */
export const CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY = "containers.detail.pageSize.v1";

let cached: ContainerDetailPageSize | null = null;

export async function readRememberedContainerDetailPageSize(): Promise<ContainerDetailPageSize> {
  if (cached) return cached;
  let loaded: ContainerDetailPageSize;
  try {
    loaded = normalizeContainerDetailPageSize(await AppAsyncStorage.getString(CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY));
  } catch {
    loaded = CONTAINER_DETAIL_DEFAULT_PAGE_SIZE;
  }
  // 读取期间用户可能已经改过每页条数（remember 写了缓存），此时以最新选择为准，不被旧值覆盖。
  cached ??= loaded;
  return cached;
}

/** 同步读内存缓存：再次进入页面时直接用，避免先按默认值渲染再跳；从未读过时返回 null。 */
export function peekRememberedContainerDetailPageSize() {
  return cached;
}

export function rememberContainerDetailPageSize(pageSize: ContainerDetailPageSize) {
  cached = normalizeContainerDetailPageSize(pageSize);
  void AppAsyncStorage.setString(CONTAINER_DETAIL_PAGE_SIZE_STORAGE_KEY, String(cached)).catch(() => undefined);
}

/** 仅供测试：清空内存缓存，模拟冷启动。 */
export function resetRememberedContainerDetailPageSizeCache() {
  cached = null;
}
