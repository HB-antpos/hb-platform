/**
 * 设置 → 设备与打印 → 蓝牙扫码枪。
 *
 * 详情 / 搜索 / 测试三页放在**同一个 BusinessSheet** 里用二级页（onBack）切换：
 * iOS 同一帧换原生 Modal 会丢弃第二个，所以不能连开两个弹层；也不新开路由
 * （避免与后台「设备管理」页重名、免做三端菜单登记）。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, Linking, TextInput as NativeTextInput, Platform, Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, Switch, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useHidBarcodeScanner } from "../use-hid-barcode-scanner";
import { setBleScanInterceptor, subscribeCrossSourceDuplicates } from "./ble-scan-hub";
import type { BleDiscoveredDevice, BleScannerStatus } from "./ble-scanner-controller";
import {
  bleDeviceSuffix,
  type BleScanKind,
  type BleScanTerminator,
  findBleScannerProfile,
  guessBarcodeSymbology,
  looksLikeBleScanner,
  normalizeBleScannerAlias,
  scanValueTail,
} from "./ble-scanner-core";
import {
  bleScannerDisplayName,
  describeBleRssi,
  maskSensitiveScanValue,
  resolveBleScannerBadge,
} from "./ble-scanner-presentation";
import {
  disconnectBleScanner,
  forgetBleScanner,
  pairBleScanner,
  reconnectBleScanner,
  setBleScannerAlias,
  setBleScannerAutoReconnect,
  startBleScannerDiscovery,
  subscribeBleScannerScans,
} from "./ble-scanner-service";
import { useBleScannerStore } from "./ble-scanner-state";

type Translate = (key: string, options?: Record<string, unknown>) => string;
type SheetPage = "home" | "search" | "test";

/** 一次搜索最长持续时间；到点自动停止，省电，需要时点「重新扫描」。 */
const DISCOVERY_SECONDS = 15;
const MAX_TEST_RECORDS = 20;

interface BleScannerSettingsSheetProps {
  visible: boolean;
  onDismiss: () => void;
}

export function BleScannerSettingsSheet({ visible, onDismiss }: BleScannerSettingsSheetProps) {
  const { t } = useAppTranslation(["settings", "common"]);
  const status = useBleScannerStore((state) => state.status);
  const saved = status.saved;
  const [page, setPage] = useState<SheetPage>("home");

  useEffect(() => {
    if (visible) setPage("home");
  }, [visible]);

  // 没保存过扫码枪时，弹层首页就是搜索页。
  const effectivePage: SheetPage = page === "home" && !saved ? "search" : page;
  const title = effectivePage === "test" ? t("bleScanner.test.title") : t("bleScanner.title");
  const subtitle =
    effectivePage === "test"
      ? t("bleScanner.test.subtitle")
      : effectivePage === "search"
        ? t("bleScanner.search.subtitle")
        : saved
          ? [bleScannerDisplayName(saved, t("bleScanner.unnamed")), saved.alias ? saved.name : null]
              .filter(Boolean)
              .join(" · ")
          : undefined;
  const onBack =
    effectivePage === "test" || (effectivePage === "search" && saved) ? () => setPage("home") : undefined;

  return (
    <BusinessSheet visible={visible} title={title} subtitle={subtitle} onDismiss={onDismiss} onBack={onBack}>
      {effectivePage === "search" ? (
        <SearchPage t={t} status={status} onPaired={() => setPage("test")} />
      ) : effectivePage === "test" ? (
        <TestPage t={t} status={status} />
      ) : (
        <DetailPage t={t} status={status} onTest={() => setPage("test")} onReplace={() => setPage("search")} />
      )}
    </BusinessSheet>
  );
}

/* ------------------------------------------------------------------ */
/* 详情                                                                 */
/* ------------------------------------------------------------------ */

function DetailPage({
  t,
  status,
  onTest,
  onReplace,
}: {
  t: Translate;
  status: BleScannerStatus;
  onTest: () => void;
  onReplace: () => void;
}) {
  const saved = status.saved;
  const [busy, setBusy] = useState(false);
  const [aliasDraft, setAliasDraft] = useState(saved?.alias ?? "");
  useEffect(() => {
    setAliasDraft(saved?.alias ?? "");
  }, [saved?.alias]);

  if (!saved) return null;
  const badge = resolveBleScannerBadge(status);
  const profile = findBleScannerProfile(saved.profileId);
  const channelKey =
    status.state === "learning"
      ? "learning"
      : profile
        ? "known"
        : saved.serviceUuid
          ? "learned"
          : "unknown";
  const linkActive = ["ready", "learning", "connecting", "reconnecting", "sleeping"].includes(status.state);
  const bluetoothBlocked = ["bluetooth-off", "unauthorized", "unsupported"].includes(status.state);

  const run = async (task: () => Promise<void>) => {
    setBusy(true);
    try {
      await task();
    } catch (error) {
      Alert.alert(t("bleScanner.actionFailed"), error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  };

  const saveAlias = () => {
    const next = normalizeBleScannerAlias(aliasDraft);
    if (next === saved.alias) return;
    void run(() => setBleScannerAlias(next));
  };

  const confirmForget = () => {
    Alert.alert(t("bleScanner.forgetConfirmTitle"), t("bleScanner.forgetConfirmMessage"), [
      { text: t("common:actions.cancel"), style: "cancel" },
      { text: t("bleScanner.forget"), style: "destructive", onPress: () => void run(forgetBleScanner) },
    ]);
  };

  return (
    <View style={styles.page} testID="ble-scanner-detail">
      <View style={[styles.statusCard, toneCardStyle(badge.tone)]}>
        <View style={styles.statusIcon}>
          <Icon source="barcode-scan" size={24} color={toneColor(badge.tone)} />
        </View>
        <View style={styles.flex}>
          <Text variant="titleMedium" style={styles.strong}>
            {t(`bleScanner.state.${badge.key}`)}
          </Text>
          <Text variant="bodySmall" style={styles.meta}>
            {t(`bleScanner.stateHint.${badge.key}`)}
          </Text>
        </View>
      </View>

      <View style={styles.grid}>
        <InfoTile label={t("bleScanner.info.channel")} value={t(`bleScanner.channel.${channelKey}`)} />
        <InfoTile
          label={t("bleScanner.info.signal")}
          value={
            status.rssi === null
              ? t("bleScanner.info.unknown")
              : t(`bleScanner.signal.${describeBleRssi(status.rssi)}`, { rssi: status.rssi })
          }
        />
        <InfoTile
          label={t("bleScanner.info.battery")}
          value={status.batteryLevel === null ? t("bleScanner.info.unknown") : `${status.batteryLevel}%`}
          danger={status.batteryLevel !== null && status.batteryLevel < 20}
        />
        <InfoTile
          label={t("bleScanner.info.lastScan")}
          value={status.lastScanValue ? scanValueTail(maskSensitiveScanValue(status.lastScanValue)) : t("bleScanner.info.none")}
        />
      </View>

      <Button mode="contained" icon="barcode-scan" onPress={onTest} testID="ble-scanner-open-test">
        {t("bleScanner.testScan")}
      </Button>

      <View style={styles.optionRow}>
        <View style={styles.flex}>
          <Text variant="bodyMedium">{t("bleScanner.autoReconnect")}</Text>
          <Text variant="bodySmall" style={styles.meta}>
            {t("bleScanner.autoReconnectHint")}
          </Text>
        </View>
        <Switch
          value={saved.autoReconnect}
          disabled={busy}
          onValueChange={(value) => void run(() => setBleScannerAutoReconnect(value))}
        />
      </View>

      <TextInput
        mode="outlined"
        dense
        label={t("bleScanner.alias")}
        placeholder={t("bleScanner.aliasPlaceholder")}
        value={aliasDraft}
        maxLength={24}
        onChangeText={setAliasDraft}
        onEndEditing={saveAlias}
        onSubmitEditing={saveAlias}
        returnKeyType="done"
      />
      <Text variant="bodySmall" style={styles.meta}>
        {t("bleScanner.aliasHint")}
      </Text>

      {profile && profile.settingCodes.length > 0 ? (
        <Notice tone="info" text={t("bleScanner.knownModelHint")} />
      ) : null}
      {status.lastErrorCode && !linkActive ? (
        <Text variant="bodySmall" style={styles.meta}>
          {t("bleScanner.lastError", { code: status.lastErrorCode })}
        </Text>
      ) : null}

      <View style={styles.actionRow}>
        {linkActive ? (
          <Button mode="outlined" style={styles.flex} disabled={busy} onPress={() => void run(disconnectBleScanner)}>
            {t("bleScanner.disconnect")}
          </Button>
        ) : (
          <Button
            mode="outlined"
            style={styles.flex}
            disabled={busy || bluetoothBlocked}
            onPress={() => void run(reconnectBleScanner)}
          >
            {t("bleScanner.reconnect")}
          </Button>
        )}
        <Button mode="outlined" icon="swap-horizontal" style={styles.flex} disabled={busy} onPress={onReplace}>
          {t("bleScanner.replace")}
        </Button>
      </View>
      <Button mode="text" textColor={HB_COLORS.danger} disabled={busy} onPress={confirmForget}>
        {t("bleScanner.forget")}
      </Button>
    </View>
  );
}

/* ------------------------------------------------------------------ */
/* 搜索                                                                 */
/* ------------------------------------------------------------------ */

function SearchPage({ t, status, onPaired }: { t: Translate; status: BleScannerStatus; onPaired: () => void }) {
  const [devices, setDevices] = useState<readonly BleDiscoveredDevice[]>([]);
  const [scanning, setScanning] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [scanNonce, setScanNonce] = useState(0);
  const [showAll, setShowAll] = useState(false);
  const [pairingId, setPairingId] = useState<string | null>(null);
  const savedId = status.saved?.peripheralId ?? null;

  useEffect(() => {
    let cancelled = false;
    let stop: (() => void) | null = null;
    const startedAt = Date.now();
    setDevices([]);
    setScanning(true);
    setElapsed(0);
    const finish = () => {
      clearInterval(timer);
      stop?.();
      stop = null;
      if (!cancelled) setScanning(false);
    };
    const timer = setInterval(() => {
      const seconds = Math.floor((Date.now() - startedAt) / 1000);
      setElapsed(seconds);
      if (seconds >= DISCOVERY_SECONDS) finish();
    }, 1000);
    void startBleScannerDiscovery((list) => {
      if (!cancelled) setDevices(list);
    }).then(
      (stopDiscovery) => {
        if (cancelled) {
          stopDiscovery?.();
          return;
        }
        stop = stopDiscovery;
        // 旧原生包 / 审核态 / 启动失败：没有搜索可停。
        if (!stopDiscovery) finish();
      },
      () => finish(),
    );
    return () => {
      cancelled = true;
      finish();
    };
  }, [scanNonce]);

  const visibleDevices = useMemo(
    () =>
      showAll
        ? devices
        : devices.filter((device) => device.id === savedId || looksLikeBleScanner(device.serviceUuids, device.name)),
    [devices, savedId, showAll],
  );

  const handlePair = async (device: BleDiscoveredDevice) => {
    setPairingId(device.id);
    try {
      await pairBleScanner(device);
      onPaired();
    } catch (error) {
      Alert.alert(t("bleScanner.actionFailed"), error instanceof Error ? error.message : String(error));
    } finally {
      setPairingId(null);
    }
  };

  const bluetoothProblem =
    status.state === "bluetooth-off"
      ? "bluetoothOff"
      : status.state === "unauthorized" || status.lastErrorCode?.includes("permission")
        ? "unauthorized"
        : null;

  return (
    <View style={styles.page} testID="ble-scanner-search">
      <Notice tone="info" text={t("bleScanner.search.bleModeHint")} />
      {bluetoothProblem ? (
        <View style={styles.page}>
          <Notice tone="danger" text={t(`bleScanner.search.${bluetoothProblem}`)} />
          <Button mode="outlined" icon="cog-outline" onPress={() => void Linking.openSettings()}>
            {t("bleScanner.search.openSettings")}
          </Button>
        </View>
      ) : null}

      <View style={styles.listHeader}>
        <Text variant="labelLarge" style={styles.meta}>
          {t("bleScanner.search.nearby")}
        </Text>
        {scanning ? (
          <View style={styles.inline}>
            <ActivityIndicator size={14} />
            <Text variant="labelSmall" style={styles.meta}>
              {t("bleScanner.search.scanning", { seconds: elapsed })}
            </Text>
          </View>
        ) : null}
      </View>

      <View style={styles.listCard}>
        {visibleDevices.length === 0 ? (
          <Text variant="bodySmall" style={[styles.meta, styles.emptyText]}>
            {scanning ? t("bleScanner.search.waiting") : t("bleScanner.search.empty")}
          </Text>
        ) : (
          visibleDevices.map((device, index) => {
            const recommended = looksLikeBleScanner(device.serviceUuids, device.name);
            const isSaved = device.id === savedId;
            const pairing = pairingId === device.id;
            return (
              <View key={device.id} style={[styles.deviceRow, index > 0 && styles.rowDivider]}>
                <Icon source={recommended ? "barcode-scan" : "bluetooth"} size={22} color={HB_COLORS.textSecondary} />
                <View style={styles.flex}>
                  <Text variant="bodyMedium" style={styles.strong}>
                    {`${device.name || t("bleScanner.unnamed")} · ${bleDeviceSuffix(device.id)}`}
                  </Text>
                  <View style={styles.inline}>
                    {isSaved ? <Tag label={t("bleScanner.search.lastConnected")} tone="success" /> : null}
                    {recommended ? <Tag label={t("bleScanner.search.recommended")} tone="info" /> : null}
                    <Text variant="labelSmall" style={styles.meta}>
                      {device.rssi === null
                        ? t("bleScanner.info.unknown")
                        : t(`bleScanner.signal.${describeBleRssi(device.rssi)}`, { rssi: device.rssi })}
                    </Text>
                  </View>
                </View>
                <Button
                  compact
                  mode={pairing ? "contained" : "outlined"}
                  loading={pairing}
                  disabled={pairingId !== null}
                  onPress={() => void handlePair(device)}
                >
                  {pairing ? t("bleScanner.search.connecting") : t("bleScanner.search.connect")}
                </Button>
              </View>
            );
          })
        )}
      </View>

      <View style={styles.optionRow}>
        <View style={styles.flex}>
          <Text variant="bodyMedium">{t("bleScanner.search.showAll")}</Text>
          <Text variant="bodySmall" style={styles.meta}>
            {t("bleScanner.search.showAllHint")}
          </Text>
        </View>
        <Switch value={showAll} onValueChange={setShowAll} />
      </View>

      <Notice tone="warning" text={t("bleScanner.search.notFoundHint")} />

      <Button
        mode="outlined"
        icon="refresh"
        disabled={scanning || pairingId !== null}
        onPress={() => setScanNonce((value) => value + 1)}
      >
        {t("bleScanner.search.rescan")}
      </Button>
    </View>
  );
}

/* ------------------------------------------------------------------ */
/* 测试                                                                 */
/* ------------------------------------------------------------------ */

type TestRecord = Readonly<{
  id: number;
  value: string;
  source: "ble" | "hid";
  kind: BleScanKind;
  at: number;
  packetCount: number | null;
  terminator: BleScanTerminator | null;
  durationMs: number | null;
  rawHex: string | null;
}>;

function TestPage({ t, status }: { t: Translate; status: BleScannerStatus }) {
  const [records, setRecords] = useState<readonly TestRecord[]>([]);
  const [showRaw, setShowRaw] = useState(false);
  const [duplicateSeen, setDuplicateSeen] = useState(false);
  const nextIdRef = useRef(1);

  const addRecord = useCallback((record: Omit<TestRecord, "id">) => {
    const id = nextIdRef.current++;
    setRecords((current) => [{ ...record, id }, ...current].slice(0, MAX_TEST_RECORDS));
  }, []);

  // 测试期间独占：所有 BLE 码只进测试面板，不触发任何业务页面。
  useEffect(() => {
    setBleScanInterceptor(() => undefined);
    const offScans = subscribeBleScannerScans((scan) => {
      addRecord({
        value: scan.frame.value,
        source: "ble",
        kind: scan.kind,
        at: scan.frame.receivedAt,
        packetCount: scan.frame.packetCount,
        terminator: scan.frame.terminator,
        durationMs: scan.frame.durationMs,
        rawHex: scan.frame.rawHex,
      });
    });
    const offDuplicates = subscribeCrossSourceDuplicates(() => setDuplicateSeen(true));
    return () => {
      setBleScanInterceptor(null);
      offScans();
      offDuplicates();
    };
  }, [addRecord]);

  // 键盘模式扫码枪也能在这里测：弹层是独立窗口，必须用隐藏输入框接收。
  const hidScanner = useHidBarcodeScanner({
    enabled: true,
    textInputOnly: true,
    onScan: (barcode) => {
      addRecord({
        value: barcode,
        source: "hid",
        kind: "barcode",
        at: Date.now(),
        packetCount: null,
        terminator: null,
        durationMs: null,
        rawHex: null,
      });
    },
  });

  const latest = records[0] ?? null;
  const checks: { key: string; ok: boolean }[] = [
    { key: "bluetooth", ok: !["bluetooth-off", "unauthorized", "unsupported"].includes(status.state) },
    { key: "connected", ok: status.state === "ready" || status.state === "learning" },
    { key: "subscribed", ok: status.state === "ready" },
    { key: "received", ok: records.some((record) => record.source === "ble") },
    { key: "noDuplicate", ok: !duplicateSeen },
  ];

  return (
    <View style={styles.page} testID="ble-scanner-test">
      {hidScanner.textInputProps ? (
        <NativeTextInput style={styles.hiddenScannerInput} {...hidScanner.textInputProps} />
      ) : null}
      <View style={styles.chips}>
        {checks.map((check) => (
          <Tag
            key={check.key}
            label={t(`bleScanner.test.check.${check.key}`)}
            tone={check.ok ? "success" : check.key === "noDuplicate" ? "warning" : "neutral"}
            icon={check.ok ? "check-circle" : "circle-outline"}
          />
        ))}
      </View>
      {status.state === "learning" ? <Notice tone="info" text={t("bleScanner.test.learningHint")} /> : null}
      {status.state === "sleeping" ? <Notice tone="info" text={t("bleScanner.test.wakeHint")} /> : null}
      {duplicateSeen ? <Notice tone="warning" text={t("bleScanner.test.duplicateHint")} /> : null}

      {latest ? (
        <View style={[styles.latestCard, latest.kind !== "barcode" && styles.latestCardMuted]}>
          <View style={styles.listHeader}>
            <Text variant="labelMedium" style={styles.successText}>
              {t("bleScanner.test.latest", { time: formatClock(latest.at) })}
            </Text>
            <Text variant="labelSmall" style={styles.meta}>
              {describeRecordType(t, latest)}
            </Text>
          </View>
          <Text variant="headlineSmall" selectable style={styles.latestValue}>
            {maskSensitiveScanValue(latest.value)}
          </Text>
          <Text variant="bodySmall" style={styles.meta}>
            {latest.source === "ble"
              ? t("bleScanner.test.bleDetail", {
                  length: latest.value.length,
                  packets: latest.packetCount ?? 1,
                  terminator: t(`bleScanner.test.terminator.${latest.terminator ?? "idle"}`),
                  duration: latest.durationMs ?? 0,
                })
              : t("bleScanner.test.hidDetail", { length: latest.value.length })}
          </Text>
          {latest.rawHex ? (
            <Pressable onPress={() => setShowRaw((value) => !value)} accessibilityRole="button">
              <Text variant="labelSmall" style={styles.link}>
                {showRaw ? t("bleScanner.test.hideRaw") : t("bleScanner.test.showRaw")}
              </Text>
            </Pressable>
          ) : null}
          {showRaw && latest.rawHex ? (
            <Text variant="bodySmall" selectable style={styles.mono}>
              {latest.kind === "barcode" && latest.value !== maskSensitiveScanValue(latest.value)
                ? t("bleScanner.test.rawHidden")
                : latest.rawHex}
            </Text>
          ) : null}
        </View>
      ) : (
        <Notice tone="info" text={t("bleScanner.test.waiting")} />
      )}

      <View style={styles.listHeader}>
        <Text variant="labelLarge" style={styles.meta}>
          {t("bleScanner.test.recent")}
        </Text>
        {records.length > 0 ? (
          <Pressable onPress={() => setRecords([])} accessibilityRole="button">
            <Text variant="labelMedium" style={styles.link}>
              {t("bleScanner.test.clear")}
            </Text>
          </Pressable>
        ) : null}
      </View>
      {records.length > 0 ? (
        <View style={styles.listCard}>
          {records.map((record, index) => (
            <View key={record.id} style={[styles.recordRow, index > 0 && styles.rowDivider]}>
              <Text variant="bodyMedium" style={[styles.mono, styles.flex]} numberOfLines={2}>
                {maskSensitiveScanValue(record.value)}
              </Text>
              {record.source === "hid" ? <Tag label={t("bleScanner.test.sourceKeyboard")} tone="neutral" /> : null}
              {record.kind !== "barcode" ? (
                <Tag label={t(`bleScanner.test.kind.${record.kind}`)} tone="warning" />
              ) : null}
              <Text variant="labelSmall" style={styles.meta}>
                {t("bleScanner.test.recordMeta", { length: record.value.length, time: formatClock(record.at) })}
              </Text>
            </View>
          ))}
        </View>
      ) : null}

      <Notice tone="success" text={t("bleScanner.test.keyboardHint")} />
    </View>
  );
}

function describeRecordType(t: Translate, record: TestRecord): string {
  if (record.kind !== "barcode") return t(`bleScanner.test.kind.${record.kind}`);
  const symbology = guessBarcodeSymbology(record.value);
  const symbologyLabel = symbology === "other" ? t("bleScanner.test.symbologyOther") : symbology;
  const source = record.source === "hid" ? t("bleScanner.test.sourceKeyboard") : t("bleScanner.test.sourceBle");
  return `${source} · ${symbologyLabel}`;
}

function formatClock(at: number): string {
  const date = new Date(at);
  return [date.getHours(), date.getMinutes(), date.getSeconds()]
    .map((part) => String(part).padStart(2, "0"))
    .join(":");
}

/* ------------------------------------------------------------------ */
/* 小部件                                                               */
/* ------------------------------------------------------------------ */

type NoticeTone = "info" | "warning" | "danger" | "success";

function Notice({ tone, text }: { tone: NoticeTone; text: string }) {
  const icon =
    tone === "danger" ? "alert-circle-outline" : tone === "warning" ? "help-circle-outline" : "information-outline";
  return (
    <View style={[styles.notice, NOTICE_STYLE[tone]]}>
      <Icon source={icon} size={16} color={NOTICE_TEXT[tone]} />
      <Text variant="bodySmall" style={[styles.flex, { color: NOTICE_TEXT[tone] }]}>
        {text}
      </Text>
    </View>
  );
}

function Tag({
  label,
  tone,
  icon,
}: {
  label: string;
  tone: "success" | "info" | "warning" | "neutral";
  icon?: string;
}) {
  const color =
    tone === "success"
      ? HB_COLORS.success
      : tone === "info"
        ? HB_COLORS.action
        : tone === "warning"
          ? HB_COLORS.warning
          : HB_COLORS.textSecondary;
  return (
    <View style={[styles.tag, { borderColor: color }]}>
      {icon ? <Icon source={icon} size={12} color={color} /> : null}
      <Text variant="labelSmall" style={{ color }}>
        {label}
      </Text>
    </View>
  );
}

function InfoTile({ label, value, danger }: { label: string; value: string; danger?: boolean }) {
  return (
    <View style={styles.tile}>
      <Text variant="labelSmall" style={styles.meta}>
        {label}
      </Text>
      <Text variant="bodyMedium" style={[styles.strong, danger && { color: HB_COLORS.danger }]}>
        {value}
      </Text>
    </View>
  );
}

function toneColor(tone: string): string {
  if (tone === "success") return HB_COLORS.success;
  if (tone === "warning") return HB_COLORS.warning;
  if (tone === "danger") return HB_COLORS.danger;
  return HB_COLORS.textSecondary;
}

function toneCardStyle(tone: string) {
  if (tone === "success") return styles.statusCardSuccess;
  if (tone === "warning") return styles.statusCardWarning;
  if (tone === "danger") return styles.statusCardDanger;
  return styles.statusCardNeutral;
}

const NOTICE_STYLE = StyleSheet.create({
  info: { backgroundColor: "#EFF8FF" },
  success: { backgroundColor: "#ECFDF3" },
  warning: { backgroundColor: "#FFFAEB" },
  danger: { backgroundColor: "#FEF3F2" },
});

const NOTICE_TEXT: Record<NoticeTone, string> = {
  info: HB_COLORS.action,
  success: HB_COLORS.success,
  warning: HB_COLORS.warning,
  danger: HB_COLORS.danger,
};

const styles = StyleSheet.create({
  page: { gap: HB_SPACING.sm },
  flex: { flex: 1, minWidth: 0 },
  strong: { fontWeight: "600", color: HB_COLORS.textPrimary },
  meta: { color: HB_COLORS.textSecondary },
  inline: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: HB_SPACING.xxs },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xxs },
  statusCard: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
  },
  statusCardSuccess: { backgroundColor: "#ECFDF3", borderColor: "#ABEFC6" },
  statusCardWarning: { backgroundColor: "#FFFAEB", borderColor: "#FEDF89" },
  statusCardDanger: { backgroundColor: "#FEF3F2", borderColor: "#FECDCA" },
  statusCardNeutral: { backgroundColor: HB_COLORS.surfaceMuted, borderColor: HB_COLORS.outlineMuted },
  statusIcon: {
    width: 40,
    height: 40,
    borderRadius: HB_RADIUS.control,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: HB_COLORS.white,
  },
  grid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  tile: {
    flexBasis: "47%",
    flexGrow: 1,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    gap: 2,
  },
  optionRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, paddingVertical: HB_SPACING.xxs },
  actionRow: { flexDirection: "row", gap: HB_SPACING.xs },
  notice: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
  },
  listHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  listCard: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    overflow: "hidden",
  },
  emptyText: { padding: HB_SPACING.md, textAlign: "center" },
  deviceRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, padding: HB_SPACING.sm },
  recordRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, padding: HB_SPACING.sm },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  tag: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 999,
    borderWidth: StyleSheet.hairlineWidth,
  },
  latestCard: {
    gap: HB_SPACING.xxs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: "#ECFDF3",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#ABEFC6",
  },
  latestCardMuted: { backgroundColor: "#FFFAEB", borderColor: "#FEDF89" },
  latestValue: { fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  successText: { color: HB_COLORS.success, fontWeight: "600" },
  link: { color: HB_COLORS.action, fontWeight: "600" },
  mono: { fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }), color: HB_COLORS.textPrimary },
  hiddenScannerInput: { position: "absolute", width: 1, height: 1, opacity: 0 },
});
