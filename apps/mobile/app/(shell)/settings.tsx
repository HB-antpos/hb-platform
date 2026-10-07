import { CashierBarcodeCard } from "@/modules/employee-profile/CashierBarcodeCard";
import { resolveEmployeeProfileIdentity } from "@/modules/employee-profile/cache-keys";
import { useEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from "react";
import {
  AccessibilityInfo,
  Alert,
  findNodeHandle,
  InteractionManager,
  Modal as NativeModal,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  View,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { useRouter } from "expo-router";
import {
  Button,
  HelperText,
  Icon,
  IconButton,
  SegmentedButtons,
  Surface,
  Switch,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { PrinterDeviceDetails } from "@/components/printer/PrinterDeviceDetails";
import { PrinterTransportFilterControls } from "@/components/printer/PrinterTransportFilterControls";
import { showSystemPairingRequiredAlert } from "@/components/printer/system-pairing-alert";
import {
  clearSavedReceiptPrinter,
  clearSavedPrinter,
  connectSavedPrinter,
  disconnectCurrentPrinter,
  hydrateSavedReceiptPrinter,
  scanPrinterDevices,
  selectReceiptPrinter,
  selectPrinter,
  syncPrinterStatus,
  testReceiptPrinterConnection,
  testPrinterConnection,
} from "@/modules/printer/api";
import {
  DEFAULT_PRINTER_TRANSPORT_FILTERS,
  filterPrinterDevices,
  isUnsupportedPrinterTransport,
  orderPrinterDevices,
  requiresSystemPairing,
} from "@/modules/printer/device-list";
import { isBlePrintingSupported } from "@/modules/printer/native";
import { usePrinterStore, useReceiptPrinterStore, type PrinterConnectionState } from "@/modules/printer/state";
import type { PrinterDevice } from "@/modules/printer/types";
import { i18n, setAppLanguage } from "@/shared/i18n/i18n";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { AppLanguage } from "@/shared/i18n/types";
import { useAuthStore } from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import { resolveSettingsAuthMode, shouldShowProfileAction } from "@/modules/device/settings-mode";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { isRequiredLocationError } from "@/modules/attendance/required-location";
import {
  buildAppUpdateInfoRows,
  formatAppPackageVersion,
  resolveAppUpdateSourceKey,
} from "@/modules/updates/app-update-info";
import { getCurrentAppUpdateInfo } from "@/modules/updates/app-update-runtime";
import { useMobileOtaManualCheck } from "@/modules/updates/MobileOtaUpdateBoundary";
import {
  API_HOST_PRESETS,
  getCurrentApiHost,
  getStoredApiHost,
  normalizeApiHost,
  setStoredApiHost,
} from "@/shared/api/config";
import { DeviceActivationDialog } from "@/modules/device-activation/DeviceActivationDialog";
import type { MobileDeviceActivationMode } from "@/modules/device-activation/types";
import {
  describeOfflineCatalogSummary,
  OfflineCatalogManagementPanel,
} from "@/components/product-maintenance/OfflineCatalogManagementPanel";
import {
  hasStoredDeviceSession,
  isOfflineProductQueryEligible,
} from "@/modules/product-maintenance/offline-eligibility";
import { useOfflineCatalogStore } from "@/modules/product-maintenance/offline-catalog/offline-catalog-store";
import { useStores } from "@/modules/shop/use-stores";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

function resolveDeviceStatusText(
  status: number | undefined,
  description: string | null | undefined,
  t: (key: string, options?: Record<string, unknown>) => string,
  language: string
) {
  if (description && language === "zh") {
    return description;
  }

  switch (status) {
    case -1:
      return t("deviceStatus.pending");
    case 0:
      return t("deviceStatus.disabled");
    case 1:
      return t("deviceStatus.enabled");
    case 2:
      return t("deviceStatus.locked");
    case 3:
      return t("deviceStatus.unregistered");
    default:
      return t("deviceStatus.unregistered");
  }
}

interface CompactSectionProps {
  title: string;
  description?: string;
  testID?: string;
  children: ReactNode;
}

function CompactSection({ title, description, testID, children }: CompactSectionProps) {
  return (
    <View style={styles.section} testID={testID}>
      <View style={styles.sectionHeader}>
        <Text variant="labelLarge" style={styles.sectionTitle}>
          {title}
        </Text>
        {description ? (
          <Text variant="bodySmall" style={styles.meta}>
            {description}
          </Text>
        ) : null}
      </View>
      <Surface style={styles.card} elevation={0}>
        {children}
      </Surface>
    </View>
  );
}

type StatusTone = "success" | "warning" | "danger" | "neutral";

interface StatusPillProps {
  label: string;
  tone?: StatusTone;
}

function StatusPill({ label, tone = "neutral" }: StatusPillProps) {
  const toneStyle = {
    success: styles.statusPillSuccess,
    warning: styles.statusPillWarning,
    danger: styles.statusPillDanger,
    neutral: styles.statusPillNeutral,
  }[tone];
  const textToneStyle = {
    success: styles.statusPillTextSuccess,
    warning: styles.statusPillTextWarning,
    danger: styles.statusPillTextDanger,
    neutral: styles.statusPillTextNeutral,
  }[tone];
  const dotToneStyle = {
    success: styles.statusPillDotSuccess,
    warning: styles.statusPillDotWarning,
    danger: styles.statusPillDotDanger,
    neutral: styles.statusPillDotNeutral,
  }[tone];

  // 不限行数：英文状态（如 "Status: Auto reconnecting"）宁可换行也不能被省略号截断。
  return (
    <View style={[styles.statusPill, toneStyle]}>
      <View style={[styles.statusPillDot, dotToneStyle]} />
      <Text variant="labelSmall" style={[styles.statusPillText, textToneStyle]}>
        {label}
      </Text>
    </View>
  );
}

interface AccessibleSettingsModalProps {
  visible: boolean;
  title: string;
  description?: string;
  dismissLabel: string;
  testID: string;
  onDismiss: () => void;
  children: ReactNode;
}

function AccessibleSettingsModal({
  visible,
  title,
  description,
  dismissLabel,
  testID,
  onDismiss,
  children,
}: AccessibleSettingsModalProps) {
  const headingRef = useRef<View>(null);

  const focusHeading = () => {
    InteractionManager.runAfterInteractions(() => {
      const headingHandle = findNodeHandle(headingRef.current);
      if (headingHandle) {
        AccessibilityInfo.setAccessibilityFocus(headingHandle);
      }
    });
  };

  return (
    <NativeModal
      visible={visible}
      transparent
      animationType="fade"
      statusBarTranslucent
      onShow={focusHeading}
      onRequestClose={onDismiss}
    >
      <View style={styles.modalBackdrop} accessibilityViewIsModal>
        <Pressable
          style={StyleSheet.absoluteFill}
          onPress={onDismiss}
          accessibilityRole="button"
          accessibilityLabel={dismissLabel}
        />
        <View testID={testID} style={styles.sheetModal}>
          <View style={styles.sheetHeader}>
            <View
              ref={headingRef}
              accessible
              accessibilityRole="header"
              accessibilityLabel={description ? `${title}. ${description}` : title}
              style={styles.compactRowText}
            >
              <Text variant="titleLarge" style={styles.sheetTitle} accessible={false}>
                {title}
              </Text>
              {description ? (
                <Text variant="bodySmall" style={styles.meta} accessible={false}>
                  {description}
                </Text>
              ) : null}
            </View>
            <IconButton
              icon="close"
              onPress={onDismiss}
              accessibilityLabel={dismissLabel}
            />
          </View>
          {children}
        </View>
      </View>
    </NativeModal>
  );
}

interface CompactRowProps {
  label: string;
  value?: string;
  meta?: string;
  icon?: string;
  status?: string;
  statusTone?: StatusTone;
  action?: ReactNode;
  onPress?: () => void;
  accessibilityLabel?: string;
  disabled?: boolean;
  testID?: string;
  actionRef?: RefObject<View | null>;
}

function CompactRow({
  label,
  value,
  meta,
  icon,
  status,
  statusTone,
  action,
  onPress,
  accessibilityLabel,
  disabled,
  testID,
  actionRef,
}: CompactRowProps) {
  const rowContent = (
    <View style={styles.compactRow} testID={onPress ? undefined : testID}>
      {icon ? (
        <View style={styles.rowIconBox}>
          <Icon source={icon} size={21} color={HB_COLORS.action} />
        </View>
      ) : null}
      <View style={styles.compactRowBody}>
        {/* 状态标签跟在标题后、放不下整体换行；右侧只留操作与箭头，文字列不再被挤到截断。 */}
        <View style={styles.compactRowTitleLine}>
          <Text variant="bodyMedium" style={styles.compactRowLabel}>
            {label}
          </Text>
          {status ? <StatusPill label={status} tone={statusTone} /> : null}
        </View>
        {value ? (
          <Text variant="bodySmall" style={styles.compactRowValue}>
            {value}
          </Text>
        ) : null}
        {meta ? (
          <Text variant="bodySmall" style={styles.meta}>
            {meta}
          </Text>
        ) : null}
      </View>
      {action || onPress ? (
        <View style={styles.compactRowEnd}>
          {action ? <View style={styles.compactRowAction}>{action}</View> : null}
          {onPress ? (
            <View style={styles.rowChevron} accessible={false}>
              <Icon source="chevron-right" size={22} color={HB_COLORS.textSecondary} />
            </View>
          ) : null}
        </View>
      ) : null}
    </View>
  );

  if (!onPress) {
    return rowContent;
  }

  return (
    <Pressable
      ref={actionRef}
      onPress={onPress}
      disabled={disabled}
      testID={testID}
      accessibilityRole="button"
      accessibilityLabel={[
        accessibilityLabel || label,
        value,
        meta,
        status,
      ].filter(Boolean).join(". ")}
      accessibilityState={{ disabled: Boolean(disabled) }}
      style={({ pressed }) => pressed && styles.compactRowPressed}
    >
      {rowContent}
    </Pressable>
  );
}

interface UpdateInfoTileProps {
  label: string;
  value: string;
}

// 标签在上、数值在下且不限行数：英文标签更长时数值照样完整显示（原先渠道会被按钮挤掉）。
function UpdateInfoTile({ label, value }: UpdateInfoTileProps) {
  return (
    <View style={styles.updateTile} accessible accessibilityLabel={[label, value].join(". ")}>
      <Text variant="labelMedium" style={styles.updateTileLabel}>
        {label}
      </Text>
      <Text variant="bodyMedium" style={styles.updateTileValue}>
        {value}
      </Text>
    </View>
  );
}

interface InfoRowProps {
  label: string;
  value: string;
  monospace?: boolean;
  style?: StyleProp<ViewStyle>;
}

// 诊断用的两列信息行：标签最多占 45%，数值右对齐、可换行且可长按复制（便于排障时复制更新 ID）。
function InfoRow({ label, value, monospace, style }: InfoRowProps) {
  return (
    <View style={[styles.infoRow, style]}>
      <Text variant="bodySmall" style={styles.infoRowLabel}>
        {label}
      </Text>
      <Text
        variant="bodySmall"
        selectable
        style={[styles.infoRowValue, monospace && styles.infoRowValueMono]}
      >
        {value}
      </Text>
    </View>
  );
}

interface PrinterDeviceListProps {
  devices: PrinterDevice[];
  selectedAddress?: string | null;
  actionLabel: string;
  disabled: boolean;
  onSelect: (printer: PrinterDevice) => void;
}

function PrinterDeviceList({
  devices,
  selectedAddress,
  actionLabel,
  disabled,
  onSelect,
}: PrinterDeviceListProps) {
  return (
    <View style={styles.printerList}>
      {devices.map((printer) => {
        const selected = selectedAddress === printer.address;
        const unsupported = isUnsupportedPrinterTransport(printer, Platform.OS, isBlePrintingSupported());
        return (
          <View key={printer.address} style={styles.printerRow}>
            <PrinterDeviceDetails device={printer} />
            <Button
              compact
              mode={selected ? "contained-tonal" : "outlined"}
              onPress={() => onSelect(printer)}
              disabled={disabled || unsupported}
            >
              {actionLabel}
            </Button>
          </View>
        );
      })}
    </View>
  );
}

export default function Settings() {
  const router = useRouter();
  const { t, language } = useAppTranslation(["settings", "common", "productQuery"]);
  const checkMobileOtaUpdate = useMobileOtaManualCheck();
  const user = useAuthStore((state) => state.user);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const logout = useAuthStore((state) => state.logout);
  const loginDeviceAccount = useAuthStore((state) => state.loginDeviceAccount);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const deviceSession = useDeviceStore((state) => state.session);
  const accountBinding = useDeviceStore((state) => state.accountBinding);
  const refreshAccountBinding = useDeviceStore((state) => state.refreshAccountBinding);
  const validateDevice = useDeviceStore((state) => state.validate);
  const unbindDevice = useDeviceStore((state) => state.unbind);
  const unbindAccountBinding = useDeviceStore((state) => state.unbindAccountBinding);
  const deviceLoading = useDeviceStore((state) => state.isLoading);
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const printerStatus = usePrinterStore((state) => state.status);
  const autoReconnectPaused = usePrinterStore((state) => state.autoReconnectPaused);
  const savedReceiptPrinter = useReceiptPrinterStore((state) => state.savedPrinter);
  const receiptPrinterStatus = useReceiptPrinterStore((state) => state.status);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [rawPrinters, setRawPrinters] = useState<PrinterDevice[]>([]);
  const [printerBusy, setPrinterBusy] = useState(false);
  const [printerScanCompleted, setPrinterScanCompleted] = useState(false);
  const [filterXPOnly, setFilterXPOnly] = useState(true);
  const [transportFilters, setTransportFilters] = useState({
    ...DEFAULT_PRINTER_TRANSPORT_FILTERS,
  });
  const [receiptRawPrinters, setReceiptRawPrinters] = useState<PrinterDevice[]>([]);
  const [receiptPrinterBusy, setReceiptPrinterBusy] = useState(false);
  const [receiptPrinterScanCompleted, setReceiptPrinterScanCompleted] = useState(false);
  const [updateBusy, setUpdateBusy] = useState(false);
  const [updateInfo, setUpdateInfo] = useState(() => getCurrentAppUpdateInfo());
  const [apiHost, setApiHost] = useState(getCurrentApiHost());
  const [apiHostDraft, setApiHostDraft] = useState(getCurrentApiHost());
  const [apiHostModalVisible, setApiHostModalVisible] = useState(false);
  const [diagnosticsVisible, setDiagnosticsVisible] = useState(false);
  const [deviceSettingsVisible, setDeviceSettingsVisible] = useState(false);
  const [printerSettingsVisible, setPrinterSettingsVisible] = useState(false);
  const [activationVisible, setActivationVisible] = useState(false);
  const [activationMode, setActivationMode] = useState<MobileDeviceActivationMode>("redeem");
  const [offlineDataVisible, setOfflineDataVisible] = useState(false);
  const modalReturnFocusHandleRef = useRef<number | null>(null);
  const diagnosticsHeaderTriggerRef = useRef<View>(null);
  const diagnosticsAboutTriggerRef = useRef<View>(null);
  const deviceTriggerRef = useRef<View>(null);
  const labelPrinterTriggerRef = useRef<View>(null);
  const receiptPrinterTriggerRef = useRef<View>(null);
  const apiHostTriggerRef = useRef<View>(null);
  const offlineDataTriggerRef = useRef<View>(null);

  const rememberModalTrigger = (triggerRef: RefObject<View | null>) => {
    modalReturnFocusHandleRef.current = findNodeHandle(triggerRef.current);
  };

  const restoreModalTriggerFocus = () => {
    const triggerHandle = modalReturnFocusHandleRef.current;
    if (!triggerHandle) {
      return;
    }
    InteractionManager.runAfterInteractions(() => {
      AccessibilityInfo.setAccessibilityFocus(triggerHandle);
    });
  };

  const dismissDeviceSettings = () => {
    setDeviceSettingsVisible(false);
    restoreModalTriggerFocus();
  };

  const dismissPrinterSettings = () => {
    setPrinterSettingsVisible(false);
    restoreModalTriggerFocus();
  };

  const dismissDiagnostics = () => {
    setDiagnosticsVisible(false);
    restoreModalTriggerFocus();
  };

  const dismissApiHostSettings = () => {
    setApiHostModalVisible(false);
    restoreModalTriggerFocus();
  };

  const dismissOfflineDataSettings = () => {
    setOfflineDataVisible(false);
    restoreModalTriggerFocus();
  };

  const settingsAuthMode = resolveSettingsAuthMode({
    hasUser: Boolean(user),
    hasDeviceSession: Boolean(deviceSession),
  });
  const showProfileAction = shouldShowProfileAction(settingsAuthMode);
  const canViewDeviceCard = Boolean(user || deviceSession || accountBinding);
  // 离线商品数据只对设备注册绑定会话开放，与商品查询页的离线资格口径一致。
  const offlineEligible = isOfflineProductQueryEligible({
    sessionKind,
    hasStoredDeviceSession: hasStoredDeviceSession(deviceSession),
  });
  const { selectedStore: offlineSelectedStore, selectedStoreCode: offlineSelectedStoreCode } =
    useStores();
  const offlineActiveMeta = useOfflineCatalogStore((state) =>
    offlineSelectedStoreCode ? (state.activeMeta[offlineSelectedStoreCode] ?? null) : null
  );
  const offlineRefresh = useOfflineCatalogStore((state) => state.refresh);
  const offlineSummary = describeOfflineCatalogSummary({
    refresh: offlineRefresh,
    storeCode: offlineSelectedStoreCode,
    activeMeta: offlineActiveMeta,
    language,
    t,
  });

  useEffect(() => {
    // 设置首页的离线数据行要在不打开面板时也能显示状态，这里先读当前分店的快照摘要。
    if (!offlineEligible || !offlineSelectedStoreCode) {
      return;
    }
    void useOfflineCatalogStore
      .getState()
      .open()
      .then((ready) =>
        ready ? useOfflineCatalogStore.getState().loadActiveMeta(offlineSelectedStoreCode) : null
      );
  }, [offlineEligible, offlineSelectedStoreCode]);

  const deviceStatusText = resolveDeviceStatusText(
    deviceSession?.status,
    deviceSession?.statusDescription,
    t,
    language
  );
  const deviceReady = Boolean(accountBinding) ||
    (deviceSession?.status === 1 && Boolean(deviceSession.storeCode));
  const deviceStoreDisplayName =
    accountBinding?.binding.storeName ||
    deviceSession?.storeName ||
    deviceSession?.storeCode ||
    t("common:na");
  const deviceDisplayName =
    accountBinding?.binding.deviceCode ||
    deviceSession?.systemDeviceNumber ||
    t("overview.notConfigured");
  const updateInfoRows = useMemo(() => buildAppUpdateInfoRows(updateInfo), [updateInfo]);
  const appPackageVersion = useMemo(
    () => formatAppPackageVersion(updateInfo, t("updates.unknown")),
    [t, updateInfo]
  );
  const appUpdateSourceKey = resolveAppUpdateSourceKey(updateInfo);
  // 用 || 而非 ??：开发包与未配置渠道的安装包里 Updates.channel 是空字符串，?? 会让渠道显示为空白。
  const appUpdateChannelLabel = updateInfo.channel || t("updates.noChannel");

  const hasSelectedTransport =
    Platform.OS !== "android" || transportFilters.showClassic || transportFilters.showBle;
  const visiblePrinters = useMemo(
    () =>
      filterPrinterDevices(rawPrinters, {
        ...transportFilters,
        xpOnly: filterXPOnly,
        platform: Platform.OS,
      }),
    [filterXPOnly, rawPrinters, transportFilters]
  );

  const visibleReceiptPrinters = useMemo(
    () => orderPrinterDevices(receiptRawPrinters),
    [receiptRawPrinters]
  );

  useEffect(() => {
    if (printerSettingsVisible) {
      // 筛选不持久化；每次打开详情均优先展示当前 Android 打印通道支持的经典蓝牙。
      setTransportFilters({ ...DEFAULT_PRINTER_TRANSPORT_FILTERS });
    }
  }, [printerSettingsVisible]);

  useEffect(() => {
    let cancelled = false;

    void getStoredApiHost().then((host) => {
      if (cancelled) {
        return;
      }

      setApiHost(host);
      setApiHostDraft(host);
    });

    void syncPrinterStatus().catch((error) => {
      if (cancelled) {
        return;
      }

      const message = resolveLocalizedErrorMessage(error, {
        language,
        t,
        fallbackKey: "dialogs.refreshFailedMessage",
      });
      const store = usePrinterStore.getState();
      store.setLastError(message);
      store.setStatus("error");
    });

    void hydrateSavedReceiptPrinter().catch((error) => {
      if (cancelled) {
        return;
      }

      const message = resolveLocalizedErrorMessage(error, {
        language,
        t,
        fallbackKey: "dialogs.refreshFailedMessage",
      });
      const store = useReceiptPrinterStore.getState();
      store.setLastError(message);
      store.setStatus("error");
    });

    return () => {
      cancelled = true;
    };
  }, []);

  const isPrinterConnected = printerStatus === "connected";
  const isPrinterConnecting = printerStatus === "connecting";
  const isPrinterReconnecting = printerStatus === "reconnecting";
  const isReceiptPrinterTesting =
    receiptPrinterStatus === "connecting" || receiptPrinterStatus === "connected";
  const printerNativeBusy = printerBusy || receiptPrinterBusy;

  const getErrorMessage = (error: unknown, fallbackKey: string) =>
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    });

  const getPrinterErrorMessage = (error: unknown) =>
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey: "dialogs.printerConnectFailedMessage",
      allowRawMessageInChinese: false,
    });

  function resolvePrinterStatusText(
    status: PrinterConnectionState,
    paused: boolean,
    namespace: "printer" | "receiptPrinter",
    tLabel: (key: string, options?: Record<string, unknown>) => string
  ) {
    if (paused || status === "paused") {
      return tLabel(`${namespace}.statusPaused`);
    }
    switch (status) {
      case "connected":
        return tLabel(`${namespace}.statusConnected`);
      case "connecting":
        return tLabel(`${namespace}.statusConnecting`);
      case "reconnecting":
        return tLabel(`${namespace}.statusReconnecting`);
      case "error":
        return tLabel(`${namespace}.statusDisconnected`);
      default:
        return tLabel(`${namespace}.statusDisconnected`);
    }
  }

  const labelPrinterStatusText = resolvePrinterStatusText(
    printerStatus,
    autoReconnectPaused,
    "printer",
    t
  );
  const receiptPrinterStatusText = resolvePrinterStatusText(
    receiptPrinterStatus,
    false,
    "receiptPrinter",
    t
  );
  const deviceStatusTone: StatusTone = accountBinding || deviceReady
    ? "success"
    : deviceSession?.status === 0 || deviceSession?.status === 2
      ? "danger"
      : "warning";
  const labelPrinterStatusTone: StatusTone = isPrinterConnected
    ? "success"
    : printerStatus === "error"
      ? "danger"
      : isPrinterConnecting || isPrinterReconnecting
        ? "warning"
        : "neutral";
  const receiptPrinterStatusTone: StatusTone = receiptPrinterStatus === "connected"
    ? "success"
    : receiptPrinterStatus === "error"
      ? "danger"
      : receiptPrinterStatus === "connecting" || receiptPrinterStatus === "reconnecting"
        ? "warning"
        : "neutral";
  const connectionNeedsAttention =
    (canViewDeviceCard && !deviceReady) ||
    printerStatus === "error" ||
    receiptPrinterStatus === "error";
  const currentApiHostPreset = API_HOST_PRESETS.find(
    (preset) => normalizeApiHost(apiHost) === preset.host
  );
  const apiHostDisplayName = currentApiHostPreset
    ? t(`apiHost.presets.${currentApiHostPreset.key}`)
    : apiHost;

  const handleLogout = () => {
    Alert.alert(t("dialogs.logoutTitle"), t("dialogs.logoutMessage"), [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("dialogs.logoutAction"),
        style: "destructive",
        onPress: async () => {
          setIsSubmitting(true);
          try {
            await logout();
            router.replace("/(auth)/login");
          } finally {
            setIsSubmitting(false);
          }
        },
      },
    ]);
  };

  const handleDeviceUnbind = () => {
    if (isIosReviewSessionActive()) {
      Alert.alert(
        "App Review Demo",
        "Device binding is simulated and does not change this device. / 设备绑定为本地演示，不会修改当前设备。",
      );
      return;
    }
    Alert.alert(
      t("dialogs.unbindDeviceTitle"),
      t("dialogs.unbindDeviceMessage"),
      [
        { text: t("common:actions.cancel"), style: "cancel" },
        {
          text: t("device.unbind"),
          style: "destructive",
          onPress: async () => {
            setIsSubmitting(true);
            try {
              if (accountBinding) {
                await unbindAccountBinding("user-requested");
                if (sessionKind === "deviceAccount") {
                  await logout();
                  router.replace("/(auth)/login");
                }
              } else {
                await unbindDevice();
                router.replace("/(auth)/login");
              }
            } catch (error) {
              Alert.alert(
                t("dialogs.unbindDeviceFailedTitle"),
                getErrorMessage(error, "dialogs.unbindDeviceFailedMessage")
              );
            } finally {
              setIsSubmitting(false);
            }
          },
        },
      ]
    );
  };

  const openDeviceActivation = () => {
    setActivationMode(accountBinding ? "rebind" : "redeem");
    setActivationVisible(true);
  };

  const handleActivationCompleted = async () => {
    try {
      await refreshAccountBinding();
      await loginDeviceAccount();
    } catch {
      Alert.alert(
        t("dialogs.bindingLoginFailedTitle"),
        t("dialogs.bindingLoginFailedMessage"),
      );
      router.replace("/(auth)/login");
    }
  };

  const handleLanguageChange = async (nextLanguage: AppLanguage) => {
    if (isIosReviewSessionActive()) {
      // 审核模式仅切换当前内存语言，不覆盖普通用户的持久化偏好。
      await i18n.changeLanguage(nextLanguage);
      return;
    }
    await setAppLanguage(nextLanguage);
  };

  const openDiagnostics = (triggerRef: RefObject<View | null>) => {
    rememberModalTrigger(triggerRef);
    setDiagnosticsVisible(true);
  };

  const openDeviceSettings = () => {
    rememberModalTrigger(deviceTriggerRef);
    setDeviceSettingsVisible(true);
  };

  const openPrinterSettings = (triggerRef: RefObject<View | null>) => {
    rememberModalTrigger(triggerRef);
    setPrinterSettingsVisible(true);
  };

  const openApiHostSettings = () => {
    rememberModalTrigger(apiHostTriggerRef);
    setApiHostDraft(apiHost);
    setApiHostModalVisible(true);
  };

  const openOfflineDataSettings = () => {
    rememberModalTrigger(offlineDataTriggerRef);
    setOfflineDataVisible(true);
  };

  const handleSaveApiHost = async () => {
    const normalizedHost = normalizeApiHost(apiHostDraft);
    if (!normalizedHost) {
      Alert.alert(t("apiHost.emptyTitle"), t("apiHost.emptyMessage"));
      return;
    }

    try {
      if (isIosReviewSessionActive()) {
        // 审核模式不持久化 API Host，所有业务请求仍由本地 adapter 处理。
        setApiHost(normalizedHost);
        setApiHostDraft(normalizedHost);
        dismissApiHostSettings();
        Alert.alert(
          "App Review Demo",
          "Server settings are temporary in offline demo mode. / 离线演示中的服务器设置仅当前会话有效。",
        );
        return;
      }
      // 保存后无需手动刷新客户端，API 拦截器会在后续请求前同步新的 baseURL。
      const host = await setStoredApiHost(normalizedHost);
      setApiHost(host);
      setApiHostDraft(host);
      dismissApiHostSettings();
      Alert.alert(t("apiHost.savedTitle"), t("apiHost.savedMessage", { host }));
    } catch (error) {
      Alert.alert(
        t("apiHost.saveFailedTitle"),
        getErrorMessage(error, "apiHost.saveFailedMessage")
      );
    }
  };

  const handleCheckUpdates = async () => {
    if (isIosReviewSessionActive()) {
      Alert.alert(
        "App Review Demo",
        "Updates are disabled in offline demo mode. / 离线演示模式不检查更新。",
      );
      return;
    }
    setUpdateBusy(true);
    try {
      // 与启动检查复用同一受控策略，确保唯一发布 channel 上的目标不会被遗漏。
      const result = await checkMobileOtaUpdate();
      if (result.status === "not-available") {
        Alert.alert(t("dialogs.updateNotAvailableTitle"), t("dialogs.updateNotAvailableMessage"));
      } else if (result.status === "disabled") {
        Alert.alert(t("dialogs.updateConfigurationDisabledTitle"), t("dialogs.updateConfigurationDisabledMessage"));
      } else if (result.status === "failed") {
        Alert.alert(t("dialogs.updateCheckFailedTitle"), t("dialogs.updateCheckFailedMessage"));
      }
    } catch (error) {
      Alert.alert(
        t("dialogs.updateCheckFailedTitle"),
        getErrorMessage(error, "dialogs.updateCheckFailedMessage")
      );
    } finally {
      setUpdateInfo(getCurrentAppUpdateInfo());
      setUpdateBusy(false);
    }
  };

  const handleRefreshDevice = async () => {
    setIsSubmitting(true);
    try {
      const isReady = await validateDevice();
      Alert.alert(
        isReady ? t("dialogs.refreshReadyTitle") : t("dialogs.refreshPendingTitle"),
        isReady ? t("dialogs.refreshReadyMessage") : t("dialogs.refreshPendingMessage")
      );
    } catch (error) {
      Alert.alert(
        t("dialogs.refreshFailedTitle"),
        isRequiredLocationError(error)
          ? t("dialogs.locationRequiredMessage")
          : getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleScanPrinters = async () => {
    setPrinterBusy(true);
    try {
      const nextPrinters = await scanPrinterDevices();
      setRawPrinters(nextPrinters);
      setPrinterScanCompleted(true);
    } catch (error) {
      Alert.alert(
        t("dialogs.printerScanFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setPrinterBusy(false);
    }
  };

  const handleScanReceiptPrinters = async () => {
    setReceiptPrinterBusy(true);
    try {
      const nextPrinters = await scanPrinterDevices();
      setReceiptRawPrinters(nextPrinters);
      setReceiptPrinterScanCompleted(true);
    } catch (error) {
      Alert.alert(
        t("dialogs.receiptPrinterScanFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setReceiptPrinterBusy(false);
    }
  };

  const connectPrinterDevice = async (device: PrinterDevice) => {
    setPrinterBusy(true);
    try {
      await selectPrinter(device);
      Alert.alert(
        t("dialogs.printerConnectedTitle"),
        t("dialogs.printerConnectedMessage", { printer: device.name || device.address })
      );
    } catch (error) {
      Alert.alert(
        t("dialogs.printerConnectFailedTitle"),
        getPrinterErrorMessage(error)
      );
    } finally {
      setPrinterBusy(false);
    }
  };

  const handleConnectPrinter = (device: PrinterDevice) => {
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

  const handleTestPrinter = async () => {
    setPrinterBusy(true);
    try {
      await testPrinterConnection();
      Alert.alert(t("dialogs.printerTestSuccessTitle"), t("dialogs.printerTestSuccessMessage"));
    } catch (error) {
      Alert.alert(
        t("dialogs.printerTestFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setPrinterBusy(false);
    }
  };

  const handleClearPrinter = async () => {
    setPrinterBusy(true);
    try {
      await clearSavedPrinter();
      Alert.alert(t("dialogs.printerClearedTitle"), t("dialogs.printerClearedMessage"));
    } catch (error) {
      Alert.alert(
        t("dialogs.printerDisconnectFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setPrinterBusy(false);
    }
  };

  const handleConnectSavedPrinter = async () => {
    setPrinterBusy(true);
    try {
      await connectSavedPrinter();
    } catch (error) {
      Alert.alert(
        t("dialogs.printerConnectFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setPrinterBusy(false);
    }
  };

  const handleDisconnectPrinter = async () => {
    setPrinterBusy(true);
    try {
      await disconnectCurrentPrinter({ pauseAutoReconnect: true });
    } catch (error) {
      Alert.alert(
        t("dialogs.printerDisconnectFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setPrinterBusy(false);
    }
  };

  const handleSaveReceiptPrinter = async (device: PrinterDevice) => {
    setReceiptPrinterBusy(true);
    try {
      await selectReceiptPrinter(device);
      Alert.alert(
        t("dialogs.receiptPrinterSavedTitle"),
        t("dialogs.receiptPrinterSavedMessage", { printer: device.name || device.address })
      );
    } catch (error) {
      Alert.alert(
        t("dialogs.receiptPrinterConnectFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setReceiptPrinterBusy(false);
    }
  };

  const handleConnectReceiptPrinter = (device: PrinterDevice) => {
    if (isUnsupportedPrinterTransport(device, Platform.OS, isBlePrintingSupported())) {
      return;
    }

    // 小票机同样：经典蓝牙先去系统配对，BLE 直接保存。
    if (requiresSystemPairing(device, Platform.OS)) {
      showSystemPairingRequiredAlert(t, device);
      return;
    }

    void handleSaveReceiptPrinter(device);
  };

  const handleTestReceiptPrinter = async () => {
    setReceiptPrinterBusy(true);
    try {
      await testReceiptPrinterConnection();
      Alert.alert(t("dialogs.receiptPrinterTestSuccessTitle"), t("dialogs.receiptPrinterTestSuccessMessage"));
    } catch (error) {
      Alert.alert(
        t("dialogs.receiptPrinterTestFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setReceiptPrinterBusy(false);
    }
  };

  const handleClearReceiptPrinter = async () => {
    setReceiptPrinterBusy(true);
    try {
      await clearSavedReceiptPrinter();
      Alert.alert(t("dialogs.receiptPrinterClearedTitle"), t("dialogs.receiptPrinterClearedMessage"));
    } catch (error) {
      Alert.alert(
        t("dialogs.receiptPrinterDisconnectFailedTitle"),
        getErrorMessage(error, "dialogs.refreshFailedMessage")
      );
    } finally {
      setReceiptPrinterBusy(false);
    }
  };

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container}>
      <ScrollView
        testID="settings-overview"
        contentContainerStyle={styles.content}
        contentInsetAdjustmentBehavior="automatic"
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.header}>
          <Text variant="headlineMedium" style={styles.title}>
            {t("title")}
          </Text>
          <Button
            ref={diagnosticsHeaderTriggerRef}
            compact
            mode="text"
            testID="settings-diagnostics"
            icon="stethoscope"
            onPress={() => openDiagnostics(diagnosticsHeaderTriggerRef)}
            accessibilityLabel={t("overview.diagnostics")}
            contentStyle={styles.headerActionContent}
          >
            {t("overview.diagnostics")}
          </Button>
        </View>

        <CompactSection title={t("groups.account")} testID="settings-group-account">
          <CompactRow
            icon="account-circle-outline"
            label={t("account.title")}
            value={user?.fullName || user?.username || t("common:notLoggedIn")}
            meta={
              user?.username ||
              (showProfileAction ? t("account.guestEmail") : t("account.deviceModeHelper"))
            }
            status={t(showProfileAction ? "overview.accountMode" : "overview.deviceMode")}
            statusTone="neutral"
            onPress={
              showProfileAction
                ? () => {
                    router.push(
                      "/(shell)/employee-profile" as unknown as Parameters<typeof router.push>[0]
                    );
                  }
                : undefined
            }
            accessibilityLabel={t("account.profileButton")}
          />
          {/* 只有个人账号能改自己的密码；设备授权与设备绑定账号没有个人密码可改。 */}
          {isAuthenticated && user && sessionKind === "account" ? (
            <CompactRow
              icon="lock-reset"
              label={t("account.changePassword")}
              meta={t("account.changePasswordHelper")}
              onPress={() => {
                router.push(
                  "/(auth)/change-password?mode=voluntary" as unknown as Parameters<typeof router.push>[0]
                );
              }}
              accessibilityLabel={t("account.changePassword")}
            />
          ) : null}
          {isAuthenticated && user && sessionKind !== "device" ? (
            <CashierBarcodeCard
              key={resolveEmployeeProfileIdentity(user)}
              employeeName={user.fullName || user.username || ""}
              username={user.username || ""}
              userIdentity={resolveEmployeeProfileIdentity(user)}
              compact
              expandLabel={t("account.expandPersonalCode")}
            />
          ) : null}
        </CompactSection>

        <CompactSection
          title={t("groups.devices")}
          testID="settings-group-device-connections"
        >
          <View style={styles.connectionSummary}>
            <View
              style={[
                styles.connectionSummaryIcon,
                connectionNeedsAttention && styles.connectionSummaryIconWarning,
              ]}
            >
              <Icon
                source={connectionNeedsAttention ? "alert-circle-outline" : "check-circle-outline"}
                size={24}
                color={connectionNeedsAttention ? HB_COLORS.warning : HB_COLORS.success}
              />
            </View>
            <View style={styles.connectionSummaryText}>
              <Text variant="titleSmall" style={styles.compactRowLabel}>
                {t(
                  connectionNeedsAttention
                    ? "overview.deviceServicesAttention"
                    : "overview.deviceServicesReady"
                )}
              </Text>
              <Text variant="bodySmall" style={styles.meta}>
                {t("overview.connectionSummary", { store: deviceStoreDisplayName })}
              </Text>
            </View>
          </View>

          <View style={styles.sectionDivider} />
          <CompactRow
            icon="cellphone-cog"
            label={t("overview.currentDevice")}
            value={deviceDisplayName}
            meta={deviceStoreDisplayName}
            status={accountBinding ? t("deviceStatus.bound") : deviceStatusText}
            statusTone={deviceStatusTone}
            actionRef={deviceTriggerRef}
            onPress={openDeviceSettings}
            accessibilityLabel={t("overview.manageDevice")}
          />

          <View style={styles.sectionDivider} />
          <CompactRow
            icon="printer-outline"
            label={t("printer.title")}
            value={savedPrinter?.name || savedPrinter?.address || t("printer.notSelected")}
            meta={savedPrinter ? labelPrinterStatusText : undefined}
            status={t(
              isPrinterConnected
                ? "overview.connected"
                : savedPrinter
                  ? "overview.configured"
                  : "overview.notConfigured"
            )}
            statusTone={labelPrinterStatusTone}
            actionRef={labelPrinterTriggerRef}
            onPress={() => openPrinterSettings(labelPrinterTriggerRef)}
            accessibilityLabel={t("overview.managePrinters")}
          />

          <View style={styles.sectionDivider} />
          <CompactRow
            icon="receipt-text-outline"
            label={t("receiptPrinter.title")}
            value={
              savedReceiptPrinter?.name ||
              savedReceiptPrinter?.address ||
              t("receiptPrinter.notSelected")
            }
            meta={savedReceiptPrinter ? receiptPrinterStatusText : undefined}
            status={t(
              receiptPrinterStatus === "connected"
                ? "overview.connected"
                : savedReceiptPrinter
                  ? "overview.configured"
                  : "overview.notConfigured"
            )}
            statusTone={receiptPrinterStatusTone}
            actionRef={receiptPrinterTriggerRef}
            onPress={() => openPrinterSettings(receiptPrinterTriggerRef)}
            accessibilityLabel={t("overview.managePrinters")}
          />

          {offlineEligible ? (
            <>
              <View style={styles.sectionDivider} />
              <CompactRow
                icon="database-outline"
                label={t("offlineData.title")}
                value={
                  offlineSelectedStore?.storeName ||
                  offlineSelectedStoreCode ||
                  t("offlineData.storeNotSelected")
                }
                meta={offlineSummary.summary}
                status={t(`offlineData.${offlineSummary.statusKey}`)}
                statusTone={offlineSummary.tone}
                actionRef={offlineDataTriggerRef}
                onPress={openOfflineDataSettings}
                accessibilityLabel={t("offlineData.manage")}
                testID="settings-offline-data"
              />
            </>
          ) : null}
        </CompactSection>

        <CompactSection
          title={t("groups.preferences")}
          testID="settings-group-preferences-security"
        >
          <CompactRow
            icon="translate"
            label={t("common:language.title")}
            action={
              <SegmentedButtons
                value={language}
                onValueChange={(value) => void handleLanguageChange(value as AppLanguage)}
                buttons={[
                  {
                    value: "zh",
                    label: t("overview.languageZhShort"),
                    accessibilityLabel: t("common:language.zh"),
                  },
                  {
                    value: "en",
                    label: t("overview.languageEnShort"),
                    accessibilityLabel: t("common:language.en"),
                  },
                ]}
                style={styles.languageSelector}
              />
            }
          />
          <View style={styles.sectionDivider} />
          <CompactRow
            icon="shield-check-outline"
            label={t("privacy.title")}
            value={t("overview.privacySummary")}
            onPress={() => router.push("/privacy")}
            accessibilityLabel={t("privacy.openPolicy")}
          />
          {/* 数据权利说明须完整可读：英文约 190 字符，原先塞在两行副标题里后半句被截掉。 */}
          <View style={styles.inlineNote}>
            <View style={styles.inlineNoteIcon}>
              <Icon source="information-outline" size={16} color={HB_COLORS.textSecondary} />
            </View>
            <Text variant="bodySmall" style={styles.inlineNoteText}>
              {t(
                showProfileAction
                  ? "privacy.employeeDescription"
                  : "privacy.deviceDescription"
              )}
            </Text>
          </View>
        </CompactSection>

        <CompactSection
          title={t("groups.support")}
          testID="settings-group-app-support"
        >
          {/* 检查按钮独占一行：英文 "Check for updates" 约占半行，与文字同行时曾把更新渠道挤到被截断。 */}
          <View style={styles.updatePanel} testID="settings-app-update">
            <View style={styles.updatePanelHeader}>
              <View style={styles.rowIconBox}>
                <Icon source="cloud-download-outline" size={21} color={HB_COLORS.action} />
              </View>
              <View style={[styles.compactRowTitleLine, styles.updatePanelTitleLine]}>
                <Text variant="bodyMedium" style={styles.compactRowLabel}>
                  {t("updates.title")}
                </Text>
                <StatusPill label={t(appUpdateSourceKey)} />
              </View>
            </View>
            <View style={styles.updateTiles}>
              <UpdateInfoTile label={t("updates.version")} value={appPackageVersion} />
              <UpdateInfoTile label={t("updates.channel")} value={appUpdateChannelLabel} />
            </View>
            <Button
              mode="outlined"
              icon="refresh"
              testID="settings-check-updates"
              onPress={handleCheckUpdates}
              loading={updateBusy}
              disabled={updateBusy}
              style={styles.updateCheckButton}
              contentStyle={styles.updateCheckButtonContent}
            >
              {t("updates.check")}
            </Button>
          </View>
          <View style={styles.sectionDivider} />
          <CompactRow
            icon="server-network"
            label={t("apiHost.title")}
            value={apiHostDisplayName}
            meta={apiHost}
            actionRef={apiHostTriggerRef}
            onPress={openApiHostSettings}
            accessibilityLabel={t("apiHost.change")}
          />
          <View style={styles.sectionDivider} />
          <CompactRow
            icon="information-outline"
            label={t("overview.aboutDiagnostics")}
            value={t("overview.aboutDescription")}
            actionRef={diagnosticsAboutTriggerRef}
            onPress={() => openDiagnostics(diagnosticsAboutTriggerRef)}
            accessibilityLabel={t("overview.diagnostics")}
          />
        </CompactSection>

        {user ? (
          <Button
            mode="outlined"
            icon="logout"
            textColor={HB_COLORS.danger}
            onPress={handleLogout}
            loading={isSubmitting}
            disabled={isSubmitting}
            style={styles.logoutButton}
            contentStyle={styles.destructiveButtonContent}
          >
            {t("account.logoutToLogin")}
          </Button>
        ) : null}

        <Text variant="bodySmall" style={styles.buildFooter}>
          {t("overview.buildFooter", {
            version: appPackageVersion,
            channel: appUpdateChannelLabel,
          })}
        </Text>
      </ScrollView>
      <DeviceActivationDialog
        visible={activationVisible}
        mode={activationMode}
        onDismiss={() => setActivationVisible(false)}
        onCompleted={handleActivationCompleted}
      />
      <AccessibleSettingsModal
        visible={deviceSettingsVisible}
        title={t("device.title")}
        description={t("device.description")}
        dismissLabel={t("common:actions.close")}
        testID="settings-device-details"
        onDismiss={dismissDeviceSettings}
      >
            <ScrollView
              style={styles.sheetScroll}
              contentContainerStyle={styles.sheetContent}
              showsVerticalScrollIndicator={false}
            >
              <View style={styles.statusBlock}>
                <CompactRow
                  label={t("device.statusLabel")}
                  status={accountBinding ? t("deviceStatus.bound") : deviceStatusText}
                  statusTone={deviceStatusTone}
                />
                <View style={styles.sectionDivider} />
                <CompactRow label={t("device.storeLabelCompact")} value={deviceStoreDisplayName} />
                <View style={styles.sectionDivider} />
                <CompactRow label={t("device.deviceNumberLabel")} value={deviceDisplayName} />
                {accountBinding ? (
                  <>
                    <View style={styles.sectionDivider} />
                    <CompactRow
                      label={t("device.accountLabel")}
                      value={
                        accountBinding.binding.targetFullName ||
                        accountBinding.binding.targetUsername
                      }
                    />
                    <View style={styles.sectionDivider} />
                    <CompactRow
                      label={t("device.systemLabel")}
                      value={accountBinding.binding.deviceSystem}
                    />
                  </>
                ) : null}
              </View>

              <Button
                mode="contained"
                icon="qrcode-scan"
                onPress={() => {
                  setDeviceSettingsVisible(false);
                  openDeviceActivation();
                }}
                loading={isSubmitting || deviceLoading}
                disabled={isSubmitting || deviceLoading}
                style={styles.primaryButton}
              >
                {accountBinding
                  ? t("device.rebindByScan")
                  : deviceSession
                    ? t("device.upgradeByScan")
                    : t("device.bindByScan")}
              </Button>

              {deviceSession && !accountBinding ? (
                <Button
                  mode="outlined"
                  onPress={handleRefreshDevice}
                  loading={isSubmitting || deviceLoading}
                  disabled={isSubmitting || deviceLoading}
                >
                  {t("device.refreshStatus")}
                </Button>
              ) : null}

              {deviceReady ? (
                <View style={styles.successBox}>
                  <Icon source="check-circle-outline" size={20} color={HB_COLORS.success} />
                  <Text variant="bodySmall" style={styles.successText}>
                    {t("device.ready")}
                  </Text>
                </View>
              ) : null}

              {deviceSession || accountBinding ? (
                <View style={styles.dangerZone}>
                  <Text variant="labelLarge" style={styles.dangerTitle}>
                    {t("overview.dangerZone")}
                  </Text>
                  <Text variant="bodySmall" style={styles.dangerDescription}>
                    {t("dialogs.unbindDeviceMessage")}
                  </Text>
                  <Button
                    mode="outlined"
                    icon="link-off"
                    textColor={HB_COLORS.danger}
                    onPress={handleDeviceUnbind}
                    loading={isSubmitting || deviceLoading}
                    disabled={isSubmitting || deviceLoading}
                    style={styles.deviceDangerButton}
                  >
                    {t("device.unbind")}
                  </Button>
                </View>
              ) : null}
            </ScrollView>
      </AccessibleSettingsModal>

      <AccessibleSettingsModal
        visible={printerSettingsVisible}
        title={t("groups.printers")}
        description={t("overview.printerDetailsDescription")}
        dismissLabel={t("common:actions.close")}
        testID="settings-printer-details"
        onDismiss={dismissPrinterSettings}
      >
            <ScrollView
              style={styles.sheetScroll}
              contentContainerStyle={styles.sheetContent}
              showsVerticalScrollIndicator={false}
            >
              <View style={styles.printerSection}>
                <View style={styles.printerSectionHeader}>
                  <View style={styles.compactRowTitleLine}>
                    <Text variant="titleMedium" style={styles.sheetSubheading}>
                      {t("printer.title")}
                    </Text>
                    <StatusPill label={labelPrinterStatusText} tone={labelPrinterStatusTone} />
                  </View>
                  <Text variant="bodySmall" style={styles.compactRowValue}>
                    {savedPrinter
                      ? t("printer.selected", {
                          printer: savedPrinter.name || savedPrinter.address,
                        })
                      : t("printer.notSelected")}
                  </Text>
                </View>

                <View style={styles.primaryPrinterActions}>
                  <Button
                    compact
                    mode="contained"
                    icon="magnify"
                    onPress={handleScanPrinters}
                    loading={printerBusy && !isPrinterConnecting}
                    disabled={printerNativeBusy || !hasSelectedTransport}
                    style={styles.primaryActionButton}
                  >
                    {printerBusy && !isPrinterConnecting
                      ? t("printer.scanning")
                      : t("printer.scan")}
                  </Button>
                  {savedPrinter ? (
                    isPrinterConnected ? (
                      <Button
                        compact
                        mode="outlined"
                        icon="link-off"
                        onPress={handleDisconnectPrinter}
                        disabled={printerNativeBusy}
                        style={styles.primaryActionButton}
                      >
                        {t("printer.disconnect")}
                      </Button>
                    ) : (
                      <Button
                        compact
                        mode="outlined"
                        icon="bluetooth-connect"
                        onPress={handleConnectSavedPrinter}
                        loading={printerBusy && (isPrinterConnecting || isPrinterReconnecting)}
                        disabled={printerNativeBusy}
                        style={styles.primaryActionButton}
                      >
                        {printerBusy && (isPrinterConnecting || isPrinterReconnecting)
                          ? t("printer.connecting")
                          : t("printer.connect")}
                      </Button>
                    )
                  ) : null}
                </View>

                <View style={styles.filterRow}>
                  <Text variant="bodyMedium">{t("printer.filterXPOnly")}</Text>
                  <Switch
                    value={filterXPOnly}
                    onValueChange={setFilterXPOnly}
                    disabled={printerNativeBusy}
                  />
                </View>

                <PrinterTransportFilterControls
                  value={transportFilters}
                  onChange={setTransportFilters}
                  disabled={printerNativeBusy}
                />

                {printerScanCompleted && hasSelectedTransport ? (
                  visiblePrinters.length ? (
                    <>
                      <Text variant="labelMedium" style={styles.listLabel}>
                        {t("printer.available")}
                      </Text>
                      <PrinterDeviceList
                        devices={visiblePrinters}
                        selectedAddress={savedPrinter?.address}
                        actionLabel={t("printer.connect")}
                        disabled={printerNativeBusy}
                        onSelect={(printer) => void handleConnectPrinter(printer)}
                      />
                    </>
                  ) : (
                    <HelperText type="info" visible>
                      {Platform.OS === "android"
                        ? rawPrinters.length
                          ? t("printer.emptyTransportFiltered")
                          : t("printer.empty")
                        : rawPrinters.length && filterXPOnly
                          ? t("printer.emptyFiltered")
                          : t("printer.empty")}
                    </HelperText>
                  )
                ) : null}

                <View style={styles.printerActions}>
                  <Button
                    compact
                    mode="outlined"
                    icon="printer-check"
                    onPress={handleTestPrinter}
                    disabled={printerNativeBusy || !savedPrinter || !isPrinterConnected}
                  >
                    {t("printer.test")}
                  </Button>
                  <Button
                    compact
                    mode="text"
                    icon="delete-outline"
                    textColor={HB_COLORS.danger}
                    onPress={handleClearPrinter}
                    disabled={printerNativeBusy || !savedPrinter}
                  >
                    {t("printer.clear")}
                  </Button>
                </View>
              </View>

              <View style={styles.printerRoleDivider} />

              <View style={styles.printerSection}>
                <View style={styles.printerSectionHeader}>
                  <View style={styles.compactRowTitleLine}>
                    <Text variant="titleMedium" style={styles.sheetSubheading}>
                      {t("receiptPrinter.title")}
                    </Text>
                    <StatusPill
                      label={receiptPrinterStatusText}
                      tone={receiptPrinterStatusTone}
                    />
                  </View>
                  <Text variant="bodySmall" style={styles.compactRowValue}>
                    {savedReceiptPrinter
                      ? t("receiptPrinter.selected", {
                          printer: savedReceiptPrinter.name || savedReceiptPrinter.address,
                        })
                      : t("receiptPrinter.notSelected")}
                  </Text>
                </View>

                <View style={styles.primaryPrinterActions}>
                  <Button
                    compact
                    mode="contained"
                    icon="magnify"
                    onPress={handleScanReceiptPrinters}
                    loading={receiptPrinterBusy && !isReceiptPrinterTesting}
                    disabled={printerNativeBusy}
                    style={styles.primaryActionButton}
                  >
                    {receiptPrinterBusy && !isReceiptPrinterTesting
                      ? t("receiptPrinter.scanning")
                      : t("receiptPrinter.scan")}
                  </Button>
                  <Button
                    compact
                    mode="outlined"
                    icon="receipt-text-outline"
                    onPress={handleTestReceiptPrinter}
                    loading={receiptPrinterBusy && isReceiptPrinterTesting}
                    disabled={printerNativeBusy || !savedReceiptPrinter}
                    style={styles.primaryActionButton}
                  >
                    {receiptPrinterBusy && isReceiptPrinterTesting
                      ? t("receiptPrinter.testing")
                      : t("receiptPrinter.test")}
                  </Button>
                </View>

                {receiptPrinterScanCompleted ? (
                  visibleReceiptPrinters.length ? (
                    <>
                      <Text variant="labelMedium" style={styles.listLabel}>
                        {t("receiptPrinter.available")}
                      </Text>
                      <PrinterDeviceList
                        devices={visibleReceiptPrinters}
                        selectedAddress={savedReceiptPrinter?.address}
                        actionLabel={t("receiptPrinter.save")}
                        disabled={printerNativeBusy}
                        onSelect={handleConnectReceiptPrinter}
                      />
                    </>
                  ) : (
                    <HelperText type="info" visible>
                      {t("receiptPrinter.empty")}
                    </HelperText>
                  )
                ) : null}

                <View style={styles.printerActions}>
                  <Button
                    compact
                    mode="text"
                    icon="delete-outline"
                    textColor={HB_COLORS.danger}
                    onPress={handleClearReceiptPrinter}
                    disabled={printerNativeBusy || !savedReceiptPrinter}
                  >
                    {t("receiptPrinter.clear")}
                  </Button>
                </View>
              </View>
            </ScrollView>
      </AccessibleSettingsModal>

      {offlineEligible ? (
        <AccessibleSettingsModal
          visible={offlineDataVisible}
          title={t("offlineData.title")}
          description={t("offlineData.description")}
          dismissLabel={t("common:actions.close")}
          testID="settings-offline-data-details"
          onDismiss={dismissOfflineDataSettings}
        >
          <ScrollView
            style={styles.sheetScroll}
            contentContainerStyle={styles.sheetContent}
            showsVerticalScrollIndicator={false}
          >
            <OfflineCatalogManagementPanel
              onNotify={(message) => Alert.alert(t("offlineData.title"), message)}
            />
          </ScrollView>
        </AccessibleSettingsModal>
      ) : null}

      <AccessibleSettingsModal
        visible={diagnosticsVisible}
        title={t("diagnostics.title")}
        description={t("diagnostics.description")}
        dismissLabel={t("common:actions.close")}
        testID="settings-diagnostics-details"
        onDismiss={dismissDiagnostics}
      >
            <ScrollView
              style={styles.sheetScroll}
              contentContainerStyle={styles.sheetContent}
              showsVerticalScrollIndicator={false}
            >
              <View style={styles.diagnosticsSummary}>
                <View
                  style={[
                    styles.connectionSummaryIcon,
                    connectionNeedsAttention && styles.connectionSummaryIconWarning,
                  ]}
                >
                  <Icon
                    source={connectionNeedsAttention ? "alert-circle-outline" : "check-circle-outline"}
                    size={24}
                    color={connectionNeedsAttention ? HB_COLORS.warning : HB_COLORS.success}
                  />
                </View>
                <View style={styles.compactRowText}>
                  <Text variant="titleSmall" style={styles.compactRowLabel}>
                    {t(
                      connectionNeedsAttention
                        ? "overview.deviceServicesAttention"
                        : "overview.deviceServicesReady"
                    )}
                  </Text>
                  <Text variant="bodySmall" style={styles.meta}>
                    {t("diagnostics.connectionSummary")}
                  </Text>
                </View>
              </View>

              <View style={styles.diagnosticList}>
                <InfoRow
                  label={t("device.statusLabel")}
                  value={accountBinding ? t("deviceStatus.bound") : deviceStatusText}
                  style={styles.diagnosticListRow}
                />
                <View style={styles.sectionDivider} />
                <InfoRow
                  label={t("device.storeLabelCompact")}
                  value={deviceStoreDisplayName}
                  style={styles.diagnosticListRow}
                />
                <View style={styles.sectionDivider} />
                <InfoRow
                  label={t("printer.title")}
                  value={labelPrinterStatusText}
                  style={styles.diagnosticListRow}
                />
                <View style={styles.sectionDivider} />
                <InfoRow
                  label={t("receiptPrinter.title")}
                  value={receiptPrinterStatusText}
                  style={styles.diagnosticListRow}
                />
                <View style={styles.sectionDivider} />
                <InfoRow label={t("apiHost.title")} value={apiHost} style={styles.diagnosticListRow} />
              </View>

              <View style={styles.diagnosticsSection}>
                <Text variant="labelLarge" style={styles.sheetSubheading}>
                  {t("diagnostics.runtimeDetails")}
                </Text>
                <View style={styles.updateInfoCompactList}>
                  {updateInfoRows.map((row) => (
                    <InfoRow
                      key={row.key}
                      label={t(row.labelKey)}
                      value={row.value ?? t(row.valueKey ?? "updates.unknown")}
                      monospace={row.key === "updateId" && Boolean(row.value)}
                    />
                  ))}
                </View>
              </View>

              <View style={styles.modalActions}>
                <Button mode="text" onPress={dismissDiagnostics}>
                  {t("common:actions.close")}
                </Button>
                <Button
                  mode="contained"
                  icon="cloud-download-outline"
                  onPress={handleCheckUpdates}
                  loading={updateBusy}
                  disabled={updateBusy}
                >
                  {t("updates.check")}
                </Button>
              </View>
            </ScrollView>
      </AccessibleSettingsModal>

      <AccessibleSettingsModal
        visible={apiHostModalVisible}
        title={t("apiHost.modalTitle")}
        description={t("apiHost.modalDescription")}
        dismissLabel={t("common:actions.close")}
        testID="settings-api-host-details"
        onDismiss={dismissApiHostSettings}
      >
        <ScrollView
          style={styles.sheetScroll}
          contentContainerStyle={styles.apiHostContent}
          showsVerticalScrollIndicator={false}
        >
          <View style={styles.apiHostCurrentBox}>
            <Text variant="labelMedium" style={styles.meta}>
              {t("apiHost.current")}
            </Text>
            <Text variant="bodyLarge" style={styles.value}>
              {apiHost}
            </Text>
          </View>
          <Text variant="labelLarge">{t("apiHost.presetsLabel")}</Text>
          <View style={styles.apiHostPresetList}>
            {API_HOST_PRESETS.map((preset) => {
              const selected = normalizeApiHost(apiHostDraft) === preset.host;
              return (
                <Button
                  key={preset.key}
                  compact
                  mode={selected ? "contained" : "outlined"}
                  style={styles.apiHostPresetButton}
                  onPress={() => setApiHostDraft(preset.host)}
                >
                  {t(`apiHost.presets.${preset.key}`)}
                </Button>
              );
            })}
          </View>
          <TextInput
            label={t("apiHost.inputLabel")}
            value={apiHostDraft}
            onChangeText={setApiHostDraft}
            mode="outlined"
            autoCapitalize="none"
            autoCorrect={false}
          />
          <View style={styles.modalActions}>
            <Button mode="text" onPress={dismissApiHostSettings}>
              {t("common:actions.cancel")}
            </Button>
            <Button mode="contained" onPress={handleSaveApiHost}>
              {t("common:actions.save")}
            </Button>
          </View>
        </ScrollView>
      </AccessibleSettingsModal>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: HB_COLORS.background,
  },
  content: {
    width: "100%",
    maxWidth: 680,
    alignSelf: "center",
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.xs,
    paddingBottom: HB_SPACING.lg,
    gap: HB_SPACING.lg,
  },
  header: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
  },
  title: {
    color: HB_COLORS.textPrimary,
    fontWeight: "800",
    letterSpacing: -0.4,
  },
  headerActionContent: {
    minHeight: 44,
  },
  section: {
    gap: HB_SPACING.xs,
  },
  sectionHeader: {
    gap: HB_SPACING.xxs,
    paddingHorizontal: HB_SPACING.xxs,
  },
  sectionTitle: {
    color: HB_COLORS.textSecondary,
    fontWeight: "700",
    letterSpacing: 0.2,
  },
  card: {
    overflow: "hidden",
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surface,
  },
  connectionSummary: {
    minHeight: 76,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  connectionSummaryIcon: {
    width: 42,
    height: 42,
    borderRadius: 21,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#ECFDF3",
  },
  connectionSummaryIconWarning: {
    backgroundColor: "#FFFAEB",
  },
  connectionSummaryText: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  compactRow: {
    minHeight: 68,
    flexDirection: "row",
    // 多行文字时图标与标题顶端对齐；单行时由 compactRowBody 的 minHeight 居中。
    alignItems: "flex-start",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  compactRowPressed: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  rowIconBox: {
    width: 40,
    height: 40,
    borderRadius: HB_RADIUS.control,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#EAF2FF",
  },
  compactRowText: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  compactRowBody: {
    flex: 1,
    minWidth: 0,
    minHeight: 40,
    justifyContent: "center",
    gap: 2,
  },
  compactRowTitleLine: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    columnGap: HB_SPACING.xs,
    rowGap: HB_SPACING.xxs,
  },
  compactRowLabel: {
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  compactRowValue: {
    color: HB_COLORS.textPrimary,
    fontSize: 13,
    lineHeight: 18,
  },
  compactRowEnd: {
    flexShrink: 0,
    alignSelf: "center",
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xxs,
  },
  compactRowAction: {
    flexShrink: 0,
  },
  rowChevron: {
    width: 44,
    height: 44,
    margin: -HB_SPACING.xs,
    alignItems: "center",
    justifyContent: "center",
  },
  statusPill: {
    maxWidth: "100%",
    minHeight: 22,
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    borderRadius: 999,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 3,
  },
  statusPillDot: {
    width: 6,
    height: 6,
    borderRadius: 3,
  },
  statusPillDotSuccess: {
    backgroundColor: HB_COLORS.success,
  },
  statusPillDotWarning: {
    backgroundColor: HB_COLORS.warning,
  },
  statusPillDotDanger: {
    backgroundColor: HB_COLORS.danger,
  },
  statusPillDotNeutral: {
    backgroundColor: HB_COLORS.textSecondary,
  },
  statusPillSuccess: {
    backgroundColor: "#ECFDF3",
  },
  statusPillWarning: {
    backgroundColor: "#FFFAEB",
  },
  statusPillDanger: {
    backgroundColor: "#FEF3F2",
  },
  statusPillNeutral: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  statusPillText: {
    flexShrink: 1,
    fontWeight: "700",
  },
  statusPillTextSuccess: {
    color: HB_COLORS.success,
  },
  statusPillTextWarning: {
    color: HB_COLORS.warning,
  },
  statusPillTextDanger: {
    color: HB_COLORS.danger,
  },
  statusPillTextNeutral: {
    color: HB_COLORS.textSecondary,
  },
  languageSelector: {
    width: 158,
  },
  sectionDivider: {
    height: StyleSheet.hairlineWidth,
    backgroundColor: HB_COLORS.outlineMuted,
  },
  value: {
    marginTop: HB_SPACING.xs,
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  meta: {
    color: HB_COLORS.textSecondary,
  },
  unbondedMeta: {
    color: HB_COLORS.warning,
    fontWeight: "700",
  },
  inlineNote: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    marginHorizontal: HB_SPACING.md,
    marginBottom: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  inlineNoteIcon: {
    paddingTop: 1,
  },
  inlineNoteText: {
    flex: 1,
    color: HB_COLORS.textSecondary,
    lineHeight: 18,
  },
  updatePanel: {
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    paddingBottom: HB_SPACING.md,
  },
  updatePanelHeader: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
  },
  updatePanelTitleLine: {
    flex: 1,
    minWidth: 0,
  },
  updateTiles: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  updateTile: {
    // 两格等分；可用宽度不足 2×128 时自动上下排列。
    flexGrow: 1,
    flexBasis: 128,
    minWidth: 0,
    gap: 2,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  updateTileLabel: {
    color: HB_COLORS.textSecondary,
  },
  updateTileValue: {
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  updateCheckButton: {
    borderRadius: HB_RADIUS.control,
  },
  updateCheckButtonContent: {
    minHeight: 44,
  },
  updateInfoCompactList: {
    gap: HB_SPACING.xs,
  },
  infoRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
  },
  infoRowLabel: {
    flexShrink: 1,
    maxWidth: "45%",
    color: HB_COLORS.textSecondary,
    lineHeight: 18,
  },
  infoRowValue: {
    flex: 1,
    color: HB_COLORS.textPrimary,
    textAlign: "right",
    fontWeight: "600",
    fontSize: 13,
    lineHeight: 18,
  },
  infoRowValueMono: {
    fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }),
    fontSize: 12,
  },
  apiHostCurrentBox: {
    gap: HB_SPACING.xxs,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surfaceMuted,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.sm,
  },
  apiHostPresetList: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  apiHostPresetButton: {
    borderRadius: 999,
  },
  statusBlock: {
    overflow: "hidden",
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  primaryButton: {
    marginTop: HB_SPACING.xxs,
  },
  filterRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  primaryPrinterActions: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  primaryActionButton: {
    // 按文字宽度起步再均分剩余空间：Paper 按钮文字只有一行，英文放不下时整行换行而不是截断成 "Scan receipt print…"。
    flexGrow: 1,
  },
  printerSection: {
    gap: HB_SPACING.sm,
  },
  printerSectionHeader: {
    gap: HB_SPACING.xxs,
  },
  printerRoleDivider: {
    height: StyleSheet.hairlineWidth,
    marginVertical: HB_SPACING.xxs,
    backgroundColor: HB_COLORS.outline,
  },
  listLabel: {
    color: HB_COLORS.textSecondary,
  },
  printerList: {
    gap: HB_SPACING.xs,
  },
  printerRow: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  printerActions: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  successBox: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#ECFDF3",
    padding: HB_SPACING.sm,
  },
  successText: {
    flex: 1,
    color: HB_COLORS.success,
  },
  dangerZone: {
    gap: HB_SPACING.xs,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#FECDCA",
    backgroundColor: "#FEF3F2",
    padding: HB_SPACING.sm,
  },
  dangerTitle: {
    color: HB_COLORS.danger,
    fontWeight: "700",
  },
  dangerDescription: {
    color: HB_COLORS.textSecondary,
  },
  deviceDangerButton: {
    borderColor: HB_COLORS.danger,
    alignSelf: "flex-start",
  },
  modalBackdrop: {
    flex: 1,
    justifyContent: "center",
    paddingHorizontal: HB_SPACING.md,
    backgroundColor: "rgba(15, 23, 42, 0.48)",
  },
  sheetModal: {
    width: "100%",
    maxHeight: "90%",
    maxWidth: 680,
    alignSelf: "center",
    overflow: "hidden",
    borderRadius: HB_RADIUS.sheet,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surface,
  },
  sheetHeader: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.md,
    paddingBottom: HB_SPACING.sm,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  sheetTitle: {
    color: HB_COLORS.textPrimary,
    fontWeight: "800",
  },
  sheetSubheading: {
    color: HB_COLORS.textPrimary,
    fontWeight: "700",
  },
  sheetScroll: {
    flexShrink: 1,
  },
  sheetContent: {
    gap: HB_SPACING.md,
    padding: HB_SPACING.md,
    paddingBottom: HB_SPACING.lg,
  },
  diagnosticsSummary: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surfaceMuted,
    padding: HB_SPACING.sm,
  },
  diagnosticList: {
    overflow: "hidden",
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surface,
  },
  diagnosticListRow: {
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
  },
  diagnosticsSection: {
    gap: HB_SPACING.xs,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surfaceMuted,
    padding: HB_SPACING.sm,
  },
  apiHostContent: {
    padding: HB_SPACING.md,
    paddingBottom: HB_SPACING.lg,
    gap: HB_SPACING.sm,
  },
  modalActions: {
    flexDirection: "row",
    flexWrap: "wrap",
    justifyContent: "flex-end",
    gap: HB_SPACING.xs,
  },
  logoutButton: {
    borderColor: HB_COLORS.danger,
    borderRadius: HB_RADIUS.control,
  },
  destructiveButtonContent: {
    minHeight: 48,
  },
  buildFooter: {
    color: HB_COLORS.textSecondary,
    textAlign: "center",
    marginTop: -HB_SPACING.xs,
  },
});
