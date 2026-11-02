/**
 * 小票纸面的版式换算（纯函数，node 下可测试）：
 * 纸宽固定 42 个等宽字符，手机宽度不够时整张纸缩小字号而不是折行，保证金额列不错位。
 */

/** 80mm 热敏纸可打印宽度 576 点 = 42 列。 */
export const RECEIPT_PRINTABLE_DOTS = 576;
export const RECEIPT_COLUMNS = 42;
/** 条码规格与收银机一致：3 点 / 模块、高 80 点。 */
export const RECEIPT_BARCODE_MODULE_DOTS = 3;
export const RECEIPT_BARCODE_HEIGHT_DOTS = 80;
export const RECEIPT_QR_DOTS = 200;

/** 等宽字体的字宽约为字号的 0.6 倍（Menlo 0.602、Android monospace 约 0.6）；留 2% 余量防止最后一列被截断。 */
const MONO_CHAR_WIDTH_RATIO = 0.62;
const MAX_FONT_SIZE = 13;
const MIN_FONT_SIZE = 8;

/** 在可用宽度内放得下 42 列的最大字号，上限 13、下限 8。 */
export function receiptFontSize(contentWidth: number): number {
  const fitted = Math.floor((contentWidth / (RECEIPT_COLUMNS * MONO_CHAR_WIDTH_RATIO)) * 2) / 2;
  return Math.max(MIN_FONT_SIZE, Math.min(MAX_FONT_SIZE, fitted));
}

/** 点数换算成纸面像素：42 列文字宽度 = 576 点。 */
export function receiptDotsToPixels(dots: number, fontSize: number): number {
  const columnsWidth = RECEIPT_COLUMNS * fontSize * MONO_CHAR_WIDTH_RATIO;
  return (dots / RECEIPT_PRINTABLE_DOTS) * columnsWidth;
}

/** 条形码里「条」的位置与宽度（单位：模块），已把交替的条空序列还原成矩形。 */
export function receiptBarRects(runs: readonly number[]): { x: number; width: number }[] {
  const bars: { x: number; width: number }[] = [];
  let x = 0;
  runs.forEach((run, index) => {
    if (index % 2 === 0) bars.push({ x, width: run });
    x += run;
  });
  return bars;
}
