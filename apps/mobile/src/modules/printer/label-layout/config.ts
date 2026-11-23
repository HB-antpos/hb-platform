import type { LabelGraphicsMode } from "./label-doc";

export type JsLabelKind = "product" | "discount" | "clearance" | "bigDiscount" | "warehouseProduct" | "warehouseLocation";

export type JsLabelPlatform = "android" | "ios";

/**
 * 每个平台、每种标签是否改走 JS 排版。真机对纸确认与旧版一致后改成 true 发 OTA；有问题改回 false 再发 OTA 回退。
 * 只在新原生包（getLabelRenderInfo 可用）上生效，旧包始终走原生排版。
 * - 安卓：2026-11-23 TC26 对纸包（每次先 JS 后原生两张）6 种全部一致，已全开；
 * - iOS：还没在 iPhone 上对纸，先保持原生排版，等 iOS 新包进 TestFlight 对纸后再开。
 */
export const JS_LABEL_LAYOUT: Readonly<Record<JsLabelPlatform, Readonly<Record<JsLabelKind, boolean>>>> = {
  android: {
    product: true,
    discount: true,
    clearance: true,
    bigDiscount: true,
    warehouseProduct: true,
    warehouseLocation: true,
  },
  ios: {
    product: false,
    discount: false,
    clearance: false,
    bigDiscount: false,
    warehouseProduct: false,
    warehouseLocation: false,
  },
};

/**
 * 位图下发方式：EG 十六进制（经 print，最稳妥）或 CG 二进制（经 writeBytes，数据量约减半）。
 * TC26 所连标签机 11-23 实测 CG 正常；其他型号未验证，默认 EG，确认后可改 CG 发 OTA。
 */
export const JS_LABEL_GRAPHICS_MODE: LabelGraphicsMode = "EG";

type JsLabelOverrides = {
  layout?: Partial<Record<JsLabelKind, boolean>>;
  graphicsMode?: LabelGraphicsMode;
};

// 运行时覆盖（测试，以及以后真机对纸时的临时开关）；不持久化，重启即恢复上面的常量。
let overrides: JsLabelOverrides = {};

export function setJsLabelOverrides(next: JsLabelOverrides) {
  overrides = { layout: { ...next.layout }, graphicsMode: next.graphicsMode };
}

/** platform 取 Platform.OS；安卓 / iOS 以外（web 等）一律不走 JS 排版。运行时覆盖不分平台（只供测试 / 对纸）。 */
export function isJsLabelLayoutEnabled(kind: JsLabelKind, platform: string) {
  const override = overrides.layout?.[kind];
  if (override !== undefined) return override;
  if (platform !== "android" && platform !== "ios") return false;
  return JS_LABEL_LAYOUT[platform][kind];
}

export function jsLabelGraphicsMode(): LabelGraphicsMode {
  return overrides.graphicsMode ?? JS_LABEL_GRAPHICS_MODE;
}
