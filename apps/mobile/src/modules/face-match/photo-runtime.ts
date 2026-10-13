// 参考照片的原生实现：expo-image-manipulator 压缩 + expo-file-system 删除临时文件，注入给 photo-processing 的纯流程。
// 照片字节只以 base64 字符串返回给调用方放进内存；选图器与压缩器在应用缓存目录留下的临时文件在流程结束时立即删除。
import { processPickedPhoto, toPhotoDraft, type PhotoProcessingDeps, type PickedPhotoAsset } from "./photo-processing";
import type { FacePhotoDraft } from "./types";

/** 只允许删除应用缓存目录里的文件（选图器、压缩器的产物都在那里），绝不碰相册里的原图。 */
export function isAppCacheFile(uri: string, cacheDirectory: string | null | undefined): boolean {
  if (!cacheDirectory || !uri.startsWith(cacheDirectory)) return false;
  return !uri.slice(cacheDirectory.length).split("/").includes("..");
}

async function createDeps(): Promise<PhotoProcessingDeps> {
  const [manipulator, fileSystem] = await Promise.all([
    import("expo-image-manipulator"),
    import("expo-file-system/legacy"),
  ]);
  return {
    manipulate: async (uri, actions, options) => {
      const result = await manipulator.manipulateAsync(uri, [...actions], {
        compress: options.compress,
        format: manipulator.SaveFormat.JPEG,
        base64: true,
      });
      return { uri: result.uri, width: result.width, height: result.height, base64: result.base64 };
    },
    deleteTemp: async (uri) => {
      if (!isAppCacheFile(uri, fileSystem.cacheDirectory)) return;
      try {
        await fileSystem.deleteAsync(uri, { idempotent: true });
      } catch {
        // 删除失败不影响流程；缓存目录会被系统在空间紧张时清理。
      }
    },
  };
}

async function resolveMissingSize(asset: PickedPhotoAsset): Promise<PickedPhotoAsset> {
  if ((asset.width ?? 0) > 0 && (asset.height ?? 0) > 0) return asset;
  const { Image } = await import("react-native");
  try {
    const size = await new Promise<{ width: number; height: number }>((resolve, reject) => {
      Image.getSize(asset.uri, (width, height) => resolve({ width, height }), reject);
    });
    return { ...asset, ...size };
  } catch {
    return asset;
  }
}

/** 压缩一张选中的照片并返回内存里的草稿；失败抛 FacePhotoError（code 对应 i18n 提示）。 */
export async function processPhotoForUpload(asset: PickedPhotoAsset): Promise<FacePhotoDraft> {
  const deps = await createDeps();
  const processed = await processPickedPhoto(await resolveMissingSize(asset), deps);
  return toPhotoDraft(processed);
}

/** 丢弃一张用不上的选中资源（超出张数等）：只删临时文件。 */
export async function discardPickedAsset(asset: PickedPhotoAsset): Promise<void> {
  const deps = await createDeps();
  await deps.deleteTemp(asset.uri);
}
