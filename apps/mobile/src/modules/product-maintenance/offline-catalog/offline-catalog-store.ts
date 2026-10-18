/**
 * 离线商品目录的应用级状态（zustand）。
 *
 * - 懒打开 SQLite（仅离线资格会话调用 `open`）；
 * - 每店的 active 快照摘要；
 * - 刷新协调器状态（进度/成功/失败）供状态行与横幅订阅；
 * - `lookup` / `getDetail` 只读 active 快照，供商品查询页离线态使用。
 */
import { create } from "zustand";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { createOfflineCatalogTransport } from "@/modules/product-maintenance/api";
import type { ProductDetail, ProductLookupItem } from "@/modules/product-maintenance/types";
import { ExpoSqliteDriver } from "@/shared/db/expo-sqlite-driver";
import type { SqliteConnectionPort } from "@/shared/db/types";
import { buildOfflineLookupItems, buildOfflineProductDetail } from "./offline-catalog-detail";
import { applyOfflineCatalogMigrations, OFFLINE_CATALOG_DATABASE_NAME } from "./offline-catalog-migrations";
import {
  OfflineCatalogRefreshCoordinator,
  type OfflineCatalogRefreshState,
} from "./offline-catalog-refresh-coordinator";
import { createOfflineCatalogRemote } from "./offline-catalog-remote";
import { OfflineCatalogRepository } from "./offline-catalog-repository";
import {
  isOfflineCatalogCancellation,
  OfflineCatalogSyncService,
  type OfflineCatalogRefreshResult,
} from "./offline-catalog-sync-service";
import { getLeadingZeroBarcodeVariant } from "@/modules/scanner/barcode-variants";
import { normalizeOfflineLookupCode, type ActiveOfflineCatalogMetadata } from "./types";

interface OfflineCatalogRuntime {
  db: SqliteConnectionPort;
  repository: OfflineCatalogRepository;
  syncService: OfflineCatalogSyncService;
}

interface OfflineCatalogState {
  dbReady: boolean;
  dbError: string | null;
  activeMeta: Record<string, ActiveOfflineCatalogMetadata | null>;
  refresh: OfflineCatalogRefreshState;
  open: () => Promise<boolean>;
  close: () => Promise<void>;
  loadActiveMeta: (storeCode: string) => Promise<ActiveOfflineCatalogMetadata | null>;
  refreshCatalog: (storeCode: string) => Promise<OfflineCatalogRefreshResult | null>;
  cancelRefresh: () => void;
  lookup: (storeCode: string, keyword: string) => Promise<ProductLookupItem[]>;
  getDetail: (storeCode: string, productCode: string) => Promise<ProductDetail | null>;
}

let runtime: OfflineCatalogRuntime | null = null;
let openInFlight: Promise<boolean> | null = null;
const coordinator = new OfflineCatalogRefreshCoordinator();

function createSnapshotId(): string {
  return `snap-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

async function createRuntime(): Promise<OfflineCatalogRuntime> {
  const db = await new ExpoSqliteDriver().open(OFFLINE_CATALOG_DATABASE_NAME);
  await applyOfflineCatalogMigrations(db);
  const repository = new OfflineCatalogRepository(db);
  const remote = createOfflineCatalogRemote(createOfflineCatalogTransport());
  const syncService = new OfflineCatalogSyncService(repository, remote, { createSnapshotId });
  return { db, repository, syncService };
}

export const useOfflineCatalogStore = create<OfflineCatalogState>((set, get) => {
  return {
    dbReady: false,
    dbError: null,
    activeMeta: {},
    refresh: { kind: "idle" },

    async open() {
      if (isIosReviewSessionActive()) {
        // 审核态没有真实后端也没有离线数据，绝不打开数据库。
        return false;
      }
      if (runtime) {
        return true;
      }
      if (openInFlight) {
        return openInFlight;
      }
      openInFlight = (async () => {
        try {
          runtime = await createRuntime();
          set({ dbReady: true, dbError: null });
          return true;
        } catch (error) {
          const message = error instanceof Error ? error.message : String(error);
          console.warn("[offline-catalog] open database failed", { message });
          set({ dbReady: false, dbError: message });
          return false;
        } finally {
          openInFlight = null;
        }
      })();
      return openInFlight;
    },

    async close() {
      // 登出 / 解绑：中止进行中的下载，等它真正落定后再关库，否则 SQLite 句柄会被
      // 仍在写 staging 的任务持有；同时清空全部按店记账，避免下个会话读到上个会话的摘要。
      coordinator.cancel();
      const pendingOpen = openInFlight;
      openInFlight = null;
      const current = runtime;
      runtime = null;
      set({
        dbReady: false,
        dbError: null,
        activeMeta: {},
      });
      // 若关闭恰好撞上开库进行中，先等它结束，否则那次 IIFE 会把 runtime 重新赋值、
      // 让数据库在登出之后被「复活」。
      await pendingOpen?.catch(() => undefined);
      const revived = runtime as OfflineCatalogRuntime | null;
      runtime = null;
      await current?.db.close().catch(() => undefined);
      if (revived && revived !== current) {
        await revived.db.close().catch(() => undefined);
      }
      set({ dbReady: false });
    },

    async loadActiveMeta(storeCode) {
      const normalized = storeCode.trim();
      if (!normalized || !(await get().open()) || !runtime) {
        return null;
      }
      try {
        const meta = await runtime.repository.getActiveMetadata(normalized);
        set((state) => ({ activeMeta: { ...state.activeMeta, [normalized]: meta } }));
        return meta;
      } catch (error) {
        console.warn("[offline-catalog] load active metadata failed", {
          storeCode: normalized,
          message: error instanceof Error ? error.message : String(error),
        });
        return null;
      }
    },

    async refreshCatalog(storeCode) {
      const normalized = storeCode.trim();
      if (!normalized || !(await get().open()) || !runtime) {
        return null;
      }
      const activeRuntime = runtime;
      try {
        const result = await coordinator.start(normalized, ({ signal, onProgress }) =>
          activeRuntime.syncService.refresh({
            storeCode: normalized,
            signal,
            onProgress,
            retrySyncPlanGatewayTimeout: true,
          }),
        );
        set((state) => ({
          activeMeta: { ...state.activeMeta, [normalized]: result.metadata },
        }));
        return result;
      } catch (error) {
        if (isOfflineCatalogCancellation(error)) {
          return null;
        }
        console.warn("[offline-catalog] refresh failed", {
          storeCode: normalized,
          message: error instanceof Error ? error.message : String(error),
        });
        return null;
      }
    },

    cancelRefresh() {
      coordinator.cancel();
    },

    async lookup(storeCode, keyword) {
      // 与 loadActiveMeta / refreshCatalog 一致地走懒打开：否则数据库尚未就绪时
      // 返回空数组，会被上层解释成「本店没有这个商品」，与事实相反。
      if (!(await get().open()) || !runtime) {
        return [];
      }
      const normalizedKeyword = normalizeOfflineLookupCode(keyword);
      let rows = await runtime.repository.lookup(storeCode, normalizedKeyword);
      if (rows.length === 0) {
        // 精确匹配没有结果才回退到前导 0 的另一种写法：库里可能同时存在 12 位 X 与 13 位 0+X 两个不同商品，
        // 精确命中必须优先；回退只认条码来源，避免纯数字货号被变体误命中。
        const variant = getLeadingZeroBarcodeVariant(normalizedKeyword);
        if (variant) {
          rows = (await runtime.repository.lookup(storeCode, variant)).filter(
            (row) => row.matchSource.endsWith("Barcode"),
          );
        }
      }
      return buildOfflineLookupItems(rows, keyword);
    },

    async getDetail(storeCode, productCode) {
      if (!(await get().open()) || !runtime) {
        return null;
      }
      const rows = await runtime.repository.getProductRows(storeCode, productCode);
      return buildOfflineProductDetail(rows);
    },
  };
});

// store 建立后再订阅协调器，避免在 zustand 初始化期间调用 set。
coordinator.subscribe((refresh) => {
  useOfflineCatalogStore.setState({ refresh });
});

export function isOfflineCatalogRefreshRunning(): boolean {
  return coordinator.isRunning;
}
