import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import { DetailAction, resolveDetailAction, type RowActions } from "./detail-status";
import { getDetailProgressBucket } from "./progress-buckets";

/**
 * 批量执行（二期 C）的纯逻辑，口径搬自 Web InvoiceEdit/batchExecuteConfirm.ts：
 * 涨价 / 降价分开执行（#556）、进货价涨跌超 40% 二次确认（#586）、确认当刻的动作快照、跳过失败行重试。
 *
 * 与 Web 的差异（用户 10-30 定）：手机端只执行「新建商品」「更新进货价」两类，
 * 改货号 / 加多码留在 Web；不同步 HQ、不额外选分店（只写本单分店）。
 */

/** 手机端允许执行的操作类型。 */
export const MOBILE_EXECUTABLE_ACTIONS: readonly number[] = [DetailAction.CreateProduct, DetailAction.UpdatePurchasePrice];

/** 进货价较上次涨跌绝对值超过该比例（严格大于）时必须二次确认；与后端 LocalSupplierInvoicesPurchasePriceChangeGuard 同口径。 */
export const PRICE_CHANGE_CONFIRM_RATIO = 0.4;
export const PRICE_CHANGE_CONFIRM_REQUIRED_CODE = "PRICE_CHANGE_CONFIRM_REQUIRED";
export const BATCH_EXECUTE_VALIDATION_ERROR_CODE = "VALIDATION_ERROR";

/** 新商品带副码时的主档类型：1 套装 / 2 多码（Web 默认多码）。 */
export type NewProductType = 1 | 2;

type ExecuteSource = Pick<
  LocalSupplierInvoiceItem,
  | "detailGuid"
  | "activityType"
  | "purchasePrice"
  | "lastPurchasePrice"
  | "itemNumber"
  | "productName"
  | "additionalBarcodes"
  | "isCreatedByThisInvoice"
  | "existingProductCount"
  | "barcodeStatus"
  | "barcodeMatchCount"
>;

export interface ExecuteTargets {
  /** 本次可在手机上执行的行（新建商品 / 更新进货价），保持明细顺序。 */
  guids: string[];
  /** 选中了但手机端不执行的「更新货号 / 添加多码」行数（提示去 Web）。 */
  webOnlyCount: number;
  /** 选中了但没有可执行操作的行数（无 / 等待操作 / 已执行）。 */
  noActionCount: number;
}

/**
 * 确定要执行的行：
 * - 有勾选：只取勾选且当前可见的行（与 Web「执行勾选」一致，看不到的勾选不执行）；
 * - 无勾选：取整张单「待执行」的行（已检测 + 有待执行操作，不受筛选影响，与 Web「执行全部待执行」一致）。
 * 两种情况都只保留新建商品 / 更新进货价。
 */
export function pickExecuteTargets(
  details: readonly ExecuteSource[],
  options: { selectedGuids: readonly string[]; visibleGuids: readonly string[]; rowActions?: RowActions }
): ExecuteTargets {
  const rowActions = options.rowActions ?? {};
  const hasSelection = options.selectedGuids.length > 0;
  const selected = new Set(options.selectedGuids);
  const visible = new Set(options.visibleGuids);
  const result: ExecuteTargets = { guids: [], webOnlyCount: 0, noActionCount: 0 };
  for (const detail of details) {
    if (hasSelection) {
      if (!selected.has(detail.detailGuid) || !visible.has(detail.detailGuid)) continue;
    } else if (getDetailProgressBucket(detail, rowActions) !== "pending") {
      continue;
    }
    const action = resolveDetailAction(detail, rowActions);
    if (MOBILE_EXECUTABLE_ACTIONS.includes(action)) result.guids.push(detail.detailGuid);
    else if (action === DetailAction.UpdateItemNumber || action === DetailAction.AddMultiCode) result.webOnlyCount += 1;
    else result.noActionCount += 1;
  }
  return result;
}

export interface ExecuteGroups {
  createGuids: string[];
  /** 更新进货价且本次价高于上次（上次 > 0 才可比）。 */
  upGuids: string[];
  /** 更新进货价且本次价低于上次。 */
  downGuids: string[];
  /** 更新进货价但持平或没有上次价可比：总是随「更新进货价」一起执行。 */
  flatGuids: string[];
}

/** 按「新建商品 / 涨价 / 降价 / 其余更新进货价」分组，保持传入顺序（Web splitCreateProductDetailGuids + splitPurchasePriceDirectionGuids）。 */
export function groupExecuteTargets(
  guids: readonly string[],
  details: readonly ExecuteSource[],
  rowActions: RowActions = {}
): ExecuteGroups {
  const detailMap = new Map(details.map((item) => [item.detailGuid, item]));
  const groups: ExecuteGroups = { createGuids: [], upGuids: [], downGuids: [], flatGuids: [] };
  for (const guid of guids) {
    const detail = detailMap.get(guid);
    if (!detail) continue;
    const action = resolveDetailAction(detail, rowActions);
    if (action === DetailAction.CreateProduct) {
      groups.createGuids.push(guid);
      continue;
    }
    const last = detail.lastPurchasePrice;
    const current = detail.purchasePrice;
    const comparable = typeof last === "number" && last > 0 && typeof current === "number";
    if (comparable && current > last) groups.upGuids.push(guid);
    else if (comparable && current < last) groups.downGuids.push(guid);
    else groups.flatGuids.push(guid);
  }
  return groups;
}

export interface ExecuteChoice {
  includeCreate: boolean;
  includeUp: boolean;
  includeDown: boolean;
  /** 持平 / 无上次价可比的更新进货价（Web 把它们并在「其余」里总是执行；手机端分组单列，默认勾选）。 */
  includeFlat: boolean;
}

/** 按用户勾选合并出最终执行的行（保持原顺序）；没勾选的一类留作待执行。 */
export function pickChosenGuids(guids: readonly string[], groups: ExecuteGroups, choice: ExecuteChoice): string[] {
  const excluded = new Set<string>([
    ...(choice.includeCreate ? [] : groups.createGuids),
    ...(choice.includeUp ? [] : groups.upGuids),
    ...(choice.includeDown ? [] : groups.downGuids),
    ...(choice.includeFlat ? [] : groups.flatGuids),
  ]);
  return guids.filter((guid) => !excluded.has(guid));
}

/** 涨跌幅（本次 / 上次 - 1）；上次为空或 ≤ 0、本次缺失时返回 null。 */
function getRatio(last: number | null | undefined, current: number | null | undefined): number | null {
  if (typeof last !== "number" || !(last > 0)) return null;
  if (typeof current !== "number" || !Number.isFinite(current)) return null;
  return current / last - 1;
}

export interface LargePriceChangeRow {
  detailGuid: string;
  itemNumber: string;
  productName: string;
  lastPurchasePrice: number;
  purchasePrice: number;
  /** 0.5 = +50%，负数为降价。 */
  ratio: number;
}

/** 将执行「更新进货价」且涨跌幅严格超过 40% 的行；新进货价 ≤ 0 的行后端会跳过，不需要确认。 */
export function findLargePriceChangeRows(
  guids: readonly string[],
  details: readonly ExecuteSource[],
  rowActions: RowActions = {}
): LargePriceChangeRow[] {
  const detailMap = new Map(details.map((item) => [item.detailGuid, item]));
  const rows: LargePriceChangeRow[] = [];
  for (const guid of guids) {
    const detail = detailMap.get(guid);
    if (!detail || resolveDetailAction(detail, rowActions) !== DetailAction.UpdatePurchasePrice) continue;
    if (!(typeof detail.purchasePrice === "number" && detail.purchasePrice > 0)) continue;
    const ratio = getRatio(detail.lastPurchasePrice, detail.purchasePrice);
    if (ratio === null || Math.abs(ratio) <= PRICE_CHANGE_CONFIRM_RATIO) continue;
    rows.push({
      detailGuid: detail.detailGuid,
      itemNumber: detail.itemNumber,
      productName: detail.productName,
      lastPurchasePrice: detail.lastPurchasePrice as number,
      purchasePrice: detail.purchasePrice,
      ratio,
    });
  }
  return rows;
}

/** 本次要新建、且带副码（必须选主档类型）的行。 */
export function countNewProductsWithAdditionalBarcodes(
  guids: readonly string[],
  details: readonly ExecuteSource[],
  rowActions: RowActions = {}
): number {
  const selected = new Set(guids);
  return details.filter((detail) => selected.has(detail.detailGuid)
    && resolveDetailAction(detail, rowActions) === DetailAction.CreateProduct
    && (detail.additionalBarcodes?.length ?? 0) > 0).length;
}

export interface BatchExecuteRequest {
  detailGuids: string[];
  /** 确认当刻的动作快照：后端逐行比对数据库现值，不一致就整批拒绝（防止别人刚改过）。 */
  expectedActions: { detailGuid: string; action: number; activityType: number }[];
  confirmedCreateProductCount: number;
  confirmedAt: string;
  newProductProductTypeSelections: { detailGuid: string; productType: NewProductType }[];
  confirmedLargePriceChange?: boolean;
}

/** 组装 batch-execute 请求体（Web buildBatchExecuteSnapshot + createBatchExecuteRequest；不带 targetStoreCodes = 只写本单分店）。 */
export function buildBatchExecuteRequest(
  guids: readonly string[],
  details: readonly ExecuteSource[],
  options: { rowActions?: RowActions; newProductType: NewProductType; confirmedAt: string; confirmedLargePriceChange?: boolean }
): BatchExecuteRequest {
  const rowActions = options.rowActions ?? {};
  const detailMap = new Map(details.map((item) => [item.detailGuid, item]));
  const expectedActions = guids.flatMap((detailGuid) => {
    const detail = detailMap.get(detailGuid);
    if (!detail) return [];
    const action = resolveDetailAction(detail, rowActions);
    return [{ detailGuid, action, activityType: detail.activityType ?? action }];
  });
  const newProductProductTypeSelections = expectedActions
    .filter((item) => item.action === DetailAction.CreateProduct
      && (detailMap.get(item.detailGuid)?.additionalBarcodes?.length ?? 0) > 0)
    .map((item) => ({ detailGuid: item.detailGuid, productType: options.newProductType }));
  return {
    detailGuids: expectedActions.map((item) => item.detailGuid),
    expectedActions,
    confirmedCreateProductCount: expectedActions.filter((item) => item.action === DetailAction.CreateProduct).length,
    confirmedAt: options.confirmedAt,
    newProductProductTypeSelections,
    ...(options.confirmedLargePriceChange ? { confirmedLargePriceChange: true } : {}),
  };
}

/** 从请求里去掉指定明细，计数与类型选择同步收敛（「跳过失败行，执行其余」）。 */
export function excludeFromBatchExecuteRequest(request: BatchExecuteRequest, excludedGuids: readonly string[]): BatchExecuteRequest {
  const excluded = new Set(excludedGuids);
  const expectedActions = request.expectedActions.filter((item) => !excluded.has(item.detailGuid));
  return {
    ...request,
    detailGuids: request.detailGuids.filter((guid) => !excluded.has(guid)),
    expectedActions,
    confirmedCreateProductCount: expectedActions.filter((item) => item.action === DetailAction.CreateProduct).length,
    newProductProductTypeSelections: request.newProductProductTypeSelections.filter((item) => !excluded.has(item.detailGuid)),
  };
}

/**
 * 后端逐行错误都以「明细 {guid} 」开头。每条错误都能对应到本次某一行、至少一行失败、且跳过后还剩行时，
 * 才允许「跳过失败行，执行其余」；否则是整单级错误，保持整批拒绝（Web findSkippableFailedRows）。
 */
export function findSkippableFailedRows(
  detailGuids: readonly string[],
  errors: readonly string[]
): { failedGuids: string[]; remainingCount: number } | null {
  if (!errors.length) return null;
  const failed = new Set<string>();
  for (const error of errors) {
    const guid = detailGuids.find((candidate) => error.startsWith(`明细 ${candidate} `));
    if (!guid) return null;
    failed.add(guid);
  }
  const failedGuids = detailGuids.filter((guid) => failed.has(guid));
  if (failedGuids.length === 0 || failedGuids.length >= detailGuids.length) return null;
  return { failedGuids, remainingCount: detailGuids.length - failedGuids.length };
}

/** 勾选只保留当前可见的行（筛选变化后看不到的勾选自动去掉，与 Web constrainSelectedRowKeysToVisibleDetails 一致）。 */
export function constrainSelectionToVisible(selected: readonly string[], visibleGuids: readonly string[]): string[] {
  if (!selected.length) return selected as string[];
  const visible = new Set(visibleGuids);
  const next = selected.filter((guid) => visible.has(guid));
  return next.length === selected.length ? (selected as string[]) : next;
}
