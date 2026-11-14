import {
  CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH,
  CASH_MOVEMENT_NOTE_REQUIRED_REASONS,
  type CashMovementReasonCode,
} from "@hb/pos-domain/core/contracts/cash-movement";
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { Modal, Text, View } from "react-native";

import {
  cashMovementText,
  resolveCashMovementLocale,
  type CashMovementCopyKey,
  type CashMovementLocale,
} from "./cash-movement-copy";
import {
  CASH_IN_REASONS,
  CASH_OUT_REASONS,
  CASH_QUICK_AMOUNTS,
  formatCashMovementMoney,
  parseCashAmountText,
  type CashAmountKey,
  type CashMovementPresenter,
  type CashMovementRecordView,
  type CashMovementState,
  type CashMovementTab,
} from "./cash-movement-presenter";

import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSegmented } from "@/ui/controls/pos-segmented";
import { createPosStyles, posColors, posRadius, usePosThemeVersion } from "@/ui/theme";

export const CASH_MOVEMENT_MIN_TOUCH_TARGET = 44;

export type CashMovementScreenPresenter = Pick<
  CashMovementPresenter,
  | "cancelVoid"
  | "confirmVoid"
  | "dismissStatus"
  | "getState"
  | "load"
  | "pressKey"
  | "quickAmount"
  | "reprint"
  | "selectReason"
  | "setNote"
  | "setTab"
  | "setVoidNote"
  | "startVoid"
  | "submit"
  | "subscribe"
>;

type CashMovementScreenProps = Readonly<{
  presenter: CashMovementScreenPresenter;
  /** 主管授权弹窗出现时由路由临时隐藏本弹窗（状态都在 presenter 里，不会丢）。 */
  visible?: boolean;
  onClose(): void;
}>;

const KEYPAD_ROWS: readonly (readonly CashAmountKey[])[] = [
  ["1", "2", "3"],
  ["4", "5", "6"],
  ["7", "8", "9"],
  ["decimal", "0", "backspace"],
];

export function CashMovementScreen({
  onClose,
  presenter,
  visible = true,
}: CashMovementScreenProps) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { i18n } = useTranslation();
  const locale = resolveCashMovementLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = useCallback(
    (key: CashMovementCopyKey, values?: Readonly<Record<string, string | number>>) =>
      cashMovementText(locale, key, values),
    [locale],
  );

  useEffect(() => {
    // 打开即读取今日记录：顶部的今日合计与「被拒」徽标都靠它。
    void presenter.load();
  }, [presenter]);

  return (
    <Modal
      animationType="fade"
      onRequestClose={onClose}
      presentationStyle="fullScreen"
      statusBarTranslucent
      supportedOrientations={["landscape-left", "landscape-right"]}
      visible={visible}
    >
      <View style={styles.page} testID="cash-movement-screen">
        <View style={styles.header}>
          <View style={styles.headerText}>
            <Text accessibilityRole="header" style={styles.title}>
              {t("title")}
            </Text>
            <Text numberOfLines={1} style={styles.subtitle}>
              {t("totals.today", {
                in: formatCashMovementMoney(state.todayCashInCents),
                out: formatCashMovementMoney(state.todayCashOutCents),
              })}
            </Text>
          </View>
          <PosButton
            accessibilityLabel={t("action.close")}
            icon="close"
            label={t("action.close")}
            onPress={onClose}
            size="medium"
            testID="cash-movement-close"
            tone="secondary"
          />
        </View>
        <PosSegmented<CashMovementTab>
          accessibilityLabel={t("title")}
          onChange={(tab) => presenter.setTab(tab)}
          options={[
            { value: "in", label: t("tab.in"), testID: "cash-movement-tab-in" },
            { value: "out", label: t("tab.out"), testID: "cash-movement-tab-out" },
            {
              value: "records",
              label: t("tab.records"),
              testID: "cash-movement-tab-records",
              badge: state.rejectedCount,
            },
          ]}
          style={styles.tabs}
          testID="cash-movement-tabs"
          value={state.tab}
        />
        <StatusBanner state={state} t={t} />
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.bodyContent}
          style={styles.body}
          testID="cash-movement-scroll"
        >
          {state.tab === "records" ? (
            <RecordsPane locale={locale} presenter={presenter} state={state} t={t} />
          ) : (
            <MovementForm presenter={presenter} state={state} t={t} />
          )}
        </PosKeyboardAwareScrollView>
      </View>
    </Modal>
  );
}

type Translate = (
  key: CashMovementCopyKey,
  values?: Readonly<Record<string, string | number>>,
) => string;

function StatusBanner({ state, t }: Readonly<{ state: CashMovementState; t: Translate }>) {
  usePosThemeVersion();
  if (!state.statusCode) return null;
  const success =
    state.statusCode === "saved" ||
    state.statusCode === "void-saved" ||
    state.statusCode === "reprint-printed";
  const warned = success && state.hardwareWarnings.length > 0;
  const values = { number: state.statusNumber ?? "" };
  return (
    <View
      accessibilityLiveRegion="polite"
      style={[styles.status, success ? styles.statusSuccess : styles.statusError, warned && styles.statusWarn]}
      testID="cash-movement-status"
    >
      <Text style={styles.statusText}>{t(`status.${state.statusCode}`, values)}</Text>
      {state.hardwareWarnings.map((warning) => (
        <Text key={warning} style={styles.statusText} testID={`cash-movement-warning-${warning}`}>
          {t(`status.warning.${warning}`)}
        </Text>
      ))}
    </View>
  );
}

function MovementForm({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: CashMovementScreenPresenter;
  state: CashMovementState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const isIn = state.tab === "in";
  const reasons = isIn ? CASH_IN_REASONS : CASH_OUT_REASONS;
  const noteRequired = CASH_MOVEMENT_NOTE_REQUIRED_REASONS.includes(state.reasonCode);
  const cents = parseCashAmountText(state.amountText);
  const confirmLabel = state.busy
    ? t("confirm.working")
    : cents !== null && cents > 0
      ? t(isIn ? "confirm.in" : "confirm.out", { amount: formatCashMovementMoney(cents) })
      : t(isIn ? "confirm.empty.in" : "confirm.empty.out");
  return (
    <View style={styles.form} testID={`cash-movement-form-${state.tab}`}>
      <View style={styles.formLeft}>
        <Text style={styles.fieldLabel}>{t("field.reason")}</Text>
        {reasons.map((reason) => (
          <ReasonCard
            disabled={state.busy}
            key={reason}
            onPress={() => presenter.selectReason(reason)}
            reasonCode={reason}
            selected={state.reasonCode === reason}
            t={t}
          />
        ))}
        <Text style={styles.fieldLabel}>{t("field.note")}</Text>
        <PosKeyboardAwareTextInput
          accessibilityLabel={t("field.note")}
          editable={!state.busy}
          maxLength={CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH}
          onChangeText={(value) => presenter.setNote(value)}
          placeholder={t(noteRequired ? "note.required" : "note.optional")}
          placeholderTextColor={posColors.placeholder}
          style={styles.noteInput}
          testID="cash-movement-note"
          value={state.note}
        />
      </View>
      <View style={styles.formRight}>
        <Text style={styles.fieldLabel}>{t("field.amount")}</Text>
        <View style={styles.amountBox}>
          <Text
            accessibilityLabel={t("field.amount")}
            adjustsFontSizeToFit
            numberOfLines={1}
            style={[styles.amountText, state.amountText.length === 0 && styles.amountPlaceholder]}
            testID="cash-movement-amount"
          >
            {state.amountText.length === 0
              ? t("amount.placeholder")
              : `$${state.amountText}`}
          </Text>
        </View>
        <View style={styles.quickRow}>
          {CASH_QUICK_AMOUNTS.map((value) => (
            <PosPressable
              accessibilityLabel={t("amount.quick", { value })}
              accessibilityRole="button"
              disabled={state.busy}
              key={value}
              onPress={() => presenter.quickAmount(value)}
              sound="key"
              style={({ pressed }) => [styles.quickKey, pressed && styles.keyPressed]}
              testID={`cash-movement-quick-${value}`}
            >
              <Text style={styles.quickKeyText}>{t("amount.quick", { value })}</Text>
            </PosPressable>
          ))}
        </View>
        <View style={styles.keypad} testID="cash-movement-keypad">
          {KEYPAD_ROWS.map((row, index) => (
            <View key={index} style={styles.keypadRow}>
              {row.map((key) => (
                <PosPressable
                  accessibilityLabel={keyLabel(key, t)}
                  accessibilityRole="button"
                  disabled={state.busy}
                  key={key}
                  onPress={() => presenter.pressKey(key)}
                  sound="key"
                  style={({ pressed }) => [styles.key, pressed && styles.keyPressed]}
                  testID={`cash-movement-key-${key}`}
                >
                  <Text style={styles.keyText}>{keyText(key)}</Text>
                </PosPressable>
              ))}
            </View>
          ))}
          <PosPressable
            accessibilityLabel={t("keypad.clear")}
            accessibilityRole="button"
            disabled={state.busy}
            onPress={() => presenter.pressKey("clear")}
            sound="danger"
            style={({ pressed }) => [styles.key, styles.clearKey, pressed && styles.keyPressed]}
            testID="cash-movement-key-clear"
          >
            <Text style={styles.keyText}>{t("keypad.clear")}</Text>
          </PosPressable>
        </View>
        {state.validationCode ? (
          <Text
            accessibilityLiveRegion="polite"
            accessibilityRole="alert"
            style={styles.validation}
            testID="cash-movement-validation"
          >
            {t(validationKey(state.validationCode))}
          </Text>
        ) : null}
        <PosButton
          disabled={state.busy}
          label={confirmLabel}
          onPress={() => void presenter.submit()}
          size="large"
          testID="cash-movement-confirm"
          tone="primary"
        />
      </View>
    </View>
  );
}

function ReasonCard({
  disabled,
  onPress,
  reasonCode,
  selected,
  t,
}: Readonly<{
  disabled: boolean;
  onPress(): void;
  reasonCode: CashMovementReasonCode;
  selected: boolean;
  t: Translate;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={t(`reason.${reasonCode}`)}
      accessibilityRole="radio"
      accessibilityState={{ checked: selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      sound="tap"
      style={({ pressed }) => [
        styles.reasonCard,
        selected && styles.reasonCardSelected,
        pressed && styles.keyPressed,
      ]}
      testID={`cash-movement-reason-${reasonCode}`}
    >
      <Text style={styles.reasonTitle}>{t(`reason.${reasonCode}`)}</Text>
      <Text style={styles.reasonHint}>{t(`reasonHint.${reasonCode}`)}</Text>
    </PosPressable>
  );
}

function RecordsPane({
  locale,
  presenter,
  state,
  t,
}: Readonly<{
  locale: CashMovementLocale;
  presenter: CashMovementScreenPresenter;
  state: CashMovementState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const voidTarget = state.voidDraft
    ? state.records.find(
        (record) => record.movement.movementGuid === state.voidDraft?.originalMovementGuid,
      )
    : undefined;
  return (
    <View style={styles.records} testID="cash-movement-records">
      {state.rejectedCount > 0 ? (
        <View accessibilityRole="alert" style={styles.rejectedBanner} testID="cash-movement-rejected-banner">
          <Text style={styles.rejectedText}>
            {t("records.rejectedBanner", { count: state.rejectedCount })}
          </Text>
        </View>
      ) : null}
      {state.voidDraft && voidTarget ? (
        <View style={styles.voidPanel} testID="cash-movement-void-panel">
          <Text style={styles.voidTitle}>
            {t("void.title", { number: voidTarget.movement.displayNumber })}
          </Text>
          <Text style={styles.voidSummary}>
            {t("void.summary", {
              reason: t(`reason.${voidTarget.movement.reasonCode}`),
              amount: formatCashMovementMoney(voidTarget.movement.amountCents),
            })}
          </Text>
          <Text style={styles.fieldLabel}>{t("void.note")}</Text>
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("void.note")}
            editable={!state.busy}
            maxLength={CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH}
            onChangeText={(value) => presenter.setVoidNote(value)}
            placeholder={t("void.notePlaceholder")}
            placeholderTextColor={posColors.placeholder}
            style={styles.noteInput}
            testID="cash-movement-void-note"
            value={state.voidDraft.note}
          />
          {state.voidDraft.validationCode ? (
            <Text accessibilityRole="alert" style={styles.validation} testID="cash-movement-void-validation">
              {t(validationKey(state.voidDraft.validationCode))}
            </Text>
          ) : null}
          <View style={styles.voidActions}>
            <PosButton
              disabled={state.busy}
              label={t("void.cancel")}
              onPress={() => presenter.cancelVoid()}
              size="medium"
              testID="cash-movement-void-cancel"
              tone="quiet"
            />
            <PosButton
              disabled={state.busy}
              label={t("void.confirm")}
              onPress={() => void presenter.confirmVoid()}
              size="medium"
              testID="cash-movement-void-confirm"
              tone="danger"
            />
          </View>
        </View>
      ) : null}
      {state.loadState === "failed" ? (
        <View style={styles.emptyBox}>
          <Text style={styles.emptyText} testID="cash-movement-load-failed">
            {t("status.load-failed")}
          </Text>
          <PosButton
            label={t("records.retry")}
            onPress={() => void presenter.load()}
            size="medium"
            testID="cash-movement-reload"
            tone="secondary"
          />
        </View>
      ) : state.records.length === 0 ? (
        <View style={styles.emptyBox}>
          <Text style={styles.emptyText} testID="cash-movement-empty">
            {state.loadState === "ready" ? t("records.empty") : t("records.loading")}
          </Text>
        </View>
      ) : (
        <View>
          <RecordHeader t={t} />
          {state.records.map((record) => (
            <RecordRow
              busy={state.busy}
              key={record.movement.movementGuid}
              locale={locale}
              presenter={presenter}
              record={record}
              state={state}
              t={t}
            />
          ))}
        </View>
      )}
    </View>
  );
}

function RecordHeader({ t }: Readonly<{ t: Translate }>) {
  usePosThemeVersion();
  return (
    <View style={styles.rowHeader}>
      <Text style={[styles.headText, styles.colTime]}>{t("col.time")}</Text>
      <Text style={[styles.headText, styles.colNumber]}>{t("col.number")}</Text>
      <Text style={[styles.headText, styles.colType]}>{t("col.type")}</Text>
      <Text style={[styles.headText, styles.colReason]}>{t("col.reason")}</Text>
      <Text style={[styles.headText, styles.colAmount]}>{t("col.amount")}</Text>
      <Text style={[styles.headText, styles.colCashier]}>{t("col.cashier")}</Text>
      <Text style={[styles.headText, styles.colNote]}>{t("col.note")}</Text>
      <Text style={[styles.headText, styles.colUpload]}>{t("col.upload")}</Text>
      <Text style={[styles.headText, styles.colActions]}>{t("col.actions")}</Text>
    </View>
  );
}

function RecordRow({
  busy,
  locale,
  presenter,
  record,
  state,
  t,
}: Readonly<{
  busy: boolean;
  locale: CashMovementLocale;
  presenter: CashMovementScreenPresenter;
  record: CashMovementRecordView;
  state: CashMovementState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const { movement } = record;
  const guid = movement.movementGuid;
  const cashier =
    movement.authorizedByName !== null
      ? `${movement.cashierName} / ${movement.authorizedByName}`
      : movement.cashierName;
  const denied = record.voidEligibility.ok ? null : record.voidEligibility.reason;
  return (
    <View style={[styles.row, record.voided && styles.rowVoided]} testID={`cash-movement-row-${guid}`}>
      <Text style={[styles.cellText, styles.colTime]}>
        {formatRecordTime(movement.occurredAtIso, state.businessTimeZone, locale)}
      </Text>
      <Text style={[styles.cellText, styles.colNumber]}>{movement.displayNumber}</Text>
      <View style={styles.colType}>
        <Text style={styles.cellText}>{t(`type.${record.kind}`)}</Text>
        {record.voided ? (
          <Text style={styles.voidedTag} testID={`cash-movement-voided-${guid}`}>
            {t("record.voided")}
          </Text>
        ) : null}
      </View>
      <Text style={[styles.cellText, styles.colReason]}>{t(`reason.${movement.reasonCode}`)}</Text>
      <Text
        style={[styles.cellText, styles.colAmount, record.signedCents < 0 && styles.amountOut]}
        testID={`cash-movement-amount-${guid}`}
      >
        {`${record.signedCents < 0 ? "−" : "+"}${formatCashMovementMoney(Math.abs(record.signedCents))}`}
      </Text>
      <Text style={[styles.cellText, styles.colCashier]}>{cashier}</Text>
      <Text numberOfLines={2} style={[styles.cellText, styles.colNote]}>
        {movement.note ?? ""}
      </Text>
      <Text
        style={[
          styles.cellText,
          styles.colUpload,
          movement.uploadState === "rejected" && styles.uploadRejected,
        ]}
        testID={`cash-movement-upload-${guid}`}
      >
        {t(`upload.${movement.uploadState}`)}
      </Text>
      <View style={styles.colActions}>
        <PosButton
          disabled={busy}
          label={t("action.reprint")}
          onPress={() => void presenter.reprint(guid)}
          size="small"
          testID={`cash-movement-reprint-${guid}`}
          tone="quiet"
        />
        <PosButton
          disabled={busy || denied !== null}
          label={t("action.void")}
          onPress={() => presenter.startVoid(guid)}
          size="small"
          testID={`cash-movement-void-${guid}`}
          tone="danger"
        />
        {denied ? (
          <Text style={styles.deniedText} testID={`cash-movement-void-reason-${guid}`}>
            {t(`voidDenied.${denied}`)}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

/** 界面只展示操作员能自己改正的校验错误；其余（构造记录时的内部码）统一按保存失败提示。 */
function validationKey(code: string): CashMovementCopyKey {
  switch (code) {
    case "REASON_INVALID":
    case "AMOUNT_INVALID":
    case "AMOUNT_TOO_LARGE":
    case "NOTE_TOO_LONG":
    case "NOTE_REQUIRED":
    case "VOID_NOTE_REQUIRED":
      return `validation.${code}`;
    default:
      return "status.save-failed";
  }
}

function keyText(key: CashAmountKey): string {
  if (key === "decimal") return ".";
  if (key === "backspace") return "⌫";
  return key;
}

function keyLabel(key: CashAmountKey, t: Translate): string {
  if (key === "decimal") return t("keypad.decimal");
  if (key === "backspace") return t("keypad.backspace");
  return key;
}

/** 按门店营业时区显示 HH:mm，与营业日口径一致；时区不可用时退回设备时区。 */
function formatRecordTime(
  iso: string,
  timeZone: string | null,
  locale: CashMovementLocale,
): string {
  const date = new Date(iso);
  if (!Number.isFinite(date.getTime())) return "";
  try {
    return new Intl.DateTimeFormat(locale === "zh" ? "zh-CN" : "en-AU", {
      hour: "2-digit",
      hour12: false,
      minute: "2-digit",
      ...(timeZone ? { timeZone } : {}),
    }).format(date);
  } catch {
    return iso.slice(11, 16);
  }
}

const styles = createPosStyles((colors) => ({
  page: { backgroundColor: colors.canvas, flex: 1, gap: 12, paddingHorizontal: 20, paddingTop: 28, paddingBottom: 12 },
  header: { alignItems: "center", flexDirection: "row", gap: 12, justifyContent: "space-between" },
  headerText: { flex: 1, gap: 2 },
  title: { color: colors.ink, fontSize: 28, fontWeight: "900" },
  subtitle: { color: colors.mutedInk, fontSize: 15, fontWeight: "700" },
  tabs: { alignSelf: "stretch" },
  body: { flex: 1 },
  bodyContent: { gap: 12, paddingBottom: 24 },
  status: { borderRadius: posRadius.medium, borderWidth: 1, gap: 4, padding: 12 },
  statusSuccess: { backgroundColor: colors.greenSoft, borderColor: colors.greenBorder },
  statusWarn: { backgroundColor: colors.yellowSoft, borderColor: colors.yellowBorder },
  statusError: { backgroundColor: colors.redSoft, borderColor: colors.redBorder },
  statusText: { color: colors.ink, fontSize: 15, fontWeight: "800" },
  form: { flexDirection: "row", gap: 20 },
  formLeft: { flex: 1, gap: 10 },
  formRight: { flex: 1, gap: 10 },
  fieldLabel: { color: colors.mutedInk, fontSize: 14, fontWeight: "800" },
  reasonCard: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    gap: 2,
    minHeight: 72,
    justifyContent: "center",
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  reasonCardSelected: { backgroundColor: colors.selectedSurface, borderColor: colors.accent, borderWidth: 2 },
  reasonTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  reasonHint: { color: colors.mutedInk, fontSize: 14, fontWeight: "600" },
  noteInput: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    color: colors.ink,
    fontSize: 18,
    minHeight: 56,
    paddingHorizontal: 14,
  },
  amountBox: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 2,
    justifyContent: "center",
    minHeight: 72,
    paddingHorizontal: 16,
  },
  amountText: { color: colors.ink, fontSize: 40, fontWeight: "900", textAlign: "right" },
  amountPlaceholder: { color: colors.placeholder },
  quickRow: { flexDirection: "row", gap: 8 },
  quickKey: {
    alignItems: "center",
    backgroundColor: colors.accentSoft,
    borderColor: colors.accent,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minHeight: 52,
  },
  quickKeyText: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  keypad: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  keypadRow: { flexDirection: "row", gap: 8, width: "100%" },
  key: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minHeight: 60,
  },
  clearKey: { flexBasis: "100%", minHeight: 48 },
  keyPressed: { opacity: 0.7 },
  keyText: { color: colors.ink, fontSize: 24, fontWeight: "800" },
  validation: { color: colors.red, fontSize: 15, fontWeight: "800" },
  records: { gap: 10 },
  rejectedBanner: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderRadius: posRadius.medium,
    borderWidth: 2,
    padding: 14,
  },
  rejectedText: { color: colors.red, fontSize: 17, fontWeight: "900" },
  voidPanel: {
    backgroundColor: colors.surface,
    borderColor: colors.redBorder,
    borderRadius: posRadius.medium,
    borderWidth: 2,
    gap: 8,
    padding: 14,
  },
  voidTitle: { color: colors.ink, fontSize: 20, fontWeight: "900" },
  voidSummary: { color: colors.mutedInk, fontSize: 15, fontWeight: "700" },
  voidActions: { flexDirection: "row", gap: 10, justifyContent: "flex-end" },
  emptyBox: { alignItems: "center", gap: 12, padding: 32 },
  emptyText: { color: colors.mutedInk, fontSize: 17, fontWeight: "700" },
  rowHeader: {
    backgroundColor: colors.surfaceAlt,
    borderRadius: posRadius.small,
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 10,
    paddingVertical: 8,
  },
  headText: { color: colors.mutedInk, fontSize: 13, fontWeight: "800" },
  row: {
    alignItems: "center",
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 8,
    minHeight: 64,
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  rowVoided: { opacity: 0.6 },
  cellText: { color: colors.ink, fontSize: 15, fontWeight: "700" },
  amountOut: { color: colors.red },
  uploadRejected: { color: colors.red, fontWeight: "900" },
  voidedTag: { color: colors.mutedInk, fontSize: 12, fontWeight: "800" },
  deniedText: { color: colors.mutedInk, fontSize: 12, fontWeight: "600" },
  colTime: { width: 56 },
  colNumber: { width: 80 },
  colType: { width: 84 },
  colReason: { width: 130 },
  colAmount: { textAlign: "right", width: 96 },
  colCashier: { width: 110 },
  colNote: { flex: 1 },
  colUpload: { width: 84 },
  colActions: { gap: 4, width: 150 },
}));
