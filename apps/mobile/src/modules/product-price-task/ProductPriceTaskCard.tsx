import { useCallback, useMemo, useRef, useState } from "react";
import { ActivityIndicator, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQueryClient } from "@tanstack/react-query";
import { Button, Dialog, Menu, Portal, Text } from "react-native-paper";
import {
  getSavedPrinter,
  printDiscountLabelPayload,
  printProductLabelPayload,
} from "@/modules/printer/api";
import { usePrinterStore } from "@/modules/printer/state";
import { getProductFastDetail } from "@/modules/product-maintenance/api";
import type { ProductDetail } from "@/modules/product-maintenance/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  applyPriceUpdateTasks,
  completePriceUpdateLabels,
  keepStorePriceForTasks,
} from "../price-updates/api";
import {
  runPriceUpdateBatch,
  type PriceUpdateRunDependencies,
  type PriceUpdateRunMode,
  type PriceUpdateRunState,
} from "../price-updates/batch-runner";
import { buildPriceUpdateLabelJob } from "../price-updates/label-print";
import { runPriceLabelPrintExclusive } from "../price-updates/price-label-print-lock";
import { formatDiscountLabel, formatMoney } from "../price-updates/presentation";
import { priceUpdateCountQueryKey, priceUpdateTasksQueryKey } from "../price-updates/query-keys";
import type { PriceUpdateBatchResult, StorePriceUpdateTask } from "../price-updates/types";
import { useProductPriceTask } from "./use-product-price-task";
import {
  buildProductTaskCardModel,
  resolveRunOutcome,
  resolveRunSteps,
  type RunStepStatus,
} from "./view-model";

export interface ProductPriceTaskCardProps {
  storeCode: string | null;
  productCode: string | null;
  /** 在线 + 有「价格和标签」权限 + 已选分店；不满足时整张卡不出现。 */
  enabled: boolean;
  /** 页面上还有未保存的价格修改：处理任务会刷新商品详情，会冲掉这些修改，所以先拦住。 */
  editBlocked: boolean;
  /** 改价成功后刷新商品详情，让下方价格卡显示新值。 */
  onCompleted: () => void | Promise<void>;
  onConnectPrinter: () => void;
  onViewTask: (task: StorePriceUpdateTask | null) => void;
}

type Notice = { tone: "info" | "warning" | "danger"; text: string };
type DoneInfo = { text: string; reprintTask: StorePriceUpdateTask | null };

function assertBatchSucceeded(result: PriceUpdateBatchResult, taskId: number, fallback: string) {
  const outcome = result.items.find((item) => item.taskId === taskId);
  if (!outcome?.success) {
    throw new Error(outcome?.message || fallback);
  }
}

async function printTaskLabel(task: StorePriceUpdateTask) {
  let detail: ProductDetail | null = null;
  try {
    detail = await getProductFastDetail(task.productCode, task.storeCode);
  } catch {
    // 详情只用于补等级、供应商等字段；取不到不阻塞出标签，价格与条码以任务为准。
    detail = null;
  }
  const job = buildPriceUpdateLabelJob(task, detail);
  if (job.kind === "discount") {
    await printDiscountLabelPayload(job.payload, null);
  } else {
    await printProductLabelPayload(job.payload, null);
  }
}

function StepIcon({ status }: { status: RunStepStatus }) {
  if (status === "active") {
    return <ActivityIndicator size={16} color={HB_COLORS.brand} />;
  }
  const config: Record<Exclude<RunStepStatus, "active">, { name: string; color: string }> = {
    waiting: { name: "circle-outline", color: HB_COLORS.outline },
    done: { name: "check-circle", color: HB_COLORS.success },
    failed: { name: "close-circle", color: HB_COLORS.danger },
    skipped: { name: "minus-circle-outline", color: HB_COLORS.outline },
  };
  const { name, color } = config[status];
  return <MaterialCommunityIcons name={name as never} size={18} color={color} />;
}

export function ProductPriceTaskCard({
  storeCode,
  productCode,
  enabled,
  editBlocked,
  onCompleted,
  onConnectPrinter,
  onViewTask,
}: ProductPriceTaskCardProps) {
  const { t, language } = useAppTranslation(["productQuery", "priceUpdates", "common"]);
  const queryClient = useQueryClient();
  const taskQuery = useProductPriceTask({ storeCode, productCode, enabled });
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const printerPaused = usePrinterStore((state) => state.autoReconnectPaused);

  const [busy, setBusy] = useState(false);
  // 同步闸门：连点在 setBusy 生效前就会重入，必须用 ref 拦截。
  const busyRef = useRef(false);
  // 运行期间卡片展示开始时的任务快照：完成后接口会让原任务从列表消失，不能让卡片中途闪没。
  const [lockedTask, setLockedTask] = useState<StorePriceUpdateTask | null>(null);
  const [runState, setRunState] = useState<PriceUpdateRunState | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [doneInfo, setDoneInfo] = useState<DoneInfo | null>(null);
  const [moreVisible, setMoreVisible] = useState(false);
  const [keepVisible, setKeepVisible] = useState(false);

  const queriedTask = taskQuery.data ?? null;
  const task = lockedTask ?? queriedTask;
  const printerReady = Boolean(savedPrinter?.address) && !printerPaused;

  // 与「价格和标签」页一致：用同一个标签机互斥锁，避免两处同时出纸。
  const dependencies = useMemo<PriceUpdateRunDependencies | null>(() => {
    if (!storeCode) return null;
    return {
      apply: (items) => applyPriceUpdateTasks(storeCode, items),
      printLabel: printTaskLabel,
      markPrinted: async (taskId) => {
        assertBatchSucceeded(
          await completePriceUpdateLabels(storeCode, [taskId], "Printed"),
          taskId,
          t("priceTask.notice.operationFailed")
        );
      },
      runPrintExclusive: runPriceLabelPrintExclusive,
    };
  }, [storeCode, t]);

  const getErrorMessage = useCallback(
    (error: unknown) =>
      resolveLocalizedErrorMessage(error, {
        t,
        language,
        fallbackKey: "priceTask.notice.operationFailed",
      }),
    [language, t]
  );

  const refreshTasks = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: priceUpdateTasksQueryKey() }),
      queryClient.invalidateQueries({ queryKey: priceUpdateCountQueryKey() }),
    ]);
  }, [queryClient]);

  const withBusy = useCallback(async (operation: () => Promise<void>, options?: { keepDone?: boolean }) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setNotice(null);
    // 「再打一张」期间原任务早已不在列表里，完成条必须留着，否则整张卡会闪没。
    if (!options?.keepDone) setDoneInfo(null);
    try {
      await operation();
    } finally {
      busyRef.current = false;
      setBusy(false);
      setLockedTask(null);
      setRunState(null);
    }
  }, []);

  const handleRun = useCallback(
    (mode: PriceUpdateRunMode) => {
      const target = task;
      if (!target || !dependencies) return;
      void withBusy(async () => {
        try {
          let ready = false;
          if (mode === "applyAndPrint") {
            const printer = await getSavedPrinter();
            ready = Boolean(printer?.address) && !printerPaused;
            if (!ready && target.kind === "LabelOnly") {
              // 只有标签没有价格可改：没有标签机就无事可做。
              setNotice({ tone: "warning", text: t("priceTask.notice.printerRequired") });
              return;
            }
          }
          setLockedTask(target);
          const final = await runPriceUpdateBatch({
            tasks: [target],
            mode,
            printerReady: ready,
            dependencies,
            onChange: setRunState,
          });
          const outcome = resolveRunOutcome(final);
          await refreshTasks();
          if (final.apply.success > 0) {
            await onCompleted();
          }
          switch (outcome) {
            case "completed": {
              const printed = final.prints[0];
              const finalTask = printed?.task ?? target;
              const price = buildProductTaskCardModel(finalTask).comparison.toPrice;
              setDoneInfo({
                text: t("priceTask.done.completed", { price: formatMoney(price) }),
                reprintTask: printed ? finalTask : null,
              });
              break;
            }
            case "priceUpdatedLabelPending":
              setNotice({ tone: "info", text: t("priceTask.notice.priceUpdatedLabelPending") });
              break;
            case "targetChanged":
              setNotice({ tone: "warning", text: t("priceTask.notice.targetChanged") });
              break;
            case "applyFailed":
              setNotice({
                tone: "danger",
                text: final.apply.error || t("priceTask.notice.applyFailed"),
              });
              break;
            case "printFailed": {
              const item = final.prints[0];
              setNotice({
                tone: "danger",
                text: item?.printedAwaitingConfirm
                  ? t("priceTask.notice.confirmFailed")
                  : item?.error || t("priceTask.notice.printFailed"),
              });
              break;
            }
            default:
              break;
          }
        } catch (error) {
          setNotice({ tone: "danger", text: getErrorMessage(error) });
          await refreshTasks();
        }
      });
    },
    [dependencies, getErrorMessage, onCompleted, printerPaused, refreshTasks, t, task, withBusy]
  );

  const handleMarkReplaced = useCallback(() => {
    const target = task;
    if (!target || !storeCode) return;
    void withBusy(async () => {
      try {
        assertBatchSucceeded(
          await completePriceUpdateLabels(storeCode, [target.id], "MarkedReplaced"),
          target.id,
          t("priceTask.notice.operationFailed")
        );
        await refreshTasks();
        setDoneInfo({ text: t("priceTask.done.markedReplaced"), reprintTask: null });
      } catch (error) {
        setNotice({ tone: "danger", text: getErrorMessage(error) });
        await refreshTasks();
      }
    });
  }, [getErrorMessage, refreshTasks, storeCode, t, task, withBusy]);

  const handleConfirmKeep = useCallback(() => {
    setKeepVisible(false);
    const target = task;
    if (!target || !storeCode) return;
    void withBusy(async () => {
      try {
        assertBatchSucceeded(
          await keepStorePriceForTasks(storeCode, [target.id]),
          target.id,
          t("priceTask.notice.operationFailed")
        );
        await refreshTasks();
        setDoneInfo({ text: t("priceTask.done.keptStorePrice"), reprintTask: null });
      } catch (error) {
        setNotice({ tone: "danger", text: getErrorMessage(error) });
        await refreshTasks();
      }
    });
  }, [getErrorMessage, refreshTasks, storeCode, t, task, withBusy]);

  const handleReprint = useCallback(() => {
    const target = doneInfo?.reprintTask;
    if (!target) return;
    void withBusy(
      async () => {
        try {
          await runPriceLabelPrintExclusive(() => printTaskLabel(target));
          setDoneInfo({ text: t("priceTask.done.reprinted"), reprintTask: target });
        } catch (error) {
          setNotice({ tone: "danger", text: getErrorMessage(error) });
        }
      },
      { keepDone: true }
    );
  }, [doneInfo, getErrorMessage, t, withBusy]);

  if (!enabled) return null;

  const tp = (key: string, params?: Record<string, unknown>) => t(key, { ...params, ns: "priceUpdates" });

  // 已完成：绿色确认条（原任务已不在待处理列表里）。
  if (!task && doneInfo) {
    return (
      <View style={[styles.card, styles.doneCard]} accessibilityLiveRegion="polite">
        <View style={styles.doneRow}>
          <MaterialCommunityIcons name="check-circle" size={20} color={HB_COLORS.success} />
          <Text variant="bodyMedium" style={styles.doneText}>
            {doneInfo.text}
          </Text>
        </View>
        {notice ? <NoticeLine notice={notice} /> : null}
        <View style={styles.actionRow}>
          {doneInfo.reprintTask && printerReady ? (
            <Button compact mode="outlined" disabled={busy} loading={busy} onPress={handleReprint}>
              {t("priceTask.actions.reprint")}
            </Button>
          ) : null}
          <Button compact mode="text" disabled={busy} onPress={() => onViewTask(null)}>
            {t("priceTask.actions.viewTask")}
          </Button>
        </View>
      </View>
    );
  }

  if (!task) return null;

  const model = buildProductTaskCardModel(task);
  const isPriceUpdate = model.variant === "priceUpdate";
  const comparison = model.comparison;
  const steps = resolveRunSteps(runState, isPriceUpdate);
  const running = busy && lockedTask !== null;
  const actionsDisabled = busy || editBlocked;

  return (
    <View
      style={[
        styles.card,
        model.overdue ? styles.cardOverdue : null,
        !isPriceUpdate ? styles.cardLabelOnly : null,
      ]}
    >
      <View style={styles.headerRow}>
        <MaterialCommunityIcons
          name={isPriceUpdate ? "tag-arrow-up-outline" : "label-outline"}
          size={20}
          color={isPriceUpdate ? HB_COLORS.action : HB_COLORS.warning}
        />
        <Text variant="titleSmall" style={styles.title} numberOfLines={2}>
          {isPriceUpdate ? t("priceTask.titlePriceUpdate") : t("priceTask.titleLabelOnly")}
        </Text>
      </View>
      <View style={styles.tagRow}>
        {model.overdue ? (
          <Text style={[styles.tag, styles.tagDanger]}>
            {t("priceTask.overdueTag", { days: model.overdueByDays })}
          </Text>
        ) : (
          <Text style={[styles.tag, styles.tagNeutral]}>
            {t("priceTask.waitingTag", { days: model.waitingDays })}
          </Text>
        )}
        {comparison.delta != null && comparison.delta !== 0 ? (
          <Text style={[styles.tag, styles.tagBrand]}>
            {comparison.delta > 0
              ? t("priceTask.deltaUp", { amount: formatMoney(Math.abs(comparison.delta)) })
              : t("priceTask.deltaDown", { amount: formatMoney(Math.abs(comparison.delta)) })}
          </Text>
        ) : null}
      </View>

      <View style={styles.compareRow}>
        <View style={styles.compareCell}>
          <Text variant="labelSmall" style={styles.compareLabel}>
            {isPriceUpdate ? t("priceTask.storeNow") : t("priceTask.shelfLabel")}
          </Text>
          <Text variant="titleMedium" style={styles.compareFrom}>
            {formatMoney(comparison.fromPrice)}
          </Text>
        </View>
        <MaterialCommunityIcons name="arrow-right" size={18} color={HB_COLORS.textSecondary} />
        <View style={styles.compareCell}>
          <Text variant="labelSmall" style={styles.compareLabel}>
            {isPriceUpdate ? t("priceTask.warehouseNew") : t("priceTask.storeNow")}
          </Text>
          <Text variant="titleMedium" style={styles.compareTo}>
            {formatMoney(comparison.toPrice)}
          </Text>
        </View>
      </View>
      {comparison.discountChanged ? (
        <Text variant="bodySmall" style={styles.discountRow}>
          {t("priceTask.discountRow", {
            from: formatDiscountLabel(comparison.fromDiscountRate, tp),
            to: formatDiscountLabel(comparison.toDiscountRate, tp),
          })}
        </Text>
      ) : null}

      {running ? (
        <View style={styles.steps} accessibilityLiveRegion="polite">
          {(
            [
              isPriceUpdate ? (["apply", steps.apply] as const) : null,
              ["print", steps.print] as const,
              ["complete", steps.complete] as const,
            ] as const
          )
            .filter((entry): entry is NonNullable<typeof entry> => entry !== null)
            .map(([key, status]) => (
              <View key={key} style={styles.stepRow}>
                <StepIcon status={status} />
                <Text
                  variant="bodyMedium"
                  style={status === "waiting" || status === "skipped" ? styles.stepMuted : styles.stepText}
                >
                  {t(`priceTask.steps.${key}`)}
                  {status === "skipped" ? `（${t("priceTask.steps.skipped")}）` : ""}
                </Text>
              </View>
            ))}
        </View>
      ) : null}

      {notice ? <NoticeLine notice={notice} /> : null}

      {!running ? (
        <>
          {editBlocked ? (
            <Text variant="bodySmall" style={styles.blockedHint}>
              {t("priceTask.dirtyBlocked")}
            </Text>
          ) : null}
          {!printerReady ? (
            <View style={styles.printerRow}>
              <MaterialCommunityIcons name="printer-off" size={16} color={HB_COLORS.warning} />
              <Text variant="bodySmall" style={styles.printerText}>
                {t("priceTask.printerOffline")}
              </Text>
              <Button compact mode="text" onPress={onConnectPrinter}>
                {t("priceTask.connectPrinter")}
              </Button>
            </View>
          ) : null}
          <View style={styles.actionRow}>
            {isPriceUpdate ? (
              <>
                <Button
                  mode="contained"
                  compact
                  disabled={actionsDisabled}
                  onPress={() => handleRun(printerReady ? "applyAndPrint" : "applyOnly")}
                >
                  {printerReady ? t("priceTask.actions.applyAndPrint") : t("priceTask.actions.applyOnly")}
                </Button>
                {printerReady ? (
                  <Button mode="outlined" compact disabled={actionsDisabled} onPress={() => handleRun("applyOnly")}>
                    {tp("actions.applyOnly")}
                  </Button>
                ) : null}
                <Menu
                  visible={moreVisible}
                  onDismiss={() => setMoreVisible(false)}
                  anchor={
                    <Button mode="text" compact disabled={busy} onPress={() => setMoreVisible(true)}>
                      {t("priceTask.actions.more")}
                    </Button>
                  }
                >
                  <Menu.Item
                    leadingIcon="tag-check-outline"
                    title={t("priceTask.actions.keepStorePrice")}
                    disabled={actionsDisabled}
                    onPress={() => {
                      setMoreVisible(false);
                      setKeepVisible(true);
                    }}
                  />
                  <Menu.Item
                    leadingIcon="open-in-new"
                    title={t("priceTask.actions.viewTask")}
                    onPress={() => {
                      setMoreVisible(false);
                      onViewTask(task);
                    }}
                  />
                </Menu>
              </>
            ) : (
              <>
                <Button
                  mode="contained"
                  compact
                  disabled={actionsDisabled || !printerReady}
                  onPress={() => handleRun("applyAndPrint")}
                >
                  {t("priceTask.actions.printLabel")}
                </Button>
                <Button mode="outlined" compact disabled={actionsDisabled} onPress={handleMarkReplaced}>
                  {t("priceTask.actions.markReplaced")}
                </Button>
                <Button mode="text" compact disabled={busy} onPress={() => onViewTask(task)}>
                  {t("priceTask.actions.viewTask")}
                </Button>
              </>
            )}
          </View>
        </>
      ) : null}

      <Portal>
        <Dialog visible={keepVisible} onDismiss={() => setKeepVisible(false)}>
          <Dialog.Title>{tp("keepDialog.title")}</Dialog.Title>
          <Dialog.Content>
            <Text variant="bodyMedium">
              {tp("keepDialog.description", { name: task.productName || task.productCode })}
            </Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setKeepVisible(false)}>{t("common:actions.cancel")}</Button>
            <Button onPress={handleConfirmKeep}>{tp("actions.keepStorePrice")}</Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
    </View>
  );
}

function NoticeLine({ notice }: { notice: Notice }) {
  const color =
    notice.tone === "danger"
      ? HB_COLORS.danger
      : notice.tone === "warning"
        ? HB_COLORS.warning
        : HB_COLORS.action;
  const background =
    notice.tone === "danger" ? "#FEF3F2" : notice.tone === "warning" ? "#FFFAEB" : "#F5F9FF";
  return (
    <View style={[styles.notice, { backgroundColor: background }]} accessibilityLiveRegion="polite">
      <Text variant="bodySmall" style={{ color }}>
        {notice.text}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.md,
    gap: HB_SPACING.xs,
  },
  cardOverdue: { borderColor: "#FDA29B" },
  cardLabelOnly: { borderColor: "#FEC84B" },
  doneCard: { backgroundColor: "#ECFDF3", borderColor: "#ABEFC6" },
  doneRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  doneText: { flex: 1, color: HB_COLORS.success, fontWeight: "600" },
  headerRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  title: { flex: 1, color: HB_COLORS.textPrimary, fontWeight: "700" },
  tagRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  tag: {
    fontSize: 12,
    lineHeight: 16,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 2,
    borderRadius: HB_RADIUS.control,
    overflow: "hidden",
    fontWeight: "600",
  },
  tagDanger: { backgroundColor: "#FEE4E2", color: HB_COLORS.danger },
  tagNeutral: { backgroundColor: HB_COLORS.surfaceMuted, color: HB_COLORS.textSecondary },
  tagBrand: { backgroundColor: "#E8F1FF", color: HB_COLORS.action },
  compareRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    padding: HB_SPACING.sm,
  },
  compareCell: { flex: 1, gap: 2 },
  compareLabel: { color: HB_COLORS.textSecondary },
  compareFrom: { color: HB_COLORS.textSecondary, fontWeight: "600" },
  compareTo: { color: HB_COLORS.brand, fontWeight: "700" },
  discountRow: { color: HB_COLORS.textSecondary },
  steps: { gap: HB_SPACING.xs, paddingVertical: HB_SPACING.xxs },
  stepRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  stepText: { color: HB_COLORS.textPrimary },
  stepMuted: { color: HB_COLORS.textSecondary },
  notice: { borderRadius: HB_RADIUS.control, paddingHorizontal: HB_SPACING.sm, paddingVertical: HB_SPACING.xs },
  blockedHint: { color: HB_COLORS.warning },
  printerRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xxs },
  printerText: { flex: 1, color: HB_COLORS.warning },
  actionRow: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: HB_SPACING.xs },
});
