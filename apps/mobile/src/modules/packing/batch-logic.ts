import { CARTON_STATUS, type PackCartonSummary, type PackVoidBlankResult } from "./types";
import { isUnusedBlank } from "./packing-logic";

// 批量箱标（预打印空箱标）的纯逻辑：数量、请求号复用、批次进度、断点续打、连续打印执行器、多选与作废结果。
// 界面与网络都不在这里；执行器的打印 / 上报通过参数注入，方便单测。

/** 一次最多创建的张数（后端 count 1..100）。 */
export const MAX_BATCH_COUNT = 100;
export const DEFAULT_BATCH_COUNT = 20;
/** 快捷数量。 */
export const BATCH_QUICK_COUNTS = [10, 20, 50, 100] as const;
/** 本地点未启用空箱标的上限（后端 409 BLANK_LIMIT_EXCEEDED 的 limit；界面只用来提示，不用来拦截）。 */
export const BLANK_CARTON_LIMIT = 300;
/** labels-printed / void-blank 一次最多 100 个。 */
export const MAX_GUIDS_PER_REQUEST = 100;
/** 每个箱号的打印份数上限（与 printPackCartonLabel 一致）。 */
export const MAX_BATCH_COPIES = 5;

export function clampBatchCount(value: number) {
  if (!Number.isFinite(value)) return 1;
  return Math.max(1, Math.min(MAX_BATCH_COUNT, Math.trunc(value)));
}

/** 数量输入框：只留数字；空串 / 0 无效返回 null；超过上限按上限处理。 */
export function parseBatchCountInput(text: string): number | null {
  const digits = text.replace(/\D/g, "");
  if (!digits) return null;
  const value = Number.parseInt(digits, 10);
  if (!Number.isFinite(value) || value < 1) return null;
  return Math.min(MAX_BATCH_COUNT, value);
}

/** 还能再建几张（本地点没启用的空箱标越多越少）；不知道已有张数返回 null。 */
export function blankRemaining(existing: number | null | undefined, limit = BLANK_CARTON_LIMIT) {
  return typeof existing === "number" ? Math.max(0, limit - existing) : null;
}

/**
 * 创建请求的 clientRequestId 复用：同样的请求内容失败重试沿用同一个号（服务端据此返回同一批，不重复创建）；
 * 数量 / 去向变了换新号；创建成功后 reset。
 */
export function createRequestIdReuse(generate: () => string) {
  let held: { key: string; id: string } | null = null;
  return {
    get(key: string) {
      if (!held || held.key !== key) held = { key, id: generate() };
      return held.id;
    },
    reset() {
      held = null;
    },
  };
}

export function batchRequestKey(input: {
  ownerCode: string;
  count: number;
  destinationType: number;
  destinationCode: string | null;
  carrierGuid: string | null;
}) {
  return [input.ownerCode, input.count, input.destinationType, input.destinationCode ?? "", input.carrierGuid ?? ""].join("|");
}

const KNOWN_BATCH_BUSINESS_CODES = new Set(["BATCH_NOT_FOUND", "CARTON_NOT_FOUND", "OWNER_NOT_ALLOWED", "OWNER_REQUIRED"]);

/**
 * 批次接口不存在（后端还没部署批量箱标）：404 且不是业务错误码；此时界面提示「当前服务器不支持」，不报错。
 * 注意 axios 的 error.code（ERR_BAD_REQUEST 之类）也是大写下划线，所以按「已知业务码」排除，而不是按格式。
 */
export function isBatchApiMissing(status: number | null, code: string | null): boolean {
  return status === 404 && !(code && KNOWN_BATCH_BUSINESS_CODES.has(code));
}

// ---- 批次进度与断点续打 ----

/** 批内顺序（batchSeq 升序，缺序号的放最后按箱号）；作废的箱不参与打印。 */
export function printableBatchCartons(cartons: readonly PackCartonSummary[]): PackCartonSummary[] {
  return cartons
    .filter((carton) => carton.status !== CARTON_STATUS.voided)
    .sort(
      (left, right) =>
        (left.batchSeq ?? Number.MAX_SAFE_INTEGER) - (right.batchSeq ?? Number.MAX_SAFE_INTEGER) ||
        left.cartonNo.localeCompare(right.cartonNo),
    );
}

export type BatchRowState = "printed" | "next" | "waiting";

export interface BatchRow {
  carton: PackCartonSummary;
  state: BatchRowState;
}

export interface BatchProgress {
  total: number;
  printed: number;
  /** 还没打印（labelPrintCount = 0）的张数。 */
  remaining: number;
  /** 下一张要打的（按批内顺序第一张没打印的）；全部打完为 null。 */
  next: PackCartonSummary | null;
  rows: BatchRow[];
  /** 0..1，进度条用。 */
  ratio: number;
}

/** 已打印以 labelPrintCount > 0 为准（本地打印成功后也会把它 +1），所以退出再进入、换设备都能恢复进度。 */
export function computeBatchProgress(cartons: readonly PackCartonSummary[]): BatchProgress {
  const ordered = printableBatchCartons([...cartons]);
  const next = ordered.find((carton) => carton.labelPrintCount === 0) ?? null;
  const rows = ordered.map<BatchRow>((carton) => ({
    carton,
    state: carton.labelPrintCount > 0 ? "printed" : carton === next ? "next" : "waiting",
  }));
  const printed = rows.filter((row) => row.state === "printed").length;
  return {
    total: ordered.length,
    printed,
    remaining: ordered.length - printed,
    next,
    rows,
    ratio: ordered.length === 0 ? 0 : printed / ordered.length,
  };
}

/** 「继续打印」要打的：只取还没打印的，已打印的不重打。 */
export function pendingLabelCartons(cartons: readonly PackCartonSummary[]): PackCartonSummary[] {
  return printableBatchCartons([...cartons]).filter((carton) => carton.labelPrintCount === 0);
}

/** 本地记一张：把这些箱的打印次数 +1（后端上报失败时界面也能先显示进度）。 */
export function markCartonsPrinted(cartons: readonly PackCartonSummary[], guids: readonly string[], printedAtUtc: string) {
  const set = new Set(guids.map((guid) => guid.toLowerCase()));
  return cartons.map((carton) =>
    set.has(carton.cartonGuid.toLowerCase())
      ? { ...carton, labelPrintCount: carton.labelPrintCount + 1, labelPrintedAtUtc: printedAtUtc }
      : carton,
  );
}

export function chunkGuids<T>(items: readonly T[], size = MAX_GUIDS_PER_REQUEST): T[][] {
  const chunks: T[][] = [];
  for (let index = 0; index < items.length; index += size) chunks.push(items.slice(index, index + size));
  return chunks;
}

// ---- 连续打印执行器 ----

export interface LabelQueueDeps {
  /** 打印一张（失败抛错）；调用方决定份数、样式。 */
  print: (carton: PackCartonSummary) => Promise<unknown>;
  /** 上报已打印（labels-printed）；失败不影响继续打印，未报上去的留在缓冲里，下一张成功后和结束时重试。 */
  report: (cartonGuids: string[]) => Promise<unknown>;
  /** 每打印成功一张就调用（本地记一张）。 */
  onPrinted?: (carton: PackCartonSummary) => void;
  /** 开始打某一张前调用，界面据此显示「打印中」。 */
  onStart?: (carton: PackCartonSummary) => void;
  /** 用户点了「暂停」：当前这张打完后停在下一张之前。 */
  shouldStop?: () => boolean;
}

export interface LabelQueueResult {
  printed: PackCartonSummary[];
  /** 打印失败（断连 / 卡纸等）停下的那一张；没有失败为 null。 */
  failed: { carton: PackCartonSummary; error: unknown } | null;
  /** 因 shouldStop 提前停下。 */
  stopped: boolean;
  /** 已打印但上报没成功的箱（结束时最后重试过一次仍失败）。 */
  unreported: string[];
}

/**
 * 一张一张打印：每成功一张就记一张并上报；遇到失败立刻停在这一张（它不记为已打印），
 * 退出（含失败 / 暂停）前必须把已成功的都报上去——结束时的最后一次上报在 finally 里做。
 */
export async function runLabelQueue(cartons: readonly PackCartonSummary[], deps: LabelQueueDeps): Promise<LabelQueueResult> {
  const printed: PackCartonSummary[] = [];
  let failed: LabelQueueResult["failed"] = null;
  let stopped = false;
  let buffer: string[] = [];

  const flush = async () => {
    if (buffer.length === 0) return;
    const pending = buffer;
    buffer = [];
    const unsent: string[] = [];
    for (const chunk of chunkGuids(pending)) {
      try {
        await deps.report(chunk);
      } catch {
        unsent.push(...chunk);
      }
    }
    buffer = unsent;
  };

  try {
    for (const carton of cartons) {
      if (deps.shouldStop?.()) {
        stopped = true;
        break;
      }
      deps.onStart?.(carton);
      try {
        await deps.print(carton);
      } catch (error) {
        failed = { carton, error };
        break;
      }
      printed.push(carton);
      buffer.push(carton.cartonGuid);
      deps.onPrinted?.(carton);
      await flush();
    }
  } finally {
    await flush();
  }
  return { printed, failed, stopped, unreported: [...buffer] };
}

// ---- 多选 ----

export function toggleSelected(selected: ReadonlySet<string>, guid: string): Set<string> {
  const next = new Set(selected);
  if (next.has(guid)) next.delete(guid);
  else next.add(guid);
  return next;
}

/** 全选 / 取消全选：已经全部选中就清空，否则选中列表里全部。 */
export function toggleSelectAll(cartons: readonly PackCartonSummary[], selected: ReadonlySet<string>): Set<string> {
  const all = cartons.map((carton) => carton.cartonGuid);
  return all.length > 0 && all.every((guid) => selected.has(guid)) ? new Set() : new Set(all);
}

/** 当前列表里仍存在的选中项（列表刷新后消失的自动剔除），保持列表顺序。 */
export function selectedCartons(cartons: readonly PackCartonSummary[], selected: ReadonlySet<string>) {
  return cartons.filter((carton) => selected.has(carton.cartonGuid));
}

/** 选中项里哪些是还没启用的空箱标（只有它们能作废）。 */
export function splitSelection(chosen: readonly PackCartonSummary[]) {
  const blanks = chosen.filter((carton) => isUnusedBlank(carton));
  return { blanks, others: chosen.filter((carton) => !isUnusedBlank(carton)) };
}

export interface VoidSummary {
  voided: number;
  /** 不是空箱标而被跳过的张数（客户端先剔除的 + 服务端 NOT_BLANK）。 */
  notBlank: number;
  /** 其他跳过（找不到等）。 */
  otherSkipped: number;
}

/** 作废结果汇总：selectedCount 是用户选的总数，sentCount 是真正发给后端的（只含空箱标）。 */
export function summarizeVoidResult(selectedCount: number, sentCount: number, result: PackVoidBlankResult | null): VoidSummary {
  const clientSkipped = Math.max(0, selectedCount - sentCount);
  if (!result) return { voided: 0, notBlank: clientSkipped, otherSkipped: 0 };
  const serverNotBlank = result.skipped.filter((item) => item.reason === "NOT_BLANK").length;
  return {
    voided: result.voided.length,
    notBlank: clientSkipped + serverNotBlank,
    otherSkipped: result.skipped.length - serverNotBlank,
  };
}

/** 空箱标在列表里的打印状态：没打印过 / 打印过几次与最近一次时间。 */
export function blankPrintStatus(carton: Pick<PackCartonSummary, "labelPrintCount" | "labelPrintedAtUtc">) {
  return carton.labelPrintCount > 0
    ? ({ kind: "printed", count: carton.labelPrintCount, at: carton.labelPrintedAtUtc } as const)
    : ({ kind: "notPrinted" } as const);
}
