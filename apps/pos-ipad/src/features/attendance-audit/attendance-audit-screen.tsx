import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useEffect, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Image,
  ScrollView,
  Text,
  View,
} from "react-native";

import {
  attendanceAuditQrStatusCopyKey,
  attendanceAuditStatusCopyKey,
  attendanceAuditText,
  attendanceAuditUploadStateCopyKey,
  resolveAttendanceAuditLocale,
  type AttendanceAuditCopyKey,
} from "./attendance-audit-copy";
import type {
  AttendanceAuditPresenter,
  AttendanceAuditPresenterState,
} from "./attendance-audit-presenter";
import type {
  OperationAuditRecord,
  OperationAuditSource,
  OperationAuditUploadState,
} from "@hb/pos-domain/features/attendance-audit/operation-audit-presenter";

import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPill, type PosPillTone } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import {
  PosSegmented,
  type PosSegmentedOption,
} from "@/ui/controls/pos-segmented";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
  type PosPalette,
} from "@/ui/theme";

export const ATTENDANCE_AUDIT_MIN_TOUCH_TARGET = 44;

/** 二维码边长；外面再留一圈白边，保证扫码器能识别静区。 */
const ATTENDANCE_QR_SIZE = 210;

export type AttendanceAuditScreenPresenter = Pick<
  AttendanceAuditPresenter,
  | "getState"
  | "loadAudit"
  | "refreshAttendanceQr"
  | "selectAudit"
  | "setAuditQuery"
  | "setAuditSource"
  | "setAuditUploadState"
  | "start"
  | "subscribe"
>;

type AttendanceAuditScreenProps = Readonly<{
  onBack?(): void;
  presenter: AttendanceAuditScreenPresenter;
}>;

type AttendanceAuditTranslate = (
  key: AttendanceAuditCopyKey,
  values?: Readonly<Record<string, string | number>>,
) => string;

type QrStatusCode = AttendanceAuditPresenterState["qr"]["statusCode"];

/** 上传状态分段里「全部」对应 presenter 的 null。 */
type UploadFilterValue = OperationAuditUploadState | "all";

// 这里只存色名，渲染时再从当前主题取色，换主题后才会跟着变。
const QR_STATUS_COLOR: Record<QrStatusCode, keyof PosPalette> = {
  "clock-rollback": "red",
  "enable-online": "yellowInk",
  "offline-signed": "yellowInk",
  "online-verified": "green",
  "setup-failed": "red",
};

const UPLOAD_STATE_TONE: Record<OperationAuditUploadState, PosPillTone> = {
  pending: "warning",
  rejected: "danger",
  uploaded: "good",
};

/**
 * iPad 横屏高密度工作台：考勤 QR 始终独立显示；审计读取按 Audit.View
 * 单独保护。UI 永不接收二维码 token、签名密钥或审计 properties 原文。
 */
export function AttendanceAuditScreen({
  onBack,
  presenter,
}: AttendanceAuditScreenProps) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveAttendanceAuditLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t: AttendanceAuditTranslate = (key, values) =>
    attendanceAuditText(locale, key, values);
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );

  useEffect(() => {
    presenter.start();
  }, [presenter]);

  return (
    <PosScreenFrame
      active="attendance-audit"
      header={
        <PosPageHeader
          backLabel={t("action.back")}
          backTestID="attendance-audit-back"
          onBack={onBack}
          subtitle={t("header.subtitle")}
          title={t("header.title")}
        />
      }
      testID="attendance-audit-screen"
    >
      <View style={styles.workspace} testID="attendance-audit-workspace">
        <AttendanceQrPane presenter={presenter} state={state} t={t} />
        <AuditWorkspace presenter={presenter} state={state} t={t} />
      </View>
    </PosScreenFrame>
  );
}

function AttendanceQrPane({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: AttendanceAuditScreenPresenter;
  state: AttendanceAuditPresenterState;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  const qr = state.qr;
  const locked = qr.kind === "clock-invalid" || qr.requiresOnlineResync;
  return (
    <View style={[styles.card, styles.qrPanel]}>
      <View style={styles.panelHeading}>
        <Text numberOfLines={1} style={styles.panelTitle}>
          {t("qr.title")}
        </Text>
        <PosPill
          label={qr.online ? t("qr.online") : t("qr.offline")}
          style={styles.headingPill}
          tone={qr.online ? "good" : "warning"}
        />
      </View>

      <View style={styles.qrStage}>
        {locked ? (
          <View
            accessibilityLiveRegion="assertive"
            style={styles.clockLock}
            testID="attendance-clock-lock"
          >
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.red}
              name="clock-alert-outline"
              size={48}
            />
            <Text style={styles.lockTitle}>{t("qr.clockRollback.title")}</Text>
            <Text style={styles.lockBody}>{t("qr.clockRollback.body")}</Text>
          </View>
        ) : qr.qrImageUri ? (
          // 二维码固定白底黑码，不跟随主题，深色主题下也要保证能扫。
          <View style={styles.qrFrame}>
            <Image
              accessibilityLabel={t("qr.imageLabel")}
              resizeMode="contain"
              source={{ uri: qr.qrImageUri }}
              style={styles.qrImage}
              testID="attendance-qr-image"
            />
          </View>
        ) : (
          <View style={styles.qrPlaceholder}>
            {qr.kind === "initializing" ? (
              <ActivityIndicator color={posColors.accent} size="large" />
            ) : null}
            <Text style={styles.placeholderTitle}>
              {t(
                qr.kind === "initializing" ? "qr.preparing" : "qr.unavailable",
              )}
            </Text>
            <Text style={styles.placeholderBody}>{t("qr.placeholder")}</Text>
          </View>
        )}
      </View>

      <View style={styles.countdownCard}>
        <Text
          adjustsFontSizeToFit
          numberOfLines={1}
          style={styles.countdownValue}
        >
          {t("qr.remaining", { seconds: qr.secondsRemaining })}
        </Text>
        <Text style={styles.countdownLabel}>{t("qr.remainingLabel")}</Text>
      </View>
      <View style={styles.contextCard}>
        <ContextRow label={t("context.store")} value={qr.storeText || "—"} />
        <ContextRow
          divider
          label={t("context.device")}
          value={qr.deviceText || "—"}
        />
      </View>
      <QrStatusMessage statusCode={qr.statusCode} t={t} />
      <PosButton
        icon="shield-refresh-outline"
        label={t("action.refreshQr")}
        onPress={() => void presenter.refreshAttendanceQr()}
        size="button"
        style={styles.qrRefresh}
        testID="attendance-qr-refresh"
      />
    </View>
  );
}

function AuditWorkspace({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: AttendanceAuditScreenPresenter;
  state: AttendanceAuditPresenterState;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  const audit = state.audit;
  if (!audit.access.canView) {
    // 没有审计权限：右半边整体换成说明，左边考勤二维码不受影响。
    return (
      <View
        style={[styles.card, styles.auditPanel, styles.centeredPanel]}
        testID="audit-permission-required"
      >
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.mutedInk}
          name="shield-lock-outline"
          size={56}
        />
        <Text style={styles.permissionCode}>AUDIT.VIEW</Text>
        <Text style={styles.permissionTitle}>{t("audit.permission.title")}</Text>
        <Text style={styles.permissionBody}>{t("audit.permission.body")}</Text>
      </View>
    );
  }

  return (
    <View style={styles.auditPanel}>
      <View style={styles.auditHeading}>
        <Text style={styles.panelTitle}>{t("audit.title")}</Text>
        <PosPill
          label={t(audit.rows.length === 1 ? "audit.resultCountOne" : "audit.resultCount", { count: audit.rows.length })}
          style={styles.headingPill}
        />
      </View>

      <AuditFilters presenter={presenter} state={state} t={t} />
      <AuditStatus state={state} t={t} />

      <View style={styles.auditMasterDetail}>
        <AuditList presenter={presenter} state={state} t={t} />
        <AuditDetails state={state} t={t} />
      </View>
    </View>
  );
}

function AuditFilters({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: AttendanceAuditScreenPresenter;
  state: AttendanceAuditPresenterState;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  const audit = state.audit;
  const sources: readonly PosSegmentedOption<OperationAuditSource>[] = [
    {
      label: t("audit.source.local"),
      testID: "audit-source-local",
      value: "local",
    },
    {
      // 远程审计必须联网，离线时这一段不可点。
      disabled: !audit.online,
      label: t("audit.source.remote"),
      testID: "audit-source-remote",
      value: "remote",
    },
  ];
  const uploadStates: readonly PosSegmentedOption<UploadFilterValue>[] = [
    { label: t("audit.upload.all"), testID: "audit-upload-all", value: "all" },
    {
      label: t("audit.upload.pending"),
      testID: "audit-upload-pending",
      value: "pending",
    },
    {
      label: t("audit.upload.uploaded"),
      testID: "audit-upload-uploaded",
      value: "uploaded",
    },
    {
      label: t("audit.upload.rejected"),
      testID: "audit-upload-rejected",
      value: "rejected",
    },
  ];
  return (
    <PosKeyboardAwareScrollView
      contentContainerStyle={styles.filters}
      style={styles.filtersScroll}
      testID="attendance-audit-filters-keyboard-scroll"
    >
      <PosSegmented
        onChange={(source) => presenter.setAuditSource(source)}
        options={sources}
        style={styles.sourceSegment}
        value={audit.source}
      />
      <PosSegmented
        hugContent
        onChange={(value) =>
          presenter.setAuditUploadState(value === "all" ? null : value)
        }
        options={uploadStates}
        value={audit.uploadState ?? "all"}
      />
      {/* 搜索框与「查询」绑在一组：英文界面一行放不下时整组一起换行。 */}
      <View style={styles.searchRow}>
        <View style={styles.searchField}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.mutedInk}
            name="magnify"
            size={24}
          />
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("audit.searchLabel")}
            autoCapitalize="none"
            autoCorrect={false}
            onChangeText={(value) => presenter.setAuditQuery(value)}
            onSubmitEditing={() => void presenter.loadAudit()}
            placeholder={t("audit.searchPlaceholder")}
            placeholderTextColor={posColors.placeholder}
            style={styles.searchInput}
            testID="audit-search-input"
            value={audit.query}
          />
        </View>
        <PosButton
          disabled={
            audit.kind === "loading" ||
            (audit.source === "remote" && !audit.online)
          }
          label={t("audit.action.search")}
          onPress={() => void presenter.loadAudit()}
          size="medium"
          testID="audit-search-submit"
        />
      </View>
    </PosKeyboardAwareScrollView>
  );
}

function AuditStatus({
  state,
  t,
}: Readonly<{
  state: AttendanceAuditPresenterState;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  const audit = state.audit;
  if (audit.kind === "loading") {
    return (
      <View style={styles.loadingLine}>
        <ActivityIndicator color={posColors.accent} />
        <Text style={styles.loadingText}>{t("audit.loading")}</Text>
      </View>
    );
  }
  const message = auditStatusMessage(audit.statusCode, t);
  if (!message) return null;
  return (
    <View accessibilityLiveRegion="polite" style={styles.auditWarning}>
      <Text style={styles.auditWarningText}>{message}</Text>
    </View>
  );
}

function AuditList({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: AttendanceAuditScreenPresenter;
  state: AttendanceAuditPresenterState;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  const audit = state.audit;
  return (
    <ScrollView
      contentContainerStyle={styles.auditList}
      style={styles.auditListPane}
    >
      {audit.rows.length === 0 ? (
        <View style={styles.emptyCard}>
          <Text style={styles.emptyTitle}>{t("audit.empty.title")}</Text>
          <Text style={styles.emptyBody}>{t("audit.empty.body")}</Text>
        </View>
      ) : (
        audit.rows.map((row) => (
          <AuditRow
            key={row.eventId}
            onPress={() => void presenter.selectAudit(row.eventId)}
            record={row}
            selected={audit.selectedEventId === row.eventId}
            t={t}
          />
        ))
      )}
    </ScrollView>
  );
}

function AuditRow({
  onPress,
  record,
  selected,
  t,
}: Readonly<{
  onPress(): void;
  record: OperationAuditRecord;
  selected: boolean;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.auditRow, selected && styles.auditRowSelected]}
      testID={`audit-row-${record.eventId}`}
    >
      <View style={styles.auditRowTop}>
        <Text numberOfLines={1} style={styles.auditOperation}>
          {record.operationType}
        </Text>
        <PosPill
          label={uploadStateLabel(record.uploadState, t)}
          style={styles.headingPill}
          tone={UPLOAD_STATE_TONE[record.uploadState]}
        />
      </View>
      <Text numberOfLines={1} style={styles.auditPrimary}>
        {auditRowReference(record, t)}
      </Text>
      <Text numberOfLines={1} style={styles.auditMeta}>
        {formatTimestamp(record.occurredAtIso)} · {record.cashierName ?? "—"}
      </Text>
    </PosPressable>
  );
}

function AuditDetails({
  state,
  t,
}: Readonly<{
  state: AttendanceAuditPresenterState;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  const { audit } = state;
  return (
    <View style={[styles.card, styles.auditDetailPane]}>
      <Text style={styles.panelTitle}>{t("audit.details")}</Text>
      <ScrollView
        contentContainerStyle={styles.detailScroll}
        style={styles.detailScrollView}
      >
        {audit.detailLoading ? (
          <ActivityIndicator color={posColors.accent} />
        ) : audit.detail ? (
          <AuditDetailCard record={audit.detail} t={t} />
        ) : (
          <View style={styles.emptyCard}>
            <Text style={styles.emptyTitle}>
              {t("audit.detail.empty.title")}
            </Text>
            <Text style={styles.emptyBody}>{t("audit.detail.empty.body")}</Text>
          </View>
        )}
      </ScrollView>
    </View>
  );
}

function AuditDetailCard({
  record,
  t,
}: Readonly<{
  record: OperationAuditRecord;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.detailCard}>
      {/* 8 个字段两列排，一眼能扫完；长订单号在各自的格里换行。 */}
      <View style={styles.detailGrid}>
        <DetailField
          label={t("audit.detail.operation")}
          value={record.operationType}
        />
        <DetailField label={t("audit.detail.outcome")} value={record.outcome} />
        <DetailField
          label={t("audit.detail.time")}
          value={formatTimestamp(record.occurredAtIso)}
        />
        <DetailField
          label={t("audit.detail.cashier")}
          value={record.cashierName ?? "—"}
        />
        <DetailField
          label={t("audit.detail.receipt")}
          value={record.receiptNumber ?? "—"}
        />
        <DetailField
          label={t("audit.detail.order")}
          value={record.orderGuid ?? "—"}
        />
        <DetailField
          label={t("audit.detail.amount")}
          value={
            record.paymentAmountCents === null
              ? "—"
              : formatMoney(record.paymentAmountCents)
          }
        />
        <DetailField
          label={t("audit.detail.correlation")}
          value={record.correlationId ?? "—"}
        />
      </View>
      {record.safeMessage ? (
        <View style={styles.messageCard}>
          <Text style={styles.detailLabel}>{t("audit.detail.safeMessage")}</Text>
          <Text selectable style={styles.safeMessage}>
            {record.safeMessage}
          </Text>
        </View>
      ) : null}
      {record.items.length > 0 ? (
        <View style={styles.itemsCard}>
          <Text style={styles.detailLabel}>{t("audit.detail.itemChanges")}</Text>
          {record.items.map((item) => (
            <View key={`${item.lineIndex}-${item.productCode ?? "none"}`}>
              <Text style={styles.itemTitle}>
                {item.displayName ?? item.productCode ?? "—"}
              </Text>
              <Text style={styles.itemMeta}>
                #{item.lineIndex + 1} · {t("audit.detail.quantity")}{" "}
                {item.quantityDelta ?? "—"} · {t("audit.detail.amount")}{" "}
                {item.actualAmountDeltaCents === null
                  ? "—"
                  : formatMoney(item.actualAmountDeltaCents)}
              </Text>
            </View>
          ))}
        </View>
      ) : null}
      <Text style={styles.detailFootnote}>{t("audit.detail.empty.body")}</Text>
    </View>
  );
}

export function AttendanceAuditUnavailableScreen({
  onBack,
}: Readonly<{ onBack?(): void }>) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveAttendanceAuditLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t: AttendanceAuditTranslate = (key, values) =>
    attendanceAuditText(locale, key, values);
  return (
    <PosScreenFrame
      active="attendance-audit"
      testID="attendance-audit-runtime-unavailable"
    >
      <View style={styles.unavailablePage}>
        <View style={[styles.card, styles.unavailableCard]}>
          <Text style={styles.permissionCode}>SECURE RUNTIME</Text>
          <Text style={styles.permissionTitle}>{t("unavailable.title")}</Text>
          <Text style={styles.permissionBody}>{t("unavailable.body")}</Text>
          {onBack ? (
            <PosButton
              icon="arrow-left"
              label={t("action.back")}
              onPress={onBack}
              size="large"
              sound="navigate"
              style={styles.unavailableAction}
              testID="attendance-audit-unavailable-back"
              tone="primary"
            />
          ) : null}
        </View>
      </View>
    </PosScreenFrame>
  );
}

function ContextRow({
  divider = false,
  label,
  value,
}: Readonly<{ divider?: boolean; label: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={[styles.contextRow, divider && styles.contextRowDivider]}>
      <Text style={styles.contextLabel}>{label}</Text>
      <Text numberOfLines={1} style={styles.contextValue}>
        {value}
      </Text>
    </View>
  );
}

function DetailField({
  label,
  value,
}: Readonly<{ label: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.detailField}>
      <Text style={styles.detailLabel}>{label}</Text>
      <Text selectable style={styles.detailValue}>
        {value}
      </Text>
    </View>
  );
}

function QrStatusMessage({
  statusCode,
  t,
}: Readonly<{
  statusCode: QrStatusCode;
  t: AttendanceAuditTranslate;
}>) {
  usePosThemeVersion();
  return (
    <Text
      style={[styles.qrStatus, { color: posColors[QR_STATUS_COLOR[statusCode]] }]}
    >
      {t(attendanceAuditQrStatusCopyKey(statusCode))}
    </Text>
  );
}

function auditStatusMessage(
  statusCode: AttendanceAuditPresenterState["audit"]["statusCode"],
  t: AttendanceAuditTranslate,
): string | null {
  if (!statusCode) return null;
  return t(attendanceAuditStatusCopyKey(statusCode));
}

function uploadStateLabel(
  value: OperationAuditUploadState,
  t: AttendanceAuditTranslate,
): string {
  return t(attendanceAuditUploadStateCopyKey(value));
}

/** 列表第二行：有小票显示小票号，其次订单号，都没有时退回事件编号前 8 位。 */
function auditRowReference(
  record: OperationAuditRecord,
  t: AttendanceAuditTranslate,
): string {
  if (record.receiptNumber) {
    return `${t("audit.detail.receipt")} ${record.receiptNumber}`;
  }
  if (record.orderGuid) {
    return `${t("audit.detail.order")} ${record.orderGuid}`;
  }
  return `${t("audit.row.event")} ${record.eventId.slice(0, 8)}`;
}

function formatTimestamp(value: string): string {
  return value.replace("T", " ").replace(".000Z", " UTC");
}

function formatMoney(cents: number): string {
  const sign = cents < 0 ? "-" : "";
  return `${sign}$${(Math.abs(cents) / 100).toFixed(2)}`;
}

const styles = createPosStyles((colors) => ({
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    padding: 14,
  },
  qrPanel: {
    gap: 10,
    width: 330,
  },
  auditPanel: {
    flex: 1,
    gap: 10,
    minWidth: 0,
  },
  centeredPanel: {
    alignItems: "center",
    gap: 10,
    justifyContent: "center",
    paddingHorizontal: 48,
  },
  panelHeading: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "space-between",
  },
  auditHeading: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  panelTitle: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 19,
    fontWeight: "900",
  },
  headingPill: {
    alignSelf: "center",
  },
  qrStage: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: ATTENDANCE_QR_SIZE + 18,
  },
  qrFrame: {
    alignItems: "center",
    // 二维码必须白底，这里不跟随主题。
    backgroundColor: "#FFFFFF",
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1.5,
    justifyContent: "center",
    padding: 8,
  },
  qrImage: {
    height: ATTENDANCE_QR_SIZE,
    width: ATTENDANCE_QR_SIZE,
  },
  qrPlaceholder: {
    alignItems: "center",
    alignSelf: "stretch",
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 10,
    justifyContent: "center",
    minHeight: ATTENDANCE_QR_SIZE + 18,
    paddingHorizontal: 20,
  },
  placeholderTitle: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
    textAlign: "center",
  },
  placeholderBody: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
    textAlign: "center",
  },
  clockLock: {
    alignItems: "center",
    alignSelf: "stretch",
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 8,
    justifyContent: "center",
    minHeight: ATTENDANCE_QR_SIZE + 18,
    paddingHorizontal: 20,
  },
  lockTitle: {
    color: colors.red,
    fontSize: 20,
    fontWeight: "900",
    textAlign: "center",
  },
  lockBody: {
    color: colors.ink,
    fontSize: 14,
    fontWeight: "600",
    lineHeight: 20,
    textAlign: "center",
  },
  countdownCard: {
    alignItems: "center",
    backgroundColor: colors.accentSoft,
    borderRadius: posRadius.large,
    padding: 8,
  },
  countdownValue: {
    color: colors.accent,
    fontSize: 40,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    lineHeight: 44,
  },
  countdownLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },
  contextCard: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    paddingHorizontal: 14,
    paddingVertical: 4,
  },
  contextRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "space-between",
    minHeight: 34,
  },
  contextRowDivider: {
    borderTopColor: colors.border,
    borderTopWidth: 1,
  },
  contextLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
  },
  contextValue: {
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontWeight: "800",
    textAlign: "right",
  },
  qrStatus: {
    fontSize: 12.5,
    fontWeight: "800",
    lineHeight: 17,
    textAlign: "center",
  },
  qrRefresh: {
    // 把「安全刷新」压到面板底部。
    marginTop: "auto",
  },
  filters: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  filtersScroll: {
    flexGrow: 0,
  },
  sourceSegment: {
    width: 170,
  },
  searchRow: {
    alignItems: "center",
    flexBasis: 240,
    flexDirection: "row",
    flexGrow: 1,
    gap: 10,
  },
  searchField: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flex: 1,
    flexDirection: "row",
    gap: 8,
    height: posTouch.medium,
    minWidth: 0,
    paddingHorizontal: 12,
  },
  searchInput: {
    alignSelf: "stretch",
    color: colors.ink,
    flex: 1,
    fontSize: 16,
    fontWeight: "700",
    minHeight: ATTENDANCE_AUDIT_MIN_TOUCH_TARGET,
    paddingVertical: 0,
  },
  auditMasterDetail: {
    flex: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 0,
  },
  auditListPane: {
    flexGrow: 0,
    width: 330,
  },
  auditDetailPane: {
    flex: 1,
    gap: 10,
    minWidth: 0,
  },
  auditList: {
    gap: 8,
    paddingBottom: 4,
  },
  auditRow: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    gap: 3,
    justifyContent: "center",
    minHeight: 84,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  auditRowSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
    borderWidth: 2,
  },
  auditRowTop: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
  },
  auditOperation: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 16,
    fontWeight: "900",
  },
  auditPrimary: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
  },
  auditMeta: {
    color: colors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
  },
  detailScrollView: {
    flex: 1,
  },
  detailScroll: {
    flexGrow: 1,
    paddingBottom: 4,
  },
  detailCard: {
    flexGrow: 1,
    gap: 12,
  },
  detailGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    rowGap: 12,
  },
  detailField: {
    gap: 2,
    paddingRight: 12,
    width: "50%",
  },
  detailLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "800",
  },
  detailValue: {
    color: colors.ink,
    fontSize: 15.5,
    fontWeight: "900",
    lineHeight: 21,
  },
  messageCard: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    gap: 4,
    padding: 12,
  },
  safeMessage: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 21,
  },
  itemsCard: {
    borderTopColor: colors.border,
    borderTopWidth: 1,
    gap: 8,
    paddingTop: 12,
  },
  itemTitle: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  itemMeta: {
    color: colors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    marginTop: 2,
  },
  detailFootnote: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 17,
    // 脱敏说明压在详情卡片底部。
    marginTop: "auto",
  },
  loadingLine: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    minHeight: 40,
  },
  loadingText: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
  },
  auditWarning: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  auditWarningText: {
    color: colors.red,
    fontSize: 14,
    fontWeight: "800",
  },
  emptyCard: {
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderStyle: "dashed",
    borderWidth: 1,
    gap: 6,
    padding: 18,
  },
  emptyTitle: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  emptyBody: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 19,
  },
  permissionCode: {
    color: colors.accent,
    fontSize: 12,
    fontWeight: "900",
    letterSpacing: 1.8,
  },
  permissionTitle: {
    color: colors.ink,
    fontSize: 23,
    fontWeight: "900",
    textAlign: "center",
  },
  permissionBody: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 22,
    maxWidth: 580,
    textAlign: "center",
  },
  unavailablePage: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    padding: 32,
  },
  unavailableCard: {
    alignItems: "center",
    gap: 12,
    maxWidth: 620,
    padding: 28,
    width: "100%",
  },
  unavailableAction: {
    alignSelf: "stretch",
    marginTop: 8,
  },
}));
