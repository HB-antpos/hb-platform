import { requireOptionalNativeModule } from "expo";
import { Directory, Paths } from "expo-file-system";

import {
  createExternalDisplayBridge,
  type ExternalDisplayNativeModule,
} from "./external-display-bridge";
import { registerExternalDisplayReactSurface } from "./external-display-react-surface";

const nativeModule =
  requireOptionalNativeModule<ExternalDisplayNativeModule>(
    "HBExternalDisplay",
  );

registerExternalDisplayReactSurface(nativeModule);

export const customerDisplayAdvertisementCacheRootUri = new Directory(
  Paths.cache,
  "hb-pos-customer-display-ads",
).uri;

// 商品缩略图专用缓存目录（与广告缓存分开，淘汰/清理互不影响）。
export const customerDisplayProductImageCacheRootUri = new Directory(
  Paths.cache,
  "hb-pos-customer-display-product-images",
).uri;

/**
 * Expo Go、模拟器无外屏或未链接本地模块时保持显式 disconnected，不镜像主屏。
 */
export const externalDisplay = createExternalDisplayBridge({
  advertisementCacheRootUri:
    customerDisplayAdvertisementCacheRootUri,
  productImageCacheRootUri:
    customerDisplayProductImageCacheRootUri,
  nativeModule,
});
