// 复核图片读取后的系统 HTTP 缓存清理（原生实现）：把 expo-file-system 接成 http-cache-scrub 的文件系统端口。
// 只在 iOS 启用（背景与局限见 http-cache-scrub.ts）；其它平台直通，不做任何文件操作。
import { Platform } from "react-native";
import { createImageCacheScrubber, type CacheFsPort, type ImageCacheScrubber } from "./http-cache-scrub";

const PASSTHROUGH: ImageCacheScrubber = {
  wrap: (fetcher) => fetcher(),
  release: () => Promise.resolve(),
  pendingCount: () => 0,
};

let fileSystemPromise: Promise<typeof import("expo-file-system/legacy")> | null = null;
const loadFileSystem = () => (fileSystemPromise ??= import("expo-file-system/legacy"));

function createFileSystemPort(): CacheFsPort {
  return {
    getCacheDirectory: async () => (await loadFileSystem()).cacheDirectory,
    listDirectory: async (uri) => (await loadFileSystem()).readDirectoryAsync(uri),
    getFileInfo: async (uri) => {
      const info = await (await loadFileSystem()).getInfoAsync(uri);
      if (!info.exists) return { exists: false };
      return {
        exists: true,
        isDirectory: info.isDirectory,
        // modificationTime 单位是秒（可带小数）。
        modifiedAtMs: typeof info.modificationTime === "number" ? info.modificationTime * 1000 : undefined,
      };
    },
    readHeadBase64: async (uri, length) => {
      const fileSystem = await loadFileSystem();
      return fileSystem.readAsStringAsync(uri, { encoding: fileSystem.EncodingType.Base64, position: 0, length });
    },
    deleteFile: async (uri) => (await loadFileSystem()).deleteAsync(uri, { idempotent: true }),
  };
}

/** 为一个复核弹框会话创建清理器；弹框关闭时调用 release()。 */
export function createReviewImageScrubber(): ImageCacheScrubber {
  if (Platform.OS !== "ios") return PASSTHROUGH;
  return createImageCacheScrubber(createFileSystemPort(), {
    setTimeout: (callback, ms) => setTimeout(callback, ms),
    clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    now: () => Date.now(),
  });
}
