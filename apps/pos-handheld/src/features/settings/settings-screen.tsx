import { useEffect, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Linking,
  Modal,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  resolveSettingsLocale,
  settingsText,
  type SettingsCopyKey,
  type SettingsLocale,
} from "./settings-copy";
import {
  type PaymentEnvironment,
  type SettingsDangerousConfirmation,
  type SettingsPane,
  type SettingsPresenter,
  type SettingsPrinterDevice,
  type SettingsState,
  type SettingsStatusCode,
} from "./settings-presenter";
import type {
  SettingsSquareDevice,
  SettingsSquareDeviceCode,
  SettingsSquareLocation,
} from "@hb/pos-domain/features/settings/settings-square-setup";
import type { PendingWorkBlocker } from "@hb/pos-domain";

import {
  ANDROID_NEARBY_DEVICES_MIN_API,
  androidApiLevel,
} from "@/core/peripherals/printer/android-bluetooth-permissions";
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
import { toggleAppLanguage } from "@/i18n";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSwitch } from "@/ui/controls/pos-switch";
import { usePosSound } from "@/ui/feedback/pos-sound-context";
import { HandheldStateSurface } from "@/ui/handheld/handheld-design-states";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import { posColors } from "@/ui/theme";

export const SETTINGS_MIN_TOUCH_TARGET = 48;

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
  | "requestDeviceRegistrationReset"
  | "requestDeviceReregistration"
  | "previewDeviceReregistration"
  | "requestLinklyPair"
  | "refreshLinklySetup"
  | "refreshSquareDeviceCode"
  | "savePaymentSettings"
  | "saveManualCardSettings"
  | "savePrinterSettings"
  | "scanPrinters"
  | "selectLinklyTerminal"
  | "selectPane"
  | "setApiAddressDraft"
  | "setDrawerEnabled"
  | "setLinklyEnvironment"
  | "setPaymentProvider"
  | "setUseManualCard"
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
  | "testPaymentProvider"
  | "testPrinter"
  | "testScanner"
> &
  Readonly<{ getState(): SettingsState }>;

type SettingsScreenProps = Readonly<{
  locale?: SettingsLocale;
  onBack?(): void;
  onOpenSyncHistory?(): void;
  presenter: SettingsScreenPresenter;
  scanner?: HidScannerRouter;
}>;

const NAV_ITEMS: readonly Readonly<{
  label: SettingsCopyKey;
  pane: SettingsPane;
}>[] = [
  { pane: "general", label: "navigation.general" },
  { pane: "payments", label: "navigation.payments" },
  { pane: "peripherals", label: "navigation.peripherals" },
  { pane: "device", label: "navigation.device" },
  { pane: "hardware", label: "navigation.hardware" },
];

/**
 * 手持设置页保持清晰的分区与工作区。危险操作不使用平台 Alert，
 * 而在同一屏显示完整影响范围，确保自动化、键盘与触控都走同一确认路径。
 */
export function SettingsScreen({
  locale: localeOverride,
  onBack,
  onOpenSyncHistory,
  presenter,
  scanner,
}: SettingsScreenProps) {
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
  const interactionLocked = state.busy || state.confirmation !== null;

  return (
    <SafeAreaView style={styles.safeArea} testID="settings-screen">
      <HandheldStateSurface slug="settings-index" style={styles.stateSurface}>
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.page}
          pointerEvents={state.confirmation ? "none" : "auto"}
          style={styles.pageScroll}
          testID="settings-content-scroll"
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
        <View style={styles.header}>
          <View style={styles.titleGroup}>
            <Text style={styles.eyebrow}>{t("header.eyebrow")}</Text>
            <Text style={styles.title}>{t("header.title")}</Text>
            <Text style={styles.subtitle}>{t("header.subtitle")}</Text>
          </View>
          {onBack ? (
            <ActionButton
              disabled={interactionLocked}
              label={t("action.backToSales")}
              onPress={onBack}
              testID="settings-back"
              tone="quiet"
            />
          ) : null}
        </View>

        {state.statusCode ? (
          <StatusBanner
            locale={locale}
            statusCode={state.statusCode}
            blockers={state.pendingWorkBlockers}
            receiptProfileVersion={state.printer.profileVersion}
          />
        ) : null}

        <View
          pointerEvents={state.confirmation ? "none" : "auto"}
          style={styles.navigation}
        >
          <Text style={styles.navigationTitle}>{t("navigation.title")}</Text>
          {NAV_ITEMS.map((item) => (
            <ActionButton
              disabled={interactionLocked}
              key={item.pane}
              label={t(item.label)}
              onPress={() => presenter.selectPane(item.pane)}
              selected={state.activePane === item.pane}
              testID={`settings-nav-${item.pane}`}
              tone="nav"
            />
          ))}
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

        <View style={styles.workspace} testID="settings-workspace">
          {state.kind === "loading" || state.kind === "idle" ? (
            <EmptyPanel
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
              locale={locale}
              onBackToSales={onBack}
              onOpenSyncHistory={onOpenSyncHistory}
              presenter={presenter}
              scanner={scanner}
              state={state}
            />
          ) : null}
        </View>
        </PosKeyboardAwareScrollView>

        <Modal
          animationType="fade"
          onRequestClose={() => presenter.cancelConfirmation()}
          supportedOrientations={["portrait", "portrait-upside-down"]}
          testID="settings-confirmation-modal"
          transparent
          visible={state.confirmation !== null}
        >
          <View accessibilityViewIsModal style={styles.confirmationOverlay}>
            <PosPressable
              accessible={false}
              onPress={() => {
                if (!state.busy) presenter.cancelConfirmation();
              }}
              sound="navigate"
              style={styles.modalDismissArea}
              testID="settings-confirmation-backdrop"
            />
            {state.confirmation ? (
              <ConfirmationCard
                busy={state.busy}
                confirmation={state.confirmation}
                deviceCode={state.device.deviceCode}
                locale={locale}
                onCancel={() => presenter.cancelConfirmation()}
                onConfirm={(employeeBarcode) =>
                  void presenter.confirmDangerousAction(employeeBarcode)
                }
                storeCode={state.device.storeCode}
              />
            ) : null}
          </View>
        </Modal>
      </HandheldStateSurface>
    </SafeAreaView>
  );
}

export function SettingsUnavailableScreen({
  locale: localeOverride,
  onBack,
}: Readonly<{ locale?: SettingsLocale; onBack(): void }>) {
  const { i18n } = useTranslation();
  const locale =
    localeOverride ??
    resolveSettingsLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = (key: SettingsCopyKey) => settingsText(locale, key);
  return (
    <SafeAreaView style={styles.safeArea} testID="settings-runtime-unavailable">
      <View style={styles.unavailable}>
        <Text style={styles.eyebrow}>{t("unavailable.eyebrow")}</Text>
        <Text style={styles.unavailableTitle}>{t("unavailable.title")}</Text>
        <Text style={styles.unavailableHint}>{t("unavailable.hint")}</Text>
        <ActionButton
          label={t("action.backToSales")}
          onPress={onBack}
          testID="settings-unavailable-back"
        />
      </View>
    </SafeAreaView>
  );
}

function SettingsPaneContent({
  locale,
  onBackToSales,
  onOpenSyncHistory,
  presenter,
  scanner,
  state,
}: Readonly<{
  locale: SettingsLocale;
  onBackToSales: (() => void) | undefined;
  onOpenSyncHistory: (() => void) | undefined;
  presenter: SettingsScreenPresenter;
  scanner: HidScannerRouter | undefined;
  state: SettingsState;
}>) {
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
          onBackToSales={onBackToSales}
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
        <GeneralPane locale={locale} presenter={presenter} state={state} />
      );
  }
}

function GeneralPane({
  locale,
  presenter,
  state,
}: Readonly<{
  locale: SettingsLocale;
  presenter: SettingsScreenPresenter;
  state: SettingsState;
}>) {
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
    <HandheldStateSurface slug="transaction-settings">
      <View testID="settings-pane-content-general">
      <PaneHeading
        subtitle={t("general.subtitle")}
        title={t("general.title")}
      />
      <SectionCard
        eyebrow={t("general.language")}
        title={t("general.terminalPreferences")}
      >
        <Text style={styles.sectionCopy}>{t("general.singleScreen")}</Text>
        <Text style={styles.sectionCopy}>{t("general.securityHint")}</Text>
        <ActionButton
          label={t("general.switchLanguage")}
          onPress={() => void toggleAppLanguage()}
          testID="settings-language-switch"
          tone="secondary"
        />
      </SectionCard>
      <SectionCard
        eyebrow={t("eyebrow.interaction")}
        title={t("general.soundFeedback")}
      >
        <View
          style={[styles.soundPreferenceRow, styles.soundPreferenceDivider]}
        >
          <View style={styles.soundPreferenceCopy}>
            <Text style={styles.soundPreferenceLabel}>
              {t("general.buttonSound")}
            </Text>
            <Text style={[styles.sectionCopy, styles.soundPreferenceHint]}>
              {t("general.buttonSoundHint")}
            </Text>
          </View>
          <PosSwitch
            accessibilityLabel={t("general.buttonSound")}
            accessibilityRole="switch"
            accessibilityState={{ checked: buttonSoundEnabled }}
            onValueChange={setButtonSoundEnabled}
            sound={false}
            style={styles.soundSwitch}
            testID="settings-button-sound"
            thumbColor={buttonSoundEnabled ? posColors.blue : undefined}
            trackColor={{ false: "#A8B2BC", true: posColors.blueSoft }}
            value={buttonSoundEnabled}
          />
        </View>
        <View style={styles.soundPreferenceRow}>
          <View style={styles.soundPreferenceCopy}>
            <Text style={styles.soundPreferenceLabel}>
              {t("general.specialNodeSound")}
            </Text>
            <Text style={[styles.sectionCopy, styles.soundPreferenceHint]}>
              {t("general.specialNodeSoundHint")}
            </Text>
          </View>
          <PosSwitch
            accessibilityLabel={t("general.specialNodeSound")}
            accessibilityRole="switch"
            accessibilityState={{ checked: specialNodeSoundEnabled }}
            onValueChange={setSpecialNodeSoundEnabled}
            sound={false}
            style={styles.soundSwitch}
            testID="settings-special-node-sound"
            thumbColor={specialNodeSoundEnabled ? posColors.blue : undefined}
            trackColor={{ false: "#A8B2BC", true: posColors.blueSoft }}
            value={specialNodeSoundEnabled}
          />
        </View>
      </SectionCard>
      <SectionCard
        eyebrow={t("eyebrow.network")}
        title={t("general.apiAddress")}
      >
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("general.apiAddress")}
          autoCapitalize="none"
          autoCorrect={false}
          editable={!locked && state.access.canReregisterDevice}
          onChangeText={(value) => presenter.setApiAddressDraft(value)}
          placeholder="https://example.com/pos-api"
          placeholderTextColor="#7B8793"
          style={styles.textInput}
          testID="settings-api-address"
          value={state.apiAddressDraft}
        />
        <View style={styles.actionRow} testID="settings-api-actions">
          <ActionButton
            disabled={locked || !state.access.canReregisterDevice}
            label={t("general.useLocalApi")}
            onPress={() =>
              presenter.setApiAddressDraft(
                DEFAULT_LOCAL_HBPOS_API_BASE_URL,
              )
            }
            testID="settings-api-use-local"
            tone="secondary"
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
            tone="secondary"
          />
          <ActionButton
            disabled={locked || !state.access.canReregisterDevice}
            label={t("general.testApiAddress")}
            onPress={() => void presenter.testApiAddress()}
            testID="settings-api-test"
            tone="secondary"
          />
          <ActionButton
            disabled={
              locked ||
              catalogRefreshRunning ||
              !state.access.canReregisterDevice
            }
            label={t("general.reviewApiAddress")}
            onPress={() => presenter.requestApiAddressChange()}
            testID="settings-api-request-change"
          />
        </View>
      </SectionCard>

      <SectionCard eyebrow={t("eyebrow.catalog")} title={t("general.catalog")}>
        <View style={styles.metricRow}>
          <Metric
            label={t("metric.snapshot")}
            value={state.catalog.snapshotId ?? t("value.none")}
          />
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

      <SectionCard
        eyebrow={t("eyebrow.updates")}
        title={t("general.appUpdate")}
      >
        <View style={styles.metricRow}>
          <Metric label={t("metric.channel")} value={state.appUpdate.channel} />
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
            label={t("general.checkUpdate")}
            onPress={() => void presenter.checkForAppUpdate()}
            testID="settings-update-check"
            tone="secondary"
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
    </HandheldStateSurface>
  );
}

function CatalogRefreshStatus({
  locale,
  refresh,
}: Readonly<{
  locale: SettingsLocale;
  refresh: CatalogRefreshState;
}>) {
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
          <ActivityIndicator color={posColors.orange} size="small" />
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
  const [squarePicker, setSquarePicker] = useState<
    "device" | "location" | null
  >(null);
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
    state.paymentProviderDraft !== "square";
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
    <View testID="settings-pane-content-payments">
      <PaneHeading
        subtitle={t("payments.subtitle")}
        title={t("payments.title")}
      />
      <SectionCard
        eyebrow={t("eyebrow.activeCardTerminal")}
        title={t("payments.provider")}
      >
        <Text style={styles.sectionCopy}>{t("payments.providerHint")}</Text>
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
            : state.paymentProviderDraft === "square"
              ? t("payments.squareSelected")
              : t("payments.linklySelected")}
        </Text>
        <View style={styles.actionRow}>
          <View>
            <Text style={styles.fieldLabel}>{t("payments.manualCard")}</Text>
            <Text style={styles.sectionCopy}>{t("payments.manualCardHint")}</Text>
          </View>
          <ToggleButton
            disabled={disabled}
            label={state.paymentMethods.useManualCard ? t("payments.manualCardOn") : t("payments.manualCardOff")}
            onPress={() => presenter.setUseManualCard(!state.paymentMethods.useManualCard)}
            selected={state.paymentMethods.useManualCard}
            testID="settings-payment-manual-card"
          />
        </View>
        <ActionButton
          compact
          disabled={disabled}
          label={t("payments.saveManualCard")}
          onPress={() => void presenter.saveManualCardSettings()}
          testID="settings-payment-manual-card-save"
          tone="secondary"
        />
      </SectionCard>
      <View style={styles.twoColumn}>
        <SectionCard
          eyebrow={t("eyebrow.cardTerminal")}
          style={styles.columnCard}
          title="Square"
        >
          {!squareNeedsInitialSetup ? (
            <Availability
              available={squareAvailable}
              blockerCode={state.square.blockerCode}
              locale={locale}
            />
          ) : null}
          <EnvironmentSelector
            disabled={squareSetupDisabled}
            environment={state.squareDraft.environment}
            locale={locale}
            onSelect={(environment) =>
              presenter.setSquareEnvironment(environment)
            }
            prefix="settings-square"
          />
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
              style={styles.textInput}
              testID="settings-square-device-code-name"
              value={state.squareDeviceCodeNameDraft}
            />
            <Text style={styles.squareFieldHint}>
              {t("square.deviceCodeNameHint")}
            </Text>
            <View style={styles.actionRow}>
              <ActionButton
                compact
                disabled={
                  squareDeviceCodeDisabled ||
                  state.squareSetup.deviceCodes.kind === "loading"
                }
                label={t("square.loadDeviceCodes")}
                onPress={() => void presenter.loadSquareDeviceCodes()}
                testID="settings-square-device-code-load"
                tone="secondary"
              />
              <ActionButton
                compact
                disabled={
                  squareDeviceCodeDisabled ||
                  state.squareDeviceCodeNameDraft.trim().length === 0 ||
                  state.squareSetup.deviceCodes.kind === "loading"
                }
                label={t("square.createDeviceCode")}
                onPress={() => void presenter.createSquareDeviceCode()}
                testID="settings-square-device-code-create"
              />
              <ActionButton
                compact
                disabled={
                  squareDeviceCodeDisabled ||
                  !state.squareSetup.selectedDeviceCodeId ||
                  !squareDeviceCodesMatchLocation ||
                  state.squareSetup.deviceCodes.kind === "loading"
                }
                label={t("square.refreshDeviceCode")}
                onPress={() => void presenter.refreshSquareDeviceCode()}
                testID="settings-square-device-code-refresh"
                tone="secondary"
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
                <Text style={styles.squarePairingNote}>
                  {t("square.deviceCodePairing")}
                </Text>
              </>
            )}
          </View>
          <ActionButton
            disabled={squareRuntimeDisabled}
            label={t("action.test")}
            onPress={() => void presenter.testPaymentProvider("square")}
            testID="settings-square-test"
            tone="secondary"
          />
        </SectionCard>

        <SectionCard
          eyebrow={t("eyebrow.eftpos")}
          style={styles.columnCard}
          title="Linkly"
        >
          {!linklyNeedsInitialSetup || !state.linklySetup ? (
            <Availability
              available={linklyAvailable}
              blockerCode={state.linkly.blockerCode}
              locale={locale}
            />
          ) : null}
          <EnvironmentSelector
            disabled={linklySetupDisabled}
            environment={state.linklyDraft.environment}
            locale={locale}
            onSelect={(environment) =>
              presenter.setLinklyEnvironment(environment)
            }
            prefix="settings-linkly"
          />
          <Text style={styles.sectionCopy}>{t("payments.linklyHint")}</Text>
          {state.linklySetup ? (
            <LinklySetupCard
              disabled={linklySetupDisabled}
              locale={locale}
              presenter={presenter}
              state={state}
            />
          ) : (
            <ActionButton
              disabled={linklySetupDisabled}
              label={t("action.test")}
              onPress={() => void presenter.testPaymentProvider("linkly")}
              testID="settings-linkly-test"
              tone="secondary"
            />
          )}
        </SectionCard>
      </View>
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
      <ActionButton
        disabled={disabled || catalogRefreshRunning}
        label={t("payments.save")}
        onPress={() => void presenter.savePaymentSettings()}
        testID="settings-payment-save"
      />
    </View>
  );
}

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
  const [pairCode, setPairCode] = useState("");
  const [pairTerminalId, setPairTerminalId] = useState("");
  const setup = state.linklySetup;
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  useEffect(() => {
    setPairCode("");
    setPairTerminalId(
      setup?.terminals.value?.selectedTerminalId ??
        setup?.terminals.value?.terminals[0]?.terminalId ??
        "",
    );
  }, [
    state.linklyDraft.environment,
    setup?.pairCodeResetToken,
    setup?.terminals.value?.selectedTerminalId,
    setup?.terminals.value?.terminals[0]?.terminalId,
  ]);
  if (!setup) return null;
  const health = setup.health.value;
  const terminalSnapshot = setup.terminals.value;
  const pairTerminal = terminalSnapshot?.terminals.find(
    (terminal) => terminal.terminalId === pairTerminalId,
  );
  const healthReady = linklyHealthReady(state);
  const storeCredentialsReady = linklyStoreCredentialsReady(state);
  const terminalPaired = linklyTerminalPaired(state);
  const healthStatus = linklyHealthStatusText(
    locale,
    setup.health.kind,
    health?.isReady,
  );

  return (
    <View style={styles.linklySetupCard} testID="settings-linkly-setup">
      <View style={styles.squareSummaryRow}>
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
      </View>
      <View style={styles.actionRow}>
        <ActionButton
          compact
          disabled={disabled || setup.health.kind === "loading"}
          label={t("linkly.refresh")}
          onPress={() => void presenter.refreshLinklySetup()}
          testID="settings-linkly-refresh"
          tone="secondary"
        />
        <ActionButton
          compact
          disabled={disabled || !healthReady}
          label={t("action.test")}
          onPress={() => void presenter.testPaymentProvider("linkly")}
          testID="settings-linkly-test"
          tone="secondary"
        />
      </View>
      <FieldLabel label={t("linkly.terminals")} />
      {terminalSnapshot?.terminals.length ? (
        <View style={styles.linklyTerminalList} testID="settings-linkly-terminals">
          {terminalSnapshot.terminals.map((terminal) => {
            const selected = terminal.terminalId === pairTerminalId;
            const terminalDisabled =
              disabled || setup.terminals.kind === "switching";
            const status = terminal.isBusy
              ? t("linkly.statusBusy")
              : terminal.pairingState === "NeedsRepair"
                ? t("linkly.statusNeedsRepair")
                : terminal.isReady && terminal.pairingState === "Ready"
                  ? t("linkly.statusReady")
                  : terminal.pairingState === "Unpaired"
                    ? t("linkly.statusUnpaired")
                    : t("linkly.statusUnknown");
            return (
              <PosPressable
                accessibilityLabel={`${terminal.displayName}. Lane ${terminal.laneNo}. ${status}`}
                accessibilityRole="button"
                accessibilityState={{ disabled: terminalDisabled, selected }}
                disabled={terminalDisabled}
                key={terminal.terminalId}
                onPress={() => {
                  setPairCode("");
                  setPairTerminalId(terminal.terminalId);
                  if (terminal.isReady && terminal.pairingState === "Ready") {
                    void presenter.selectLinklyTerminal(terminal.terminalId);
                  }
                }}
                sound="tap"
                style={({ pressed }) => [
                  styles.linklyTerminalOption,
                  selected && styles.linklyTerminalOptionSelected,
                  terminalDisabled && styles.squareSelectionDisabled,
                  pressed && !terminalDisabled && styles.pressedButton,
                ]}
                testID={`settings-linkly-terminal-${terminal.terminalId}`}
              >
                <Text style={styles.linklyTerminalName}>
                  {`${terminal.displayName} · Lane ${terminal.laneNo}`}
                </Text>
                <Text style={styles.linklyTerminalStatus}>{status}</Text>
              </PosPressable>
            );
          })}
        </View>
      ) : (
        <Text style={styles.squareFieldHint}>{t("linkly.noTerminals")}</Text>
      )}
      <FieldLabel label={t("linkly.pairCode")} />
      <PosKeyboardAwareTextInput
        accessibilityLabel={t("linkly.pairCode")}
        autoCapitalize="none"
        autoCorrect={false}
        editable={!disabled}
        keyboardType="number-pad"
        maxLength={6}
        onChangeText={(value) =>
          setPairCode(value.replace(/[^0-9]/gu, "").slice(0, 6))
        }
        style={styles.textInput}
        testID="settings-linkly-pair-code"
        value={pairCode}
      />
      <Text style={styles.squareFieldHint}>{t("linkly.pairCodeHint")}</Text>
      <ActionButton
        compact
        disabled={
          disabled ||
          !storeCredentialsReady ||
          !pairTerminal ||
          pairTerminal.isBusy ||
          pairCode.length !== 6
        }
        label={t("linkly.pair")}
        onPress={() => presenter.requestLinklyPair(pairTerminalId, pairCode)}
        testID="settings-linkly-pair"
      />
      <Text style={styles.linklySetupStatus} testID="settings-linkly-logon-status">
        {setup.logonTest.status === "passed"
          ? t("linkly.logonPassed")
          : t("linkly.logonRequired")}
      </Text>
      <Text style={styles.squarePairingNote} testID="settings-linkly-instructions">
        {t("linkly.instructions")}
      </Text>
    </View>
  );
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
            selectDisabled && styles.squareSelectionDisabled,
            pressed && !selectDisabled && styles.pressedButton,
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
          compact
          disabled={disabled || loading}
          label={loadLabel}
          onPress={onLoad}
          style={styles.squareLoadButton}
          testID={`${testID}-load`}
          tone="secondary"
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
              disabled && styles.squareSelectionDisabled,
              pressed && !disabled && styles.pressedButton,
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
              <Text style={styles.squareCurrentTag}>{selectedLabel}</Text>
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
      supportedOrientations={["portrait", "portrait-upside-down"]}
      testID={`${testID}-modal`}
      transparent
      visible={visible}
    >
      <View accessibilityViewIsModal style={styles.confirmationOverlay}>
        <PosPressable
          accessible={false}
          onPress={onClose}
          sound="navigate"
          style={styles.modalDismissArea}
          testID={`${testID}-backdrop`}
        />
        <View style={styles.squarePicker} testID={testID}>
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
                      optionDisabled && styles.squareSelectionDisabled,
                      pressed && !optionDisabled && styles.pressedButton,
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
              label={reloadLabel}
              onPress={onReload}
              testID={`${testID}-reload`}
              tone="secondary"
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

function linklyStoreCredentialsReady(
  state: Pick<SettingsState, "linklySetup">,
): boolean {
  const health = state.linklySetup?.health;
  return Boolean(
    health?.kind === "ready" &&
      health.value?.checks.some(
        (check) =>
          check.code.trim().toUpperCase() === "STORE_CREDENTIAL" &&
          check.isReady,
      ),
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
  const setup = state.linklySetup;
  return Boolean(
    setup?.health.kind === "ready" &&
      setup.health.value?.environment === state.linklyDraft.environment &&
      setup.health.value.isReady === true,
  );
}

function linklySelectionReady(
  state: Pick<SettingsState, "linklySetup" | "linklyDraft">,
): boolean {
  const logonTest = state.linklySetup?.logonTest;
  return (
    linklyHealthReady(state) &&
    logonTest?.environment === state.linklyDraft.environment &&
    logonTest.status === "passed"
  );
}

function linklyHealthStatusText(
  locale: SettingsLocale,
  kind: NonNullable<SettingsState["linklySetup"]>["health"]["kind"],
  isReady: boolean | undefined,
): string {
  if (kind === "loading") return settingsText(locale, "linkly.statusLoading");
  if (kind === "failed") return settingsText(locale, "linkly.statusUnavailable");
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
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  // 已应用总部下发资料（profileVersion > 0）：六项资料只读，按钮改为「立即同步」。
  const managed = state.printer.profileVersion > 0;
  const fieldsDisabled = disabled || managed;
  return (
    <View testID="settings-receipt-profile-card">
      <SectionCard
        eyebrow={t("eyebrow.storeProfile")}
        title={t("peripherals.storeProfile")}
      >
        {managed ? (
          <View testID="settings-receipt-profile-managed">
            <Text style={styles.sectionCopy}>
              {t("peripherals.storeProfileManaged")}
            </Text>
            <Text style={styles.sectionCopy}>
              {t("peripherals.storeProfileVersion", {
                version: state.printer.profileVersion,
              })}
            </Text>
          </View>
        ) : null}
        <FieldLabel label={t("field.receiptBrandName")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.receiptBrandName")}
          autoCapitalize="words"
          autoCorrect={false}
          editable={!fieldsDisabled}
          maxLength={120}
          onChangeText={(value) => presenter.setReceiptBrandName(value)}
          style={styles.textInput}
          testID="settings-receipt-brand-name"
          value={state.printer.brandName}
        />
        <FieldLabel label={t("field.receiptStoreName")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.receiptStoreName")}
          autoCapitalize="words"
          autoCorrect={false}
          editable={!fieldsDisabled}
          maxLength={120}
          onChangeText={(value) => presenter.setReceiptStoreName(value)}
          style={styles.textInput}
          testID="settings-receipt-store-name"
          value={state.printer.storeName}
        />
        <FieldLabel label={t("field.receiptAddress")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.receiptAddress")}
          editable={!fieldsDisabled}
          maxLength={240}
          multiline
          onChangeText={(value) => presenter.setReceiptAddress(value)}
          style={styles.multilineTextInput}
          testID="settings-receipt-address"
          textAlignVertical="top"
          value={state.printer.address}
        />
        <FieldLabel label={t("field.receiptPhone")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.receiptPhone")}
          autoCapitalize="none"
          autoCorrect={false}
          editable={!fieldsDisabled}
          keyboardType="phone-pad"
          maxLength={60}
          onChangeText={(value) => presenter.setReceiptPhone(value)}
          style={styles.textInput}
          testID="settings-receipt-phone"
          value={state.printer.phone}
        />
        <FieldLabel label={t("field.receiptAbn")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.receiptAbn")}
          autoCapitalize="characters"
          autoCorrect={false}
          editable={!fieldsDisabled}
          maxLength={32}
          onChangeText={(value) => presenter.setReceiptAbn(value)}
          style={styles.textInput}
          testID="settings-receipt-abn"
          value={state.printer.abn}
        />
        <FieldLabel label={t("field.receiptReturnPolicy")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.receiptReturnPolicy")}
          editable={!fieldsDisabled}
          maxLength={500}
          multiline
          onChangeText={(value) => presenter.setReceiptReturnPolicy(value)}
          style={styles.multilineTextInput}
          testID="settings-receipt-return-policy"
          textAlignVertical="top"
          value={state.printer.returnPolicy}
        />
        <View style={styles.actionRow}>
          <ActionButton
            disabled={disabled}
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
            tone="secondary"
          />
        </View>
      </SectionCard>
    </View>
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
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const printerDisabled =
    state.busy ||
    state.confirmation !== null ||
    !state.access.canConfigurePrinter;
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
  const printerPickerLocationOff =
    printerPickerError === "printer-bluetooth-location-off";
  const printerPickerPairingRequired =
    printerPickerError === "printer-pairing-required";

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
    <HandheldStateSurface slug="peripheral-settings">
      <View testID="settings-pane-content-peripherals">
      <PaneHeading
        subtitle={t("peripherals.subtitle")}
        title={t("peripherals.title")}
      />
      <SectionCard
        eyebrow={t("eyebrow.receipt")}
        title={t("peripherals.printer")}
      >
        <View style={styles.actionRow}>
          <ToggleButton
            disabled={printerDisabled}
            label={t("peripherals.printing")}
            onPress={() =>
              presenter.setPrinterEnabled(!state.printer.printEnabled)
            }
            selected={state.printer.printEnabled}
            testID="settings-printer-enabled"
          />
          <ToggleButton
            disabled={printerDisabled}
            label={t("peripherals.drawer")}
            onPress={() =>
              presenter.setDrawerEnabled(!state.printer.drawerEnabled)
            }
            selected={state.printer.drawerEnabled}
            testID="settings-drawer-enabled"
          />
          <ToggleButton
            disabled={printerDisabled}
            label={state.printer.paper}
            onPress={() =>
              presenter.setPrinterPaper(
                state.printer.paper === "80mm" ? "58mm" : "80mm",
              )
            }
            selected
            testID="settings-printer-paper"
          />
          <ToggleButton
            disabled={printerDisabled}
            label={t(
              state.printer.locale === "en"
                ? "printer.locale.en"
                : "printer.locale.zhCN",
            )}
            onPress={() =>
              presenter.setPrinterLocale(
                state.printer.locale === "en" ? "zh-CN" : "en",
              )
            }
            selected
            testID="settings-printer-locale"
          />
        </View>
        <FieldLabel label={t("field.peripheralId")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.printerPeripheralId")}
          autoCapitalize="none"
          autoCorrect={false}
          editable={!printerDisabled}
          onChangeText={(value) => presenter.setPrinterPeripheralId(value)}
          style={styles.textInput}
          testID="settings-printer-id"
          value={state.printer.peripheralId ?? ""}
        />
        <View style={styles.actionRow}>
          <ActionButton
            disabled={printerDisabled}
            label={t("peripherals.scanPrinters")}
            onPress={() => void scanPrinterDevices()}
            testID="settings-printer-scan"
            tone="secondary"
          />
          <ActionButton
            disabled={printerDisabled}
            label={t("action.save")}
            onPress={() => void presenter.savePrinterSettings()}
            testID="settings-printer-save"
          />
          <ActionButton
            disabled={printerDisabled}
            label={t("peripherals.testPrint")}
            onPress={() => void presenter.testPrinter()}
            testID="settings-printer-test"
            tone="secondary"
          />
          <ActionButton
            disabled={
              printerDisabled ||
              !state.printer.peripheralId ||
              !state.printer.drawerEnabled
            }
            label={t("peripherals.testDrawer")}
            onPress={() => void presenter.testCashDrawer()}
            testID="settings-drawer-test"
            tone="secondary"
          />
          <ActionButton
            disabled={printerDisabled || !state.printer.peripheralId}
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

      <View style={styles.twoColumn}>
        <SectionCard
          eyebrow={t("eyebrow.scanner")}
          style={styles.columnCard}
          title={t("peripherals.scanner")}
        >
          <Text style={styles.sectionCopy}>
            {t("label.status", {
              status: hardwareStatusText(locale, state.hardware.scannerStatus),
            })}
          </Text>
          <ActionButton
            disabled={
              state.busy ||
              state.confirmation !== null ||
              !state.access.canTestScanner
            }
            label={t("peripherals.captureOneScan")}
            onPress={() => void presenter.testScanner()}
            testID="settings-scanner-test"
            tone="secondary"
          />
        </SectionCard>
      </View>

      <Modal
        animationType="fade"
        onRequestClose={closePrinterPicker}
        supportedOrientations={["portrait", "portrait-upside-down"]}
        testID="settings-printer-picker-modal"
        transparent
        visible={printerPickerVisible}
      >
        <View accessibilityViewIsModal style={styles.confirmationOverlay}>
          <PosPressable
            accessible={false}
            onPress={closePrinterPicker}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="settings-printer-picker-backdrop"
          />
          <HandheldStateSurface
            slug="pda-printer-connect"
            style={styles.printerPickerState}
          >
            <View style={styles.printerPicker} testID="settings-printer-picker">
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
                      {t(bluetoothPermissionHintKey())}
                    </Text>
                    <View style={styles.printerPickerPermissionAction}>
                      <ActionButton
                        label={t("action.openSystemSettings")}
                        onPress={() => void Linking.openSettings()}
                        testID="settings-printer-open-system-settings"
                        tone="secondary"
                      />
                    </View>
                  </>
                ) : null}
                {printerPickerPairingRequired ? (
                  <>
                    <Text style={styles.printerPickerPermissionHint}>
                      {t("printer.pairingRequiredHint")}
                    </Text>
                    <View style={styles.printerPickerPermissionAction}>
                      <ActionButton
                        label={t("action.openBluetoothSettings")}
                        onPress={() => void openBluetoothSettings()}
                        testID="settings-printer-open-bluetooth-settings"
                        tone="secondary"
                      />
                    </View>
                  </>
                ) : null}
                {printerPickerLocationOff ? (
                  <>
                    <Text style={styles.printerPickerPermissionHint}>
                      {t("printer.locationServiceHint")}
                    </Text>
                    <View style={styles.printerPickerPermissionAction}>
                      <ActionButton
                        label={t("action.openLocationSettings")}
                        onPress={() => void openLocationSettings()}
                        testID="settings-printer-open-location-settings"
                        tone="secondary"
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
                        <Text
                          style={[
                            styles.deviceTransport,
                            device.transport === "bluetooth-classic" &&
                              device.paired === false &&
                              styles.deviceTransportWarning,
                          ]}
                          testID={`settings-printer-device-transport-${device.id}`}
                        >
                          {printerTransportHint(device, t)}
                        </Text>
                      </View>
                      <ActionButton
                        compact
                        disabled={printerPickerBusy}
                        label={t("action.connect")}
                        onPress={() => void connectPrinterDevice(device.id)}
                        testID={`settings-printer-connect-${device.id}`}
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
                label={t("peripherals.scanPrinters")}
                onPress={() => void scanPrinterDevices()}
                testID="settings-printer-picker-rescan"
                tone="secondary"
              />
            </View>
            </View>
          </HandheldStateSurface>
        </View>
      </Modal>
      </View>
    </HandheldStateSurface>
  );
}

function DevicePane({
  locale,
  onBackToSales,
  onOpenSyncHistory,
  presenter,
  scanner,
  state,
}: Readonly<{
  locale: SettingsLocale;
  onBackToSales: (() => void) | undefined;
  onOpenSyncHistory: (() => void) | undefined;
  presenter: SettingsScreenPresenter;
  scanner: HidScannerRouter | undefined;
  state: SettingsState;
}>) {
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
    <View testID="settings-pane-content-device">
      <PaneHeading subtitle={t("device.subtitle")} title={t("device.title")} />
      <SectionCard
        eyebrow={t("eyebrow.currentBinding")}
        title={t("device.currentBinding")}
      >
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
      <SectionCard
        eyebrow={t("eyebrow.reregister")}
        title={t("device.reregister")}
      >
        <FieldLabel label={t("field.deviceActivationCode")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.deviceActivationCode")}
          autoCapitalize="characters"
          autoCorrect={false}
          editable={!disabled}
          maxLength={96}
          onChangeText={(value) => presenter.setDeviceActivationCode(value)}
          secureTextEntry
          style={styles.textInput}
          testID="settings-reregister-store"
          textContentType="oneTimeCode"
          value={state.deviceActivationCodeDraft}
        />
        <View style={styles.actionRow}>
          <ActionButton
            disabled={disabled || !scanner}
            label={t("device.scanActivationCode")}
            onPress={() => setCameraVisible(true)}
            testID="settings-reregister-scan"
            tone="secondary"
          />
          <ActionButton
            disabled={disabled}
            label={t("device.previewActivationCode")}
            onPress={() => void presenter.previewDeviceReregistration()}
            testID="settings-reregister-preview"
            tone="secondary"
          />
        </View>
        <FieldLabel label={t("field.terminalName")} />
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.terminalName")}
          editable={!disabled}
          onChangeText={(value) => presenter.setTerminalName(value)}
          style={styles.textInput}
          testID="settings-reregister-terminal"
          value={state.terminalNameDraft}
        />
        {state.deviceActivationPreview ? (
          <View testID="settings-reregister-preview-card">
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
            <ActionButton
              disabled={disabled}
              label={t("device.reviewReregistration")}
              onPress={() => void presenter.requestDeviceReregistration()}
              testID="settings-reregister-request"
              tone="danger"
            />
          </View>
        ) : null}
        {state.deviceReregistrationPreflight.kind === "blocked" ||
        state.deviceReregistrationPreflight.kind === "failed" ? (
          <PendingWorkPanel
            locale={locale}
            onBackToSales={onBackToSales}
            onOpenSyncHistory={onOpenSyncHistory}
            onRecheck={() => void presenter.requestDeviceReregistration()}
            preflight={state.deviceReregistrationPreflight}
          />
        ) : null}
      </SectionCard>
      <SectionCard
        eyebrow={t("eyebrow.dangerZone")}
        title={t("device.resetRegistration")}
      >
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
        <ActionButton
          disabled={disabled}
          label={t("device.reviewResetRegistration")}
          onPress={() => presenter.requestDeviceRegistrationReset()}
          testID="settings-device-registration-reset-request"
          tone="danger"
        />
      </SectionCard>
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

function PendingWorkPanel({
  locale,
  onBackToSales,
  onOpenSyncHistory,
  onRecheck,
  preflight,
}: Readonly<{
  locale: SettingsLocale;
  onBackToSales: (() => void) | undefined;
  onOpenSyncHistory: (() => void) | undefined;
  onRecheck(): void;
  preflight: Extract<
    SettingsState["deviceReregistrationPreflight"],
    { kind: "blocked" | "failed" }
  >;
}>) {
  const t = (
    key: SettingsCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => settingsText(locale, key, values);
  const blockers = preflight.kind === "blocked" ? preflight.blockers : [];
  const needsSales = blockers.some(
    ({ code }) => code === "active-cart" || code === "unresolved-payments",
  );
  const needsSyncHistory = blockers.some(
    ({ code }) =>
      code === "pending-sales" ||
      code === "pending-returns" ||
      code === "payment-configuration-sensitive-orders",
  );

  return (
    <View
      accessibilityLiveRegion="polite"
      accessibilityRole="alert"
      style={styles.pendingWorkPanel}
      testID="settings-reregister-blockers"
    >
      <Text style={styles.pendingWorkTitle}>
        {t(
          preflight.kind === "blocked"
            ? "device.reregisterBlockedTitle"
            : "device.reregisterSafetyFailedTitle",
        )}
      </Text>
      <Text style={styles.pendingWorkBody}>
        {t(
          preflight.kind === "blocked"
            ? "device.reregisterBlockedBody"
            : "device.reregisterSafetyFailedBody",
        )}
      </Text>
      {blockers.length > 0 ? (
        <View style={styles.pendingWorkList}>
          {blockers.map((blocker) => (
            <View
              key={blocker.code}
              style={styles.pendingWorkRow}
              testID={`settings-reregister-blocker-${blocker.code}`}
            >
              <Text style={styles.pendingWorkItem}>
                {pendingWorkBlockerCopy(locale, blocker)}
              </Text>
            </View>
          ))}
        </View>
      ) : null}
      {needsSyncHistory && !onOpenSyncHistory ? (
        <Text
          style={styles.pendingWorkPermissionNote}
          testID="settings-reregister-sync-history-permission"
        >
          {t("device.syncHistoryPermissionRequired")}
        </Text>
      ) : null}
      <View style={styles.actionRow}>
        {needsSales && onBackToSales ? (
          <ActionButton
            compact
            label={t("action.backToSales")}
            onPress={onBackToSales}
            testID="settings-reregister-back-to-sales"
            tone="secondary"
          />
        ) : null}
        {needsSyncHistory && onOpenSyncHistory ? (
          <ActionButton
            compact
            label={t("action.viewSyncHistory")}
            onPress={onOpenSyncHistory}
            testID="settings-reregister-open-sync-history"
            tone="secondary"
          />
        ) : null}
        <ActionButton
          compact
          label={t("action.recheck")}
          onPress={onRecheck}
          testID="settings-reregister-recheck"
          tone="danger"
        />
      </View>
    </View>
  );
}

function pendingWorkBlockerCopy(
  locale: SettingsLocale,
  blocker: PendingWorkBlocker,
): string {
  const key = `device.blocker.${blocker.code}` as SettingsCopyKey;
  return settingsText(
    locale,
    key,
    blocker.kind === "count" ? { count: blocker.count } : undefined,
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
  const t = (key: SettingsCopyKey) => settingsText(locale, key);
  return (
    <View testID="settings-pane-content-hardware">
      <PaneHeading
        subtitle={t("hardware.subtitle")}
        title={t("hardware.title")}
      />
      <View style={styles.hardwareGrid}>
        <HardwareCard
          actionLabel={t("hardware.printTest")}
          disabled={
            state.busy ||
            state.confirmation !== null ||
            !state.access.canConfigurePrinter
          }
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
          onPress={() => void presenter.testScanner()}
          status={state.hardware.scannerStatus}
          statusText={hardwareStatusText(locale, state.hardware.scannerStatus)}
          testID="settings-hardware-scanner"
          title={t("peripherals.scanner")}
        />
      </View>
      <SectionCard
        eyebrow={t("eyebrow.lastCapture")}
        title={t("hardware.lastScan")}
      >
        <Text style={styles.scannerValue}>
          {state.hardware.lastScannerValue ?? t("hardware.waitingForTest")}
        </Text>
      </SectionCard>
    </View>
  );
}

function ConfirmationCard({
  busy,
  confirmation,
  deviceCode,
  locale,
  onCancel,
  onConfirm,
  storeCode,
}: Readonly<{
  busy: boolean;
  confirmation: SettingsDangerousConfirmation;
  deviceCode: string;
  locale: SettingsLocale;
  onCancel(): void;
  onConfirm(employeeBarcode?: string): void;
  storeCode: string;
}>) {
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
            {confirmationTitle(
              locale,
              confirmation,
              storeCode,
              deviceCode,
            )}
          </Text>
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
              <FieldLabel
                label={settingsText(locale, "field.employeeBarcode")}
              />
              <PosKeyboardAwareTextInput
                accessibilityLabel={settingsText(
                  locale,
                  "field.employeeBarcode",
                )}
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
            testID="settings-confirm-cancel"
            tone="quiet"
          />
          <ActionButton
            disabled={
              busy || (requiresEmployeeScan && !employeeBarcode.trim())
            }
            label={settingsText(
              locale,
              busy ? "action.checking" : "action.confirm",
            )}
            onPress={submit}
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
}: Readonly<{
  disabled: boolean;
  environment: PaymentEnvironment;
  locale: SettingsLocale;
  onSelect(environment: PaymentEnvironment): void;
  prefix: string;
}>) {
  return (
    <View style={styles.actionRow}>
      <ToggleButton
        disabled={disabled}
        label={settingsText(locale, "environment.production")}
        onPress={() => onSelect("Production")}
        selected={environment === "Production"}
        testID={`${prefix}-production`}
      />
      <ToggleButton
        disabled={disabled}
        label={settingsText(locale, "environment.sandbox")}
        onPress={() => onSelect("Sandbox")}
        selected={environment === "Sandbox"}
        testID={`${prefix}-sandbox`}
      />
    </View>
  );
}

function HardwareCard({
  actionLabel,
  disabled,
  onPress,
  status,
  statusText,
  testID,
  title,
}: Readonly<{
  actionLabel: string;
  disabled: boolean;
  onPress(): void;
  status: string;
  statusText: string;
  testID: string;
  title: string;
}>) {
  return (
    <View style={styles.hardwareCard}>
      <Text style={styles.hardwareTitle}>{title}</Text>
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
      <ActionButton
        disabled={disabled}
        label={actionLabel}
        onPress={onPress}
        testID={testID}
        tone="secondary"
      />
    </View>
  );
}

function PaneHeading({
  subtitle,
  title,
}: Readonly<{ subtitle: string; title: string }>) {
  return (
    <View style={styles.paneHeading}>
      <Text style={styles.paneTitle}>{title}</Text>
      <Text style={styles.paneSubtitle}>{subtitle}</Text>
    </View>
  );
}

function SectionCard({
  children,
  eyebrow,
  style,
  title,
}: Readonly<{
  children: React.ReactNode;
  eyebrow: string;
  style?: StyleProp<ViewStyle>;
  title: string;
}>) {
  return (
    <View style={[styles.sectionCard, style]}>
      <Text style={styles.cardEyebrow}>{eyebrow}</Text>
      <Text style={styles.cardTitle}>{title}</Text>
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
  return (
    <View
      style={[
        styles.availability,
        available ? styles.availabilityReady : styles.availabilityUnavailable,
      ]}
    >
      <Text style={styles.availabilityText}>
        {available
          ? settingsText(locale, "availability.ready")
          : settingsText(locale, "availability.unavailable", {
              blocker: blockerCode ? ` · ${blockerCode}` : "",
            })}
      </Text>
    </View>
  );
}

function Metric({ label, value }: Readonly<{ label: string; value: string }>) {
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
  return <Text style={styles.fieldLabel}>{label}</Text>;
}

function EmptyPanel({
  message,
  testID,
}: Readonly<{ message: string; testID: string }>) {
  return (
    <View style={styles.emptyPanel} testID={testID}>
      <Text style={styles.emptyText}>{message}</Text>
    </View>
  );
}

function StatusBanner({
  locale,
  statusCode,
  blockers,
  receiptProfileVersion,
}: Readonly<{
  locale: SettingsLocale;
  statusCode: SettingsStatusCode;
  blockers: readonly PendingWorkBlocker[];
  /** 「已更新到版本 N」文案用：同步成功后本机已应用的下发版本。 */
  receiptProfileVersion: number;
}>) {
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
      <Text style={styles.statusText}>
        {statusCopy(locale, statusCode, { version: receiptProfileVersion })}
      </Text>
      {statusCode === "pending-local-data"
        ? blockers.map((blocker) => (
            <Text key={blocker.code} style={styles.statusText}>
              {pendingWorkBlockerCopy(locale, blocker)}
            </Text>
          ))
        : null}
      <Text style={styles.statusCode}>[{statusCode}]</Text>
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
  return (
    <ActionButton
      disabled={disabled}
      label={`${selected ? "✓ " : ""}${label}`}
      onPress={onPress}
      selected={selected}
      testID={testID}
      tone="secondary"
    />
  );
}

function ActionButton({
  compact = false,
  disabled = false,
  label,
  onPress,
  selected = false,
  style,
  testID,
  tone = "primary",
}: Readonly<{
  compact?: boolean;
  disabled?: boolean;
  label: string;
  onPress(): void;
  selected?: boolean;
  style?: StyleProp<ViewStyle>;
  testID: string;
  tone?: "danger" | "nav" | "primary" | "quiet" | "secondary";
}>) {
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled, selected }}
      disabled={disabled}
      onPress={onPress}
      sound={
        tone === "danger" ? "danger" : tone === "nav" ? "navigate" : "tap"
      }
      style={({ pressed }) => [
        styles.button,
        compact && styles.compactButton,
        tone === "secondary" && styles.secondaryButton,
        tone === "quiet" && styles.quietButton,
        tone === "danger" && styles.dangerButton,
        tone === "nav" && styles.navButton,
        selected && styles.selectedButton,
        disabled && styles.disabledButton,
        pressed && !disabled && styles.pressedButton,
        style,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.buttonLabel,
          (tone === "secondary" || tone === "quiet" || tone === "nav") &&
            styles.secondaryButtonLabel,
          selected && styles.selectedButtonLabel,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
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
      return settingsText(locale, "confirmation.pairLinkly");
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

/**
 * 系统设置里蓝牙扫描权限的名称随 Android 版本变化：12+ 叫“附近的设备”，
 * 10/11 归在“位置信息”下，没有单独的蓝牙项；非 Android 沿用通用提示。
 */
function bluetoothPermissionHintKey(): SettingsCopyKey {
  if (Platform.OS !== "android") {
    return "printer.bluetoothPermissionHint";
  }
  return androidApiLevel({ os: Platform.OS, version: Platform.Version }) >=
    ANDROID_NEARBY_DEVICES_MIN_API
    ? "printer.bluetoothPermissionHintNearby"
    : "printer.bluetoothPermissionHintLocation";
}

/**
 * 与移动端一致：BLE 直接连接；经典蓝牙按系统配对状态提示，未知（旧原生包）时统一提示先配对。
 */
function printerTransportHint(
  device: SettingsPrinterDevice,
  t: (key: SettingsCopyKey) => string,
): string {
  if (device.transport === "bluetooth-le") return t("printer.transport.ble");
  if (device.transport !== "bluetooth-classic") return device.transport;
  if (device.paired === true) return t("printer.transport.classicPaired");
  if (device.paired === false) return t("printer.transport.classicUnpaired");
  return t("printer.transport.classic");
}

/** 经典蓝牙打印机需在系统里配对；个别 ROM 不支持该 Intent 时退回应用详情页。 */
async function openBluetoothSettings(): Promise<void> {
  try {
    await Linking.sendIntent("android.settings.BLUETOOTH_SETTINGS");
  } catch {
    await Linking.openSettings();
  }
}

/** 直达系统定位开关页；个别 ROM 不支持该 Intent 时退回应用详情页。 */
async function openLocationSettings(): Promise<void> {
  try {
    await Linking.sendIntent("android.settings.LOCATION_SOURCE_SETTINGS");
  } catch {
    await Linking.openSettings();
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

const styles = StyleSheet.create({
  safeArea: { flex: 1, backgroundColor: posColors.canvas },
  stateSurface: { flex: 1 },
  // 页面级滚动容器：整页（标题/导航/内容）一起上下滚动，避免小屏手持端固定区域挤占内容区。
  page: { flexGrow: 1, gap: 14, padding: 16, paddingBottom: 28 },
  pageScroll: { flex: 1 },
  header: {
    alignItems: "flex-start",
    flexDirection: "column",
    gap: 8,
    justifyContent: "space-between",
  },
  titleGroup: { width: "100%" },
  eyebrow: {
    color: posColors.blue,
    fontSize: 12,
    fontWeight: "800",
    letterSpacing: 1.1,
  },
  title: {
    color: posColors.ink,
    fontSize: 26,
    fontWeight: "800",
    marginTop: 5,
  },
  subtitle: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 5,
  },
  // 内容区容器：位于页面级滚动容器内，按列排布各分区卡片。
  workspace: {
    flexDirection: "column",
    gap: 14,
  },
  navigation: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    padding: 12,
    width: "100%",
  },
  navigationTitle: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "800",
    letterSpacing: 0.8,
    marginBottom: 0,
    paddingHorizontal: 6,
    width: "100%",
  },
  navButton: {
    alignItems: "flex-start",
    backgroundColor: "transparent",
    borderColor: "transparent",
    flexGrow: 1,
    marginTop: 0,
    minWidth: 120,
  },
  deviceBadge: {
    backgroundColor: posColors.blueSoft,
    borderRadius: 6,
    marginTop: 0,
    padding: 12,
    width: "100%",
  },
  badgeLabel: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontWeight: "700",
  },
  badgeValue: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
    marginTop: 4,
  },
  badgeMeta: {
    color: posColors.blue,
    fontSize: 13,
    fontWeight: "700",
    marginTop: 2,
  },
  paneHeading: { marginBottom: 14 },
  paneTitle: { color: posColors.ink, fontSize: 22, fontWeight: "800" },
  paneSubtitle: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 6,
  },
  sectionCard: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    marginBottom: 14,
    padding: 16,
  },
  columnCard: { flex: 1, marginBottom: 0 },
  cardEyebrow: {
    color: posColors.blue,
    fontSize: 11,
    fontWeight: "800",
    letterSpacing: 0.9,
  },
  cardTitle: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "800",
    marginBottom: 14,
    marginTop: 4,
  },
  sectionCopy: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    marginBottom: 14,
  },
  soundPreferenceRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 16,
    minHeight: SETTINGS_MIN_TOUCH_TARGET,
  },
  soundPreferenceCopy: { flex: 1 },
  soundPreferenceDivider: {
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    marginBottom: 14,
    paddingBottom: 14,
  },
  soundPreferenceLabel: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 20,
    marginBottom: 4,
  },
  soundPreferenceHint: { fontSize: 13, lineHeight: 19, marginBottom: 0 },
  soundSwitch: { minHeight: SETTINGS_MIN_TOUCH_TARGET, minWidth: 52 },
  twoColumn: { flexDirection: "column", gap: 14 },
  actionRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    marginTop: 12,
  },
  metricRow: { flexDirection: "row", flexWrap: "wrap", gap: 10 },
  metric: {
    backgroundColor: posColors.blueSoft,
    borderRadius: 6,
    flex: 1,
    minWidth: 116,
    padding: 12,
  },
  metricLabel: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontWeight: "700",
  },
  metricValue: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "800",
    marginTop: 4,
  },
  squareSummaryRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    marginTop: 12,
  },
  squareSummaryMetric: {
    backgroundColor: posColors.blueSoft,
    borderRadius: 6,
    flex: 1,
    minWidth: 116,
    padding: 12,
  },
  squareSummaryValue: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
    lineHeight: 19,
    marginTop: 4,
  },
  linklySetupCard: {
    backgroundColor: "#FAFAF8",
    borderColor: posColors.border,
    borderRadius: 8,
    borderWidth: 1,
    marginTop: 12,
    padding: 12,
  },
  linklyTerminalList: {
    gap: 8,
    marginBottom: 4,
  },
  linklyTerminalOption: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 8,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: SETTINGS_MIN_TOUCH_TARGET,
    paddingHorizontal: 12,
    paddingVertical: 9,
  },
  linklyTerminalOptionSelected: {
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.blue,
  },
  linklyTerminalName: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
    lineHeight: 19,
  },
  linklyTerminalStatus: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "600",
    lineHeight: 17,
    marginTop: 2,
  },
  linklySetupStatus: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 19,
    marginTop: 12,
  },
  squareFieldGroup: { marginTop: 3 },
  squareSelectionRow: {
    alignItems: "stretch",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  squareSelectionValue: {
    backgroundColor: "#FAFAF8",
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minHeight: 56,
    minWidth: 140,
    paddingHorizontal: 13,
    paddingVertical: 8,
  },
  squareSelectionDisabled: { opacity: 0.54 },
  squareSelectionPrimary: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 20,
  },
  squareSelectionMetaRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
    marginTop: 3,
  },
  squareSelectionSecondary: {
    color: posColors.mutedInk,
    flexShrink: 1,
    fontSize: 12,
    fontWeight: "600",
    lineHeight: 17,
  },
  squareSelectionError: { color: posColors.red },
  squareLoadButton: { marginTop: 0 },
  squareFieldHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
    marginTop: 5,
  },
  squareDeviceCodeSection: {
    borderTopColor: posColors.border,
    borderTopWidth: 1,
    marginTop: 16,
    paddingTop: 4,
  },
  squareSandboxNote: {
    backgroundColor: posColors.blueSoft,
    borderRadius: 6,
    color: posColors.blue,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 19,
    marginTop: 12,
    padding: 10,
  },
  squarePairingNote: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
    marginTop: 10,
  },
  squareResourceState: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    marginTop: 12,
    minHeight: SETTINGS_MIN_TOUCH_TARGET,
  },
  squareResourceText: {
    color: posColors.mutedInk,
    flex: 1,
    fontSize: 13,
    lineHeight: 19,
  },
  squareDeviceCodeList: { gap: 8, marginTop: 12 },
  squareDeviceCodeRow: {
    alignItems: "center",
    backgroundColor: "#FAFAF8",
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 54,
    padding: 10,
  },
  squareDeviceCodeSelected: {
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.blue,
  },
  squareDeviceCodeIdentity: { flex: 1, minWidth: 0 },
  squareCurrentTag: {
    backgroundColor: posColors.blue,
    borderRadius: 999,
    color: "#FFFFFF",
    fontSize: 11,
    fontWeight: "800",
    overflow: "hidden",
    paddingHorizontal: 8,
    paddingVertical: 3,
  },
  fieldLabel: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
    marginBottom: 5,
    marginTop: 9,
  },
  textInput: {
    backgroundColor: "#FAFAF8",
    borderColor: posColors.border,
    borderRadius: 8,
    borderWidth: 1,
    color: posColors.ink,
    fontSize: 16,
    minHeight: SETTINGS_MIN_TOUCH_TARGET,
    paddingHorizontal: 13,
    paddingVertical: 9,
  },
  fieldHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
    marginTop: 5,
  },
  dangerDescription: {
    color: posColors.red,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 20,
    marginBottom: 12,
  },
  pendingWorkPanel: {
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
    borderRadius: 6,
    borderWidth: 1,
    marginTop: 14,
    padding: 12,
  },
  pendingWorkTitle: {
    color: posColors.red,
    fontSize: 16,
    fontWeight: "800",
    lineHeight: 22,
  },
  pendingWorkBody: {
    color: posColors.ink,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 6,
  },
  pendingWorkList: { gap: 8, marginTop: 12 },
  pendingWorkRow: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    minHeight: 44,
    paddingHorizontal: 11,
    paddingVertical: 10,
  },
  pendingWorkItem: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 20,
  },
  pendingWorkPermissionNote: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 12,
  },
  multilineTextInput: {
    backgroundColor: "#FAFAF8",
    borderColor: posColors.border,
    borderRadius: 8,
    borderWidth: 1,
    color: posColors.ink,
    fontSize: 16,
    minHeight: 96,
    paddingHorizontal: 13,
    paddingVertical: 9,
    textAlignVertical: "top",
  },
  button: {
    alignItems: "center",
    alignSelf: "flex-start",
    backgroundColor: posColors.orange,
    borderColor: posColors.orange,
    borderRadius: 8,
    borderWidth: 1,
    justifyContent: "center",
    marginTop: 12,
    minHeight: SETTINGS_MIN_TOUCH_TARGET,
    paddingHorizontal: 17,
    paddingVertical: 9,
  },
  compactButton: {
    marginTop: 0,
    minWidth: 110,
    paddingHorizontal: 12,
  },
  buttonLabel: {
    color: "#FFFFFF",
    fontSize: 14,
    fontWeight: "800",
    textAlign: "center",
  },
  secondaryButton: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
  },
  quietButton: {
    backgroundColor: "transparent",
    borderColor: posColors.border,
  },
  dangerButton: {
    backgroundColor: posColors.red,
    borderColor: posColors.red,
  },
  secondaryButtonLabel: { color: posColors.ink },
  selectedButton: {
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.blue,
  },
  selectedButtonLabel: { color: posColors.blue },
  disabledButton: { opacity: 0.42 },
  pressedButton: { opacity: 0.78 },
  safetyNote: {
    color: posColors.green,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 20,
    marginTop: 12,
  },
  backgroundNote: {
    color: posColors.blue,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 20,
    marginTop: 6,
  },
  catalogRefreshStatus: {
    backgroundColor: posColors.blueSoft,
    borderRadius: 8,
    marginTop: 12,
    padding: 12,
  },
  catalogRefreshWarning: {
    backgroundColor: "#FFF5DB",
  },
  catalogRefreshFailed: {
    backgroundColor: posColors.redSoft,
  },
  catalogRefreshHeading: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
  },
  catalogRefreshTitle: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  catalogRefreshTrack: {
    backgroundColor: posColors.surface,
    borderRadius: 999,
    height: 7,
    marginTop: 10,
    overflow: "hidden",
  },
  catalogRefreshFill: {
    backgroundColor: posColors.orange,
    borderRadius: 999,
    height: "100%",
  },
  catalogRefreshMeta: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 18,
    marginTop: 5,
  },
  catalogRefreshCode: {
    color: posColors.red,
    fontFamily: "Courier",
    fontSize: 11,
    fontWeight: "700",
    marginTop: 6,
  },
  availability: {
    alignSelf: "flex-start",
    borderRadius: 999,
    marginBottom: 10,
    paddingHorizontal: 11,
    paddingVertical: 6,
  },
  availabilityReady: { backgroundColor: posColors.greenSoft },
  availabilityUnavailable: { backgroundColor: posColors.redSoft },
  availabilityText: {
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "800",
  },
  deviceRow: {
    alignItems: "stretch",
    borderColor: posColors.border,
    borderTopWidth: 1,
    flexDirection: "column",
    gap: 12,
    justifyContent: "space-between",
    marginTop: 12,
    paddingTop: 12,
  },
  deviceIdentity: { flex: 1 },
  deviceDetailRow: {
    alignItems: "flex-start",
    flexDirection: "column",
    gap: 8,
    minHeight: 20,
  },
  deviceFieldLabel: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
    width: "100%",
  },
  deviceNameRow: {
    alignItems: "center",
    flex: 1,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  deviceName: { color: posColors.ink, fontSize: 15, fontWeight: "800" },
  preferredPrinterTag: {
    alignSelf: "flex-start",
    backgroundColor: posColors.blueSoft,
    borderRadius: 999,
    color: posColors.blue,
    fontSize: 11,
    fontWeight: "800",
    lineHeight: 15,
    overflow: "hidden",
    paddingHorizontal: 7,
    paddingVertical: 2,
  },
  deviceMeta: {
    color: posColors.mutedInk,
    flex: 1,
    fontSize: 12,
    fontWeight: "600",
  },
  deviceTransport: {
    color: posColors.mutedInk,
    fontSize: 11,
    marginLeft: 0,
    marginTop: 2,
  },
  deviceTransportWarning: {
    color: posColors.orange,
    fontWeight: "700",
  },
  printerPicker: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxHeight: "82%",
    maxWidth: 520,
    padding: 16,
    width: "100%",
  },
  printerPickerState: {
    alignItems: "center",
    // 建立确定高度链：overlay(flex:1) → stateSurface(flex:1) → picker(maxHeight 82%)。
    // 包装层随内容自适应时，82% 会按卡片自身内容高度计算，底部按钮被挤出卡片。
    flex: 1,
    justifyContent: "center",
    // 包装层铺满遮罩后不能拦截卡片外的点击，背景关闭仍由 backdrop 处理。
    pointerEvents: "box-none",
    width: "100%",
  },
  squarePicker: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxHeight: "82%",
    maxWidth: 760,
    padding: 20,
    shadowColor: "#000000",
    shadowOffset: { height: 5, width: 0 },
    shadowOpacity: 0.18,
    shadowRadius: 10,
    width: "86%",
  },
  squarePickerBody: { gap: 8, paddingBottom: 2 },
  squarePickerOption: {
    backgroundColor: "#FAFAF8",
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    minHeight: 58,
    padding: 11,
  },
  printerPickerHeader: { marginBottom: 12 },
  printerPickerTitle: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "800",
  },
  printerPickerHint: {
    color: posColors.mutedInk,
    fontSize: 13,
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
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "700",
    marginTop: 12,
  },
  printerPickerEmpty: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 21,
    paddingVertical: 24,
    textAlign: "center",
  },
  printerPickerError: {
    backgroundColor: posColors.redSoft,
    borderRadius: 8,
    color: posColors.red,
    fontSize: 13,
    fontWeight: "700",
    padding: 10,
  },
  printerPickerErrorBody: { minHeight: 92 },
  printerPickerPermissionHint: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 21,
    marginTop: 10,
  },
  printerPickerPermissionAction: { alignSelf: "flex-start", marginTop: 12 },
  printerPickerActions: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    justifyContent: "flex-end",
    marginTop: 16,
  },
  hardwareGrid: { flexDirection: "column", gap: 14, marginBottom: 14 },
  hardwareCard: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    flex: 1,
    padding: 18,
  },
  hardwareTitle: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "800",
  },
  hardwareStatus: {
    fontSize: 14,
    fontWeight: "700",
    marginBottom: 8,
    marginTop: 8,
  },
  hardwareStatusReady: { color: posColors.green },
  hardwareStatusDisconnected: { color: posColors.red },
  hardwareStatusUnavailable: { color: posColors.mutedInk },
  scannerValue: {
    color: posColors.ink,
    fontFamily: "Courier",
    fontSize: 24,
    fontWeight: "800",
  },
  confirmation: {
    alignItems: "stretch",
    backgroundColor: "#FFF3F0",
    borderColor: posColors.red,
    borderRadius: 6,
    borderWidth: 1,
    flexDirection: "column",
    gap: 20,
    maxWidth: 520,
    padding: 16,
    width: "100%",
  },
  confirmationOverlay: {
    alignItems: "center",
    backgroundColor: "rgba(16, 37, 58, 0.45)",
    flex: 1,
    justifyContent: "center",
    padding: 16,
  },
  modalDismissArea: {
    bottom: 0,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  confirmationCopy: { flex: 1 },
  confirmationScroll: { width: "100%" },
  confirmationScrollContent: { gap: 20 },
  confirmationTitle: {
    color: posColors.red,
    fontSize: 18,
    fontWeight: "800",
  },
  confirmationBody: {
    color: posColors.ink,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 6,
  },
  confirmationScan: { marginTop: 8 },
  confirmationActions: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  statusBanner: {
    alignItems: "stretch",
    borderRadius: 6,
    flexDirection: "column",
    gap: 8,
    marginBottom: 14,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  statusSuccess: { backgroundColor: posColors.greenSoft },
  statusWarning: { backgroundColor: posColors.redSoft },
  statusText: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "700",
  },
  statusCode: {
    color: posColors.mutedInk,
    fontFamily: "Courier",
    fontSize: 11,
  },
  emptyPanel: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minHeight: 320,
    padding: 28,
  },
  emptyText: {
    color: posColors.mutedInk,
    fontSize: 18,
    fontWeight: "700",
    textAlign: "center",
  },
  unavailable: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    padding: 40,
  },
  unavailableTitle: {
    color: posColors.ink,
    fontSize: 28,
    fontWeight: "800",
    marginTop: 10,
    textAlign: "center",
  },
  unavailableHint: {
    color: posColors.mutedInk,
    fontSize: 16,
    lineHeight: 24,
    marginTop: 10,
    maxWidth: 680,
    textAlign: "center",
  },
});
