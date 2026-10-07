// 存单 / 收据照片压缩：长边 ≤ 2048、JPEG、≤ 5 MiB。
// 直接复用员工档案的「证件照」处理链（只按长边缩放不裁剪，质量 0.85 → 0.72 → 0.6 逐级回退，
// 最后仍超过 5 MiB 才报错）——票据四边都有内容，同样不能裁。
import {
  EmployeeProfileImageProcessingError,
  processEmployeeProfileImage,
} from "@/modules/employee-profile/image-processing";
import { CASH_IMAGE_MAX_BYTES, CASH_IMAGE_MAX_SIDE } from "./constants";
import type { ProcessedPhotoInput } from "./photo-drafts";

export type CashImageProcessingErrorCode = "decode_failed" | "blob_read_failed" | "file_too_large";

export class CashImageProcessingError extends Error {
  constructor(public readonly code: CashImageProcessingErrorCode) {
    super(code);
  }
}

export interface CashImageSource {
  uri: string;
  width?: number | null;
  height?: number | null;
}

type EmployeeProcessingDependencies = NonNullable<Parameters<typeof processEmployeeProfileImage>[1]>;

export interface CashImageProcessingDependencies extends EmployeeProcessingDependencies {
  /** 来源没带宽高时（个别相册返回 0）用它补；默认读 RN Image.getSize。 */
  resolveSize?: (uri: string) => Promise<{ width: number; height: number }>;
}

async function defaultResolveSize(uri: string): Promise<{ width: number; height: number }> {
  const { Image } = await import("react-native");
  return new Promise((resolve, reject) => {
    Image.getSize(uri, (width, height) => resolve({ width, height }), reject);
  });
}

function isPositive(value: number | null | undefined): value is number {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

/** 压缩一张照片，失败统一抛 CashImageProcessingError（code 对应 i18n 提示）。 */
export async function processCashPhoto(
  source: CashImageSource,
  dependencies: CashImageProcessingDependencies = {},
): Promise<ProcessedPhotoInput> {
  const { resolveSize = defaultResolveSize, ...processingDependencies } = dependencies;
  let width = source.width;
  let height = source.height;
  if (!isPositive(width) || !isPositive(height)) {
    try {
      ({ width, height } = await resolveSize(source.uri));
    } catch {
      throw new CashImageProcessingError("decode_failed");
    }
  }
  if (!isPositive(width) || !isPositive(height)) throw new CashImageProcessingError("decode_failed");

  try {
    const processed = await processEmployeeProfileImage(
      { kind: "identityPhoto", uri: source.uri, width, height },
      processingDependencies,
    );
    if (!processed) throw new CashImageProcessingError("decode_failed");
    return { uri: processed.uri, width: processed.width, height: processed.height, fileSize: processed.fileSize };
  } catch (error) {
    if (error instanceof CashImageProcessingError) throw error;
    if (error instanceof EmployeeProfileImageProcessingError) throw new CashImageProcessingError(error.code);
    throw new CashImageProcessingError("decode_failed");
  }
}

/** 压缩后的硬性上限，供测试与界面说明引用。 */
export const CASH_IMAGE_LIMITS = { maxBytes: CASH_IMAGE_MAX_BYTES, maxSide: CASH_IMAGE_MAX_SIDE } as const;
