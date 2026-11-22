import type { LabelGraphicsMode } from "./label-doc";

export type JsLabelKind = "product" | "discount" | "clearance" | "bigDiscount" | "warehouseProduct" | "warehouseLocation";

/**
 * 每种标签是否改走 JS 排版（P1：全部关闭，线上仍是原生排版）。
 * 真机对纸确认某种标签与旧版一致后，把它改成 true 发 OTA 即可；有问题改回 false 再发 OTA 回退。
 * 只在新原生包（getLabelRenderInfo 可用）上生效，旧包始终走原生排版。
 */
export const JS_LABEL_LAYOUT: Readonly<Record<JsLabelKind, boolean>> = {
  product: false,
  discount: false,
  clearance: false,
  bigDiscount: false,
  warehouseProduct: false,
  warehouseLocation: false,
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

export function isJsLabelLayoutEnabled(kind: JsLabelKind) {
  return overrides.layout?.[kind] ?? JS_LABEL_LAYOUT[kind];
}

export function jsLabelGraphicsMode(): LabelGraphicsMode {
  return overrides.graphicsMode ?? JS_LABEL_GRAPHICS_MODE;
}
