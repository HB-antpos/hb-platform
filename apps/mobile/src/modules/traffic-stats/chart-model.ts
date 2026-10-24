// 客流曲线图的几何与刻度（纯函数，不依赖 React Native，Node 下可测）。
// 曲线用单调三次插值（Fritsch–Carlson）：曲线不会冲出相邻数据点的高低范围，
// 不会在 0 附近画出「负人数」的下凹，比 Catmull-Rom 更适合计数类数据。

export interface Point {
  x: number;
  y: number;
}

export interface ChartMargin {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface ChartLayout {
  width: number;
  height: number;
  margin: ChartMargin;
  /** 数据点个数（横轴格数）。 */
  count: number;
  yMax: number;
  /** line：首尾点贴着绘图区边缘；bar：每格居中（柱状图）。 */
  mode: "line" | "bar";
}

export function plotWidth(layout: ChartLayout): number {
  return Math.max(1, layout.width - layout.margin.left - layout.margin.right);
}

export function plotHeight(layout: ChartLayout): number {
  return Math.max(1, layout.height - layout.margin.top - layout.margin.bottom);
}

export function xAt(layout: ChartLayout, index: number): number {
  const width = plotWidth(layout);
  if (layout.mode === "bar") return layout.margin.left + (width * (index + 0.5)) / layout.count;
  return layout.margin.left + (layout.count <= 1 ? width / 2 : (width * index) / (layout.count - 1));
}

export function yAt(layout: ChartLayout, value: number): number {
  const clamped = Math.min(Math.max(value, 0), layout.yMax);
  return layout.margin.top + plotHeight(layout) * (1 - clamped / layout.yMax);
}

/** 触点 x 坐标 → 最近的数据点下标（吸附到整点 / 整天）。 */
export function nearestIndex(layout: ChartLayout, x: number): number {
  if (layout.count <= 1) return 0;
  let best = 0;
  let bestDistance = Infinity;
  for (let index = 0; index < layout.count; index += 1) {
    const distance = Math.abs(xAt(layout, index) - x);
    if (distance < bestDistance) {
      best = index;
      bestDistance = distance;
    }
  }
  return best;
}

/**
 * 纵轴取「好读」的刻度：最多 5 格，顶部留约 12% 给峰值标签。
 * 小店每小时只有几个人，所以刻度从 1 开始；数据全为 0 时给一个最小可画的轴。
 */
export function niceScale(maxValue: number): { max: number; step: number } {
  const target = Math.max(maxValue, 1) * 1.12;
  for (let magnitude = 1; magnitude <= 1e9; magnitude *= 10) {
    for (const base of [1, 2, 2.5, 5]) {
      const step = base * magnitude;
      if (Number.isInteger(step) && target / step <= 5) return { max: Math.ceil(target / step) * step, step };
    }
  }
  const step = Math.ceil(target / 5);
  return { max: step * 5, step };
}

export function seriesMax(...series: readonly (readonly (number | null | undefined)[])[]): number {
  let max = 0;
  for (const values of series) {
    for (const value of values) if (value != null && value > max) max = value;
  }
  return max;
}

/** 单调三次插值 → SVG path；少于 2 个点返回空串。 */
export function monotonePath(points: readonly Point[]): string {
  const n = points.length;
  if (n === 0) return "";
  if (n === 1) return `M${fmt(points[0].x)},${fmt(points[0].y)}`;
  const dx: number[] = [];
  const slope: number[] = [];
  for (let i = 0; i < n - 1; i += 1) {
    dx[i] = points[i + 1].x - points[i].x;
    slope[i] = dx[i] === 0 ? 0 : (points[i + 1].y - points[i].y) / dx[i];
  }
  const tangent: number[] = Array<number>(n).fill(0);
  tangent[0] = slope[0];
  tangent[n - 1] = slope[n - 2];
  for (let i = 1; i < n - 1; i += 1) {
    tangent[i] = slope[i - 1] * slope[i] <= 0 ? 0 : (slope[i - 1] + slope[i]) / 2;
  }
  for (let i = 0; i < n - 1; i += 1) {
    if (slope[i] === 0) {
      tangent[i] = 0;
      tangent[i + 1] = 0;
      continue;
    }
    const a = tangent[i] / slope[i];
    const b = tangent[i + 1] / slope[i];
    const sum = a * a + b * b;
    if (sum > 9) {
      const tau = 3 / Math.sqrt(sum);
      tangent[i] = tau * a * slope[i];
      tangent[i + 1] = tau * b * slope[i];
    }
  }
  let d = `M${fmt(points[0].x)},${fmt(points[0].y)}`;
  for (let i = 0; i < n - 1; i += 1) {
    const h = dx[i] / 3;
    d += ` C${fmt(points[i].x + h)},${fmt(points[i].y + tangent[i] * h)} ${fmt(points[i + 1].x - h)},${fmt(points[i + 1].y - tangent[i + 1] * h)} ${fmt(points[i + 1].x)},${fmt(points[i + 1].y)}`;
  }
  return d;
}

function fmt(value: number): string {
  return (Math.round(value * 10) / 10).toString();
}

/** 把带 null 的序列投影成点；null 之后的部分不再连线（今天之后的日子 / 小时）。 */
export function projectSeries(layout: ChartLayout, values: readonly (number | null)[]): (Point & { index: number; value: number })[] {
  const points: (Point & { index: number; value: number })[] = [];
  for (let index = 0; index < values.length; index += 1) {
    const value = values[index];
    if (value == null) {
      // 首个缺口之后不再画（今天之后的日子不会再有数据）；对照期缺口同理。
      if (points.length > 0) break;
      continue;
    }
    points.push({ index, value, x: xAt(layout, index), y: yAt(layout, value) });
  }
  return points;
}

/** 面积填充：曲线下方封口到横轴。 */
export function areaPath(curve: string, points: readonly Point[], baselineY: number): string {
  if (points.length < 2 || !curve) return "";
  return `${curve} L${fmt(points[points.length - 1].x)},${fmt(baselineY)} L${fmt(points[0].x)},${fmt(baselineY)} Z`;
}

/** 粗估 SVG 文本宽度：全角字符按字号，其余按 0.56 倍字号；用来给提示框定宽。 */
export function estimateTextWidth(text: string, fontSize: number): number {
  return [...text].reduce((width, char) => width + (/[　-鿿＀-￯]/.test(char) ? fontSize : fontSize * 0.56), 0);
}

/** 提示框水平位置：以数据点为中心，夹在绘图区内；left 为真时放在竖线左侧（被「现在」标签挡住时用）。 */
export function tooltipLeft(layout: ChartLayout, anchorX: number, boxWidth: number): number {
  const min = layout.margin.left;
  const max = layout.width - layout.margin.right - boxWidth;
  return Math.min(Math.max(anchorX - boxWidth / 2, min), Math.max(min, max));
}

/** 横轴标签间隔：小时窗口宽时隔 2 / 3 / 4 个标一次，避免挤在一起。 */
export function labelStep(count: number): number {
  if (count <= 8) return 1;
  if (count <= 13) return 2;
  if (count <= 19) return 3;
  return 4;
}
