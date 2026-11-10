import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useEffect, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Keyboard,
  Linking,
  Modal,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
  type StyleProp,
  type ViewStyle,
} from "react-native";

import type { ApiEntrySettingsController } from "@hb/pos-api-client/api-entry-mode-coordinator";
import {
  API_ENTRY_HOSTS,
  API_ENTRY_MODES,
  DEFAULT_API_ENTRY_MODE,
  isFailoverBaseUrl,
  type ApiEntryMode,
} from "@hb/pos-api-client/transport-failover";

import { useApiEntrySettings } from "./api-entry-settings";
import {
  resolveSettingsLocale,
  settingsText,
  type SettingsCopyKey,
  type SettingsLocale,
} from "./settings-copy";
import {
  hasLinklyCloudCredentials,
  isLinklyHealthReady,
  isLinklySetupReady,
  type PaymentEnvironment,
  type SettingsDangerousConfirmation,
  type SettingsPane,
  type SettingsPresenter,
  type SettingsState,
  type SettingsStatusCode,
} from "./settings-presenter";
import type { PendingWorkBlocker } from "@hb/pos-domain";
import { RECEIPT_TERMS_TEXT_MAX_LENGTH } from "@hb/pos-receipt-core/features/receipts/receipt-terms-text";
import type {
  SettingsSquareDevice,
  SettingsSquareDeviceCode,
  SettingsSquareLocation,
} from "@hb/pos-domain/features/settings/settings-square-setup";

import {
  HidScannerCapture,
  type HidScannerRouter,
} from "@/core/peripherals/scanner";
import {
  DEFAULT_LOCAL_HBPOS_API_BASE_URL,
  DEFAULT_REMOTE_HBPOS_API_BASE_URL,
} from "@hb/pos-domain/core/security/pos-api-addresses";
import type { CatalogRefreshState } from "@/features/catalog/catalog-refresh-coordinator";
import { CameraScannerModal } from "@/features/scanner-camera/camera-scanner-modal";
import { zellerSettingsText } from "@/features/zeller-terminal/zeller-settings-copy";
import { ZellerSettingsPanel } from "@/features/zeller-terminal/zeller-settings-panel";
import { useZellerTerminalBoundary } from "@/features/zeller-terminal/zeller-terminal-boundary";
import {
  PosButton,
  type PosButtonSize,
  type PosButtonTone,
  type PosIconName,
} from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPill, type PosPillTone } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSegmented } from "@/ui/controls/pos-segmented";
import { PosSwitch } from "@/ui/controls/pos-switch";
import {
  type TouchSoundKind,
  usePosSound,
} from "@/ui/feedback/pos-sound-context";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import { PosThemePicker } from "@/ui/shell/pos-theme-picker";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

export const SETTINGS_MIN_TOUCH_TARGET = 44;

export type SettingsScreenPresenter = Pick<
  SettingsPresenter,
  | "cancelConfirmation"
  | "checkForAppUpdate"
  | "clearSavedPrinter"
  | "confirmDangerousAction"
  | "connectPrinter"
  | "createSquareDeviceCode"
  | "downloadCatalog"
  | "getState"
  | "load"
  | "loadSquareDeviceCodes"
  | "loadSquareDevices"
  | "loadSquareLocations"
  | "loadReceiptProfile"
  | "syncReceiptProfile"
  | "requestApiAddressChange"
  | "requestAppRestart"
  | "requestCatalogReset"
  | "requestDeviceReregistration"
  | "previewDeviceReregistration"
  | "requestDeviceRegistrationReset"
  | "requestEnableZeller"
  | "requestLinklyPair"
  | "requestLinklyTerminalAssignment"
  | "refreshLinklySetup"
  | "refreshSquareDeviceCode"
  | "savePaymentSettings"
  | "savePaymentMethods"
  | "setUseManualCard"
  | "setGiftCardEnabled"
  | "savePrinterSettings"
  | "scanPrinters"
  | "selectLinklyTerminal"
  | "selectPane"
  | "setApiAddressDraft"
  | "setDrawerEnabled"
  | "setExternalDisplayEnabled"
  | "setLinklyEnvironment"
  | "setPaymentProvider"
  | "setPrinterEnabled"
  | "setPrinterLocale"
  | "setPrinterPaper"
  | "setPrinterPeripheralId"
  | "setReceiptBrandName"
  | "setReceiptStoreName"
  | "setReceiptAddress"
  | "setReceiptPhone"
  | "setReceiptAbn"
  | "setReceiptReturnPolicy"
  | "setReceiptVoucherTerms"
  | "setReceiptInstallmentTerms"
  | "setDeviceActivationCode"
  | "setSquareDeviceId"
  | "setSquareDeviceCodeId"
  | "setSquareDeviceCodeNameDraft"
  | "setSquareEnvironment"
  | "setSquareLocationId"
  | "setTerminalName"
  | "subscribe"
  | "testApiAddress"
  | "testCashDrawer"
  | "testExternalDisplay"
  | "testPaymentProvider"
  | "testLinklyTerminalConnection"
  | "testPrinter"
  | "testScanner"
> &
  Readonly<{ getState(): SettingsState }>;

type SettingsScreenProps = Readonly<{
  /** 收银入口（线路）设置控制器；不传则不显示该设置（运行时未接线）。 */
  apiEntry?: ApiEntrySettingsController;
  locale?: SettingsLocale;
  onBack?(): void;
  /** 打开只读网络检测页；不传则不显示入口。 */
  onOpenNetworkCheck?(): void;
  onOpenSyncHistory?(): void;
  presenter: SettingsScreenPresenter;
  scanner?: HidScannerRouter;
}>;

const NAV_ITEMS: readonly Readonly<{
  icon: PosIconName;
  label: SettingsCopyKey;
  pane: SettingsPane;
}>[] = [
  { pane: "general", label: "navigation.general", icon: "cog-outline" },
  {
    pane: "payments",
    label: "navigation.payments",
    icon: "credit-card-outline",
  },
  {
    pane: "peripherals",
    label: "navigation.peripherals",
    icon: "printer-outline",
  },
  { pane: "device", label: "navigation.device", icon: "tablet" },
  { pane: "hardware", label: "navigation.hardware", icon: "test-tube" },
];

/**
 * 软键盘当前占用的高度（没有键盘为 0）。确认弹窗带「员工条码」输入框且文案很长（英文尤甚），
 * 居中的卡片会被键盘盖住下半截；把遮罩层的底部内边距加上键盘高度，
 * 卡片就在键盘之上的可见区域里居中，标题、说明、输入框和按钮都不被盖住。
 */
function useKeyboardHeight(): number {
  const [height, setHeight] = useState(0);
  useEffect(() => {
    const show = Keyboard.addListener("keyboardWillShow", (event) =>
      setHeight(event.endCoordinates.height),
    );
    const hide = Keyboard.addListener("keyboardWillHide", () => setHeight(0));
    return () => {
      show.remove();
      hide.remove();
    };
  }, []);
  return height;
}

/** 窄屏 iPad 上线路管理需要更宽的工作区，分区目录在这个宽度以下收窄。 */
const SETTINGS_COMPACT_NAVIGATION_WIDTH = 1100;

/**
 * iPad 横屏设置台：左侧是放大的分区目录与当前终端，右侧每组设置一张卡片。
 * 危险操作不使用平台 Alert，而在同一屏显示完整影响范围，确保自动化、键盘与触控都走同一确认路径。
 */
export function SettingsScreen({
  apiEntry,
  locale: localeOverride,
  onBack,
  onOpenNetworkCheck,
  onOpenSyncHistory,
  presenter,
  scanner,
}: SettingsScreenProps) {
  usePosThemeVersion();
  const keyboardHeight = useKeyboardHeight();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );

  useEffect(() => {
    if (presenter.getState().kind === "idle") {
      void presenter.load();
    }
  }, [presenter]);
  useEffect(() => {
    if (!scanner) return undefined;
    scanner.pushContext("dialog");
    return () => {
      scanner.popContext();
    };
  }, [scanner]);
  const setScannerStatus = usePosShellStore((current) => current.setScanner);
  const { i18n } = useTranslation();
  const locale =
    localeOverride ??
    resolveSettingsLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const { width: windowWidth } = useWindowDimensions();
  const modernPayments = state.activePane === "payments" &&
    state.linklySetup?.terminals.value?.lineManagementSupported === true;
  const interactionLocked = state.busy || state.confirmation !== null;
  const cancelConfirmation = (): void => {
    if (state.busy) return;
    presenter.cancelConfirmation();
  };

  return (
    <PosScreenFrame
      active="settings"
      header={
        <PosPageHeader
          backDisabled={interactionLocked}
          backLabel={t("action.backToSales")}
          backTestID="settings-back"
          onBack={onBack}
          subtitle={t("header.subtitle")}
          title={t("header.title")}
        />
      }
      // 忙碌或确认弹窗打开时不允许从侧栏跳走，与「返回」按钮的禁用条件一致。
      navigationLocked={interactionLocked}
      testID="settings-screen"
    >
      {scanner ? (
        <HidScannerCapture
          active={
            state.activePane === "hardware" && state.confirmation === null
          }
          onCaptureStatusChange={setScannerStatus}
          scanner={scanner}
        />
      ) : null}
      <View style={styles.page}>
        {state.statusCode ? (
          <StatusBanner
            locale={locale}
            statusCode={state.statusCode}
            receiptProfileVersion={state.printer.profileVersion}
          />
        ) : null}

        <View style={styles.workspace} testID="settings-workspace">
          <View
            pointerEvents={state.confirmation ? "none" : "auto"}
            style={[
              styles.navigation,
              modernPayments &&
                windowWidth < SETTINGS_COMPACT_NAVIGATION_WIDTH &&
                styles.navigationCompact,
            ]}
          >
            <Text style={styles.navigationTitle}>{t("navigation.title")}</Text>
            {NAV_ITEMS.map((item) => (
              <SectionNavButton
                disabled={interactionLocked}
                icon={item.icon}
                key={item.pane}
                label={t(item.label)}
                onPress={() => presenter.selectPane(item.pane)}
                selected={state.activePane === item.pane}
                testID={`settings-nav-${item.pane}`}
              />
            ))}
            {/* 当前终端固定在目录底部：门店名称、设备代码、门店代码三行，顺序不变。 */}
            <View style={styles.deviceBadge} testID="settings-device-badge">
              <Text style={styles.badgeLabel}>
                {t("device.currentTerminal")}
              </Text>
              <Text
                accessibilityLabel={state.device.storeName || "—"}
                ellipsizeMode="tail"
                numberOfLines={2}
                style={styles.badgeValue}
                testID="settings-device-store-name"
              >
                {state.device.storeName || "—"}
              </Text>
              <Text
                style={styles.badgeMeta}
                testID="settings-device-code"
              >
                {state.device.deviceCode || "—"}
              </Text>
              <Text
                style={styles.badgeMeta}
                testID="settings-device-store-code"
              >
                {state.device.storeCode || t("device.unbound")}
              </Text>
            </View>
          </View>

          <View
            pointerEvents={state.confirmation ? "none" : "auto"}
            style={styles.workspaceMain}
          >
            {state.kind === "loading" || state.kind === "idle" ? (
              <EmptyPanel
                busy
                message={t("state.loading")}
                testID="settings-loading"
              />
            ) : null}
            {state.kind === "failed" ? (
              <EmptyPanel
                message={t("state.failed")}
                testID="settings-failed"
              />
            ) : null}
            {state.kind === "unauthorized" ? (
              <EmptyPanel
                message={t("state.unauthorized")}
                testID="settings-unauthorized"
              />
            ) : null}
            {state.kind === "ready" ? (
              <SettingsPaneContent
                apiEntry={apiEntry}
                locale={locale}
                onBack={onBack}
                onOpenNetworkCheck={onOpenNetworkCheck}
                onOpenSyncHistory={onOpenSyncHistory}
                presenter={presenter}
                scanner={scanner}
                state={state}
              />
            ) : null}
          </View>
        </View>

      </View>

      <Modal
        animationType="fade"
        onRequestClose={() => presenter.cancelConfirmation()}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="settings-confirmation-modal"
        transparent
        visible={state.confirmation !== null}
      >
        <View
          accessibilityViewIsModal
          style={[
            styles.confirmationOverlay,
            keyboardHeight > 0 && { paddingBottom: 24 + keyboardHeight },
          ]}
        >
          <PosPressable
            accessible={false}
            onPress={cancelConfirmation}
            sound="navigate"
            style={styles.backdropDismissArea}
            testID="settings-confirmation-backdrop"
          />
          {state.confirmation ? (
            <ConfirmationCard
              busy={state.busy}
              confirmation={state.confirmation}
              locale={locale}
              onCancel={() => presenter.cancelConfirmation()}
              onConfirm={(employeeBarcode) =>
                void presenter.confirmDangerousAction(employeeBarcode)
              }
              storeCode={state.device.storeCode}
              deviceCode={state.device.deviceCode}
            />
          ) : null}
        </View>
      </Modal>
    </PosScreenFrame>
  );
}

export function SettingsUnavailableScreen({
  locale: localeOverride,
  onBack,
}: Readonly<{ locale?: SettingsLocale; onBack(): void }>) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale =
    localeOverride ??
    resolveSettingsLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = (key: SettingsCopyKey) => settingsText(locale, key);
  return (
    <PosScreenFrame
      active="settings"
      header={<PosPageHeader title={t("header.title")} />}
      testID="settings-runtime-unavailable"
    >
      <View style={styles.unavailable}>
        <View style={styles.unavailableCard}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.mutedInk}
            name="cog-off-outline"
            size={44}
          />
          <Text style={styles.unavailableEyebrow}>
            {t("unavailable.eyebrow")}
          </Text>
          <Text style={styles.unavailableTitle}>{t("unavailable.title")}</Text>
          <Text style={styles.unavailableHint}>{t("unavailable.hint")}</Text>
          <PosButton
            icon="arrow-left"
            label={t("action.backToSales")}
            onPress={onBack}
            size="button"
            sound="navigate"
            testID="settings-unavailable-back"
          />
        </View>
      </View>
    </PosScreenFrame>
  );
}

function SettingsPaneContent({
  apiEntry,
  locale,
  onBack,
  onOpenNetworkCheck,
  onOpenSyncHistory,
  presenter,
  scanner,
  state,
}: Readonly<{
  apiEntry: ApiEntrySettingsController | undefined;
  locale: SettingsLocale;
  onBack: (() => void) | undefined;
  onOpenNetworkCheck: (() => void) | undefined;
  onOpenSyncHistory: (() => void) | undefined;
  presenter: SettingsScreenPresenter;
  scanner: HidScannerRouter | undefined;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  switch (state.activePane) {
    case "payments":
      return (
        <PaymentsPane locale={locale} presenter={presenter} state={state} />
      );
    case "peripherals":
      return (
        <PeripheralsPane locale={locale} presenter={presenter} state={state} />
      );
    case "device":
      return (
        <DevicePane
          locale={locale}
          onBack={onBack}
          onOpenSyncHistory={onOpenSyncHistory}
          presenter={presenter}
          scanner={scanner}
          state={state}
        />
      );
    case "hardware":
      return (
        <HardwarePane locale={locale} presenter={presenter} state={state} />
      );
    default:
      return (
        <GeneralPane
          apiEntry={apiEntry}
          locale={locale}
          onOpenNetworkCheck={onOpenNetworkCheck}
          presenter={presenter}
          state={state}
        />
      );
  }
}

function GeneralPane({
  apiEntry,
  locale,
  onOpenNetworkCheck,
  presenter,
  state,
}: Readonly<{
  apiEntry: ApiEntrySettingsController | undefined;
  locale: SettingsLocale;
  onOpenNetworkCheck: (() => void) | undefined;
  presenter: SettingsScreenPresenter;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  const locked = state.busy || state.confirmation !== null;
  const catalogRefreshRunning = state.catalogRefresh.kind === "running";
  const {
    buttonSoundEnabled,
    setButtonSoundEnabled,
    setSpecialNodeSoundEnabled,
    specialNodeSoundEnabled,
  } = usePosSound();
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  return (
    <View style={styles.pane} testID="settings-pane-content-general">
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.content}
        style={styles.contentScroll}
        testID="settings-content-scroll"
      >
        <SectionCard title={t("general.soundFeedback")}>
          <View style={styles.switchRowGroup}>
            <View style={styles.switchRow}>
              <View style={styles.switchCopy}>
                <Text style={styles.switchLabel}>
                  {t("general.buttonSound")}
                </Text>
                <Text style={styles.switchHint}>
                  {t("general.buttonSoundHint")}
                </Text>
              </View>
              <PosSwitch
                accessibilityLabel={t("general.buttonSound")}
                accessibilityRole="switch"
                accessibilityState={{ checked: buttonSoundEnabled }}
                onValueChange={setButtonSoundEnabled}
                sound={false}
                style={styles.settingSwitch}
                testID="settings-button-sound"
                {...switchTint()}
                value={buttonSoundEnabled}
              />
            </View>
            <View style={styles.switchRow}>
              <View style={styles.switchCopy}>
                <Text style={styles.switchLabel}>
                  {t("general.specialNodeSound")}
                </Text>
                <Text style={styles.switchHint}>
                  {t("general.specialNodeSoundHint")}
                </Text>
              </View>
              <PosSwitch
                accessibilityLabel={t("general.specialNodeSound")}
                accessibilityRole="switch"
                accessibilityState={{ checked: specialNodeSoundEnabled }}
                onValueChange={setSpecialNodeSoundEnabled}
                sound={false}
                style={styles.settingSwitch}
                testID="settings-special-node-sound"
                {...switchTint()}
                value={specialNodeSoundEnabled}
              />
            </View>
          </View>
        </SectionCard>

        {/* 界面主题不需要任何权限：能进设置页的人都可以换，切换与保存由 PosThemePicker 自己完成。 */}
        <SectionCard
          hint={t("general.themeHint")}
          testID="settings-theme-card"
          title={t("general.theme")}
        >
          <PosThemePicker language={locale} testID="settings-theme" />
        </SectionCard>

        <ApiEntryCard
          apiBaseUrl={state.apiBaseUrl}
          canChange={state.access.canReregisterDevice}
          controller={apiEntry}
          locale={locale}
          locked={locked}
        />

        <SectionCard
          hint={t("general.subtitle")}
          title={t("general.apiAddress")}
        >
          <View style={styles.cardRow}>
            <View
              style={[
                styles.inputFrame,
                (locked || !state.access.canReregisterDevice) &&
                  styles.inputFrameDisabled,
              ]}
            >
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.mutedInk}
                name="link-variant"
                size={24}
              />
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("general.apiAddress")}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!locked && state.access.canReregisterDevice}
                onChangeText={(value) => presenter.setApiAddressDraft(value)}
                placeholder="https://example.com/pos-api"
                placeholderTextColor={posColors.placeholder}
                style={styles.inputFrameField}
                testID="settings-api-address"
                value={state.apiAddressDraft}
              />
            </View>
            <ActionButton
              disabled={locked || !state.access.canReregisterDevice}
              label={t("general.useLocalApi")}
              onPress={() =>
                presenter.setApiAddressDraft(
                  DEFAULT_LOCAL_HBPOS_API_BASE_URL,
                )
              }
              testID="settings-api-use-local"
              tone="quiet"
            />
            <ActionButton
              disabled={locked || !state.access.canReregisterDevice}
              label={t("general.useRemoteApi")}
              onPress={() =>
                presenter.setApiAddressDraft(
                  DEFAULT_REMOTE_HBPOS_API_BASE_URL,
                )
              }
              testID="settings-api-use-remote"
              tone="quiet"
            />
          </View>
          <View style={styles.actionRow} testID="settings-api-actions">
            <ActionButton
              disabled={locked || !state.access.canReregisterDevice}
              fill
              icon="lan-check"
              label={t("general.testApiAddress")}
              onPress={() => void presenter.testApiAddress()}
              testID="settings-api-test"
            />
            {onOpenNetworkCheck ? (
              // 网络检测只读，不受「可重新绑定设备」权限限制；确认弹窗打开时仍锁定避免误触。
              <ActionButton
                disabled={locked}
                fill
                icon="access-point-network"
                label={t("general.networkCheck")}
                onPress={onOpenNetworkCheck}
                testID="settings-network-check"
              />
            ) : null}
            {/* 红色只表示「会改变数据分区」；点了仍先弹确认框，提示音沿用普通按钮。 */}
            <ActionButton
              disabled={
                locked ||
                catalogRefreshRunning ||
                !state.access.canReregisterDevice
              }
              fill
              icon="swap-horizontal"
              label={t("general.reviewApiAddress")}
              onPress={() => presenter.requestApiAddressChange()}
              sound="tap"
              testID="settings-api-request-change"
              tone="danger"
            />
          </View>
        </SectionCard>

        <View style={styles.cardColumns}>
          <SectionCard style={styles.columnCard} title={t("general.catalog")}>
            <View style={styles.metricRow}>
              <Metric
                label={t("metric.snapshot")}
                value={state.catalog.snapshotId ?? t("value.none")}
              />
            </View>
            <View style={styles.metricRow}>
              <Metric
                label={t("metric.items")}
                value={String(state.catalog.itemCount)}
              />
              <Metric
                label={t("metric.activated")}
                value={compactDate(state.catalog.activatedAt)}
              />
            </View>
            <CatalogRefreshStatus
              locale={locale}
              refresh={state.catalogRefresh}
            />
            <View style={styles.actionRow}>
              <ActionButton
                disabled={
                  locked ||
                  catalogRefreshRunning ||
                  !state.access.canDownloadCatalog
                }
                fill
                icon="cloud-download-outline"
                label={t(
                  catalogRefreshRunning
                    ? "general.downloadingCatalog"
                    : "general.downloadCatalog",
                )}
                onPress={() => void presenter.downloadCatalog()}
                testID="settings-catalog-download"
              />
              <ActionButton
                disabled={
                  locked ||
                  catalogRefreshRunning ||
                  !state.access.canResetCatalog
                }
                label={t("general.resetCatalog")}
                onPress={() => presenter.requestCatalogReset()}
                testID="settings-catalog-reset"
                tone="danger"
              />
            </View>
            <Text style={styles.safetyNote}>{t("general.catalogSafety")}</Text>
            <Text style={styles.backgroundNote}>
              {t("general.catalogBackground")}
            </Text>
          </SectionCard>

          <SectionCard style={styles.columnCard} title={t("general.appUpdate")}>
            <View style={styles.metricRow}>
              <Metric
                label={t("metric.channel")}
                value={state.appUpdate.channel}
              />
            </View>
            <View style={styles.metricRow}>
              <Metric
                label={t("metric.currentVersion")}
                value={state.appUpdate.currentVersion || "—"}
              />
              <Metric
                label={t("metric.availableVersion")}
                value={state.appUpdate.availableVersion ?? t("value.current")}
              />
            </View>
            <View style={styles.actionRow}>
              <ActionButton
                disabled={locked || !state.access.canManageAppUpdate}
                fill
                icon="update"
                label={t("general.checkUpdate")}
                onPress={() => void presenter.checkForAppUpdate()}
                testID="settings-update-check"
              />
              <ActionButton
                disabled={
                  locked ||
                  catalogRefreshRunning ||
                  !state.access.canManageAppUpdate ||
                  !state.appUpdate.restartAvailable
                }
                label={t("general.restart")}
                onPress={() => presenter.requestAppRestart()}
                testID="settings-update-restart"
                tone="danger"
              />
            </View>
          </SectionCard>
        </View>
      </PosKeyboardAwareScrollView>
    </View>
  );
}

type ApiEntryChoice = ApiEntryMode | "auto";

/**
 * 收银入口（线路）：显示当前实际在用的线路与设置来源，并允许在本机手动指定首选线路。
 * - 手动指定只改「首选」，故障转移照常生效；选「自动」= 清除本机覆盖、恢复跟随后台。
 * - 自定义 API 地址（本地 / 局域网 / 其他服务器）不在三条线路之内，选择无意义，整张卡片的选项禁用。
 */
function ApiEntryCard({
  apiBaseUrl,
  canChange,
  controller,
  locale,
  locked,
}: Readonly<{
  apiBaseUrl: string;
  canChange: boolean;
  controller: ApiEntrySettingsController | undefined;
  locale: SettingsLocale;
  locked: boolean;
}>) {
  usePosThemeVersion();
  const snapshot = useApiEntrySettings(controller);
  if (!controller || !snapshot) return null;
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const modeLabel = (mode: ApiEntryMode): string =>
    t(`apiEntry.mode.${mode}`);
  const applicable = isFailoverBaseUrl(apiBaseUrl);
  const editable = applicable && canChange && !locked;
  const sourceText =
    snapshot.source === "manual"
      ? t("apiEntry.source.manual")
      : snapshot.source === "backend" && snapshot.backendMode
        ? t("apiEntry.source.backend", {
            route: modeLabel(snapshot.backendMode),
          })
        : t("apiEntry.source.default", {
            route: modeLabel(DEFAULT_API_ENTRY_MODE),
          });
  return (
    <SectionCard hint={t("apiEntry.hint")} title={t("apiEntry.title")}>
      <View style={styles.cardBody} testID="settings-api-entry">
        {applicable ? (
          <View style={styles.metricRow}>
            <Metric
              label={t("apiEntry.current")}
              value={`${modeLabel(snapshot.activeMode)} · ${API_ENTRY_HOSTS[snapshot.activeMode]}`}
            />
            <Metric label={t("apiEntry.setting")} value={sourceText} />
          </View>
        ) : (
          <Text style={styles.sectionCopy} testID="settings-api-entry-unsupported">
            {t("apiEntry.unsupported")}
          </Text>
        )}
        {applicable && snapshot.usingFallback ? (
          <Text
            style={styles.dangerDescription}
            testID="settings-api-entry-fallback"
          >
            {t("apiEntry.fallback", {
              preferred: modeLabel(snapshot.preferredMode),
              active: modeLabel(snapshot.activeMode),
            })}
          </Text>
        ) : null}
        {/* 四选一整宽分段：「自动」= 清除本机覆盖，其余三项指定首选线路。 */}
        <PosSegmented<ApiEntryChoice>
          accessibilityLabel={t("apiEntry.title")}
          disabled={!editable}
          onChange={(choice) =>
            controller.setManualMode(choice === "auto" ? null : choice)
          }
          options={[
            {
              value: "auto",
              label: t("apiEntry.option.auto"),
              testID: "settings-api-entry-auto",
            },
            ...API_ENTRY_MODES.map((mode) => ({
              value: mode,
              label: modeLabel(mode),
              testID: `settings-api-entry-${mode}`,
            })),
          ]}
          testID="settings-api-entry-options"
          value={snapshot.manualMode ?? "auto"}
        />
        {applicable && !canChange ? (
          <Text style={styles.sectionCopy} testID="settings-api-entry-no-permission">
            {t("apiEntry.noPermission")}
          </Text>
        ) : null}
        {snapshot.manualSaveFailed ? (
          <Text
            style={styles.dangerDescription}
            testID="settings-api-entry-save-failed"
          >
            {t("apiEntry.saveFailed")}
          </Text>
        ) : null}
      </View>
    </SectionCard>
  );
}

function CatalogRefreshStatus({
  locale,
  refresh,
}: Readonly<{
  locale: SettingsLocale;
  refresh: CatalogRefreshState;
}>) {
  usePosThemeVersion();
  if (refresh.kind === "idle") return null;

  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const progress = refresh.progress;
  const preparing =
    refresh.kind === "running" &&
    progress.currentStep === "prepare" &&
    progress.steps[0]?.percent === 0;
  const currentStep =
    progress.steps.find((step) => step.step === progress.currentStep) ??
    progress.steps[0];
  const details = currentStep
    ? [
        currentStep.completedItemCount !== undefined &&
        currentStep.totalItemCount !== undefined
          ? t("catalogRefresh.items", {
              completed: currentStep.completedItemCount,
              total: currentStep.totalItemCount,
            })
          : null,
        currentStep.completedPageCount !== undefined &&
        currentStep.totalPageCount !== undefined
          ? t("catalogRefresh.pages", {
              completed: currentStep.completedPageCount,
              total: currentStep.totalPageCount,
            })
          : null,
      ].filter((value): value is string => value !== null)
    : [];
  const titleKey: SettingsCopyKey =
    refresh.kind === "running"
      ? "catalogRefresh.running"
      : refresh.kind === "success"
        ? "catalogRefresh.success"
        : refresh.kind === "warning"
          ? "catalogRefresh.warning"
          : "catalogRefresh.failed";

  return (
    <View
      accessibilityRole={
        refresh.kind === "warning" || refresh.kind === "failed"
          ? "alert"
          : undefined
      }
      style={[
        styles.catalogRefreshStatus,
        refresh.kind === "warning" && styles.catalogRefreshWarning,
        refresh.kind === "failed" && styles.catalogRefreshFailed,
      ]}
      testID="settings-catalog-refresh-state"
    >
      <View style={styles.catalogRefreshHeading}>
        {refresh.kind === "running" ? (
          <ActivityIndicator color={posColors.accent} size="small" />
        ) : null}
        <Text style={styles.catalogRefreshTitle}>{t(titleKey)}</Text>
      </View>
      {preparing ? (
        <Text style={styles.catalogRefreshMeta}>
          {t("catalogRefresh.preparing")}
        </Text>
      ) : null}
      {!preparing ? (
        <>
          <Text style={styles.catalogRefreshMeta}>
            {t("catalogRefresh.currentStep", {
              step: t(
                `catalogRefresh.step.${progress.currentStep}` as SettingsCopyKey,
              ),
            })}
          </Text>
          <View
            accessibilityLabel={t("catalogRefresh.progress", {
              percent: formatPercent(progress.overallPercent),
            })}
            accessibilityRole="progressbar"
            accessibilityValue={{
              min: 0,
              max: 100,
              now: progress.overallPercent,
            }}
            style={styles.catalogRefreshTrack}
            testID="settings-catalog-refresh-progress"
          >
            <View
              style={[
                styles.catalogRefreshFill,
                { width: `${progress.overallPercent}%` },
              ]}
            />
          </View>
          <Text style={styles.catalogRefreshMeta}>
            {t("catalogRefresh.progress", {
              percent: formatPercent(progress.overallPercent),
            })}
          </Text>
        </>
      ) : null}
      <Text
        style={styles.catalogRefreshMeta}
        testID="settings-catalog-refresh-elapsed"
      >
        {t("catalogRefresh.elapsed", {
          elapsed: formatElapsedMilliseconds(progress.elapsedMilliseconds),
        })}
      </Text>
      {details.length > 0 ? (
        <Text
          style={styles.catalogRefreshMeta}
          testID="settings-catalog-refresh-detail"
        >
          {details.join(" · ")}
        </Text>
      ) : null}
      {refresh.kind === "warning" || refresh.kind === "failed" ? (
        <Text style={styles.catalogRefreshCode}>
          {t("catalogRefresh.safeCode", {
            code:
              refresh.kind === "warning"
                ? refresh.warningCode
                : refresh.errorCode,
          })}
        </Text>
      ) : null}
    </View>
  );
}

function PaymentsPane({
  locale,
  presenter,
  state,
}: Readonly<{
  locale: SettingsLocale;
  presenter: SettingsScreenPresenter;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  const [squarePicker, setSquarePicker] = useState<
    "device" | "location" | null
  >(null);
  const modernLayout = state.linklySetup?.terminals.value?.lineManagementSupported === true;
  // 查看配置与启用支付分开，未配对线路也能进入管理页。
  const [providerPanel, setProviderPanel] = useState<PaymentProviderPanel>(
    state.paymentProviderDraft ?? "linkly",
  );
  // Zeller 页签只在 App 根上提供了 Zeller 终端、并且当前构建开放时出现（正式版暂不开放），只接进页签式布局。
  const zellerBoundary = useZellerTerminalBoundary();
  const zeller = zellerBoundary?.settingsTabAvailable ? zellerBoundary : null;
  const zellerPanelOpen = zeller !== null && modernLayout && providerPanel === "zeller";
  const providerTabs: readonly PaymentProviderPanel[] =
    zeller !== null ? ["square", "linkly", "zeller"] : ["square", "linkly"];
  // Square / Linkly 的环境切换与启用按钮只认这两家；停在 Zeller 页签时这些控件不显示。
  // Zeller 页签不可用（例如正式版）而当前通道是 zeller 时，回落到 Linkly 面板，不留空白。
  const cardProviderPanel: "square" | "linkly" = providerPanel === "zeller" ? "linkly" : providerPanel;
  // Zeller SDK 只在第一次打开 Zeller 页签时才加载，不打开的人不受影响。
  const activateZeller = zeller?.activate;
  useEffect(() => {
    if (zellerPanelOpen) activateZeller?.();
  }, [activateZeller, zellerPanelOpen]);
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const disabled =
    state.busy ||
    state.confirmation !== null ||
    !state.access.canConfigurePayments;
  const catalogRefreshRunning = state.catalogRefresh.kind === "running";
  const squareAvailable =
    state.square.available && state.square.blockerCode === null;
  const squareNeedsInitialSetup =
    state.squareSetup.available &&
    state.square.blockerCode === "SQUARE_CONFIGURATION_MISSING";
  const linklyAvailable =
    state.linkly.available && state.linkly.blockerCode === null;
  // 缺少公开环境属于可恢复的首次设置态；无效配置与读取失败仍保持关闭。
  const linklyNeedsInitialSetup =
    !state.linkly.available &&
    state.linkly.blockerCode === "LINKLY_CONFIGURATION_MISSING";
  const linklySelectable =
    (linklyAvailable || linklyNeedsInitialSetup) &&
    (!state.linklySetup || linklySelectionReady(state));
  const squareRuntimeDisabled =
    disabled || !squareAvailable || state.paymentProviderDraft !== "square";
  const linklySetupDisabled = disabled;
  const squareSetupDisabled =
    disabled ||
    !state.squareSetup.available ||
    (!modernLayout && state.paymentProviderDraft !== "square");
  const selectedLocation = state.squareSetup.locations.items.find(
    (location) => location.id === state.squareSetup.selectedLocationId,
  );
  const selectedDevice = state.squareSetup.devices.items.find(
    (device) => device.id === state.squareSetup.selectedDeviceId,
  );
  const squareLocationMeta = state.squareSetup.selectedLocationId
    ? squareSelectionMeta(
        state.squareSetup.selectedLocationId,
        selectedLocation?.status,
        t("square.statusUnknown"),
      )
    : t("square.notSelected");
  const squareDeviceMeta = state.squareSetup.selectedDeviceId
    ? squareSelectionMeta(
        state.squareSetup.selectedDeviceId,
        selectedDevice?.status,
        t("square.statusUnknown"),
        selectedDevice?.code,
      )
    : t("square.notSelected");
  const squareProduction = state.squareDraft.environment === "Production";
  const squareDeviceCodeDisabled =
    squareSetupDisabled ||
    !squareProduction ||
    !state.squareSetup.selectedLocationId;
  const squareDeviceCodesMatchLocation =
    state.squareSetup.deviceCodesLoadedForLocationId ===
    state.squareSetup.selectedLocationId;
  const squareDeviceCodeResourceKind: SquareSetupResourceKind =
    state.squareSetup.deviceCodes.kind === "ready" &&
    !squareDeviceCodesMatchLocation
      ? "idle"
      : state.squareSetup.deviceCodes.kind;
  const pickerResource =
    squarePicker === "device"
      ? state.squareSetup.devices
      : state.squareSetup.locations;
  const pickerOptions: readonly SquarePickerOption[] =
    squarePicker === "device"
      ? state.squareSetup.devices.items.map(squareDevicePickerOption)
      : state.squareSetup.locations.items.map(squareLocationPickerOption);
  return (
    <View style={styles.pane} testID="settings-pane-content-payments">
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.content}
        style={styles.contentScroll}
        testID="settings-content-scroll"
      >
        <PaneHint
          text={t(modernLayout ? "payments.manageSubtitle" : "payments.subtitle")}
        />
        {catalogRefreshRunning ? (
          <CatalogBusyNotice text={t("payments.catalogBusy")} />
        ) : null}
        <SectionCard
          hint={t("payments.methodsHint")}
          title={t("payments.methodsTitle")}
        >
          <View style={styles.paymentMethodsRow}>
            {([
              { key: "manual", label: "payments.manualCard", hint: "payments.manualCardHint", value: state.paymentMethodsDraft.useManualCard, change: (value: boolean) => presenter.setUseManualCard(value) },
              { key: "gift", label: "payments.giftCard", hint: "payments.giftCardHint", value: state.paymentMethodsDraft.giftCardEnabled, change: (value: boolean) => presenter.setGiftCardEnabled(value) },
            ] as const).map((method) => (
              <View key={method.key} style={styles.switchRow}>
                <View style={styles.switchCopy}>
                  <Text style={styles.switchLabel}>{t(method.label)}</Text>
                  <Text style={styles.switchHint}>{t(method.hint)}</Text>
                </View>
                <PosSwitch
                  accessibilityLabel={t(method.label)}
                  accessibilityRole="switch"
                  accessibilityState={{ checked: method.value, disabled: disabled || catalogRefreshRunning || !state.paymentMethodsAvailable }}
                  disabled={disabled || catalogRefreshRunning || !state.paymentMethodsAvailable}
                  onValueChange={method.change}
                  style={styles.settingSwitch}
                  testID={`settings-payment-method-${method.key}`}
                  {...switchTint()}
                  value={method.value}
                />
              </View>
            ))}
            <ActionButton
              disabled={disabled || catalogRefreshRunning || !state.paymentMethodsAvailable}
              icon="content-save-outline"
              label={t("payments.saveMethods")}
              onPress={() => void presenter.savePaymentMethods()}
              testID="settings-payment-methods-save"
            />
          </View>
        </SectionCard>

        {!modernLayout ? (
          <SectionCard
            hint={t("payments.providerHint")}
            title={t("payments.provider")}
          >
            <View style={styles.actionRow}>
              <ToggleButton
                disabled={
                  disabled ||
                  (!squareAvailable && !state.squareSetup.available)
                }
                label="Square"
                onPress={() => presenter.setPaymentProvider("square")}
                selected={state.paymentProviderDraft === "square"}
                testID="settings-payment-provider-square"
              />
              <ToggleButton
                disabled={disabled || !linklySelectable}
                label="Linkly"
                onPress={() => presenter.setPaymentProvider("linkly")}
                selected={state.paymentProviderDraft === "linkly"}
                testID="settings-payment-provider-linkly"
              />
            </View>
            <Text
              style={styles.safetyNote}
              testID="settings-payment-provider-state"
            >
              {state.paymentProviderDraft === null
                ? t("payments.noneSelected")
                : t(PAYMENT_PROVIDER_SELECTED_COPY[state.paymentProviderDraft])}
            </Text>
          </SectionCard>
        ) : null}

        {/* 页签式布局：整块是一张「刷卡终端」卡；旧布局：Square / Linkly 两张卡并排。 */}
        <PaymentTerminalShell modern={modernLayout} title={t("payments.provider")}>
          {modernLayout ? (
            <View style={styles.paymentToolbar} testID="settings-payment-toolbar">
              <View accessibilityRole="tablist" style={styles.paymentTabs}>
                {providerTabs.map((provider, index) => {
                  const viewing = providerPanel === provider;
                  return (
                    <PosPressable
                      key={provider}
                      accessibilityRole="tab"
                      accessibilityLabel={t("payments.manageProvider", { provider: PAYMENT_PROVIDER_PANEL_LABELS[provider] })}
                      accessibilityState={{ selected: viewing, disabled }}
                      disabled={disabled}
                      onPress={() => setProviderPanel(provider)}
                      sound="navigate"
                      style={({ pressed }) => [
                        styles.paymentTab,
                        index > 0 && styles.paymentTabDivider,
                        viewing && styles.paymentTabSelected,
                        pressed && !disabled && !viewing && styles.paymentTabPressed,
                      ]}
                      testID={`settings-payment-tab-${provider}`}
                    >
                      {/* 小圆点标出这台 iPad 当前启用的是哪一家，和正在查看哪个页签无关。 */}
                      {state.paymentProviderDraft === provider ? (
                        <View
                          style={styles.paymentTabDot}
                          testID={`settings-payment-tab-${provider}-active`}
                        />
                      ) : null}
                      <Text
                        numberOfLines={1}
                        style={[
                          styles.paymentTabLabel,
                          viewing && styles.paymentTabSelectedLabel,
                          disabled && !viewing && styles.paymentTabDisabledLabel,
                        ]}
                      >
                        {PAYMENT_PROVIDER_PANEL_LABELS[provider]}
                      </Text>
                    </PosPressable>
                  );
                })}
              </View>
              {zellerPanelOpen ? (
                // Zeller 没有测试环境：原来放环境切换的位置改成一句说明，不留空也不让人找「环境去哪了」。
                <Text style={styles.paymentEnvironmentNote} testID="settings-zeller-environment-note">
                  {zellerSettingsText(locale, "tab.environmentNote")}
                </Text>
              ) : (
                <EnvironmentSelector
                  disabled={disabled}
                  environment={cardProviderPanel === "square" ? state.squareDraft.environment : state.linklyDraft.environment}
                  locale={locale}
                  onSelect={(environment) => cardProviderPanel === "square"
                    ? presenter.setSquareEnvironment(environment)
                    : presenter.setLinklyEnvironment(environment)}
                  prefix={`settings-${cardProviderPanel}`}
                  style={styles.paymentEnvironment}
                />
              )}
            </View>
          ) : null}

          {!modernLayout || (!zellerPanelOpen && cardProviderPanel === "square") ? (
            <ProviderPanelShell modern={modernLayout} title="Square">
              {!squareNeedsInitialSetup ? (
                <Availability
                  available={squareAvailable}
                  blockerCode={state.square.blockerCode}
                  locale={locale}
                />
              ) : null}
              {!modernLayout ? (
                <EnvironmentSelector
                  disabled={squareSetupDisabled}
                  environment={state.squareDraft.environment}
                  locale={locale}
                  onSelect={(environment) =>
                    presenter.setSquareEnvironment(environment)
                  }
                  prefix="settings-square"
                />
              ) : null}
              <View style={styles.squareSummaryRow}>
                <SquareSummaryMetric
                  label={t("square.serverToken")}
                  testID="settings-square-token-status"
                  value={squareTokenStatusText(locale, state.squareSetup.token)}
                />
                <SquareSummaryMetric
                  label={t("square.currentBinding")}
                  testID="settings-square-current-binding"
                  value={`${state.square.environment} · ${
                    state.square.locationId || t("square.notSelected")
                  }\n${state.square.deviceId || t("square.notSelected")}`}
                />
              </View>
              <SquareSelectionField
                disabled={squareSetupDisabled}
                disabledText={t("square.setupUnavailable")}
                emptyText={t("square.locationsEmpty")}
                failedText={t("square.loadFailed")}
                idleText={t("square.notLoaded")}
                label={t("square.location")}
                loadLabel={t("square.loadLocations")}
                loadingText={t("square.loadingLocations")}
                onLoad={() => {
                  setSquarePicker("location");
                  void presenter.loadSquareLocations();
                }}
                onSelect={() => setSquarePicker("location")}
                primaryText={selectedLocation?.name ?? t("square.notSelected")}
                resourceKind={state.squareSetup.locations.kind}
                secondaryText={squareLocationMeta}
                selectLabel={t("square.selectLocation")}
                selectReady={state.squareSetup.locations.kind === "ready"}
                testID="settings-square-location"
              />
              <SquareSelectionField
                disabled={
                  squareSetupDisabled || !state.squareSetup.selectedLocationId
                }
                disabledText={t("square.setupUnavailable")}
                emptyText={t("square.devicesEmpty")}
                failedText={t("square.loadFailed")}
                idleText={t("square.notLoaded")}
                label={t("square.device")}
                loadLabel={t("square.loadDevices")}
                loadingText={t("square.loadingDevices")}
                onLoad={() => {
                  setSquarePicker("device");
                  void presenter.loadSquareDevices();
                }}
                onSelect={() => setSquarePicker("device")}
                primaryText={selectedDevice?.name ?? t("square.notSelected")}
                resourceKind={state.squareSetup.devices.kind}
                secondaryText={squareDeviceMeta}
                selectLabel={t("square.selectDevice")}
                selectReady={
                  state.squareSetup.devices.kind === "ready" &&
                  state.squareSetup.devicesLoadedForLocationId ===
                    state.squareSetup.selectedLocationId
                }
                testID="settings-square-device"
              />

              <View style={styles.squareDeviceCodeSection}>
                <FieldLabel label={t("square.deviceCode")} />
                <PosKeyboardAwareTextInput
                  accessibilityLabel={t("square.deviceCodeName")}
                  autoCapitalize="words"
                  autoCorrect={false}
                  editable={!squareDeviceCodeDisabled}
                  onChangeText={(value) =>
                    presenter.setSquareDeviceCodeNameDraft(value)
                  }
                  placeholder={t("square.deviceCodeName")}
                  placeholderTextColor={posColors.placeholder}
                  style={[
                    styles.textInput,
                    squareDeviceCodeDisabled && styles.textInputDisabled,
                  ]}
                  testID="settings-square-device-code-name"
                  value={state.squareDeviceCodeNameDraft}
                />
                <Text style={styles.fieldHint}>
                  {t("square.deviceCodeNameHint")}
                </Text>
                <View style={styles.actionRow}>
                  <ActionButton
                    disabled={
                      squareDeviceCodeDisabled ||
                      state.squareSetup.deviceCodes.kind === "loading"
                    }
                    icon="refresh"
                    label={t("square.loadDeviceCodes")}
                    onPress={() => void presenter.loadSquareDeviceCodes()}
                    testID="settings-square-device-code-load"
                    tone="quiet"
                  />
                  <ActionButton
                    disabled={
                      squareDeviceCodeDisabled ||
                      state.squareDeviceCodeNameDraft.trim().length === 0 ||
                      state.squareSetup.deviceCodes.kind === "loading"
                    }
                    icon="plus"
                    label={t("square.createDeviceCode")}
                    onPress={() => void presenter.createSquareDeviceCode()}
                    testID="settings-square-device-code-create"
                  />
                  <ActionButton
                    disabled={
                      squareDeviceCodeDisabled ||
                      !state.squareSetup.selectedDeviceCodeId ||
                      !squareDeviceCodesMatchLocation ||
                      state.squareSetup.deviceCodes.kind === "loading"
                    }
                    label={t("square.refreshDeviceCode")}
                    onPress={() => void presenter.refreshSquareDeviceCode()}
                    testID="settings-square-device-code-refresh"
                    tone="quiet"
                  />
                </View>
                {!squareProduction ? (
                  <Text
                    accessibilityLiveRegion="polite"
                    style={styles.squareSandboxNote}
                    testID="settings-square-device-code-disabled"
                  >
                    {t("square.deviceCodeSandboxDisabled")}
                  </Text>
                ) : (
                  <>
                    <SquareDeviceCodeList
                      disabled={squareDeviceCodeDisabled}
                      emptyText={t("square.deviceCodesEmpty")}
                      failedText={t("square.loadFailed")}
                      idleText={t("square.deviceCodesIdle")}
                      items={state.squareSetup.deviceCodes.items}
                      loadingText={t("square.deviceCodesLoading")}
                      onSelect={(deviceCodeId) =>
                        presenter.setSquareDeviceCodeId(deviceCodeId)
                      }
                      resourceKind={squareDeviceCodeResourceKind}
                      selectedId={state.squareSetup.selectedDeviceCodeId}
                      selectedLabel={t("square.currentDeviceCode")}
                      statusUnknown={t("square.statusUnknown")}
                      codePending={t("square.codePending")}
                    />
                    <Text style={styles.fieldHint}>
                      {t("square.deviceCodePairing")}
                    </Text>
                  </>
                )}
              </View>
              <View style={styles.actionRow}>
                <ActionButton
                  disabled={squareRuntimeDisabled}
                  icon="check-network-outline"
                  label={t("action.test")}
                  onPress={() => void presenter.testPaymentProvider("square")}
                  testID="settings-square-test"
                />
              </View>
            </ProviderPanelShell>
          ) : null}

          {!modernLayout || (!zellerPanelOpen && cardProviderPanel === "linkly") ? (
            <ProviderPanelShell modern={modernLayout} title="Linkly">
              {(!modernLayout || !linklyAvailable) && (!linklyNeedsInitialSetup || !state.linklySetup) ? (
                <Availability
                  available={linklyAvailable}
                  blockerCode={state.linkly.blockerCode}
                  locale={locale}
                />
              ) : null}
              {!modernLayout ? (
                <EnvironmentSelector
                  disabled={linklySetupDisabled}
                  environment={state.linklyDraft.environment}
                  locale={locale}
                  onSelect={(environment) =>
                    presenter.setLinklyEnvironment(environment)
                  }
                  prefix="settings-linkly"
                />
              ) : null}
              {!modernLayout ? <Text style={styles.sectionCopy}>{t("payments.linklyHint")}</Text> : null}
              {state.linklySetup ? (
                <LinklySetupCard
                  disabled={linklySetupDisabled}
                  locale={locale}
                  presenter={presenter}
                  state={state}
                />
              ) : (
                <View style={styles.actionRow}>
                  <ActionButton
                    disabled={linklySetupDisabled}
                    icon="check-network-outline"
                    label={t("action.test")}
                    onPress={() => void presenter.testPaymentProvider("linkly")}
                    testID="settings-linkly-test"
                  />
                </View>
              )}
            </ProviderPanelShell>
          ) : null}

          {zellerPanelOpen && zeller ? (
            <ZellerSettingsPanel
              // 「现在用哪家」和「是否已启用」都看已保存的通道，不看还没保存的草稿选择。
              activeProviderLabel={
                state.paymentProvider ? PAYMENT_PROVIDER_PANEL_LABELS[state.paymentProvider] : null
              }
              disabled={disabled || catalogRefreshRunning}
              enable={{
                active: state.paymentProvider === "zeller",
                // 与保存 Square / Linkly 设置同一套流程：危险操作确认 → 写本机配置 → 重载运行时。
                onEnable: () => void presenter.requestEnableZeller(),
              }}
              locale={locale}
              sdk={zeller.state}
              {...(zeller.hideSdkUi ? { hideSdkUi: zeller.hideSdkUi } : {})}
              {...(zeller.onOpenNetworkCheck ? { onOpenNetworkCheck: zeller.onOpenNetworkCheck } : {})}
            />
          ) : null}
        </PaymentTerminalShell>
      </PosKeyboardAwareScrollView>

      <SquarePickerModal
        closeLabel={t("square.closePicker")}
        disabled={squareSetupDisabled}
        disabledText={t("square.setupUnavailable")}
        emptyText={
          squarePicker === "device"
            ? t("square.devicesEmpty")
            : t("square.locationsEmpty")
        }
        failedText={t("square.loadFailed")}
        hint={
          squarePicker === "device"
            ? t("square.devicePickerHint")
            : t("square.locationPickerHint")
        }
        idleText={t("square.notLoaded")}
        loadingText={
          squarePicker === "device"
            ? t("square.loadingDevices")
            : t("square.loadingLocations")
        }
        onClose={() => setSquarePicker(null)}
        onReload={() => {
          if (squarePicker === "device") {
            void presenter.loadSquareDevices();
          } else {
            void presenter.loadSquareLocations();
          }
        }}
        onSelect={(id) => {
          if (squarePicker === "device") {
            presenter.setSquareDeviceId(id);
          } else {
            presenter.setSquareLocationId(id);
          }
          setSquarePicker(null);
        }}
        options={pickerOptions}
        reloadLabel={
          squarePicker === "device"
            ? t("square.loadDevices")
            : t("square.loadLocations")
        }
        resourceKind={pickerResource.kind}
        selectedId={
          squarePicker === "device"
            ? state.squareSetup.selectedDeviceId
            : state.squareSetup.selectedLocationId
        }
        testID={
          squarePicker === "device"
            ? "settings-square-device-picker"
            : "settings-square-location-picker"
        }
        title={
          squarePicker === "device"
            ? t("square.devicePickerTitle")
            : t("square.locationPickerTitle")
        }
        visible={squarePicker !== null}
      />

      {/* 启用与保存固定在分区底部，不随上面的配置内容滚动；Zeller 页签没有这两个操作。 */}
      {zellerPanelOpen && zeller ? null : (
        <View style={styles.paneFooter}>
          {modernLayout ? (
            <>
              <Text style={styles.paneFooterNote} testID="settings-payment-provider-state">
                {state.paymentProviderDraft === null ? t("payments.noneSelected")
                  : t(PAYMENT_PROVIDER_SELECTED_COPY[state.paymentProviderDraft])}
              </Text>
              <ActionButton
                disabled={disabled || (cardProviderPanel === "linkly" ? !linklySelectable : !squareAvailable && !state.squareSetup.available)}
                label={t(state.paymentProviderDraft === cardProviderPanel ? "payments.enabledProvider" : "payments.enableProvider", { provider: PAYMENT_PROVIDER_PANEL_LABELS[cardProviderPanel] })}
                onPress={() => presenter.setPaymentProvider(cardProviderPanel)}
                selected={state.paymentProviderDraft === cardProviderPanel}
                size="button"
                style={styles.paneFooterButton}
                testID={`settings-payment-provider-${cardProviderPanel}`}
              />
            </>
          ) : (
            <View style={styles.paneFooterSpacer} />
          )}
          <ActionButton
            disabled={disabled || catalogRefreshRunning || (modernLayout && state.paymentProviderDraft === null)}
            icon="content-save-outline"
            label={t("payments.save")}
            onPress={() => void presenter.savePaymentSettings()}
            size="button"
            style={styles.paneFooterButton}
            testID="settings-payment-save"
            tone="primary"
          />
        </View>
      )}
    </View>
  );
}

/** 页签式布局把所有刷卡终端放进一张卡；旧布局里 Square / Linkly 各一张卡并排。 */
function PaymentTerminalShell({
  children,
  modern,
  title,
}: Readonly<{ children: React.ReactNode; modern: boolean; title: string }>) {
  usePosThemeVersion();
  return modern ? (
    <SectionCard title={title}>{children}</SectionCard>
  ) : (
    <View style={styles.cardColumns}>{children}</View>
  );
}

/** 单个刷卡终端的配置区：页签式布局里直接排在卡片内，旧布局里自成一张卡。 */
function ProviderPanelShell({
  children,
  modern,
  title,
}: Readonly<{ children: React.ReactNode; modern: boolean; title: string }>) {
  usePosThemeVersion();
  return modern ? (
    <View style={styles.cardBody}>{children}</View>
  ) : (
    <SectionCard style={styles.columnCard} title={title}>
      {children}
    </SectionCard>
  );
}

/** 「支付设置」里可以查看的刷卡终端页签。 */
type PaymentProviderPanel = "square" | "linkly" | "zeller";

/** 每家通道被选中时的提示文案；用查表而不是二选一，新增通道时漏写会直接编译不过。 */
const PAYMENT_PROVIDER_SELECTED_COPY: Record<PaymentProviderPanel, SettingsCopyKey> = {
  square: "payments.squareSelected",
  linkly: "payments.linklySelected",
  zeller: "payments.zellerSelected",
};

const PAYMENT_PROVIDER_PANEL_LABELS: Record<PaymentProviderPanel, string> = {
  square: "Square",
  linkly: "Linkly",
  zeller: "Zeller",
};

type SquareSetupResourceKind =
  SettingsState["squareSetup"]["locations"]["kind"];

type SquarePickerOption = Readonly<{
  disabled: boolean;
  id: string;
  meta: string;
  name: string;
}>;

function SquareSummaryMetric({
  label,
  testID,
  value,
}: Readonly<{ label: string; testID: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.squareSummaryMetric} testID={testID}>
      <Text style={styles.metricLabel}>{label}</Text>
      <Text numberOfLines={2} style={styles.squareSummaryValue}>
        {value}
      </Text>
    </View>
  );
}

function LinklySetupCard({
  disabled,
  locale,
  presenter,
  state,
}: Readonly<{
  disabled: boolean;
  locale: SettingsLocale;
  presenter: SettingsScreenPresenter;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  const [pairCode, setPairCode] = useState("");
  const [pairTerminalId, setPairTerminalId] = useState("");
  const [pairingExpanded, setPairingExpanded] = useState(false);
  const [assignmentTerminalId, setAssignmentTerminalId] = useState("");
  const setup = state.linklySetup;
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  useEffect(() => {
    setPairCode("");
    setPairingExpanded(false);
    setPairTerminalId(
      setup?.terminals.value?.selectedTerminalId ?? "",
    );
  }, [
    state.linklyDraft.environment,
    setup?.pairCodeResetToken,
    setup?.terminals.value?.selectedTerminalId,
  ]);
  if (!setup) return null;
  const health = setup.health.value;
  const terminalSnapshot = setup.terminals.value;
  const lineManagementSupported = terminalSnapshot?.lineManagementSupported === true;
  const currentDeviceCode = state.device.deviceCode.trim();
  const pairTerminal = terminalSnapshot?.terminals.find(
    (terminal) => terminal.terminalId === pairTerminalId,
  );
  const healthReady = linklyHealthReady(state);
  const storeCredentialsReady = hasLinklyCloudCredentials(
    state,
    state.linklyDraft.environment,
  );
  const terminalPaired = linklyTerminalPaired(state);
  const healthStatus = linklyHealthStatusText(
    locale,
    setup.health.kind,
    health?.isReady,
  );

  const pairTestRunning = Object.values(setup.connectionTests).some(
    (test) => test.terminalId === pairTerminalId && test.kind === "running",
  );
  const pairInputDisabled = disabled || pairTerminal?.isBusy || pairTestRunning;
  const pairingForm = (
    <View style={lineManagementSupported ? styles.linklyPairForm : styles.linklyLegacyPairForm} testID="settings-linkly-pairing-form">
      {lineManagementSupported && pairTerminal ? (
        <>
          <View style={styles.linklyPairHeading}>
            <Text style={styles.linklyPairTitle}>
              {t("linkly.repairTitle", { terminal: `${pairTerminal.displayName} · Lane ${pairTerminal.laneNo}` })}
            </Text>
            <ActionButton compact label={t("action.cancel")} onPress={() => {
              setPairCode("");
              setPairingExpanded(false);
            }} testID="settings-linkly-pair-cancel" tone="quiet" />
          </View>
          <Text style={styles.fieldHint}>{t("linkly.repairHint")}</Text>
        </>
      ) : <FieldLabel label={t("linkly.pairCode")} />}
      <View style={styles.linklyPairInputRow}>
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("linkly.pairCode")}
          autoCapitalize="none"
          autoCorrect={false}
          editable={!pairInputDisabled}
          keyboardType="number-pad"
          maxLength={6}
          onChangeText={(value) => setPairCode(value.replace(/[^0-9]/gu, "").slice(0, 6))}
          placeholder={t("linkly.pairCodePlaceholder")}
          placeholderTextColor={posColors.placeholder}
          style={[styles.textInput, styles.linklyPairInput]}
          testID="settings-linkly-pair-code"
          value={pairCode}
        />
        <ActionButton
          disabled={Boolean(pairInputDisabled) || !storeCredentialsReady || !pairTerminal || pairCode.length !== 6}
          icon="link-variant-plus"
          label={t(lineManagementSupported ? "linkly.submitNewPairCode" : "linkly.pair")}
          onPress={() => {
            if (presenter.requestLinklyPair(pairTerminalId, pairCode)) setPairCode("");
          }}
          testID="settings-linkly-pair"
          tone="accent"
        />
      </View>
      <Text style={styles.fieldHint}>{t(lineManagementSupported ? "linkly.pairCodeSteps" : "linkly.pairCodeHint")}</Text>
    </View>
  );

  return (
    <View style={styles.linklySetup} testID="settings-linkly-setup">
      {!lineManagementSupported ? <View style={styles.squareSummaryRow}>
        <SquareSummaryMetric
          label={t("linkly.storeCredentials")}
          testID="settings-linkly-store-credentials"
          value={
            storeCredentialsReady
              ? `${t("linkly.statusReady")} · ${
                  health?.storeCode || t("linkly.notAvailable")
                }`
              : t("linkly.statusNotReady")
          }
        />
        <SquareSummaryMetric
          label={t("linkly.currentPairing")}
          testID="settings-linkly-current-pairing"
          value={
            terminalPaired
              ? `${t("linkly.statusPaired")} · ${
                  health?.deviceCode || t("linkly.notAvailable")
                }`
              : t("linkly.statusUnpaired")
          }
        />
        <SquareSummaryMetric
          label={t("linkly.backendReady")}
          testID="settings-linkly-backend-ready"
          value={healthStatus}
        />
      </View> : null}
      {/* 状态在左、刷新与测试在右；线路管理布局里只显示本机所选刷卡机的连接状态。 */}
      <View style={styles.linklyStatusRow}>
        {lineManagementSupported ? (
          <View style={styles.linklyStatusCopy}>
            <Text style={styles.metricLabel}>{t("linkly.backendReady")}</Text>
            <Text style={styles.linklyStatusValue} testID="settings-linkly-backend-ready">{terminalSnapshot?.selectedTerminalId ? healthStatus : t("linkly.noLocalSelection")}</Text>
          </View>
        ) : null}
        <ActionButton
          compact
          disabled={disabled || setup.health.kind === "loading"}
          icon="refresh"
          label={t("linkly.refresh")}
          onPress={() => void presenter.refreshLinklySetup()}
          testID="settings-linkly-refresh"
          tone="quiet"
        />
        <ActionButton
          compact
          disabled={disabled || !healthReady}
          icon="check-network-outline"
          label={t("action.test")}
          onPress={() => void presenter.testPaymentProvider("linkly")}
          testID="settings-linkly-test"
          tone="quiet"
        />
      </View>
      {setup.health.kind === "selection-required" ? (
        <Text
          style={styles.fieldHint}
          testID="settings-linkly-selection-required"
        >
          {t("linkly.selectionRequiredHint")}
        </Text>
      ) : null}
      {lineManagementSupported ? (
        <View style={styles.linklyDirectoryHeading}>
          <Text style={styles.linklyDirectoryTitle}>{t("linkly.terminals")}</Text>
          <Text style={styles.fieldHint}>{t("linkly.lineManagementHint")}</Text>
        </View>
      ) : <FieldLabel label={t("linkly.terminals")} />}
      {terminalSnapshot?.terminals.length ? (
        <View style={styles.linklyTerminalList} testID="settings-linkly-terminals">
          {terminalSnapshot.terminals.map((terminal) => {
            const selected =
              terminal.terminalId === terminalSnapshot.selectedTerminalId;
            const pairingTarget =
              (!lineManagementSupported || pairingExpanded) && !selected && terminal.terminalId === pairTerminalId;
            const terminalDisabled =
              disabled || setup.terminals.kind === "switching" || terminal.isBusy;
            const status = terminal.isBusy
              ? t("linkly.statusBusy")
              : terminal.pairingState === "NeedsRepair"
                ? t("linkly.statusNeedsRepair")
                : terminal.isReady && terminal.pairingState === "Ready"
                  ? t(lineManagementSupported ? "linkly.statusPaired" : "linkly.terminalReady")
                  : terminal.pairingState === "Unpaired"
                    ? t("linkly.statusUnpaired")
                    : t("linkly.statusUnknown");
            // 状态标签的颜色：忙碌 / 需重新配对用提醒色，已就绪用绿色，其余中性。
            const statusTone: PosPillTone = terminal.isBusy || terminal.pairingState === "NeedsRepair"
              ? "warning"
              : terminal.isReady && terminal.pairingState === "Ready"
                ? "good"
                : "neutral";
            return (
              <View key={terminal.terminalId}
                style={lineManagementSupported ? [styles.linklyLineCard, selected && styles.linklyLineCardSelected] : undefined}
                testID={`settings-linkly-card-${terminal.terminalId}`}
              >
                <View style={lineManagementSupported ? styles.linklyLineOverview : undefined}>
                {lineManagementSupported ? (
                  <MaterialCommunityIcons
                    accessible={false}
                    color={posColors.ink}
                    name="credit-card-wireless-outline"
                    size={32}
                  />
                ) : null}
                <PosPressable
                accessibilityLabel={`${terminal.displayName}. Lane ${terminal.laneNo}. ${status}${
                  selected
                    ? `. ${t("linkly.currentSelection")}`
                    : pairingTarget
                      ? `. ${t("linkly.pairTarget")}`
                      : ""
                }`}
                accessibilityRole="button"
                accessibilityState={{ disabled: terminalDisabled, selected }}
                disabled={terminalDisabled}
                onPress={() => {
                  setPairCode("");
                  setPairTerminalId(terminal.terminalId);
                  setPairingExpanded(lineManagementSupported);
                  if (
                    !lineManagementSupported &&
                    terminal.isReady &&
                    terminal.pairingState === "Ready"
                  ) {
                    void presenter.selectLinklyTerminal(terminal.terminalId);
                  }
                }}
                sound="tap"
                style={({ pressed }) => [
                  lineManagementSupported ? styles.linklyLineIdentity : styles.linklyTerminalOption,
                  !lineManagementSupported && selected && styles.linklyTerminalOptionSelected,
                  terminalDisabled && styles.dimmed,
                  pressed && !terminalDisabled && styles.pressed,
                ]}
                testID={`settings-linkly-terminal-${terminal.terminalId}`}
              >
                <View style={styles.linklyLineHeading}>
                  <Text style={styles.linklyTerminalName}>
                    {`${terminal.displayName} · Lane ${terminal.laneNo}`}
                  </Text>
                  <PosPill label={status} tone={statusTone} />
                  {selected || (!lineManagementSupported && pairingTarget) ? (
                    <PosPill
                      label={t(
                        selected
                          ? "linkly.currentSelection"
                          : "linkly.pairTarget",
                      )}
                      tone={selected ? "accent" : "info"}
                    />
                  ) : null}
                </View>
                {terminal.assignedDeviceCode ? (
                  <Text style={styles.linklyTerminalStatus}>
                    {t("linkly.assignedDevice", { deviceCode: terminal.assignedDeviceCode })}
                  </Text>
                ) : (
                  <Text style={styles.linklyTerminalStatus}>{t("linkly.unassigned")}</Text>
                )}
                </PosPressable>
                {lineManagementSupported ? (
                  <LinklyLineActions
                    currentDeviceCode={currentDeviceCode}
                    disabled={terminalDisabled}
                    expanded={assignmentTerminalId === terminal.terminalId}
                    locale={locale}
                    onAssign={(deviceCode) =>
                      presenter.requestLinklyTerminalAssignment(
                        terminal.terminalId,
                        deviceCode,
                      )
                    }
                    onChangeBinding={() => {
                      setPairCode("");
                      setPairingExpanded(false);
                      setAssignmentTerminalId((current) =>
                        current === terminal.terminalId
                          ? ""
                          : terminal.terminalId,
                      );
                    }}
                    onRepair={() => {
                      setPairCode("");
                      setPairTerminalId(terminal.terminalId);
                      setPairingExpanded(true);
                      setAssignmentTerminalId("");
                    }}
                    onTest={() =>
                      void presenter.testLinklyTerminalConnection(
                        terminal.terminalId,
                      )
                    }
                    snapshot={terminalSnapshot}
                    terminal={terminal}
                    testState={
                      Object.values(setup.connectionTests).find(
                        (item) => item.terminalId === terminal.terminalId,
                      ) ?? null
                    }
                  />
                ) : null}
                </View>
                {lineManagementSupported && pairingExpanded && pairTerminalId === terminal.terminalId
                  ? pairingForm : null}
              </View>
            );
          })}
        </View>
      ) : (
        <Text style={styles.fieldHint}>{t("linkly.noTerminals")}</Text>
      )}
      {terminalSnapshot && !lineManagementSupported ? (
        <Text style={styles.fieldHint} testID="settings-linkly-line-management-unavailable">
          {t("linkly.lineManagementUnavailable")}
        </Text>
      ) : null}
      {!lineManagementSupported ? pairingForm : null}
      <Text style={styles.linklySetupStatus} testID="settings-linkly-logon-status">
        {setup.logonTest.status === "passed"
          ? t("linkly.logonPassed")
          : t("linkly.logonRequired")}
      </Text>
      {!lineManagementSupported ? <Text style={styles.fieldHint} testID="settings-linkly-instructions">
        {t("linkly.instructions")}
      </Text> : null}
    </View>
  );
}

function LinklyLineActions({
  currentDeviceCode,
  disabled,
  expanded,
  locale,
  onAssign,
  onChangeBinding,
  onRepair,
  onTest,
  snapshot,
  terminal,
  testState,
}: Readonly<{
  currentDeviceCode: string;
  disabled: boolean;
  expanded: boolean;
  locale: SettingsLocale;
  onAssign(deviceCode: string | null): void;
  onChangeBinding(): void;
  onRepair(): void;
  onTest(): void;
  snapshot: NonNullable<SettingsState["linklySetup"]>["terminals"]["value"];
  terminal: NonNullable<SettingsState["linklySetup"]>["terminals"]["value"] extends infer S
    ? S extends { terminals: readonly (infer T)[] } ? T : never
    : never;
  testState: NonNullable<SettingsState["linklySetup"]>["connectionTests"][string] | null;
}>) {
  usePosThemeVersion();
  if (!snapshot) return null;
  const t = (key: SettingsCopyKey, values?: Readonly<Record<string, string | number>>) =>
    settingsText(locale, key, values);
  const lineActionDisabled = disabled || testState?.kind === "running";
  const targets = snapshot.devices ?? [];
  const validTargets = targets.filter((device) => device.isAvailable);
  const selfTargetAvailable = validTargets.some(
    (device) => device.deviceCode === currentDeviceCode,
  );
  const connectionResult = testState?.result;
  // 云端 Status 的 TF 表示要求签到；旧服务端将其归为 unreachable，不能误报断网。
  const logonRequired = connectionResult?.status === "unreachable" &&
    connectionResult.responseCode?.trim().toUpperCase() === "TF";
  const connectionStatus = connectionResult
    ? logonRequired
      ? t("linkly.connection.logon-required")
      : linklyConnectionStatusText(locale, connectionResult.status)
    : null;
  const lastHealthStatus = terminal.lastHealthAt
    ? linklyConnectionStatusText(locale, terminal.lastHealthStatus)
    : null;
  const connectionHint = terminal.isBusy
    ? t("linkly.failure.busy")
    : testState?.kind === "failed"
      ? t(`linkly.failure.${testState.failureReason ?? "unknown"}`)
      : logonRequired
        ? t("linkly.guidance.logon-required")
      : connectionResult && connectionResult.status !== "connected"
        ? t(`linkly.guidance.${connectionResult.status}`)
        : null;
  return (
    <View style={styles.linklyLineActionColumn}>
      {/* 行内操作排在行尾，每个 48 高；配对、分配、解绑仍先弹确认框。 */}
      <View style={styles.linklyLineActionRow}>
        <ActionButton compact disabled={lineActionDisabled || !terminal.isReady} label={t("linkly.testConnection")} onPress={onTest} testID={`settings-linkly-connection-test-${terminal.terminalId}`} tone="quiet" />
        <ActionButton compact disabled={lineActionDisabled || !terminal.isReady || !selfTargetAvailable} label={t("linkly.useThisLine")} onPress={() => onAssign(currentDeviceCode)} testID={`settings-linkly-use-self-${terminal.terminalId}`} />
        <ActionButton compact disabled={lineActionDisabled} label={t("linkly.changeBinding")} onPress={onChangeBinding} testID={`settings-linkly-change-binding-${terminal.terminalId}`} tone="quiet" />
        <ActionButton compact disabled={lineActionDisabled} label={t("linkly.repair")} onPress={onRepair} testID={`settings-linkly-repair-${terminal.terminalId}`} tone="quiet" />
      </View>
      <Text style={styles.linklyLineTestStatus}>
        {testState?.kind === "running"
          ? t("linkly.connectionTesting")
          : testState?.kind === "failed"
            ? t("linkly.connectionTestFailedTitle")
          : connectionResult
              ? `${connectionStatus} · ${formatLinklyHealthTime(locale, connectionResult.checkedAt)}`
              : terminal.lastHealthAt
                ? `${lastHealthStatus} · ${t("linkly.lastTest", { time: formatLinklyHealthTime(locale, terminal.lastHealthAt) })}`
                : t("linkly.connectionNotTested")}
      </Text>
      {connectionHint ? (
        <Text accessibilityLiveRegion="polite" style={styles.linklyConnectionWarning} testID={`settings-linkly-test-guidance-${terminal.terminalId}`}>
          {connectionHint}
        </Text>
      ) : null}
      {expanded ? (
        <View style={styles.linklyLineActionRow} testID={`settings-linkly-targets-${terminal.terminalId}`}>
        <ActionButton compact disabled={lineActionDisabled || !terminal.assignedDeviceCode} label={t("linkly.unbind")} onPress={() => onAssign(null)} testID={`settings-linkly-unbind-${terminal.terminalId}`} tone="danger" />
          {targets.map((device) => (
            <ActionButton
              compact
              disabled={lineActionDisabled || !device.isAvailable}
              key={device.deviceCode}
              label={`${device.deviceCode}${device.deviceCode === currentDeviceCode ? ` · ${t("linkly.thisDevice")}` : ""}${!device.isAvailable ? ` · ${t("linkly.targetUnavailable")}` : ""}`}
              onPress={() => onAssign(device.deviceCode)}
              testID={`settings-linkly-target-${terminal.terminalId}-${device.deviceCode}`}
            />
          ))}
        </View>
      ) : null}
    </View>
  );
}

function linklyConnectionStatusText(
  locale: SettingsLocale,
  status: string | null,
): string {
  const normalized = status?.trim().toLowerCase().replace(/[_\s-]/gu, "") ?? "";
  const key = normalized === "ready" ||
    normalized === "connected" ||
    normalized === "healthy"
    ? "linkly.connection.connected"
    : normalized === "unhealthy"
      ? "linkly.connection.failed"
    : normalized === "unreachable"
      ? "linkly.connection.unreachable"
      : normalized === "needsrepair"
        ? "linkly.connection.needs-repair"
        : normalized === "unknown"
          ? "linkly.connection.unknown"
          : "linkly.statusUnknown";
  return settingsText(locale, key);
}

function formatLinklyHealthTime(
  locale: SettingsLocale,
  value: string,
): string {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value.trim();
  return new Intl.DateTimeFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(parsed);
}

function SquareSelectionField({
  disabled,
  disabledText,
  emptyText,
  failedText,
  idleText,
  label,
  loadLabel,
  loadingText,
  onLoad,
  onSelect,
  primaryText,
  resourceKind,
  secondaryText,
  selectLabel,
  selectReady,
  testID,
}: Readonly<{
  disabled: boolean;
  disabledText: string;
  emptyText: string;
  failedText: string;
  idleText: string;
  label: string;
  loadLabel: string;
  loadingText: string;
  onLoad(): void;
  onSelect(): void;
  primaryText: string;
  resourceKind: SquareSetupResourceKind;
  secondaryText: string;
  selectLabel: string;
  selectReady: boolean;
  testID: string;
}>) {
  usePosThemeVersion();
  const loading = resourceKind === "loading";
  const selectDisabled = disabled || loading || !selectReady;
  const resourceText =
    resourceKind === "disabled"
      ? disabledText
      : resourceKind === "loading"
        ? loadingText
        : resourceKind === "failed"
          ? failedText
          : resourceKind === "empty"
            ? emptyText
            : resourceKind === "idle"
              ? idleText
              : secondaryText;
  return (
    <View style={styles.squareFieldGroup}>
      <FieldLabel label={label} />
      <View style={styles.squareSelectionRow} testID={`${testID}-row`}>
        <PosPressable
          accessibilityLabel={`${selectLabel}: ${primaryText}. ${resourceText}`}
          accessibilityRole="button"
          accessibilityState={{ disabled: selectDisabled }}
          disabled={selectDisabled}
          onPress={onSelect}
          sound="tap"
          style={({ pressed }) => [
            styles.squareSelectionValue,
            selectDisabled && styles.dimmed,
            pressed && !selectDisabled && styles.pressed,
          ]}
          testID={`${testID}-select`}
        >
          <Text numberOfLines={1} style={styles.squareSelectionPrimary}>
            {primaryText}
          </Text>
          <View style={styles.squareSelectionMetaRow}>
            {loading ? (
              <ActivityIndicator color={posColors.blue} size="small" />
            ) : null}
            <Text
              accessibilityLiveRegion="polite"
              numberOfLines={2}
              style={[
                styles.squareSelectionSecondary,
                resourceKind === "failed" && styles.squareSelectionError,
              ]}
            >
              {resourceText}
            </Text>
          </View>
        </PosPressable>
        <ActionButton
          disabled={disabled || loading}
          icon="refresh"
          label={loadLabel}
          onPress={onLoad}
          size="button"
          testID={`${testID}-load`}
          tone="quiet"
        />
      </View>
    </View>
  );
}

function SquareDeviceCodeList({
  codePending,
  disabled,
  emptyText,
  failedText,
  idleText,
  items,
  loadingText,
  onSelect,
  resourceKind,
  selectedId,
  selectedLabel,
  statusUnknown,
}: Readonly<{
  codePending: string;
  disabled: boolean;
  emptyText: string;
  failedText: string;
  idleText: string;
  items: readonly SettingsSquareDeviceCode[];
  loadingText: string;
  onSelect(id: string): void;
  resourceKind: SquareSetupResourceKind;
  selectedId: string;
  selectedLabel: string;
  statusUnknown: string;
}>) {
  usePosThemeVersion();
  if (resourceKind !== "ready") {
    const message =
      resourceKind === "loading"
        ? loadingText
        : resourceKind === "failed"
          ? failedText
          : resourceKind === "empty"
            ? emptyText
            : idleText;
    return (
      <View
        accessibilityLiveRegion="polite"
        style={styles.squareResourceState}
        testID="settings-square-device-code-state"
      >
        {resourceKind === "loading" ? (
          <ActivityIndicator color={posColors.blue} size="small" />
        ) : null}
        <Text
          style={[
            styles.squareResourceText,
            resourceKind === "failed" && styles.squareSelectionError,
          ]}
        >
          {message}
        </Text>
      </View>
    );
  }
  return (
    <View style={styles.squareDeviceCodeList}>
      {items.map((deviceCode) => {
        const selected = deviceCode.id === selectedId;
        return (
          <PosPressable
            accessibilityLabel={`${deviceCode.name}. ${
              deviceCode.code ?? codePending
            }. ${deviceCode.status ?? statusUnknown}`}
            accessibilityRole="button"
            accessibilityState={{ disabled, selected }}
            disabled={disabled}
            key={deviceCode.id}
            onPress={() => onSelect(deviceCode.id)}
            sound="tap"
            style={({ pressed }) => [
              styles.squareDeviceCodeRow,
              selected && styles.squareDeviceCodeSelected,
              disabled && styles.dimmed,
              pressed && !disabled && styles.pressed,
            ]}
            testID={`settings-square-device-code-${deviceCode.id}`}
          >
            <View style={styles.squareDeviceCodeIdentity}>
              <Text numberOfLines={1} style={styles.squareSelectionPrimary}>
                {deviceCode.name}
              </Text>
              <Text numberOfLines={2} style={styles.squareSelectionSecondary}>
                {deviceCode.code ?? codePending} · {deviceCode.status ?? statusUnknown}
              </Text>
            </View>
            {selected ? (
              <PosPill
                label={selectedLabel}
                style={styles.pillCentered}
                tone="accent"
              />
            ) : null}
          </PosPressable>
        );
      })}
    </View>
  );
}

function SquarePickerModal({
  closeLabel,
  disabled,
  disabledText,
  emptyText,
  failedText,
  hint,
  idleText,
  loadingText,
  onClose,
  onReload,
  onSelect,
  options,
  reloadLabel,
  resourceKind,
  selectedId,
  testID,
  title,
  visible,
}: Readonly<{
  closeLabel: string;
  disabled: boolean;
  disabledText: string;
  emptyText: string;
  failedText: string;
  hint: string;
  idleText: string;
  loadingText: string;
  onClose(): void;
  onReload(): void;
  onSelect(id: string): void;
  options: readonly SquarePickerOption[];
  reloadLabel: string;
  resourceKind: SquareSetupResourceKind;
  selectedId: string;
  testID: string;
  title: string;
  visible: boolean;
}>) {
  usePosThemeVersion();
  const loading = resourceKind === "loading";
  const resourceMessage =
    resourceKind === "disabled"
      ? disabledText
      : resourceKind === "failed"
        ? failedText
        : resourceKind === "empty"
          ? emptyText
          : idleText;
  return (
    <Modal
      animationType="fade"
      onRequestClose={onClose}
      supportedOrientations={["landscape-left", "landscape-right"]}
      testID={`${testID}-modal`}
      transparent
      visible={visible}
    >
      <View accessibilityViewIsModal style={styles.confirmationOverlay}>
        <PosPressable
          accessible={false}
          onPress={onClose}
          sound="navigate"
          style={styles.backdropDismissArea}
          testID={`${testID}-backdrop`}
        />
        <View style={styles.pickerSheet} testID={testID}>
          <View style={styles.printerPickerHeader}>
            <Text style={styles.printerPickerTitle}>{title}</Text>
            <Text style={styles.printerPickerHint}>{hint}</Text>
          </View>
          {loading ? (
            <View
              accessibilityLiveRegion="polite"
              style={styles.printerPickerProgress}
            >
              <ActivityIndicator color={posColors.blue} size="large" />
              <Text style={styles.printerPickerProgressText}>{loadingText}</Text>
            </View>
          ) : resourceKind === "ready" ? (
            <ScrollView
              contentContainerStyle={styles.squarePickerBody}
              style={styles.printerPickerScroll}
              testID={`${testID}-list`}
            >
              {options.map((option) => {
                const optionDisabled = disabled || option.disabled;
                const selected = option.id === selectedId;
                return (
                  <PosPressable
                    accessibilityLabel={`${option.name}. ${option.meta}`}
                    accessibilityRole="button"
                    accessibilityState={{ disabled: optionDisabled, selected }}
                    disabled={optionDisabled}
                    key={option.id}
                    onPress={() => onSelect(option.id)}
                    sound="tap"
                    style={({ pressed }) => [
                      styles.squarePickerOption,
                      selected && styles.squareDeviceCodeSelected,
                      optionDisabled && styles.dimmed,
                      pressed && !optionDisabled && styles.pressed,
                    ]}
                    testID={`${testID}-option-${option.id}`}
                  >
                    <Text numberOfLines={1} style={styles.squareSelectionPrimary}>
                      {selected ? `✓ ${option.name}` : option.name}
                    </Text>
                    <Text numberOfLines={2} style={styles.squareSelectionSecondary}>
                      {option.meta}
                    </Text>
                  </PosPressable>
                );
              })}
            </ScrollView>
          ) : (
            <Text
              accessibilityLiveRegion="polite"
              style={[
                styles.printerPickerEmpty,
                resourceKind === "failed" && styles.squareSelectionError,
              ]}
            >
              {resourceMessage}
            </Text>
          )}
          <View style={styles.printerPickerActions}>
            <ActionButton
              label={closeLabel}
              onPress={onClose}
              testID={`${testID}-close`}
              tone="quiet"
            />
            <ActionButton
              disabled={disabled || loading}
              icon="refresh"
              label={reloadLabel}
              onPress={onReload}
              testID={`${testID}-reload`}
            />
          </View>
        </View>
      </View>
    </Modal>
  );
}

function squareSelectionMeta(
  id: string,
  status: string | null | undefined,
  statusUnknown: string,
  code?: string | null,
): string {
  return [id, code, status ?? statusUnknown].filter(Boolean).join(" · ");
}

function squareTokenStatusText(
  locale: SettingsLocale,
  token: SettingsState["squareSetup"]["token"],
): string {
  if (token.kind === "loading") {
    return settingsText(locale, "square.tokenLoading");
  }
  if (token.kind === "failed") {
    return settingsText(locale, "square.tokenFailed");
  }
  if (token.kind === "disabled") {
    return settingsText(locale, "square.setupUnavailable");
  }
  if (token.kind !== "ready" || !token.value) {
    return settingsText(locale, "square.tokenIdle");
  }
  if (!token.value.configured) {
    return settingsText(locale, "square.tokenMissing");
  }
  return settingsText(
    locale,
    token.value.enabled ? "square.tokenReady" : "square.tokenDisabled",
  );
}

function linklyTerminalPaired(
  state: Pick<SettingsState, "linklySetup">,
): boolean {
  const health = state.linklySetup?.health;
  const checks = health?.value?.checks ?? [];
  return Boolean(
    health?.kind === "ready" &&
      checks.some(
        (check) =>
          check.code.trim().toUpperCase() === "TERMINAL_SECRET" &&
          check.isReady,
      ) &&
      checks.some(
        (check) =>
          check.code.trim().toUpperCase() === "TERMINAL_POS_ID" &&
          check.isReady,
      ),
  );
}

function linklyHealthReady(
  state: Pick<SettingsState, "linklySetup" | "linklyDraft">,
): boolean {
  return isLinklyHealthReady(state, state.linklyDraft.environment);
}

function linklySelectionReady(
  state: Pick<SettingsState, "linklySetup" | "linklyDraft">,
): boolean {
  return isLinklySetupReady(state, state.linklyDraft.environment);
}

function linklyHealthStatusText(
  locale: SettingsLocale,
  kind: NonNullable<SettingsState["linklySetup"]>["health"]["kind"],
  isReady: boolean | undefined,
): string {
  if (kind === "loading") return settingsText(locale, "linkly.statusLoading");
  if (kind === "failed") return settingsText(locale, "linkly.statusUnavailable");
  if (kind === "selection-required") {
    return settingsText(locale, "linkly.selectionRequired");
  }
  if (kind !== "ready") return settingsText(locale, "linkly.statusUnavailable");
  return settingsText(
    locale,
    isReady === true ? "linkly.statusReady" : "linkly.statusNotReady",
  );
}

function squareLocationPickerOption(
  location: SettingsSquareLocation,
): SquarePickerOption {
  return Object.freeze({
    disabled: false,
    id: location.id,
    meta: [location.id, location.status, location.currency, location.country]
      .filter(Boolean)
      .join(" · "),
    name: location.name,
  });
}

function squareDevicePickerOption(
  device: SettingsSquareDevice,
): SquarePickerOption {
  const normalizedStatus = device.status?.trim().toUpperCase() ?? "";
  return Object.freeze({
    // 明确标记后端禁用项；其余状态的最终选择约束仍由 Presenter 统一执行。
    disabled: normalizedStatus === "DISABLED",
    id: device.id,
    meta: [device.id, device.code, device.status].filter(Boolean).join(" · "),
    name: device.name,
  });
}

function ReceiptStoreProfileCard({
  disabled,
  locale,
  presenter,
  state,
}: Readonly<{
  disabled: boolean;
  locale: SettingsLocale;
  presenter: SettingsScreenPresenter;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  // 已应用总部下发资料（profileVersion > 0）：八项资料（含券使用说明、分期条款）只读，按钮改为「立即同步」。
  const managed = state.printer.profileVersion > 0;
  const fieldsDisabled = disabled || managed;
  // 只读（总部下发或无权限）时输入框换成禁用底色，一眼看出不能改。
  const inputStyle = [
    styles.textInput,
    styles.profileInput,
    fieldsDisabled && styles.textInputDisabled,
  ];
  const multilineStyle = [
    styles.multilineTextInput,
    fieldsDisabled && styles.textInputDisabled,
  ];
  return (
    <SectionCard
      accessory={
        <ActionButton
          compact
          disabled={disabled}
          icon={managed ? "sync" : "store-outline"}
          label={t(
            managed
              ? "peripherals.syncStoreProfile"
              : "peripherals.loadStoreProfile",
          )}
          onPress={() =>
            void (managed
              ? presenter.syncReceiptProfile()
              : presenter.loadReceiptProfile())
          }
          testID="settings-receipt-profile-load"
          tone="quiet"
        />
      }
      title={t("peripherals.storeProfile")}
    >
      {managed ? (
        <View
          style={styles.profileManagedNotice}
          testID="settings-receipt-profile-managed"
        >
          <Text style={styles.profileManagedText}>
            {t("peripherals.storeProfileManaged")}
          </Text>
          <Text style={styles.profileManagedText}>
            {t("peripherals.storeProfileVersion", {
              version: state.printer.profileVersion,
            })}
          </Text>
        </View>
      ) : null}
      {/* 八项资料三列排：品牌 / 门店 / 电话，地址（占两列）/ ABN，退货政策 / 代金券说明 / 分期条款。 */}
      <View style={styles.profileRow}>
        <View style={styles.profileField}>
          <FieldLabel label={t("field.receiptBrandName")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptBrandName")}
            autoCapitalize="words"
            autoCorrect={false}
            editable={!fieldsDisabled}
            maxLength={120}
            onChangeText={(value) => presenter.setReceiptBrandName(value)}
            style={inputStyle}
            testID="settings-receipt-brand-name"
            value={state.printer.brandName}
          />
        </View>
        <View style={styles.profileField}>
          <FieldLabel label={t("field.receiptStoreName")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptStoreName")}
            autoCapitalize="words"
            autoCorrect={false}
            editable={!fieldsDisabled}
            maxLength={120}
            onChangeText={(value) => presenter.setReceiptStoreName(value)}
            style={inputStyle}
            testID="settings-receipt-store-name"
            value={state.printer.storeName}
          />
        </View>
        <View style={styles.profileField}>
          <FieldLabel label={t("field.receiptPhone")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptPhone")}
            autoCapitalize="none"
            autoCorrect={false}
            editable={!fieldsDisabled}
            maxLength={60}
            onChangeText={(value) => presenter.setReceiptPhone(value)}
            style={inputStyle}
            testID="settings-receipt-phone"
            value={state.printer.phone}
          />
        </View>
      </View>
      <View style={styles.profileRow}>
        <View style={styles.profileFieldWide}>
          <FieldLabel label={t("field.receiptAddress")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptAddress")}
            editable={!fieldsDisabled}
            maxLength={240}
            multiline
            onChangeText={(value) => presenter.setReceiptAddress(value)}
            style={[inputStyle, styles.profileAddressInput]}
            testID="settings-receipt-address"
            textAlignVertical="top"
            value={state.printer.address}
          />
        </View>
        <View style={styles.profileField}>
          <FieldLabel label={t("field.receiptAbn")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptAbn")}
            autoCapitalize="characters"
            autoCorrect={false}
            editable={!fieldsDisabled}
            maxLength={32}
            onChangeText={(value) => presenter.setReceiptAbn(value)}
            style={inputStyle}
            testID="settings-receipt-abn"
            value={state.printer.abn}
          />
        </View>
      </View>
      <View style={styles.profileRow}>
        <View style={styles.profileField}>
          <FieldLabel label={t("field.receiptReturnPolicy")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptReturnPolicy")}
            editable={!fieldsDisabled}
            maxLength={500}
            multiline
            onChangeText={(value) => presenter.setReceiptReturnPolicy(value)}
            style={multilineStyle}
            testID="settings-receipt-return-policy"
            textAlignVertical="top"
            value={state.printer.returnPolicy}
          />
        </View>
        <View style={styles.profileField}>
          <FieldLabel label={t("field.receiptVoucherTerms")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptVoucherTerms")}
            editable={!fieldsDisabled}
            maxLength={RECEIPT_TERMS_TEXT_MAX_LENGTH}
            multiline
            onChangeText={(value) => presenter.setReceiptVoucherTerms(value)}
            placeholder={t("field.receiptTermsPlaceholder")}
            placeholderTextColor={posColors.placeholder}
            style={multilineStyle}
            testID="settings-receipt-voucher-terms"
            textAlignVertical="top"
            value={state.printer.voucherTerms}
          />
        </View>
        <View style={styles.profileField}>
          <FieldLabel label={t("field.receiptInstallmentTerms")} />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("field.receiptInstallmentTerms")}
            editable={!fieldsDisabled}
            maxLength={RECEIPT_TERMS_TEXT_MAX_LENGTH}
            multiline
            onChangeText={(value) => presenter.setReceiptInstallmentTerms(value)}
            placeholder={t("field.receiptTermsPlaceholder")}
            placeholderTextColor={posColors.placeholder}
            style={multilineStyle}
            testID="settings-receipt-installment-terms"
            textAlignVertical="top"
            value={state.printer.installmentTerms}
          />
        </View>
      </View>
      <Text
        style={styles.fieldHint}
        testID="settings-receipt-terms-hint"
      >
        {t("peripherals.receiptTermsHint")}
      </Text>
    </SectionCard>
  );
}

function PeripheralsPane({
  locale,
  presenter,
  state,
}: Readonly<{
  locale: SettingsLocale;
  presenter: SettingsScreenPresenter;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const printerDisabled =
    state.busy ||
    state.confirmation !== null ||
    !state.access.canConfigurePrinter;
  const displayDisabled =
    state.busy ||
    state.confirmation !== null ||
    !state.access.canManageCustomerDisplay;
  const [printerPickerVisible, setPrinterPickerVisible] = useState(false);
  const [printerPickerStatus, setPrinterPickerStatus] = useState<
    "connecting" | "ready" | "scanning"
  >("ready");
  const [printerPickerDevices, setPrinterPickerDevices] = useState<
    SettingsState["printerDevices"]
  >(Object.freeze([]));
  const [printerPickerError, setPrinterPickerError] =
    useState<SettingsStatusCode | null>(null);
  const printerPickerBusy = printerPickerStatus !== "ready";
  const printerPickerPermissionRequired =
    printerPickerError === "printer-bluetooth-permission-required";

  const scanPrinterDevices = async () => {
    setPrinterPickerVisible(true);
    setPrinterPickerStatus("scanning");
    setPrinterPickerDevices(Object.freeze([]));
    setPrinterPickerError(null);
    await presenter.scanPrinters();
    const nextState = presenter.getState();
    if (nextState.statusCode === "printer-scan-finished") {
      setPrinterPickerDevices(nextState.printerDevices);
    } else {
      setPrinterPickerError(nextState.statusCode);
    }
    setPrinterPickerStatus("ready");
  };

  const connectPrinterDevice = async (peripheralId: string) => {
    setPrinterPickerStatus("connecting");
    setPrinterPickerError(null);
    await presenter.connectPrinter(peripheralId);
    const nextStatus = presenter.getState().statusCode;
    setPrinterPickerStatus("ready");
    if (nextStatus === "printer-connected") {
      setPrinterPickerVisible(false);
      return;
    }
    setPrinterPickerError(nextStatus);
  };

  const closePrinterPicker = () => {
    if (!printerPickerBusy) setPrinterPickerVisible(false);
  };
  return (
    <View style={styles.pane} testID="settings-pane-content-peripherals">
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.content}
        style={styles.contentScroll}
        testID="settings-content-scroll"
      >
        <PaneHint text={t("peripherals.subtitle")} />
        <SectionCard
          title={t("peripherals.printer")}
          titleAccessory={
            <PosPill
              label={hardwareStatusText(locale, state.hardware.printerStatus)}
              testID="settings-printer-status"
              tone={hardwareStatusTone(state.hardware.printerStatus)}
            />
          }
        >
          {/* 第一行：打印 / 钱箱开关，纸宽与小票语言二选一。 */}
          <View style={styles.printerOptionRow}>
            <View style={styles.inlineSwitch}>
              <Text style={styles.switchLabel}>{t("peripherals.printing")}</Text>
              <PosSwitch
                accessibilityLabel={t("peripherals.printing")}
                accessibilityRole="switch"
                accessibilityState={{
                  checked: state.printer.printEnabled,
                  disabled: printerDisabled,
                }}
                disabled={printerDisabled}
                onValueChange={(enabled) => presenter.setPrinterEnabled(enabled)}
                style={styles.settingSwitch}
                testID="settings-printer-enabled"
                {...switchTint()}
                value={state.printer.printEnabled}
              />
            </View>
            <View style={styles.inlineSwitch}>
              <Text style={styles.switchLabel}>{t("peripherals.drawer")}</Text>
              <PosSwitch
                accessibilityLabel={t("peripherals.drawer")}
                accessibilityRole="switch"
                accessibilityState={{
                  checked: state.printer.drawerEnabled,
                  disabled: printerDisabled,
                }}
                disabled={printerDisabled}
                onValueChange={(enabled) => presenter.setDrawerEnabled(enabled)}
                style={styles.settingSwitch}
                testID="settings-drawer-enabled"
                {...switchTint()}
                value={state.printer.drawerEnabled}
              />
            </View>
            <PosSegmented<ReceiptPaper>
              disabled={printerDisabled}
              onChange={(paper) => presenter.setPrinterPaper(paper)}
              options={[
                { value: "80mm", label: "80mm", testID: "settings-printer-paper-80mm" },
                { value: "58mm", label: "58mm", testID: "settings-printer-paper-58mm" },
              ]}
              style={styles.printerOptionSegment}
              testID="settings-printer-paper"
              value={state.printer.paper}
            />
            <PosSegmented<ReceiptLocale>
              disabled={printerDisabled}
              onChange={(printerLocale) =>
                presenter.setPrinterLocale(printerLocale)
              }
              options={[
                {
                  value: "en",
                  label: t("printer.locale.en"),
                  testID: "settings-printer-locale-en",
                },
                {
                  value: "zh-CN",
                  label: t("printer.locale.zhCN"),
                  testID: "settings-printer-locale-zh-CN",
                },
              ]}
              style={styles.printerOptionSegment}
              testID="settings-printer-locale"
              value={state.printer.locale}
            />
          </View>
          {/* 第二行：设备 ID、扫描附近设备、保存。 */}
          <View style={styles.cardRow}>
            <View
              style={[
                styles.inputFrame,
                printerDisabled && styles.inputFrameDisabled,
              ]}
            >
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.mutedInk}
                name="bluetooth"
                size={24}
              />
              <Text style={styles.inputFramePrefix}>
                {t("field.peripheralId")}
              </Text>
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("field.printerPeripheralId")}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!printerDisabled}
                onChangeText={(value) => presenter.setPrinterPeripheralId(value)}
                style={styles.inputFrameField}
                testID="settings-printer-id"
                value={state.printer.peripheralId ?? ""}
              />
            </View>
            <ActionButton
              disabled={printerDisabled}
              icon="bluetooth-connect"
              label={t("peripherals.scanPrinters")}
              onPress={() => void scanPrinterDevices()}
              testID="settings-printer-scan"
            />
            <ActionButton
              disabled={printerDisabled}
              icon="content-save-outline"
              label={t("action.save")}
              onPress={() => void presenter.savePrinterSettings()}
              testID="settings-printer-save"
            />
          </View>
          {/* 第三行：两个测试与清除。 */}
          <View style={styles.actionRow}>
            <ActionButton
              disabled={printerDisabled}
              fill
              icon="printer-check"
              label={t("peripherals.testPrint")}
              onPress={() => void presenter.testPrinter()}
              testID="settings-printer-test"
            />
            <ActionButton
              disabled={
                printerDisabled ||
                !state.printer.peripheralId ||
                !state.printer.drawerEnabled
              }
              fill
              icon="archive-arrow-up-outline"
              label={t("peripherals.testDrawer")}
              onPress={() => void presenter.testCashDrawer()}
              testID="settings-drawer-test"
            />
            <ActionButton
              disabled={printerDisabled || !state.printer.peripheralId}
              fill
              icon="printer-off-outline"
              label={t("peripherals.clearSavedPrinter")}
              onPress={() => void presenter.clearSavedPrinter()}
              testID="settings-printer-clear-saved"
              tone="danger"
            />
          </View>
        </SectionCard>

        <ReceiptStoreProfileCard
          disabled={printerDisabled}
          locale={locale}
          presenter={presenter}
          state={state}
        />

        {/* 扫描器和客显各一条：状态在左，操作在右。 */}
        <View style={styles.cardColumns}>
          <SectionCard style={styles.columnCard}>
            <View style={styles.deviceLine}>
              <View style={styles.deviceLineCopy}>
                <Text style={styles.cardTitle}>{t("peripherals.scanner")}</Text>
                <Text style={styles.deviceLineStatus}>
                  {t("label.status", {
                    status: hardwareStatusText(locale, state.hardware.scannerStatus),
                  })}
                </Text>
              </View>
              <ActionButton
                disabled={
                  state.busy ||
                  state.confirmation !== null ||
                  !state.access.canTestScanner
                }
                icon="barcode-scan"
                label={t("peripherals.captureOneScan")}
                onPress={() => void presenter.testScanner()}
                testID="settings-scanner-test"
              />
            </View>
          </SectionCard>
          <SectionCard style={styles.columnCard}>
            <View style={styles.deviceLine}>
              <View style={styles.deviceLineCopy}>
                <Text style={styles.cardTitle}>
                  {t("peripherals.externalDisplay")}
                </Text>
                <Text style={styles.deviceLineStatus}>
                  {t("label.status", {
                    status: hardwareStatusText(locale, state.externalDisplay.status),
                  })}
                </Text>
              </View>
              <View style={styles.inlineSwitch}>
                <Text style={styles.switchLabel}>{t("action.enabled")}</Text>
                <PosSwitch
                  accessibilityLabel={t("action.enabled")}
                  accessibilityRole="switch"
                  accessibilityState={{
                    checked: state.externalDisplay.enabled,
                    disabled: displayDisabled || !state.externalDisplay.available,
                  }}
                  disabled={displayDisabled || !state.externalDisplay.available}
                  onValueChange={(enabled) =>
                    void presenter.setExternalDisplayEnabled(enabled)
                  }
                  style={styles.settingSwitch}
                  testID="settings-display-enabled"
                  {...switchTint()}
                  value={state.externalDisplay.enabled}
                />
              </View>
              <ActionButton
                disabled={displayDisabled || !state.externalDisplay.available}
                icon="monitor-eye"
                label={t("peripherals.testDisplay")}
                onPress={() => void presenter.testExternalDisplay()}
                testID="settings-display-test"
              />
            </View>
          </SectionCard>
        </View>
      </PosKeyboardAwareScrollView>

      <Modal
        animationType="fade"
        onRequestClose={closePrinterPicker}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="settings-printer-picker-modal"
        transparent
        visible={printerPickerVisible}
      >
        <View accessibilityViewIsModal style={styles.confirmationOverlay}>
          <PosPressable
            accessible={false}
            onPress={closePrinterPicker}
            sound="navigate"
            style={styles.backdropDismissArea}
            testID="settings-printer-picker-backdrop"
          />
          <View style={styles.pickerSheet} testID="settings-printer-picker">
            <View
              style={styles.printerPickerHeader}
              testID="settings-printer-picker-header"
            >
              <Text style={styles.printerPickerTitle}>
                {t("printer.pickerTitle")}
              </Text>
              <Text style={styles.printerPickerHint}>
                {t("printer.pickerHint")}
              </Text>
            </View>

            {printerPickerBusy ? (
              <View style={styles.printerPickerProgress}>
                <ActivityIndicator color={posColors.blue} size="large" />
                <Text style={styles.printerPickerProgressText}>
                  {t(
                    printerPickerStatus === "scanning"
                      ? "printer.scanning"
                      : "printer.connecting",
                  )}
                </Text>
              </View>
            ) : printerPickerError ? (
              <View style={styles.printerPickerErrorBody}>
                <Text style={styles.printerPickerError}>
                  {statusCopy(locale, printerPickerError)}
                </Text>
                {printerPickerPermissionRequired ? (
                  <>
                    <Text style={styles.printerPickerPermissionHint}>
                      {t("printer.bluetoothPermissionHint")}
                    </Text>
                    <View style={styles.printerPickerPermissionAction}>
                      <ActionButton
                        icon="open-in-new"
                        label={t("action.openSystemSettings")}
                        onPress={() => void Linking.openSettings()}
                        testID="settings-printer-open-system-settings"
                      />
                    </View>
                  </>
                ) : null}
              </View>
            ) : (
              <ScrollView
                contentContainerStyle={styles.printerPickerBody}
                style={styles.printerPickerScroll}
                testID="settings-printer-device-list"
              >
                {printerPickerDevices.length === 0 ? (
                  <Text style={styles.printerPickerEmpty}>
                    {t("printer.noneFound")}
                  </Text>
                ) : (
                  printerPickerDevices.map((device) => (
                    <View
                      key={device.id}
                      style={styles.deviceRow}
                      testID={`settings-printer-device-${device.id}`}
                    >
                      <View style={styles.deviceIdentity}>
                        <View style={styles.deviceDetailRow}>
                          <Text style={styles.deviceFieldLabel}>
                            {t("printer.deviceName")}
                          </Text>
                          <View style={styles.deviceNameRow}>
                            <Text
                              style={styles.deviceName}
                              testID={`settings-printer-device-name-${device.id}`}
                            >
                              {device.name}
                            </Text>
                            {device.preferred ? (
                              <Text
                                style={styles.preferredPrinterTag}
                                testID={`settings-printer-preferred-${device.id}`}
                              >
                                {t("printer.preferredN160")}
                              </Text>
                            ) : null}
                          </View>
                        </View>
                        <View style={styles.deviceDetailRow}>
                          <Text style={styles.deviceFieldLabel}>
                            {t("printer.deviceAddress")}
                          </Text>
                          <Text
                            style={styles.deviceMeta}
                            testID={`settings-printer-device-address-${device.id}`}
                          >
                            {device.id}
                          </Text>
                        </View>
                        <Text style={styles.deviceTransport}>
                          {device.transport}
                        </Text>
                      </View>
                      <ActionButton
                        disabled={printerPickerBusy}
                        icon="bluetooth-connect"
                        label={t("action.connect")}
                        onPress={() => void connectPrinterDevice(device.id)}
                        testID={`settings-printer-connect-${device.id}`}
                        tone="accent"
                      />
                    </View>
                  ))
                )}
              </ScrollView>
            )}

            <View
              style={styles.printerPickerActions}
              testID="settings-printer-picker-actions"
            >
              <ActionButton
                disabled={printerPickerBusy}
                label={t("action.cancel")}
                onPress={closePrinterPicker}
                testID="settings-printer-picker-close"
                tone="quiet"
              />
              <ActionButton
                disabled={printerPickerBusy}
                icon="bluetooth-connect"
                label={t("peripherals.scanPrinters")}
                onPress={() => void scanPrinterDevices()}
                testID="settings-printer-picker-rescan"
              />
            </View>
          </View>
        </View>
      </Modal>
    </View>
  );
}

type ReceiptPaper = SettingsState["printer"]["paper"];
type ReceiptLocale = SettingsState["printer"]["locale"];

function DevicePane({
  locale,
  onBack,
  onOpenSyncHistory,
  presenter,
  scanner,
  state,
}: Readonly<{
  locale: SettingsLocale;
  onBack: (() => void) | undefined;
  onOpenSyncHistory: (() => void) | undefined;
  presenter: SettingsScreenPresenter;
  scanner: HidScannerRouter | undefined;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  const [cameraVisible, setCameraVisible] = useState(false);
  useEffect(() => {
    if (!cameraVisible || !scanner) return undefined;
    return scanner.acquireContext("device-activation");
  }, [cameraVisible, scanner]);
  const disabled =
    state.busy ||
    state.confirmation !== null ||
    state.catalogRefresh.kind === "running" ||
    !state.access.canReregisterDevice;
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  return (
    <View style={styles.pane} testID="settings-pane-content-device">
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.content}
        style={styles.contentScroll}
        testID="settings-content-scroll"
      >
        <PaneHint text={t("device.subtitle")} />
        <SectionCard title={t("device.currentBinding")}>
          <View style={styles.metricRow}>
            <Metric
              label={t("metric.store")}
              value={state.device.storeCode || "—"}
            />
            <Metric
              label={t("metric.storeName")}
              value={state.device.storeName || "—"}
            />
            <Metric
              label={t("metric.device")}
              value={state.device.deviceCode || "—"}
            />
          </View>
        </SectionCard>
        <SectionCard title={t("device.reregister")}>
          {/* 开通码一行：输入框 + 扫码 + 预览目标分店。 */}
          <View style={styles.fieldGroup}>
            <FieldLabel label={t("field.deviceActivationCode")} />
            <View style={styles.cardRow}>
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("field.deviceActivationCode")}
                autoCapitalize="characters"
                autoCorrect={false}
                editable={!disabled}
                maxLength={96}
                onChangeText={(value) => presenter.setDeviceActivationCode(value)}
                secureTextEntry
                style={[
                  styles.textInput,
                  styles.textInputFill,
                  disabled && styles.textInputDisabled,
                ]}
                testID="settings-reregister-store"
                textContentType="oneTimeCode"
                value={state.deviceActivationCodeDraft}
              />
              <ActionButton
                disabled={disabled || !scanner}
                icon="qrcode-scan"
                label={t("device.scanActivationCode")}
                onPress={() => setCameraVisible(true)}
                testID="settings-reregister-scan"
              />
              <ActionButton
                disabled={disabled}
                icon="store-search-outline"
                label={t("device.previewActivationCode")}
                onPress={() => void presenter.previewDeviceReregistration()}
                testID="settings-reregister-preview"
              />
            </View>
          </View>
          <View style={styles.fieldGroup}>
            <FieldLabel label={t("field.terminalName")} />
            <PosKeyboardAwareTextInput
              accessibilityLabel={t("field.terminalName")}
              editable={!disabled}
              onChangeText={(value) => presenter.setTerminalName(value)}
              style={[styles.textInput, disabled && styles.textInputDisabled]}
              testID="settings-reregister-terminal"
              value={state.terminalNameDraft}
            />
          </View>
          {state.deviceActivationPreview ? (
            <View
              style={styles.reregisterPreview}
              testID="settings-reregister-preview-card"
            >
              <Text style={styles.dangerDescription}>
                {t("device.currentToTarget", {
                  current: state.device.storeCode || "—",
                  target: `${state.deviceActivationPreview.storeName} · ${state.deviceActivationPreview.storeCode}`,
                })}
              </Text>
              <View style={styles.metricRow}>
                <Metric
                  label={t("metric.platform")}
                  value={state.deviceActivationPreview.deviceSystem}
                />
                <Metric
                  label={t("metric.expires")}
                  value={formatActivationExpiry(
                    state.deviceActivationPreview.expiresAtUtc,
                    locale,
                  )}
                />
              </View>
              <View style={styles.actionRow}>
                <ActionButton
                  disabled={disabled}
                  icon="swap-horizontal"
                  label={t(
                    state.deviceReregistrationPreflight.kind === "checking"
                      ? "action.checking"
                      : "device.reviewReregistration",
                  )}
                  onPress={() => void presenter.requestDeviceReregistration()}
                  size="button"
                  testID="settings-reregister-request"
                  tone="danger"
                />
              </View>
            </View>
          ) : null}
          {state.deviceReregistrationPreflight.kind === "blocked" ||
          state.deviceReregistrationPreflight.kind === "failed" ? (
            <DeviceReregistrationBlockers
              blockers={
                state.deviceReregistrationPreflight.kind === "blocked"
                  ? state.deviceReregistrationPreflight.blockers
                  : []
              }
              disabled={disabled}
              failed={state.deviceReregistrationPreflight.kind === "failed"}
              locale={locale}
              onBack={onBack}
              onOpenSyncHistory={onOpenSyncHistory}
              onRecheck={() => void presenter.requestDeviceReregistration()}
            />
          ) : null}
        </SectionCard>
        <SectionCard title={t("device.resetRegistration")}>
          <Text style={styles.dangerDescription}>
            {t("device.resetRegistrationImpact")}
          </Text>
          <View style={styles.metricRow}>
            <Metric
              label={t("metric.store")}
              value={state.device.storeCode || "—"}
            />
            <Metric
              label={t("metric.device")}
              value={state.device.deviceCode || "—"}
            />
          </View>
          <View style={styles.actionRow}>
            <ActionButton
              disabled={disabled}
              icon="delete-alert-outline"
              label={t("device.reviewResetRegistration")}
              onPress={() => presenter.requestDeviceRegistrationReset()}
              size="button"
              testID="settings-device-registration-reset-request"
              tone="danger"
            />
          </View>
        </SectionCard>
      </PosKeyboardAwareScrollView>
      {scanner ? (
        <CameraScannerModal
          context="device-activation"
          onClose={() => setCameraVisible(false)}
          onScan={(value) => {
            presenter.setDeviceActivationCode(value);
            void presenter.previewDeviceReregistration();
          }}
          scanner={scanner}
          visible={cameraVisible}
        />
      ) : null}
    </View>
  );
}

const DEVICE_REREGISTRATION_BLOCKER_COPY = {
  "active-cart": {
    hint: "device.blockers.active-cart.hint",
    label: "device.blockers.active-cart",
  },
  "fulfilment-in-flight": {
    hint: "device.blockers.fulfilment-in-flight.hint",
    label: "device.blockers.fulfilment-in-flight",
  },
  "sync-or-audit-in-flight": {
    hint: "device.blockers.sync-or-audit-in-flight.hint",
    label: "device.blockers.sync-or-audit-in-flight",
  },
  "payment-configuration-sensitive-orders": {
    hint: "device.blockers.payment-configuration-sensitive-orders.hint",
    label: "device.blockers.payment-configuration-sensitive-orders",
  },
  "pending-durable-writes": {
    hint: "device.blockers.pending-durable-writes.hint",
    label: "device.blockers.pending-durable-writes",
  },
  "pending-returns": {
    hint: "device.blockers.pending-returns.hint",
    label: "device.blockers.pending-returns",
  },
  "pending-sales": {
    hint: "device.blockers.pending-sales.hint",
    label: "device.blockers.pending-sales",
  },
  "unresolved-payments": {
    hint: "device.blockers.unresolved-payments.hint",
    label: "device.blockers.unresolved-payments",
  },
} as const satisfies Record<
  PendingWorkBlocker["code"],
  Readonly<{ hint: SettingsCopyKey; label: SettingsCopyKey }>
>;

function DeviceReregistrationBlockers({
  blockers,
  disabled,
  failed,
  locale,
  onBack,
  onOpenSyncHistory,
  onRecheck,
}: Readonly<{
  blockers: readonly PendingWorkBlocker[];
  disabled: boolean;
  failed: boolean;
  locale: SettingsLocale;
  onBack: (() => void) | undefined;
  onOpenSyncHistory: (() => void) | undefined;
  onRecheck(): void;
}>) {
  usePosThemeVersion();
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const needsSales = blockers.some(
    (blocker) =>
      blocker.code === "active-cart" ||
      blocker.code === "unresolved-payments",
  );
  const needsSyncHistory = blockers.some(
    (blocker) =>
      blocker.code === "payment-configuration-sensitive-orders" ||
      blocker.code === "pending-returns" ||
      blocker.code === "pending-sales",
  );

  return (
    <View
      accessibilityLiveRegion="polite"
      accessibilityRole="alert"
      style={styles.reregistrationBlockerPanel}
      testID={
        failed
          ? "settings-reregister-preflight-failed"
          : "settings-reregister-blockers"
      }
    >
      <Text style={styles.reregistrationBlockerTitle}>
        {t(
          failed
            ? "device.blockers.failedTitle"
            : "device.blockers.title",
        )}
      </Text>
      <Text style={styles.reregistrationBlockerBody}>
        {t(
          failed
            ? "device.blockers.failedBody"
            : "device.blockers.body",
        )}
      </Text>
      {failed ? null : (
        <View style={styles.reregistrationBlockerList}>
          {blockers.map((blocker) => {
            const copy = DEVICE_REREGISTRATION_BLOCKER_COPY[blocker.code];
            return (
              <View
                key={blocker.code}
                style={styles.reregistrationBlockerRow}
                testID={`settings-reregister-blocker-${blocker.code}`}
              >
                <View style={styles.reregistrationBlockerCopy}>
                  <Text style={styles.reregistrationBlockerLabel}>
                    {t(copy.label)}
                  </Text>
                  <Text style={styles.reregistrationBlockerHint}>
                    {t(copy.hint)}
                  </Text>
                </View>
                <Text style={styles.reregistrationBlockerValue}>
                  {blocker.kind === "count"
                    ? t("device.blockers.count", { count: blocker.count })
                    : t("device.blockers.inProgress")}
                </Text>
              </View>
            );
          })}
        </View>
      )}
      {needsSyncHistory && !onOpenSyncHistory ? (
        <Text style={styles.reregistrationSupervisorHint}>
          {t("device.blockers.contactSupervisor")}
        </Text>
      ) : null}
      <View style={styles.actionRow}>
        {needsSales && onBack ? (
          <ActionButton
            disabled={disabled}
            icon="arrow-left"
            label={t("action.backToSales")}
            onPress={onBack}
            testID="settings-reregister-back-to-sales"
          />
        ) : null}
        {needsSyncHistory && onOpenSyncHistory ? (
          <ActionButton
            disabled={disabled}
            icon="sync"
            label={t("device.blockers.openSyncHistory")}
            onPress={onOpenSyncHistory}
            testID="settings-reregister-open-sync-history"
          />
        ) : null}
        <ActionButton
          disabled={disabled}
          icon="refresh"
          label={t("device.blockers.recheck")}
          onPress={onRecheck}
          testID="settings-reregister-recheck"
        />
      </View>
    </View>
  );
}

function HardwarePane({
  locale,
  presenter,
  state,
}: Readonly<{
  locale: SettingsLocale;
  presenter: SettingsScreenPresenter;
  state: SettingsState;
}>) {
  usePosThemeVersion();
  const t = (key: SettingsCopyKey) => settingsText(locale, key);
  return (
    <View style={styles.pane} testID="settings-pane-content-hardware">
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.content}
        style={styles.contentScroll}
        testID="settings-content-scroll"
      >
        <PaneHint text={t("hardware.subtitle")} />
        {/* 三张测试卡各带一个测试按钮：打印机、扫描器、客显。 */}
        <View style={styles.cardColumns}>
          <HardwareCard
            actionLabel={t("hardware.printTest")}
            disabled={
              state.busy ||
              state.confirmation !== null ||
              !state.access.canConfigurePrinter
            }
            icon="printer-outline"
            onPress={() => void presenter.testPrinter()}
            status={state.hardware.printerStatus}
            statusText={hardwareStatusText(locale, state.hardware.printerStatus)}
            testID="settings-hardware-printer"
            title={t("peripherals.printer")}
          />
          <HardwareCard
            actionLabel={t("hardware.captureScan")}
            disabled={
              state.busy ||
              state.confirmation !== null ||
              !state.access.canTestScanner
            }
            icon="barcode-scan"
            onPress={() => void presenter.testScanner()}
            status={state.hardware.scannerStatus}
            statusText={hardwareStatusText(locale, state.hardware.scannerStatus)}
            testID="settings-hardware-scanner"
            title={t("peripherals.scanner")}
          />
          <HardwareCard
            actionLabel={t("hardware.showTest")}
            disabled={
              state.busy ||
              state.confirmation !== null ||
              !state.access.canManageCustomerDisplay ||
              !state.externalDisplay.available
            }
            icon="monitor"
            onPress={() => void presenter.testExternalDisplay()}
            status={state.hardware.externalDisplayStatus}
            statusText={hardwareStatusText(
              locale,
              state.hardware.externalDisplayStatus,
            )}
            testID="settings-hardware-display"
            title={t("hardware.display")}
          />
        </View>
        <SectionCard title={t("hardware.lastScan")}>
          <Text style={styles.scannerValue}>
            {state.hardware.lastScannerValue ?? t("hardware.waitingForTest")}
          </Text>
        </SectionCard>
      </PosKeyboardAwareScrollView>
    </View>
  );
}

function ConfirmationCard({
  busy,
  confirmation,
  locale,
  onCancel,
  onConfirm,
  storeCode,
  deviceCode,
}: Readonly<{
  busy: boolean;
  confirmation: SettingsDangerousConfirmation;
  locale: SettingsLocale;
  onCancel(): void;
  onConfirm(employeeBarcode?: string): void;
  storeCode: string;
  deviceCode: string;
}>) {
  usePosThemeVersion();
  const requiresEmployeeScan =
    confirmation.kind === "reset-device-registration";
  const [employeeBarcode, setEmployeeBarcode] = useState("");
  const submit = (): void => {
    const scannedBarcode = employeeBarcode.trim();
    if (requiresEmployeeScan) {
      setEmployeeBarcode("");
      onConfirm(scannedBarcode);
      return;
    }
    onConfirm();
  };
  return (
    <View
      accessibilityRole="alert"
      style={styles.confirmation}
      testID="settings-confirmation"
    >
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.confirmationScrollContent}
        style={styles.confirmationScroll}
        testID="settings-confirmation-scroll"
      >
        <View style={styles.confirmationCopy}>
        <Text style={styles.confirmationTitle}>
          {confirmationTitle(locale, confirmation, storeCode, deviceCode)}
        </Text>
        {confirmation.kind === "pair-linkly" ? (
          <Text style={styles.confirmationBody} testID="settings-linkly-pairing-details">
            {settingsText(locale, "confirmation.pairLinklyDetails")}
          </Text>
        ) : null}
        {confirmation.kind === "assign-linkly-terminal" ? (
          <Text
            style={styles.confirmationBody}
            testID="settings-linkly-assignment-details"
          >
            {settingsText(
              locale,
              confirmation.targetDeviceCode === null
                ? "confirmation.unbindLinklyDetails"
                : "confirmation.assignLinklyDetails",
              {
                // 确认框直接呈现同一 CAS 快照中的源设备和目标原线路。
                sourceDevice: confirmation.input.assignedDeviceCode ??
                  settingsText(locale, "confirmation.linklyUnassigned"),
                targetDevice: confirmation.targetDeviceCode ?? "—",
                replacedTerminal: confirmation.replacedTerminalLabel ??
                  settingsText(locale, "confirmation.linklyNoTargetLine"),
              },
            )}
          </Text>
        ) : null}
        <Text style={styles.confirmationBody}>
          {settingsText(
            locale,
            requiresEmployeeScan
              ? "confirmation.resetDeviceBody"
              : "confirmation.body",
          )}
        </Text>
        {requiresEmployeeScan ? (
          <View style={styles.confirmationScan}>
            <FieldLabel label={settingsText(locale, "field.employeeBarcode")} />
            <PosKeyboardAwareTextInput
              accessibilityLabel={settingsText(locale, "field.employeeBarcode")}
              autoCapitalize="none"
              autoCorrect={false}
              autoFocus
              editable={!busy}
              onChangeText={setEmployeeBarcode}
              secureTextEntry
              style={styles.textInput}
              testID="settings-device-registration-reset-barcode"
              value={employeeBarcode}
            />
            <Text style={styles.fieldHint}>
              {settingsText(locale, "field.employeeBarcodeHint")}
            </Text>
          </View>
        ) : null}
        </View>
        <View style={styles.confirmationActions}>
          <ActionButton
            disabled={busy}
            label={settingsText(locale, "action.cancel")}
            onPress={onCancel}
            size="button"
            testID="settings-confirm-cancel"
            tone="quiet"
          />
          <ActionButton
            disabled={busy || (requiresEmployeeScan && !employeeBarcode.trim())}
            label={settingsText(
              locale,
              busy ? "action.checking" : "action.confirm",
            )}
            onPress={submit}
            size="button"
            testID="settings-confirm"
            tone="danger"
          />
        </View>
      </PosKeyboardAwareScrollView>
    </View>
  );
}

function EnvironmentSelector({
  disabled,
  environment,
  locale,
  onSelect,
  prefix,
  style,
}: Readonly<{
  disabled: boolean;
  environment: PaymentEnvironment;
  locale: SettingsLocale;
  onSelect(environment: PaymentEnvironment): void;
  prefix: string;
  style?: StyleProp<ViewStyle>;
}>) {
  usePosThemeVersion();
  return (
    <PosSegmented<PaymentEnvironment>
      disabled={disabled}
      onChange={onSelect}
      options={[
        {
          value: "Production",
          label: settingsText(locale, "environment.production"),
          testID: `${prefix}-production`,
        },
        {
          value: "Sandbox",
          label: settingsText(locale, "environment.sandbox"),
          testID: `${prefix}-sandbox`,
        },
      ]}
      style={style}
      value={environment}
    />
  );
}

function HardwareCard({
  actionLabel,
  disabled,
  icon,
  onPress,
  status,
  statusText,
  testID,
  title,
}: Readonly<{
  actionLabel: string;
  disabled: boolean;
  icon: PosIconName;
  onPress(): void;
  status: string;
  statusText: string;
  testID: string;
  title: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.sectionCard, styles.columnCard]}>
      <View style={styles.hardwareHeading}>
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.ink}
          name={icon}
          size={30}
        />
        <View style={styles.deviceLineCopy}>
          <Text style={styles.cardTitle}>{title}</Text>
          <Text
            style={[
              styles.hardwareStatus,
              status === "connected" || status === "ready"
                ? styles.hardwareStatusReady
                : status === "disconnected"
                  ? styles.hardwareStatusDisconnected
                  : styles.hardwareStatusUnavailable,
            ]}
            testID={`${testID}-status`}
          >
            {statusText}
          </Text>
        </View>
      </View>
      <ActionButton
        disabled={disabled}
        label={actionLabel}
        onPress={onPress}
        size="button"
        testID={testID}
      />
    </View>
  );
}

/** 分区顶部的一句说明（原来的分区副标题）；分区名称已经在左侧目录里高亮，不再重复大标题。 */
function PaneHint({ text }: Readonly<{ text: string }>) {
  usePosThemeVersion();
  return <Text style={styles.paneHint}>{text}</Text>;
}

/** 每组设置一张卡：标题（可带状态标签）、一句说明、右上角操作，下面直接是控件。 */
function SectionCard({
  accessory,
  children,
  hint,
  style,
  testID,
  title,
  titleAccessory,
}: Readonly<{
  /** 卡片右上角的操作（例如「从门店载入」）。 */
  accessory?: React.ReactNode;
  children: React.ReactNode;
  hint?: string;
  style?: StyleProp<ViewStyle>;
  testID?: string;
  title?: string;
  /** 紧跟标题的状态标签。 */
  titleAccessory?: React.ReactNode;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.sectionCard, style]} testID={testID}>
      {title || accessory ? (
        <View style={styles.cardHeader}>
          <View style={styles.cardHeaderCopy}>
            <View style={styles.cardTitleRow}>
              {title ? <Text style={styles.cardTitle}>{title}</Text> : null}
              {titleAccessory}
            </View>
            {hint ? <Text style={styles.cardHint}>{hint}</Text> : null}
          </View>
          {accessory}
        </View>
      ) : null}
      {children}
    </View>
  );
}

function Availability({
  available,
  blockerCode,
  locale,
}: Readonly<{
  available: boolean;
  blockerCode: string | null;
  locale: SettingsLocale;
}>) {
  usePosThemeVersion();
  return (
    <View
      style={[
        styles.availability,
        available ? styles.availabilityReady : styles.availabilityUnavailable,
      ]}
    >
      <Text
        style={[
          styles.availabilityText,
          available
            ? styles.availabilityTextReady
            : styles.availabilityTextUnavailable,
        ]}
      >
        {available
          ? settingsText(locale, "availability.ready")
          : settingsText(locale, "availability.unavailable", {
              blocker: blockerCode ? ` · ${blockerCode}` : "",
            })}
      </Text>
    </View>
  );
}

/** 只读信息：标签在前、数值在后排成一行，多项并排时自动换行。 */
function Metric({ label, value }: Readonly<{ label: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.metric}>
      <Text style={styles.metricLabel}>{label}</Text>
      <Text numberOfLines={2} style={styles.metricValue}>
        {value}
      </Text>
    </View>
  );
}

function FieldLabel({ label }: Readonly<{ label: string }>) {
  usePosThemeVersion();
  return <Text style={styles.fieldLabel}>{label}</Text>;
}

function EmptyPanel({
  busy = false,
  message,
  testID,
}: Readonly<{ busy?: boolean; message: string; testID: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.emptyPanel} testID={testID}>
      {busy ? <ActivityIndicator color={posColors.accent} size="large" /> : null}
      <Text style={styles.emptyText}>{message}</Text>
    </View>
  );
}

/**
 * 目录更新期间支付页签里的操作（保存支付设置、配对 / 启用 Zeller 等）整块禁用。
 * 按钮变灰却不说原因会让人以为是故障，所以在页签顶部明说，更新结束后自动消失。
 */
function CatalogBusyNotice({ text }: Readonly<{ text: string }>) {
  usePosThemeVersion();
  return (
    <View
      accessibilityLiveRegion="polite"
      style={[styles.statusBanner, styles.statusInfo]}
      testID="settings-payments-catalog-busy"
    >
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.yellow}
        name="progress-clock"
        size={22}
      />
      <Text style={styles.statusText}>{text}</Text>
    </View>
  );
}

function StatusBanner({
  locale,
  statusCode,
  receiptProfileVersion,
}: Readonly<{
  locale: SettingsLocale;
  statusCode: SettingsStatusCode;
  /** 「已更新到版本 N」文案用：同步成功后本机已应用的下发版本。 */
  receiptProfileVersion: number;
}>) {
  usePosThemeVersion();
  const success = isSuccessStatus(statusCode);
  return (
    <View
      accessibilityLiveRegion="polite"
      style={[
        styles.statusBanner,
        success ? styles.statusSuccess : styles.statusWarning,
      ]}
      testID="settings-status"
    >
      <MaterialCommunityIcons
        accessible={false}
        color={success ? posColors.green : posColors.red}
        name={success ? "check-circle-outline" : "alert-circle-outline"}
        size={22}
      />
      <Text style={styles.statusText}>
        {statusCopy(locale, statusCode, { version: receiptProfileVersion })}
      </Text>
    </View>
  );
}

function hardwareStatusText(locale: SettingsLocale, status: string): string {
  const statusKey: Readonly<Record<string, SettingsCopyKey>> = {
    connected: "hardware.connected",
    ready: "hardware.ready",
    disconnected: "hardware.disconnected",
    unavailable: "hardware.unavailable",
  };
  return statusKey[status] ? settingsText(locale, statusKey[status]) : status;
}

/** 硬件状态对应的标签颜色：已连接 / 就绪为绿色，断开为红色，其余（不可用、未知）中性。 */
function hardwareStatusTone(status: string): PosPillTone {
  if (status === "connected" || status === "ready") return "good";
  return status === "disconnected" ? "danger" : "neutral";
}

/** 开关统一配色：开 = 主操作色，关 = 灰色槽。渲染时读取，换主题后跟着变。 */
function switchTint() {
  return {
    ios_backgroundColor: posColors.disabledBorder,
    trackColor: { false: posColors.disabledBorder, true: posColors.accent },
    // 开关本体只有 31 高，44 的触控目标靠扩大点击范围得到，不能靠撑大 Switch 自己的尺寸：
    // iOS 上 ios_backgroundColor 会铺满整个 Switch 视图，撑大后就在轨道后面露出一块灰色圆角底。
    hitSlop: { bottom: 7, left: 4, right: 4, top: 7 },
  };
}

/** 左侧分区目录的一项：66 高、带图标，当前分区深色填充。 */
function SectionNavButton({
  disabled,
  icon,
  label,
  onPress,
  selected,
  testID,
}: Readonly<{
  disabled: boolean;
  icon: PosIconName;
  label: string;
  onPress(): void;
  selected: boolean;
  testID: string;
}>) {
  usePosThemeVersion();
  const foreground = selected
    ? posColors.onInverse
    : disabled
      ? posColors.disabledInk
      : posColors.ink;
  return (
    <PosPressable
      accessibilityLabel={label}
      accessibilityRole="button"
      accessibilityState={{ disabled, selected }}
      disabled={disabled}
      onPress={onPress}
      sound="navigate"
      style={({ pressed }) => [
        styles.navButton,
        selected && styles.navButtonSelected,
        pressed && !disabled && !selected && styles.navButtonPressed,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={foreground}
        name={icon}
        size={26}
      />
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.8}
        numberOfLines={1}
        style={[styles.navButtonLabel, { color: foreground }]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

/** 二选一里的一个选项（旧布局的刷卡终端提供方）：选中时深色填充并带勾。 */
function ToggleButton({
  disabled,
  label,
  onPress,
  selected,
  testID,
}: Readonly<{
  disabled: boolean;
  label: string;
  onPress(): void;
  selected: boolean;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <ActionButton
      disabled={disabled}
      fill
      label={label}
      onPress={onPress}
      selected={selected}
      selectedTone="inverse"
      testID={testID}
    />
  );
}

type ActionButtonTone = "accent" | "danger" | "primary" | "quiet" | "secondary";

const ACTION_BUTTON_TONES: Record<ActionButtonTone, PosButtonTone> = {
  accent: "info",
  danger: "danger",
  primary: "primary",
  quiet: "quiet",
  secondary: "secondary",
};

/**
 * 设置页按钮：统一走 PosButton。常规 56 高，行内次要操作（compact）48 高。
 * 危险操作沿用 danger 提示音；selected 只用于「已选中 / 已启用」的状态展示。
 */
function ActionButton({
  compact = false,
  disabled = false,
  fill = false,
  icon,
  label,
  onPress,
  selected = false,
  selectedTone = "success",
  size,
  sound,
  style,
  testID,
  tone = "secondary",
}: Readonly<{
  compact?: boolean;
  disabled?: boolean;
  fill?: boolean;
  icon?: PosIconName;
  label: string;
  onPress(): void;
  selected?: boolean;
  selectedTone?: PosButtonTone;
  size?: PosButtonSize;
  sound?: TouchSoundKind;
  style?: StyleProp<ViewStyle>;
  testID: string;
  tone?: ActionButtonTone;
}>) {
  usePosThemeVersion();
  const resolvedIcon = selected ? "check" : icon;
  return (
    <PosButton
      accessibilityState={{ disabled, selected }}
      disabled={disabled}
      fill={fill}
      label={label}
      onPress={onPress}
      size={size ?? (compact ? "small" : "medium")}
      sound={sound ?? (tone === "danger" ? "danger" : "tap")}
      style={style}
      testID={testID}
      tone={selected ? selectedTone : ACTION_BUTTON_TONES[tone]}
      {...(resolvedIcon ? { icon: resolvedIcon } : {})}
    />
  );
}

function confirmationTitle(
  locale: SettingsLocale,
  confirmation: SettingsDangerousConfirmation,
  storeCode: string,
  deviceCode: string,
): string {
  switch (confirmation.kind) {
    case "change-api-address":
      return settingsText(locale, "confirmation.changeApiAddress", {
        apiBaseUrl: confirmation.apiBaseUrl,
      });
    case "change-payment-settings":
      return settingsText(locale, "confirmation.changePaymentSettings");
    case "pair-linkly":
      return settingsText(locale, "confirmation.pairLinkly", { terminal: confirmation.terminalLabel ?? confirmation.terminalId });
    case "assign-linkly-terminal":
      return settingsText(
        locale,
        confirmation.targetDeviceCode === null
          ? "confirmation.unbindLinklyTerminal"
          : confirmation.replacedTerminalLabel
            ? "confirmation.replaceLinklyTerminal"
            : "confirmation.assignLinklyTerminal",
        {
          terminal: confirmation.terminalLabel,
          deviceCode: confirmation.targetDeviceCode ?? "—",
          replacedTerminal: confirmation.replacedTerminalLabel ?? "—",
        },
      );
    case "reset-catalog":
      return settingsText(locale, "confirmation.resetCatalog");
    case "reregister-device":
      return settingsText(locale, "confirmation.reregisterDevice", {
        currentStoreCode: confirmation.currentStoreCode || "—",
        targetStoreCode: confirmation.preview.storeCode,
        targetStoreName: confirmation.preview.storeName,
        deviceSystem: confirmation.preview.deviceSystem,
        expiresAt: formatActivationExpiry(
          confirmation.preview.expiresAtUtc,
          locale,
        ),
      });
    case "reset-device-registration":
      return settingsText(locale, "confirmation.resetDeviceRegistration", {
        storeCode: storeCode || "—",
        deviceCode: deviceCode || "—",
      });
    default:
      return settingsText(locale, "confirmation.restartApp");
  }
}

function statusCopy(
  locale: SettingsLocale,
  statusCode: SettingsStatusCode,
  values?: Readonly<Record<string, string | number>>,
): string {
  return settingsText(locale, `status.${statusCode}` as SettingsCopyKey, values);
}

function isSuccessStatus(statusCode: SettingsStatusCode): boolean {
  return [
    "api-address-saved",
    "api-health-check-passed",
    "app-restart-requested",
    "app-update-checked",
    "cash-drawer-test-passed",
    "catalog-downloaded",
    "catalog-reset",
    "device-reregister-started",
    "device-registration-reset-completed",
    "display-setting-saved",
    "display-test-passed",
    "linkly-paired",
    "payment-settings-saved",
    "payment-test-passed",
    "printer-connected",
    "printer-cleared",
    "printer-scan-finished",
    "printer-settings-saved",
    "printer-test-passed",
    "receipt-profile-loaded",
    "receipt-profile-synced",
    "receipt-profile-up-to-date",
    "scanner-test-passed",
  ].includes(statusCode);
}

function compactDate(value: string | null): string {
  if (!value) return "—";
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf())
    ? value
    : parsed.toISOString().replace("T", " ").slice(0, 16);
}

function formatPercent(percent: number): string {
  return String(Math.round(percent * 100) / 100);
}

function formatElapsedMilliseconds(elapsedMilliseconds: number): string {
  const totalSeconds = Math.max(
    0,
    Math.floor(elapsedMilliseconds / 1_000),
  );
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(
    2,
    "0",
  )}`;
}

function formatActivationExpiry(
  expiresAtUtc: string,
  locale: SettingsLocale,
): string {
  const date = new Date(expiresAtUtc);
  return Number.isNaN(date.getTime())
    ? expiresAtUtc
    : date.toLocaleString(locale === "zh" ? "zh-CN" : "en-AU");
}

const styles = createPosStyles((colors) => ({
  // ---- 页面骨架：左侧分区目录 + 右侧工作区 ----
  page: { flex: 1, gap: 12, minHeight: 0, padding: 12 },
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
  },
  navigation: { gap: 8, width: 232 },
  navigationCompact: { width: 184 },
  navigationTitle: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
    marginHorizontal: 2,
    marginTop: 2,
  },
  navButton: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: 66,
    paddingHorizontal: 14,
  },
  navButtonSelected: {
    backgroundColor: colors.inverseSurface,
    borderColor: colors.inverseSurface,
  },
  navButtonPressed: { backgroundColor: colors.surfaceAlt },
  navButtonLabel: { flexShrink: 1, fontSize: 16.5, fontWeight: "900" },
  deviceBadge: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 2,
    marginTop: "auto",
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  badgeLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
    marginBottom: 4,
  },
  badgeValue: { color: colors.ink, fontSize: 16, fontWeight: "900" },
  badgeMeta: { color: colors.mutedInk, fontSize: 13, fontWeight: "600" },
  workspaceMain: { flex: 1, minHeight: 0, minWidth: 0 },
  pane: { flex: 1, gap: 12, minHeight: 0 },
  contentScroll: { flex: 1 },
  content: { gap: 10, paddingBottom: 16 },
  paneHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    paddingHorizontal: 2,
  },
  // 分区底部固定的操作条（支付设置的启用与保存）。
  paneFooter: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  paneFooterNote: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 19,
    minWidth: 140,
  },
  paneFooterSpacer: { flex: 1 },
  paneFooterButton: { minWidth: 220 },

  // ---- 卡片 ----
  sectionCard: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 8,
    minWidth: 0,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  cardColumns: { alignItems: "stretch", flexDirection: "row", gap: 10 },
  columnCard: { flex: 1 },
  cardHeader: { alignItems: "center", flexDirection: "row", gap: 12 },
  cardHeaderCopy: { flex: 1, gap: 2, minWidth: 0 },
  cardTitleRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  cardTitle: { color: colors.ink, fontSize: 17, fontWeight: "900" },
  cardHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
  },
  cardBody: { gap: 8 },
  cardRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  actionRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  sectionCopy: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
  },

  // ---- 开关行 ----
  switchRowGroup: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 22,
  },
  switchRow: {
    alignItems: "center",
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 46,
    minWidth: 240,
  },
  switchCopy: { flex: 1, minWidth: 0 },
  switchLabel: { color: colors.ink, fontSize: 15.5, fontWeight: "800" },
  switchHint: {
    color: colors.mutedInk,
    fontSize: 12,
    fontWeight: "600",
    lineHeight: 17,
    marginTop: 2,
  },
  // 系统开关固定 51×31，放大到约 58×34；触控区仍保证 44。
  // 不要给 Switch 设 minWidth / minHeight：iOS 会把 ios_backgroundColor 铺满撑大后的整块视图，
  // 在轨道后面露出一块比轨道大的灰色圆角底（用户反馈「开关背景不对」）。
  settingSwitch: {
    transform: [{ scaleX: 1.12 }, { scaleY: 1.1 }],
  },
  inlineSwitch: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    minHeight: 46,
  },
  paymentMethodsRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 22,
  },

  // ---- 只读信息 ----
  metricRow: {
    columnGap: 18,
    flexDirection: "row",
    flexWrap: "wrap",
    rowGap: 4,
  },
  metric: {
    alignItems: "baseline",
    flexDirection: "row",
    flexShrink: 1,
    gap: 6,
  },
  metricLabel: { color: colors.mutedInk, fontSize: 14, fontWeight: "700" },
  metricValue: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },

  // ---- 输入框 ----
  inputFrame: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flex: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: posTouch.medium,
    minWidth: 220,
    paddingHorizontal: 14,
  },
  inputFrameDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  inputFramePrefix: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
  },
  inputFrameField: {
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontWeight: "700",
    minHeight: posTouch.medium - 3,
    minWidth: 0,
    paddingVertical: 0,
  },
  textInput: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
    minHeight: posTouch.medium,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  textInputDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
    color: colors.mutedInk,
  },
  textInputFill: { flex: 1, minWidth: 220 },
  multilineTextInput: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
    minHeight: 72,
    paddingHorizontal: 14,
    paddingTop: 10,
    paddingVertical: 10,
    textAlignVertical: "top",
  },
  fieldGroup: { gap: 4 },
  fieldLabel: { color: colors.mutedInk, fontSize: 13, fontWeight: "800" },
  fieldHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
  },

  // ---- 支付设置 ----
  paymentToolbar: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  paymentTabs: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flex: 1,
    flexDirection: "row",
    minHeight: posTouch.medium,
    minWidth: 300,
    overflow: "hidden",
  },
  paymentTab: {
    alignItems: "center",
    flex: 1,
    flexDirection: "row",
    gap: 6,
    justifyContent: "center",
    minHeight: posTouch.medium,
    paddingHorizontal: 16,
  },
  paymentTabDivider: { borderLeftColor: colors.border, borderLeftWidth: 1 },
  paymentTabSelected: { backgroundColor: colors.inverseSurface },
  paymentTabPressed: { backgroundColor: colors.surfaceAlt },
  paymentTabDot: {
    backgroundColor: colors.green,
    borderRadius: 5,
    height: 9,
    width: 9,
  },
  paymentTabLabel: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 15.5,
    fontWeight: "800",
  },
  paymentTabSelectedLabel: { color: colors.onInverse },
  paymentTabDisabledLabel: { color: colors.disabledInk },
  paymentEnvironment: { width: 220 },
  paymentEnvironmentNote: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
    maxWidth: 260,
  },
  availability: {
    alignSelf: "flex-start",
    borderRadius: 13,
    paddingHorizontal: 10,
    paddingVertical: 5,
  },
  availabilityReady: { backgroundColor: colors.greenSoft },
  availabilityUnavailable: { backgroundColor: colors.redSoft },
  availabilityText: { fontSize: 12.5, fontWeight: "800" },
  availabilityTextReady: { color: colors.green },
  availabilityTextUnavailable: { color: colors.red },
  squareSummaryRow: { flexDirection: "row", flexWrap: "wrap", gap: 10 },
  squareSummaryMetric: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flex: 1,
    gap: 3,
    minWidth: 140,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  squareSummaryValue: {
    color: colors.ink,
    fontSize: 14,
    fontWeight: "800",
    lineHeight: 19,
  },
  squareFieldGroup: { gap: 4 },
  squareSelectionRow: {
    alignItems: "stretch",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  squareSelectionValue: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flex: 1,
    justifyContent: "center",
    minHeight: posTouch.button,
    minWidth: 140,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  squareSelectionPrimary: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
    lineHeight: 21,
  },
  squareSelectionMetaRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
    marginTop: 2,
  },
  squareSelectionSecondary: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 17,
  },
  squareSelectionError: { color: colors.red },
  squareDeviceCodeSection: {
    borderTopColor: colors.border,
    borderTopWidth: 1,
    gap: 8,
    paddingTop: 10,
  },
  squareSandboxNote: {
    backgroundColor: colors.blueSoft,
    borderRadius: posRadius.medium,
    color: colors.blue,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 19,
    overflow: "hidden",
    padding: 10,
  },
  squareResourceState: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    minHeight: SETTINGS_MIN_TOUCH_TARGET,
  },
  squareResourceText: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
  },
  squareDeviceCodeList: { gap: 8 },
  squareDeviceCodeRow: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: posTouch.button,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  squareDeviceCodeSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
  },
  squareDeviceCodeIdentity: { flex: 1, minWidth: 0 },
  squarePickerBody: { gap: 8, paddingBottom: 2 },
  squarePickerOption: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    justifyContent: "center",
    minHeight: 78,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  linklySetup: { gap: 8 },
  linklyStatusRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  linklyStatusCopy: {
    alignItems: "baseline",
    flex: 1,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
    minWidth: 180,
  },
  linklyStatusValue: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 14,
    fontWeight: "800",
  },
  linklyDirectoryHeading: { gap: 2, marginTop: 2 },
  linklyDirectoryTitle: { color: colors.ink, fontSize: 15, fontWeight: "900" },
  linklyTerminalList: { gap: 8 },
  // 每台刷卡机一行（最少 84 高）；本机正在用的那台用主操作色粗框标出。
  linklyLineCard: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    gap: 8,
    justifyContent: "center",
    minHeight: 84,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  linklyLineCardSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
    borderWidth: 2.5,
    paddingHorizontal: 13,
    paddingVertical: 7,
  },
  linklyLineOverview: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
  },
  linklyLineIdentity: {
    flexBasis: 200,
    flexGrow: 1,
    justifyContent: "center",
    minHeight: posTouch.small,
    minWidth: 0,
  },
  linklyLineHeading: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  linklyTerminalName: { color: colors.ink, fontSize: 16, fontWeight: "900" },
  linklyTerminalStatus: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    marginTop: 3,
  },
  linklyLineActionColumn: {
    flexBasis: 380,
    flexGrow: 1,
    gap: 6,
    minWidth: 0,
  },
  linklyLineActionRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    justifyContent: "flex-end",
  },
  linklyLineTestStatus: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    textAlign: "right",
  },
  linklyConnectionWarning: {
    backgroundColor: colors.yellowSoft,
    borderRadius: posRadius.small,
    color: colors.yellowInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    overflow: "hidden",
    padding: 10,
  },
  linklyTerminalOption: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    justifyContent: "center",
    minHeight: posTouch.button,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  linklyTerminalOptionSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
  },
  linklyPairForm: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    gap: 8,
    padding: 12,
  },
  linklyLegacyPairForm: { gap: 8 },
  linklyPairHeading: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    justifyContent: "space-between",
  },
  linklyPairTitle: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 15,
    fontWeight: "900",
  },
  linklyPairInputRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  linklyPairInput: { flex: 1, minWidth: 200 },
  linklySetupStatus: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 19,
  },

  // ---- 外设 ----
  printerOptionRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 16,
  },
  printerOptionSegment: { flex: 1, minWidth: 170 },
  profileManagedNotice: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    gap: 2,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  profileManagedText: {
    color: colors.ink,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 19,
  },
  profileRow: { alignItems: "flex-start", flexDirection: "row", gap: 10 },
  profileField: { flexBasis: 0, flexGrow: 1, gap: 4, minWidth: 0 },
  // 占两列：基准宽度补上一个列间距，和上下两行的三列对齐。
  profileFieldWide: { flexBasis: 10, flexGrow: 2, gap: 4, minWidth: 0 },
  // 门店资料输入框按设计图用 48 高，三列排开一屏能看完。
  profileInput: { minHeight: posTouch.small },
  profileAddressInput: { paddingTop: 13 },
  deviceLine: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
  },
  deviceLineCopy: { flex: 1, gap: 2, minWidth: 96 },
  deviceLineStatus: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },

  // ---- 设备注册 ----
  dangerDescription: {
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 20,
  },
  reregisterPreview: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    gap: 8,
    padding: 12,
  },
  reregistrationBlockerPanel: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    gap: 8,
    padding: 14,
  },
  reregistrationBlockerTitle: {
    color: colors.red,
    fontSize: 17,
    fontWeight: "900",
    lineHeight: 23,
  },
  reregistrationBlockerBody: {
    color: colors.ink,
    fontSize: 13.5,
    fontWeight: "600",
    lineHeight: 20,
  },
  reregistrationBlockerList: {},
  reregistrationBlockerRow: {
    alignItems: "center",
    borderTopColor: colors.redBorder,
    borderTopWidth: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: posTouch.small,
    paddingVertical: 8,
  },
  reregistrationBlockerCopy: { flex: 1, minWidth: 0 },
  reregistrationBlockerLabel: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 20,
  },
  reregistrationBlockerHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
    marginTop: 2,
  },
  reregistrationBlockerValue: {
    color: colors.red,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    lineHeight: 20,
    minWidth: 58,
    textAlign: "right",
  },
  reregistrationSupervisorHint: {
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 20,
  },

  // ---- 系统与目录 ----
  safetyNote: {
    color: colors.green,
    fontSize: 12.5,
    fontWeight: "700",
    lineHeight: 18,
  },
  backgroundNote: {
    color: colors.blue,
    fontSize: 12.5,
    fontWeight: "700",
    lineHeight: 18,
  },
  catalogRefreshStatus: {
    backgroundColor: colors.blueSoft,
    borderRadius: posRadius.medium,
    padding: 12,
  },
  catalogRefreshWarning: { backgroundColor: colors.yellowSoft },
  catalogRefreshFailed: { backgroundColor: colors.redSoft },
  catalogRefreshHeading: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
  },
  catalogRefreshTitle: { color: colors.ink, fontSize: 15, fontWeight: "900" },
  catalogRefreshTrack: {
    backgroundColor: colors.surface,
    borderRadius: 999,
    height: 8,
    marginTop: 10,
    overflow: "hidden",
  },
  catalogRefreshFill: {
    backgroundColor: colors.accent,
    borderRadius: 999,
    height: "100%",
  },
  catalogRefreshMeta: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "700",
    lineHeight: 18,
    marginTop: 5,
  },
  catalogRefreshCode: {
    color: colors.red,
    fontFamily: "Courier",
    fontSize: 12,
    fontWeight: "700",
    marginTop: 6,
  },

  // ---- 硬件测试 ----
  hardwareHeading: { alignItems: "center", flexDirection: "row", gap: 12 },
  hardwareStatus: { fontSize: 14, fontWeight: "800" },
  hardwareStatusReady: { color: colors.green },
  hardwareStatusDisconnected: { color: colors.red },
  hardwareStatusUnavailable: { color: colors.mutedInk },
  scannerValue: {
    color: colors.ink,
    fontFamily: "Courier",
    fontSize: 26,
    fontWeight: "800",
  },

  // ---- 弹窗：蓝牙设备 / Square 选择 / 危险操作确认 ----
  confirmationOverlay: {
    alignItems: "center",
    backgroundColor: colors.overlay,
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  backdropDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  pickerSheet: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1,
    maxHeight: "82%",
    maxWidth: 760,
    padding: 20,
    shadowColor: colors.shadow,
    shadowOffset: { height: 5, width: 0 },
    shadowOpacity: 0.18,
    shadowRadius: 10,
    width: "86%",
  },
  printerPickerHeader: { marginBottom: 12 },
  printerPickerTitle: { color: colors.ink, fontSize: 20, fontWeight: "900" },
  printerPickerHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    marginTop: 4,
  },
  printerPickerBody: { flexGrow: 1, minHeight: 92 },
  printerPickerScroll: { flexShrink: 1, minHeight: 92 },
  printerPickerProgress: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 150,
  },
  printerPickerProgressText: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
    marginTop: 12,
  },
  printerPickerEmpty: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 22,
    paddingVertical: 24,
    textAlign: "center",
  },
  printerPickerError: {
    backgroundColor: colors.redSoft,
    borderRadius: posRadius.medium,
    color: colors.red,
    fontSize: 14,
    fontWeight: "800",
    overflow: "hidden",
    padding: 12,
  },
  printerPickerErrorBody: { minHeight: 92 },
  printerPickerPermissionHint: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "600",
    lineHeight: 21,
    marginTop: 10,
  },
  printerPickerPermissionAction: { alignSelf: "flex-start", marginTop: 12 },
  printerPickerActions: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "flex-end",
    marginTop: 16,
  },
  deviceRow: {
    alignItems: "center",
    borderColor: colors.border,
    borderTopWidth: 1,
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    minHeight: 78,
    paddingVertical: 10,
  },
  deviceIdentity: { flex: 1 },
  deviceDetailRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    minHeight: 22,
  },
  deviceFieldLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "700",
    width: 172,
  },
  deviceNameRow: {
    alignItems: "center",
    flex: 1,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  deviceName: { color: colors.ink, fontSize: 16, fontWeight: "900" },
  preferredPrinterTag: {
    alignSelf: "flex-start",
    backgroundColor: colors.blueSoft,
    borderRadius: 999,
    color: colors.blue,
    fontSize: 12,
    fontWeight: "800",
    lineHeight: 16,
    overflow: "hidden",
    paddingHorizontal: 8,
    paddingVertical: 2,
  },
  deviceMeta: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 13,
    fontWeight: "600",
  },
  deviceTransport: {
    color: colors.mutedInk,
    fontSize: 12,
    marginLeft: 180,
    marginTop: 2,
  },
  confirmation: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.red,
    borderRadius: posRadius.sheet,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 20,
    maxWidth: 980,
    padding: 20,
    shadowColor: colors.shadow,
    shadowOffset: { height: 5, width: 0 },
    shadowOpacity: 0.18,
    shadowRadius: 10,
    width: "90%",
  },
  confirmationCopy: { flex: 1 },
  confirmationScroll: { flex: 1 },
  confirmationScrollContent: {
    alignItems: "center",
    flexDirection: "row",
    gap: 20,
  },
  confirmationTitle: {
    color: colors.red,
    fontSize: 19,
    fontWeight: "900",
    lineHeight: 26,
  },
  confirmationBody: {
    color: colors.ink,
    fontSize: 14,
    fontWeight: "600",
    lineHeight: 21,
    marginTop: 6,
  },
  confirmationScan: { gap: 6, marginTop: 10 },
  confirmationActions: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },

  // ---- 状态横幅、空状态、不可用页 ----
  statusBanner: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: posTouch.small,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  statusSuccess: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
  },
  statusWarning: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
  },
  statusInfo: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
  },
  statusText: {
    color: colors.ink,
    flex: 1,
    fontSize: 14,
    fontWeight: "800",
  },
  emptyPanel: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flex: 1,
    gap: 14,
    justifyContent: "center",
    padding: 28,
  },
  emptyText: {
    color: colors.mutedInk,
    fontSize: 18,
    fontWeight: "800",
    textAlign: "center",
  },
  unavailable: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  unavailableCard: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1,
    gap: 12,
    maxWidth: 720,
    padding: 32,
  },
  unavailableEyebrow: {
    color: colors.accent,
    fontSize: 12,
    fontWeight: "900",
    letterSpacing: 1.5,
  },
  unavailableTitle: {
    color: colors.ink,
    fontSize: 26,
    fontWeight: "900",
    textAlign: "center",
  },
  unavailableHint: {
    color: colors.mutedInk,
    fontSize: 16,
    fontWeight: "600",
    lineHeight: 24,
    textAlign: "center",
  },
  // PosPill 默认靠上对齐；放在比它高的行里时改为垂直居中。
  pillCentered: { alignSelf: "center" },
  dimmed: { opacity: 0.5 },
  pressed: { opacity: 0.72 },
}));
