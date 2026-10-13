// 参考照片的选取校验与压缩流程（纯逻辑，原生能力全部由调用方注入，可在 Node 下测试）。
//
// 隐私约束（参考照片是生物识别信息）：
// 1. 压缩后的 JPEG 字节与缩略图只以 base64 字符串的形式存在于内存（组件 state），不写磁盘缓存、不进日志；
// 2. 系统选图器 / 相机会在应用缓存目录留下临时文件，压缩一结束（无论成功失败）立即删除所有临时文件；
// 3. 重新编码为 JPEG 同时丢掉 EXIF（含定位）并把 iOS 的 HEIC 统一转成 JPEG，保证服务端只会收到 JPG。
import { PHOTO_MAX_BYTES, UNWELCOME_PHOTO_MAX } from "./constants";
import type { FacePhotoDraft } from "./types";

/** 选图器返回的资源（只取需要的字段）。 */
export interface PickedPhotoAsset {
  uri: string;
  width?: number | null;
  height?: number | null;
  mimeType?: string | null;
  fileName?: string | null;
  fileSize?: number | null;
}

export type PhotoIssueCode = "unsupportedType" | "sourceTooLarge" | "decodeFailed" | "tooLarge" | "tooMany";

export class FacePhotoError extends Error {
  constructor(public readonly code: PhotoIssueCode) {
    super(code);
  }
}

/** 允许的来源格式：JPG / PNG，以及 iOS 相册里常见的 HEIC / HEIF（压缩时统一转成 JPEG）。 */
const ACCEPTED_MIME = new Set(["image/jpeg", "image/jpg", "image/png", "image/heic", "image/heif"]);
const ACCEPTED_EXTENSIONS = new Set(["jpg", "jpeg", "png", "heic", "heif"]);

/** 来源文件的体积上限：防止把一张巨大的原图整张读入内存；正常手机照片远小于它。 */
export const PHOTO_SOURCE_MAX_BYTES = 40 * 1024 * 1024;

function extensionOf(fileName: string | null | undefined): string {
  const name = (fileName ?? "").toLowerCase();
  const dot = name.lastIndexOf(".");
  return dot >= 0 ? name.slice(dot + 1) : "";
}

/**
 * 选取阶段的当场校验（压缩之前）：类型不是 JPG/PNG/HEIC、来源体积离谱，立即提示。
 * 类型以选图器给的 mimeType 为准，缺失时看扩展名；两者都没有时放行，交给解码步骤判断（解码失败同样会提示）。
 */
export function checkPickedAsset(asset: PickedPhotoAsset): PhotoIssueCode | null {
  const mime = (asset.mimeType ?? "").trim().toLowerCase();
  const extension = extensionOf(asset.fileName);
  if (mime) {
    if (!ACCEPTED_MIME.has(mime)) return "unsupportedType";
  } else if (extension && !ACCEPTED_EXTENSIONS.has(extension)) {
    return "unsupportedType";
  }
  if (typeof asset.fileSize === "number" && asset.fileSize > PHOTO_SOURCE_MAX_BYTES) return "sourceTooLarge";
  return null;
}

/** base64 解码后的字节数（不实际解码）。 */
export function estimateBase64Bytes(base64: string): number {
  if (!base64) return 0;
  const padding = base64.endsWith("==") ? 2 : base64.endsWith("=") ? 1 : 0;
  return Math.floor((base64.length * 3) / 4) - padding;
}

/** 一次还能再加几张。 */
export function remainingPhotoSlots(currentCount: number, max: number = UNWELCOME_PHOTO_MAX): number {
  return Math.max(0, max - currentCount);
}

/** 压缩档位：长边上限与 JPEG 质量，从高到低逐级回退，直到 ≤ 5MB。 */
export const PHOTO_PROCESSING_LEVELS: readonly { maxSide: number; quality: number }[] = [
  { maxSide: 2048, quality: 0.85 },
  { maxSide: 1600, quality: 0.72 },
  { maxSide: 1280, quality: 0.6 },
];
export const PHOTO_THUMBNAIL = { maxSide: 480, quality: 0.6 } as const;

export interface ManipulatedImage {
  uri: string;
  width: number;
  height: number;
  base64?: string;
}

export interface PhotoProcessingDeps {
  /** 缩放 + 编码为 JPEG 并要求返回 base64（原生实现会在缓存目录写一个结果文件，由本流程负责删除）。 */
  manipulate: (
    uri: string,
    actions: readonly { resize: { width?: number; height?: number } }[],
    options: { compress: number },
  ) => Promise<ManipulatedImage>;
  /** 尽力删除一个临时文件；实现自己负责只删应用缓存目录内的文件，且不抛错。 */
  deleteTemp: (uri: string) => Promise<void>;
}

function resizeActions(width: number, height: number, maxSide: number) {
  if (Math.max(width, height) <= maxSide) return [];
  return width >= height ? [{ resize: { width: maxSide } }] : [{ resize: { height: maxSide } }];
}

export interface ProcessedPhoto {
  base64: string;
  byteSize: number;
  width: number;
  height: number;
  previewDataUri: string;
}

/**
 * 压缩一张选中的照片：转 JPEG、按长边缩放、逐级降质量直到 ≤ 5MB，另生成一张小缩略图用于预览。
 * 任何路径（成功、失败、抛错）都会删除选图器给的临时文件和本流程产生的全部临时文件。
 */
export async function processPickedPhoto(
  asset: PickedPhotoAsset,
  deps: PhotoProcessingDeps,
): Promise<ProcessedPhoto> {
  const issue = checkPickedAsset(asset);
  if (issue) {
    await deps.deleteTemp(asset.uri).catch(() => undefined);
    throw new FacePhotoError(issue);
  }
  const temps = new Set<string>([asset.uri]);
  try {
    const sourceWidth = asset.width && asset.width > 0 ? asset.width : 0;
    const sourceHeight = asset.height && asset.height > 0 ? asset.height : 0;

    let best: ManipulatedImage | null = null;
    for (const level of PHOTO_PROCESSING_LEVELS) {
      let result: ManipulatedImage;
      try {
        result = await deps.manipulate(asset.uri, resizeActions(sourceWidth, sourceHeight, level.maxSide), {
          compress: level.quality,
        });
      } catch {
        throw new FacePhotoError("decodeFailed");
      }
      temps.add(result.uri);
      if (!result.base64) throw new FacePhotoError("decodeFailed");
      const size = estimateBase64Bytes(result.base64);
      if (size > 0 && size <= PHOTO_MAX_BYTES) {
        best = result;
        break;
      }
    }
    if (!best || !best.base64) throw new FacePhotoError("tooLarge");

    let thumbnail: ManipulatedImage;
    try {
      thumbnail = await deps.manipulate(
        best.uri,
        resizeActions(best.width, best.height, PHOTO_THUMBNAIL.maxSide),
        { compress: PHOTO_THUMBNAIL.quality },
      );
    } catch {
      throw new FacePhotoError("decodeFailed");
    }
    temps.add(thumbnail.uri);
    if (!thumbnail.base64) throw new FacePhotoError("decodeFailed");

    return {
      base64: best.base64,
      byteSize: estimateBase64Bytes(best.base64),
      width: best.width,
      height: best.height,
      previewDataUri: `data:image/jpeg;base64,${thumbnail.base64}`,
    };
  } finally {
    await Promise.all([...temps].map((uri) => deps.deleteTemp(uri).catch(() => undefined)));
  }
}

let photoKeySequence = 0;

/** 内存里的照片编号，只在本页使用，不含文件名等内容。 */
export function createPhotoKey(): string {
  photoKeySequence += 1;
  return `face-photo-${photoKeySequence}`;
}

export function toPhotoDraft(processed: ProcessedPhoto, key: string = createPhotoKey()): FacePhotoDraft {
  return { key, ...processed };
}
