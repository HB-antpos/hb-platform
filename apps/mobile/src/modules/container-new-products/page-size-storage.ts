import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { CONTAINER_NEW_PRODUCTS_PAGE_SIZE, normalizePageSize, type ContainerNewProductsPageSize } from "./pagination";

/** 记住 HB新品 每页条数；读写失败只是不记住，不影响页面。 */
const STORAGE_KEY = "hb.containerNewProducts.pageSize";

let cached: ContainerNewProductsPageSize | null = null;

export async function readRememberedPageSize(): Promise<ContainerNewProductsPageSize> {
  if (cached) return cached;
  try {
    cached = normalizePageSize(await AppAsyncStorage.getString(STORAGE_KEY));
  } catch {
    cached = CONTAINER_NEW_PRODUCTS_PAGE_SIZE;
  }
  return cached;
}

/** 同步读内存缓存：再次进入页面时直接用，避免先按 50 渲染再跳。 */
export function peekRememberedPageSize() {
  return cached;
}

export function rememberPageSize(pageSize: ContainerNewProductsPageSize) {
  cached = pageSize;
  void AppAsyncStorage.setString(STORAGE_KEY, String(pageSize)).catch(() => undefined);
}
