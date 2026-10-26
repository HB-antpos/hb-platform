import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AccessibilityInfo, Alert, AppState, findNodeHandle, InteractionManager, Modal, Platform, Pressable, ScrollView, StyleSheet, View } from "react-native";
import * as Crypto from "expo-crypto";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import QRCode from "react-native-qrcode-svg";
import { ActivityIndicator, Button, HelperText, Icon, SegmentedButtons, Surface, Text } from "react-native-paper";

import {
  confirmMyCashierBarcodePrintApi,
  getMyCashierBarcodeApi,
  refreshMyCashierBarcodeApi,
} from "./api";
import {
  classifyCashierBarcodePrintError,
  canRefreshCashierBarcode,
  CashierBarcodePendingChangedError,
  CashierBarcodePrintConfirmationError,
  CashierBarcodeRevalidationError,
  executeCashierBarcodePrint,
  isCashierBarcodeChangedError,
  loadCashierBarcodePrintPending,
  prepareNewCashierBarcodePrint,
  prepareUncertainCashierBarcodeReprint,
  resolveUncertainCashierBarcodePrint,
  saveCashierBarcodePrintPending,
  type PendingCashierBarcodePrintConfirmation,
} from "./cashier-barcode";
import { getCashierBarcodeQueryKey } from "./cache-keys";
import { Ean13Barcode } from "./Ean13Barcode";
import { isValidEan13 } from "./ean13-modules";
import { PersonalCodePrintBusyError, runPersonalCodePrintExclusive } from "@/modules/printer/personal-code-print-lock";
import { personalCodeSymbology, usePersonalCodeSymbology } from "@/modules/printer/use-personal-code-symbology";
import type { PersonalCodeSymbology } from "@/modules/printer/personal-code-symbology";
import { printEmployeeCashierBarcodeLabel } from "@/modules/printer/api";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { SecureStorage } from "@/shared/storage/secure";
import {
  activateCashierPrintSecureSession,
  type CashierPrintSecureSession,
} from "@/shared/storage/cashier-print-secure-session";

type CashierPrintAction = "default" | "confirmUncertain" | "reprintUncertain";

/**
 * 当前账号个人码的码值（与卡片共用同一份查询缓存，不会多发请求）。
 * 设置页名片要在二维码旁显示码值：扫码枪扫不上时可以手输。
 */
export function usePersonalCodeValue(userIdentity: string, enabled = true) {
  const queryKey = useMemo(() => getCashierBarcodeQueryKey(userIdentity), [userIdentity]);
  // 设备授权会话没有个人码：enabled=false 时不发请求。
  const query = useQuery({ queryKey, queryFn: getMyCashierBarcodeApi, enabled });
  return query.data?.exists ? (query.data.barcode?.trim() ?? "") : "";
}

export function CashierBarcodeCard({
  employeeName,
  username,
  userIdentity,
  compact = false,
  expandLabel,
  variant,
}: {
  employeeName: string;
  username: string;
  userIdentity: string;
  compact?: boolean;
  expandLabel?: string;
  /** "tile"：只显示一块 104px 二维码（设置页名片右侧用），点开仍是同一个弹层（放大、刷新、打印）。 */
  variant?: "tile";
}) {
  const { t } = useAppTranslation(["employeeProfile", "common", "settings"]);
  const router = useRouter();
  const [expanded, setExpanded] = useState(false);
  const modalHeadingRef = useRef<View>(null);
  const compactTriggerRef = useRef<View>(null);
  const queryClient = useQueryClient();
  const [pendingConfirmation, setPendingConfirmation] =
    useState<PendingCashierBarcodePrintConfirmation | null>(null);
  const [pendingLoaded, setPendingLoaded] = useState(false);
  const pendingIdentityRef = useRef(userIdentity);
  const secureSessionRef = useRef<CashierPrintSecureSession | null>(null);
  const mismatchHandledRef = useRef("");
  const queryKey = useMemo(() => getCashierBarcodeQueryKey(userIdentity), [userIdentity]);
  const barcodeQuery = useQuery({ queryKey, queryFn: getMyCashierBarcodeApi });
  const [symbology, setSymbology] = usePersonalCodeSymbology();

  const persistPendingConfirmation = useCallback(async (
    value: PendingCashierBarcodePrintConfirmation | null
  ) => {
    const targetIdentity = userIdentity;
    const session = secureSessionRef.current;
    if (!session || session.userIdentity !== targetIdentity) {
      throw new Error("Cashier print secure session expired.");
    }
    await saveCashierBarcodePrintPending(SecureStorage, targetIdentity, value, session);
    if (pendingIdentityRef.current === targetIdentity) {
      setPendingConfirmation(value);
    }
  }, [userIdentity]);

  const refreshMutation = useMutation({
    mutationFn: refreshMyCashierBarcodeApi,
    onSuccess: (data) => {
      setPendingConfirmation(null);
      queryClient.setQueryData(queryKey, data);
    },
  });
  const printMutation = useMutation({
    mutationFn: (action: CashierPrintAction) => runPersonalCodePrintExclusive(async () => {
      let barcode = barcodeQuery.data?.barcode;
      if (!barcode) {
        throw new Error(t("cashierBarcode.empty"));
      }
      let effectivePending = pendingConfirmation;
      const refetchBarcode = async () => {
        const result = await barcodeQuery.refetch();
        return result.isError || !result.data?.exists ? null : result.data.barcode ?? null;
      };
      if (action === "confirmUncertain" && effectivePending?.phase === "printing") {
        effectivePending = await resolveUncertainCashierBarcodePrint(effectivePending, "printed");
        await persistPendingConfirmation(effectivePending);
      } else if (action === "reprintUncertain") {
        if (!effectivePending) {
          throw new CashierBarcodeRevalidationError();
        }
        barcode = await prepareUncertainCashierBarcodeReprint({
          pending: effectivePending,
          refetchBarcode,
          onPendingChange: persistPendingConfirmation,
        });
        effectivePending = null;
      } else if (!effectivePending) {
        // 每个会实际出纸的新 attempt 都必须先取得服务端权威条码。
        barcode = await prepareNewCashierBarcodePrint({
          cachedBarcode: barcode,
          refetchBarcode,
        });
      }
      return executeCashierBarcodePrint({
        pending: effectivePending,
        barcode,
        createAttemptId: () => Crypto.randomUUID(),
        printLabel: async (value) => {
          const printed = await printEmployeeCashierBarcodeLabel({
            employeeName,
            username,
            barcode: value,
            // 打印前确保偏好已读取完毕，避免刚进页面就点打印时误用默认值。
            symbology: await personalCodeSymbology.hydrate(),
          });
          if (!printed) {
            throw new Error(t("cashierBarcode.printFailed"));
          }
        },
        confirmPrint: (confirmation) => confirmMyCashierBarcodePrintApi(
          confirmation.barcode,
          confirmation.attemptId
        ),
        onPendingChange: persistPendingConfirmation,
      });
    }),
    onSuccess: (data) => queryClient.setQueryData(queryKey, data),
    onError: (error) => {
      if (error instanceof PersonalCodePrintBusyError) {
        Alert.alert(t("cashierBarcode.title"), t("settings:account.personalCodeBusy"));
        return;
      }
      if (error instanceof CashierBarcodePendingChangedError || isCashierBarcodeChangedError(error)) {
        void persistPendingConfirmation(null).catch(() => undefined);
        // 实体标签已打印但后台条码已变化，立即重新加载，避免继续显示过期条码。
        void queryClient.invalidateQueries({ queryKey });
        Alert.alert(t("cashierBarcode.changedTitle"), t("cashierBarcode.changed"));
        return;
      }
      if (error instanceof CashierBarcodePrintConfirmationError) {
        Alert.alert(
          t("cashierBarcode.confirmationPendingTitle"),
          t("cashierBarcode.confirmationPending")
        );
        return;
      }
      if (error instanceof CashierBarcodeRevalidationError) {
        void queryClient.invalidateQueries({ queryKey });
        Alert.alert(t("cashierBarcode.revalidationFailedTitle"), t("cashierBarcode.revalidationFailed"));
        return;
      }
      Alert.alert(
        t("cashierBarcode.printFailed"),
        t(`cashierBarcode.printErrors.${classifyCashierBarcodePrintError(error)}`),
        [
          { text: t("common:actions.cancel"), style: "cancel" },
          { text: t("cashierBarcode.openPrinterSettings"), onPress: () => router.navigate("/(shell)/settings") },
        ]
      );
    },
  });

  useEffect(() => {
    setExpanded(false);
    pendingIdentityRef.current = userIdentity;
    const secureSession = activateCashierPrintSecureSession(userIdentity);
    secureSessionRef.current = secureSession;
    mismatchHandledRef.current = "";
    setPendingConfirmation(null);
    setPendingLoaded(false);
    let cancelled = false;
    void loadCashierBarcodePrintPending(SecureStorage, userIdentity, secureSession)
      .then((value) => {
        if (!cancelled && pendingIdentityRef.current === userIdentity) {
          setPendingConfirmation(value);
          setPendingLoaded(true);
        }
      })
      .catch(() => {
        if (!cancelled && pendingIdentityRef.current === userIdentity) {
          // 无法确认磁盘上是否存在未完成打印时保持操作禁用，避免重复出纸。
          setPendingLoaded(false);
          Alert.alert(t("cashierBarcode.recoveryFailedTitle"), t("cashierBarcode.recoveryFailed"));
        }
      });
    return () => { cancelled = true; };
  }, [t, userIdentity]);

  useEffect(() => {
    const subscription = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        void queryClient.invalidateQueries({ queryKey });
      }
    });
    return () => subscription.remove();
  }, [queryClient, queryKey]);

  useEffect(() => {
    const currentBarcode = barcodeQuery.data?.barcode;
    if (!pendingLoaded || !pendingConfirmation || !currentBarcode
      || pendingConfirmation.barcode === currentBarcode
      || mismatchHandledRef.current === pendingConfirmation.attemptId) {
      return;
    }
    mismatchHandledRef.current = pendingConfirmation.attemptId;
    // 恢复到历史条码 attempt 时立即清理，禁止确认旧码或自动打印新码。
    void persistPendingConfirmation(null).then(() => {
      void queryClient.invalidateQueries({ queryKey });
      Alert.alert(t("cashierBarcode.changedTitle"), t("cashierBarcode.changed"));
    }).catch(() => undefined);
  }, [barcodeQuery.data?.barcode, pendingConfirmation, pendingLoaded, persistPendingConfirmation, queryClient, queryKey, t]);

  const handlePrintPress = () => {
    if (pendingConfirmation?.phase !== "printing") {
      printMutation.mutate("default");
      return;
    }
    Alert.alert(t("cashierBarcode.uncertainTitle"), t("cashierBarcode.uncertainDescription"), [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("cashierBarcode.notPrintedReprint"),
        style: "destructive",
        onPress: () => printMutation.mutate("reprintUncertain"),
      },
      {
        text: t("cashierBarcode.printedConfirmOnly"),
        onPress: () => printMutation.mutate("confirmUncertain"),
      },
    ]);
  };

  const confirmRefresh = () => {
    Alert.alert(t("cashierBarcode.refreshTitle"), t("cashierBarcode.refreshDescription"), [
      { text: t("common:actions.cancel"), style: "cancel" },
      { text: t("cashierBarcode.refresh"), style: "destructive", onPress: () => refreshMutation.mutate() },
    ]);
  };

  const busy = refreshMutation.isPending || printMutation.isPending || !pendingLoaded;
  const refreshAllowed = canRefreshCashierBarcode(pendingConfirmation, pendingLoaded);
  const barcodeValue = barcodeQuery.data?.barcode?.trim() ?? "";
  // 偏好为条码但值不是合法 EAN13 时，屏幕回退到二维码（此时打印也会因校验失败而中止）。
  const showLinearBarcode = symbology === "barcode" && isValidEan13(barcodeValue);
  const detail = (
    <Surface style={styles.card} elevation={0}>
      <View style={styles.header}>
        <View ref={modalHeadingRef} accessible accessibilityRole="header" accessibilityLabel={t("cashierBarcode.title")} style={styles.titleBlock}>
          <Text variant="titleMedium">{t("cashierBarcode.title")}</Text>
          <Text variant="bodySmall" style={styles.muted}>{employeeName}</Text>
        </View>
        {barcodeQuery.data?.exists ? (
          <Text variant="labelSmall" style={styles.muted}>
            {t("cashierBarcode.printCount", { count: barcodeQuery.data.printCount })}
          </Text>
        ) : null}
      </View>

      {barcodeQuery.isLoading ? (
        <View style={styles.centered}><ActivityIndicator /><Text>{t("cashierBarcode.loading")}</Text></View>
      ) : barcodeQuery.isError ? (
        <View style={styles.centered}>
          <HelperText type="error" visible>{t("cashierBarcode.loadFailed")}</HelperText>
          <Button mode="outlined" icon="refresh" onPress={() => void barcodeQuery.refetch()}>{t("common:actions.retry")}</Button>
        </View>
      ) : barcodeQuery.data?.exists && barcodeValue ? (
        <View style={styles.qrPreview}>
          <SegmentedButtons
            density="small"
            value={symbology}
            onValueChange={(next) => void setSymbology(next as PersonalCodeSymbology)}
            buttons={[
              { value: "qr", label: t("cashierBarcode.symbology.qr"), disabled: busy },
              { value: "barcode", label: t("cashierBarcode.symbology.barcode"), disabled: busy },
            ]}
            style={styles.symbologySwitch}
          />
          <View
            accessible
            accessibilityRole="image"
            accessibilityLabel={`${t("cashierBarcode.title")}: ${barcodeValue}`}
            style={showLinearBarcode ? styles.barcodeCanvas : styles.qrCanvas}
          >
            {showLinearBarcode ? (
              <Ean13Barcode value={barcodeValue} width={260} />
            ) : (
              <QRCode
                value={barcodeValue}
                size={168}
                backgroundColor="#FFFFFF"
                color="#111111"
              />
            )}
          </View>
          <Text accessible={false} selectable variant="labelSmall" style={styles.qrValue}>
            {barcodeValue}
          </Text>
        </View>
      ) : (
        <Text variant="bodyMedium" style={styles.muted}>{t("cashierBarcode.empty")}</Text>
      )}

      <View style={styles.actions}>
        <Button mode="outlined" icon="refresh" onPress={confirmRefresh} loading={refreshMutation.isPending} disabled={busy || !refreshAllowed} compact>
          {t("cashierBarcode.refresh")}
        </Button>
        <Button
          mode="contained"
          icon="printer"
          onPress={handlePrintPress}
          loading={printMutation.isPending}
          disabled={busy || !barcodeQuery.data?.exists}
          compact
        >
          {pendingConfirmation?.phase === "printing"
            ? t("cashierBarcode.resolvePrintStatus")
            : pendingConfirmation
              ? t("cashierBarcode.retryConfirmation")
              : t("cashierBarcode.print")}
        </Button>
      </View>
      <HelperText type="error" visible={refreshMutation.isError}>{t("cashierBarcode.refreshFailed")}</HelperText>
    </Surface>
  );
  if (!compact) return detail;
  const restoreTriggerFocus = () => {
    InteractionManager.runAfterInteractions(() => {
      const handle = findNodeHandle(compactTriggerRef.current);
      if (handle) AccessibilityInfo.setAccessibilityFocus(handle);
    });
  };
  const dismiss = () => {
    setExpanded(false);
    // iOS 等原生弹窗退出后恢复焦点，避免背景仍被 modal 屏蔽。
    if (Platform.OS !== "ios") restoreTriggerFocus();
  };
  return (
    <>
      {variant === "tile" ? (
        <Pressable
          ref={compactTriggerRef}
          testID="settings-personal-code"
          accessibilityRole="button"
          accessibilityLabel={`${t("cashierBarcode.title")}${barcodeValue ? `: ${barcodeValue}` : ""}. ${expandLabel || ""}`.trim()}
          onPress={() => setExpanded(true)}
          style={styles.tile}
        >
          {barcodeQuery.isLoading ? (
            <ActivityIndicator />
          ) : barcodeValue && !barcodeQuery.isError ? (
            showLinearBarcode ? (
              // 磁贴只是入口缩略图：条码模式下按 104px 宽显示并带编号，扫码请进弹层。
              <View style={styles.tileBarcode}>
                <Ean13Barcode value={barcodeValue} width={104} />
                <Text variant="labelSmall" style={styles.tileBarcodeValue}>{barcodeValue}</Text>
              </View>
            ) : (
              <QRCode value={barcodeValue} size={104} backgroundColor="#FFFFFF" color="#111111" />
            )
          ) : (
            // 加载失败 / 还没生成：整块仍可点，进弹层里重试或生成。
            <Text variant="labelSmall" style={styles.tileMessage}>
              {barcodeQuery.isError ? t("cashierBarcode.loadFailed") : t("cashierBarcode.empty")}
            </Text>
          )}
          <View style={styles.tileZoom} pointerEvents="none">
            <Icon source="arrow-expand" size={15} color="#FFFFFF" />
          </View>
        </Pressable>
      ) : (
        <Pressable
          ref={compactTriggerRef}
          testID="settings-personal-code"
          accessibilityRole="button"
          accessibilityLabel={expandLabel || t("cashierBarcode.title")}
          onPress={() => setExpanded(true)}
          style={styles.compactCard}
        >
          <View style={styles.compactHeading}>
            <Text variant="titleSmall">{t("cashierBarcode.title")}</Text>
            {barcodeQuery.isError ? <Text style={styles.muted}>{t("cashierBarcode.loadFailed")}</Text> : null}
            {!barcodeQuery.isLoading && !barcodeQuery.isError && !barcodeValue ? <Text style={styles.muted}>{t("cashierBarcode.empty")}</Text> : null}
          </View>
          <View style={styles.compactQr}>
            {barcodeQuery.isLoading ? <ActivityIndicator /> : barcodeValue && !barcodeQuery.isError ? (
              <>
                {showLinearBarcode ? (
                  <Ean13Barcode value={barcodeValue} width={160} />
                ) : (
                  <QRCode value={barcodeValue} size={104} backgroundColor="#FFFFFF" color="#111111" />
                )}
                <Text variant="labelSmall" style={styles.compactValue}>{barcodeValue}</Text>
              </>
            ) : null}
          </View>
          <Text variant="bodySmall" style={styles.compactHint}>{expandLabel}</Text>
        </Pressable>
      )}
      <Modal visible={expanded} transparent animationType="fade" onRequestClose={dismiss}
        onDismiss={restoreTriggerFocus}
        onShow={() => { InteractionManager.runAfterInteractions(() => { const handle = findNodeHandle(modalHeadingRef.current); if (handle) AccessibilityInfo.setAccessibilityFocus(handle); }); }}>
        <View style={styles.modalBackdrop} accessibilityViewIsModal>
          <View style={styles.modalSheet}>
            <ScrollView contentContainerStyle={styles.modalContent}>{detail}</ScrollView>
            <Button onPress={dismiss}>{t("common:actions.close")}</Button>
          </View>
        </View>
      </Modal>
    </>
  );
}

const styles = StyleSheet.create({
  card: { padding: 18, borderRadius: 12, gap: 12, backgroundColor: "#FFFFFF" },
  compactCard: { alignItems: "center", gap: 4, padding: 14, borderTopWidth: 1, borderTopColor: "#EEF1F5" },
  compactHeading: { alignSelf: "stretch", gap: 4 },
  compactHint: { color: "#667085", textAlign: "center", fontSize: 11 },
  compactValue: { color: "#667085", fontSize: 10, marginTop: 8, fontVariant: ["tabular-nums"] },
  tile: {
    width: 120,
    height: 120,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
    padding: 8,
  },
  tileMessage: { color: "#667085", textAlign: "center" },
  tileBarcode: { alignItems: "center", gap: 4 },
  tileBarcodeValue: { color: "#667085", fontSize: 9, fontVariant: ["tabular-nums"] },
  tileZoom: {
    position: "absolute",
    right: -7,
    bottom: -7,
    width: 28,
    height: 28,
    borderRadius: 14,
    backgroundColor: "#1677FF",
    borderWidth: 2,
    borderColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
  },
  compactQr: { backgroundColor: "#FFFFFF", padding: 12, minWidth: 128, minHeight: 128, alignItems: "center", justifyContent: "center" },
  modalBackdrop: { flex: 1, backgroundColor: "rgba(15,23,42,0.42)", justifyContent: "center", padding: 20 },
  modalSheet: { maxHeight: "88%", borderRadius: 16, backgroundColor: "#FFFFFF", overflow: "hidden", paddingBottom: 12 },
  modalContent: { flexGrow: 1 },
  header: { flexDirection: "row", alignItems: "flex-start", justifyContent: "space-between", gap: 12 },
  titleBlock: { flex: 1, gap: 3 },
  muted: { color: "#667085" },
  centered: { alignItems: "center", justifyContent: "center", gap: 8, minHeight: 72 },
  qrPreview: { alignItems: "center", gap: 6 },
  qrCanvas: { backgroundColor: "#FFFFFF", padding: 32 },
  // 与二维码画布等高（168 + 上下各 32），切换时弹窗高度不跳动。
  barcodeCanvas: { backgroundColor: "#FFFFFF", paddingVertical: 32, minHeight: 232, justifyContent: "center" },
  symbologySwitch: { alignSelf: "stretch" },
  qrValue: {
    color: "#667085",
    fontVariant: ["tabular-nums"],
    letterSpacing: 0.4,
    textAlign: "center",
  },
  actions: { flexDirection: "row", flexWrap: "wrap", justifyContent: "flex-end", gap: 8 },
});
