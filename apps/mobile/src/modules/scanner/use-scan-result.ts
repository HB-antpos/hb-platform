import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AppState, type AppStateStatus } from "react-native";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { i18n } from "@/shared/i18n/i18n";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { resolveMinimumOrderQuantity } from "@/modules/shop/use-add-to-cart";
import { lookupProductsByBarcode } from "@/modules/scanner/api";
import { lookupStoreSupplyStatus } from "@/modules/supply-notice/api";
import type { StoreSupplyStatus } from "@/modules/supply-notice/types";
import { addToCart } from "@/modules/shop/api";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import {
  createScanTraceId,
  getScanPerformanceTimestamp,
  logScanPerformance,
} from "@/modules/scanner/scan-performance";
import {
  clearScanLookupInFlight,
  createScanLookupCache,
  createScanLookupInFlight,
  getCachedScanLookupProduct,
  rememberScanLookupProduct,
  runScanLookupInFlight,
} from "@/modules/scanner/scan-lookup-cache";
import { rememberScanLookupProductForMatch } from "@/modules/scanner/scan-lookup-policy";
import {
  canUpdateAddScanFeedback,
  shouldFlushCartSyncImmediately,
  shouldFlushCartSyncOnAppStateChange,
} from "@/modules/scanner/scan-result-lifecycle";
import {
  canCompleteScanJob,
  completeActiveScanJob,
  createInitialScanQueue,
  enqueueScanJob,
  startScanJob,
  type EnqueueScanDecision,
  type ScanQueueJob,
} from "@/modules/scanner/scan-queue";
import {
  getOptimisticCartMutationCache,
  isCurrentCartStore,
  resolveCartMutationBatchCache,
  snapshotCartMutationCache,
  syncCartMutationCache,
  type CartMutationCacheSnapshot,
} from "@/modules/shop/cart-cache";
import { useCartStore } from "@/store/cart-store";
import type {
  StoreOrderProductItem,
  StoreOrderProductListResult,
  StoreOrderScanLookupResult,
} from "@/modules/shop/types";
import type { ScanFeedbackState, ScanSelectionState, ScanSource } from "@/modules/scanner/types";

const scanQueueOptions = {
  duplicateWindowMs: 250,
  maxSize: 10,
};

const scanLookupCacheTtlMs = 60_000;
const cartSyncDelayMs = 1_000;
const cartSyncMaxBatchSize = 6;

const initialFeedback: ScanFeedbackState = {
  status: "ready",
  message: i18n.t("common:scanner.waiting"),
};

function normalizeStoreCode(storeCode?: string | null) {
  const normalized = storeCode?.trim();
  return normalized ? normalized : null;
}

function normalizeScanValue(value?: string | null) {
  return value?.trim().toUpperCase() ?? "";
}

function isProductListResult(value: unknown): value is StoreOrderProductListResult {
  return Boolean(
    value
    && typeof value === "object"
    && Array.isArray((value as StoreOrderProductListResult).items)
  );
}

function findCachedScanProducts(
  queryClient: QueryClient,
  storeCode: string,
  barcode: string
) {
  const scanValue = normalizeScanValue(barcode);
  if (!scanValue) {
    return [];
  }

  const matches = new Map<string, StoreOrderProductItem>();
  for (const [queryKey, data] of queryClient.getQueriesData<StoreOrderProductListResult>({ queryKey: ["shopProducts"] })) {
    if (!isProductListResult(data)) {
      continue;
    }

    const queryStoreCode = normalizeStoreCode(
      typeof queryKey[1] === "object" && queryKey[1] !== null
        ? (queryKey[1] as { storeCode?: string | null }).storeCode
        : null
    );
    if (queryStoreCode && queryStoreCode !== storeCode) {
      continue;
    }

    for (const product of data.items) {
      if (
        normalizeScanValue(product.barcode) === scanValue
        || normalizeScanValue(product.itemNumber) === scanValue
        || normalizeScanValue(product.productCode) === scanValue
      ) {
        matches.set(product.productCode, product);
      }
    }
  }

  return [...matches.values()];
}

interface UseScanResultOptions {
  autoAddWhenSingle?: boolean;
  mode?: "add-to-cart" | "lookup";
  onAddedToCart?: (product: StoreOrderProductItem, barcode: string, source: ScanSource, scanTraceId?: string) => void | Promise<void>;
  onProductFound?: (
    product: StoreOrderProductItem,
    barcode: string,
    source: ScanSource,
    scanTraceId?: string,
    storeCode?: string | null
  ) => void | Promise<void>;
  storeCode?: string | null;
}

interface PendingCartSyncEntry {
  product: StoreOrderProductItem;
  quantity: number;
  scanTraceId?: string;
  snapshot: CartMutationCacheSnapshot;
  storeCode: string;
}

export function useScanResult({
  autoAddWhenSingle = false,
  mode = "add-to-cart",
  onAddedToCart,
  onProductFound,
  storeCode,
}: UseScanResultOptions) {
  const queryClient = useQueryClient();
  const selectedStoreCodeForSync = normalizeStoreCode(storeCode);
  const [feedback, setFeedback] = useState<ScanFeedbackState>(initialFeedback);
  const [selectionState, setSelectionState] = useState<ScanSelectionState | null>(null);
  const [isLookupPending, setIsLookupPending] = useState(false);
  const isHandlingBarcodeRef = useRef(false);
  const activeAddScanCountRef = useRef(0);
  const lookupCacheRef = useRef(createScanLookupCache(scanLookupCacheTtlMs));
  const scanLookupAddInFlightRef = useRef(createScanLookupInFlight<StoreOrderScanLookupResult>());
  const pendingCartSyncRef = useRef<PendingCartSyncEntry[]>([]);
  const cartSyncTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const isFlushingCartSyncRef = useRef(false);
  const flushCartSyncQueueRef = useRef<() => Promise<void>>(async () => undefined);
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);
  const isMountedRef = useRef(true);
  const latestAddScanTraceIdRef = useRef<string | null>(null);
  const scanQueueRef = useRef(createInitialScanQueue());
  const scanGenerationRef = useRef(0);
  const selectionStateRef = useRef<ScanSelectionState | null>(null);
  const storeCodeRef = useRef<string | null>(normalizeStoreCode(storeCode));
  const drainScanQueueRef = useRef<() => void>(() => undefined);
  const setCartSummary = useCartStore((state) => state.setCartSummary);
  const adjustCartSyncPending = useCartStore((state) => state.adjustCartSyncPending);
  const cartSyncPendingCount = useCartStore((state) =>
    selectedStoreCodeForSync ? state.cartSyncPendingByStore[selectedStoreCodeForSync] ?? 0 : 0
  );

  storeCodeRef.current = normalizeStoreCode(storeCode);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  const updateFeedback = useCallback((nextFeedback: ScanFeedbackState) => {
    if (!isMountedRef.current) {
      logScanPerformance("scan.feedback.ignored.unmounted", {
        status: nextFeedback.status,
        barcode: nextFeedback.barcode,
      });
      return;
    }

    setFeedback(nextFeedback);
    void playScanFeedbackSound(nextFeedback.status);
  }, []);

  const updateLookupPendingState = useCallback((nextPending: boolean) => {
    if (isMountedRef.current) {
      setIsLookupPending(nextPending);
    }
  }, []);

  const applyScanFeedback = useCallback((
    nextFeedback: ScanFeedbackState,
    options: { isAddMode?: boolean; scanTraceId?: string; sound?: boolean } = {}
  ) => {
    if (!isMountedRef.current) {
      logScanPerformance("scan.feedback.ignored.unmounted", {
        status: nextFeedback.status,
        barcode: nextFeedback.barcode,
      });
      return false;
    }

    // 只让最新加购扫码更新可见反馈；旧请求仍继续完成加购和后端同步。
    if (
      options.isAddMode
      && !canUpdateAddScanFeedback(latestAddScanTraceIdRef.current, options.scanTraceId)
    ) {
      logScanPerformance("scan.feedback.ignored.stale-add-scan", {
        scanTraceId: options.scanTraceId,
        latestScanTraceId: latestAddScanTraceIdRef.current,
        status: nextFeedback.status,
        barcode: nextFeedback.barcode,
      });
      return false;
    }

    if (options.sound === false) {
      setFeedback(nextFeedback);
      return true;
    }

    updateFeedback(nextFeedback);
    return true;
  }, [updateFeedback]);

  const updateSelectionState = useCallback((nextSelectionState: ScanSelectionState | null) => {
    selectionStateRef.current = nextSelectionState;
    setSelectionState(nextSelectionState);
  }, []);

  const isCurrentStoreJob = useCallback((job: ScanQueueJob) => {
    return normalizeStoreCode(job.storeCode) === storeCodeRef.current;
  }, []);

  const logStaleStoreJob = useCallback((stage: string, job: ScanQueueJob) => {
    logScanPerformance("scan.ignored.stale-store", {
      scanTraceId: job.scanTraceId,
      barcode: job.barcode,
      source: job.source,
      stage,
      storeCode: normalizeStoreCode(job.storeCode),
      currentStoreCode: storeCodeRef.current,
    });
  }, []);

  const logEnqueueDecision = useCallback(
    (decision: EnqueueScanDecision, job: ScanQueueJob) => {
      const basePayload = {
        scanTraceId: job.scanTraceId,
        barcode: job.barcode,
        source: job.source,
        storeCode: normalizeStoreCode(job.storeCode),
        queueSize: decision.queueSize,
      };

      if (decision.type === "duplicate") {
        logScanPerformance("scan.ignored.duplicate", {
          ...basePayload,
          reason: decision.reason,
        });
        return;
      }

      if (decision.type === "overflow") {
        logScanPerformance("scan.queue.overflow", {
          ...basePayload,
          droppedScanTraceId: decision.droppedJob?.scanTraceId,
        });
        return;
      }

      logScanPerformance("scan.queued.busy", basePayload);
    },
    []
  );

  const scheduleCartSyncFlush = useCallback(() => {
    if (cartSyncTimerRef.current) {
      clearTimeout(cartSyncTimerRef.current);
      cartSyncTimerRef.current = null;
    }

    cartSyncTimerRef.current = setTimeout(
      () => {
        cartSyncTimerRef.current = null;
        void flushCartSyncQueueRef.current();
      },
      cartSyncDelayMs
    );
  }, []);

  const flushCartSyncQueue = useCallback(async () => {
    if (isFlushingCartSyncRef.current || pendingCartSyncRef.current.length === 0) {
      return;
    }

    if (cartSyncTimerRef.current) {
      clearTimeout(cartSyncTimerRef.current);
      cartSyncTimerRef.current = null;
    }

    const entries = pendingCartSyncRef.current;
    pendingCartSyncRef.current = [];
    isFlushingCartSyncRef.current = true;

    try {
      const batches = new Map<string, PendingCartSyncEntry[]>();
      for (const entry of entries) {
        const key = `${entry.storeCode}:${entry.product.productCode}`;
        batches.set(key, [...(batches.get(key) ?? []), entry]);
      }

      for (const batchEntries of batches.values()) {
        const first = batchEntries[0]!;
        const quantity = batchEntries.reduce((sum, entry) => sum + entry.quantity, 0);
        const syncStartedAt = getScanPerformanceTimestamp();
        logScanPerformance("cart.add.batch-sync.start", {
          scanTraceId: first.scanTraceId,
          storeCode: first.storeCode,
          productCode: first.product.productCode,
          quantity,
          batchSize: batchEntries.length,
        });

        try {
          const cart = await addToCart(
            {
              storeCode: first.storeCode,
              productCode: first.product.productCode,
              quantity,
              importPrice: first.product.importPrice,
            },
            first.scanTraceId
          );
          const nextCart = resolveCartMutationBatchCache(
            queryClient,
            first.storeCode,
            batchEntries.map((entry) => entry.snapshot),
            cart
          );
          const currentStoreCode = useCartStore.getState().selectedStore?.storeCode ?? null;
          if (isCurrentCartStore(currentStoreCode, first.storeCode)) {
            setCartSummary(nextCart);
          }
          logScanPerformance("cart.add.batch-sync.done", {
            scanTraceId: first.scanTraceId,
            storeCode: first.storeCode,
            productCode: first.product.productCode,
            quantity,
            batchSize: batchEntries.length,
            totalQuantity: nextCart?.totalQuantity ?? 0,
            elapsedMs: getScanPerformanceTimestamp() - syncStartedAt,
          });
        } catch (error) {
          const nextCart = resolveCartMutationBatchCache(
            queryClient,
            first.storeCode,
            batchEntries.map((entry) => entry.snapshot)
          );
          const currentStoreCode = useCartStore.getState().selectedStore?.storeCode ?? null;
          if (isCurrentCartStore(currentStoreCode, first.storeCode)) {
            setCartSummary(nextCart ?? null);
          }
          logScanPerformance("cart.add.batch-sync.error", {
            scanTraceId: first.scanTraceId,
            storeCode: first.storeCode,
            productCode: first.product.productCode,
            quantity,
            batchSize: batchEntries.length,
            elapsedMs: getScanPerformanceTimestamp() - syncStartedAt,
            error: error instanceof Error ? error.message : String(error),
          });
          if (isMountedRef.current) {
            updateFeedback({
              status: "error",
              message: resolveLocalizedErrorMessage(error, {
                language: i18n.resolvedLanguage ?? "zh",
                t: i18n.t.bind(i18n),
                fallbackKey: "common:scanner.failed",
              }),
              productName: first.product.productName,
            });
          }
        } finally {
          adjustCartSyncPending(first.storeCode, -batchEntries.length);
        }
      }
    } finally {
      isFlushingCartSyncRef.current = false;
      if (pendingCartSyncRef.current.length > 0) {
        // 退后台/卸载后不能依赖 timer；批量满时继续立即刷剩余队列。
        if (
          shouldFlushCartSyncImmediately(
            pendingCartSyncRef.current.length,
            cartSyncMaxBatchSize,
            appStateRef.current,
            isMountedRef.current
          )
        ) {
          void flushCartSyncQueueRef.current();
        } else {
          scheduleCartSyncFlush();
        }
      }
    }
  }, [adjustCartSyncPending, queryClient, scheduleCartSyncFlush, setCartSummary, updateFeedback]);

  useEffect(() => {
    flushCartSyncQueueRef.current = flushCartSyncQueue;
  }, [flushCartSyncQueue]);

  useEffect(() => {
    const subscription = AppState.addEventListener("change", (nextState) => {
      const previousState = appStateRef.current;
      appStateRef.current = nextState;

      // 退后台前尽快刷新同步队列，避免 1 秒 timer 还没触发就被系统挂起。
      if (shouldFlushCartSyncOnAppStateChange(previousState, nextState)) {
        void flushCartSyncQueueRef.current();
      }
    });

    return () => {
      subscription.remove();
    };
  }, []);

  useEffect(
    () => {
      isMountedRef.current = true;
      return () => {
        isMountedRef.current = false;
        if (cartSyncTimerRef.current) {
          clearTimeout(cartSyncTimerRef.current);
          cartSyncTimerRef.current = null;
        }
        void flushCartSyncQueueRef.current();
      };
    },
    []
  );

  const completeAddedProduct = useCallback(
    async (
      product: StoreOrderProductItem,
      barcode: string,
      source: ScanSource,
      scanTraceId: string | undefined,
      expectedStoreCode: string | null,
      quantity: number,
      expectedGeneration?: number
    ) => {
      if (expectedStoreCode !== storeCodeRef.current) {
        logScanPerformance("cart.add.frontend.ignored.stale-store", {
          scanTraceId,
          barcode,
          source,
          storeCode: expectedStoreCode,
          currentStoreCode: storeCodeRef.current,
          productCode: product.productCode,
        });
        return;
      }
      if (expectedGeneration !== undefined && expectedGeneration !== scanGenerationRef.current) {
        logScanPerformance("cart.add.frontend.ignored.stale-generation", {
          scanTraceId,
          barcode,
          source,
          storeCode: expectedStoreCode,
          productCode: product.productCode,
          expectedGeneration,
          currentGeneration: scanGenerationRef.current,
        });
        return;
      }
      if (!canUpdateAddScanFeedback(latestAddScanTraceIdRef.current, scanTraceId)) {
        logScanPerformance("cart.add.frontend.ignored.stale-add-scan", {
          scanTraceId,
          barcode,
          source,
          storeCode: expectedStoreCode,
          productCode: product.productCode,
          latestScanTraceId: latestAddScanTraceIdRef.current,
        });
        return;
      }
      if (!isMountedRef.current) {
        logScanPerformance("cart.add.frontend.ignored.unmounted", {
          scanTraceId,
          barcode,
          source,
          storeCode: expectedStoreCode,
          productCode: product.productCode,
        });
        return;
      }

      await onAddedToCart?.(product, barcode, source, scanTraceId);
      if (expectedGeneration !== undefined && expectedGeneration !== scanGenerationRef.current) {
        logScanPerformance("cart.add.frontend.ignored.stale-generation", {
          scanTraceId,
          barcode,
          source,
          storeCode: expectedStoreCode,
          productCode: product.productCode,
          expectedGeneration,
          currentGeneration: scanGenerationRef.current,
        });
        return;
      }

      applyScanFeedback(
        {
          status: "added",
          message: i18n.t(source === "camera" ? "common:scanner.addedByCamera" : "common:scanner.addedByScanner"),
          barcode,
          productName: product.productName,
          addedQuantity: quantity,
        },
        { isAddMode: true, scanTraceId }
      );
    },
    [applyScanFeedback, onAddedToCart]
  );

  const addMatchedProduct = useCallback(
    async (
      product: StoreOrderProductItem,
      barcode: string,
      source: ScanSource,
      scanTraceId: string | undefined,
      expectedStoreCode: string | null,
      expectedGeneration?: number
    ) => {
      if (!expectedStoreCode) {
        throw new Error(i18n.t("common:errors.selectStoreFirst"));
      }
      if (expectedGeneration !== undefined && expectedGeneration !== scanGenerationRef.current) {
        logScanPerformance("cart.add.local.ignored.stale-generation", {
          scanTraceId,
          barcode,
          source,
          storeCode: expectedStoreCode,
          productCode: product.productCode,
          expectedGeneration,
          currentGeneration: scanGenerationRef.current,
        });
        return;
      }

      applyScanFeedback(
        {
          status: "found",
          message: i18n.t("common:scanner.addingToCart"),
          barcode,
          productName: product.productName,
        },
        { isAddMode: true, scanTraceId }
      );

      const quantity = resolveMinimumOrderQuantity(product);
      const addStartedAt = getScanPerformanceTimestamp();
      logScanPerformance("cart.add.frontend.start", {
        scanTraceId,
        barcode,
        source,
        storeCode: expectedStoreCode,
        productCode: product.productCode,
        quantity,
      });
      const snapshot = snapshotCartMutationCache(queryClient, expectedStoreCode, {
        product,
        quantity,
        type: "add",
      });
      const optimisticCart = getOptimisticCartMutationCache(queryClient, expectedStoreCode);
      syncCartMutationCache(queryClient, expectedStoreCode, optimisticCart, product.productCode);
      const currentStoreCode = useCartStore.getState().selectedStore?.storeCode ?? null;
      if (isCurrentCartStore(currentStoreCode, expectedStoreCode)) {
        setCartSummary(optimisticCart);
      }

      pendingCartSyncRef.current.push({
        product,
        quantity,
        scanTraceId,
        snapshot,
        storeCode: expectedStoreCode,
      });
      adjustCartSyncPending(expectedStoreCode, 1);
      const pendingQuantity = pendingCartSyncRef.current.reduce((sum, entry) => sum + entry.quantity, 0);
      const pendingBatchSize = pendingCartSyncRef.current.length;
      if (
        shouldFlushCartSyncImmediately(
          pendingBatchSize,
          cartSyncMaxBatchSize,
          appStateRef.current,
          isMountedRef.current
        )
      ) {
        void flushCartSyncQueueRef.current();
      } else {
        scheduleCartSyncFlush();
      }

      logScanPerformance("cart.add.local.done", {
        scanTraceId,
        barcode,
        source,
        storeCode: expectedStoreCode,
        productCode: product.productCode,
        quantity,
        pendingQuantity,
        pendingBatchSize,
        totalQuantity: optimisticCart?.totalQuantity ?? 0,
        elapsedMs: getScanPerformanceTimestamp() - addStartedAt,
      });
      await completeAddedProduct(
        product,
        barcode,
        source,
        scanTraceId,
        expectedStoreCode,
        quantity,
        expectedGeneration
      );
    },
    [adjustCartSyncPending, applyScanFeedback, completeAddedProduct, queryClient, scheduleCartSyncFlush, setCartSummary]
  );

  const processScanJob = useCallback(
    async (job: ScanQueueJob) => {
      const { barcode, scanTraceId, source } = job;
      const activeStoreCode = normalizeStoreCode(job.storeCode);
      const scanStartedAt = job.receivedAt ?? getScanPerformanceTimestamp();
      const jobGeneration = scanGenerationRef.current;
      const isAddMode = mode === "add-to-cart";
      if (isAddMode) {
        activeAddScanCountRef.current += 1;
      } else {
        isHandlingBarcodeRef.current = true;
      }
      updateLookupPendingState(true);

      try {
        if (!isCurrentStoreJob(job)) {
          logStaleStoreJob("before-lookup", job);
          return;
        }

        if (!activeStoreCode) {
          logScanPerformance("scan.blocked.no-store", {
            scanTraceId,
            barcode,
            source,
          });
          applyScanFeedback(
            {
              status: "blocked",
              message: i18n.t("common:scanner.selectStoreFirst"),
              barcode,
            },
            { isAddMode, scanTraceId }
          );
          return;
        }

        applyScanFeedback(
          {
            status: "scanning",
            message: i18n.t("common:scanner.lookupInProgress"),
            barcode,
          },
          { isAddMode, scanTraceId, sound: false }
        );

        if (isAddMode) {
          const cachedProduct = getCachedScanLookupProduct(
            lookupCacheRef.current,
            activeStoreCode,
            barcode,
            getScanPerformanceTimestamp()
          );
          if (cachedProduct) {
            logScanPerformance("scan.lookup.frontend.cache-hit", {
              scanTraceId,
              barcode,
              source,
              storeCode: activeStoreCode,
              productCode: cachedProduct.productCode,
            });
            await addMatchedProduct(cachedProduct, barcode, source, scanTraceId, activeStoreCode, jobGeneration);
            logScanPerformance("scan.add-flow.done", {
              scanTraceId,
              barcode,
              source,
              storeCode: activeStoreCode,
              productCode: cachedProduct.productCode,
              cacheHit: true,
              totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
            });
            return;
          }

          const cachedProducts = findCachedScanProducts(queryClient, activeStoreCode, barcode);
          if (cachedProducts.length === 1) {
            const product = cachedProducts[0]!;
            rememberScanLookupProduct(
              lookupCacheRef.current,
              activeStoreCode,
              barcode,
              product,
              getScanPerformanceTimestamp()
            );
            logScanPerformance("scan.lookup.frontend.product-cache-hit", {
              scanTraceId,
              barcode,
              source,
              storeCode: activeStoreCode,
              productCode: product.productCode,
              totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
            });
            await addMatchedProduct(product, barcode, source, scanTraceId, activeStoreCode, jobGeneration);
            logScanPerformance("scan.add-flow.local-done", {
              scanTraceId,
              barcode,
              source,
              storeCode: activeStoreCode,
              productCode: product.productCode,
              localProductCacheHit: true,
              totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
            });
            return;
          }

          if (cachedProducts.length > 1) {
            const canShowMultiple = applyScanFeedback(
              {
                status: "multiple",
                message: i18n.t("common:scanner.multipleCart"),
                barcode,
              },
              { isAddMode: true, scanTraceId }
            );
            if (canShowMultiple) {
              updateSelectionState({
                barcode,
                scanTraceId,
                storeCode: activeStoreCode,
                source,
                items: cachedProducts,
              });
            }
            logScanPerformance("scan.multiple-found.product-cache-hit", {
              scanTraceId,
              barcode,
              source,
              storeCode: activeStoreCode,
              itemCount: cachedProducts.length,
              totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
            });
            return;
          }
        }

        const lookupStartedAt = getScanPerformanceTimestamp();
        const lookupStage = "scan.lookup.frontend";
        logScanPerformance(`${lookupStage}.start`, {
          scanTraceId,
          barcode,
          source,
          storeCode: activeStoreCode,
        });
        let sharedScanLookup = false;
        let result: StoreOrderScanLookupResult;
        if (isAddMode) {
          const lookupResult = await runScanLookupInFlight(
            scanLookupAddInFlightRef.current,
            activeStoreCode,
            barcode,
            () => lookupProductsByBarcode(barcode, activeStoreCode, scanTraceId)
          );
          result = lookupResult.result;
          sharedScanLookup = lookupResult.shared;
        } else {
          result = await lookupProductsByBarcode(barcode, activeStoreCode, scanTraceId);
        }
        logScanPerformance(`${lookupStage}.done`, {
          scanTraceId,
          barcode: result.barcode,
          source,
          storeCode: activeStoreCode,
          itemCount: result.items?.length ?? 0,
          shared: sharedScanLookup,
          elapsedMs: getScanPerformanceTimestamp() - lookupStartedAt,
        });
        if (jobGeneration !== scanGenerationRef.current) {
          logScanPerformance("scan.ignored.stale-generation", {
            scanTraceId,
            barcode: result.barcode,
            source,
            storeCode: activeStoreCode,
            jobGeneration,
            currentGeneration: scanGenerationRef.current,
          });
          return;
        }
        if (!isCurrentStoreJob(job)) {
          logStaleStoreJob("after-lookup", job);
          return;
        }

        const items = result.items ?? [];

        if (isAddMode && items.length === 1) {
          rememberScanLookupProductForMatch(
            lookupCacheRef.current,
            activeStoreCode,
            result.barcode,
            items[0],
            getScanPerformanceTimestamp(),
            result.matchType,
          );
          await addMatchedProduct(items[0], result.barcode, source, scanTraceId, activeStoreCode, jobGeneration);
          logScanPerformance("scan.add-flow.local-done", {
            scanTraceId,
            barcode: result.barcode,
            source,
            storeCode: activeStoreCode,
            productCode: items[0].productCode,
            sharedLookup: sharedScanLookup,
            totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
          });
          return;
        }

        if (items.length === 0) {
          logScanPerformance("scan.not-found", {
            scanTraceId,
            barcode: result.barcode,
            source,
            storeCode: activeStoreCode,
            totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
          });
          // 扫到的是仓库暂停供货的商品，而不是扫错码：提示区分开，并在首页零结果处展示恢复计划。
          let supplyStatus: StoreSupplyStatus | undefined;
          try {
            supplyStatus = (await lookupStoreSupplyStatus(activeStoreCode, result.barcode))[0];
          } catch {
            supplyStatus = undefined;
          }
          if (!isCurrentStoreJob(job)) {
            logStaleStoreJob("after-supply-lookup", job);
            return;
          }
          if (supplyStatus) {
            // 暂停供货用独立状态：提示文案、图标和提示音都与「未找到」区分，店员不看屏幕也能听出来。
            applyScanFeedback(
              {
                status: "supply_paused",
                message: i18n.t("supplyNotice:scanPaused"),
                barcode: result.barcode,
                productName: supplyStatus.productName || supplyStatus.productCode,
                itemNumber: supplyStatus.itemNumber || supplyStatus.productCode,
                pausedSupply: true,
                supplyStatus,
              },
              { isAddMode, scanTraceId }
            );
            return;
          }
          applyScanFeedback(
            {
              status: "not_found",
              message: i18n.t("common:scanner.notFound"),
              barcode: result.barcode,
              pausedSupply: false,
            },
            { isAddMode, scanTraceId }
          );
          return;
        }

        if (items.length === 1) {
          if (mode === "lookup") {
            updateFeedback({
              status: "found",
              message: i18n.t("common:scanner.found"),
              barcode: result.barcode,
              productName: items[0].productName,
            });
            logScanPerformance("scan.single-found", {
              scanTraceId,
              barcode: result.barcode,
              source,
              storeCode: activeStoreCode,
              productCode: items[0].productCode,
              totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
            });
            await onProductFound?.(items[0], result.barcode, source, scanTraceId, activeStoreCode);
            logScanPerformance("scan.lookup-flow.done", {
              scanTraceId,
              barcode: result.barcode,
              source,
              storeCode: activeStoreCode,
              productCode: items[0].productCode,
              totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
            });
            return;
          }

          await addMatchedProduct(items[0], result.barcode, source, scanTraceId, activeStoreCode, jobGeneration);
          logScanPerformance("scan.add-flow.done", {
            scanTraceId,
            barcode: result.barcode,
            source,
            storeCode: activeStoreCode,
            productCode: items[0].productCode,
            totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
          });
          return;
        }

        const canShowMultiple = applyScanFeedback(
          {
            status: "multiple",
            message: i18n.t(mode === "lookup" ? "common:scanner.multipleLookup" : "common:scanner.multipleCart"),
            barcode: result.barcode,
          },
          { isAddMode, scanTraceId }
        );
        if (canShowMultiple) {
          updateSelectionState({
            barcode: result.barcode,
            scanTraceId,
            storeCode: activeStoreCode,
            source,
            items,
          });
        }
        logScanPerformance("scan.multiple-found", {
          scanTraceId,
          barcode: result.barcode,
          source,
          storeCode: activeStoreCode,
          itemCount: items.length,
          totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
        });
      } catch (error) {
        if (jobGeneration !== scanGenerationRef.current) {
          logScanPerformance("scan.error.ignored.stale-generation", {
            scanTraceId,
            barcode,
            source,
            storeCode: activeStoreCode,
            jobGeneration,
            currentGeneration: scanGenerationRef.current,
            error: error instanceof Error ? error.message : String(error),
          });
          return;
        }
        if (!isCurrentStoreJob(job)) {
          logStaleStoreJob("error", job);
          return;
        }

        logScanPerformance("scan.error", {
          scanTraceId,
          barcode,
          source,
          storeCode: activeStoreCode,
          totalElapsedMs: getScanPerformanceTimestamp() - scanStartedAt,
          error: error instanceof Error ? error.message : String(error),
        });
        applyScanFeedback(
          {
            status: "error",
            message: resolveLocalizedErrorMessage(error, {
              language: i18n.resolvedLanguage ?? "zh",
              t: i18n.t.bind(i18n),
              fallbackKey: "common:scanner.failed",
            }),
            barcode,
          },
          { isAddMode, scanTraceId }
        );
      } finally {
        if (isAddMode) {
          if (jobGeneration === scanGenerationRef.current) {
            activeAddScanCountRef.current = Math.max(0, activeAddScanCountRef.current - 1);
            updateLookupPendingState(activeAddScanCountRef.current > 0);
          } else {
            logScanPerformance("scan.finally.ignored.stale-generation", {
              scanTraceId,
              barcode,
              source,
              storeCode: activeStoreCode,
              jobGeneration,
              currentGeneration: scanGenerationRef.current,
            });
          }
          return;
        }

        if (!canCompleteScanJob(scanQueueRef.current, job, jobGeneration, scanGenerationRef.current)) {
          logScanPerformance("scan.finally.ignored.stale-job", {
            scanTraceId,
            barcode,
            source,
            storeCode: activeStoreCode,
            currentActiveScanTraceId: scanQueueRef.current.active?.scanTraceId,
            jobGeneration,
            currentGeneration: scanGenerationRef.current,
          });
          return;
        }

        isHandlingBarcodeRef.current = false;
        updateLookupPendingState(false);
        const completed = completeActiveScanJob(scanQueueRef.current);
        scanQueueRef.current = completed.queue;
        drainScanQueueRef.current();
      }
    },
    [
      addMatchedProduct,
      applyScanFeedback,
      isCurrentStoreJob,
      logStaleStoreJob,
      mode,
      onProductFound,
      queryClient,
      updateFeedback,
      updateLookupPendingState,
      updateSelectionState,
    ]
  );

  const drainScanQueue = useCallback(() => {
    // 多候选弹窗期间不消费下一条，避免用户选择上下文被后续扫码覆盖。
    if (selectionStateRef.current || isHandlingBarcodeRef.current) {
      return;
    }

    let nextJob = scanQueueRef.current.active;
    if (!nextJob && scanQueueRef.current.pending.length > 0) {
      const completed = completeActiveScanJob(scanQueueRef.current);
      scanQueueRef.current = completed.queue;
      nextJob = completed.nextJob;
    }

    if (!nextJob) {
      return;
    }

    void processScanJob(nextJob);
  }, [processScanJob]);

  useEffect(() => {
    drainScanQueueRef.current = drainScanQueue;
  }, [drainScanQueue]);

  useEffect(() => {
    // 门店切换时丢弃旧门店上下文，避免旧扫码在新门店下继续显示或加购。
    scanGenerationRef.current += 1;
    scanQueueRef.current = createInitialScanQueue();
    lookupCacheRef.current = createScanLookupCache(scanLookupCacheTtlMs);
    clearScanLookupInFlight(scanLookupAddInFlightRef.current);
    isHandlingBarcodeRef.current = false;
    activeAddScanCountRef.current = 0;
    latestAddScanTraceIdRef.current = null;
    updateLookupPendingState(false);
    updateSelectionState(null);
    setFeedback(initialFeedback);
  }, [storeCode, updateLookupPendingState, updateSelectionState]);

  const handleBarcode = useCallback(
    async (barcode: string, source: ScanSource) => {
      const scanTraceId = createScanTraceId(source, barcode);
      const scanStartedAt = getScanPerformanceTimestamp();
      const job: ScanQueueJob = {
        barcode,
        scanTraceId,
        receivedAt: scanStartedAt,
        source,
        storeCode: normalizeStoreCode(storeCode),
      };
      logScanPerformance("scan.received", {
        scanTraceId,
        barcode,
        source,
        storeCode: job.storeCode,
      });

      if (mode === "add-to-cart") {
        if (selectionStateRef.current) {
          logScanPerformance("scan.dropped.selection-open", {
            scanTraceId,
            barcode,
            source,
            storeCode: job.storeCode,
          });
          return;
        }

        latestAddScanTraceIdRef.current = scanTraceId;
        if (!job.storeCode) {
          logScanPerformance("scan.blocked.no-store", {
            scanTraceId,
            barcode,
            source,
          });
          applyScanFeedback(
            {
              status: "blocked",
              message: i18n.t("common:scanner.selectStoreFirst"),
              barcode,
            },
            { isAddMode: true, scanTraceId }
          );
          return;
        }

        // 加购模式不进队列：连续扫码每条都并发启动，缓存命中直接 scan-add。
        void processScanJob(job);
        return;
      }

      if (isHandlingBarcodeRef.current || selectionStateRef.current) {
        const enqueued = enqueueScanJob(scanQueueRef.current, job, scanStartedAt, scanQueueOptions);
        scanQueueRef.current = enqueued.queue;
        logEnqueueDecision(enqueued.decision, job);
        return;
      }

      if (!job.storeCode) {
        logScanPerformance("scan.blocked.no-store", {
          scanTraceId,
          barcode,
          source,
        });
        updateFeedback({
          status: "blocked",
          message: i18n.t("common:scanner.selectStoreFirst"),
          barcode,
        });
        return;
      }

      const started = startScanJob(scanQueueRef.current, job, scanStartedAt, scanQueueOptions);
      scanQueueRef.current = started.queue;
      if (started.decision.type === "duplicate") {
        logScanPerformance("scan.ignored.duplicate", {
          scanTraceId,
          barcode,
          source,
          storeCode: job.storeCode,
          queueSize: started.decision.queueSize,
          reason: started.decision.reason,
        });
        return;
      }

      await processScanJob(job);
    },
    [applyScanFeedback, logEnqueueDecision, mode, processScanJob, storeCode, updateFeedback]
  );

  const confirmSelection = useCallback(
    async (product: StoreOrderProductItem) => {
      if (!selectionState) {
        return;
      }

      updateSelectionState(null);
      const selectionStoreCode = normalizeStoreCode(selectionState.storeCode);
      if (selectionStoreCode !== storeCodeRef.current) {
        logScanPerformance("scan.selection.ignored.stale-store", {
          scanTraceId: selectionState.scanTraceId,
          barcode: selectionState.barcode,
          source: selectionState.source,
          storeCode: selectionStoreCode,
          currentStoreCode: storeCodeRef.current,
          productCode: product.productCode,
        });
        drainScanQueueRef.current();
        return;
      }

      if (mode === "lookup") {
        const selectionGeneration = scanGenerationRef.current;
        isHandlingBarcodeRef.current = true;
        updateLookupPendingState(true);
        try {
          const selectStartedAt = getScanPerformanceTimestamp();
          logScanPerformance("scan.selection.frontend.start", {
            scanTraceId: selectionState.scanTraceId,
            barcode: selectionState.barcode,
            source: selectionState.source,
            storeCode: selectionStoreCode,
            productCode: product.productCode,
          });
          await onProductFound?.(
            product,
            selectionState.barcode,
            selectionState.source,
            selectionState.scanTraceId,
            selectionStoreCode
          );
          logScanPerformance("scan.selection.frontend.done", {
            scanTraceId: selectionState.scanTraceId,
            barcode: selectionState.barcode,
            source: selectionState.source,
            storeCode: selectionStoreCode,
            productCode: product.productCode,
            elapsedMs: getScanPerformanceTimestamp() - selectStartedAt,
          });
          updateFeedback({
            status: "found",
            message: i18n.t("common:scanner.selected"),
            barcode: selectionState.barcode,
            productName: product.productName,
          });
        } finally {
          if (selectionGeneration === scanGenerationRef.current) {
            isHandlingBarcodeRef.current = false;
            updateLookupPendingState(false);
            drainScanQueueRef.current();
          } else {
            logScanPerformance("scan.selection.finally.ignored.stale-generation", {
              scanTraceId: selectionState.scanTraceId,
              barcode: selectionState.barcode,
              source: selectionState.source,
              storeCode: selectionStoreCode,
              selectionGeneration,
              currentGeneration: scanGenerationRef.current,
            });
          }
        }
        return;
      }

      const selectionGeneration = scanGenerationRef.current;
      isHandlingBarcodeRef.current = true;
      updateLookupPendingState(true);
      latestAddScanTraceIdRef.current = selectionState.scanTraceId ?? null;
      try {
        await addMatchedProduct(
          product,
          selectionState.barcode,
          selectionState.source,
          selectionState.scanTraceId,
          selectionStoreCode,
          selectionGeneration
        );
      } finally {
        if (selectionGeneration === scanGenerationRef.current) {
          isHandlingBarcodeRef.current = false;
          updateLookupPendingState(false);
          drainScanQueueRef.current();
        } else {
          logScanPerformance("scan.selection.finally.ignored.stale-generation", {
            scanTraceId: selectionState.scanTraceId,
            barcode: selectionState.barcode,
            source: selectionState.source,
            storeCode: selectionStoreCode,
            selectionGeneration,
            currentGeneration: scanGenerationRef.current,
          });
        }
      }
    },
    [addMatchedProduct, mode, onProductFound, selectionState, updateFeedback, updateLookupPendingState, updateSelectionState]
  );

  const actions = useMemo(
    () => ({
      clearSelection() {
        updateSelectionState(null);
        drainScanQueueRef.current();
      },
      resetFeedback() {
        setFeedback({
          status: "ready",
          message: i18n.t("common:scanner.waiting"),
        });
      },
    }),
    [updateSelectionState]
  );

  return {
    feedback,
    selectionState,
    isBusy: isLookupPending || cartSyncPendingCount > 0,
    handleBarcode,
    confirmSelection,
    ...actions,
  };
}
