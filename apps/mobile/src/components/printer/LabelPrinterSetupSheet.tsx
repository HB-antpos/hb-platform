import { useEffect, useMemo, useState } from "react";
import { Alert, Platform, StyleSheet, View } from "react-native";
import { Button, HelperText, Switch, Text } from "react-native-paper";

import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { PrinterDeviceDetails } from "@/components/printer/PrinterDeviceDetails";
import { PrinterTransportFilterControls } from "@/components/printer/PrinterTransportFilterControls";
import { showSystemPairingRequiredAlert } from "@/components/printer/system-pairing-alert";
import {
  clearSavedPrinter,
  connectSavedPrinter,
  disconnectCurrentPrinter,
  scanPrinterDevices,
  selectPrinter,
  testPrinterConnection,
} from "@/modules/printer/api";
import {
  DEFAULT_PRINTER_TRANSPORT_FILTERS,
  filterPrinterDevices,
  isUnsupportedPrinterTransport,
  requiresSystemPairing,
} from "@/modules/printer/device-list";
import { isBlePrintingSupported } from "@/modules/printer/native";
import { usePrinterStore, type PrinterConnectionState } from "@/modules/printer/state";
import type { PrinterDevice } from "@/modules/printer/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface LabelPrinterSetupSheetProps {
  visible: boolean;
  onDismiss: () => void;
}

function resolveStatusKey(status: PrinterConnectionState, paused: boolean) {
  if (paused || status === "paused") return "printer.statusPaused";
  if (status === "connected") return "printer.statusConnected";
  if (status === "connecting") return "printer.statusConnecting";
  if (status === "reconnecting") return "printer.statusReconnecting";
  return "printer.statusDisconnected";
}

/**
 * 蓝牙标签打印机的就地设置弹层：扫描、连接、测试、清除。
 * 与「设置 → 打印机」共用 printer/api 与已保存的打印机，业务页面无需跳转离开当前上下文。
 * 文案复用 settings 命名空间，两处保持一致。
 */
export function LabelPrinterSetupSheet({ visible, onDismiss }: LabelPrinterSetupSheetProps) {
  const { t, language } = useAppTranslation(["settings", "common"]);
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const status = usePrinterStore((state) => state.status);
  const autoReconnectPaused = usePrinterStore((state) => state.autoReconnectPaused);
  const [devices, setDevices] = useState<PrinterDevice[]>([]);
  const [scanCompleted, setScanCompleted] = useState(false);
  const [filterXPOnly, setFilterXPOnly] = useState(true);
  const [transportFilters, setTransportFilters] = useState({
    ...DEFAULT_PRINTER_TRANSPORT_FILTERS,
  });
  const [busy, setBusy] = useState(false);

  const isConnected = status === "connected";
  const isConnecting = status === "connecting" || status === "reconnecting";

  useEffect(() => {
    if (visible) {
      // 筛选只服务本次选择；重新打开时回到最安全的经典蓝牙默认视图。
      setTransportFilters({ ...DEFAULT_PRINTER_TRANSPORT_FILTERS });
    }
  }, [visible]);

  const hasSelectedTransport =
    Platform.OS !== "android" || transportFilters.showClassic || transportFilters.showBle;
  const visibleDevices = useMemo(
    () =>
      filterPrinterDevices(devices, {
        ...transportFilters,
        xpOnly: filterXPOnly,
        platform: Platform.OS,
      }),
    [devices, filterXPOnly, transportFilters]
  );

  const getErrorMessage = (error: unknown) =>
    resolveLocalizedErrorMessage(error, { language, t, fallbackKey: "dialogs.refreshFailedMessage" });

  const getPrinterErrorMessage = (error: unknown) =>
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey: "dialogs.printerConnectFailedMessage",
      allowRawMessageInChinese: false,
    });

  // 所有蓝牙操作串行执行：busy 期间禁用全部按钮。
  const run = async (
    action: () => Promise<unknown>,
    failedTitleKey: string,
    resolveError: (error: unknown) => string = getErrorMessage
  ) => {
    setBusy(true);
    try {
      await action();
    } catch (error) {
      Alert.alert(t(failedTitleKey), resolveError(error));
    } finally {
      setBusy(false);
    }
  };

  const handleScan = () =>
    run(async () => {
      setDevices(await scanPrinterDevices());
      setScanCompleted(true);
    }, "dialogs.printerScanFailedTitle");

  const connectPrinterDevice = (device: PrinterDevice) =>
    run(async () => {
      await selectPrinter(device);
      Alert.alert(
        t("dialogs.printerConnectedTitle"),
        t("dialogs.printerConnectedMessage", { printer: device.name || device.address })
      );
    }, "dialogs.printerConnectFailedTitle", getPrinterErrorMessage);

  const handleSelect = (device: PrinterDevice) => {
    if (isUnsupportedPrinterTransport(device, Platform.OS, isBlePrintingSupported())) {
      return;
    }

    // 经典蓝牙须先在系统蓝牙设置中配对；BLE 与已配对设备直接连接。
    if (requiresSystemPairing(device, Platform.OS)) {
      showSystemPairingRequiredAlert(t, device);
      return;
    }

    void connectPrinterDevice(device);
  };

  const handleConnectSaved = () =>
    run(() => connectSavedPrinter(), "dialogs.printerConnectFailedTitle", getPrinterErrorMessage);
  const handleDisconnect = () =>
    run(() => disconnectCurrentPrinter({ pauseAutoReconnect: true }), "dialogs.printerDisconnectFailedTitle");
  const handleTest = () =>
    run(async () => {
      await testPrinterConnection();
      Alert.alert(t("dialogs.printerTestSuccessTitle"), t("dialogs.printerTestSuccessMessage"));
    }, "dialogs.printerTestFailedTitle");
  const handleClear = () =>
    run(async () => {
      await clearSavedPrinter();
      Alert.alert(t("dialogs.printerClearedTitle"), t("dialogs.printerClearedMessage"));
    }, "dialogs.printerDisconnectFailedTitle");

  return (
    <BusinessSheet
      visible={visible}
      title={t("printer.title")}
      subtitle={t("printer.description")}
      onDismiss={onDismiss}
      dismissable={!busy}
    >
      <View style={styles.content}>
        <View style={styles.headerRow}>
          <Text variant="bodyMedium" style={styles.flex} numberOfLines={1}>
            {savedPrinter
              ? t("printer.selected", { printer: savedPrinter.name || savedPrinter.address })
              : t("printer.notSelected")}
          </Text>
          <View style={[styles.statusPill, isConnected ? styles.statusPillOk : null]}>
            <Text variant="labelSmall" style={isConnected ? styles.statusTextOk : styles.statusText}>
              {t(resolveStatusKey(status, autoReconnectPaused))}
            </Text>
          </View>
        </View>

        <View style={styles.actions}>
          <Button
            mode="contained"
            icon="magnify"
            onPress={handleScan}
            loading={busy && !isConnecting}
            disabled={busy || !hasSelectedTransport}
            style={styles.flex}
          >
            {busy && !isConnecting ? t("printer.scanning") : t("printer.scan")}
          </Button>
          {savedPrinter ? (
            isConnected ? (
              <Button mode="outlined" icon="link-off" onPress={handleDisconnect} disabled={busy} style={styles.flex}>
                {t("printer.disconnect")}
              </Button>
            ) : (
              <Button
                mode="outlined"
                icon="bluetooth-connect"
                onPress={handleConnectSaved}
                loading={busy && isConnecting}
                disabled={busy}
                style={styles.flex}
              >
                {busy && isConnecting ? t("printer.connecting") : t("printer.connect")}
              </Button>
            )
          ) : null}
        </View>

        <View style={styles.filterRow}>
          <Text variant="bodyMedium">{t("printer.filterXPOnly")}</Text>
          <Switch value={filterXPOnly} onValueChange={setFilterXPOnly} disabled={busy} />
        </View>

        <PrinterTransportFilterControls
          value={transportFilters}
          onChange={setTransportFilters}
          disabled={busy}
        />

        {scanCompleted && hasSelectedTransport ? (
          visibleDevices.length ? (
            <View>
              <Text variant="labelMedium" style={styles.listLabel}>
                {t("printer.available")}
              </Text>
              {visibleDevices.map((device) => {
                const selected = savedPrinter?.address === device.address;
                const unsupported = isUnsupportedPrinterTransport(device, Platform.OS, isBlePrintingSupported());
                return (
                  <View key={device.address} style={styles.deviceRow}>
                    <PrinterDeviceDetails device={device} />
                    <Button
                      compact
                      mode={selected ? "contained-tonal" : "outlined"}
                      onPress={() => void handleSelect(device)}
                      disabled={busy || unsupported}
                    >
                      {t("printer.connect")}
                    </Button>
                  </View>
                );
              })}
            </View>
          ) : (
            <HelperText type="info" visible>
              {Platform.OS === "android"
                ? devices.length
                  ? t("printer.emptyTransportFiltered")
                  : t("printer.empty")
                : devices.length && filterXPOnly
                  ? t("printer.emptyFiltered")
                  : t("printer.empty")}
            </HelperText>
          )
        ) : null}

        <View style={styles.footerActions}>
          <Button
            compact
            mode="outlined"
            icon="printer-check"
            onPress={handleTest}
            disabled={busy || !savedPrinter || !isConnected}
          >
            {t("printer.test")}
          </Button>
          <Button
            compact
            mode="text"
            icon="delete-outline"
            textColor={HB_COLORS.danger}
            onPress={handleClear}
            disabled={busy || !savedPrinter}
          >
            {t("printer.clear")}
          </Button>
        </View>
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  content: {
    gap: HB_SPACING.sm,
  },
  flex: {
    flex: 1,
  },
  headerRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
  statusPill: {
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 2,
    borderRadius: 999,
    backgroundColor: "#F2F4F7",
  },
  statusPillOk: {
    backgroundColor: "#ECFDF3",
  },
  statusText: {
    color: HB_COLORS.textSecondary,
  },
  statusTextOk: {
    color: HB_COLORS.success,
  },
  actions: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  filterRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#F2F4F7",
  },
  listLabel: {
    color: HB_COLORS.textSecondary,
    marginBottom: HB_SPACING.xxs,
  },
  deviceRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingVertical: HB_SPACING.xs,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  footerActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
});
