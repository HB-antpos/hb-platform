import { Platform } from "react-native";
import { scanFromURLAsync, type BarcodeType } from "expo-camera";
import HBImageBarcode from "../../../modules/hb-image-barcode/src/HBImageBarcodeModule";
import type { LocalImageScanner } from "@/modules/scanner/local-image-barcode";

// 与相机实时扫码一致的全码制范围；expo-camera 默认只认 qr，必须显式传入。
const ALL_BARCODE_TYPES: BarcodeType[] = [
  "ean13",
  "ean8",
  "upc_a",
  "upc_e",
  "code128",
  "code39",
  "code93",
  "itf14",
  "codabar",
  "qr",
  "pdf417",
  "datamatrix",
  "aztec",
];

/**
 * 相册识别能力：
 * - iOS：expo-camera 的 scanFromURLAsync 只会找二维码，商品一维码必须走自带 Vision 模块，旧原生包没有它就不提供入口；
 * - Android：scanFromURLAsync 走 ML Kit，传入全码制即可读一维码。
 */
export function canScanLocalImage(): boolean {
  if (Platform.OS === "ios") {
    return HBImageBarcode != null;
  }
  return Platform.OS === "android";
}

export const localImageScanner: LocalImageScanner = {
  async scan(uri) {
    if (Platform.OS === "ios") {
      if (!HBImageBarcode) {
        throw new Error("HBImageBarcode native module is unavailable");
      }
      return HBImageBarcode.scanImage(uri);
    }
    const results = await scanFromURLAsync(uri, ALL_BARCODE_TYPES);
    return results.map(({ type, data }) => ({ type, data }));
  },
};

export type PickedLocalImage = { status: "picked"; uri: string } | { status: "canceled" };

/** 打开系统相册选一张图；系统相册选择器无需额外相册权限（见 expo-image-picker 文档）。 */
export async function pickLocalImage(): Promise<PickedLocalImage> {
  const ImagePicker = await import("expo-image-picker");
  const result = await ImagePicker.launchImageLibraryAsync({
    mediaTypes: ["images"],
    allowsMultipleSelection: false,
    allowsEditing: false,
    quality: 1,
    exif: false,
  });
  const uri = result.assets?.[0]?.uri;
  if (result.canceled || !uri) {
    return { status: "canceled" };
  }
  return { status: "picked", uri };
}
