import { PACK_CARTON_STATUS, type PackMismatch, type PackProgress, type PackProgressCarton } from "./pack-types";

// 提交拣货前核对的纯逻辑：汇总、未封箱与差异商品的计算，以及「按装箱数改拣货数」的请求生成。

export interface PackTotals {
  /** 已拣件数合计。 */
  picked: number;
  /** 已装箱件数合计。 */
  packed: number;
  /** 本订单的箱数（不含作废）。 */
  cartonCount: number;
}

export function packTotals(progress: Pick<PackProgress, "lines" | "cartons">): PackTotals {
  let picked = 0;
  let packed = 0;
  for (const line of progress.lines) {
    picked += line.pickedPieces;
    packed += line.packedPieces;
  }
  return { picked, packed, cartonCount: progress.cartons.length };
}

/** 还没封的非空箱：会拦住提交，要先封箱。空箱提交时由服务端自动作废，不算。 */
export function openFilledCartons(progress: Pick<PackProgress, "cartons">): PackProgressCarton[] {
  return progress.cartons.filter((carton) => carton.status === PACK_CARTON_STATUS.packing && carton.pieceCount > 0);
}

export function emptyOpenCartons(progress: Pick<PackProgress, "cartons">): PackProgressCarton[] {
  return progress.cartons.filter((carton) => carton.status === PACK_CARTON_STATUS.packing && carton.pieceCount === 0);
}

/** 订单行信息：核对页用拣货单（sheet）里的行把商品对回订单行。 */
export interface ReviewSheetLine {
  detailGuid: string;
  productCode: string;
  pickedTotal: number;
}

/**
 * 拣货数 ≠ 装箱数的商品。订单没有任何箱时不对账（与服务端一致：没有箱的提交照旧）。
 * diff = 已拣 − 已装（正：拣多了没装进箱；负：箱里装多了）。
 */
export function reviewMismatches(progress: Pick<PackProgress, "lines" | "cartons">, sheetLines: readonly ReviewSheetLine[]): PackMismatch[] {
  if (progress.cartons.length === 0) return [];
  return progress.lines
    .filter((line) => line.pickedPieces !== line.packedPieces)
    .map((line) => ({
      productCode: line.productCode,
      productName: line.productName,
      itemNumber: line.itemNumber,
      detailGuids: sheetLines.filter((item) => sameCode(item.productCode, line.productCode)).map((item) => item.detailGuid),
      picked: line.pickedPieces,
      packed: line.packedPieces,
      diff: line.pickedPieces - line.packedPieces,
    }));
}

export interface ReviewState {
  openCartons: PackProgressCarton[];
  mismatches: PackMismatch[];
  /** 没有未封的非空箱、也没有差异：可以提交。 */
  canSubmit: boolean;
}

export function reviewState(progress: Pick<PackProgress, "lines" | "cartons">, sheetLines: readonly ReviewSheetLine[]): ReviewState {
  const openCartons = openFilledCartons(progress);
  const mismatches = reviewMismatches(progress, sheetLines);
  return { openCartons, mismatches, canSubmit: openCartons.length === 0 && mismatches.length === 0 };
}

/** 「按装箱数改拣货数」的一次请求：PUT lines/{detailGuid}/total（不带 cartonGuid）。 */
export interface PickTotalChange {
  detailGuid: string;
  total: number;
  /** 期望的当前合计，用拣货现有的合计（别人刚改过会返回 409 PICKED_TOTAL_CHANGED）。 */
  expectedTotal: number;
}

/**
 * 让这个商品的拣货合计等于装箱数，生成逐行的改总数请求。
 * - 只有一行订单行（常见）：这一行的 total = 装箱数；
 * - 同一商品在订单上有多行：拣货数不足时补到第一行；拣货数过多时从最后一行往前扣，扣到合计 = 装箱数为止，不会扣成负数；
 * - 商品不在订单上（只在箱里）没有订单行可改，返回 null，由人去箱里处理。
 * currentTotals：订单行当前的拣货合计（detailGuid → 件数）。
 */
export function planPickTotalChanges(mismatch: Pick<PackMismatch, "detailGuids" | "packed">, currentTotals: ReadonlyMap<string, number>): PickTotalChange[] | null {
  const guids = mismatch.detailGuids.filter((guid) => currentTotals.has(guid));
  if (guids.length === 0) return null;
  const totals = guids.map((guid) => currentTotals.get(guid) as number);
  const sum = totals.reduce((acc, value) => acc + value, 0);
  const target = Math.max(0, mismatch.packed);
  if (sum === target) return [];
  if (target > sum) {
    return [{ detailGuid: guids[0], total: totals[0] + (target - sum), expectedTotal: totals[0] }];
  }
  const changes: PickTotalChange[] = [];
  let excess = sum - target;
  for (let index = guids.length - 1; index >= 0 && excess > 0; index -= 1) {
    const cut = Math.min(totals[index], excess);
    if (cut > 0) {
      changes.push({ detailGuid: guids[index], total: totals[index] - cut, expectedTotal: totals[index] });
      excess -= cut;
    }
  }
  return changes;
}

function sameCode(left: string, right: string) {
  return left.trim().toLowerCase() === right.trim().toLowerCase();
}
