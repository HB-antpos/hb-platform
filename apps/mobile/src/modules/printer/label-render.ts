/**
 * JS 排版用的原生文字渲染接口（新原生包才有）：类型、结果校验、base64 编解码与分批。
 * 原生约定见 docs/plans/2026-11-23-mobile-label-js-layout.md；这里只做纯函数，便于在 Node 下测试。
 */

/** 两端原生包内置的字体键（Roboto，Apache 2.0）；也可传 file:// 开头的字体文件绝对路径。 */
export const LABEL_FONTS = [
  "roboto-regular",
  "roboto-light",
  "roboto-medium",
  "roboto-bold",
  "roboto-black",
] as const;

export type LabelBundledFont = (typeof LABEL_FONTS)[number];
export type LabelFont = LabelBundledFont | `file://${string}`;

export type LabelRenderInfo = {
  version: number;
  fonts: string[];
  maxBatch: number;
  maxBitmapWidth: number;
  maxBitmapHeight: number;
  supportsFileFonts: boolean;
};

/** 当前 JS 需要的最低原生渲染版本。 */
export const LABEL_RENDER_MIN_VERSION = 1;

export type LabelTextItem = {
  text: string;
  font: LabelFont;
  /** 字号，单位为打印点（203dpi 下 1 点 ≈ 0.125mm）。 */
  size: number;
};

export type LabelRenderTextItem = LabelTextItem & {
  /** 黑底白字。 */
  inverse?: boolean;
  /** 四周留白（点）。 */
  padding?: number;
};

/** 以基线起点为原点、y 向下的浮点度量；inkTop 通常为负。 */
export type LabelTextMetrics = {
  advance: number;
  ascent: number;
  descent: number;
  inkLeft: number;
  inkTop: number;
  inkRight: number;
  inkBottom: number;
};

export type LabelInkBox = { left: number; top: number; right: number; bottom: number };

/** 1bit 位图：每行 rowBytes 字节、高位在左、1 = 黑，与 CPCL EG / CG 的数据布局相同。 */
export type LabelTextBitmap = {
  width: number;
  height: number;
  rowBytes: number;
  /** 基线起点在位图中的位置。 */
  originX: number;
  originY: number;
  /** 阈值化后实际像素的墨迹框（右、下不含）；空白文字为 null。 */
  ink: LabelInkBox | null;
  bits: Uint8Array;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object";
}

function finiteNumber(value: unknown, name: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`Printer render result has an invalid ${name}.`);
  }
  return value;
}

function nonNegativeInt(value: unknown, name: string): number {
  const number = finiteNumber(value, name);
  if (!Number.isInteger(number) || number < 0) {
    throw new Error(`Printer render result has an invalid ${name}.`);
  }
  return number;
}

/** 旧包、取不到或版本不够都返回 null，调用方据此回退到旧的原生排版。 */
export function normalizeLabelRenderInfo(raw: unknown): LabelRenderInfo | null {
  if (!isRecord(raw)) return null;
  const version = typeof raw.version === "number" ? raw.version : 0;
  if (!Number.isFinite(version) || version < LABEL_RENDER_MIN_VERSION) return null;
  const fonts = Array.isArray(raw.fonts) ? raw.fonts.filter((font): font is string => typeof font === "string") : [];
  const positive = (value: unknown, fallback: number) =>
    typeof value === "number" && Number.isFinite(value) && value > 0 ? Math.floor(value) : fallback;
  return {
    version,
    fonts,
    maxBatch: positive(raw.maxBatch, 64),
    maxBitmapWidth: positive(raw.maxBitmapWidth, 2048),
    maxBitmapHeight: positive(raw.maxBitmapHeight, 1024),
    supportsFileFonts: raw.supportsFileFonts === true,
  };
}

export function normalizeTextMetrics(raw: unknown): LabelTextMetrics {
  if (!isRecord(raw)) throw new Error("Printer measure result is not an object.");
  return {
    advance: finiteNumber(raw.advance, "advance"),
    ascent: finiteNumber(raw.ascent, "ascent"),
    descent: finiteNumber(raw.descent, "descent"),
    inkLeft: finiteNumber(raw.inkLeft, "inkLeft"),
    inkTop: finiteNumber(raw.inkTop, "inkTop"),
    inkRight: finiteNumber(raw.inkRight, "inkRight"),
    inkBottom: finiteNumber(raw.inkBottom, "inkBottom"),
  };
}

export function normalizeTextBitmap(raw: unknown): LabelTextBitmap {
  if (!isRecord(raw)) throw new Error("Printer render result is not an object.");
  const width = nonNegativeInt(raw.width, "width");
  const height = nonNegativeInt(raw.height, "height");
  if (width === 0 || height === 0) throw new Error("Printer render result is empty.");
  const rowBytes = Math.ceil(width / 8);
  if (typeof raw.bits !== "string") throw new Error("Printer render result has no bits.");
  const bits = decodeBase64(raw.bits);
  // 字节数必须严格等于 rowBytes × height，否则后续拼 EG / CG 会整张错位。
  if (bits.length !== rowBytes * height) {
    throw new Error(`Printer render result has ${bits.length} bytes, expected ${rowBytes * height}.`);
  }
  const ink =
    raw.hasInk === true
      ? {
          left: nonNegativeInt(raw.inkLeft, "inkLeft"),
          top: nonNegativeInt(raw.inkTop, "inkTop"),
          right: nonNegativeInt(raw.inkRight, "inkRight"),
          bottom: nonNegativeInt(raw.inkBottom, "inkBottom"),
        }
      : null;
  if (ink && (ink.right <= ink.left || ink.bottom <= ink.top || ink.right > width || ink.bottom > height)) {
    throw new Error("Printer render result has an invalid ink box.");
  }
  return {
    width,
    height,
    rowBytes,
    originX: finiteNumber(raw.originX, "originX"),
    originY: finiteNumber(raw.originY, "originY"),
    ink,
    bits,
  };
}

/** 按原生上限切批，保持原顺序。 */
export function chunkItems<T>(items: readonly T[], size: number): T[][] {
  const batch = Math.max(1, Math.floor(size));
  const chunks: T[][] = [];
  for (let index = 0; index < items.length; index += batch) {
    chunks.push(items.slice(index, index + batch));
  }
  return chunks;
}

const BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const BASE64_LOOKUP = (() => {
  const table = new Int16Array(128).fill(-1);
  for (let index = 0; index < BASE64_ALPHABET.length; index += 1) {
    table[BASE64_ALPHABET.charCodeAt(index)] = index;
  }
  return table;
})();

/** 纯 JS base64 编码（不依赖 Buffer / btoa，Hermes 与 Node 行为一致）。 */
export function encodeBase64(bytes: Uint8Array): string {
  let output = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const a = bytes[index];
    const b = index + 1 < bytes.length ? bytes[index + 1] : 0;
    const c = index + 2 < bytes.length ? bytes[index + 2] : 0;
    const triple = (a << 16) | (b << 8) | c;
    output += BASE64_ALPHABET[(triple >> 18) & 63] + BASE64_ALPHABET[(triple >> 12) & 63];
    output += index + 1 < bytes.length ? BASE64_ALPHABET[(triple >> 6) & 63] : "=";
    output += index + 2 < bytes.length ? BASE64_ALPHABET[triple & 63] : "=";
  }
  return output;
}

/** 纯 JS base64 解码；忽略空白，遇到非法字符直接报错（不静默产出错位数据）。 */
export function decodeBase64(value: string): Uint8Array {
  const clean = value.replace(/\s+/g, "").replace(/=+$/, "");
  if (clean.length % 4 === 1) throw new Error("Invalid base64 data.");
  const output = new Uint8Array(Math.floor((clean.length * 3) / 4));
  let buffer = 0;
  let bitCount = 0;
  let offset = 0;
  for (let index = 0; index < clean.length; index += 1) {
    const code = clean.charCodeAt(index);
    const digit = code < 128 ? BASE64_LOOKUP[code] : -1;
    if (digit < 0) throw new Error("Invalid base64 data.");
    // 只需要低位，屏蔽高位免得长串左移溢出。
    buffer = ((buffer << 6) | digit) & 0xffffff;
    bitCount += 6;
    if (bitCount >= 8) {
      bitCount -= 8;
      output[offset] = (buffer >> bitCount) & 0xff;
      offset += 1;
    }
  }
  return output;
}
