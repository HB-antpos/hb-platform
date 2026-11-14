import type { PackProgressLine } from "./pack-types";
import type { PickSheetLine } from "./types";

// 先拣后装的纯逻辑：待装排序、一次扫码加多少、超装判断。

/** 单个商品在一只箱里的件数上限，与打包模块、后端一致。 */
export const MAX_CARTON_PIECES = 100000;

export interface PackLaterLine extends PackProgressLine {
  /** 待装 = 已拣 − 已装（装多了为负）。 */
  remaining: number;
}

export interface PackLaterGroups {
  /** 还没装完的商品，焦点商品（从核对页「去补装」带过来的）排最前，其余保持服务端顺序。 */
  pending: PackLaterLine[];
  /** 已装完（含装多了）的商品，界面折叠在下面。 */
  done: PackLaterLine[];
}

const sameCode = (left: string, right: string) => left.trim().toLowerCase() === right.trim().toLowerCase();

/** 已拣 = 0 且已装 = 0 的商品没有东西可装（没拣到 / 没货），不列出。 */
export function splitPackLines(lines: readonly PackProgressLine[], focusProductCode: string | null = null): PackLaterGroups {
  const pending: PackLaterLine[] = [];
  const done: PackLaterLine[] = [];
  for (const line of lines) {
    if (line.pickedPieces === 0 && line.packedPieces === 0) continue;
    const entry = { ...line, remaining: line.pickedPieces - line.packedPieces };
    (entry.remaining > 0 ? pending : done).push(entry);
  }
  if (focusProductCode) {
    const index = pending.findIndex((line) => sameCode(line.productCode, focusProductCode));
    if (index > 0) pending.unshift(...pending.splice(index, 1));
  }
  return { pending, done };
}

export interface PackLaterTotals {
  picked: number;
  packed: number;
  /** 还差多少件没装（只累加待装的商品，装多了的不抵扣）。 */
  remaining: number;
  /** 还没装完的商品数。 */
  pendingProducts: number;
}

export function packLaterTotals(lines: readonly PackProgressLine[]): PackLaterTotals {
  let picked = 0;
  let packed = 0;
  let remaining = 0;
  let pendingProducts = 0;
  for (const line of lines) {
    picked += line.pickedPieces;
    packed += line.packedPieces;
    const left = line.pickedPieces - line.packedPieces;
    if (left > 0) {
      remaining += left;
      pendingProducts += 1;
    }
  }
  return { picked, packed, remaining, pendingProducts };
}

export type PackScanStep = "pack" | "each";

/** 一次扫码往箱里加多少：按中包用订单行的中包数（没设置按 1 件），按件固定 1 件，与拣货页一致。 */
export function packScanStep(mode: PackScanStep, line: Pick<PickSheetLine, "minOrderQuantity"> | null): number {
  if (mode === "each") return 1;
  const pack = line?.minOrderQuantity;
  return typeof pack === "number" && Number.isInteger(pack) && pack > 0 ? pack : 1;
}

/** 箱内该商品的新绝对件数：不小于 0、不超过上限。 */
export function nextCartonQuantity(current: number, delta: number): number {
  return Math.max(0, Math.min(MAX_CARTON_PIECES, Math.trunc(current + delta)));
}

/**
 * 这次改动会让全订单该商品的装箱合计超出已拣多少件（0 = 没超）。
 * currentInCarton / newInCarton：该商品在这只箱里改动前 / 改动后的件数；其他箱的件数不变。
 */
export function overPackBy(line: Pick<PackProgressLine, "pickedPieces" | "packedPieces">, currentInCarton: number, newInCarton: number): number {
  const packedAfter = line.packedPieces - currentInCarton + newInCarton;
  return Math.max(0, packedAfter - line.pickedPieces);
}

/** 扫到的码对应的订单行去重成商品：一个商品一项；超过一项时让拣货员选。 */
export function distinctProducts<T extends Pick<PickSheetLine, "productCode">>(lines: readonly T[]): T[] {
  const seen = new Set<string>();
  const result: T[] = [];
  for (const line of lines) {
    const key = line.productCode.trim().toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(line);
  }
  return result;
}
