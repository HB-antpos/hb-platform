import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, FlatList, Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { usePrinterStore } from "@/modules/printer/state";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { fetchCartonBatch, reportLabelsPrinted } from "../api";
import { readPackingError } from "../api-normalization";
import {
  computeBatchProgress,
  isBatchApiMissing,
  pendingLabelCartons,
  runLabelQueue,
  type BatchRowState,
} from "../batch-logic";
import { packingErrorMessage, printErrorDetail } from "../packing-errors";
import { formatLocalTime } from "../packing-logic";
import { usePackPreferences } from "../pack-preferences";
import { PACKING_BATCH_LIST, printCartonLabel } from "../pack-print";
import type { PackCartonSummary } from "../types";
import { PrinterStatusTag } from "../components/PrinterStatusTag";
import { MONO_FONT, PACK_COLORS, SectionTitle, Tag, WarningCard, packStyles } from "../components/pack-ui";

type RunState = "idle" | "running" | "paused" | "failed" | "disconnected";

/**
 * 一批空箱标的连续打印与断点续打：一张一张发给打印机，每成功一张就记一张并上报；断连 / 失败 / 暂停时停在当前张，
 * 「继续打印」只打还没打印的（labelPrintCount = 0）；点某一张可单独重打。退出后再进入从服务端恢复进度。
 */
export function BatchPrintScreen({
  batchGuid,
  autoPrint = false,
  copies = 1,
  duplicate = false,
}: {
  batchGuid: string;
  autoPrint?: boolean;
  copies?: number;
  duplicate?: boolean;
}) {
  const { t, language } = useAppTranslation("packing");
  const router = useRouter();
  const focused = useIsFocused();
  const queryClient = useQueryClient();
  const user = useAuthStore((state) => state.user);
  const printerStatus = usePrinterStore((state) => state.status);
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const [runState, setRunState] = useState<RunState>("idle");
  const [currentGuid, setCurrentGuid] = useState<string | null>(null);
  const [failure, setFailure] = useState<{ cartonNo: string; detail: string } | null>(null);
  // 本机已打印的次数：上报没成功时服务端仍是 0，界面不能因为刷新又退回「没打印」。
  const [localCounts, setLocalCounts] = useState<Record<string, number>>({});
  const [snackbar, setSnackbar] = useState("");
  const stopRef = useRef(false);
  const runningRef = useRef(false);
  const autoStartedRef = useRef(false);
  const operatorName = user?.fullName?.trim() || user?.username || null;
  const printCopies = Math.max(1, Math.min(5, Math.trunc(copies) || 1));

  const query = useQuery({
    queryKey: ["packing", "batch", batchGuid],
    queryFn: ({ signal }) => fetchCartonBatch(batchGuid, signal),
    enabled: focused && Boolean(batchGuid),
    staleTime: 0,
  });

  // 退出页面：当前这张打完就停（runLabelQueue 的 finally 会把已成功的都上报）。
  useEffect(
    () => () => {
      stopRef.current = true;
    },
    [],
  );

  const cartons = useMemo<PackCartonSummary[]>(
    () =>
      (query.data?.cartons ?? []).map((carton) => {
        const local = localCounts[carton.cartonGuid] ?? 0;
        return local > carton.labelPrintCount ? { ...carton, labelPrintCount: local } : carton;
      }),
    [localCounts, query.data],
  );
  const progress = useMemo(() => computeBatchProgress(cartons), [cartons]);
  const pending = useMemo(() => pendingLabelCartons(cartons), [cartons]);
  const printerConnected = printerStatus === "connected";

  const markPrintedLocally = useCallback((carton: PackCartonSummary) => {
    setLocalCounts((current) => ({ ...current, [carton.cartonGuid]: Math.max(current[carton.cartonGuid] ?? 0, carton.labelPrintCount) + 1 }));
  }, []);

  const finishRun = useCallback(
    (result: Awaited<ReturnType<typeof runLabelQueue>>, total: number) => {
      void queryClient.invalidateQueries({ queryKey: ["packing", "batches"] });
      void queryClient.invalidateQueries({ queryKey: ["packing", "cartons"] });
      void query.refetch();
      if (result.unreported.length > 0) setSnackbar(t("batch.unreported", { count: result.unreported.length }));
      if (result.failed) {
        setFailure({ cartonNo: result.failed.carton.cartonNo, detail: printErrorDetail(result.failed.error) });
        // 打印前连接已断（或这次写入时发现断开）：显示「已断开」，接上后继续；其他失败（卡纸等）显示失败原因。
        setRunState(usePrinterStore.getState().status === "connected" ? "failed" : "disconnected");
      } else if (result.stopped) {
        setRunState("paused");
      } else {
        setRunState(result.printed.length >= total ? "idle" : "paused");
      }
    },
    [query, queryClient, t],
  );

  /** 一张一张打印 queue；每成功一张就本地记一张并上报。 */
  const runQueue = useCallback(
    async (queue: PackCartonSummary[]) => {
      if (runningRef.current || queue.length === 0) return;
      runningRef.current = true;
      stopRef.current = false;
      setFailure(null);
      setRunState("running");
      try {
        const result = await runLabelQueue(queue, {
          print: (carton) => printCartonLabel(carton, printCopies, smallLabel, operatorName),
          report: (guids) => reportLabelsPrinted(guids),
          onStart: (carton) => setCurrentGuid(carton.cartonGuid),
          onPrinted: markPrintedLocally,
          shouldStop: () => stopRef.current,
        });
        setCurrentGuid(null);
        finishRun(result, queue.length);
      } finally {
        runningRef.current = false;
      }
    },
    [finishRun, markPrintedLocally, operatorName, printCopies, smallLabel],
  );

  // 「创建并打印」进来：数据加载完、打印机已连接就自动开始；只自动一次，之后的暂停 / 失败都由用户点按钮继续。
  useEffect(() => {
    if (!autoPrint || autoStartedRef.current || !query.data || !printerConnected) return;
    autoStartedRef.current = true;
    void runQueue(pendingLabelCartons(query.data.cartons));
  }, [autoPrint, printerConnected, query.data, runQueue]);

  const reprintOne = (carton: PackCartonSummary) => {
    if (runningRef.current) return;
    Alert.alert(t("batch.reprintTitle", { cartonNo: carton.cartonNo }), t("batch.reprintBody"), [
      { text: t("actions.cancel"), style: "cancel" },
      {
        text: t("batch.reprint"),
        onPress: () => {
          // 单张重打也走同一个执行器（上报 + 本地记数）。
          runningRef.current = true;
          setRunState("running");
          setFailure(null);
          void runLabelQueue([carton], {
            print: (item) => printCartonLabel(item, printCopies, smallLabel, operatorName),
            report: (guids) => reportLabelsPrinted(guids),
            onStart: (item) => setCurrentGuid(item.cartonGuid),
            onPrinted: markPrintedLocally,
          })
            .then((result) => {
              setCurrentGuid(null);
              runningRef.current = false;
              finishRun(result, 1);
              // 重打成功后回到暂停 / 空闲，不改变其余张的状态。
              if (!result.failed) {
                setRunState("idle");
                setSnackbar(t("batch.reprintDone", { cartonNo: carton.cartonNo }));
              }
            })
            .catch(() => {
              runningRef.current = false;
              setCurrentGuid(null);
              setRunState("idle");
            });
        },
      },
    ]);
  };

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PACKING_BATCH_LIST));

  const info = query.isError ? readPackingError(query.error) : null;
  const batch = query.data?.batch ?? null;
  const running = runState === "running";
  const statusLabel =
    progress.remaining === 0 && progress.total > 0
      ? t("batch.stateDone")
      : running
        ? t("batch.stateRunning")
        : runState === "disconnected"
          ? t("batch.stateDisconnected")
          : runState === "failed"
            ? t("batch.stateFailed")
            : runState === "paused"
              ? t("batch.statePaused")
              : progress.printed === 0
                ? t("batch.stateIdle")
                : t("batch.statePaused");
  const nextNo = progress.next?.cartonNo ?? "";

  const header = (
    <View style={styles.headerBlock}>
      <View style={packStyles.card}>
        <View style={packStyles.barRow}>
          <Text style={styles.bigCount}>
            {progress.printed}
            <Text style={styles.bigTotal}>{` ${t("batch.progressTail", { total: progress.total })}`}</Text>
          </Text>
          <Tag tone={progress.remaining === 0 && progress.total > 0 ? "green" : running ? "blue" : "orange"} label={statusLabel} />
        </View>
        <View accessibilityRole="progressbar" accessibilityValue={{ min: 0, max: progress.total, now: progress.printed }} style={styles.track}>
          <View style={[styles.fill, { width: `${Math.round(progress.ratio * 100)}%` }]} />
        </View>
      </View>
      {duplicate ? <Text style={packStyles.muted}>{t("batch.duplicate")}</Text> : null}
      {failure ? (
        <WarningCard>
          <Text style={styles.noteText}>{t("batch.failedNote", { cartonNo: failure.cartonNo, detail: failure.detail })}</Text>
        </WarningCard>
      ) : null}
      {runState === "disconnected" || (runState === "paused" && !printerConnected) ? (
        <WarningCard>
          <View style={styles.noteRow}>
            <MaterialCommunityIcons name="alert-outline" size={18} color={PACK_COLORS.warning} />
            <Text style={styles.noteText}>{t("batch.disconnectedNote", { done: progress.printed, next: nextNo })}</Text>
          </View>
          <Button mode="outlined" onPress={() => router.push("/(shell)/settings")} contentStyle={packStyles.buttonContent}>
            {t("batch.reconnect")}
          </Button>
        </WarningCard>
      ) : runState === "paused" ? (
        <WarningCard>
          <Text style={styles.noteText}>{t("batch.pausedNote", { done: progress.printed, next: nextNo })}</Text>
        </WarningCard>
      ) : null}
      {progress.remaining === 0 && progress.total > 0 ? <Text style={packStyles.muted}>{t("batch.allDone")}</Text> : null}
      {batch && batch.voidedCount > 0 ? <Text style={packStyles.muted}>{t("batch.voidedSkipped", { count: batch.voidedCount })}</Text> : null}
      <SectionTitle title={t("batch.thisBatch")} right={batch ? `${batch.firstCartonNo} – ${batch.lastCartonNo}` : null} />
    </View>
  );

  const empty = query.isLoading ? (
    <ActivityIndicator style={packStyles.loading} />
  ) : query.isError ? (
    <EmptyState
      title={
        info?.code === "BATCH_NOT_FOUND"
          ? t("batch.notFound")
          : info && isBatchApiMissing(info.status, info.code)
            ? t("batch.listUnavailable")
            : packingErrorMessage(query.error, t, language)
      }
      actionLabel={t("actions.retry")}
      onAction={() => void query.refetch()}
    />
  ) : (
    <EmptyState title={t("batch.listEmpty")} />
  );

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader
        title={t("batch.printTitle")}
        subtitle={batch ? t("batch.printSubtitle", { time: formatLocalTime(batch.createdAtUtc), count: batch.count }) : undefined}
        onBack={goBack}
        backLabel={t("actions.back")}
        right={<PrinterStatusTag />}
      />
      <FlatList
        contentContainerStyle={packStyles.body}
        data={progress.rows}
        keyExtractor={(row) => row.carton.cartonGuid}
        ListHeaderComponent={query.data ? header : null}
        ListEmptyComponent={empty}
        extraData={`${currentGuid}|${runState}`}
        renderItem={({ item }) => (
          <View style={[packStyles.cardList, styles.rowCard]}>
            <BatchRowView
              carton={item.carton}
              state={item.state}
              printing={currentGuid === item.carton.cartonGuid}
              canReprint={!running && printerConnected}
              onPress={() => reprintOne(item.carton)}
            />
          </View>
        )}
      />
      <View style={packStyles.bar}>
        {progress.total > 0 ? (
          <View style={packStyles.buttons}>
            <Button mode="outlined" onPress={goBack} style={[packStyles.button, packStyles.flexSmall]} contentStyle={packStyles.buttonContent}>
              {t("batch.later")}
            </Button>
            {running ? (
              <Button
                mode="contained-tonal"
                icon="pause"
                onPress={() => {
                  stopRef.current = true;
                  setSnackbar(t("batch.pausing"));
                }}
                style={[packStyles.button, packStyles.flex]}
                contentStyle={packStyles.buttonContent}
              >
                {t("batch.pause")}
              </Button>
            ) : (
              <Button
                mode="contained"
                icon="printer"
                disabled={pending.length === 0 || !printerConnected}
                onPress={() => void runQueue(pending)}
                style={[packStyles.button, packStyles.flex]}
                contentStyle={packStyles.buttonContent}
              >
                {progress.printed === 0 && runState === "idle"
                  ? t("batch.start", { count: pending.length })
                  : t("batch.continue", { count: pending.length })}
              </Button>
            )}
          </View>
        ) : null}
      </View>
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3500}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

function BatchRowView({
  carton,
  state,
  printing,
  canReprint,
  onPress,
}: {
  carton: PackCartonSummary;
  state: BatchRowState;
  printing: boolean;
  canReprint: boolean;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const activated = Boolean(carton.activatedAtUtc);
  const tag = printing ? (
    <Tag tone="blue" icon="printer" label={t("batch.rowPrinting")} />
  ) : state === "printed" ? (
    <Tag tone="green" icon="check-circle-outline" label={activated ? t("batch.rowActivated") : t("batch.rowPrinted")} />
  ) : state === "next" ? (
    <Tag tone="blue" icon="play" label={t("batch.rowNext")} />
  ) : (
    <Tag tone="gray" icon="clock-outline" label={t("batch.rowWaiting")} />
  );
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${carton.cartonNo} ${t("batch.reprintOne")}`}
      accessibilityState={{ disabled: !canReprint }}
      disabled={!canReprint}
      onPress={onPress}
      style={styles.row}
    >
      <View style={styles.rowMain}>
        <Text style={[styles.rowNo, state === "next" || printing ? styles.rowNoStrong : null]}>{carton.cartonNo}</Text>
        {carton.labelPrintCount > 1 ? (
          <Text style={packStyles.muted}>{t("batch.rowPrintedTimes", { count: carton.labelPrintCount })}</Text>
        ) : null}
      </View>
      {tag}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  headerBlock: { gap: 10 },
  bigCount: { fontSize: 28, lineHeight: 34, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  bigTotal: { fontSize: 14, fontWeight: "500", color: PACK_COLORS.textSecondary },
  track: { height: 8, borderRadius: 4, backgroundColor: PACK_COLORS.neutralChipBg, overflow: "hidden", marginTop: 8 },
  fill: { height: 8, borderRadius: 4, backgroundColor: PACK_COLORS.action },
  noteRow: { flexDirection: "row", alignItems: "flex-start", gap: 6 },
  noteText: { flex: 1, fontSize: 13, lineHeight: 19, color: PACK_COLORS.warningText },
  rowCard: { paddingVertical: 0 },
  row: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 10 },
  rowMain: { flex: 1, minWidth: 0 },
  rowNo: { fontFamily: MONO_FONT, fontSize: 15, color: PACK_COLORS.neutralChipText },
  rowNoStrong: { fontWeight: "700", color: PACK_COLORS.ink },
});
