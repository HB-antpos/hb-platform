import {
  CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH,
  type CashMovementReasonCode,
} from "@hb/pos-domain/core/contracts/cash-movement";
import { useEffect, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { Modal, StyleSheet, Text, View } from "react-native";

import {
  CASH_MOVEMENT_QUICK_AMOUNTS_CENTS,
  formatCents,
  type CashMovementKeypadKey,
} from "./cash-movement-amount";
import {
  cashMovementText,
  resolveCashMovementLocale,
  type CashMovementCopyKey,
  type CashMovementLocale,
} from "./cash-movement-copy";
import {
  CASH_MOVEMENT_REASONS_BY_TAB,
  type CashMovementPresenter,
  type CashMovementRecordView,
  type CashMovementState,
  type CashMovementStatusCode,
  type CashMovementTab,
} from "./cash-movement-presenter";

import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { HandheldActionButton } from "@/ui/handheld/handheld-actions";
import { posColors } from "@/ui/theme";

export const CASH_MOVEMENT_MIN_TOUCH_TARGET = 48;

export type CashMovementScreenPresenter = Pick<
  CashMovementPresenter,
  | "beginVoid"
  | "cancelVoid"
  | "confirmVoid"
  | "getState"
  | "load"
  | "pressAmountKey"
  | "reprint"
  | "selectReason"
  | "setNote"
  | "setQuickAmount"
  | "setTab"
  | "setVoidNote"
  | "submit"
  | "subscribe"
>;

type CashMovementScreenProps = Readonly<{
  presenter: CashMovementScreenPresenter;
  onClose(): void;
  /** 缺省按 i18n 当前语言。 */
  locale?: CashMovementLocale;
}>;

const KEYPAD_ROWS: readonly (readonly CashMovementKeypadKey[])[] = [
  ["1", "2", "3"],
  ["4", "5", "6"],
  ["7", "8", "9"],
  [".", "0", "00"],
];

const SUCCESS_STATUS: ReadonlySet<CashMovementStatusCode> = new Set([
  "saved",
  "void-saved",
  "reprint-printed",
]);

/** 钱箱弹窗：三个页签「存入 / 取出 / 今日记录」，小屏单列，不出现横向溢出。 */
export function CashMovementScreen({
  presenter,
  onClose,
  locale: localeOverride,
}: CashMovementScreenProps) {
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { i18n } = useTranslation();
  useEffect(() => {
    void presenter.load();
  }, [presenter]);
  const locale =
    localeOverride ?? resolveCashMovementLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = (
    key: CashMovementCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => cashMovementText(locale, key, values);

  const tabs: readonly (readonly [CashMovementTab, CashMovementCopyKey])[] = [
    ["in", "tab.in"],
    ["out", "tab.out"],
    ["records", "tab.records"],
  ];

  return (
    <Modal
      animationType="slide"
      onRequestClose={onClose}
      presentationStyle="fullScreen"
      statusBarTranslucent
      supportedOrientations={["portrait"]}
      visible
    >
      <View style={styles.page} testID="cash-movement-screen">
        <View style={styles.header}>
          <View style={styles.headerText}>
            <Text style={styles.eyebrow}>{t("eyebrow")}</Text>
            <Text accessibilityRole="header" style={styles.title}>
              {t("title")}
            </Text>
          </View>
          <PosPressable
            accessibilityLabel={t("action.close")}
            accessibilityRole="button"
            onPress={onClose}
            sound="navigate"
            style={styles.closeButton}
            testID="cash-movement-close"
          >
            <Text style={styles.closeLabel}>{t("action.close")}</Text>
          </PosPressable>
        </View>

        <View accessibilityRole="tablist" style={styles.tabs}>
          {tabs.map(([tab, key]) => {
            const selected = state.tab === tab;
            return (
              <PosPressable
                accessibilityLabel={t(key)}
                accessibilityRole="tab"
                accessibilityState={{ disabled: state.busy, selected }}
                disabled={state.busy}
                key={tab}
                onPress={() => presenter.setTab(tab)}
                sound="navigate"
                style={[styles.tab, selected && styles.tabSelected]}
                testID={`cash-movement-tab-${tab}`}
              >
                <Text style={[styles.tabLabel, selected && styles.tabLabelSelected]}>
                  {t(key)}
                </Text>
              </PosPressable>
            );
          })}
        </View>

        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.content}
          style={styles.scroll}
          testID="cash-movement-scroll"
        >
          {state.statusCode ? (
            <StatusBanner locale={locale} statusCode={state.statusCode} />
          ) : null}
          {state.tab === "records" ? (
            <RecordsPane presenter={presenter} state={state} t={t} />
          ) : (
            <FormPane
              presenter={presenter}
              state={state}
              t={t}
              tab={state.tab}
            />
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

function StatusBanner({
  locale,
  statusCode,
}: Readonly<{ locale: CashMovementLocale; statusCode: CashMovementStatusCode }>) {
  const success = SUCCESS_STATUS.has(statusCode);
  // 「已保存但开箱 / 打印失败」是要提醒的警告，不是错误也不是纯成功。
  const warning = statusCode.startsWith("saved-") || statusCode.startsWith("void-saved-");
  return (
    <View
      accessibilityLiveRegion="polite"
      accessibilityRole="alert"
      style={[
        styles.status,
        success ? styles.statusSuccess : warning ? styles.statusWarning : styles.statusError,
      ]}
      testID="cash-movement-status"
    >
      <Text
        style={[
          styles.statusText,
          success
            ? styles.statusTextSuccess
            : warning
              ? styles.statusTextWarning
              : styles.statusTextError,
        ]}
      >
        {cashMovementText(locale, `status.${statusCode}`)}
      </Text>
    </View>
  );
}

function FormPane({
  presenter,
  state,
  t,
  tab,
}: Readonly<{
  presenter: CashMovementScreenPresenter;
  state: CashMovementState;
  t: Translate;
  tab: "in" | "out";
}>) {
  const noteRequired = state.reasonCode === "OtherIn" || state.reasonCode === "PaidOut";
  const amountLabel = formatCents(state.amountCents);
  return (
    <View style={styles.form}>
      <Text style={styles.sectionLabel}>{t("form.reason")}</Text>
      <View style={styles.reasons}>
        {CASH_MOVEMENT_REASONS_BY_TAB[tab].map((reason) => (
          <ReasonOption
            disabled={state.busy}
            key={reason}
            onPress={() => presenter.selectReason(reason)}
            reason={reason}
            selected={state.reasonCode === reason}
            t={t}
          />
        ))}
      </View>

      <Text style={styles.sectionLabel}>{t("form.amount")}</Text>
      <View style={styles.amountBox}>
        <Text
          accessibilityLabel={`${t("form.amount")} ${amountLabel}`}
          style={styles.amountText}
          testID="cash-movement-amount"
        >
          {state.amountText === "" ? "$0.00" : `$${state.amountText}`}
        </Text>
      </View>

      <View accessibilityLabel={t("form.quickAmounts")} style={styles.quickRow}>
        {CASH_MOVEMENT_QUICK_AMOUNTS_CENTS.map((cents) => (
          <PosPressable
            accessibilityRole="button"
            disabled={state.busy}
            key={cents}
            onPress={() => presenter.setQuickAmount(cents)}
            sound="key"
            style={styles.quickKey}
            testID={`cash-movement-quick-${cents / 100}`}
          >
            <Text style={styles.quickLabel}>{`$${cents / 100}`}</Text>
          </PosPressable>
        ))}
      </View>

      <View style={styles.keypad}>
        {KEYPAD_ROWS.map((row) => (
          <View key={row.join("")} style={styles.keypadRow}>
            {row.map((key) => (
              <PosPressable
                accessibilityLabel={key}
                accessibilityRole="button"
                disabled={state.busy}
                key={key}
                onPress={() => presenter.pressAmountKey(key)}
                sound="key"
                style={styles.key}
                testID={`cash-movement-key-${key === "." ? "dot" : key}`}
              >
                <Text style={styles.keyLabel}>{key}</Text>
              </PosPressable>
            ))}
          </View>
        ))}
        <View style={styles.keypadRow}>
          <PosPressable
            accessibilityLabel={t("action.backspace")}
            accessibilityRole="button"
            disabled={state.busy}
            onPress={() => presenter.pressAmountKey("backspace")}
            sound="key"
            style={[styles.key, styles.keySecondary]}
            testID="cash-movement-key-backspace"
          >
            <Text style={styles.keyLabelSecondary}>{t("action.backspace")}</Text>
          </PosPressable>
          <PosPressable
            accessibilityLabel={t("action.clear")}
            accessibilityRole="button"
            disabled={state.busy}
            onPress={() => presenter.pressAmountKey("clear")}
            sound="key"
            style={[styles.key, styles.keySecondary]}
            testID="cash-movement-key-clear"
          >
            <Text style={styles.keyLabelSecondary}>{t("action.clear")}</Text>
          </PosPressable>
        </View>
      </View>

      <View style={styles.noteHeader}>
        <Text style={styles.sectionLabel}>
          {noteRequired ? t("form.noteRequiredLabel") : t("form.noteOptional")}
        </Text>
        <Text style={styles.noteCounter}>
          {t("form.noteCounter", {
            count: state.note.length,
            max: CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH,
          })}
        </Text>
      </View>
      <PosKeyboardAwareTextInput
        accessibilityLabel={t("form.note")}
        editable={!state.busy}
        maxLength={CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH}
        multiline
        onChangeText={(text) => presenter.setNote(text)}
        placeholder={t("form.notePlaceholder")}
        style={styles.noteInput}
        testID="cash-movement-note"
        value={state.note}
      />

      <HandheldActionButton
        disabled={state.busy}
        label={
          state.busy
            ? t("action.working")
            : t(tab === "in" ? "action.confirmIn" : "action.confirmOut", {
                amount: amountLabel,
              })
        }
        onPress={() => void presenter.submit()}
        testID="cash-movement-submit"
      />
    </View>
  );
}

function ReasonOption({
  disabled,
  onPress,
  reason,
  selected,
  t,
}: Readonly<{
  disabled: boolean;
  onPress(): void;
  reason: CashMovementReasonCode;
  selected: boolean;
  t: Translate;
}>) {
  return (
    <PosPressable
      accessibilityLabel={t(`reason.${reason}`)}
      accessibilityRole="radio"
      accessibilityState={{ checked: selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      sound="tap"
      style={[styles.reason, selected && styles.reasonSelected]}
      testID={`cash-movement-reason-${reason}`}
    >
      <Text style={[styles.reasonTitle, selected && styles.reasonTitleSelected]}>
        {t(`reason.${reason}`)}
      </Text>
      <Text style={styles.reasonHint}>{t(`reasonHint.${reason}`)}</Text>
    </PosPressable>
  );
}

function RecordsPane({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: CashMovementScreenPresenter;
  state: CashMovementState;
  t: Translate;
}>) {
  return (
    <View style={styles.form}>
      {state.rejectedCount > 0 ? (
        <View accessibilityRole="alert" style={styles.rejectedBanner} testID="cash-movement-rejected-banner">
          <Text style={styles.rejectedText}>
            {t("records.rejectedBanner", { count: state.rejectedCount })}
          </Text>
        </View>
      ) : null}
      {state.summary ? (
        <Text style={styles.summaryLine} testID="cash-movement-summary">
          {t("records.summary", {
            in: formatCents(state.summary.cashInCents),
            out: formatCents(state.summary.cashOutCents),
          })}
        </Text>
      ) : null}
      {state.kind === "loading" && state.records.length === 0 ? (
        <Text style={styles.emptyText}>{t("records.loading")}</Text>
      ) : state.records.length === 0 ? (
        <Text style={styles.emptyText} testID="cash-movement-empty">
          {t("records.empty")}
        </Text>
      ) : (
        state.records.map((record) => (
          <RecordCard
            busy={state.busy}
            key={record.movement.movementGuid}
            presenter={presenter}
            record={record}
            state={state}
            t={t}
          />
        ))
      )}
    </View>
  );
}

function RecordCard({
  busy,
  presenter,
  record,
  state,
  t,
}: Readonly<{
  busy: boolean;
  presenter: CashMovementScreenPresenter;
  record: CashMovementRecordView;
  state: CashMovementState;
  t: Translate;
}>) {
  const { movement } = record;
  const guid = movement.movementGuid;
  const signed = `${movement.direction === "In" ? "+" : "-"}${formatCents(movement.amountCents)}`;
  const voiding = state.voidDraft?.movementGuid === guid;
  const eligibility = record.voidEligibility;
  const canVoid = eligibility.ok;
  const rejected = movement.uploadState === "rejected";
  return (
    <View
      style={[styles.card, rejected && styles.cardRejected]}
      testID={`cash-movement-record-${guid}`}
    >
      <View style={styles.cardTop}>
        <Text style={styles.cardTitle}>
          {t(`direction.${record.kind}`)} · {t(`reason.${movement.reasonCode}`)}
        </Text>
        <Text
          style={[styles.cardAmount, movement.direction === "In" ? styles.amountIn : styles.amountOut]}
        >
          {signed}
        </Text>
      </View>
      <Text style={styles.cardMeta}>
        {formatTime(movement.occurredAtIso, state.businessTimeZone)} · {movement.displayNumber}
      </Text>
      {movement.voidsMovementGuid !== null ? (
        <Text style={styles.cardMeta}>
          {t("record.voids", {
            number:
              state.records.find((other) => other.movement.movementGuid === movement.voidsMovementGuid)
                ?.movement.displayNumber ?? "",
          })}
        </Text>
      ) : null}
      <Text style={styles.cardMeta}>
        {movement.authorizedByName
          ? t("record.cashierAuthorized", {
              cashier: movement.cashierName,
              authorizer: movement.authorizedByName,
            })
          : t("record.cashier", { value: movement.cashierName })}
      </Text>
      {movement.note ? (
        <Text style={styles.cardNote}>{t("record.note", { value: movement.note })}</Text>
      ) : null}
      <View style={styles.cardBadges}>
        <Text
          style={[styles.badge, rejected ? styles.badgeRejected : styles.badgeNeutral]}
          testID={`cash-movement-upload-${guid}`}
        >
          {t(`upload.${movement.uploadState}`)}
        </Text>
        {record.isVoided ? (
          <Text style={[styles.badge, styles.badgeNeutral]}>{t("record.voided")}</Text>
        ) : null}
      </View>

      {voiding ? (
        <View style={styles.voidForm}>
          <Text style={styles.sectionLabel}>
            {t("void.title", { number: movement.displayNumber })}
          </Text>
          <Text style={styles.cardMeta}>{t("void.hint")}</Text>
          <PosKeyboardAwareTextInput
            accessibilityLabel={t("void.notePlaceholder")}
            editable={!busy}
            maxLength={CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH}
            multiline
            onChangeText={(text) => presenter.setVoidNote(text)}
            placeholder={t("void.notePlaceholder")}
            style={styles.noteInput}
            testID="cash-movement-void-note"
            value={state.voidDraft?.note ?? ""}
          />
          <HandheldActionButton
            disabled={busy}
            label={busy ? t("action.working") : t("void.confirm")}
            onPress={() => void presenter.confirmVoid()}
            testID="cash-movement-void-confirm"
            variant="danger"
          />
          <HandheldActionButton
            disabled={busy}
            label={t("void.cancel")}
            onPress={() => presenter.cancelVoid()}
            sound="navigate"
            testID="cash-movement-void-cancel"
            variant="secondary"
          />
        </View>
      ) : (
        <View style={styles.cardActions}>
          <PosPressable
            accessibilityRole="button"
            accessibilityState={{ disabled: busy }}
            disabled={busy}
            onPress={() => void presenter.reprint(guid)}
            sound="tap"
            style={styles.cardButton}
            testID={`cash-movement-reprint-${guid}`}
          >
            <Text style={styles.cardButtonLabel}>{t("action.reprint")}</Text>
          </PosPressable>
          {/* 冲正记录本身不能再冲正，不显示按钮；其余记录不满足资格时禁用并写明原因。 */}
          {record.kind !== "void" ? (
            <PosPressable
              accessibilityRole="button"
              accessibilityState={{ disabled: busy || !canVoid }}
              disabled={busy || !canVoid}
              onPress={() => presenter.beginVoid(guid)}
              sound="tap"
              style={[styles.cardButton, (busy || !canVoid) && styles.cardButtonDisabled]}
              testID={`cash-movement-void-${guid}`}
            >
              <Text style={styles.cardButtonLabel}>{t("action.void")}</Text>
            </PosPressable>
          ) : null}
        </View>
      )}
      {!voiding && record.kind !== "void" && !eligibility.ok && eligibility.reason !== "ALREADY_VOIDED" ? (
        <Text style={styles.cardMeta} testID={`cash-movement-void-reason-${guid}`}>
          {t(`voidDenied.${eligibility.reason}`)}
        </Text>
      ) : null}
    </View>
  );
}

/** HH:mm（门店营业时区）；时区未知或无法格式化时退回 ISO 的时分，保证不抛错。 */
function formatTime(iso: string, timeZone: string | null): string {
  try {
    return new Intl.DateTimeFormat("en-GB", {
      hour: "2-digit",
      hourCycle: "h23",
      minute: "2-digit",
      ...(timeZone ? { timeZone } : {}),
    }).format(new Date(iso));
  } catch {
    return iso.slice(11, 16);
  }
}

const styles = StyleSheet.create({
  page: { backgroundColor: posColors.canvas, flex: 1 },
  header: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 12,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  headerText: { flex: 1 },
  eyebrow: { color: posColors.orange, fontSize: 12, fontWeight: "900", letterSpacing: 1.2 },
  title: { color: posColors.ink, fontSize: 22, fontWeight: "800" },
  closeButton: {
    alignItems: "center",
    borderColor: posColors.border,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: CASH_MOVEMENT_MIN_TOUCH_TARGET,
    paddingHorizontal: 16,
  },
  closeLabel: { color: posColors.ink, fontSize: 15, fontWeight: "800" },
  tabs: {
    backgroundColor: posColors.surface,
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
  },
  tab: {
    alignItems: "center",
    borderBottomColor: "transparent",
    borderBottomWidth: 3,
    flex: 1,
    justifyContent: "center",
    minHeight: CASH_MOVEMENT_MIN_TOUCH_TARGET,
  },
  tabSelected: { borderBottomColor: posColors.orange },
  tabLabel: { color: posColors.mutedInk, fontSize: 15, fontWeight: "700" },
  tabLabelSelected: { color: posColors.ink, fontWeight: "900" },
  scroll: { flex: 1 },
  content: { gap: 12, padding: 16, paddingBottom: 32 },
  form: { gap: 12 },
  sectionLabel: { color: posColors.ink, fontSize: 14, fontWeight: "800" },
  reasons: { gap: 8 },
  reason: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderWidth: 1,
    gap: 4,
    minHeight: CASH_MOVEMENT_MIN_TOUCH_TARGET,
    padding: 12,
  },
  reasonSelected: { backgroundColor: posColors.orangeSoft, borderColor: posColors.orange },
  reasonTitle: { color: posColors.ink, fontSize: 16, fontWeight: "800" },
  reasonTitleSelected: { color: posColors.orange },
  reasonHint: { color: posColors.mutedInk, fontSize: 13, lineHeight: 18 },
  amountBox: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderWidth: 1,
    paddingHorizontal: 16,
    paddingVertical: 10,
  },
  amountText: { color: posColors.ink, fontSize: 32, fontWeight: "900", textAlign: "right" },
  quickRow: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  quickKey: {
    alignItems: "center",
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.border,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: CASH_MOVEMENT_MIN_TOUCH_TARGET,
    minWidth: 56,
    paddingHorizontal: 12,
  },
  quickLabel: { color: posColors.blue, fontSize: 15, fontWeight: "800" },
  keypad: { gap: 8 },
  keypadRow: { flexDirection: "row", gap: 8 },
  key: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minHeight: CASH_MOVEMENT_MIN_TOUCH_TARGET,
  },
  keySecondary: { backgroundColor: posColors.canvas },
  keyLabel: { color: posColors.ink, fontSize: 22, fontWeight: "800" },
  keyLabelSecondary: { color: posColors.mutedInk, fontSize: 15, fontWeight: "800" },
  noteHeader: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  noteCounter: { color: posColors.mutedInk, fontSize: 12 },
  noteInput: {
    backgroundColor: "#FFFDF8",
    borderColor: posColors.border,
    borderWidth: 1,
    color: posColors.ink,
    fontSize: 16,
    minHeight: 72,
    paddingHorizontal: 12,
    paddingVertical: 8,
    textAlignVertical: "top",
  },
  status: { borderLeftWidth: 3, padding: 12 },
  statusSuccess: { backgroundColor: posColors.greenSoft, borderLeftColor: posColors.green },
  statusWarning: { backgroundColor: posColors.yellowSoft, borderLeftColor: posColors.yellow },
  statusError: { backgroundColor: posColors.redSoft, borderLeftColor: posColors.red },
  statusText: { fontSize: 15, fontWeight: "700", lineHeight: 21 },
  statusTextSuccess: { color: posColors.green },
  statusTextWarning: { color: posColors.yellow },
  statusTextError: { color: posColors.red },
  rejectedBanner: {
    backgroundColor: posColors.redSoft,
    borderLeftColor: posColors.red,
    borderLeftWidth: 4,
    padding: 12,
  },
  rejectedText: { color: posColors.red, fontSize: 15, fontWeight: "800", lineHeight: 21 },
  summaryLine: { color: posColors.ink, fontSize: 15, fontWeight: "800" },
  emptyText: { color: posColors.mutedInk, fontSize: 15, paddingVertical: 24, textAlign: "center" },
  card: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderWidth: 1,
    gap: 4,
    padding: 12,
  },
  cardRejected: { borderColor: posColors.red, borderWidth: 2 },
  cardTop: { alignItems: "flex-start", flexDirection: "row", gap: 8, justifyContent: "space-between" },
  cardTitle: { color: posColors.ink, flex: 1, fontSize: 16, fontWeight: "800" },
  cardAmount: { fontSize: 16, fontWeight: "900" },
  amountIn: { color: posColors.green },
  amountOut: { color: posColors.red },
  cardMeta: { color: posColors.mutedInk, fontSize: 13, lineHeight: 18 },
  cardNote: { color: posColors.ink, fontSize: 14, lineHeight: 20 },
  cardBadges: { flexDirection: "row", flexWrap: "wrap", gap: 6, marginTop: 4 },
  badge: { fontSize: 12, fontWeight: "800", paddingHorizontal: 8, paddingVertical: 3 },
  badgeNeutral: { backgroundColor: posColors.blueSoft, color: posColors.blue },
  badgeRejected: { backgroundColor: posColors.red, color: "#FFFFFF" },
  cardActions: { flexDirection: "row", gap: 8, marginTop: 8 },
  cardButton: {
    alignItems: "center",
    borderColor: posColors.border,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minHeight: CASH_MOVEMENT_MIN_TOUCH_TARGET,
  },
  cardButtonDisabled: { opacity: 0.45 },
  cardButtonLabel: { color: posColors.ink, fontSize: 15, fontWeight: "800" },
  voidForm: { gap: 8, marginTop: 8 },
});
