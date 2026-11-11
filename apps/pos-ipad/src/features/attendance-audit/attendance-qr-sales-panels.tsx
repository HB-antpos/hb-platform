import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, Image, Modal, Text, View } from "react-native";
import Svg, { Circle } from "react-native-svg";

import {
  attendanceAuditText,
  resolveAttendanceAuditLocale,
  type AttendanceAuditCopyKey,
} from "./attendance-audit-copy";
import type { AttendanceQrDisplaySource } from "./attendance-audit-runtime";
import {
  ATTENDANCE_QR_TOKEN_LIFETIME_MS,
  type AttendanceQrState,
} from "./attendance-qr-controller";

import { PosButton } from "@/ui/controls/pos-button";
import { PosPressable } from "@/ui/controls/pos-pressable";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
} from "@/ui/theme";

/**
 * 尺寸按扫码距离定（iPad 264 ppi，1pt ≈ 0.19mm）：
 * - 空车大码 268pt ≈ 52mm，手机隔一臂距离也能扫；
 * - 放大弹窗 356pt ≈ 68mm；
 * - 侧栏缩略码 46pt 只是入口，模块太小扫不了，必须点开放大。
 * 码四周留白边作静区，正中压倒计时圆标（原生用 Q 级纠错，圆标只遮约 4%）。
 */
export const ATTENDANCE_SALES_QR_SIZE = 268;
export const ATTENDANCE_MODAL_QR_SIZE = 356;
const RAIL_QR_SIZE = 46;
const QR_QUIET_ZONE = 16;
const TOKEN_LIFETIME_SECONDS = Math.round(
  ATTENDANCE_QR_TOKEN_LIFETIME_MS / 1000,
);

type Translate = (
  key: AttendanceAuditCopyKey,
  values?: Readonly<Record<string, string | number>>,
) => string;

/** 文案语言：收银页传入自己的 locale 时以它为准（与收银页其余文字一致），否则跟随 i18n。 */
function useAttendanceQrCopy(language?: string): Translate {
  const { i18n } = useTranslation();
  const locale = resolveAttendanceAuditLocale(
    language ?? i18n.resolvedLanguage ?? i18n.language,
  );
  return (key, values) => attendanceAuditText(locale, key, values);
}

const NO_SOURCE_SUBSCRIBE = () => () => undefined;
const NO_SOURCE_STATE = (): AttendanceQrState | null => null;

/** 订阅考勤码状态；没有考勤服务（组合根未提供或未登录）时返回 null。 */
export function useAttendanceQrState(
  source: AttendanceQrDisplaySource | null,
): AttendanceQrState | null {
  return useSyncExternalStore(
    source ? source.subscribe : NO_SOURCE_SUBSCRIBE,
    source ? source.getState : NO_SOURCE_STATE,
    source ? source.getState : NO_SOURCE_STATE,
  );
}

type QrPlaceholderKind =
  | "preparing"
  | "enable-online"
  | "clock-rollback"
  | "setup-failed";

/** 没有码图时显示哪种说明：与 WPF 同一套状态（请先联网启用 / 时钟变化需校时 / 启用失败自动重试）。 */
function placeholderKind(state: AttendanceQrState): QrPlaceholderKind {
  if (state.kind === "clock-invalid" || state.statusCode === "clock-rollback") {
    return "clock-rollback";
  }
  if (state.kind === "setup-failed") return "setup-failed";
  if (state.kind === "unavailable") return "enable-online";
  return "preparing";
}

/**
 * 码正中的倒计时圆标：白底 + 剩余秒数 + 进度环（在线绿 / 离线黄）。
 * 员工对准码时自然看到，快到换码时可以等下一张。
 */
function CountdownBadge({
  t,
  online,
  seconds,
  size,
  testID,
}: Readonly<{
  online: boolean;
  seconds: number;
  size: number;
  t: Translate;
  testID?: string;
}>) {
  
  const stroke = Math.max(2, Math.round(size * 0.075));
  const radius = (size - stroke) / 2;
  const circumference = 2 * Math.PI * radius;
  const fraction = Math.min(1, Math.max(0, seconds / TOKEN_LIFETIME_SECONDS));
  const ringColor = online ? posColors.green : posColors.yellow;
  return (
    <View
      accessibilityLabel={t("panel.seconds", { seconds })}
      accessible
      pointerEvents="none"
      style={[
        styles.badge,
        { borderRadius: size / 2, height: size, width: size },
      ]}
      testID={testID}
    >
      <Svg height={size} style={styles.badgeRing} width={size}>
        <Circle
          cx={size / 2}
          cy={size / 2}
          fill="none"
          r={radius}
          stroke="#E3E8EE"
          strokeWidth={stroke}
        />
        {/* 从 12 点方向顺时针走：剩余越少弧越短。 */}
        <Circle
          cx={size / 2}
          cy={size / 2}
          fill="none"
          r={radius}
          stroke={ringColor}
          strokeDasharray={`${circumference} ${circumference}`}
          strokeDashoffset={circumference * (1 - fraction)}
          strokeLinecap="round"
          strokeWidth={stroke}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
      </Svg>
      <Text
        allowFontScaling={false}
        style={[styles.badgeText, { fontSize: Math.round(size * 0.42) }]}
      >
        {seconds}
      </Text>
    </View>
  );
}

/** 白底码图 + 正中倒计时；码图固定白底黑码，不跟随主题，深色主题下也能扫。 */
function QrWithCountdown({
  badgeSize,
  imageTestID,
  qrSize,
  state,
  t,
}: Readonly<{
  badgeSize: number;
  imageTestID: string;
  qrSize: number;
  state: AttendanceQrState & { qrImageUri: string };
  t: Translate;
}>) {
  
  return (
    <View style={{ height: qrSize, width: qrSize }}>
      <Image
        accessibilityLabel={t("qr.imageLabel")}
        resizeMode="contain"
        source={{ uri: state.qrImageUri }}
        style={{ height: qrSize, width: qrSize }}
        testID={imageTestID}
      />
      <View pointerEvents="none" style={styles.badgeCenter}>
        <CountdownBadge
          online={state.online}
          seconds={state.secondsRemaining}
          size={badgeSize}
          t={t}
          testID={`${imageTestID}-countdown`}
        />
      </View>
    </View>
  );
}

function QrPlaceholder({
  qrSize,
  state,
  t,
  testID,
}: Readonly<{
  qrSize: number;
  state: AttendanceQrState;
  t: Translate;
  testID: string;
}>) {
  
  const kind = placeholderKind(state);
  const warning = kind === "clock-rollback" || kind === "setup-failed";
  return (
    <View
      accessibilityLiveRegion="polite"
      style={[styles.placeholder, { height: qrSize, width: qrSize }]}
      testID={testID}
    >
      {kind === "preparing" ? (
        <ActivityIndicator color={posColors.accent} size="large" />
      ) : (
        <MaterialCommunityIcons
          accessible={false}
          color={warning ? posColors.red : posColors.yellowInk}
          name={
            kind === "clock-rollback"
              ? "clock-alert-outline"
              : kind === "setup-failed"
                ? "alert-circle-outline"
                : "wifi-off"
          }
          size={44}
        />
      )}
      <Text style={styles.placeholderTitle}>
        {t(`panel.state.${kind}.title`)}
      </Text>
      <Text style={styles.placeholderBody}>
        {t(`panel.state.${kind}.body`)}
      </Text>
    </View>
  );
}

function hasImage(
  state: AttendanceQrState,
): state is AttendanceQrState & { qrImageUri: string } {
  return state.qrImageUri !== null;
}

function StatusPill({
  state,
  t,
}: Readonly<{ state: AttendanceQrState; t: Translate }>) {
  
  if (!hasImage(state)) return null;
  return (
    <View
      style={[styles.pill, state.online ? styles.pillOnline : styles.pillOffline]}
      testID="attendance-qr-status"
    >
      <MaterialCommunityIcons
        accessible={false}
        color={state.online ? posColors.green : posColors.yellowInk}
        name={state.online ? "shield-check-outline" : "shield-key-outline"}
        size={18}
      />
      <Text
        style={[
          styles.pillText,
          { color: state.online ? posColors.green : posColors.yellowInk },
        ]}
      >
        {state.online ? t("panel.online") : t("panel.offline")}
      </Text>
    </View>
  );
}

function TerminalMeta({
  state,
  t,
}: Readonly<{ state: AttendanceQrState; t: Translate }>) {
  
  return (
    <View style={styles.metaColumn}>
      {state.storeText ? (
        <View style={styles.metaRow}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.mutedInk}
            name="storefront-outline"
            size={16}
          />
          <Text numberOfLines={1} style={styles.metaText}>
            {state.storeText}
          </Text>
        </View>
      ) : null}
      {state.deviceText ? (
        <View style={styles.metaRow}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.mutedInk}
            name="card-account-details-outline"
            size={16}
          />
          <Text numberOfLines={1} style={styles.metaText}>
            {t("panel.device", { device: state.deviceText })}
          </Text>
        </View>
      ) : null}
    </View>
  );
}

/**
 * 空购物车时购物车区域中间的大码：上班、下班、交接时购物车通常是空的，
 * 员工不用让收银员点任何按钮就能直接扫。扫入第一件商品后由收银页换回购物车列表。
 */
export function AttendanceQrEmptyCartCard({
  language,
  state,
}: Readonly<{ language?: string; state: AttendanceQrState }>) {
  usePosThemeVersion();
  const t = useAttendanceQrCopy(language);
  return (
    <View style={styles.cardWrap} testID="sales-attendance-qr-card">
      <View style={styles.card}>
        <View
          style={[
            styles.qrFrame,
            { padding: QR_QUIET_ZONE },
          ]}
        >
          {hasImage(state) ? (
            <QrWithCountdown
              badgeSize={58}
              imageTestID="sales-attendance-qr-image"
              qrSize={ATTENDANCE_SALES_QR_SIZE}
              state={state}
              t={t}
            />
          ) : (
            <QrPlaceholder
              qrSize={ATTENDANCE_SALES_QR_SIZE}
              state={state}
              t={t}
              testID="sales-attendance-qr-placeholder"
            />
          )}
        </View>
        <View style={styles.cardCopy}>
          <View style={styles.titleRow}>
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.accent}
              name="account-clock-outline"
              size={32}
            />
            <Text style={styles.cardTitle}>{t("panel.title")}</Text>
          </View>
          <Text style={styles.cardHint}>{t("panel.hint")}</Text>
          {hasImage(state) ? (
            <View style={styles.metaRow}>
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.green}
                name="timer-outline"
                size={16}
              />
              <Text style={styles.countdownHint}>
                {t("panel.countdownHint")}
              </Text>
            </View>
          ) : null}
          <StatusPill state={state} t={t} />
          <TerminalMeta state={state} t={t} />
        </View>
      </View>
      <Text style={styles.collapseNote}>{t("panel.collapseNote")}</Text>
    </View>
  );
}

/**
 * 左侧栏「考勤」小格（放在「更多 / 锁屏」上方）：缩略码只是入口，点开放大弹窗再扫。
 */
export function AttendanceQrRailTile({
  active,
  disabled = false,
  language,
  onPress,
  state,
}: Readonly<{
  active: boolean;
  disabled?: boolean;
  language?: string;
  onPress(): void;
  state: AttendanceQrState;
}>) {
  usePosThemeVersion();
  const t = useAttendanceQrCopy(language);
  const warning =
    state.kind === "clock-invalid" || state.kind === "setup-failed";
  return (
    <PosPressable
      accessibilityHint={t("panel.railHint")}
      accessibilityLabel={t("panel.railLabel")}
      accessibilityRole="button"
      accessibilityState={{ disabled, expanded: active }}
      disabled={disabled}
      onPress={onPress}
      sound="navigate"
      style={({ pressed }) => [
        styles.railTile,
        active && styles.railTileActive,
        pressed && !disabled && styles.railTilePressed,
      ]}
      testID="sales-attendance-qr-rail"
    >
      {hasImage(state) ? (
        <View style={styles.railQrFrame}>
          <QrWithCountdown
            badgeSize={20}
            imageTestID="sales-attendance-qr-rail-image"
            qrSize={RAIL_QR_SIZE}
            state={state}
            t={t}
          />
        </View>
      ) : (
        <View style={styles.railIconBox}>
          <MaterialCommunityIcons
            accessible={false}
            color={warning ? posColors.red : posColors.mutedInk}
            name="qrcode"
            size={34}
          />
          <View
            style={[
              styles.railDot,
              { backgroundColor: warning ? posColors.red : posColors.yellow },
            ]}
          />
        </View>
      )}
      <Text style={[styles.railLabel, active && { color: posColors.accent }]}>
        {t("panel.railLabel")}
      </Text>
    </PosPressable>
  );
}

/**
 * 点「考勤」后的放大弹窗：收银中途有员工要打卡，点一下、扫完点空白处关掉。
 * 不需要主管授权，也不碰购物车；离开收银页或锁屏时由收银页关闭。
 */
export function AttendanceQrModal({
  language,
  onClose,
  state,
  visible,
}: Readonly<{
  language?: string;
  onClose(): void;
  state: AttendanceQrState | null;
  visible: boolean;
}>) {
  usePosThemeVersion();
  const t = useAttendanceQrCopy(language);
  return (
    <Modal
      animationType="fade"
      onRequestClose={onClose}
      presentationStyle="overFullScreen"
      supportedOrientations={["landscape-left", "landscape-right"]}
      transparent
      visible={visible && state !== null}
    >
      <View style={styles.backdrop}>
        <PosPressable
          accessibilityLabel={t("panel.closeShort")}
          accessible={false}
          onPress={onClose}
          sound="navigate"
          style={styles.backdropDismiss}
          testID="sales-attendance-qr-modal-backdrop"
        />
        {state ? (
          <View
            accessibilityViewIsModal
            style={styles.modal}
            testID="sales-attendance-qr-modal"
          >
            <View style={styles.modalHeader}>
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.accent}
                name="account-clock-outline"
                size={32}
              />
              <Text style={styles.modalTitle}>{t("panel.title")}</Text>
              <StatusPill state={state} t={t} />
              <View style={styles.flexSpacer} />
              <PosPressable
                accessibilityLabel={t("panel.closeShort")}
                accessibilityRole="button"
                onPress={onClose}
                sound="navigate"
                style={({ pressed }) => [
                  styles.modalCloseIcon,
                  pressed && styles.railTilePressed,
                ]}
                testID="sales-attendance-qr-modal-close"
              >
                <MaterialCommunityIcons
                  accessible={false}
                  color={posColors.ink}
                  name="close"
                  size={30}
                />
              </PosPressable>
            </View>
            <View style={[styles.qrFrame, styles.modalQrFrame]}>
              {hasImage(state) ? (
                <QrWithCountdown
                  badgeSize={72}
                  imageTestID="sales-attendance-qr-modal-image"
                  qrSize={ATTENDANCE_MODAL_QR_SIZE}
                  state={state}
                  t={t}
                />
              ) : (
                <QrPlaceholder
                  qrSize={ATTENDANCE_MODAL_QR_SIZE}
                  state={state}
                  t={t}
                  testID="sales-attendance-qr-modal-placeholder"
                />
              )}
            </View>
            <View style={styles.modalMetaRow}>
              <TerminalMeta state={state} t={t} />
              <View style={styles.flexSpacer} />
              <View style={styles.metaRow}>
                <MaterialCommunityIcons
                  accessible={false}
                  color={posColors.green}
                  name="timer-outline"
                  size={16}
                />
                <Text style={styles.metaText}>{t("panel.rotate")}</Text>
              </View>
            </View>
            <Text style={styles.modalHint}>{t("panel.hint")}</Text>
            <PosButton
              label={t("panel.close")}
              onPress={onClose}
              size="button"
              sound="navigate"
              testID="sales-attendance-qr-modal-done"
              tone="secondary"
            />
          </View>
        ) : null}
      </View>
    </Modal>
  );
}

const styles = createPosStyles((colors) => ({
  backdrop: {
    alignItems: "center",
    backgroundColor: colors.overlay,
    flex: 1,
    justifyContent: "center",
  },
  backdropDismiss: {
    bottom: 0,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  badge: {
    alignItems: "center",
    // 圆标固定白底，与码图一致，不跟随主题。
    backgroundColor: "#FFFFFF",
    justifyContent: "center",
  },
  badgeCenter: {
    alignItems: "center",
    bottom: 0,
    justifyContent: "center",
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  badgeRing: {
    left: 0,
    position: "absolute",
    top: 0,
  },
  badgeText: {
    color: "#10253A",
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  card: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 28,
    maxWidth: 760,
    padding: 22,
    width: "100%",
  },
  cardCopy: {
    alignItems: "flex-start",
    flex: 1,
    gap: 12,
    minWidth: 0,
  },
  cardHint: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "600",
    lineHeight: 25,
  },
  cardTitle: {
    color: colors.ink,
    fontSize: 28,
    fontWeight: "900",
  },
  cardWrap: {
    alignItems: "center",
    flex: 1,
    gap: 14,
    justifyContent: "center",
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  collapseNote: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "600",
    textAlign: "center",
  },
  countdownHint: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 14,
    fontWeight: "600",
    lineHeight: 20,
  },
  flexSpacer: {
    flex: 1,
  },
  metaColumn: {
    gap: 6,
    minWidth: 0,
  },
  metaRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
    minWidth: 0,
  },
  metaText: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 14,
    fontWeight: "600",
  },
  modal: {
    backgroundColor: colors.canvas,
    borderRadius: 20,
    gap: 14,
    paddingHorizontal: 28,
    paddingVertical: 22,
    width: 560,
  },
  modalCloseIcon: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.ink,
    borderRadius: posRadius.large,
    borderWidth: 2,
    height: 56,
    justifyContent: "center",
    width: 56,
  },
  modalHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  modalHint: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "600",
    textAlign: "center",
  },
  modalMetaRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
  },
  modalQrFrame: {
    alignSelf: "center",
    padding: (392 - ATTENDANCE_MODAL_QR_SIZE) / 2,
  },
  modalTitle: {
    color: colors.ink,
    fontSize: 26,
    fontWeight: "900",
  },
  pill: {
    alignItems: "center",
    alignSelf: "flex-start",
    borderRadius: 999,
    borderWidth: 1,
    flexDirection: "row",
    gap: 6,
    paddingHorizontal: 12,
    paddingVertical: 6,
  },
  pillOffline: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
  },
  pillOnline: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
  },
  pillText: {
    fontSize: 15,
    fontWeight: "800",
  },
  placeholder: {
    alignItems: "center",
    gap: 10,
    justifyContent: "center",
    paddingHorizontal: 18,
  },
  placeholderBody: {
    color: "#5D6D7E",
    fontSize: 14,
    fontWeight: "600",
    lineHeight: 20,
    textAlign: "center",
  },
  placeholderTitle: {
    color: "#10253A",
    fontSize: 18,
    fontWeight: "900",
    textAlign: "center",
  },
  qrFrame: {
    // 二维码必须白底，这里不跟随主题。
    backgroundColor: "#FFFFFF",
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1.5,
  },
  railDot: {
    borderColor: colors.surface,
    borderRadius: 6,
    borderWidth: 2,
    height: 12,
    position: "absolute",
    right: -2,
    top: -2,
    width: 12,
  },
  railIconBox: {
    alignItems: "center",
    height: RAIL_QR_SIZE + 6,
    justifyContent: "center",
    width: RAIL_QR_SIZE + 6,
  },
  railLabel: {
    color: colors.ink,
    fontSize: 12.5,
    fontWeight: "800",
    textAlign: "center",
  },
  railQrFrame: {
    backgroundColor: "#FFFFFF",
    borderRadius: 4,
    padding: 3,
  },
  railTile: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    gap: 6,
    justifyContent: "center",
    minHeight: 96,
    paddingVertical: 8,
  },
  railTileActive: {
    borderColor: colors.accent,
    borderWidth: 2,
  },
  railTilePressed: {
    opacity: 0.72,
  },
  titleRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
}));
