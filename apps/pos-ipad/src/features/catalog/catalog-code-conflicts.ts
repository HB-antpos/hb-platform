import {
  mapCatalogLookupToStagedItem,
  type CatalogCodeConflictRefreshPort,
  type CatalogStagedItem,
} from "./catalog-snapshot-service";
import type { VerifiedCatalogCodeConflicts } from "./hbpos-catalog-remote";

import type { LocalCatalogMatch } from "@/core/db/catalog-repository";

/** 远端只需要提供已校验的候选响应；404/501 与网络错误由同步器统一收敛为保留旧数据。 */
export interface CatalogCodeConflictRemotePort {
  getCodeConflicts(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<VerifiedCatalogCodeConflicts>;
}

/** 由 SQLCipher 仓储实现：同一事务内整体替换该门店的候选，失败必须保留旧数据。 */
export interface CatalogCodeConflictStoragePort {
  replaceStoreConflicts(
    storeCode: string,
    items: readonly CatalogStagedItem[],
  ): Promise<unknown>;
}

export type CatalogCodeConflictSyncCode =
  | "CATALOG_CODE_CONFLICTS_REPLACED"
  | "CATALOG_CODE_CONFLICTS_NOT_AVAILABLE"
  | "CATALOG_CODE_CONFLICTS_UNSUPPORTED"
  | "CATALOG_CODE_CONFLICTS_CANCELLED"
  | "CATALOG_CODE_CONFLICTS_FAILED";

/** 只记录门店、数量和错误码；不保留商品、条码或远端正文。 */
export type CatalogCodeConflictSyncEvent = Readonly<{
  storeCode: string;
  outcome: "replaced" | "kept";
  code: CatalogCodeConflictSyncCode;
  errorCode?: string;
  httpStatus?: number;
  itemCount?: number;
}>;

export type CatalogCodeConflictSynchronizerOptions = Readonly<{
  remote: CatalogCodeConflictRemotePort;
  storage: CatalogCodeConflictStoragePort;
  onDiagnostic?: (event: CatalogCodeConflictSyncEvent) => void;
}>;

/**
 * 目录刷新成功（全量、增量与 noChange）后拉取码冲突候选并整体替换本地数据。
 * 服务端未算出（available=false）、旧服务端 404/501、网络或校验失败均只诊断并保留旧候选，
 * 绝不抛回目录刷新：候选是扫码选择的增强数据，不能让已激活的目录回滚或误报失败。
 */
export class CatalogCodeConflictSynchronizer
  implements CatalogCodeConflictRefreshPort
{
  private readonly onDiagnostic: (event: CatalogCodeConflictSyncEvent) => void;

  public constructor(
    private readonly options: CatalogCodeConflictSynchronizerOptions,
  ) {
    this.onDiagnostic = options.onDiagnostic ?? logCatalogCodeConflictSync;
  }

  public async refresh(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<CatalogCodeConflictSyncEvent> {
    const event = await this.run(input);
    try {
      this.onDiagnostic(event);
    } catch {
      // 中文注释：诊断输出故障不能影响目录刷新结果。
    }
    return event;
  }

  private async run(input: Readonly<{
    storeCode: string;
    signal?: AbortSignal;
  }>): Promise<CatalogCodeConflictSyncEvent> {
    const storeCode = input.storeCode;
    if (input.signal?.aborted) {
      return { storeCode, outcome: "kept", code: "CATALOG_CODE_CONFLICTS_CANCELLED" };
    }
    try {
      const response = await this.options.remote.getCodeConflicts({
        storeCode,
        ...(input.signal ? { signal: input.signal } : {}),
      });
      if (input.signal?.aborted) {
        return { storeCode, outcome: "kept", code: "CATALOG_CODE_CONFLICTS_CANCELLED" };
      }
      if (
        response.storeCode !== storeCode ||
        response.items.some((item) => item.storeCode !== storeCode)
      ) {
        return {
          storeCode,
          outcome: "kept",
          code: "CATALOG_CODE_CONFLICTS_FAILED",
          errorCode: "CATALOG_CODE_CONFLICTS_STORE_MISMATCH",
        };
      }
      if (!response.available) {
        return { storeCode, outcome: "kept", code: "CATALOG_CODE_CONFLICTS_NOT_AVAILABLE" };
      }
      // 中文注释：与目录分页共用分币换算与文本修复规则，候选价格口径与目录行一致。
      const items = response.items.map(mapCatalogLookupToStagedItem);
      await this.options.storage.replaceStoreConflicts(storeCode, items);
      return {
        storeCode,
        outcome: "replaced",
        code: "CATALOG_CODE_CONFLICTS_REPLACED",
        itemCount: items.length,
      };
    } catch (error) {
      const status = errorStatus(error);
      if (status === 404 || status === 501) {
        return {
          storeCode,
          outcome: "kept",
          code: "CATALOG_CODE_CONFLICTS_UNSUPPORTED",
          httpStatus: status,
        };
      }
      if (input.signal?.aborted) {
        return { storeCode, outcome: "kept", code: "CATALOG_CODE_CONFLICTS_CANCELLED" };
      }
      const errorCode = errorCodeOf(error);
      return {
        storeCode,
        outcome: "kept",
        code: "CATALOG_CODE_CONFLICTS_FAILED",
        ...(errorCode === undefined ? {} : { errorCode }),
        ...(status === undefined ? {} : { httpStatus: status }),
      };
    }
  }
}

/**
 * 与 WPF CatalogCodeConflictMerger 同一规则：只有目录（含在线覆盖层）里仍存在的码才补候选，
 * 旧冲突数据不能“复活”已删除或尚未同步的码；按商品编码去重，目录行保留自身版本并排在首位。
 */
export function mergeCatalogCodeConflictCandidates(
  primary: LocalCatalogMatch | null,
  conflicts: readonly LocalCatalogMatch[],
): readonly LocalCatalogMatch[] {
  if (primary === null) return [];
  if (conflicts.length === 0) return [primary];
  const primaryLookup = normalizeCatalogKey(primary.lookupCodeNormalized);
  const primaryStore = primary.storeCode;
  const presentProducts = new Set([normalizeCatalogKey(primary.productCode)]);
  const merged: LocalCatalogMatch[] = [primary];
  for (const candidate of conflicts) {
    if (
      candidate.storeCode !== primaryStore ||
      normalizeCatalogKey(candidate.lookupCodeNormalized) !== primaryLookup
    ) {
      continue;
    }
    const productKey = normalizeCatalogKey(candidate.productCode);
    if (presentProducts.has(productKey)) continue;
    presentProducts.add(productKey);
    merged.push(candidate);
  }
  return merged;
}

export type ExactCatalogCandidateSources = Readonly<{
  /** 目录精确查询（含在线覆盖层与 tombstone），返回当前唯一目录行。 */
  findExact(lookupCode: string): Promise<LocalCatalogMatch | null>;
  /** 按门店 + 规范化查询码点查本地冲突候选。 */
  findConflicts(
    storeCode: string,
    lookupCodeNormalized: string,
  ): Promise<readonly LocalCatalogMatch[]>;
}>;

/**
 * 扫码精确查询的全部候选。单商品码只多一次主键点查；候选表读取失败时退回目录单行，
 * 保证收银主路径不因增强数据异常而中断。
 */
export async function findExactCatalogCandidates(
  sources: ExactCatalogCandidateSources,
  lookupCode: string,
): Promise<readonly LocalCatalogMatch[]> {
  const primary = await sources.findExact(lookupCode);
  if (primary === null) return [];
  let conflicts: readonly LocalCatalogMatch[];
  try {
    conflicts = await sources.findConflicts(
      primary.storeCode,
      primary.lookupCodeNormalized,
    );
  } catch {
    return [primary];
  }
  return mergeCatalogCodeConflictCandidates(primary, conflicts);
}

function normalizeCatalogKey(value: string): string {
  return value.trim().toUpperCase();
}

function errorStatus(error: unknown): number | undefined {
  const status = (error as Readonly<{ status?: unknown }> | null)?.status;
  return typeof status === "number" ? status : undefined;
}

function errorCodeOf(error: unknown): string | undefined {
  const code = (error as Readonly<{ code?: unknown }> | null)?.code;
  return typeof code === "string" && code.length > 0 && code.length <= 128
    ? code
    : undefined;
}

function logCatalogCodeConflictSync(event: CatalogCodeConflictSyncEvent): void {
  if (event.outcome === "replaced") return;
  console.warn("[HBPOS][iPad][CatalogCodeConflicts]", JSON.stringify(event));
}
