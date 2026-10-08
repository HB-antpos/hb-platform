import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { normalizePageSize } from "./pagination-logic";

/** 存储适配器：默认走 AppAsyncStorage，测试里可注入内存实现。 */
export interface PageSizeStorageAdapter {
  getString(key: string): Promise<string | null>;
  setString(key: string, value: string): Promise<void>;
}

export interface PageSizeStorage<T extends number> {
  /** 读取本机记住的每页条数；读失败 / 值不在 options 里回到 defaultSize，并写入内存缓存。 */
  read(): Promise<T>;
  /** 同步读内存缓存：再次进入页面时直接用，避免先按默认值渲染再跳；还没读过返回 null。 */
  peek(): T | null;
  /** 记住选择：先更新内存缓存，再异步落盘；落盘失败只是不记住，不影响页面。 */
  remember(pageSize: T): void;
}

export interface CreatePageSizeStorageOptions<T extends number> {
  /** AsyncStorage key；沿用旧页面的 key 才不会重置用户已保存的选择 */
  key: string;
  options: readonly T[];
  defaultSize: T;
  storage?: PageSizeStorageAdapter;
}

/** 生成「记住每页条数」的读写器，每个页面一份（缓存互相独立）。 */
export function createPageSizeStorage<T extends number>({ key, options, defaultSize, storage = AppAsyncStorage }: CreatePageSizeStorageOptions<T>): PageSizeStorage<T> {
  let cached: T | null = null;

  return {
    async read() {
      if (cached !== null) return cached;
      try {
        cached = normalizePageSize(await storage.getString(key), options, defaultSize);
      } catch {
        cached = defaultSize;
      }
      return cached;
    },
    peek() {
      return cached;
    },
    remember(pageSize) {
      cached = pageSize;
      void Promise.resolve()
        .then(() => storage.setString(key, String(pageSize)))
        .catch(() => undefined);
    },
  };
}
