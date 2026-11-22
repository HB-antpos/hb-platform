/**
 * 测试用假渲染器：字宽、行高按字号线性计算，位图按与原生相同的契约生成（墨迹 = 实心矩形），
 * 让排版坐标在 Node 里可精确断言。只被测试引用，不进 App 包。
 */
import { encodeBase64, LABEL_FONTS, type LabelRenderTextItem, type LabelTextItem } from "@/modules/printer/label-render";
import type { LabelTextRenderer } from "../text";

const MAX_WIDTH = 2048;
const MAX_HEIGHT = 1024;

function charWidth(char: string) {
  return (char.codePointAt(0) ?? 0) > 0x2e80 ? 1 : 0.5;
}

export function fakeMetrics({ text, size }: LabelTextItem) {
  const advance = Array.from(text).reduce((sum, char) => sum + charWidth(char) * size, 0);
  const blank = text.trim() === "";
  const descender = /[gjpqy]/.test(text);
  return {
    advance,
    ascent: size * 0.9,
    descent: size * 0.25,
    inkLeft: 0,
    inkTop: blank ? 0 : -size * 0.7,
    inkRight: blank ? 0 : advance,
    inkBottom: blank ? 0 : descender ? size * 0.2 : 0,
  };
}

export function fakeRender(item: LabelRenderTextItem) {
  const metrics = fakeMetrics(item);
  const padding = item.padding ?? 0;
  const inverse = item.inverse ?? false;
  const left = Math.floor(Math.min(0, metrics.inkLeft));
  const right = Math.ceil(Math.max(metrics.advance, metrics.inkRight));
  const top = Math.floor(Math.min(-metrics.ascent, metrics.inkTop));
  const bottom = Math.ceil(Math.max(metrics.descent, metrics.inkBottom));
  const width = Math.max(1, right - left + padding * 2);
  const height = Math.max(1, bottom - top + padding * 2);
  if (width > MAX_WIDTH || height > MAX_HEIGHT) {
    throw new Error(`PRINTER_RENDER_TOO_LARGE ${width}x${height}`);
  }
  const originX = padding - left;
  const originY = padding - top;
  const rowBytes = Math.ceil(width / 8);
  const bits = new Uint8Array(rowBytes * height);
  const hasInk = metrics.inkRight > metrics.inkLeft;
  const ink = hasInk
    ? {
        left: originX + Math.floor(metrics.inkLeft),
        top: originY + Math.floor(metrics.inkTop),
        right: originX + Math.ceil(metrics.inkRight),
        bottom: originY + Math.ceil(metrics.inkBottom) || originY,
      }
    : null;
  if (ink && ink.bottom <= ink.top) ink.bottom = ink.top + 1;
  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const inInk = ink !== null && x >= ink.left && x < ink.right && y >= ink.top && y < ink.bottom;
      if (inInk !== inverse) bits[y * rowBytes + (x >> 3)] |= 0x80 >> (x & 7);
    }
  }
  return {
    width,
    height,
    rowBytes,
    originX,
    originY,
    ink,
    bits,
    base64: encodeBase64(bits),
  };
}

/** calls 记录每次原生调用的条数，便于断言批量效果。 */
export function createFakeRenderer(fonts: readonly string[] = LABEL_FONTS) {
  const calls: { op: "measure" | "render"; count: number; items: LabelTextItem[] }[] = [];
  const renderer: LabelTextRenderer = {
    fonts,
    measure: async (items) => {
      calls.push({ op: "measure", count: items.length, items });
      return items.map(fakeMetrics);
    },
    render: async (items) => {
      calls.push({ op: "render", count: items.length, items });
      return items.map((item) => {
        const { width, height, rowBytes, originX, originY, ink, bits } = fakeRender(item);
        return { width, height, rowBytes, originX, originY, ink, bits };
      });
    },
  };
  return { renderer, calls };
}
