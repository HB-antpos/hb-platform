import type {
  LabelFont,
  LabelRenderTextItem,
  LabelTextBitmap,
  LabelTextItem,
  LabelTextMetrics,
} from "@/modules/printer/label-render";
import { MonoBitmap } from "./mono-bitmap";
import { ifBlankSpace } from "./format";

/** 原生文字渲染接口（真机用 native.ts 的 measureLabelTexts / renderLabelTexts；测试注入假实现）。 */
export interface LabelTextRenderer {
  /** 本包内置的字体键（缺 Condensed 的旧测试包据此回退）。 */
  fonts: readonly string[];
  measure(items: LabelTextItem[]): Promise<LabelTextMetrics[]>;
  render(items: LabelRenderTextItem[]): Promise<LabelTextBitmap[]>;
}

/** 安卓旧排版用到的字体族名；按 Android Typeface.create(family, BOLD) 的实际结果映射到内置 Roboto。 */
export type AndroidFamily = "sans-serif-black" | "sans-serif-light" | "sans-serif-condensed" | "Arial";

/**
 * Android 10+ 的 minikin：加粗 = 基础字重 + 300 后取最接近的字重，差不到 200 不做假粗。
 * - sans-serif-black（900）不论加粗都是 Black；
 * - sans-serif-light（300）加粗 → 600，Medium（500）与 Bold（700）等距时取先出现的 Medium；
 * - Arial 在安卓上不存在，回退默认 sans-serif（400）：加粗 → Bold；
 * - sans-serif-condensed 加粗 → Condensed Bold（旧包没有时退回 Bold）。
 */
export function androidFont(family: AndroidFamily, bold: boolean, fonts: readonly string[]): LabelFont {
  switch (family) {
    case "sans-serif-black":
      return "roboto-black";
    case "sans-serif-light":
      return bold ? "roboto-medium" : "roboto-light";
    case "sans-serif-condensed":
      // 旧排版只有加粗的 condensed；不加粗时没有内置字体，退回 Regular。
      if (!bold) return "roboto-regular";
      return fonts.includes("roboto-condensed-bold") ? "roboto-condensed-bold" : "roboto-bold";
    case "Arial":
    default:
      return bold ? "roboto-bold" : "roboto-regular";
  }
}

/** Kotlin fontSizeToPixels：旧代码的字号 × 3 = 打印点。 */
export function pt(size: number) {
  return size * 3;
}

/** 对应 Android textToBitmap 的参数（size 用旧代码的「字号」，内部 × 3）。 */
export type TextSpec = {
  text: string;
  size: number;
  bold: boolean;
  family: AndroidFamily;
  inverse?: boolean;
  padding?: number;
};

function toRenderItem(renderer: LabelTextRenderer, spec: TextSpec): LabelRenderTextItem {
  return {
    text: ifBlankSpace(spec.text),
    font: androidFont(spec.family, spec.bold, renderer.fonts),
    size: pt(spec.size),
    inverse: spec.inverse ?? false,
    padding: spec.padding ?? 0,
  };
}

function toMono(raw: LabelTextBitmap) {
  return new MonoBitmap(raw.width, raw.height, raw.bits);
}

/**
 * 复刻 Android textToBitmap：横向从笔触起点开始，宽 = max(前进宽, 墨迹宽) + 2×padding；
 * 纵向只包住墨迹（上下各加 padding），墨迹底边贴 padding。空白文字只有 padding 高。
 */
function cropLikeAndroid(raw: LabelTextBitmap, metrics: LabelTextMetrics, spec: TextSpec) {
  const padding = spec.padding ?? 0;
  const inverse = spec.inverse ?? false;
  const ink = raw.ink;
  const inkWidth = ink ? ink.right - ink.left : 0;
  const width = Math.max(1, Math.ceil(Math.max(metrics.advance, inkWidth)) + padding * 2);
  const height = Math.max(1, (ink ? ink.bottom - ink.top : 0) + padding * 2);
  const top = ink ? ink.top - padding : raw.originY - padding;
  return toMono(raw).crop(raw.originX - padding, top, width, height, inverse);
}

/** 批量画多段文字（一次 measure + 一次 render 原生调用），结果与入参一一对应。 */
export async function textBitmaps(renderer: LabelTextRenderer, specs: readonly TextSpec[]): Promise<MonoBitmap[]> {
  if (specs.length === 0) return [];
  const items = specs.map((spec) => toRenderItem(renderer, spec));
  const [rendered, measured] = await Promise.all([
    renderer.render(items),
    renderer.measure(items.map(({ text, font, size }) => ({ text, font, size }))),
  ]);
  return specs.map((spec, index) => cropLikeAndroid(rendered[index], measured[index], spec));
}

export async function textBitmap(renderer: LabelTextRenderer, spec: TextSpec) {
  return (await textBitmaps(renderer, [spec]))[0];
}

/**
 * 在降序候选值里找「最大且放得下」的一个，结果与旧代码逐级缩小的循环一致；
 * estimate 是按线性缩放估出的起点下标，每轮只批量试 5 个候选，通常一轮就定。
 * 全都放不下时返回最后一个候选（旧代码缩到下限后也直接用）。
 */
export async function pickLargestFitting<T>(
  candidates: readonly number[],
  estimate: number,
  evaluate: (values: number[]) => Promise<T[]>,
  fits: (result: T) => boolean
): Promise<{ value: number; result: T }> {
  const window = 5;
  let start = Math.max(0, Math.min(candidates.length - 1, estimate) - 2);
  let best: { index: number; value: number; result: T } | null = null;
  let last: { value: number; result: T } | null = null;
  const visited = new Set<number>();
  // 正常单调时最多来回几轮；字形宽度偶有非单调（字距）时靠 visited 防止来回打转，取已知最大的可行值。
  while (!visited.has(start)) {
    visited.add(start);
    const values = candidates.slice(start, start + window);
    const results = await evaluate(values);
    const firstFit = results.findIndex(fits);
    if (start + values.length >= candidates.length) {
      last = { value: values[values.length - 1], result: results[results.length - 1] };
    }
    if (firstFit >= 0 && (!best || start + firstFit < best.index)) {
      best = { index: start + firstFit, value: values[firstFit], result: results[firstFit] };
    }
    if (firstFit > 0 || (firstFit === 0 && start === 0)) break;
    if (firstFit === 0) {
      // 窗口里最大的都放得下：往更大的方向再看一轮。
      start = Math.max(0, start - window + 1);
    } else if (start + window >= candidates.length) {
      break;
    } else {
      start += window - 1;
    }
  }
  if (best) return { value: best.value, result: best.result };
  if (last) return last;
  // 理论上走不到：兜底再评估最小候选。
  const minimum = candidates[candidates.length - 1];
  return { value: minimum, result: (await evaluate([minimum]))[0] };
}

/** 线性估计：字形尺寸与字号成正比，估出能放下的候选下标（偏大一点由 pickLargestFitting 修正）。 */
export function estimateIndex(candidates: readonly number[], current: number, ratio: number) {
  const target = current * Math.min(1, ratio);
  const index = candidates.findIndex((value) => value <= target + 1e-9);
  return index < 0 ? candidates.length - 1 : index;
}

/** 从 from 起按 step 递减到 to（含），对应旧代码 `while (...) size -= step` 能取到的所有值。 */
export function descending(from: number, to: number, step: number) {
  const values: number[] = [];
  for (let index = 0; ; index += 1) {
    const value = Math.round((from - step * index) * 1000) / 1000;
    if (value < to - 1e-9) break;
    values.push(value);
  }
  return values;
}

/**
 * 复刻 Discount 标签的 fittedText：字号从 size 每次减 0.5，直到宽 ≤ maxWidth 且高 ≤ maxHeight（最小到 1）。
 */
export type FitRequest = { spec: TextSpec; maxWidth: number; maxHeight?: number };

/** 批量版 fittedText：先一次性按原字号画全部，只有放不下的再单独缩字（多数标签一次原生往返就够）。 */
export async function fittedTexts(renderer: LabelTextRenderer, requests: readonly FitRequest[]): Promise<MonoBitmap[]> {
  const firsts = await textBitmaps(
    renderer,
    requests.map((request) => request.spec)
  );
  return Promise.all(
    requests.map(async ({ spec, maxWidth, maxHeight = 64 }, index) => {
      const first = firsts[index];
      const fit = (bitmap: MonoBitmap) => bitmap.width <= maxWidth && bitmap.height <= maxHeight;
      if (fit(first)) return first;
      const candidates = descending(spec.size, 1, 0.5);
      const ratio = Math.min(maxWidth / first.width, maxHeight / first.height);
      const { result } = await pickLargestFitting(
        candidates,
        estimateIndex(candidates, spec.size, ratio),
        (sizes) => textBitmaps(renderer, sizes.map((size) => ({ ...spec, size }))),
        fit
      );
      return result;
    })
  );
}

export async function fittedText(renderer: LabelTextRenderer, spec: TextSpec, maxWidth: number, maxHeight = 64) {
  return (await fittedTexts(renderer, [{ spec, maxWidth, maxHeight }]))[0];
}

/** Kotlin wrapText：逐字累加，超宽就换行；最后一行不再折，整段留给它（由画布宽度截掉）。 */
export async function wrapChars(
  renderer: LabelTextRenderer,
  text: string,
  font: LabelFont,
  size: number,
  maxWidth: number,
  maxLines: number
): Promise<string[]> {
  const chars = Array.from(text);
  const lines: string[] = [];
  let start = 0;
  while (start < chars.length && lines.length < maxLines - 1) {
    // 批量量出从 start 起每个 ≥2 字的前缀，找第一个超宽的位置。
    const prefixes: string[] = [];
    for (let end = start + 2; end <= chars.length; end += 1) prefixes.push(chars.slice(start, end).join(""));
    if (prefixes.length === 0) break;
    const metrics = await renderer.measure(prefixes.map((value) => ({ text: value, font, size })));
    const overflow = metrics.findIndex((metric) => metric.advance > maxWidth);
    if (overflow < 0) break;
    const end = start + overflow + 1;
    lines.push(chars.slice(start, end).join(""));
    start = end;
  }
  const rest = chars.slice(start).join("");
  if (rest.length > 0 || lines.length === 0) lines.push(rest);
  return lines.slice(0, maxLines);
}

/** 按基线把多行文字画进 width × (lineHeight × 行数) 的白底画布（行内超宽部分被画布截掉）。 */
export async function drawLines(
  renderer: LabelTextRenderer,
  lines: readonly string[],
  font: LabelFont,
  size: number,
  width: number,
  lineHeight: number,
  ascent: number
) {
  const canvas = new MonoBitmap(Math.max(1, width), Math.max(1, lineHeight * lines.length));
  const rendered = await renderer.render(lines.map((line) => ({ text: ifBlankSpace(line), font, size })));
  rendered.forEach((raw, index) => {
    const baseline = Math.round(index * lineHeight + ascent);
    canvas.draw(toMono(raw), -raw.originX, baseline - raw.originY, "or");
  });
  return canvas;
}

/** 复刻 Android longTextToBitmap：逐字换行、最多 maxLines 行，画布宽取各行最大前进宽且不超过 maxWidth。 */
export async function longTextBitmap(
  renderer: LabelTextRenderer,
  text: string,
  size: number,
  bold: boolean,
  family: AndroidFamily,
  maxLines: number,
  maxWidth: number
) {
  const font = androidFont(family, bold, renderer.fonts);
  const px = pt(size);
  const safe = ifBlankSpace(text);
  const lines = await wrapChars(renderer, safe, font, px, maxWidth, maxLines);
  const metrics = await renderer.measure(lines.map((line) => ({ text: line, font, size: px })));
  const lineHeight = Math.ceil(metrics[0].descent + metrics[0].ascent);
  const widest = Math.ceil(Math.max(...metrics.map((metric) => metric.advance)));
  const width = Math.max(1, Math.min(maxWidth, widest));
  return drawLines(renderer, lines, font, px, width, lineHeight, metrics[0].ascent);
}
