import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AppState } from "react-native";
import { usePathname, useRouter } from "expo-router";
import { LabelPrinterSetupSheet } from "@/components/printer/LabelPrinterSetupSheet";
import { useAppNavigationAccess } from "@/modules/navigation/access-context";
import { usePrinterStore } from "@/modules/printer/state";
import {
  buildPriceUpdatesRoute,
  isPriceUpdatesPath,
  type PriceUpdatesRouteTarget,
} from "@/modules/price-updates/navigation";
import { isPriceLabelPrintBusy } from "@/modules/price-updates/price-label-print-lock";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { useDeviceStore } from "@/store/device-store";
import { PriceReminderDialog, type PriceReminderPrinterState } from "./PriceReminderDialog";
import {
  PRICE_REMINDER_TICK_MS,
  buildPriceReminderStorageKey,
  parseLastShownAt,
  shouldShowPriceReminder,
} from "./schedule";
import { buildPriceReminderViewModel } from "./view-model";

interface PriceReminderHostProps {
  /** iOS 审核模式等不应出现提醒的场合。 */
  suppressed: boolean;
  /** 管理员账号：完全不弹提醒（仍可从角标和工作台进入「价格和标签」）。 */
  isAdmin: boolean;
  isDeviceMode: boolean;
}

/**
 * 「价格和标签」每小时提醒的宿主，挂在 Shell 布局里，任何页面都能弹。
 *
 * 什么时候弹：前台期间每分钟检查一次，距上次弹出满 1 小时、且名下分店还有未完成任务才弹；
 * App 回到前台时先刷新汇总再检查。点「稍后」或点遮罩只是关掉，1 小时后再提醒；
 * 全部处理完（待办为 0）就不再弹。正在打印、停在「价格和标签」页时不弹；管理员账号完全不弹。
 * 「上次弹出时间」存在本机：店长账号按账号，PDA 按设备 + 分店。
 */
export function PriceReminderHost({ suppressed, isAdmin, isDeviceMode }: PriceReminderHostProps) {
  const router = useRouter();
  const pathname = usePathname();
  const { priceTasks: overview, refreshPriceTasks } = useAppNavigationAccess();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const deviceSession = useDeviceStore((state) => state.session);
  const currentStoreCode = useCartStore((state) => state.selectedStore?.storeCode ?? null);
  const printerStatus = usePrinterStore((state) => state.status);
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const printerPaused = usePrinterStore((state) => state.autoReconnectPaused);

  const [open, setOpen] = useState(false);
  const [printerSheetVisible, setPrinterSheetVisible] = useState(false);

  const storageKey = useMemo(() => {
    if (isDeviceMode) {
      return deviceSession?.hardwareId && deviceSession.storeCode
        ? buildPriceReminderStorageKey({
            kind: "device",
            hardwareId: deviceSession.hardwareId,
            storeCode: deviceSession.storeCode,
          })
        : null;
    }
    return userGuid ? buildPriceReminderStorageKey({ kind: "account", userGuid }) : null;
  }, [deviceSession?.hardwareId, deviceSession?.storeCode, isDeviceMode, userGuid]);

  // 定时器和 AppState 回调里要读「最新」的值，不能被闭包锁住旧值。
  const latest = useRef({ overview, pathname, suppressed, isAdmin, storageKey, open });
  latest.current = { overview, pathname, suppressed, isAdmin, storageKey, open };
  // undefined = 还没从本机读过；null = 读过且从没弹过。
  const lastShownRef = useRef<number | null | undefined>(undefined);
  const lastShownKeyRef = useRef<string | null>(null);
  const evaluatingRef = useRef(false);

  const evaluate = useCallback(async () => {
    // 评估里有读存储的 await：用标志位串行化，避免定时器和回前台同时触发时重复弹出。
    if (evaluatingRef.current) return;
    evaluatingRef.current = true;
    try {
      const {
        overview: current,
        pathname: path,
        suppressed: isSuppressed,
        isAdmin: isAdminAccount,
        storageKey: key,
        open: isOpen,
      } = latest.current;
      if (!key || isOpen) return;

      if (lastShownKeyRef.current !== key) {
        lastShownKeyRef.current = key;
        lastShownRef.current = undefined;
      }
      if (lastShownRef.current === undefined) {
        try {
          lastShownRef.current = parseLastShownAt(await AppAsyncStorage.getString(key));
        } catch {
          // 读不到就当作没弹过：宁可多提醒一次，也不能因为存储故障永远不提醒。
          lastShownRef.current = null;
        }
      }

      // 判定必须和弹窗内容同源：都用「最近一次渲染出来的概览」。不能用刚 refetch 回来的新数据去判定，
      // 因为弹窗是按上下文里的概览渲染的，两者不同步时会出现「判定要弹、内容却为空 → 弹窗被立刻关掉、
      // 还记成已弹」，白白吞掉这一小时的提醒。
      const nowMs = Date.now();
      const shouldShow = shouldShowPriceReminder({
        nowMs,
        lastShownAtMs: lastShownRef.current ?? null,
        overviewReady: current.ready,
        totalPending: current.totalPending,
        appActive: AppState.currentState === "active",
        onPriceUpdatesScreen: isPriceUpdatesPath(path),
        suppressed: isSuppressed,
        isAdmin: isAdminAccount,
        busy: isPriceLabelPrintBusy(),
      });
      if (!shouldShow) return;

      lastShownRef.current = nowMs;
      void AppAsyncStorage.setString(key, String(nowMs)).catch(() => undefined);
      setOpen(true);
    } finally {
      evaluatingRef.current = false;
    }
  }, []);

  // 汇总刚就绪 / 待办变化 / 离开价格和标签页时检查一次。
  useEffect(() => {
    void evaluate();
  }, [evaluate, overview.ready, overview.totalPending, pathname, storageKey, suppressed, isAdmin]);

  // 前台期间每分钟检查一次是否到点。
  useEffect(() => {
    const timer = setInterval(() => void evaluate(), PRICE_REMINDER_TICK_MS);
    return () => clearInterval(timer);
  }, [evaluate]);

  // 回到前台：刷新汇总（别人可能刚处理完或新增了任务）。检查本身不在这里做：
  // 数据变化会让概览更新，上面的 effect 随之检查；数据没变则由每分钟的定时检查兜底（最多晚一分钟）。
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next !== "active") return;
      void refreshPriceTasks().catch(() => undefined);
      void evaluate();
    });
    return () => subscription.remove();
  }, [evaluate, refreshPriceTasks]);

  const viewModel = useMemo(
    () => buildPriceReminderViewModel({ overview, currentStoreCode, isDeviceMode }),
    [currentStoreCode, isDeviceMode, overview]
  );

  // 弹窗开着时任务被处理完（待办归零）：自动关掉，不留一个空提醒。
  useEffect(() => {
    if (open && !viewModel) {
      setOpen(false);
    }
  }, [open, viewModel]);

  const printerState: PriceReminderPrinterState =
    !isDeviceMode || viewModel?.variant !== "single"
      ? "hidden"
      : savedPrinter?.address && printerStatus === "connected" && !printerPaused
        ? "ready"
        : "offline";

  const goToPriceUpdates = useCallback(
    (target: PriceUpdatesRouteTarget) => {
      setOpen(false);
      router.push(buildPriceUpdatesRoute(target) as Parameters<typeof router.push>[0]);
    },
    [router]
  );

  if (!viewModel) {
    return null;
  }

  return (
    <>
      {/* 「去连接」打开标签机设置时先收起提醒，设置关闭后再回到提醒（open 仍为 true）。 */}
      <PriceReminderDialog
        visible={open && !printerSheetVisible}
        viewModel={viewModel}
        printerState={printerState}
        onLater={() => setOpen(false)}
        onPrimary={() =>
          goToPriceUpdates({
            storeCode: viewModel.primaryAction.storeCode,
            overdueOnly: viewModel.primaryAction.overdueOnly,
          })
        }
        onSelectStore={(storeCode) => goToPriceUpdates({ storeCode })}
        onConnectPrinter={() => setPrinterSheetVisible(true)}
      />
      <LabelPrinterSetupSheet visible={printerSheetVisible} onDismiss={() => setPrinterSheetVisible(false)} />
    </>
  );
}
