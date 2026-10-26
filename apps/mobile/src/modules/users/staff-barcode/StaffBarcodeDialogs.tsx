import { useCallback, useEffect, useRef, useState, type MutableRefObject } from "react";
import { Pressable, StyleSheet, useWindowDimensions, View } from "react-native";
import * as Crypto from "expo-crypto";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import QRCode from "react-native-qrcode-svg";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { LabelPrinterSetupSheet } from "@/components/printer/LabelPrinterSetupSheet";
import { InfoCard, NoticeBox, resolvePersonalCodeLabelName, resolvePersonName } from "@/modules/people";
import { PeopleSheet, SHEET_BUTTON_STYLE, SheetActions } from "@/modules/people/detail/people-sheet";
import {
  confirmStaffCashierBarcodePrintApi,
  ensureStaffCashierBarcodeApi,
  getStaffCashierBarcodeApi,
} from "@/modules/users/api";
import {
  clearStaffBarcodeBatch,
  createStaffBarcodeBatch,
  hasIncompleteStaffBarcodeConfirmation,
  invalidateStaffBarcodeOperationSession,
  isStaffBarcodeOperationSessionCurrent,
  loadStaffBarcodeBatch,
  processNextStaffBarcodeTarget,
  resolveStaffBarcodeUncertain,
  runStaffBarcodeActionExclusive,
  saveStaffBarcodeBatch,
  skipStaffBarcodeTarget,
  updateStaffBarcodeOperationSession,
  type StaffBarcodeBatch,
  type StaffBarcodeBatchTarget,
  type StaffBarcodeOperationSession,
} from "@/modules/users/staff-barcode/controller";
import { staffBarcodeQueueSecureStorage } from "@/modules/users/staff-barcode/storage";
import type { StoreUserListItem } from "@/modules/users/types";
import { hydrateSavedPrinter, printEmployeeCashierBarcodeLabel } from "@/modules/printer/api";
import { personalCodeSymbology } from "@/modules/printer/use-personal-code-symbology";
import { runPersonalCodePrintExclusive } from "@/modules/printer/personal-code-print-lock";
import { usePrinterStore } from "@/modules/printer/state";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

function queryKey(actorGuid: string, storeCode: string, userGuid: string, generation: number) {
  return ["staffCashierBarcode", actorGuid, storeCode, userGuid, generation] as const;
}

function useOperationSession(key: string, active: boolean) {
  const sessionRef = useRef<StaffBarcodeOperationSession>({ key, active, generation: 1 });
  const next = updateStaffBarcodeOperationSession(sessionRef.current, key, active);
  if (next !== sessionRef.current) {
    // visible 的每次开关都会递增，避免同一 actor/store 快速关闭重开后旧请求通过 ABA 比较。
    sessionRef.current = next;
  }
  useEffect(() => () => {
    // 卸载不一定再触发 render，必须主动让所有旧 await token 失效。
    sessionRef.current = invalidateStaffBarcodeOperationSession(sessionRef.current);
  }, []);
  return sessionRef;
}

function isCurrentSession(sessionRef: MutableRefObject<StaffBarcodeOperationSession>, generation: number) {
  return isStaffBarcodeOperationSessionCurrent(sessionRef.current, generation);
}

function dependencies(
  queryClient: ReturnType<typeof useQueryClient>,
  actorGuid: string,
  generation: number,
  isScopeCurrent: () => boolean
) {
  return {
    ensureBarcode: async (target: StaffBarcodeBatchTarget, storeCode: string) => {
      const result = await ensureStaffCashierBarcodeApi(target.userGuid, storeCode);
      if (!isScopeCurrent()) throw new Error("STAFF_BARCODE_SCOPE_CHANGED");
      queryClient.setQueryData(queryKey(actorGuid, storeCode, target.userGuid, generation), result);
      return result;
    },
    printLabel: async (target: StaffBarcodeBatchTarget, barcode: string) => {
      const printed = await printEmployeeCashierBarcodeLabel({
        employeeName: target.employeeName,
        username: target.username,
        barcode,
        // 与本人码卡片共用设备偏好；批量打印每张都读最新值，默认二维码。
        symbology: await personalCodeSymbology.hydrate(),
      });
      if (!printed) throw new Error("STAFF_BARCODE_PRINT_NOT_ACCEPTED");
    },
    confirmPrint: async (target: StaffBarcodeBatchTarget, storeCode: string, pending: { barcode: string; attemptId: string }) => {
      const result = await confirmStaffCashierBarcodePrintApi(target.userGuid, storeCode, pending.barcode, pending.attemptId);
      if (!isScopeCurrent()) throw new Error("STAFF_BARCODE_SCOPE_CHANGED");
      queryClient.setQueryData(queryKey(actorGuid, storeCode, target.userGuid, generation), result);
      return result;
    },
    createAttemptId: () => Crypto.randomUUID(),
    runExclusive: runPersonalCodePrintExclusive,
    isScopeCurrent,
  };
}

export function StaffBarcodeDialog({
  actorGuid,
  storeCode,
  user,
  visible,
  onDismiss,
}: {
  actorGuid: string;
  storeCode: string;
  user: StoreUserListItem | null;
  visible: boolean;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["userManagement", "peopleDetail", "common"]);
  const { fontScale, width } = useWindowDimensions();
  const queryClient = useQueryClient();
  const printer = usePrinterStore((state) => state.savedPrinter);
  const printerStatus = usePrinterStore((state) => state.status);
  const printerHydrated = usePrinterStore((state) => state.hydrated);
  // 更换打印机就在弹层内完成（LabelPrinterSetupSheet），不再关掉弹层跳去设置页丢失上下文。
  const [printerSheetVisible, setPrinterSheetVisible] = useState(false);
  const [operation, setOperation] = useState<StaffBarcodeBatch | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [pendingLoaded, setPendingLoaded] = useState(false);
  const [restoreFailed, setRestoreFailed] = useState(false);
  const actionGateRef = useRef({ inFlight: false });
  const userGuid = user?.userGUID ?? "";
  const operationScope = userGuid ? `single:${userGuid}` : "single:none";
  const sessionRef = useOperationSession(
    `${actorGuid}:${storeCode}:${userGuid}`,
    visible && Boolean(actorGuid && storeCode && userGuid)
  );
  const sessionGeneration = sessionRef.current.generation;
  const barcodeQuery = useQuery({
    queryKey: queryKey(actorGuid, storeCode, userGuid, sessionGeneration),
    enabled: visible && Boolean(storeCode && userGuid && user?.status === 1),
    queryFn: () => getStaffCashierBarcodeApi(userGuid, storeCode),
  });

  useEffect(() => {
    setOperation(null);
    setPendingLoaded(false);
    setRestoreFailed(false);
    setError("");
    setBusy(false);
    if (!visible || !actorGuid || !storeCode || !userGuid) return;
    const generation = sessionRef.current.generation;
    void loadStaffBarcodeBatch(staffBarcodeQueueSecureStorage, actorGuid, storeCode, operationScope)
      .then((saved) => {
        if (!isCurrentSession(sessionRef, generation)) return;
        setOperation(saved);
        setPendingLoaded(true);
      })
      .catch(() => {
        if (!isCurrentSession(sessionRef, generation)) return;
        setRestoreFailed(true);
        setError(t("staffBarcode.errors.restore"));
      });
  }, [actorGuid, operationScope, sessionRef, storeCode, t, userGuid, visible]);

  useEffect(() => {
    if (!visible || printerHydrated) return;
    void hydrateSavedPrinter().catch(() => undefined);
  }, [printerHydrated, visible]);

  const createCode = useCallback(async () => {
    if (!user) return;
    await runStaffBarcodeActionExclusive(actionGateRef.current, async () => {
    const generation = sessionRef.current.generation;
    setBusy(true);
    setError("");
    try {
      const result = await ensureStaffCashierBarcodeApi(user.userGUID, storeCode);
      if (!isCurrentSession(sessionRef, generation)) return;
      queryClient.setQueryData(queryKey(actorGuid, storeCode, user.userGUID, generation), result);
    } catch {
      if (isCurrentSession(sessionRef, generation)) setError(t("staffBarcode.errors.unavailable"));
    } finally {
      if (isCurrentSession(sessionRef, generation)) setBusy(false);
    }
    });
  }, [actorGuid, queryClient, sessionRef, storeCode, t, user]);

  const printCode = useCallback(async () => {
    if (!user || !pendingLoaded || restoreFailed) return;
    await runStaffBarcodeActionExclusive(actionGateRef.current, async () => {
    const generation = sessionRef.current.generation;
    const persist = async (next: StaffBarcodeBatch) => {
      // 旧作用域仍写回自己的安全记录，但绝不再污染新账号/新分店界面。
      await saveStaffBarcodeBatch(staffBarcodeQueueSecureStorage, next, operationScope);
      if (isCurrentSession(sessionRef, generation)) setOperation(next);
    };
    setBusy(true);
    setError("");
    try {
      const initial = operation ?? createStaffBarcodeBatch({
        actorGuid,
        storeCode,
        targets: [{
          userGuid: user.userGUID,
          employeeName: resolvePersonalCodeLabelName(user),
          username: user.username,
          active: user.status === 1,
        }],
      });
      await persist(initial);
      const next = await processNextStaffBarcodeTarget(
        initial,
        dependencies(queryClient, actorGuid, generation, () => isCurrentSession(sessionRef, generation)),
        persist
      );
      if (!isCurrentSession(sessionRef, generation)) return;
      const target = next.targets[0];
      if (target.status === "done") {
        await clearStaffBarcodeBatch(staffBarcodeQueueSecureStorage, actorGuid, storeCode, operationScope);
        if (isCurrentSession(sessionRef, generation)) setOperation(null);
      } else if (target.status === "obsolete") {
        await clearStaffBarcodeBatch(staffBarcodeQueueSecureStorage, actorGuid, storeCode, operationScope);
        if (!isCurrentSession(sessionRef, generation)) return;
        setOperation(null);
        await barcodeQuery.refetch();
        if (isCurrentSession(sessionRef, generation)) setError(t("staffBarcode.errors.obsolete"));
      } else if (target.status === "uncertain") {
        setError(t("staffBarcode.errors.uncertain"));
      } else if (target.status === "failed" && target.pendingConfirmation?.phase === "printed") {
        setError(t("staffBarcode.confirmOnlyHint"));
      } else {
        setError(t("staffBarcode.errors.printFailed"));
      }
    } catch {
      if (isCurrentSession(sessionRef, generation)) setError(t("staffBarcode.errors.printFailed"));
    } finally {
      if (isCurrentSession(sessionRef, generation)) setBusy(false);
    }
    });
  }, [actorGuid, barcodeQuery, operation, operationScope, pendingLoaded, queryClient, restoreFailed, sessionRef, storeCode, t, user]);

  const resolveUncertain = useCallback(async (choice: "printed" | "notPrinted") => {
    if (!operation || !user) return;
    await runStaffBarcodeActionExclusive(actionGateRef.current, async () => {
    const generation = sessionRef.current.generation;
    const next = resolveStaffBarcodeUncertain(operation, user.userGUID, choice);
    await saveStaffBarcodeBatch(staffBarcodeQueueSecureStorage, next, operationScope);
    if (isCurrentSession(sessionRef, generation)) {
      setOperation(next);
      setError(choice === "printed" ? t("staffBarcode.confirmOnlyHint") : "");
    }
    });
  }, [operation, operationScope, sessionRef, t, user]);

  const openPrinterSetup = useCallback(() => setPrinterSheetVisible(true), []);
  // 弹层被外部关闭时，内嵌的打印机设置也一并收起，下次打开从干净状态开始。
  useEffect(() => {
    if (!visible) setPrinterSheetVisible(false);
  }, [visible]);

  const currentTarget = operation?.targets[0];
  const confirmationOnly = currentTarget?.status === "failed"
    && currentTarget.pendingConfirmation?.phase === "printed";
  const stackActions = fontScale > 1.2 || width < 380;
  const data = barcodeQuery.isError ? undefined : barcodeQuery.data;
  const canOperate = Boolean(user && user.status === 1);

  if (!visible) return null;

  const identity = resolvePersonName({
    fullName: user?.fullName,
    givenName: user?.givenName,
    middleName: user?.middleName,
    familyName: user?.familyName,
    nickname: user?.nickname,
    username: user?.username,
  });

  return (
    <PeopleSheet
      title={t("staffBarcode.title")}
      subtitle={`${identity.primary} · @${user?.username ?? ""} · ${storeCode}`}
      closeLabel={t("common:actions.close")}
      onDismiss={onDismiss}
      dismissable={!busy}
      testID="staff-barcode-dialog"
      footer={(
        <SheetActions stacked={stackActions}>
          <Button mode="outlined" style={SHEET_BUTTON_STYLE} onPress={onDismiss} disabled={busy}>{t("actions.cancel")}</Button>
          {!data?.exists ? <Button mode="outlined" style={SHEET_BUTTON_STYLE} onPress={() => void createCode()} disabled={!canOperate || busy || !pendingLoaded || restoreFailed}>{t("staffBarcode.actions.create")}</Button> : null}
          <Button style={SHEET_BUTTON_STYLE} mode="contained" icon="printer-outline" onPress={() => void printCode()} loading={busy} disabled={!canOperate || busy || !pendingLoaded || restoreFailed || currentTarget?.status === "uncertain"}>
            {confirmationOnly ? t("staffBarcode.actions.retryConfirmation") : data?.exists ? t("staffBarcode.actions.print") : t("staffBarcode.actions.createAndPrint")}
          </Button>
        </SheetActions>
      )}
    >
      {!canOperate ? <NoticeBox tone="red" icon="alert-circle-outline">{t("staffBarcode.errors.inactive")}</NoticeBox> : null}
      {barcodeQuery.isLoading ? <ActivityIndicator /> : null}
      {barcodeQuery.isError ? <NoticeBox tone="red" icon="alert-circle-outline">{t("staffBarcode.errors.unavailable")}</NoticeBox> : null}
      {!barcodeQuery.isLoading && !barcodeQuery.isError && !data?.exists ? (
        <Text variant="bodySmall" style={styles.muted}>{t("staffBarcode.emptyDescription")}</Text>
      ) : null}
      {data?.exists && data.barcode ? (
        <View style={styles.codePanel}>
          <View style={styles.qrFrame}>
            <QRCode value={data.barcode} size={176} />
          </View>
          <Text style={styles.codeText} selectable>{data.barcode}</Text>
          <Text variant="bodySmall" style={styles.muted}>{t("staffBarcode.printCount", { count: data.printCount })}</Text>
        </View>
      ) : null}
      <PrinterRow
        name={printer?.name || ""}
        status={printerStatus}
        busy={busy}
        onChange={openPrinterSetup}
      />
      <Text style={styles.hint}>{t("peopleDetail:barcode.changeHint")}</Text>
      {currentTarget?.status === "uncertain" ? (
        <View style={styles.actions}>
          <NoticeBox tone="red" icon="alert-circle-outline">{t("staffBarcode.errors.uncertain")}</NoticeBox>
          <Button mode="contained-tonal" onPress={() => void resolveUncertain("printed")}>{t("staffBarcode.actions.printed")}</Button>
          <Button mode="outlined" onPress={() => void resolveUncertain("notPrinted")}>{t("staffBarcode.actions.notPrinted")}</Button>
        </View>
      ) : null}
      {confirmationOnly && !error ? <NoticeBox tone="amber" icon="alert-outline">{t("staffBarcode.confirmOnlyHint")}</NoticeBox> : null}
      {error ? <NoticeBox tone="red" icon="alert-circle-outline">{error}</NoticeBox> : null}
      <LabelPrinterSetupSheet visible={printerSheetVisible} onDismiss={() => setPrinterSheetVisible(false)} />
    </PeopleSheet>
  );
}

export function StaffBarcodeBatchDialog({
  actorGuid,
  storeCode,
  users,
  visible,
  onDismiss,
}: {
  actorGuid: string;
  storeCode: string;
  users: StoreUserListItem[];
  visible: boolean;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["userManagement", "peopleDetail", "common"]);
  const { fontScale, width } = useWindowDimensions();
  const queryClient = useQueryClient();
  const printer = usePrinterStore((state) => state.savedPrinter);
  const printerStatus = usePrinterStore((state) => state.status);
  const printerHydrated = usePrinterStore((state) => state.hydrated);
  const [printerSheetVisible, setPrinterSheetVisible] = useState(false);
  const [batch, setBatch] = useState<StaffBarcodeBatch | null>(null);
  const [busy, setBusy] = useState(false);
  const [pendingLoaded, setPendingLoaded] = useState(false);
  const [restoreFailed, setRestoreFailed] = useState(false);
  const actionGateRef = useRef({ inFlight: false });
  const sessionRef = useOperationSession(
    `${actorGuid}:${storeCode}`,
    visible && Boolean(actorGuid && storeCode)
  );

  useEffect(() => {
    setBatch(null);
    setPendingLoaded(false);
    setRestoreFailed(false);
    setBusy(false);
    if (!visible || !actorGuid || !storeCode) return;
    const generation = sessionRef.current.generation;
    void loadStaffBarcodeBatch(staffBarcodeQueueSecureStorage, actorGuid, storeCode)
      .then((saved) => {
        if (!isCurrentSession(sessionRef, generation)) return;
        setBatch(saved ?? createStaffBarcodeBatch({
          actorGuid,
          storeCode,
          targets: users.map((user) => ({ userGuid: user.userGUID, employeeName: resolvePersonalCodeLabelName(user), username: user.username, active: user.status === 1 })),
        }));
        setPendingLoaded(true);
      })
      .catch(() => {
        if (isCurrentSession(sessionRef, generation)) setRestoreFailed(true);
      });
  }, [actorGuid, sessionRef, storeCode, users, visible]);

  useEffect(() => {
    if (!visible || printerHydrated) return;
    void hydrateSavedPrinter().catch(() => undefined);
  }, [printerHydrated, visible]);

  const nextActionable = batch?.targets.find((target) => target.status === "pending" || target.status === "failed");
  const nextConfirmationOnly = nextActionable?.status === "failed"
    && nextActionable.pendingConfirmation?.phase === "printed";
  const uncertain = batch?.targets.find((target) => target.status === "uncertain");
  const runNext = useCallback(async () => {
    if (!batch || uncertain || !pendingLoaded || restoreFailed) return;
    await runStaffBarcodeActionExclusive(actionGateRef.current, async () => {
    const generation = sessionRef.current.generation;
    const persist = async (next: StaffBarcodeBatch) => {
      await saveStaffBarcodeBatch(staffBarcodeQueueSecureStorage, next);
      if (isCurrentSession(sessionRef, generation)) setBatch(next);
    };
    setBusy(true);
    try {
      let current = batch;
      // 正常项串行连续处理；遇到失败或不确定立即暂停，把决定权交还给用户。
      while (isCurrentSession(sessionRef, generation)) {
        const actionable = current.targets.find((target) => target.status === "pending" || target.status === "failed");
        if (!actionable) break;
        const next = await processNextStaffBarcodeTarget(
          current,
          dependencies(queryClient, actorGuid, generation, () => isCurrentSession(sessionRef, generation)),
          persist
        );
        const result = next.targets.find((target) => target.userGuid === actionable.userGuid);
        current = next;
        if (!result || result.status !== "done") break;
      }
    } finally {
      if (isCurrentSession(sessionRef, generation)) setBusy(false);
    }
    });
  }, [actorGuid, batch, pendingLoaded, queryClient, restoreFailed, sessionRef, uncertain]);
  const resolveUncertain = useCallback(async (choice: "printed" | "notPrinted") => {
    if (!batch || !uncertain) return;
    await runStaffBarcodeActionExclusive(actionGateRef.current, async () => {
    const generation = sessionRef.current.generation;
    const next = resolveStaffBarcodeUncertain(batch, uncertain.userGuid, choice);
    await saveStaffBarcodeBatch(staffBarcodeQueueSecureStorage, next);
    if (isCurrentSession(sessionRef, generation)) setBatch(next);
    });
  }, [batch, sessionRef, uncertain]);
  const skip = useCallback(async () => {
    if (!batch || !nextActionable) return;
    await runStaffBarcodeActionExclusive(actionGateRef.current, async () => {
    const generation = sessionRef.current.generation;
    const next = skipStaffBarcodeTarget(batch, nextActionable.userGuid);
    await saveStaffBarcodeBatch(staffBarcodeQueueSecureStorage, next);
    if (isCurrentSession(sessionRef, generation)) setBatch(next);
    });
  }, [batch, nextActionable, sessionRef]);
  const finish = useCallback(async () => {
    if (!pendingLoaded || restoreFailed) return;
    await runStaffBarcodeActionExclusive(actionGateRef.current, async () => {
    const generation = sessionRef.current.generation;
    if (!hasIncompleteStaffBarcodeConfirmation(batch)) {
      await clearStaffBarcodeBatch(staffBarcodeQueueSecureStorage, actorGuid, storeCode);
    }
    if (isCurrentSession(sessionRef, generation)) {
      setBatch(null);
      onDismiss();
    }
    });
  }, [actorGuid, batch, onDismiss, pendingLoaded, restoreFailed, sessionRef, storeCode]);

  const openPrinterSetup = useCallback(() => setPrinterSheetVisible(true), []);
  useEffect(() => {
    if (!visible) setPrinterSheetVisible(false);
  }, [visible]);

  const done = batch?.targets.filter((target) => target.status === "done").length ?? 0;
  const stackActions = fontScale > 1.2 || width < 380;
  if (!visible) return null;

  return (
    <PeopleSheet
      title={t("staffBarcode.batch.title", { count: users.length })}
      subtitle={t("staffBarcode.batch.summary", { store: storeCode, done, total: batch?.targets.length ?? users.length })}
      closeLabel={t("common:actions.close")}
      onDismiss={onDismiss}
      dismissable={!busy}
      testID="staff-barcode-batch-dialog"
      footer={(
        <SheetActions stacked={stackActions}>
          <Button mode="outlined" style={SHEET_BUTTON_STYLE} onPress={() => void finish()} disabled={busy || !pendingLoaded || restoreFailed}>{t("staffBarcode.actions.end")}</Button>
          {nextActionable && !uncertain ? <Button mode="outlined" style={SHEET_BUTTON_STYLE} onPress={() => void skip()} disabled={busy}>{t("staffBarcode.actions.skip")}</Button> : null}
          <Button style={SHEET_BUTTON_STYLE} mode="contained" icon="printer-outline" onPress={() => void runNext()} loading={busy} disabled={busy || !pendingLoaded || restoreFailed || Boolean(uncertain) || !nextActionable}>
            {nextConfirmationOnly ? t("staffBarcode.actions.retryConfirmation") : done ? t("staffBarcode.actions.continue") : t("staffBarcode.actions.start")}
          </Button>
        </SheetActions>
      )}
    >
      <PrinterRow
        name={printer?.name || ""}
        status={printerStatus}
        busy={busy}
        onChange={openPrinterSetup}
      />
      <Text style={styles.hint}>{t("peopleDetail:barcode.changeHint")}</Text>
      {restoreFailed ? <NoticeBox tone="red" icon="alert-circle-outline">{t("staffBarcode.errors.restore")}</NoticeBox> : null}
      {batch?.targets.map((target) => (
        <View key={target.userGuid} style={styles.queueRow}>
          <View style={styles.queueIdentity}><Text>{target.employeeName}</Text><Text variant="bodySmall" style={styles.muted}>@{target.username}</Text></View>
          <Text variant="labelMedium">{target.status === "failed" && target.pendingConfirmation?.phase === "printed"
            ? t("staffBarcode.batch.statuses.confirmationPending")
            : t(`staffBarcode.batch.statuses.${target.status}`)}</Text>
        </View>
      ))}
      {uncertain ? (
        <View style={styles.actions}>
          <NoticeBox tone="red" icon="alert-circle-outline">{t("staffBarcode.errors.uncertain")}</NoticeBox>
          <Button mode="contained-tonal" onPress={() => void resolveUncertain("printed")}>{t("staffBarcode.actions.printed")}</Button>
          <Button mode="outlined" onPress={() => void resolveUncertain("notPrinted")}>{t("staffBarcode.actions.notPrinted")}</Button>
        </View>
      ) : null}
      <LabelPrinterSetupSheet visible={printerSheetVisible} onDismiss={() => setPrinterSheetVisible(false)} />
    </PeopleSheet>
  );
}

/** 打印机状态行：已连接 / 未连接 + 打印机名，右侧「更换」就地打开打印机设置弹层。 */
function PrinterRow({ name, status, busy, onChange }: { name: string; status: string; busy: boolean; onChange: () => void }) {
  const { t } = useAppTranslation(["userManagement", "peopleDetail"]);
  const connected = status === "connected";
  return (
    <InfoCard>
      <View style={styles.printerRow}>
        <View style={[styles.printerIcon, connected ? styles.printerIconOk : null]}>
          <Icon source={connected ? "printer-check" : "printer-off"} size={20} color={connected ? HB_COLORS.success : HB_COLORS.textSecondary} />
        </View>
        <View style={styles.printerCopy}>
          <Text style={styles.printerTitle}>
            {t("peopleDetail:barcode.printerTitle", { status: t(`staffBarcode.printerStatuses.${status}`, status) })}
          </Text>
          <Text style={styles.printerSub} numberOfLines={1}>{name || t("staffBarcode.noPrinter")}</Text>
        </View>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("peopleDetail:barcode.changePrinter")}
          disabled={busy}
          hitSlop={8}
          onPress={onChange}
          testID="staff-barcode-change-printer"
        >
          <Text style={[styles.printerChange, busy && styles.disabledText]}>{t("peopleDetail:barcode.changePrinter")}</Text>
        </Pressable>
      </View>
    </InfoCard>
  );
}

const styles = StyleSheet.create({
  actions: { gap: HB_SPACING.sm },
  codePanel: { alignItems: "center", gap: HB_SPACING.xs, paddingVertical: HB_SPACING.xs },
  codeText: { color: HB_COLORS.textPrimary, fontFamily: "Menlo", fontSize: 14, fontWeight: "700", letterSpacing: 0.5 },
  disabledText: { opacity: 0.45 },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18 },
  muted: { color: HB_COLORS.textSecondary },
  printerChange: { color: HB_COLORS.action, fontSize: 13.5, fontWeight: "800" },
  printerCopy: { flex: 1, minWidth: 0 },
  printerIcon: { alignItems: "center", backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 10, height: 36, justifyContent: "center", width: 36 },
  printerIconOk: { backgroundColor: "#ECFDF3" },
  printerRow: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 56, paddingHorizontal: 14, paddingVertical: 10 },
  printerSub: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  printerTitle: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "700" },
  qrFrame: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, borderWidth: 1, padding: 10 },
  queueIdentity: { flex: 1, minWidth: 0 },
  queueRow: { alignItems: "center", borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.control, borderWidth: StyleSheet.hairlineWidth, flexDirection: "row", gap: HB_SPACING.sm, padding: HB_SPACING.sm },
});
