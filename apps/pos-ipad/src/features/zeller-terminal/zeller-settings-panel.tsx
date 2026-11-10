import { MaterialCommunityIcons } from "@expo/vector-icons";
import { mapZellerSdkPairingResponse } from "@hb/pos-payments-core/features/payments/zeller/zeller-sdk-mapper";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { ActivityIndicator, Text, View } from "react-native";

import {
  zellerSettingsText,
  type ZellerSettingsCopyKey,
  type ZellerSettingsLocale,
} from "./zeller-settings-copy";
import type { ZellerTerminalBoundaryState } from "./zeller-terminal-boundary";

import type { ZellerProbeTerminal } from "@/features/zeller-probe/zeller-probe-runner";
import { PosButton, type PosButtonTone } from "@/ui/controls/pos-button";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  type PosPalette,
  usePosThemeVersion,
} from "@/ui/theme";

type IconName = keyof typeof MaterialCommunityIcons.glyphMap;

/** 一次「检查配对」或「配对刷卡机」之后的结论。 */
export type ZellerPairingOutcome =
  | Readonly<{ kind: "paired" }>
  | Readonly<{ kind: "unpaired" }>
  /** 用户关掉了 Zeller 窗口：什么都没变。 */
  | Readonly<{ kind: "cancelled" }>
  /** 没查到结论（网络不通、SDK 出错等）。reason 是我们的错误类型名，供排查。 */
  | Readonly<{ kind: "offline"; reason: string }>;

/**
 * 把 SDK 的 initialise() / setup() 返回值变成配对结论。
 * 只有 SDK 明确说「需要设置」才算未配对；其余错误一律算没查到，不能把它当成未配对去引导重新配对。
 */
export function resolveZellerPairingOutcome(response: unknown): ZellerPairingOutcome {
  const pairing = mapZellerSdkPairingResponse(response);
  if (pairing.isPaired) return { kind: "paired" };
  if (pairing.errorType === "SetupRequired") return { kind: "unpaired" };
  if (pairing.errorType === "Cancelled") return { kind: "cancelled" };
  return { kind: "offline", reason: pairing.errorType ?? "Unrecognised" };
}

export type ZellerSettingsPanelProps = Readonly<{
  sdk: ZellerTerminalBoundaryState;
  /** 这台 iPad 当前启用的刷卡终端名称（Square / Linkly / Zeller）；没有启用任何一家时为 null。 */
  activeProviderLabel: string | null;
  /**
   * 启用 Zeller 的能力。null 表示 Zeller 收款还没有接进收银流程：可以配对，但不能启用。
   */
  enable: Readonly<{ active: boolean; onEnable(): void }> | null;
  locale: ZellerSettingsLocale;
  /** 设置页忙或当前收银员没有配置支付的权限时为 true。 */
  disabled?: boolean;
  onOpenNetworkCheck?: () => void;
  /** 测试可注入。 */
  now?: () => Date;
}>;

type PairingStatus = "unknown" | "paired" | "unpaired" | "offline";
type Busy = "checking" | "pairing" | null;
type LastCheck = Readonly<{ time: string; failedReason: string | null }>;

type PanelView =
  | "loading"
  | "loadFailed"
  | "checking"
  | "pairing"
  | "paired"
  | "active"
  | "unpaired"
  | "offline";

// soft 只存色名：色值在渲染时按当前主题读取。
const PILL: Record<PanelView, Readonly<{ key: ZellerSettingsCopyKey; icon: IconName | null; soft: keyof PosPalette }>> = {
  loading: { key: "pill.loading", icon: null, soft: "blueSoft" },
  loadFailed: { key: "pill.loadFailed", icon: "alert-circle-outline", soft: "redSoft" },
  checking: { key: "pill.checking", icon: null, soft: "blueSoft" },
  pairing: { key: "pill.pairing", icon: null, soft: "blueSoft" },
  paired: { key: "pill.paired", icon: "check-circle", soft: "greenSoft" },
  active: { key: "pill.paired", icon: "check-circle", soft: "greenSoft" },
  unpaired: { key: "pill.unpaired", icon: "link-variant-off", soft: "surfaceSunken" },
  offline: { key: "pill.offline", icon: "wifi-off", soft: "redSoft" },
};

/**
 * 设置页「支付设置」里的 Zeller 面板：左边是配对状态和操作，右边是配对步骤。
 * 它嵌在「刷卡终端」卡片里，所以两栏用浅底内嵌块，不再各自画一张卡。
 *
 * 中文注释：
 * 1. 配对和启用是两步。配对成功不会切换这台 iPad 的刷卡通道，必须再点一次「启用 Zeller」，
 *    避免配对过程中把正在用的刷卡机切走。
 * 2. 同一时间只允许一个 SDK 调用（SDK 并发会返回 Operation Busy，并且可能把结果交给错的调用）。
 * 3. 查不到配对状态时不改变任何东西，只提示；绝不因为「没查到」就当成未配对。
 */
export function ZellerSettingsPanel({
  sdk,
  activeProviderLabel,
  enable,
  locale,
  disabled = false,
  onOpenNetworkCheck,
  now = () => new Date(),
}: ZellerSettingsPanelProps) {
  usePosThemeVersion();
  const t = useCallback(
    (key: ZellerSettingsCopyKey, values?: Readonly<Record<string, string | number>>) =>
      zellerSettingsText(locale, key, values),
    [locale],
  );
  const [status, setStatus] = useState<PairingStatus>("unknown");
  const [wasPaired, setWasPaired] = useState(false);
  const [busy, setBusy] = useState<Busy>(null);
  const [lastCheck, setLastCheck] = useState<LastCheck | null>(null);
  // 用 ref 做串行门：连点两下时第二下在同一帧里就能被挡住。
  const busyRef = useRef(false);
  const mountedRef = useRef(true);
  useEffect(
    () => () => {
      mountedRef.current = false;
    },
    [],
  );

  const terminal = sdk.status === "ready" ? sdk.terminal : null;

  const run = useCallback(
    async (kind: "checking" | "pairing", call: (terminal: ZellerProbeTerminal) => PromiseLike<unknown>) => {
      if (!terminal || busyRef.current) return;
      busyRef.current = true;
      setBusy(kind);
      let outcome: ZellerPairingOutcome;
      try {
        outcome = resolveZellerPairingOutcome(await call(terminal));
      } catch {
        // SDK 约定不抛异常；真抛了说明内部状态不可信，按没查到处理。
        outcome = { kind: "offline", reason: "SdkException" };
      }
      busyRef.current = false;
      if (!mountedRef.current) return;
      setBusy(null);
      if (outcome.kind === "cancelled") return;
      setLastCheck({
        time: formatClock(now()),
        failedReason: outcome.kind === "offline" ? outcome.reason : null,
      });
      setStatus(outcome.kind);
      if (outcome.kind === "paired") setWasPaired(true);
      if (outcome.kind === "unpaired") setWasPaired(false);
    },
    [now, terminal],
  );

  const check = useCallback(() => void run("checking", (client) => client.initialise()), [run]);
  const pair = useCallback(() => void run("pairing", (client) => client.setup()), [run]);

  // SDK 加载好后自动查一次，让打开页签看到的就是真实状态；这一步不弹窗、不需要操作。
  const autoCheckedRef = useRef(false);
  useEffect(() => {
    if (terminal && !autoCheckedRef.current) {
      autoCheckedRef.current = true;
      check();
    }
  }, [check, terminal]);

  const view: PanelView =
    sdk.status === "failed"
      ? "loadFailed"
      : sdk.status !== "ready"
        ? "loading"
        : busy === "pairing"
          ? "pairing"
          : busy === "checking" || status === "unknown"
            ? "checking"
            : status === "paired"
              ? enable?.active
                ? "active"
                : "paired"
              : status;

  const pill = PILL[view];
  const spinning = view === "loading" || view === "checking" || view === "pairing";
  const actionsDisabled = disabled || !terminal || busy !== null;

  const copy =
    view === "loadFailed"
      ? t("copy.loadFailed", { reason: sdk.status === "failed" ? sdk.reason : "" })
      : view === "checking"
        ? t("copy.checking")
        : view === "pairing"
          ? t("copy.pairing")
          : view === "active"
            ? t("copy.active")
            : view === "paired"
              ? activeProviderLabel
                ? t("copy.paired", { provider: activeProviderLabel })
                : t("copy.pairedNoProvider")
              : view === "offline"
                ? t("copy.offline")
                : t("card.intro");

  const pairingValue =
    view === "pairing"
      ? t("value.pairingInProgress")
      : view === "checking" || view === "loading"
        ? t("value.checking")
        : view === "paired" || view === "active"
          ? t("value.paired")
          : view === "unpaired"
            ? t("value.unpaired")
            : wasPaired
              ? t("value.unknownWasPaired")
              : t("value.unknown");
  const lastCheckValue = !lastCheck
    ? t("value.neverChecked")
    : lastCheck.failedReason
      ? t("value.checkFailedAt", { time: lastCheck.time, reason: lastCheck.failedReason })
      : t("value.checkedAt", { time: lastCheck.time });

  const paired = view === "paired" || view === "active";

  return (
    <View style={styles.panel} testID="settings-zeller-panel">
      <View style={styles.columns}>
        <View style={[styles.block, styles.statusBlock]}>
          <View style={styles.titleRow}>
            <Text style={styles.blockTitle}>Zeller</Text>
            <View style={[styles.pill, { backgroundColor: posColors[pill.soft] }]}>
              {spinning ? (
                <ActivityIndicator color={posColors.blue} size="small" />
              ) : pill.icon ? (
                <MaterialCommunityIcons color={posColors.ink} name={pill.icon} size={16} />
              ) : null}
              <Text style={styles.pillText} testID="settings-zeller-status">
                {t(pill.key)}
              </Text>
            </View>
          </View>
          <Text style={styles.copy} testID="settings-zeller-copy">
            {copy}
          </Text>

          <View style={styles.rows}>
            <Row label={t("row.pairing")} testID="settings-zeller-pairing" value={pairingValue} />
            <Row label={t("row.lastCheck")} testID="settings-zeller-last-check" value={lastCheckValue} />
            <Row
              label={t("row.activeProvider")}
              testID="settings-zeller-active-provider"
              value={activeProviderLabel ?? t("value.noProvider")}
            />
          </View>

          <View style={styles.actions}>
            {view === "offline" ? (
              <>
                <PanelButton
                  disabled={actionsDisabled}
                  icon="refresh"
                  label={t("action.retry")}
                  onPress={check}
                  testID="settings-zeller-check"
                  tone="blue"
                />
                {onOpenNetworkCheck ? (
                  <PanelButton
                    icon="access-point-network"
                    label={t("action.networkCheck")}
                    onPress={onOpenNetworkCheck}
                    sound="navigate"
                    testID="settings-zeller-network-check"
                    tone="secondary"
                  />
                ) : null}
              </>
            ) : (
              <>
                <PanelButton
                  disabled={actionsDisabled}
                  icon="link-variant"
                  label={t("action.check")}
                  onPress={check}
                  testID="settings-zeller-check"
                  tone="secondary"
                />
                <PanelButton
                  disabled={actionsDisabled}
                  icon="account-key-outline"
                  label={t(paired ? "action.repair" : "action.pair")}
                  onPress={pair}
                  testID="settings-zeller-pair"
                  tone={paired ? "secondary" : "primary"}
                />
              </>
            )}
          </View>

          {view === "offline" ? (
            <Banner
              hint={t("offline.hint")}
              icon="alert-outline"
              soft={posColors.redSoft}
              testID="settings-zeller-banner"
              title={t("offline.title")}
            />
          ) : view === "active" ? (
            <Banner
              hint={t("enable.activeHint")}
              icon="credit-card-check-outline"
              soft={posColors.greenSoft}
              testID="settings-zeller-banner"
              title={t("enable.activeTitle")}
            />
          ) : view === "paired" ? (
            <Banner
              hint={
                !enable
                  ? t("enable.notReadyHint")
                  : activeProviderLabel
                    ? t("enable.readyHint", { provider: activeProviderLabel })
                    : t("enable.readyHintNoProvider")
              }
              icon="swap-horizontal"
              soft={posColors.blueSoft}
              testID="settings-zeller-banner"
              title={t("enable.readyTitle")}
            >
              <PanelButton
                disabled={disabled || !enable}
                label={t("action.enable")}
                onPress={() => enable?.onEnable()}
                testID="settings-zeller-enable"
                tone="primary"
              />
            </Banner>
          ) : (
            <Banner
              hint={t("enable.lockedHint")}
              icon="lock-outline"
              soft={posColors.surfaceSunken}
              testID="settings-zeller-banner"
              title={t("enable.lockedTitle")}
            >
              <PanelButton
                disabled
                label={t("action.enable")}
                onPress={() => undefined}
                testID="settings-zeller-enable"
                tone="secondary"
              />
            </Banner>
          )}
        </View>

        <View style={[styles.block, styles.stepsBlock]}>
          <Text style={styles.blockTitle}>{t("steps.title")}</Text>
          <Step done={paired} index={1} text={t("steps.one")} />
          <Step current={view === "unpaired" || view === "pairing"} done={paired} index={2} text={t("steps.two")} />
          <Step current={view === "paired"} done={view === "active"} index={3} text={t("steps.three")} />
        </View>
      </View>
      <Text style={styles.footer}>{t("footer.note")}</Text>
    </View>
  );
}

/** 时钟时间 HH:mm；不带日期，界面上配「今天」二字。 */
function formatClock(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function Row({ label, testID, value }: Readonly<{ label: string; testID: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.row}>
      <Text style={styles.rowLabel}>{label}</Text>
      <Text selectable style={styles.rowValue} testID={testID}>
        {value}
      </Text>
    </View>
  );
}

function Step({
  current = false,
  done = false,
  index,
  text,
}: Readonly<{ current?: boolean; done?: boolean; index: number; text: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.step}>
      <View style={[styles.stepBadge, done && styles.stepBadgeDone, current && !done && styles.stepBadgeCurrent]}>
        {done ? (
          <MaterialCommunityIcons color={posColors.green} name="check" size={18} />
        ) : (
          <Text style={[styles.stepNumber, current && styles.stepNumberCurrent]}>{index}</Text>
        )}
      </View>
      <Text style={[styles.stepText, current && !done && styles.stepTextCurrent]}>{text}</Text>
    </View>
  );
}

function Banner({
  children,
  hint,
  icon,
  soft,
  testID,
  title,
}: Readonly<{
  children?: ReactNode;
  hint: string;
  icon: IconName;
  soft: string;
  testID: string;
  title: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.banner, { backgroundColor: soft }]} testID={testID}>
      <MaterialCommunityIcons color={posColors.ink} name={icon} size={24} />
      <View style={styles.bannerCopy}>
        <Text style={styles.bannerTitle} testID={`${testID}-title`}>
          {title}
        </Text>
        <Text style={styles.bannerHint} testID={`${testID}-hint`}>
          {hint}
        </Text>
      </View>
      {children}
    </View>
  );
}

const PANEL_BUTTON_TONES: Record<"primary" | "secondary" | "blue", PosButtonTone> = {
  primary: "primary",
  secondary: "secondary",
  blue: "info",
};

/** 面板里的操作按钮：统一走 PosButton（56 高），沿用原来的三种色调。 */
function PanelButton({
  disabled = false,
  icon,
  label,
  onPress,
  sound = "tap",
  testID,
  tone,
}: Readonly<{
  disabled?: boolean;
  icon?: IconName;
  label: string;
  onPress(): void;
  sound?: "tap" | "navigate";
  testID: string;
  tone: "primary" | "secondary" | "blue";
}>) {
  usePosThemeVersion();
  return (
    <PosButton
      disabled={disabled}
      label={label}
      onPress={onPress}
      size="medium"
      sound={sound}
      testID={testID}
      tone={PANEL_BUTTON_TONES[tone]}
      {...(icon ? { icon } : {})}
    />
  );
}

// 尺寸与设置页其余卡片一致：标题 17、正文 15、说明 12.5–13，按钮 56 高，圆角 10。
const styles = createPosStyles((colors) => ({
  panel: { gap: 8 },
  columns: { alignItems: "stretch", flexDirection: "row", gap: 10 },
  block: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    gap: 8,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  statusBlock: { flex: 1.15 },
  stepsBlock: { flex: 1 },
  titleRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  blockTitle: { color: colors.ink, fontSize: 17, fontWeight: "900" },
  pill: {
    alignItems: "center",
    borderRadius: 13,
    flexDirection: "row",
    gap: 6,
    minHeight: 26,
    paddingHorizontal: 10,
  },
  pillText: { color: colors.ink, fontSize: 12.5, fontWeight: "800" },
  copy: { color: colors.mutedInk, fontSize: 15, fontWeight: "600", lineHeight: 22 },
  rows: {
    borderTopColor: colors.border,
    borderTopWidth: 1,
  },
  row: {
    alignItems: "center",
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    minHeight: posTouch.min,
    paddingVertical: 8,
  },
  rowLabel: { color: colors.mutedInk, fontSize: 14, fontWeight: "700" },
  rowValue: { color: colors.ink, flexShrink: 1, fontSize: 15, fontWeight: "800", textAlign: "right" },
  actions: { flexDirection: "row", flexWrap: "wrap", gap: 10 },
  banner: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  bannerCopy: { flex: 1, minWidth: 160 },
  bannerTitle: { color: colors.ink, fontSize: 16, fontWeight: "900" },
  bannerHint: { color: colors.mutedInk, fontSize: 13, fontWeight: "600", lineHeight: 19, marginTop: 2 },
  step: { alignItems: "flex-start", flexDirection: "row", gap: 12, paddingVertical: 6 },
  stepBadge: {
    alignItems: "center",
    backgroundColor: colors.blueSoft,
    borderRadius: 15,
    height: 30,
    justifyContent: "center",
    width: 30,
  },
  stepBadgeDone: { backgroundColor: colors.greenSoft },
  stepBadgeCurrent: { backgroundColor: colors.inverseSurface },
  stepNumber: { color: colors.blue, fontSize: 15, fontVariant: ["tabular-nums"], fontWeight: "900" },
  stepNumberCurrent: { color: colors.onInverse },
  stepText: { color: colors.ink, flex: 1, fontSize: 15, fontWeight: "600", lineHeight: 22, paddingTop: 4 },
  stepTextCurrent: { fontWeight: "900" },
  footer: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
  },
}));
