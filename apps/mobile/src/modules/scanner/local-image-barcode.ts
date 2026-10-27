import { restoreCameraBarcodeData } from "@/modules/scanner/camera-barcode-data";

/** 相册图片识别的一条原始结果，字段与 expo-camera 的 BarcodeScanningResult 子集一致。 */
export interface LocalImageBarcodeHit {
  type: string;
  data: string;
}

export type LocalImageScanFailure = "no-barcode" | "multiple" | "read-failed";

export type LocalImageScanOutcome =
  | { ok: true; hit: LocalImageBarcodeHit }
  | { ok: false; reason: LocalImageScanFailure };

/** 注入式依赖：测试可替换，运行时由 local-image-barcode-runtime 提供。 */
export interface LocalImageScanner {
  scan(uri: string): Promise<LocalImageBarcodeHit[]>;
}

/**
 * 识别一张本地图片并收敛成「恰好一个条码」：
 * - 图中没有条码 -> no-barcode；
 * - 有多个不同条码 -> multiple（货架照片等场景下猜一个会查错商品，宁可让用户裁剪重选）；
 * - 同一条码被重复识别（不同码制、iOS/Android 前导 0 差异）按还原后的值去重。
 * 返回的 hit 保持原始 type/data，由相机扫码入口统一做 trim 与前导 0 还原。
 */
export async function scanBarcodeFromImage(
  uri: string,
  scanner: LocalImageScanner
): Promise<LocalImageScanOutcome> {
  let hits: LocalImageBarcodeHit[];
  try {
    hits = await scanner.scan(uri);
  } catch {
    return { ok: false, reason: "read-failed" };
  }

  const distinct = new Map<string, LocalImageBarcodeHit>();
  for (const hit of hits) {
    const data = hit.data.trim();
    if (!data) {
      continue;
    }
    const key = restoreCameraBarcodeData(hit.type, data);
    if (!distinct.has(key)) {
      distinct.set(key, { type: hit.type, data });
    }
  }

  if (distinct.size === 0) {
    return { ok: false, reason: "no-barcode" };
  }
  if (distinct.size > 1) {
    return { ok: false, reason: "multiple" };
  }
  return { ok: true, hit: [...distinct.values()][0] };
}

export const LOCAL_IMAGE_FAILURE_MESSAGE_KEYS: Record<LocalImageScanFailure, string> = {
  "no-barcode": "scanner.imageNoBarcode",
  multiple: "scanner.imageMultipleBarcodes",
  "read-failed": "scanner.imageReadFailed",
};
