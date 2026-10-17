import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { useFocusEffect, useLocalSearchParams, useRouter } from "expo-router";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  ActivityIndicator,
  Button,
  Checkbox,
  Chip,
  Dialog,
  IconButton,
  Menu,
  Portal,
  SegmentedButtons,
  Snackbar,
  Surface,
  Text,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { LabelPrinterSetupSheet } from "@/components/printer/LabelPrinterSetupSheet";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { StoreChip } from "@/components/ui/StoreChip";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useAppNavigationAccess } from "@/modules/navigation/access-context";
import {
  getSavedPrinter,
  printDiscountLabelPayload,
  printProductLabelPayload,
} from "@/modules/printer/api";
import { usePrinterStore, type PrinterConnectionState } from "@/modules/printer/state";
import { getProductFastDetail, retryProductHqSyncOperation } from "@/modules/product-maintenance/api";
import type { ProductDetail } from "@/modules/product-maintenance/types";
import { getAssignedStoresForSession } from "@/modules/shop/store-scope";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import {
  applyPriceUpdateTasks,
  cancelPriceUpdateTasksForProduct,
  completePriceUpdateLabels,
  getPendingStoreCountForProduct,
  getPriceUpdateTasks,
  keepStorePriceForTasks,
} from "./api";
import {
  retryPriceUpdatePrintItem,
  runPriceUpdateBatch,
  type PriceUpdateRunDependencies,
  type PriceUpdateRunMode,
  type PriceUpdateRunState,
} from "./batch-runner";
import { buildPriceUpdateLabelJob } from "./label-print";
import { CancelTasksDialog } from "./cancel-tasks-dialog";
import { canCancelPriceTasks, distinctProductCodes, normalizeCancelNote, runCancelByProduct } from "./cancel";
import { PriceLabelPrintBusyError, runPriceLabelPrintExclusive } from "./price-label-print-lock";
import { groupCompletedTasksByDay, mergeUniqueTasks, summarizeSelection } from "./presentation";
import { parsePriceUpdatesParams } from "./navigation";
import { isStoreOverdueFlagged } from "./overview";
import { PriceUpdateProgressSheet } from "./price-update-progress-sheet";
import { loadAllTasks, resolveSelectAllState, resolveSelectedTasks } from "./selection";
import { PriceUpdateStoreStrip, type PriceUpdateStoreStripItem } from "./store-strip";
import { CompletedTaskCard, PendingTaskCard } from "./price-update-task-cards";
import { priceUpdateCountQueryKey, priceUpdateTasksQueryKey } from "./query-keys";
import type {
  PriceUpdateBatchResult,
  PriceUpdateTaskKind,
  StorePriceUpdateTask,
  StorePriceUpdateTaskPage,
} from "./types";

type TabValue = "pending" | "completed";
/** overdue = 只看已超期的未完成任务（后端按首次发起时间从早到晚排序）。 */
type KindFilter = "all" | PriceUpdateTaskKind | "overdue";
type ListRow =
  | { type: "header"; key: string; label: string }
  | { type: "task"; key: string; task: StorePriceUpdateTask };

const PAGE_SIZE = 30;

function resolvePrinterChip(status: PrinterConnectionState, hasPrinter: boolean) {
  if (!hasPrinter) return { key: "printer.notConfigured", icon: "printer-off-outline", color: HB_COLORS.textSecondary };
  if (status === "connected") return { key: "printer.connected", icon: "printer-check", color: HB_COLORS.success };
  if (status === "connecting" || status === "reconnecting") {
    return { key: "printer.connecting", icon: "printer-outline", color: HB_COLORS.action };
  }
  if (status === "paused") return { key: "printer.paused", icon: "printer-off-outline", color: HB_COLORS.warning };
  return { key: "printer.disconnected", icon: "printer-alert", color: HB_COLORS.warning };
}

export function PriceUpdatesScreen() {
  const router = useRouter();
  const routeParams = useLocalSearchParams();
  const queryClient = useQueryClient();
  const { priceTasks, orderedVisibleRouteNames } = useAppNavigationAccess();
  const { t, language } = useAppTranslation(["priceUpdates", "common"]);
  const access = useAuthStore((state) => state.access);
  const {
    stores,
    selectedStore,
    selectedStoreCode,
    selectStore,
    isDeviceMode,
    deviceBoundStore,
    isHydratingSelection,
    isLoading: storesLoading,
  } = useStores();
  const printerStatus = usePrinterStore((state) => state.status);
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const printerPaused = usePrinterStore((state) => state.autoReconnectPaused);

  const [tab, setTab] = useState<TabValue>("pending");
  const [kindFilter, setKindFilter] = useState<KindFilter>("all");
  const [hqSyncFailedOnly, setHqSyncFailedOnly] = useState(false);
  const [smallLabel, setSmallLabel] = useState(false);
  const [selecting, setSelecting] = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
  // 「全选」时从后端拉回的当前筛选下全部任务：列表页只加载了前几十条，批量处理靠它还原没滚到的任务。
  const [selectionPool, setSelectionPool] = useState<ReadonlyMap<number, StorePriceUpdateTask>>(() => new Map());
  const [selectAllBusy, setSelectAllBusy] = useState(false);
  const selectAllTokenRef = useRef(0);
  const [batchMenuVisible, setBatchMenuVisible] = useState(false);
  // 本页期间报表从「锁定」变为「已解锁」：留一条绿色提示 + 去看报表入口。
  const [justUnlocked, setJustUnlocked] = useState(false);
  const wasLockedRef = useRef(false);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [snackbar, setSnackbar] = useState("");
  const [busy, setBusy] = useState(false);
  // 保持本店价的确认对象：单条卡片与多选批量共用同一个确认弹窗。
  const [keepTargets, setKeepTargets] = useState<StorePriceUpdateTask[]>([]);
  const [cancelTargets, setCancelTargets] = useState<StorePriceUpdateTask[]>([]);
  // 单个商品撤销时，确认框里「共 N 家分店」的数量；取不到为 null。
  const [cancelStoreCount, setCancelStoreCount] = useState<number | null>(null);
  const [printerSheetVisible, setPrinterSheetVisible] = useState(false);
  const [runState, setRunState] = useState<PriceUpdateRunState | null>(null);
  const [retrying, setRetrying] = useState(false);
  const [stopRequested, setStopRequested] = useState(false);
  const stopRequestedRef = useRef(false);
  const busyRef = useRef(false);
  const [now, setNow] = useState(() => new Date());

  const storeCode = selectedStoreCode;
  const sessionStores = useMemo(
    () => getAssignedStoresForSession({ stores, isDeviceMode, deviceBoundStore }),
    [deviceBoundStore, isDeviceMode, stores]
  );
  // 非管理分店也可处理；账号仍需专用权限，且只能选择当前会话提供的门店。
  // 设备会话没有账号权限码，仅能处理绑定分店；最终范围由后端校验。
  const canOperate =
    sessionStores.some((store) => store.storeCode === storeCode) &&
    (isDeviceMode || access.hasPermission(PERMISSIONS.StoreProducts.PriceUpdates));
  // 撤销未完成任务：账号会话 + Warehouse.CancelPriceUpdateTasks（旧后端阶段认 Warehouse.ManageProducts），与后端 cancel 接口一致。
  // 撤销按商品覆盖所有分店、不按分店裁剪，所以不要求该分店在自己的可操作范围内，和 Web 一致。
  const canCancel =
    Boolean(storeCode) &&
    canCancelPriceTasks({
      isDeviceMode,
      hasCancelPermission: access.hasPermission(PERMISSIONS.Warehouse.CancelPriceUpdateTasks),
      hasManageProducts: access.hasPermission(PERMISSIONS.Warehouse.ManageProducts),
    });
  const canSwitchStore = !isDeviceMode && stores.length > 1;

  const clearSelection = useCallback(() => {
    // 让进行中的「全选」加载作废：期间切了筛选 / 分店 / 退出批量，拉回来的结果不能再套到新的列表上。
    selectAllTokenRef.current += 1;
    setSelectAllBusy(false);
    setSelectionPool(new Map());
    setBatchMenuVisible(false);
    setSelecting(false);
    setSelectedIds(new Set());
  }, []);

  // 已选任务属于某一家分店：切换分店（含其它页面改写全局分店）后必须清空，避免把 A 店任务带到 B 店提交。
  useEffect(() => {
    clearSelection();
  }, [clearSelection, storeCode]);

  // 从工作台红条、被锁的报表入口、每小时提醒进来时会带参数：选中「超期」筛选 / 指定分店。
  // 参数只在这里解释一次（见 navigation.ts），每个值只应用一次，之后用户自己改筛选不会被覆盖。
  const routeTarget = parsePriceUpdatesParams(routeParams as Record<string, string | string[] | undefined>);
  const appliedOverdueParamRef = useRef(false);
  useEffect(() => {
    if (!routeTarget.overdueOnly || appliedOverdueParamRef.current) return;
    appliedOverdueParamRef.current = true;
    setTab("pending");
    setHqSyncFailedOnly(false);
    setKindFilter("overdue");
  }, [routeTarget.overdueOnly]);
  const appliedStoreParamRef = useRef<string | null>(null);
  useEffect(() => {
    const target = routeTarget.storeCode;
    if (!target || isDeviceMode || appliedStoreParamRef.current === target) return;
    const store = sessionStores.find((item) => item.storeCode === target);
    // 分店列表还没加载好就等 sessionStores 变化后再试；不在可见范围内的分店直接忽略。
    if (!store) return;
    appliedStoreParamRef.current = target;
    if (store.storeCode !== storeCode) {
      void selectStore(store).catch(() => undefined);
    }
  }, [isDeviceMode, routeTarget.storeCode, selectStore, sessionStores, storeCode]);

  // 报表锁定状态：被锁过、现在解锁了 → 本页显示绿色「报表已解锁」。
  useEffect(() => {
    if (priceTasks.reportsLocked) {
      wasLockedRef.current = true;
      setJustUnlocked(false);
    } else if (wasLockedRef.current && priceTasks.ready) {
      wasLockedRef.current = false;
      setJustUnlocked(true);
    }
  }, [priceTasks.ready, priceTasks.reportsLocked]);

  // 列表滚动分页与「全选」逐页拉全共用同一组筛选条件，保证全选选中的就是当前筛选下的任务。
  const fetchTaskPage = useCallback(
    (page: number, pageSize: number) =>
      getPriceUpdateTasks({
        storeCode: storeCode!,
        status: tab === "pending" ? "Pending" : "Completed",
        kind: tab === "pending" && kindFilter !== "all" && kindFilter !== "overdue" ? kindFilter : undefined,
        overdueOnly: tab === "pending" && kindFilter === "overdue",
        hqSyncFailedOnly: tab === "completed" && hqSyncFailedOnly,
        page,
        pageSize,
      }),
    [hqSyncFailedOnly, kindFilter, storeCode, tab]
  );

  const tasksQuery = useInfiniteQuery({
    queryKey: priceUpdateTasksQueryKey(storeCode, tab, kindFilter, hqSyncFailedOnly),
    enabled: Boolean(storeCode),
    initialPageParam: 1,
    queryFn: ({ pageParam }) => fetchTaskPage(pageParam, PAGE_SIZE),
    getNextPageParam: (lastPage) =>
      lastPage.page * lastPage.pageSize < lastPage.total ? lastPage.page + 1 : undefined,
  });

  useFocusEffect(
    useCallback(() => {
      setNow(new Date());
      // offset 分页重新聚焦时丢弃旧后续页，从首页重新建立稳定分页窗口。
      void queryClient.resetQueries({ queryKey: priceUpdateTasksQueryKey() });
      void queryClient.invalidateQueries({ queryKey: priceUpdateCountQueryKey() });
    }, [queryClient])
  );

  const refreshAll = useCallback(async () => {
    setNow(new Date());
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: priceUpdateTasksQueryKey() }),
      queryClient.invalidateQueries({ queryKey: priceUpdateCountQueryKey() }),
    ]);
  }, [queryClient]);

  const firstPage = tasksQuery.data?.pages[0];
  // 计数随每个列表响应返回；切换标签/筛选时新查询尚无数据，沿用上一份计数避免分段按钮上的数字闪成 0。
  const [summary, setSummary] = useState<StorePriceUpdateTaskPage | null>(null);
  useEffect(() => {
    setSummary(null);
  }, [storeCode]);
  useEffect(() => {
    if (firstPage) setSummary(firstPage);
  }, [firstPage]);
  const hqSyncEnabled = summary?.hqSyncEnabled ?? false;
  const tasks = useMemo(() => mergeUniqueTasks(tasksQuery.data?.pages ?? []), [tasksQuery.data?.pages]);
  const rows = useMemo<ListRow[]>(() => {
    if (tab === "pending") {
      return tasks.map((task) => ({ type: "task", key: `task-${task.id}`, task }));
    }
    return groupCompletedTasksByDay(tasks, now).flatMap((group) => [
      {
        type: "header" as const,
        key: `header-${group.dayKey}`,
        label: group.kind === "date" ? group.dateLabel : t(`time.${group.kind}Label`),
      },
      ...group.items.map((task) => ({ type: "task" as const, key: `task-${task.id}`, task })),
    ]);
  }, [now, t, tab, tasks]);
  const selectedTasks = useMemo(
    () => resolveSelectedTasks(selectedIds, selectionPool, tasks),
    [selectedIds, selectionPool, tasks]
  );
  const selectionSummary = useMemo(
    () => summarizeSelection(selectedTasks, selectedIds),
    [selectedIds, selectedTasks]
  );

  const getErrorMessage = useCallback(
    (error: unknown, fallbackKey: string) =>
      resolveLocalizedErrorMessage(error, { t, language, fallbackKey }),
    [language, t]
  );

  const runExclusiveAction = useCallback(async (operation: () => Promise<void>) => {
    // 同步闸门：连续点击在 setBusy 生效前就会重入，必须用 ref 拦截。
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    try {
      await operation();
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }, []);

  const printTaskLabel = useCallback(
    async (task: StorePriceUpdateTask) => {
      let detail: ProductDetail | null = null;
      try {
        detail = await getProductFastDetail(task.productCode, task.storeCode);
      } catch {
        // 详情只用于补等级、供应商等字段；取不到不阻塞出标签，价格与条码以任务为准。
        detail = null;
      }
      const job = buildPriceUpdateLabelJob(task, detail);
      const printType = smallLabel ? "small" : null;
      if (job.kind === "discount") {
        await printDiscountLabelPayload(job.payload, printType);
      } else {
        await printProductLabelPayload(job.payload, printType);
      }
    },
    [smallLabel]
  );

  const assertBatchSucceeded = useCallback(
    (result: PriceUpdateBatchResult, taskId: number) => {
      const outcome = result.items.find((item) => item.taskId === taskId);
      if (!outcome?.success) {
        throw new Error(outcome?.message || t("messages.operationFailed"));
      }
    },
    [t]
  );

  /** 批量操作的结果提示：全部成功给成功文案，部分失败给成功/失败数。 */
  const describeBatchResult = useCallback(
    (result: PriceUpdateBatchResult, successKey: string) => {
      const success = result.items.filter((item) => item.success).length;
      const failed = result.items.length - success;
      return failed === 0
        ? t(successKey, { count: success })
        : t("messages.batchPartial", { success, failed });
    },
    [t]
  );

  const buildRunDependencies = useCallback(
    (targetStoreCode: string): PriceUpdateRunDependencies => ({
      apply: (items) => applyPriceUpdateTasks(targetStoreCode, items),
      printLabel: printTaskLabel,
      markPrinted: async (taskId) => {
        assertBatchSucceeded(await completePriceUpdateLabels(targetStoreCode, [taskId], "Printed"), taskId);
      },
      runPrintExclusive: runPriceLabelPrintExclusive,
    }),
    [assertBatchSucceeded, printTaskLabel]
  );

  const startRun = useCallback(
    (targets: StorePriceUpdateTask[], mode: PriceUpdateRunMode, options?: { showSheet?: boolean }) =>
      runExclusiveAction(async () => {
        if (!storeCode || !canOperate || targets.length === 0) return;
        const runStoreCode = storeCode;
        let printerReady = false;
        if (mode === "applyAndPrint") {
          const printer = await getSavedPrinter();
          printerReady = Boolean(printer?.address) && !printerPaused;
          if (!printerReady) {
            if (!targets.some((task) => task.kind === "PriceUpdate")) {
              // 只有待换标签的任务：没有标签机就无事可做，直接提示。
              setSnackbar(t("messages.printerRequired"));
              return;
            }
            setSnackbar(t("messages.printerUnavailable"));
          }
        }
        const showSheet = options?.showSheet ?? true;
        stopRequestedRef.current = false;
        setStopRequested(false);
        const finalState = await runPriceUpdateBatch({
          tasks: targets,
          mode,
          printerReady,
          dependencies: buildRunDependencies(runStoreCode),
          onChange: (next) => {
            if (showSheet) setRunState(next);
          },
          shouldStop: () => stopRequestedRef.current,
        });
        clearSelection();
        await refreshAll();
        if (finalState.apply.targetChanged > 0) {
          setSnackbar(t("messages.targetChanged"));
        } else if (!showSheet) {
          setSnackbar(
            finalState.apply.error
              ? finalState.apply.error
              : finalState.apply.failed > 0
                ? t("messages.applyPartlyFailed", {
                    success: finalState.apply.success,
                    failed: finalState.apply.failed,
                  })
                : t("messages.applied", { count: finalState.apply.success })
          );
        }
      }),
    [buildRunDependencies, canOperate, clearSelection, printerPaused, refreshAll, runExclusiveAction, storeCode, t]
  );

  const handleRetryPrint = useCallback(
    async (taskId: number) => {
      if (!runState || !storeCode || retrying) return;
      setRetrying(true);
      try {
        await retryPriceUpdatePrintItem(runState, taskId, buildRunDependencies(storeCode), setRunState);
        await refreshAll();
      } finally {
        setRetrying(false);
      }
    },
    [buildRunDependencies, refreshAll, retrying, runState, storeCode]
  );

  const handleMarkReplaced = useCallback(
    (task: StorePriceUpdateTask) =>
      runExclusiveAction(async () => {
        if (!storeCode) return;
        try {
          assertBatchSucceeded(await completePriceUpdateLabels(storeCode, [task.id], "MarkedReplaced"), task.id);
          setSnackbar(t("messages.markedReplaced"));
        } catch (error) {
          setSnackbar(getErrorMessage(error, "messages.operationFailed"));
        }
        await refreshAll();
      }),
    [assertBatchSucceeded, getErrorMessage, refreshAll, runExclusiveAction, storeCode, t]
  );

  const handleConfirmKeep = useCallback(() => {
    const targets = keepTargets;
    setKeepTargets([]);
    if (targets.length === 0) return;
    void runExclusiveAction(async () => {
      if (!storeCode) return;
      try {
        const result = await keepStorePriceForTasks(storeCode, targets.map((task) => task.id));
        if (targets.length === 1) {
          assertBatchSucceeded(result, targets[0].id);
          setSnackbar(t("messages.keptStorePrice"));
        } else {
          setSnackbar(describeBatchResult(result, "messages.batchKeptStorePrice"));
          clearSelection();
        }
      } catch (error) {
        setSnackbar(getErrorMessage(error, "messages.operationFailed"));
      }
      await refreshAll();
    });
  }, [assertBatchSucceeded, clearSelection, describeBatchResult, getErrorMessage, keepTargets, refreshAll, runExclusiveAction, storeCode, t]);

  // 撤销未完成任务：按商品覆盖所有分店，只取消提醒、不改价格、不可恢复；
  // 已被处理 / 不存在的任务后端逐条跳过，按结果分类提示。多个商品分次串行提交（后端每次只收一个商品）。
  const handleConfirmCancel = useCallback(
    (note: string) => {
      const productCodes = distinctProductCodes(cancelTargets);
      if (productCodes.length === 0) return;
      void runExclusiveAction(async () => {
        try {
          const trimmedNote = normalizeCancelNote(note);
          const outcome = await runCancelByProduct({
            productCodes,
            cancelOne: (productCode) => cancelPriceUpdateTasksForProduct(productCode, trimmedNote),
          });
          switch (outcome.kind) {
            case "success":
              setSnackbar(t("messages.cancelSuccess", { count: outcome.cancelled }));
              break;
            case "partial":
              setSnackbar(t("messages.cancelPartial", { cancelled: outcome.cancelled, skipped: outcome.skipped }));
              break;
            case "interrupted":
              setSnackbar(t("messages.cancelInterrupted", {
                cancelled: outcome.cancelled,
                notSubmitted: outcome.notSubmitted,
              }));
              break;
            default:
              setSnackbar(t("messages.cancelNothing"));
          }
          clearSelection();
        } catch (error) {
          setSnackbar(getErrorMessage(error, "messages.cancelFailed"));
        }
        setCancelTargets([]);
        await refreshAll();
      });
    },
    [cancelTargets, clearSelection, getErrorMessage, refreshAll, runExclusiveAction, t]
  );

  // 多选「标记已换」：只对待换标签任务生效（需改价任务必须先改价，后端也会拒绝）。
  const handleBatchMarkReplaced = useCallback(
    (targets: StorePriceUpdateTask[]) =>
      runExclusiveAction(async () => {
        if (!storeCode || targets.length === 0) return;
        try {
          const result = await completePriceUpdateLabels(storeCode, targets.map((task) => task.id), "MarkedReplaced");
          setSnackbar(describeBatchResult(result, "messages.batchMarkedReplaced"));
          clearSelection();
        } catch (error) {
          setSnackbar(getErrorMessage(error, "messages.operationFailed"));
        }
        await refreshAll();
      }),
    [clearSelection, describeBatchResult, getErrorMessage, refreshAll, runExclusiveAction, storeCode]
  );

  const handleReprint = useCallback(
    (task: StorePriceUpdateTask) =>
      runExclusiveAction(async () => {
        if (!storeCode) return;
        const printer = await getSavedPrinter();
        if (!printer?.address || printerPaused) {
          setSnackbar(t("messages.printerRequired"));
          return;
        }
        try {
          await runPriceLabelPrintExclusive(async () => {
            await printTaskLabel(task);
            // 对已完成任务回写 Printed = 再打一张，后端只累加打印次数。
            assertBatchSucceeded(await completePriceUpdateLabels(storeCode, [task.id], "Printed"), task.id);
          });
          setSnackbar(t("messages.reprinted"));
        } catch (error) {
          setSnackbar(
            error instanceof PriceLabelPrintBusyError
              ? t("messages.printBusy")
              : getErrorMessage(error, "messages.printFailed")
          );
        }
        await refreshAll();
      }),
    [assertBatchSucceeded, getErrorMessage, printTaskLabel, printerPaused, refreshAll, runExclusiveAction, storeCode, t]
  );

  const handleRetryHqSync = useCallback(
    (task: StorePriceUpdateTask) =>
      runExclusiveAction(async () => {
        if (!task.hqSyncOperationId) return;
        try {
          await retryProductHqSyncOperation(task.hqSyncOperationId);
          setSnackbar(t("messages.hqSyncRetried"));
        } catch (error) {
          setSnackbar(getErrorMessage(error, "messages.hqSyncRetryFailed"));
        }
        await refreshAll();
      }),
    [getErrorMessage, refreshAll, runExclusiveAction, t]
  );

  const handleViewProduct = useCallback(
    (task: StorePriceUpdateTask) => {
      router.push({
        pathname: "/(shell)/product-query",
        params: { productCode: task.productCode, storeCode: task.storeCode },
      });
    },
    [router]
  );

  const toggleSelected = useCallback((task: StorePriceUpdateTask) => {
    setSelectedIds((current) => {
      const next = new Set(current);
      if (next.has(task.id)) next.delete(task.id);
      else next.add(task.id);
      return next;
    });
  }, []);

  // 全选 = 当前筛选下的全部未完成任务，包括还没滚动加载出来的。
  const selectableTotal = firstPage?.total ?? tasks.length;
  const selectAllState = resolveSelectAllState(selectedIds.size, selectableTotal);
  const handleToggleSelectAll = useCallback(async () => {
    if (selectAllBusy || !storeCode) return;
    if (selectAllState === "all") {
      // 再点一次 = 取消全选
      setSelectedIds(new Set());
      return;
    }
    if (tasks.length >= selectableTotal) {
      setSelectedIds(new Set(tasks.map((task) => task.id)));
      return;
    }

    // 还有没加载的：逐页拉齐再全选。期间若切了筛选 / 分店 / 退出批量，token 变化，结果直接丢弃。
    const token = ++selectAllTokenRef.current;
    setSelectAllBusy(true);
    try {
      const all = await loadAllTasks(fetchTaskPage, {
        shouldCancel: () => selectAllTokenRef.current !== token,
      });
      if (selectAllTokenRef.current !== token) return;
      setSelectionPool(new Map(all.map((task) => [task.id, task])));
      setSelectedIds(new Set(all.map((task) => task.id)));
    } catch (error) {
      if (selectAllTokenRef.current === token) {
        setSnackbar(getErrorMessage(error, "selection.loadAllFailed"));
      }
    } finally {
      if (selectAllTokenRef.current === token) {
        setSelectAllBusy(false);
      }
    }
  }, [fetchTaskPage, getErrorMessage, selectAllBusy, selectAllState, selectableTotal, storeCode, tasks]);

  const handleSelectStore = useCallback(
    async (store: Store | null) => {
      setStorePickerVisible(false);
      if (!store || store.storeCode === storeCode) return;
      // 先清空再切店：selectStore 是异步持久化，期间不能让旧分店的已选任务仍可提交。
      clearSelection();
      try {
        await selectStore(store);
      } catch (error) {
        setSnackbar(getErrorMessage(error, "messages.storeSwitchFailed"));
      }
    },
    [clearSelection, getErrorMessage, selectStore, storeCode]
  );
  const handleStripSelect = useCallback(
    (targetStoreCode: string) => {
      void handleSelectStore(sessionStores.find((store) => store.storeCode === targetStoreCode) ?? null);
    },
    [handleSelectStore, sessionStores]
  );

  const handleApplyOnly = useCallback(
    (task: StorePriceUpdateTask) => void startRun([task], "applyOnly", { showSheet: false }),
    [startRun]
  );
  const handleApplyAndPrint = useCallback(
    (task: StorePriceUpdateTask) => void startRun([task], "applyAndPrint"),
    [startRun]
  );
  const handleKeepPress = useCallback((task: StorePriceUpdateTask) => setKeepTargets([task]), []);
  const handleCancelPress = useCallback((task: StorePriceUpdateTask) => {
    setCancelStoreCount(null);
    setCancelTargets([task]);
    // 先弹框再补数量：查询慢或失败都不挡操作，框里先显示不带数量的说明。
    void getPendingStoreCountForProduct(task.productCode).then((count) => {
      setCancelStoreCount((current) => (current === null ? count : current));
    });
  }, []);
  const handleCancelSelectedPress = useCallback((tasks: StorePriceUpdateTask[]) => {
    setCancelStoreCount(null);
    setCancelTargets(tasks);
  }, []);
  const handleMarkReplacedPress = useCallback(
    (task: StorePriceUpdateTask) => void handleMarkReplaced(task),
    [handleMarkReplaced]
  );
  const handleReprintPress = useCallback((task: StorePriceUpdateTask) => void handleReprint(task), [handleReprint]);
  const handleRetryHqSyncPress = useCallback(
    (task: StorePriceUpdateTask) => void handleRetryHqSync(task),
    [handleRetryHqSync]
  );

  const printerChip = resolvePrinterChip(printerStatus, Boolean(savedPrinter?.address));
  // 店长账号（名下多家分店）顶部用分店条切换；PDA 绑定单店、单店账号仍是带锁的分店标签。
  const showStoreStrip = !isDeviceMode && sessionStores.length > 1;
  const stripItems = useMemo<PriceUpdateStoreStripItem[]>(() => {
    const rowByStore = new Map(priceTasks.rows.map((row) => [row.storeCode, row]));
    // 顺序跟随分店列表而不是按待办排序：数字变化时按钮不会跳来跳去。
    return sessionStores.map((store) => {
      const row = rowByStore.get(store.storeCode);
      return {
        storeCode: store.storeCode,
        storeName: store.storeName,
        selected: store.storeCode === storeCode,
        pendingCount: row ? row.pendingCount : null,
        overdueFlag: row ? isStoreOverdueFlagged(row, priceTasks.manageableStoreCodes) : false,
      };
    });
  }, [priceTasks.manageableStoreCodes, priceTasks.rows, sessionStores, storeCode]);
  const showMultiSelect = (canOperate || canCancel) && tab === "pending" && tasks.length > 0;

  const renderRow = useCallback(
    ({ item }: { item: ListRow }) => {
      if (item.type === "header") {
        return <Text variant="labelLarge" style={styles.groupHeader}>{item.label}</Text>;
      }
      if (tab === "completed") {
        return (
          <CompletedTaskCard
            task={item.task}
            now={now}
            canOperate={canOperate}
            hqSyncEnabled={hqSyncEnabled}
            busy={busy}
            onReprint={handleReprintPress}
            onRetryHqSync={handleRetryHqSyncPress}
          />
        );
      }
      return (
        <PendingTaskCard
          task={item.task}
          now={now}
          canOperate={canOperate}
          canCancel={canCancel}
          selecting={selecting}
          selected={selectedIds.has(item.task.id)}
          busy={busy}
          onToggleSelected={toggleSelected}
          onApplyOnly={handleApplyOnly}
          onApplyAndPrint={handleApplyAndPrint}
          onKeepStorePrice={handleKeepPress}
          onViewProduct={handleViewProduct}
          onMarkReplaced={handleMarkReplacedPress}
          onPrintLabel={handleApplyAndPrint}
          onCancelTask={handleCancelPress}
        />
      );
    },
    [
      busy,
      canCancel,
      canOperate,
      handleApplyAndPrint,
      handleCancelPress,
      handleKeepPress,
      handleApplyOnly,
      handleMarkReplacedPress,
      handleReprintPress,
      handleRetryHqSyncPress,
      handleViewProduct,
      hqSyncEnabled,
      now,
      selectedIds,
      selecting,
      tab,
      toggleSelected,
    ]
  );

  const header = (
    <View style={styles.header}>
      <View style={BUSINESS_UI.headerRow}>
        <Text style={BUSINESS_UI.title}>{t("title")}</Text>
        {showStoreStrip && !selecting ? null : (
          <View style={styles.storeChip}>
            <StoreChip
              compact
              store={selectedStore}
              onPress={() => {
                if (canSwitchStore) setStorePickerVisible(true);
                else if (isDeviceMode) setSnackbar(t("store.lockedToDevice"));
              }}
            />
          </View>
        )}
      </View>

      {/* 批量处理期间分店条收起：批量只在当前这一家店里进行（标签贴在这家店的货架上，不跨店）。 */}
      {showStoreStrip && !selecting ? (
        <PriceUpdateStoreStrip items={stripItems} onSelect={handleStripSelect} />
      ) : null}

      {selecting ? (
        <View style={styles.selectAllRow}>
          <Checkbox.Android
            status={selectAllState === "all" ? "checked" : selectAllState === "some" ? "indeterminate" : "unchecked"}
            disabled={selectAllBusy || selectableTotal === 0}
            onPress={() => void handleToggleSelectAll()}
            color={HB_COLORS.action}
          />
          <Pressable
            accessibilityRole="button"
            disabled={selectAllBusy || selectableTotal === 0}
            onPress={() => void handleToggleSelectAll()}
            style={styles.selectAllCopy}
          >
            {selectAllBusy ? (
              <View style={styles.selectAllLoading}>
                <ActivityIndicator size={14} />
                <Text variant="bodyMedium" style={styles.secondary}>{t("selection.loadingAll")}</Text>
              </View>
            ) : (
              <>
                <Text variant="titleSmall" style={styles.selectAllLabel}>
                  {selectAllState === "all"
                    ? t("selection.allSelected", { count: selectableTotal })
                    : selectAllState === "some"
                      ? t("selection.partSelected", { selected: selectedIds.size, total: selectableTotal })
                      : t("selection.selectAll")}
                </Text>
                <Text variant="bodySmall" style={styles.secondary}>{t("selection.selectAllHint")}</Text>
              </>
            )}
          </Pressable>
          <Button compact mode="text" onPress={clearSelection}>
            {t("selection.exit")}
          </Button>
        </View>
      ) : (
        <View style={styles.headerSecondRow}>
          <Chip
            compact
            icon={printerChip.icon}
            textStyle={{ color: printerChip.color }}
            style={styles.statusChip}
            accessibilityHint={t("printer.openSettingsHint")}
            // 就地打开蓝牙标签打印机设置，关闭后仍停留在本页。
            onPress={() => setPrinterSheetVisible(true)}
          >
            {t(printerChip.key)}
          </Chip>
          <Chip
            compact
            selected={smallLabel}
            showSelectedOverlay
            onPress={() => setSmallLabel((current) => !current)}
            style={styles.statusChip}
          >
            {t("printer.smallLabel")}
          </Chip>
          <View style={styles.flex} />
          {showMultiSelect ? (
            <Button
              compact
              mode="text"
              icon="checkbox-multiple-marked-outline"
              disabled={busy}
              onPress={() => setSelecting(true)}
            >
              {t("selection.enter")}
            </Button>
          ) : null}
        </View>
      )}

      <SegmentedButtons
        value={tab}
        onValueChange={(value) => {
          setTab(value as TabValue);
          clearSelection();
        }}
        buttons={[
          { value: "pending", label: t("tabs.pending", { count: summary?.pendingCount ?? 0 }) },
          { value: "completed", label: t("tabs.completed", { count: summary?.completedCount ?? 0 }) },
        ]}
        theme={{ colors: { secondaryContainer: "#E8F1FF", onSecondaryContainer: HB_COLORS.action } }}
      />

      <View style={styles.filterRow}>
        {tab === "pending" ? (
          <>
            <Chip compact selected={kindFilter === "all"} showSelectedOverlay onPress={() => { setKindFilter("all"); clearSelection(); }}>
              {t("filters.all")}
            </Chip>
            <Chip compact selected={kindFilter === "PriceUpdate"} showSelectedOverlay onPress={() => { setKindFilter("PriceUpdate"); clearSelection(); }}>
              {t("filters.priceUpdate", { count: summary?.pendingPriceUpdateCount ?? 0 })}
            </Chip>
            <Chip compact selected={kindFilter === "LabelOnly"} showSelectedOverlay onPress={() => { setKindFilter("LabelOnly"); clearSelection(); }}>
              {t("filters.labelOnly", { count: summary?.pendingLabelOnlyCount ?? 0 })}
            </Chip>
            <Chip
              compact
              selected={kindFilter === "overdue"}
              showSelectedOverlay
              selectedColor={HB_COLORS.danger}
              textStyle={styles.overdueChipText}
              onPress={() => { setKindFilter("overdue"); clearSelection(); }}
            >
              {t("filters.overdue", { count: summary?.pendingOverdueCount ?? 0 })}
            </Chip>
          </>
        ) : (
          <>
            <Chip compact selected={!hqSyncFailedOnly} showSelectedOverlay onPress={() => setHqSyncFailedOnly(false)}>
              {t("filters.all")}
            </Chip>
            {hqSyncEnabled || hqSyncFailedOnly ? (
              <Chip compact selected={hqSyncFailedOnly} showSelectedOverlay onPress={() => setHqSyncFailedOnly(true)}>
                {t("filters.hqSyncFailed")}
              </Chip>
            ) : null}
          </>
        )}
      </View>

      {storeCode && !canOperate ? (
        <Text variant="bodySmall" style={styles.readOnlyHint}>{t("store.readOnly")}</Text>
      ) : null}
    </View>
  );

  let body;
  if (!storeCode) {
    body =
      storesLoading || isHydratingSelection ? (
        <View style={styles.centered}><ActivityIndicator size="large" /></View>
      ) : (
        <View style={styles.centered}>
          <EmptyState
            title={t("store.requiredTitle")}
            description={t("store.requiredDescription")}
            primaryAction={
              canSwitchStore || stores.length === 1
                ? { label: t("common:labels.selectStore"), icon: "storefront-outline", onPress: () => setStorePickerVisible(true) }
                : undefined
            }
          />
        </View>
      );
  } else if (tasksQuery.isLoading && !tasksQuery.data) {
    body = <View style={styles.centered}><ActivityIndicator size="large" accessibilityLabel={t("list.loading")} /></View>;
  } else if (tasksQuery.isError && !tasksQuery.data) {
    body = (
      <View style={styles.centered}>
        <EmptyState
          title={t("list.loadFailed")}
          description={getErrorMessage(tasksQuery.error, "list.retryHint")}
          primaryAction={{ label: t("common:actions.retry"), icon: "refresh", onPress: () => void tasksQuery.refetch() }}
        />
      </View>
    );
  } else {
    body = (
      <FlatList
        data={rows}
        keyExtractor={(item) => item.key}
        renderItem={renderRow}
        extraData={selectedIds}
        contentContainerStyle={rows.length ? styles.list : styles.emptyList}
        refreshControl={(
          <RefreshControl
            refreshing={tasksQuery.isRefetching && !tasksQuery.isFetchingNextPage}
            onRefresh={() => void refreshAll()}
          />
        )}
        onEndReachedThreshold={0.35}
        onEndReached={() => {
          if (tasksQuery.hasNextPage && !tasksQuery.isFetchingNextPage) {
            void tasksQuery.fetchNextPage();
          }
        }}
        ListFooterComponent={tasksQuery.isFetchingNextPage ? (
          <ActivityIndicator style={styles.pageLoader} accessibilityLabel={t("list.loadingMore")} />
        ) : tasksQuery.isFetchNextPageError ? (
          <View style={styles.pageLoader}>
            <Button icon="refresh" mode="text" onPress={() => void tasksQuery.fetchNextPage()}>
              {t("list.loadMoreFailed")}
            </Button>
          </View>
        ) : null}
        ListEmptyComponent={(
          <EmptyState
            title={
              tab === "pending" && kindFilter === "overdue"
                ? t("overdueEmpty.title")
                : t(tab === "pending" ? "list.emptyPending" : "list.emptyCompleted")
            }
            description={
              tab === "pending" && kindFilter === "overdue"
                ? t("overdueEmpty.description", { days: priceTasks.overdueDays })
                : t(tab === "pending" ? "list.emptyPendingDescription" : "list.emptyCompletedDescription")
            }
            primaryAction={{ label: t("common:actions.refresh"), icon: "refresh", onPress: () => void refreshAll() }}
          />
        )}
      />
    );
  }

  // 报表锁定提示：管理分店有超期任务时一直显示，告诉人原因和数量；处理完解锁后留一条绿色提示。
  const reportsAvailable = orderedVisibleRouteNames.includes("reports");
  const lockBanner = priceTasks.reportsLocked ? (
    <View style={[styles.banner, styles.bannerDanger]} accessibilityRole="alert">
      <MaterialCommunityIcons name="lock-alert-outline" size={22} color={HB_COLORS.danger} />
      <View style={styles.bannerBody}>
        <Text variant="titleSmall" style={styles.bannerDangerTitle}>{t("lock.title")}</Text>
        <Text variant="bodySmall" style={styles.bannerDangerText}>
          {t("lock.description", { count: priceTasks.manageableOverdueCount, days: priceTasks.overdueDays })}
        </Text>
      </View>
    </View>
  ) : justUnlocked ? (
    <View style={[styles.banner, styles.bannerOk]}>
      <MaterialCommunityIcons name="lock-open-variant-outline" size={22} color={HB_COLORS.success} />
      <View style={styles.bannerBody}>
        <Text variant="titleSmall" style={styles.bannerOkTitle}>{t("lock.unlockedTitle")}</Text>
        <Text variant="bodySmall" style={styles.bannerOkText}>{t("lock.unlockedDescription")}</Text>
      </View>
      {reportsAvailable ? (
        <Button
          compact
          mode="text"
          onPress={() => router.push("/(shell)/reports" as Parameters<typeof router.push>[0])}
        >
          {t("lock.openReports")}
        </Button>
      ) : null}
    </View>
  ) : null;

  return (
    <SafeAreaView style={styles.container} edges={["top", "left", "right"]}>
      {header}
      {lockBanner}
      <View style={styles.flex}>{body}</View>

      {selecting && (canOperate || canCancel) ? (
        <Surface style={styles.footer} elevation={3}>
          <View style={styles.footerSummaryRow}>
            <Text variant="titleSmall" style={styles.footerSummary}>
              {t("selection.selectedTitle", { count: selectedIds.size })}
            </Text>
            <Text variant="bodySmall" style={styles.secondary}>
              {t("selection.summaryByKind", {
                priceUpdate: selectionSummary.applyCount,
                labelOnly: selectionSummary.labelOnlyCount,
              })}
            </Text>
          </View>
          {canOperate && hqSyncEnabled && selectionSummary.applyCount > 0 ? (
            <Text variant="bodySmall" style={styles.secondary}>{t("selection.hqSyncHint")}</Text>
          ) : null}
          {/* 按选中任务的类型给按钮：需改价 → 仅更新；待换标签 → 标记已换；不常用的「保持本店价」收进「⋯」防误触。 */}
          {canOperate && (selectionSummary.applyCount > 0 || selectionSummary.labelOnlyCount > 0) ? (
            <View style={styles.footerSecondaryButtons}>
              {selectionSummary.applyCount > 0 ? (
                <Button
                  compact
                  mode="outlined"
                  disabled={busy}
                  onPress={() => void startRun(selectedTasks, "applyOnly")}
                >
                  {t("selection.applyOnly", { count: selectionSummary.applyCount })}
                </Button>
              ) : null}
              {selectionSummary.labelOnlyCount > 0 ? (
                <Button
                  compact
                  mode="outlined"
                  disabled={busy}
                  onPress={() => void handleBatchMarkReplaced(selectedTasks.filter((task) => task.kind === "LabelOnly"))}
                >
                  {t("selection.markReplaced", { count: selectionSummary.labelOnlyCount })}
                </Button>
              ) : null}
              {selectionSummary.applyCount > 0 ? (
                <Menu
                  visible={batchMenuVisible}
                  onDismiss={() => setBatchMenuVisible(false)}
                  anchor={(
                    <IconButton
                      icon="dots-horizontal"
                      mode="outlined"
                      size={18}
                      disabled={busy}
                      accessibilityLabel={t("selection.more")}
                      onPress={() => setBatchMenuVisible(true)}
                    />
                  )}
                >
                  <Menu.Item
                    leadingIcon="hand-back-right-outline"
                    title={t("selection.keepStorePrice", { count: selectionSummary.applyCount })}
                    onPress={() => {
                      setBatchMenuVisible(false);
                      setKeepTargets(selectedTasks.filter((task) => task.kind === "PriceUpdate"));
                    }}
                  />
                </Menu>
              ) : null}
            </View>
          ) : null}
          {canOperate ? (
            <Button
              mode="contained"
              icon="printer-outline"
              disabled={busy || selectionSummary.printCount === 0}
              onPress={() => void startRun(selectedTasks, "applyAndPrint")}
            >
              {selectionSummary.applyCount > 0
                ? t("selection.applyAndPrint", { count: selectionSummary.printCount })
                : t("selection.printLabels", { count: selectionSummary.printCount })}
            </Button>
          ) : null}
          {/* 撤销不可恢复：放在主操作下面、用文字按钮 + 警示色，避免和「更新并打印」误触；点了还有确认框。 */}
          {canCancel ? (
            <Button
              mode={canOperate ? "text" : "outlined"}
              icon="close-circle-outline"
              textColor={HB_COLORS.danger}
              disabled={busy || selectedIds.size === 0}
              onPress={() => handleCancelSelectedPress(selectedTasks)}
            >
              {t("selection.cancelSelected", { count: selectedIds.size })}
            </Button>
          ) : null}
        </Surface>
      ) : null}

      <LabelPrinterSetupSheet visible={printerSheetVisible} onDismiss={() => setPrinterSheetVisible(false)} />

      <PriceUpdateProgressSheet
        state={runState}
        retrying={retrying}
        stopRequested={stopRequested}
        onStop={() => {
          stopRequestedRef.current = true;
          setStopRequested(true);
        }}
        onRetry={(taskId) => void handleRetryPrint(taskId)}
        onDone={() => setRunState(null)}
      />

      <Portal>
        <Dialog visible={keepTargets.length > 0} onDismiss={() => setKeepTargets([])}>
          <Dialog.Title>{t("keepDialog.title")}</Dialog.Title>
          <Dialog.Content>
            <Text variant="bodyMedium">
              {keepTargets.length > 1
                ? t("keepDialog.batchDescription", { count: keepTargets.length })
                : t("keepDialog.description", {
                    name: keepTargets[0]?.productName || keepTargets[0]?.productCode || "",
                  })}
            </Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setKeepTargets([])}>{t("common:actions.cancel")}</Button>
            <Button onPress={handleConfirmKeep}>{t("actions.keepStorePrice")}</Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>

      <CancelTasksDialog
        targets={cancelTargets}
        storeCount={cancelStoreCount}
        busy={busy}
        onConfirm={handleConfirmCancel}
        onDismiss={() => setCancelTargets([])}
      />

      <StorePickerModal
        visible={storePickerVisible}
        presentation="sheet"
        stores={stores}
        selectedStoreCode={storeCode}
        title={t("common:labels.selectStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={handleSelectStore}
      />

      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3000}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: BUSINESS_UI.screen,
  flex: { flex: 1 },
  centered: { flex: 1, alignItems: "center", justifyContent: "center", padding: HB_SPACING.lg },
  header: { ...BUSINESS_UI.header, backgroundColor: HB_COLORS.white, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  storeChip: { flexShrink: 1, maxWidth: "55%" },
  headerSecondRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 40 },
  statusChip: { backgroundColor: HB_COLORS.surfaceMuted },
  filterRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  readOnlyHint: { color: HB_COLORS.warning },
  secondary: { color: HB_COLORS.textSecondary },
  list: { ...BUSINESS_UI.content, paddingBottom: HB_SPACING.xl },
  emptyList: { flexGrow: 1, justifyContent: "center", padding: HB_SPACING.md },
  groupHeader: { color: HB_COLORS.textSecondary, paddingTop: HB_SPACING.xs },
  pageLoader: { paddingVertical: HB_SPACING.md },
  footer: BUSINESS_UI.footer,
  footerSummary: { fontWeight: "700", color: HB_COLORS.textPrimary },
  footerSummaryRow: { flexDirection: "row", alignItems: "baseline", justifyContent: "space-between", gap: HB_SPACING.xs },
  selectAllRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xxs, minHeight: 48 },
  selectAllCopy: { flex: 1, minWidth: 0, justifyContent: "center" },
  selectAllLabel: { fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  selectAllLoading: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  overdueChipText: { color: HB_COLORS.danger },
  banner: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md, paddingVertical: 10 },
  bannerBody: { flex: 1, minWidth: 0 },
  bannerDanger: { backgroundColor: "#FEE4E2" },
  bannerDangerTitle: { color: HB_COLORS.danger, fontWeight: "700" },
  bannerDangerText: { color: "#7A271A" },
  bannerOk: { backgroundColor: "#DCFAE6" },
  bannerOkTitle: { color: HB_COLORS.success, fontWeight: "700" },
  bannerOkText: { color: HB_COLORS.success },
  footerSecondaryButtons: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
});
