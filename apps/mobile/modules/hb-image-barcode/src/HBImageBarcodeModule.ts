import { requireOptionalNativeModule } from "expo-modules-core";

export type HBImageBarcodeHit = {
  /** 沿用 expo-camera 的条码类型命名（ean13 / code128 / qr ...）。 */
  type: string;
  data: string;
};

export type HBImageBarcodeNativeModule = {
  /** 用 iOS Vision 识别本地图片（file:// URI）里的全部条码。 */
  scanImage(uri: string): Promise<HBImageBarcodeHit[]>;
};

/**
 * 可选发现：只有新的 iOS 原生包带这个模块；旧包、Android、测试环境拿到 null，
 * 调用方必须据此降级或隐藏入口。
 */
export default requireOptionalNativeModule<HBImageBarcodeNativeModule>("HBImageBarcode");
