import { MaterialCommunityIcons } from "@expo/vector-icons";
import { receiptCode128Runs } from "@hb/pos-receipt-core/features/local-history/receipt-code128";
import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import {
  ActivityIndicator,
  Linking,
  Modal,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";


import {
  appendScannerTestEntry,
  createScannerTestEntry,
  type ScannerTestEntry,
} from "./ble-scanner-test-session";
import {
  settingsText,
  type SettingsCopyKey,
  type SettingsLocale,
} from "./settings-copy";

import {
  HidScannerCapture,
  type HidScannerRouter,
  isDeviceActivationScanValue,
} from "@/core/peripherals/scanner";
import type {
  BleDiscoveredDevice,
  BleScannerConnectionState,
  BleScannerStatus,
} from "@/core/peripherals/scanner/ble/ble-scanner-controller";
import {
  BLE_SCANNER_PROFILES,
  bleDeviceSuffix,
  type BleScannerProfile,
  findBleScannerProfile,
  guessBarcodeSymbology,
  looksLikeBleScanner,
  matchBleScannerProfile,
  normalizeBleScannerAlias,
  scanValueTail,
} from "@/core/peripherals/scanner/ble/ble-scanner-core";
import type { BleScannerService } from "@/core/peripherals/scanner/ble/ble-scanner-service";
import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPill, type PosPillTone } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSwitch } from "@/ui/controls/pos-switch";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

/** 搜索 15 秒自动停止，省电，也避免和打印机的蓝牙操作长时间并行。 */
const DISCOVERY_SECONDS = 15;

type Translate = (
  key: SettingsCopyKey,
  values?: Readonly<Record<string, string | number>>,
) => string;

function useTranslate(locale: SettingsLocale): Translate {
  return (key, values) => settingsText(locale, key, values);
}

function useBleStatus(service: BleScannerService): BleScannerStatus {
  return useSyncExternalStore(
    (listener) => service.subscribeStatus(listener),
    () => service.getStatus(),
    () => service.getStatus(),
  );
}

/* ------------------------------------------------------------------ */
/* 外设页卡片                                                            */
/* ------------------------------------------------------------------ */

/**
 * 设置 → 外设「蓝牙扫码枪」卡片：状态、电量、最近一次扫码尾号，以及搜索 / 测试 / 设置码 / 断开 / 忘记。
 * 只在原生包带 ble-plx（服务不为 null）时由设置页渲染。
 */
export function BleScannerCard({
  disabled,
  locale,
  router,
  service,
  testDisabled,
}: Readonly<{
  /** 配置类操作（搜索、断开、忘记）禁用：忙碌、确认弹窗中或没有外设配置权限。 */
  disabled: boolean;
  locale: SettingsLocale;
  router: HidScannerRouter | undefined;
  service: BleScannerService;
  testDisabled: boolean;
}>) {
  usePosThemeVersion();
  const t = useTranslate(locale);
  const status = useBleStatus(service);
  const [pairVisible, setPairVisible] = useState(false);
  const [codesVisible, setCodesVisible] = useState(false);
  const [testVisible, setTestVisible] = useState(false);
  const saved = status.saved;
  const profile = findBleScannerProfile(saved?.profileId);
  const hasCodes = (profile?.settingCodes.length ?? 0) > 0;
  const linkActive =
    status.state === "ready" ||
    status.state === "learning" ||
    status.state === "connecting" ||
    status.state === "reconnecting" ||
    status.state === "sleeping";
  const bluetoothBlocked =
    status.state === "bluetooth-off" || status.state === "unauthorized";

  return (
    <View style={styles.card} testID="settings-ble-scanner-card">
      <View style={styles.cardHeader}>
        <View style={styles.cardHeaderCopy}>
          <View style={styles.cardTitleRow}>
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.ink}
              name="barcode-scan"
              size={20}
            />
            <Text style={styles.cardTitle}>{t("bleScanner.title")}</Text>
            <PosPill
              label={t(stateCopyKey(status.state))}
              testID="settings-ble-scanner-status"
              tone={stateTone(status.state)}
            />
            {status.attention && status.batteryLevel !== null && status.batteryLevel < 20 ? (
              <PosPill label={t("bleScanner.lowBattery")} tone="warning" />
            ) : null}
          </View>
          <Text style={styles.cardHint}>{t("bleScanner.hint")}</Text>
        </View>
      </View>

      {saved ? (
        <View style={styles.metricGrid}>
          <MetricTile
            label={t("bleScanner.field.device")}
            testID="settings-ble-scanner-device"
            value={`${saved.alias ?? (saved.name || t("bleScanner.pair.unnamed"))} · ${bleDeviceSuffix(saved.peripheralId)}`}
            detail={saved.alias ? saved.name : null}
          />
          <MetricTile
            label={t("bleScanner.field.channel")}
            value={t(
              profile
                ? "bleScanner.channel.known"
                : saved.notifyUuid
                  ? "bleScanner.channel.learned"
                  : "bleScanner.channel.pending",
            )}
          />
          <MetricTile
            label={t("bleScanner.field.signal")}
            value={
              status.rssi === null || !linkActive
                ? "—"
                : t("bleScanner.signal.value", { rssi: status.rssi })
            }
          />
          <MetricTile
            label={t("bleScanner.field.battery")}
            testID="settings-ble-scanner-battery"
            value={status.batteryLevel === null ? "—" : `${status.batteryLevel}%`}
          />
          <MetricTile
            label={t("bleScanner.field.lastScan")}
            mono
            testID="settings-ble-scanner-last-scan"
            value={lastScanText(status)}
          />
        </View>
      ) : null}

      {bluetoothBlocked ? (
        <View style={[styles.banner, styles.bannerWarning]}>
          <Text style={styles.bannerText}>{t("bleScanner.bluetoothHint")}</Text>
          <PosButton
            icon="open-in-new"
            label={settingsText(locale, "action.openSystemSettings")}
            onPress={() => void Linking.openSettings()}
            size="small"
            testID="settings-ble-scanner-open-system-settings"
            tone="secondary"
          />
        </View>
      ) : null}

      <View style={styles.actionRow}>
        {saved ? (
          <>
            <PosButton
              disabled={testDisabled}
              icon="barcode-scan"
              label={t("bleScanner.test")}
              onPress={() => setTestVisible(true)}
              size="medium"
              testID="settings-ble-scanner-test"
              tone="primary"
            />
            <PosButton
              disabled={disabled}
              icon="swap-horizontal"
              label={t("bleScanner.replace")}
              onPress={() => setPairVisible(true)}
              size="medium"
              testID="settings-ble-scanner-replace"
              tone="secondary"
            />
            {hasCodes ? (
              <PosButton
                disabled={disabled}
                icon="barcode"
                label={t("bleScanner.settingCodes")}
                onPress={() => setCodesVisible(true)}
                size="medium"
                testID="settings-ble-scanner-codes"
                tone="secondary"
              />
            ) : null}
            {linkActive ? (
              <PosButton
                disabled={disabled}
                icon="link-variant-off"
                label={t("bleScanner.disconnect")}
                onPress={() => void service.disconnect()}
                size="medium"
                testID="settings-ble-scanner-disconnect"
                tone="secondary"
              />
            ) : (
              <PosButton
                disabled={disabled || bluetoothBlocked}
                icon="bluetooth-connect"
                label={t("bleScanner.reconnect")}
                onPress={() => void service.reconnect()}
                size="medium"
                testID="settings-ble-scanner-reconnect"
                tone="secondary"
              />
            )}
            <PosButton
              disabled={disabled}
              icon="link-off"
              label={t("bleScanner.forget")}
              onPress={() => void service.forget()}
              size="medium"
              sound="danger"
              testID="settings-ble-scanner-forget"
              tone="danger"
            />
          </>
        ) : (
          <PosButton
            disabled={disabled || bluetoothBlocked}
            icon="bluetooth-connect"
            label={t("bleScanner.find")}
            onPress={() => setPairVisible(true)}
            size="medium"
            testID="settings-ble-scanner-find"
            tone="primary"
          />
        )}
      </View>

      <View style={[styles.banner, styles.bannerInfo]}>
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.blue}
          name="keyboard-outline"
          size={18}
        />
        <Text style={styles.bannerText}>{t("bleScanner.hidNote")}</Text>
      </View>

      <BleScannerPairModal
        locale={locale}
        onClose={() => setPairVisible(false)}
        service={service}
        visible={pairVisible}
      />
      <Modal
        animationType="fade"
        onRequestClose={() => setCodesVisible(false)}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="settings-ble-scanner-codes-modal"
        transparent
        visible={codesVisible && profile !== null}
      >
        <View accessibilityViewIsModal style={styles.overlay}>
          <PosPressable
            accessible={false}
            onPress={() => setCodesVisible(false)}
            sound="navigate"
            style={styles.backdrop}
          />
          <View style={[styles.sheet, styles.sheetWide]}>
            {profile ? (
              <SettingCodesContent
                doneLabel={t("bleScanner.codes.done")}
                locale={locale}
                onDone={() => setCodesVisible(false)}
                profile={profile}
              />
            ) : null}
          </View>
        </View>
      </Modal>
      <Modal
        animationType="fade"
        onRequestClose={() => setTestVisible(false)}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="settings-ble-scanner-test-modal"
        transparent
        visible={testVisible}
      >
        <View accessibilityViewIsModal style={styles.overlay}>
          <View style={[styles.sheet, styles.sheetWide]}>
            {testVisible ? (
              <BleScannerTestPanel
                locale={locale}
                onClose={() => setTestVisible(false)}
                router={router}
                service={service}
              />
            ) : null}
          </View>
        </View>
      </Modal>
    </View>
  );
}

function MetricTile({
  detail = null,
  label,
  mono = false,
  testID,
  value,
}: Readonly<{
  detail?: string | null;
  label: string;
  mono?: boolean;
  testID?: string;
  value: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.metricTile} testID={testID}>
      <Text style={styles.metricLabel}>{label}</Text>
      <Text numberOfLines={1} style={[styles.metricValue, mono && styles.mono]}>
        {value}
      </Text>
      {detail ? (
        <Text numberOfLines={1} style={styles.metricDetail}>
          {detail}
        </Text>
      ) : null}
    </View>
  );
}

/* ------------------------------------------------------------------ */
/* 搜索并连接                                                            */
/**
 * 登录页「蓝牙扫码枪」面板：登录前就能配对、连接、测试扫码枪，不必先登录再进设置页。
 *
 * - 以路由内的遮罩层呈现，而不是再包一层原生 Modal：卡片里的配对 / 测试 / 设置码本身就是 Modal，
 *   避免 Modal 套 Modal 在 iOS 上叠放异常。
 * - 登录前没有收银员，设置页的外设权限闸在这里不适用；扫码枪配对只写本机配置，
 *   不涉及订单、支付或员工数据，所以配置类与测试操作都放开。
 */
export function BleScannerLoginPanel({
  locale,
  onClose,
  router,
  service,
}: Readonly<{
  locale: SettingsLocale;
  onClose(): void;
  router: HidScannerRouter | undefined;
  service: BleScannerService;
}>) {
  usePosThemeVersion();
  const t = useTranslate(locale);
  return (
    <View
      accessibilityViewIsModal
      style={[styles.overlay, styles.loginPanelOverlay]}
      testID="login-ble-scanner-panel"
    >
      <PosPressable
        accessible={false}
        onPress={onClose}
        sound="navigate"
        style={styles.backdrop}
        testID="login-ble-scanner-backdrop"
      />
      <View style={[styles.sheet, styles.sheetWide]}>
        <ScrollView
          contentContainerStyle={styles.loginPanelBody}
          keyboardShouldPersistTaps="handled"
          style={styles.loginPanelScroll}
        >
          <BleScannerCard
            disabled={false}
            locale={locale}
            router={router}
            service={service}
            testDisabled={false}
          />
        </ScrollView>
        <View style={styles.sheetActions}>
          <PosButton
            icon="close"
            label={t("bleScanner.loginClose")}
            onPress={onClose}
            size="medium"
            testID="login-ble-scanner-close"
            tone="primary"
          />
        </View>
      </View>
    </View>
  );
}

/* ------------------------------------------------------------------ */

type PairView = "list" | "codes" | "alias";

/**
 * 搜索 / 连接 / 起备注名 / 显示设置码放在同一个 Modal 里切换页面：
 * iOS 同一帧关一个原生 Modal 再开另一个，第二个会被丢弃。
 */
function BleScannerPairModal({
  locale,
  onClose,
  service,
  visible,
}: Readonly<{
  locale: SettingsLocale;
  onClose(): void;
  service: BleScannerService;
  visible: boolean;
}>) {
  usePosThemeVersion();
  const t = useTranslate(locale);
  const status = useBleStatus(service);
  const [view, setView] = useState<PairView>("list");
  const [scanRound, setScanRound] = useState(0);
  const [devices, setDevices] = useState<readonly BleDiscoveredDevice[]>([]);
  const [secondsLeft, setSecondsLeft] = useState(0);
  const [connectingId, setConnectingId] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const [aliasDraft, setAliasDraft] = useState("");
  // 唯一带设置码的已知型号（门店背夹）：此时扫码枪还在键盘模式、不会出现在列表里，
  // 所以搜索页一直提供「显示设置码」，不依赖已发现的设备。
  const codesProfile =
    BLE_SCANNER_PROFILES.find((profile) => profile.settingCodes.length > 0) ?? null;

  useEffect(() => {
    if (visible) {
      setView("list");
      setFailed(false);
      setScanRound((round) => round + 1);
    }
  }, [visible]);

  useEffect(() => {
    if (!visible || view !== "list" || scanRound === 0) return undefined;
    setDevices([]);
    setSecondsLeft(DISCOVERY_SECONDS);
    const stop = service.startDiscovery(setDevices);
    const startedAt = Date.now();
    const timer = setInterval(() => {
      const left = DISCOVERY_SECONDS - Math.floor((Date.now() - startedAt) / 1000);
      if (left <= 0) {
        clearInterval(timer);
        stop();
        setSecondsLeft(0);
        return;
      }
      setSecondsLeft(left);
    }, 500);
    return () => {
      clearInterval(timer);
      stop();
    };
  }, [scanRound, service, view, visible]);

  const busy = connectingId !== null;
  const close = () => {
    if (busy) return;
    onClose();
  };
  const connect = async (device: BleDiscoveredDevice) => {
    setConnectingId(device.id);
    setFailed(false);
    try {
      await service.pair(device);
      setAliasDraft(service.getStatus().saved?.alias ?? "");
      setView("alias");
    } catch {
      setFailed(true);
    } finally {
      setConnectingId(null);
    }
  };
  const saveAlias = async () => {
    await service.setAlias(normalizeBleScannerAlias(aliasDraft)).catch(() => undefined);
    onClose();
  };

  return (
    <Modal
      animationType="fade"
      onRequestClose={close}
      supportedOrientations={["landscape-left", "landscape-right"]}
      testID="settings-ble-scanner-pair-modal"
      transparent
      visible={visible}
    >
      <View accessibilityViewIsModal style={styles.overlay}>
        <PosPressable
          accessible={false}
          onPress={close}
          sound="navigate"
          style={styles.backdrop}
        />
        <View
          style={[styles.sheet, view === "codes" && styles.sheetWide]}
          testID="settings-ble-scanner-pair"
        >
          {view === "codes" && codesProfile ? (
            <SettingCodesContent
              doneLabel={t("bleScanner.codes.back")}
              locale={locale}
              onDone={() => setView("list")}
              profile={codesProfile}
            />
          ) : view === "alias" ? (
            <PosKeyboardAwareScrollView
              contentContainerStyle={styles.aliasBody}
              keyboardShouldPersistTaps="handled"
              testID="settings-ble-scanner-alias"
            >
              <View style={styles.sheetHeader}>
                <View style={styles.cardTitleRow}>
                  <Text style={styles.sheetTitle}>{t("bleScanner.alias.title")}</Text>
                  <PosPill
                    label={t(stateCopyKey(status.state))}
                    tone={stateTone(status.state)}
                  />
                </View>
                <Text style={styles.sheetHint}>{t("bleScanner.alias.hint")}</Text>
              </View>
              <View style={styles.inputFrame}>
                <Text style={styles.inputPrefix}>{t("bleScanner.alias.label")}</Text>
                <PosKeyboardAwareTextInput
                  accessibilityLabel={t("bleScanner.alias.label")}
                  autoCorrect={false}
                  maxLength={24}
                  onChangeText={setAliasDraft}
                  onSubmitEditing={() => void saveAlias()}
                  returnKeyType="done"
                  style={styles.inputField}
                  testID="settings-ble-scanner-alias-input"
                  value={aliasDraft}
                />
              </View>
              <View style={styles.sheetActions}>
                <PosButton
                  label={t("bleScanner.alias.skip")}
                  onPress={onClose}
                  size="medium"
                  testID="settings-ble-scanner-alias-skip"
                  tone="quiet"
                />
                <PosButton
                  icon="content-save-outline"
                  label={t("bleScanner.alias.save")}
                  onPress={() => void saveAlias()}
                  size="medium"
                  testID="settings-ble-scanner-alias-save"
                  tone="primary"
                />
              </View>
            </PosKeyboardAwareScrollView>
          ) : (
            <>
              <View style={styles.sheetHeaderRow}>
                <View style={styles.sheetHeader}>
                  <Text style={styles.sheetTitle}>{t("bleScanner.pair.title")}</Text>
                  <Text style={styles.sheetHint}>{t("bleScanner.pair.hint")}</Text>
                </View>
                <CloseButton
                  disabled={busy}
                  label={settingsText(locale, "action.cancel")}
                  onPress={close}
                  testID="settings-ble-scanner-pair-close"
                />
              </View>
              <View style={styles.stepRow}>
                {(["bleScanner.pair.step1", "bleScanner.pair.step2", "bleScanner.pair.step3"] as const).map(
                  (key, index) => (
                    <View key={key} style={[styles.step, index === 1 && styles.stepCurrent]}>
                      <Text style={[styles.stepIndex, index === 1 && styles.stepIndexCurrent]}>
                        {index + 1}
                      </Text>
                      <Text style={styles.stepText}>{t(key)}</Text>
                    </View>
                  ),
                )}
              </View>
              {status.state === "bluetooth-off" || status.state === "unauthorized" ? (
                <View style={[styles.banner, styles.bannerWarning]}>
                  <Text style={styles.bannerText}>{t("bleScanner.bluetoothHint")}</Text>
                  <PosButton
                    icon="open-in-new"
                    label={settingsText(locale, "action.openSystemSettings")}
                    onPress={() => void Linking.openSettings()}
                    size="small"
                    testID="settings-ble-scanner-pair-open-system-settings"
                    tone="secondary"
                  />
                </View>
              ) : null}
              {failed ? (
                <Text style={styles.errorText} testID="settings-ble-scanner-pair-error">
                  {t("bleScanner.pair.failed")}
                </Text>
              ) : null}
              <ScrollView
                contentContainerStyle={styles.deviceListBody}
                style={styles.deviceList}
                testID="settings-ble-scanner-device-list"
              >
                {devices.length === 0 ? (
                  <Text style={styles.emptyText}>{t("bleScanner.pair.empty")}</Text>
                ) : (
                  devices.map((device) => (
                    <DiscoveredDeviceRow
                      busy={busy}
                      connecting={connectingId === device.id}
                      device={device}
                      isCurrent={status.saved?.peripheralId === device.id}
                      currentAlias={status.saved?.alias ?? null}
                      key={device.id}
                      locale={locale}
                      onConnect={() => void connect(device)}
                    />
                  ))
                )}
              </ScrollView>
              <View style={styles.scanStatusRow}>
                {secondsLeft > 0 ? (
                  <ActivityIndicator color={posColors.accent} size="small" />
                ) : null}
                <Text style={styles.scanStatusText}>
                  {secondsLeft > 0
                    ? t("bleScanner.pair.scanning", { seconds: secondsLeft })
                    : t("bleScanner.pair.stopped")}
                </Text>
              </View>
              <View style={[styles.banner, styles.bannerWarning]}>
                <MaterialCommunityIcons
                  accessible={false}
                  color={posColors.yellowInk}
                  name="help-circle-outline"
                  size={18}
                />
                <Text style={styles.bannerText}>{t("bleScanner.pair.troubleshoot")}</Text>
              </View>
              <View style={styles.sheetActionsSpread}>
                {codesProfile ? (
                  <PosButton
                    disabled={busy}
                    icon="barcode"
                    label={t("bleScanner.pair.showCodes")}
                    onPress={() => setView("codes")}
                    size="medium"
                    testID="settings-ble-scanner-pair-codes"
                    tone="secondary"
                  />
                ) : (
                  <View />
                )}
                <View style={styles.sheetActionsInline}>
                  <PosButton
                    disabled={busy}
                    icon="refresh"
                    label={t("bleScanner.pair.rescan")}
                    onPress={() => setScanRound((round) => round + 1)}
                    size="medium"
                    testID="settings-ble-scanner-pair-rescan"
                    tone="secondary"
                  />
                  <PosButton
                    disabled={busy}
                    label={settingsText(locale, "action.cancel")}
                    onPress={close}
                    size="medium"
                    testID="settings-ble-scanner-pair-cancel"
                    tone="quiet"
                  />
                </View>
              </View>
            </>
          )}
        </View>
      </View>
    </Modal>
  );
}

function DiscoveredDeviceRow({
  busy,
  connecting,
  currentAlias,
  device,
  isCurrent,
  locale,
  onConnect,
}: Readonly<{
  busy: boolean;
  connecting: boolean;
  currentAlias: string | null;
  device: BleDiscoveredDevice;
  isCurrent: boolean;
  locale: SettingsLocale;
  onConnect(): void;
}>) {
  usePosThemeVersion();
  const t = useTranslate(locale);
  const profile = matchBleScannerProfile(device.serviceUuids, device.name);
  const recommended = looksLikeBleScanner(device.serviceUuids, device.name);
  const suffix = bleDeviceSuffix(device.id);
  return (
    <View
      style={[styles.deviceRow, isCurrent && styles.deviceRowCurrent]}
      testID={`settings-ble-scanner-device-${device.id}`}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={recommended ? posColors.ink : posColors.mutedInk}
        name={recommended ? "barcode-scan" : "bluetooth"}
        size={24}
      />
      <View style={styles.deviceIdentity}>
        <View style={styles.cardTitleRow}>
          <Text numberOfLines={1} style={styles.deviceName}>
            {device.name || t("bleScanner.pair.unnamed")}
          </Text>
          {/* 同名设备（背夹广播名固定）靠设备 ID 尾号区分。 */}
          <Text style={styles.deviceSuffix}>· {suffix}</Text>
          {isCurrent ? (
            <PosPill
              label={currentAlias ? `${t("bleScanner.pair.current")} · ${currentAlias}` : t("bleScanner.pair.current")}
              tone="good"
            />
          ) : null}
          {recommended ? (
            <PosPill
              label={profile ? t("bleScanner.pair.knownModel") : t("bleScanner.pair.recommended")}
              testID={`settings-ble-scanner-recommended-${device.id}`}
              tone="info"
            />
          ) : null}
        </View>
        <Text numberOfLines={1} style={styles.deviceMeta}>
          {device.id}
        </Text>
      </View>
      <SignalBars rssi={device.rssi} />
      <PosButton
        disabled={busy}
        icon={connecting ? "timer-sand" : "bluetooth-connect"}
        label={connecting ? t("bleScanner.pair.connecting") : t("bleScanner.pair.connect")}
        onPress={onConnect}
        size="small"
        testID={`settings-ble-scanner-connect-${device.id}`}
        tone={recommended ? "primary" : "secondary"}
      />
    </View>
  );
}

/** 信号格：-60 dBm 以上 4 格，每差 10 dBm 少一格。 */
function SignalBars({ rssi }: Readonly<{ rssi: number | null }>) {
  usePosThemeVersion();
  const level = rssi === null ? 0 : rssi >= -60 ? 4 : rssi >= -70 ? 3 : rssi >= -80 ? 2 : 1;
  return (
    <View
      accessibilityLabel={rssi === null ? "—" : `${rssi} dBm`}
      style={styles.signalBars}
    >
      {[1, 2, 3, 4].map((bar) => (
        <View
          key={bar}
          style={[
            styles.signalBar,
            { height: 4 + bar * 4 },
            bar <= level && styles.signalBarOn,
          ]}
        />
      ))}
    </View>
  );
}

/* ------------------------------------------------------------------ */
/* 屏幕设置码                                                            */
/* ------------------------------------------------------------------ */

function SettingCodesContent({
  doneLabel,
  locale,
  onDone,
  profile,
}: Readonly<{
  doneLabel: string;
  locale: SettingsLocale;
  onDone(): void;
  profile: BleScannerProfile;
}>) {
  usePosThemeVersion();
  const t = useTranslate(locale);
  const modelKey = `bleScanner.model.${profile.id}` as SettingsCopyKey;
  const model = settingsTextOr(locale, modelKey, "bleScanner.model.generic");
  return (
    <ScrollView
      contentContainerStyle={styles.codesBody}
      style={styles.codesScroll}
      testID="settings-ble-scanner-codes"
    >
      <View style={styles.sheetHeaderRow}>
        <View style={styles.sheetHeader}>
          <Text style={styles.sheetTitle}>{t("bleScanner.codes.title", { model })}</Text>
          <Text style={styles.sheetHint}>{t("bleScanner.codes.hint")}</Text>
        </View>
        <CloseButton
          label={doneLabel}
          onPress={onDone}
          testID="settings-ble-scanner-codes-close"
        />
      </View>
      <View style={[styles.banner, styles.bannerAccent]}>
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.accent}
          name="alert-circle-outline"
          size={18}
        />
        <Text style={styles.bannerStrongText}>{t("bleScanner.codes.factoryNote")}</Text>
      </View>
      <View style={styles.codeGrid}>
        {profile.settingCodes.map((code, index) => {
          const titleKey = `bleScanner.code.${code.id}` as SettingsCopyKey;
          const noteKey = `bleScanner.codeNote.${code.id}` as SettingsCopyKey;
          return (
            <View
              key={code.id}
              style={[styles.codeCard, code.required && styles.codeCardRequired]}
              testID={`settings-ble-scanner-code-${code.id}`}
            >
              <View style={styles.codeCardHeader}>
                <Text style={styles.stepIndexCurrent}>{index + 1}</Text>
                <Text style={styles.codeTitle}>{settingsTextOr(locale, titleKey, null) ?? code.value}</Text>
                <PosPill
                  label={t(code.required ? "bleScanner.codes.required" : "bleScanner.codes.recommended")}
                  tone={code.required ? "warning" : "info"}
                />
              </View>
              <Code128Bars value={code.value} />
              {settingsTextOr(locale, noteKey, null) ? (
                <Text style={styles.codeNote}>{settingsTextOr(locale, noteKey, null)}</Text>
              ) : null}
            </View>
          );
        })}
      </View>
      <View style={[styles.banner, styles.bannerInfo]}>
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.blue}
          name="cellphone-information"
          size={18}
        />
        <Text style={styles.bannerText}>{t("bleScanner.codes.backClipNote")}</Text>
      </View>
      <View style={styles.sheetActions}>
        <PosButton
          label={doneLabel}
          onPress={onDone}
          size="medium"
          testID="settings-ble-scanner-codes-done"
          tone="primary"
        />
      </View>
    </ScrollView>
  );
}

/**
 * 屏幕上画 Code128 给影像式扫码枪扫。条和底固定黑白（不跟随深色主题），
 * 两侧留足静区，否则部分扫码枪读不出来。
 */
function Code128Bars({ value }: Readonly<{ value: string }>) {
  usePosThemeVersion();
  const runs = useMemo(() => receiptCode128Runs(value), [value]);
  return (
    <View
      accessibilityLabel={value}
      accessibilityRole="image"
      style={styles.barcode}
    >
      <View style={styles.barcodeBars}>
        {runs.map((run, index) => (
          <View
            key={`${index}-${run.bar ? "bar" : "space"}-${run.modules}`}
            style={[styles.barcodeRun, { flex: run.modules }, run.bar && styles.barcodeRunDark]}
          />
        ))}
      </View>
      <Text selectable style={styles.barcodeValue}>
        {value}
      </Text>
    </View>
  );
}

/* ------------------------------------------------------------------ */
/* 测试扫码                                                              */
/* ------------------------------------------------------------------ */

/**
 * 测试面板只在打开时挂载：占用 scanner-test 场景租约（独占），所有来源（蓝牙 / 键盘 / 相机）
 * 的码只进这里；关闭即释放租约、清空记录。键盘模式扫码枪靠面板里自带的隐藏输入框接收。
 */
function BleScannerTestPanel({
  locale,
  onClose,
  router,
  service,
}: Readonly<{
  locale: SettingsLocale;
  onClose(): void;
  router: HidScannerRouter | undefined;
  service: BleScannerService;
}>) {
  usePosThemeVersion();
  const t = useTranslate(locale);
  const status = useBleStatus(service);
  const [entries, setEntries] = useState<readonly ScannerTestEntry[]>([]);
  const [duplicateDetected, setDuplicateDetected] = useState(false);
  const [showRaw, setShowRaw] = useState(false);

  useEffect(() => {
    let nextId = 1;
    const add = (entry: ScannerTestEntry) => {
      setEntries((current) => {
        const result = appendScannerTestEntry(current, entry);
        if (result.duplicate) setDuplicateDetected(true);
        return result.entries;
      });
    };
    const releaseLease = router?.acquireContext("scanner-test");
    // 键盘 / 相机的码经路由进 scanner-test 场景；蓝牙的码直接从服务拿（带分包等拼帧细节）。
    const unsubscribeRouted = router?.subscribeRouted((event) => {
      if (event.context !== "scanner-test" || event.source === "ble") return;
      const receivedAt = Date.parse(event.receivedAtIso);
      add(
        createScannerTestEntry({
          id: nextId++,
          source: event.source,
          value: event.value,
          receivedAt: Number.isFinite(receivedAt) ? receivedAt : Date.now(),
        }),
      );
    });
    const unsubscribeBle = service.subscribeTestScans((scan) => {
      add(
        createScannerTestEntry({
          id: nextId++,
          source: "ble",
          value: scan.frame.value,
          receivedAt: scan.frame.receivedAt,
          kind: scan.kind,
          frame: scan.frame,
        }),
      );
    });
    return () => {
      unsubscribeBle();
      unsubscribeRouted?.();
      releaseLease?.();
    };
  }, [router, service]);

  const latest = entries[0] ?? null;
  const bleEntries = entries.filter((entry) => entry.source === "ble");
  const averageMs =
    bleEntries.length > 0
      ? Math.round(
          bleEntries.reduce((total, entry) => total + (entry.durationMs ?? 0), 0) /
            bleEntries.length,
        )
      : null;
  const bluetoothOk =
    status.state !== "bluetooth-off" &&
    status.state !== "unauthorized" &&
    status.state !== "unsupported";
  const connected = status.state === "ready" || status.state === "learning";
  const name = status.saved?.alias ?? (status.saved?.name || t("bleScanner.title"));

  return (
    <View style={styles.testRoot} testID="settings-ble-scanner-test-panel">
      {router ? <HidScannerCapture active scanner={router} /> : null}
      <View style={styles.sheetHeaderRow}>
        <View style={styles.sheetHeader}>
          <Text style={styles.sheetTitle}>{t("bleScanner.testTitle", { name })}</Text>
          <Text style={styles.sheetHint}>{t("bleScanner.testHint")}</Text>
        </View>
        <CloseButton
          label={t("bleScanner.endTest")}
          onPress={onClose}
          testID="settings-ble-scanner-test-close"
        />
      </View>
      <View style={styles.testColumns}>
        <View style={styles.checkColumn}>
          <CheckItem ok={bluetoothOk} title={t("bleScanner.check.bluetooth")} />
          <CheckItem
            detail={
              connected
                ? [
                    status.rssi !== null
                      ? `${t("bleScanner.field.signal")} ${t("bleScanner.signal.value", { rssi: status.rssi })}`
                      : null,
                    status.batteryLevel !== null
                      ? `${t("bleScanner.field.battery")} ${status.batteryLevel}%`
                      : null,
                  ]
                    .filter(Boolean)
                    .join(" · ") || null
                : t(stateCopyKey(status.state))
            }
            ok={connected}
            title={t("bleScanner.check.connected")}
          />
          <CheckItem
            detail={status.state === "learning" ? t("bleScanner.channel.pending") : null}
            ok={status.state === "ready"}
            title={t("bleScanner.check.subscribed")}
          />
          <CheckItem
            detail={
              bleEntries.length > 0
                ? [
                    t("bleScanner.check.receivedCount", { count: bleEntries.length }),
                    averageMs !== null ? `${averageMs} ms` : null,
                  ]
                    .filter(Boolean)
                    .join(" · ")
                : null
            }
            ok={bleEntries.length > 0}
            title={t("bleScanner.check.received")}
          />
          <CheckItem
            detail={t(
              duplicateDetected
                ? "bleScanner.check.duplicateFound"
                : "bleScanner.check.duplicateNone",
            )}
            ok={!duplicateDetected}
            pendingWhenNotOk={false}
            title={t("bleScanner.check.duplicate")}
          />
        </View>
        <View style={styles.resultColumn}>
          <View style={styles.latestCard} testID="settings-ble-scanner-test-latest">
            {latest ? (
              <>
                <View style={styles.latestHeader}>
                  <Text style={styles.latestLabel}>
                    {t("bleScanner.latest", { time: formatClock(latest.receivedAt) })}
                    {"  ·  "}
                    {sourceLabel(t, latest.source)}
                  </Text>
                  {latest.kind !== "barcode" ? (
                    <PosPill label={t(kindCopyKey(latest.kind))} tone="warning" />
                  ) : null}
                  {latest.masked ? <PosPill label={t("bleScanner.masked")} tone="warning" /> : null}
                  {latest.kind === "barcode" && !latest.masked ? (
                    <PosPill
                      label={t("bleScanner.latestMeta", {
                        length: latest.length,
                        symbology: symbologyLabel(t, latest.value),
                      })}
                      tone="good"
                    />
                  ) : null}
                </View>
                <Text
                  numberOfLines={2}
                  selectable={!latest.masked}
                  style={styles.latestValue}
                  testID="settings-ble-scanner-test-latest-value"
                >
                  {latest.value}
                </Text>
                {latest.source === "ble" ? (
                  <Text style={styles.latestMeta}>
                    {`${t("bleScanner.packets")} ${latest.packetCount ?? "—"} · ${t("bleScanner.duration")} ${latest.durationMs ?? "—"} ms · ${t("bleScanner.terminator")} ${terminatorLabel(t, latest.terminator)}`}
                  </Text>
                ) : null}
                {showRaw && latest.rawHex ? (
                  <Text selectable style={styles.rawHex} testID="settings-ble-scanner-test-raw">
                    {`${t("bleScanner.rawHex")}: ${latest.rawHex}`}
                  </Text>
                ) : null}
              </>
            ) : (
              <Text style={styles.latestWaiting}>{t("bleScanner.testWaiting")}</Text>
            )}
          </View>
          <View style={styles.historyTable}>
            <View style={[styles.historyRow, styles.historyHeader]}>
              <Text style={[styles.historyCell, styles.historyValueCell, styles.historyHeadText]}>
                {t("bleScanner.col.value")}
              </Text>
              <Text style={[styles.historyCell, styles.historyHeadText]}>{t("bleScanner.source")}</Text>
              <Text style={[styles.historyCell, styles.historyHeadText]}>{t("bleScanner.col.length")}</Text>
              <Text style={[styles.historyCell, styles.historyHeadText]}>{t("bleScanner.packets")}</Text>
              <Text style={[styles.historyCell, styles.historyHeadText]}>{t("bleScanner.duration")}</Text>
              <Text style={[styles.historyCell, styles.historyHeadText]}>{t("bleScanner.col.time")}</Text>
            </View>
            <ScrollView style={styles.historyScroll} testID="settings-ble-scanner-test-history">
              {entries.length === 0 ? (
                <Text style={styles.emptyText}>{t("bleScanner.historyEmpty")}</Text>
              ) : (
                entries.map((entry) => (
                  <View key={entry.id} style={styles.historyRow}>
                    <View style={[styles.historyCell, styles.historyValueCell, styles.historyValueWrap]}>
                      <Text numberOfLines={1} style={styles.historyValue}>
                        {entry.value}
                      </Text>
                      {entry.masked ? <PosPill label={t("bleScanner.masked")} tone="warning" /> : null}
                      {entry.kind !== "barcode" ? (
                        <PosPill label={t(kindCopyKey(entry.kind))} tone="warning" />
                      ) : null}
                      {entry.duplicate ? (
                        <PosPill label={t("bleScanner.duplicateTag")} tone="danger" />
                      ) : null}
                    </View>
                    <Text style={styles.historyCell}>{sourceLabel(t, entry.source)}</Text>
                    <Text style={styles.historyCell}>{entry.masked ? "—" : entry.length}</Text>
                    <Text style={styles.historyCell}>{entry.packetCount ?? "—"}</Text>
                    <Text style={styles.historyCell}>
                      {entry.durationMs === null ? "—" : `${entry.durationMs} ms`}
                    </Text>
                    <Text style={styles.historyCell}>{formatClock(entry.receivedAt)}</Text>
                  </View>
                ))
              )}
            </ScrollView>
          </View>
        </View>
      </View>
      <View style={styles.sheetActionsSpread}>
        <View style={styles.sheetActionsInline}>
          <View style={styles.inlineSwitch}>
            <PosSwitch
              accessibilityLabel={t("bleScanner.showRaw")}
              accessibilityRole="switch"
              accessibilityState={{ checked: showRaw }}
              hitSlop={{ bottom: 7, left: 4, right: 4, top: 7 }}
              ios_backgroundColor={posColors.disabledBorder}
              onValueChange={setShowRaw}
              testID="settings-ble-scanner-test-show-raw"
              trackColor={{ false: posColors.disabledBorder, true: posColors.accent }}
              value={showRaw}
            />
            <Text style={styles.switchLabel}>{t("bleScanner.showRaw")}</Text>
          </View>
          <PosButton
            icon="broom"
            label={t("bleScanner.clear")}
            onPress={() => {
              setEntries([]);
              setDuplicateDetected(false);
            }}
            size="small"
            testID="settings-ble-scanner-test-clear"
            tone="secondary"
          />
        </View>
        <PosButton
          label={t("bleScanner.endTest")}
          onPress={onClose}
          size="medium"
          testID="settings-ble-scanner-test-end"
          tone="primary"
        />
      </View>
    </View>
  );
}

function CheckItem({
  detail = null,
  ok,
  pendingWhenNotOk = true,
  title,
}: Readonly<{
  detail?: string | null;
  ok: boolean;
  /** 未通过时显示空心圈（等待中）还是警示图标。 */
  pendingWhenNotOk?: boolean;
  title: string;
}>) {
  usePosThemeVersion();
  const icon: PosIconName = ok
    ? "check-circle"
    : pendingWhenNotOk
      ? "circle-outline"
      : "alert-circle";
  return (
    <View style={[styles.checkItem, ok && styles.checkItemOk]}>
      <MaterialCommunityIcons
        accessible={false}
        color={ok ? posColors.green : pendingWhenNotOk ? posColors.mutedInk : posColors.red}
        name={icon}
        size={22}
      />
      <View style={styles.checkCopy}>
        <Text style={styles.checkTitle}>{title}</Text>
        {detail ? <Text style={styles.checkDetail}>{detail}</Text> : null}
      </View>
    </View>
  );
}

function CloseButton({
  disabled = false,
  label,
  onPress,
  testID,
}: Readonly<{ disabled?: boolean; label: string; onPress(): void; testID: string }>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={label}
      accessibilityRole="button"
      disabled={disabled}
      onPress={onPress}
      sound="navigate"
      style={styles.closeButton}
      testID={testID}
    >
      <MaterialCommunityIcons accessible={false} color={posColors.ink} name="close" size={24} />
    </PosPressable>
  );
}

/* ------------------------------------------------------------------ */
/* 小工具                                                                */
/* ------------------------------------------------------------------ */

function stateCopyKey(state: BleScannerConnectionState): SettingsCopyKey {
  return `bleScanner.state.${state}` as SettingsCopyKey;
}

function stateTone(state: BleScannerConnectionState): PosPillTone {
  switch (state) {
    case "ready":
      return "good";
    case "connecting":
    case "learning":
      return "info";
    case "reconnecting":
    case "disconnected":
      return "warning";
    case "bluetooth-off":
    case "unauthorized":
      return "danger";
    case "sleeping":
    case "not-paired":
    case "unsupported":
      return "neutral";
  }
}

function kindCopyKey(kind: "setting-code" | "device-reply"): SettingsCopyKey {
  return kind === "setting-code" ? "bleScanner.kind.setting-code" : "bleScanner.kind.device-reply";
}

function sourceLabel(t: Translate, source: ScannerTestEntry["source"]): string {
  return t(
    source === "ble"
      ? "bleScanner.source.ble"
      : source === "hid"
        ? "bleScanner.source.hid"
        : "bleScanner.source.camera",
  );
}

function symbologyLabel(t: Translate, value: string): string {
  const guess = guessBarcodeSymbology(value);
  return guess === "other" ? t("bleScanner.symbology.other") : guess;
}

function terminatorLabel(t: Translate, terminator: ScannerTestEntry["terminator"]): string {
  if (terminator === null) return "—";
  return terminator === "idle" ? t("bleScanner.terminator.idle") : terminator;
}

/** 卡片上只显示最近一次扫码的尾部 6 位；疑似开通码整段屏蔽。 */
function lastScanText(status: BleScannerStatus): string {
  if (!status.lastScanValue) return "—";
  const value = isDeviceActivationScanValue(status.lastScanValue)
    ? "••••••"
    : scanValueTail(status.lastScanValue);
  return status.lastScanAt === null ? value : `${value} · ${formatClock(status.lastScanAt)}`;
}

function formatClock(epochMs: number): string {
  const date = new Date(epochMs);
  const pad = (value: number) => value.toString().padStart(2, "0");
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

/** 动态拼出的文案键（型号名、设置码说明）可能没有对应文案：没有就用兜底。 */
function settingsTextOr(
  locale: SettingsLocale,
  key: SettingsCopyKey,
  fallback: SettingsCopyKey,
): string;
function settingsTextOr(
  locale: SettingsLocale,
  key: SettingsCopyKey,
  fallback: null,
): string | null;
function settingsTextOr(
  locale: SettingsLocale,
  key: SettingsCopyKey,
  fallback: SettingsCopyKey | null,
): string | null {
  const text = settingsText(locale, key);
  // settingsText 对不存在的键返回 undefined（运行时），这里统一兜底。
  if (typeof text === "string" && text) return text;
  return fallback === null ? null : settingsText(locale, fallback);
}

const styles = createPosStyles((colors) => ({
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 10,
    minWidth: 0,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
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
  metricGrid: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  metricTile: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flexBasis: 180,
    flexGrow: 1,
    gap: 2,
    minWidth: 0,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  metricLabel: { color: colors.mutedInk, fontSize: 12, fontWeight: "700" },
  metricValue: {
    color: colors.ink,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  metricDetail: { color: colors.mutedInk, fontSize: 12, fontWeight: "600" },
  mono: { fontFamily: "Courier" },
  actionRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  banner: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  bannerInfo: { backgroundColor: colors.blueSoft, borderColor: colors.blueBorder },
  bannerWarning: { backgroundColor: colors.yellowSoft, borderColor: colors.yellowBorder },
  bannerAccent: { backgroundColor: colors.accentSoft, borderColor: colors.accent },
  bannerText: {
    color: colors.ink,
    flex: 1,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    minWidth: 200,
  },
  bannerStrongText: {
    color: colors.ink,
    flex: 1,
    fontSize: 14,
    fontWeight: "800",
    lineHeight: 20,
  },
  // ---- 弹窗（与打印机选择弹窗同一套尺寸） ----
  overlay: {
    alignItems: "center",
    backgroundColor: colors.overlay,
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  backdrop: { ...StyleSheet.absoluteFillObject },
  // 登录页面板铺满整个路由，盖在登录屏幕之上。
  loginPanelOverlay: { ...StyleSheet.absoluteFillObject, zIndex: 20 },
  loginPanelScroll: { flexShrink: 1 },
  loginPanelBody: { gap: 12 },
  sheet: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1,
    gap: 12,
    maxHeight: "88%",
    maxWidth: 820,
    padding: 20,
    shadowColor: colors.shadow,
    shadowOffset: { height: 5, width: 0 },
    shadowOpacity: 0.18,
    shadowRadius: 10,
    width: "86%",
  },
  sheetWide: { maxWidth: 960, width: "92%" },
  sheetHeaderRow: { alignItems: "flex-start", flexDirection: "row", gap: 12 },
  sheetHeader: { flex: 1, gap: 4, minWidth: 0 },
  sheetTitle: { color: colors.ink, fontSize: 20, fontWeight: "900" },
  sheetHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
  },
  sheetActions: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "flex-end",
  },
  sheetActionsSpread: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "space-between",
  },
  sheetActionsInline: { alignItems: "center", flexDirection: "row", gap: 10 },
  closeButton: {
    alignItems: "center",
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    height: posTouch.small,
    justifyContent: "center",
    width: posTouch.small,
  },
  stepRow: { flexDirection: "row", gap: 8 },
  step: {
    alignItems: "center",
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flex: 1,
    flexDirection: "row",
    gap: 8,
    padding: 10,
  },
  stepCurrent: { backgroundColor: colors.accentSoft, borderColor: colors.accent },
  stepIndex: {
    backgroundColor: colors.inverseSurface,
    borderRadius: 12,
    color: colors.onInverse,
    fontSize: 12,
    fontWeight: "900",
    height: 24,
    lineHeight: 24,
    overflow: "hidden",
    textAlign: "center",
    width: 24,
  },
  stepIndexCurrent: {
    backgroundColor: colors.accent,
    borderRadius: 12,
    color: colors.onAccent,
    fontSize: 12,
    fontWeight: "900",
    height: 24,
    lineHeight: 24,
    overflow: "hidden",
    textAlign: "center",
    width: 24,
  },
  stepText: { color: colors.ink, flex: 1, fontSize: 13, fontWeight: "700" },
  errorText: {
    backgroundColor: colors.redSoft,
    borderRadius: posRadius.medium,
    color: colors.red,
    fontSize: 14,
    fontWeight: "800",
    overflow: "hidden",
    padding: 12,
  },
  deviceList: {
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flexShrink: 1,
    minHeight: 120,
  },
  deviceListBody: { flexGrow: 1 },
  deviceRow: {
    alignItems: "center",
    borderColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 68,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  deviceRowCurrent: { backgroundColor: colors.accentSoft },
  deviceIdentity: { flex: 1, gap: 2, minWidth: 0 },
  deviceName: { color: colors.ink, flexShrink: 1, fontSize: 16, fontWeight: "900" },
  deviceSuffix: {
    color: colors.mutedInk,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  deviceMeta: { color: colors.mutedInk, fontFamily: "Courier", fontSize: 12 },
  signalBars: { alignItems: "flex-end", flexDirection: "row", gap: 2, height: 20 },
  signalBar: { backgroundColor: colors.disabledBorder, borderRadius: 1, width: 4 },
  signalBarOn: { backgroundColor: colors.ink },
  emptyText: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    paddingVertical: 24,
    textAlign: "center",
  },
  scanStatusRow: { alignItems: "center", flexDirection: "row", gap: 8 },
  scanStatusText: { color: colors.mutedInk, fontSize: 13, fontWeight: "700" },
  // ---- 备注名 ----
  aliasBody: { gap: 14 },
  inputFrame: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: posTouch.medium,
    paddingHorizontal: 14,
  },
  inputPrefix: { color: colors.mutedInk, fontSize: 15, fontWeight: "700" },
  inputField: {
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontWeight: "700",
    minHeight: posTouch.medium - 3,
    paddingVertical: 0,
  },
  // ---- 设置码 ----
  codesScroll: { flexShrink: 1 },
  codesBody: { gap: 12 },
  codeGrid: { flexDirection: "row", flexWrap: "wrap", gap: 12 },
  codeCard: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flexBasis: 380,
    flexGrow: 1,
    gap: 8,
    padding: 12,
  },
  codeCardRequired: { backgroundColor: colors.accentSoft, borderColor: colors.accent, borderWidth: 2 },
  codeCardHeader: { alignItems: "center", flexDirection: "row", gap: 8 },
  codeTitle: { color: colors.ink, flex: 1, fontSize: 16, fontWeight: "900" },
  codeNote: { color: colors.mutedInk, fontSize: 12.5, fontWeight: "600", textAlign: "center" },
  // 条码固定黑条白底，不随主题变色，保证扫码枪能读。
  barcode: {
    alignItems: "stretch",
    backgroundColor: "#FFFFFF",
    borderRadius: posRadius.small,
    paddingHorizontal: 24,
    paddingVertical: 10,
  },
  barcodeBars: { flexDirection: "row", height: 76 },
  barcodeRun: { backgroundColor: "#FFFFFF" },
  barcodeRunDark: { backgroundColor: "#000000" },
  barcodeValue: {
    color: "#000000",
    fontFamily: "Courier",
    fontSize: 12,
    marginTop: 4,
    textAlign: "center",
  },
  // ---- 测试扫码 ----
  testRoot: { flexShrink: 1, gap: 12 },
  testColumns: { flexDirection: "row", flexShrink: 1, gap: 12 },
  checkColumn: { gap: 8, width: 270 },
  checkItem: {
    alignItems: "center",
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 54,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  checkItemOk: { borderColor: colors.greenBorder },
  checkCopy: { flex: 1, gap: 2 },
  checkTitle: { color: colors.ink, fontSize: 14, fontWeight: "800" },
  checkDetail: { color: colors.mutedInk, fontSize: 12, fontWeight: "600" },
  resultColumn: { flex: 1, gap: 10, minWidth: 0 },
  latestCard: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 6,
    minHeight: 120,
    padding: 14,
  },
  latestHeader: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 8 },
  latestLabel: { color: colors.green, flex: 1, fontSize: 13, fontWeight: "800" },
  latestValue: {
    color: colors.ink,
    fontFamily: "Courier",
    fontSize: 30,
    fontWeight: "900",
  },
  latestMeta: { color: colors.mutedInk, fontSize: 12.5, fontWeight: "700" },
  latestWaiting: { color: colors.mutedInk, fontSize: 15, fontWeight: "700", lineHeight: 22 },
  rawHex: {
    backgroundColor: colors.surface,
    borderRadius: posRadius.small,
    color: colors.ink,
    fontFamily: "Courier",
    fontSize: 12,
    overflow: "hidden",
    padding: 8,
  },
  historyTable: {
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flexShrink: 1,
    overflow: "hidden",
  },
  historyHeader: { backgroundColor: colors.surfaceAlt },
  historyHeadText: { color: colors.mutedInk, fontSize: 12, fontWeight: "800" },
  historyScroll: { maxHeight: 220 },
  historyRow: {
    alignItems: "center",
    borderColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    minHeight: 40,
    paddingHorizontal: 10,
  },
  historyCell: {
    color: colors.ink,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    width: 72,
  },
  historyValueCell: { flex: 1, width: undefined },
  historyValueWrap: { alignItems: "center", flexDirection: "row", gap: 6, minWidth: 0 },
  historyValue: {
    color: colors.ink,
    flexShrink: 1,
    fontFamily: "Courier",
    fontSize: 14,
    fontWeight: "800",
  },
  inlineSwitch: { alignItems: "center", flexDirection: "row", gap: 10, minHeight: 46 },
  switchLabel: { color: colors.ink, fontSize: 14, fontWeight: "800" },
}));
