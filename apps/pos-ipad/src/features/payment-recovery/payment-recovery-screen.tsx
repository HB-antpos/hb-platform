import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  paymentRecoveryText,
  resolvePaymentRecoveryLocale,
  type PaymentRecoveryCopyKey,
  type PaymentRecoveryLocale,
} from "./payment-recovery-copy";
import {
  EMPTY_MANUAL_VERIFICATION_DRAFT,
  validateManualVerification,
  normalizeVerificationNote,
  type ManualVerificationDraft,
} from "./payment-recovery-presenter";
import type {
  ManualPaymentFinding,
  PaymentRecoveryCenterService,
  PaymentRecoveryEvent,
  PaymentRecoveryFilter,
  PaymentRecoveryRecord,
  PaymentRecoveryStatus,
} from "./payment-recovery-types";

import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { posColors } from "@/ui/theme";

export const PAYMENT_RECOVERY_MIN_TOUCH_TARGET = 44;
const COMPACT_BREAKPOINT = 900;

export type PaymentRecoveryScreenProps = Readonly<{
  service: PaymentRecoveryCenterService;
  onBack?(): void;
}>;

export function PaymentRecoveryScreen({
  service,
  onBack,
}: PaymentRecoveryScreenProps) {
  const state = useSyncExternalStore(service.subscribe, service.getState, service.getState);
  const { width } = useWindowDimensions();
  const compact = width < COMPACT_BREAKPOINT;
  const { i18n } = useTranslation();
  const locale = resolvePaymentRecoveryLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = useCallback(
    (key: PaymentRecoveryCopyKey, values?: Readonly<Record<string, string | number>>) =>
      paymentRecoveryText(locale, key, values),
    [locale],
  );
  const [keyword, setKeyword] = useState(state.keyword);
  const [manualRecord, setManualRecord] = useState<PaymentRecoveryRecord | null>(null);
  const [manualVisible, setManualVisible] = useState(false);

  useEffect(() => {
    void service.refresh();
  }, [service]);

  const visibleRecords = useMemo(
    () => state.records.filter((record) =>
      recordMatchesFilter(record, state.filter) && recordMatchesKeyword(record, state.keyword)),
    [state.filter, state.keyword, state.records],
  );
  const selected = useMemo(
    () => visibleRecords.find((record) => record.id === state.selectedRecordId) ?? visibleRecords[0] ?? null,
    [state.selectedRecordId, visibleRecords],
  );

  const applySearch = () => {
    service.setKeyword(keyword.trim());
    void service.refresh();
  };

  const selectFilter = (filter: PaymentRecoveryFilter) => {
    service.setFilter(filter);
    void service.refresh();
  };

  return (
    <SafeAreaView style={styles.safeArea} testID="payment-recovery-screen">
      <View style={styles.header}>
        <View style={styles.headerCopy}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text style={styles.subtitle}>{t("subtitle")}</Text>
        </View>
        <ActionButton
          label={t("action.back")}
          onPress={() => onBack?.()}
          testID="payment-recovery-back-to-sale"
          tone="secondary"
          disabled={state.action !== "idle"}
        />
      </View>

      <View style={[styles.workspace, compact && styles.workspaceCompact]}>
        <View style={[styles.listPane, compact && styles.listPaneCompact]} testID="payment-recovery-list-pane">
          <View style={styles.tabs}>
            {(["pending", "failed", "resolved"] as const).map((filter) => (
              <FilterTab
                key={filter}
                active={state.filter === filter}
                label={t(`filter.${filter}`)}
                onPress={() => selectFilter(filter)}
                testID={`payment-recovery-filter-${filter}`}
              />
            ))}
          </View>
          {/* 搜索框需置于键盘感知滚动容器内，软键盘弹出时才不会遮挡输入。 */}
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.searchRow}
            keyboardShouldPersistTaps="handled"
            scrollEnabled={false}
            style={styles.searchScroll}
          >
            <PosKeyboardAwareTextInput
              accessibilityLabel={t("search.label")}
              autoCapitalize="none"
              onChangeText={setKeyword}
              onSubmitEditing={applySearch}
              placeholder={t("search.placeholder")}
              returnKeyType="search"
              style={styles.searchInput}
              testID="payment-recovery-search"
              value={keyword}
            />
            <ActionButton
              compact
              disabled={state.refreshing}
              label={state.refreshing ? t("action.refreshing") : t("action.refresh")}
              onPress={applySearch}
              testID="payment-recovery-refresh"
              tone="quiet"
            />
          </PosKeyboardAwareScrollView>
          <Text style={styles.sectionTitle}>{t("list.title")}</Text>
          <ScrollView contentContainerStyle={styles.recordList} testID="payment-recovery-record-list">
            {state.loading ? (
              <CenteredState loading message={t("list.loading")} />
            ) : visibleRecords.length === 0 ? (
              <CenteredState message={state.errorCode ? errorText(t, state.errorCode) : t("list.empty")} />
            ) : (
              visibleRecords.map((record) => (
                <RecoveryRecordRow
                  key={record.id}
                  active={record.id === selected?.id}
                  locale={locale}
                  onPress={() => service.selectRecord(record.id)}
                  record={record}
                />
              ))
            )}
          </ScrollView>
        </View>

        <View style={styles.detailsPane} testID="payment-recovery-details-pane">
          {selected ? (
            <RecoveryDetails
              action={state.action}
              locale={locale}
              onManual={() => {
                setManualRecord(selected);
                setManualVisible(true);
              }}
              onRecover={() => void service.recoverOriginalPayment(selected.id).catch(() => undefined)}
              record={selected}
            />
          ) : (
            <CenteredState message={t("details.select")} />
          )}
          {state.errorCode && !state.loading ? (
            <Text accessibilityRole="alert" style={styles.actionError}>
            {errorText(t, state.errorCode)}
            </Text>
          ) : null}
        </View>
      </View>

      <ManualVerificationModal
        action={state.action}
        locale={locale}
        onClose={() => {
          setManualVisible(false);
          setManualRecord(null);
        }}
        onSubmit={async (draft, record) => {
          const validation = validateManualVerification(record, draft);
          if (!draft.finding || !validation.valid) return;
          // 表单使用页面内覆盖层，主管验证使用全局原生 Modal，避免 iOS 双 Modal 切换竞态。
          setManualVisible(false);
          try {
            await service.submitManualVerification({
              recordId: record.id,
              finding: draft.finding,
              verifiedAmountCents: validation.amountCents,
              evidenceReference: draft.evidenceReference.trim(),
              note: normalizeVerificationNote(draft.note),
              ...(draft.finding === "unpaid" && draft.terminalChargeAcknowledged
                ? { terminalChargeAcknowledged: true }
                : {}),
            });
            setManualRecord(null);
          } catch (error) {
            // 主管取消或授权失败时恢复原草稿，避免操作人重复录入核实凭证。
            setManualVisible(true);
            throw error;
          }
        }}
        record={manualRecord}
        visible={manualVisible}
      />
    </SafeAreaView>
  );
}

function RecoveryRecordRow({
  active,
  locale,
  onPress,
  record,
}: Readonly<{
  active: boolean;
  locale: PaymentRecoveryLocale;
  onPress(): void;
  record: PaymentRecoveryRecord;
}>) {
  const t = (key: PaymentRecoveryCopyKey) => paymentRecoveryText(locale, key);
  return (
    <PosPressable
      accessibilityLabel={`${t(statusKey(record.status))}, ${formatAud(record.amountCents)}, ${record.orderGuid}`}
      accessibilityRole="button"
      accessibilityState={{ selected: active }}
      onPress={onPress}
      sound="navigate"
      style={[styles.recordRow, active && styles.recordRowActive]}
      testID={`payment-recovery-record-${record.id}`}
    >
      <View style={styles.recordTopLine}>
        <StatusBadge locale={locale} status={record.status} />
        <Text style={styles.recordAmount}>{formatAud(record.amountCents)}</Text>
      </View>
      <Text numberOfLines={1} style={styles.recordOrder}>{record.orderGuid}</Text>
      <Text numberOfLines={1} style={styles.recordMeta}>
        {formatDateTime(record.occurredAtIso, locale)} · {record.terminalName ?? t("details.unavailable")}
      </Text>
    </PosPressable>
  );
}

function RecoveryDetails({
  action,
  locale,
  onManual,
  onRecover,
  record,
}: Readonly<{
  action: "idle" | "recovering" | "manual-verifying";
  locale: PaymentRecoveryLocale;
  onManual(): void;
  onRecover(): void;
  record: PaymentRecoveryRecord;
}>) {
  const t = (key: PaymentRecoveryCopyKey, values?: Readonly<Record<string, string | number>>) =>
    paymentRecoveryText(locale, key, values);
  const resolved = isResolved(record.status);
  const canResume = record.status === "manual-unpaid" || record.status === "payment-failed";
  return (
    <ScrollView contentContainerStyle={styles.detailsContent} testID="payment-recovery-details-scroll">
      <View style={styles.detailsHeading}>
        <View style={styles.detailsHeadingCopy}>
          <StatusBadge locale={locale} status={record.status} />
          <Text style={styles.statusHint}>{t(statusHintKey(record.status))}</Text>
        </View>
        <View>
          <Text style={styles.fieldLabel}>{t("details.amount")}</Text>
          <Text style={styles.heroAmount}>{formatAud(record.amountCents)}</Text>
        </View>
      </View>

      {record.terminalAmountMismatchCents ? (
        <Text accessibilityRole="alert" style={styles.mismatchWarning} testID="payment-recovery-mismatch-warning">
          {t("mismatch.warning", {
            charged: formatAud(record.terminalAmountMismatchCents),
            order: formatAud(record.amountCents),
          })}
        </Text>
      ) : null}
      <View style={styles.factGrid}>
        {record.terminalAmountMismatchCents ? (
          <Fact label={t("mismatch.charged")} value={formatAud(record.terminalAmountMismatchCents)} />
        ) : null}
        <Fact label={t("details.order")} value={record.orderGuid} />
        <Fact label={t("details.transaction")} value={record.transactionReference ?? t("details.unavailable")} />
        <Fact label={t("details.terminal")} value={record.terminalName ?? t("details.unavailable")} />
        <Fact label={t("details.receipt")} value={record.receiptReference ?? t("details.unavailable")} />
      </View>

      <Text style={styles.sectionTitle}>{t("details.items")}</Text>
      <View style={styles.detailsSection}>
        {record.lines.map((line) => (
          <View key={line.id} style={styles.lineRow}>
            <View style={styles.lineCopy}>
              <Text style={styles.lineName}>{line.name}</Text>
              <Text style={styles.recordMeta}>{t("details.quantity", { quantity: line.quantity })}</Text>
            </View>
            <Text style={styles.lineAmount}>{formatAud(line.amountCents)}</Text>
          </View>
        ))}
      </View>

      <Text style={styles.sectionTitle}>{t("details.history")}</Text>
      <View style={styles.detailsSection}>
        {record.events.length === 0 ? (
          <Text style={styles.emptyText}>{t("details.noHistory")}</Text>
        ) : (
          record.events.map((event) => <RecoveryEventRow event={event} key={event.id} locale={locale} />)
        )}
      </View>

      <View style={styles.detailsActions}>
          <ActionButton
            disabled={action !== "idle"}
            label={action === "recovering" ? t("action.recovering") : canResume ? t("action.resume") : t("action.recover")}
            onPress={onRecover}
            testID="payment-recovery-recover-original"
          />
          {!resolved && record.status !== "charged-order-incomplete" && record.status !== "review-required" && <ActionButton
            disabled={action !== "idle"}
            label={t("action.manual")}
            onPress={onManual}
            testID="payment-recovery-open-manual"
            tone="secondary"
          />}
      </View>
    </ScrollView>
  );
}

function RecoveryEventRow({ event, locale }: Readonly<{ event: PaymentRecoveryEvent; locale: PaymentRecoveryLocale }>) {
  return (
    <View style={styles.eventRow}>
      <View style={styles.eventMarker} />
      <View style={styles.eventCopy}>
        <Text style={styles.eventText}>
          {paymentRecoveryText(locale, `event.${event.code}`, event.params)}
        </Text>
        <Text style={styles.recordMeta}>
          {paymentRecoveryText(locale, `details.source.${event.source}`)} · {formatDateTime(event.occurredAtIso, locale)}
        </Text>
        {(["evidenceReference", "note", "supervisorName"] as const).map((field) => {
          const value = event.params?.[field];
          const key = field === "evidenceReference" ? "details.evidence" : field === "note" ? "details.note" : "details.supervisor";
          return value ? <Text key={field} selectable style={styles.recordMeta}>{paymentRecoveryText(locale, key, { value })}</Text> : null;
        })}
      </View>
    </View>
  );
}

function ManualVerificationModal({
  action,
  locale,
  onClose,
  onSubmit,
  record,
  visible,
}: Readonly<{
  action: "idle" | "recovering" | "manual-verifying";
  locale: PaymentRecoveryLocale;
  onClose(): void;
  onSubmit(draft: ManualVerificationDraft, record: PaymentRecoveryRecord): Promise<void>;
  record: PaymentRecoveryRecord | null;
  visible: boolean;
}>) {
  const [draft, setDraft] = useState<ManualVerificationDraft>(EMPTY_MANUAL_VERIFICATION_DRAFT);
  const t = (key: PaymentRecoveryCopyKey, values?: Readonly<Record<string, string | number>>) =>
    paymentRecoveryText(locale, key, values);

  useEffect(() => setDraft(EMPTY_MANUAL_VERIFICATION_DRAFT), [record?.id]);
  const validation = record ? validateManualVerification(record, draft) : null;
  const submitting = action === "manual-verifying";
  const update = <Key extends keyof ManualVerificationDraft>(
    key: Key,
    value: ManualVerificationDraft[Key],
  ) => setDraft((current) => ({ ...current, [key]: value }));
  const selectFinding = (finding: ManualPaymentFinding) => {
    setDraft((current) => ({
      ...current,
      finding,
      amount: finding === "paid" ? current.amount : "",
    }));
  };

  if (!record || !visible) return null;
  const submitKey = draft.finding ? `action.submit.${draft.finding}` as const : "action.submit.paid";
  return (
    <View style={StyleSheet.absoluteFill}>
      <View accessibilityViewIsModal style={styles.modalBackdrop} testID="payment-recovery-manual-modal">
        <PosPressable accessible={false} onPress={onClose} sound="navigate" style={styles.modalDismissArea} testID="payment-recovery-manual-backdrop" />
        <View style={styles.modalCard}>
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.modalContent}
            testID="payment-recovery-manual-scroll"
          >
            <Text style={styles.modalTitle}>{t("manual.title")}</Text>
            <Text style={styles.modalSubtitle}>{t("manual.subtitle")}</Text>
            <Text style={styles.modalOrder}>
              {t("manual.order", { order: shortGuid(record.orderGuid), amount: formatAud(record.amountCents) })}
            </Text>

            <Text style={styles.formLabel}>{t("manual.finding")}</Text>
            <View style={styles.findingGrid}>
              {(["paid", "unpaid", "uncertain"] as const).map((finding) => {
                const mismatchCents = record.terminalAmountMismatchCents ?? null;
                const charged = mismatchCents === null ? "" : formatAud(mismatchCents);
                const blockedPaid = mismatchCents !== null && finding === "paid";
                const reversal = mismatchCents !== null && finding === "unpaid";
                return (
                  <FindingOption
                    key={finding}
                    disabled={blockedPaid}
                    label={reversal ? t("manual.finding.unpaidMismatch") : t(`manual.finding.${finding}`)}
                    hint={
                      blockedPaid
                        ? t("manual.finding.paidBlockedHint", { charged })
                        : reversal
                          ? t("manual.finding.unpaidMismatchHint", { charged })
                          : t(`manual.finding.${finding}Hint`)
                    }
                    onPress={() => selectFinding(finding)}
                    selected={draft.finding === finding}
                    testID={`payment-recovery-finding-${finding}`}
                  />
                );
              })}
            </View>

            {draft.finding === "paid" ? (
              <FormField label={t("manual.amount")}>
                <PosKeyboardAwareTextInput
                  accessibilityLabel={t("manual.amount")}
                  keyboardType="decimal-pad"
                  onChangeText={(value) => update("amount", value)}
                  placeholder={t("manual.amountPlaceholder")}
                  style={[styles.textInput, validation?.errors.amount && styles.inputError]}
                  testID="payment-recovery-manual-amount"
                  value={draft.amount}
                />
                {draft.amount && validation?.errors.amount ? (
                  <ValidationText>
                    {validation.errors.amount === "mismatch"
                      ? t("validation.amountMismatch", { amount: formatAud(record.amountCents) })
                      : t("validation.amount")}
                  </ValidationText>
                ) : null}
              </FormField>
            ) : null}

            <FormField label={t("manual.evidence")}> 
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("manual.evidence")}
                autoCapitalize="characters"
                maxLength={256}
                onChangeText={(value) => update("evidenceReference", value)}
                placeholder={t("manual.evidencePlaceholder")}
                style={styles.textInput}
                testID="payment-recovery-manual-evidence"
                value={draft.evidenceReference}
              />
            </FormField>
            <FormField label={t("manual.note")}>
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("manual.note")}
                multiline
                maxLength={1000}
                onChangeText={(value) => update("note", value)}
                placeholder={t("manual.notePlaceholder")}
                style={[styles.textInput, styles.noteInput]}
                testID="payment-recovery-manual-note"
                value={draft.note}
              />
            </FormField>

            {record.terminalAmountMismatchCents && draft.finding === "unpaid" ? (
              <>
                <PosPressable
                  accessibilityRole="checkbox"
                  accessibilityState={{ checked: draft.terminalChargeAcknowledged }}
                  onPress={() => update("terminalChargeAcknowledged", !draft.terminalChargeAcknowledged)}
                  style={styles.checkboxRow}
                  testID="payment-recovery-manual-terminal-charge"
                >
                  <View style={[styles.checkbox, draft.terminalChargeAcknowledged && styles.checkboxChecked]}>
                    <Text style={styles.checkboxMark}>{draft.terminalChargeAcknowledged ? "✓" : ""}</Text>
                  </View>
                  <Text style={styles.checkboxLabel}>
                    {t("manual.terminalCharge", { charged: formatAud(record.terminalAmountMismatchCents) })}
                  </Text>
                </PosPressable>
                {validation?.errors.terminalChargeAcknowledgement ? (
                  <ValidationText>{t("validation.terminalCharge")}</ValidationText>
                ) : null}
              </>
            ) : null}
            <Text style={styles.formLabel}>{t("manual.authorization")}</Text>
            <PosPressable
              accessibilityRole="checkbox"
              accessibilityState={{ checked: draft.confirmedByOperator }}
              onPress={() => update("confirmedByOperator", !draft.confirmedByOperator)}
              style={styles.checkboxRow}
              testID="payment-recovery-manual-confirmation"
            >
              <View style={[styles.checkbox, draft.confirmedByOperator && styles.checkboxChecked]}>
                <Text style={styles.checkboxMark}>{draft.confirmedByOperator ? "✓" : ""}</Text>
              </View>
              <Text style={styles.checkboxLabel}>{t("manual.authorizationConfirm")}</Text>
            </PosPressable>
            <Text style={styles.authorizationHint}>{t("manual.authorizationHint")}</Text>
            <Text style={styles.auditNotice}>{t("manual.auditNotice")}</Text>

            <View style={styles.modalActions}>
              <ActionButton label={t("action.close")} onPress={onClose} testID="payment-recovery-manual-cancel" tone="quiet" />
              <ActionButton
                disabled={!validation?.valid || submitting}
                label={submitting ? t("action.submitting") : t(submitKey)}
                onPress={() => void onSubmit(draft, record).catch(() => undefined)}
                testID="payment-recovery-manual-submit"
              />
            </View>
          </PosKeyboardAwareScrollView>
        </View>
      </View>
    </View>
  );
}

function FindingOption({ disabled = false, hint, label, onPress, selected, testID }: Readonly<{
  disabled?: boolean;
  hint: string;
  label: string;
  onPress(): void;
  selected: boolean;
  testID: string;
}>) {
  return (
    <PosPressable
      accessibilityRole="radio"
      accessibilityState={{ selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.findingOption, selected && styles.findingOptionSelected, disabled && styles.disabled]}
      testID={testID}
    >
      <View style={[styles.radio, selected && styles.radioSelected]} />
      <View style={styles.findingCopy}>
        <Text style={styles.findingLabel}>{label}</Text>
        <Text style={styles.findingHint}>{hint}</Text>
      </View>
    </PosPressable>
  );
}

function FilterTab({ active, label, onPress, testID }: Readonly<{
  active: boolean;
  label: string;
  onPress(): void;
  testID: string;
}>) {
  return (
    <PosPressable
      accessibilityRole="tab"
      accessibilityState={{ selected: active }}
      onPress={onPress}
      sound="navigate"
      style={[styles.filterTab, active && styles.filterTabActive]}
      testID={testID}
    >
      <Text style={[styles.filterTabText, active && styles.filterTabTextActive]}>{label}</Text>
    </PosPressable>
  );
}

function StatusBadge({ locale, status }: Readonly<{ locale: PaymentRecoveryLocale; status: PaymentRecoveryStatus }>) {
  return (
    <View style={[styles.statusBadge, statusTone(status)]}>
      <Text style={styles.statusBadgeText}>{paymentRecoveryText(locale, statusKey(status))}</Text>
    </View>
  );
}

function Fact({ label, value }: Readonly<{ label: string; value: string }>) {
  return (
    <View style={styles.fact}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <Text selectable style={styles.factValue}>{value}</Text>
    </View>
  );
}

function FormField({ children, label }: Readonly<{ children: React.ReactNode; label: string }>) {
  return (
    <View style={styles.formField}>
      <Text style={styles.formLabel}>{label}</Text>
      {children}
    </View>
  );
}

function ValidationText({ children }: Readonly<{ children: string }>) {
  return <Text accessibilityRole="alert" style={styles.validationText}>{children}</Text>;
}

function CenteredState({ loading = false, message }: Readonly<{ loading?: boolean; message: string }>) {
  return (
    <View style={styles.centeredState}>
      {loading ? <ActivityIndicator color={posColors.blue} size="small" /> : null}
      <Text style={styles.emptyText}>{message}</Text>
    </View>
  );
}

function ActionButton({ compact = false, disabled = false, label, onPress, testID, tone = "primary" }: Readonly<{
  compact?: boolean;
  disabled?: boolean;
  label: string;
  onPress(): void;
  testID: string;
  tone?: "primary" | "secondary" | "quiet";
}>) {
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[
        styles.actionButton,
        compact && styles.actionButtonCompact,
        tone === "primary" ? styles.actionPrimary : tone === "secondary" ? styles.actionSecondary : styles.actionQuiet,
        disabled && styles.disabled,
      ]}
      testID={testID}
    >
      <Text style={[styles.actionText, tone !== "primary" && styles.actionTextDark]}>{label}</Text>
    </PosPressable>
  );
}

function statusKey(status: PaymentRecoveryStatus): PaymentRecoveryCopyKey {
  return `status.${status}`;
}

function statusHintKey(status: PaymentRecoveryStatus): PaymentRecoveryCopyKey {
  return `statusHint.${status}`;
}

function isResolved(status: PaymentRecoveryStatus): boolean {
  return status === "manual-paid" || status === "manual-unpaid" || status === "provider-recovered";
}

function recordMatchesFilter(record: PaymentRecoveryRecord, filter: PaymentRecoveryFilter): boolean {
  if (filter === "failed") return record.status === "payment-failed";
  if (filter === "resolved") return isResolved(record.status);
  return !isResolved(record.status) && record.status !== "payment-failed";
}

function recordMatchesKeyword(record: PaymentRecoveryRecord, keyword: string): boolean {
  const query = keyword.trim().toLocaleLowerCase();
  if (!query) return true;
  return [record.orderGuid, record.transactionReference, record.receiptReference]
    .some((value) => value?.toLocaleLowerCase().includes(query));
}

function errorText(
  t: (key: PaymentRecoveryCopyKey, values?: Readonly<Record<string, string | number>>) => string,
  code: string,
): string {
  if (code === "RECOVERY_LOAD_FAILED" || code === "RECOVERY_ACTION_FAILED" || code === "RECOVERY_BUSY" || code === "RECOVERY_CURRENT_SALE_BUSY" || code === "RECOVERY_PROVIDER_UNAVAILABLE" || code === "RECOVERY_AUTHORIZATION_DENIED" || code === "RECOVERY_SESSION_CHANGED" || code === "RECOVERY_TERMINAL_CONFIRMATION_PENDING" || code === "RECOVERY_APPROVED_AMOUNT_MISMATCH") {
    return t(`error.${code}`);
  }
  return t("error.action");
}

export const PaymentRecoveryCenterScreen = PaymentRecoveryScreen;

function statusTone(status: PaymentRecoveryStatus) {
  if (status === "payment-failed" || status === "review-required") return styles.statusDanger;
  if (isResolved(status)) return styles.statusSuccess;
  return styles.statusWarning;
}

function formatAud(cents: number): string {
  return `AU$${(cents / 100).toFixed(2)}`;
}

function formatDateTime(value: string, locale: PaymentRecoveryLocale): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    dateStyle: "short",
    timeStyle: "short",
  }).format(date);
}

function shortGuid(value: string): string {
  return value.length <= 16 ? value : `${value.slice(0, 12)}…`;
}

const styles = StyleSheet.create({
  safeArea: { backgroundColor: posColors.canvas, flex: 1 },
  header: { alignItems: "center", backgroundColor: posColors.ink, flexDirection: "row", gap: 16, justifyContent: "space-between", paddingHorizontal: 18, paddingVertical: 12 },
  headerCopy: { flex: 1 },
  title: { color: "#FFFFFF", fontSize: 24, fontWeight: "800" },
  subtitle: { color: "#DDE7EF", fontSize: 13, marginTop: 2 },
  workspace: { flex: 1, flexDirection: "row", gap: 14, padding: 14 },
  workspaceCompact: { gap: 8, padding: 8 },
  listPane: { backgroundColor: posColors.surface, borderColor: posColors.border, borderWidth: 1, minWidth: 350, width: "38%" },
  listPaneCompact: { minWidth: 310, width: "42%" },
  tabs: { borderBottomColor: posColors.border, borderBottomWidth: 1, flexDirection: "row" },
  filterTab: { alignItems: "center", borderBottomColor: "transparent", borderBottomWidth: 3, flex: 1, justifyContent: "center", minHeight: PAYMENT_RECOVERY_MIN_TOUCH_TARGET, paddingHorizontal: 8 },
  filterTabActive: { backgroundColor: posColors.blueSoft, borderBottomColor: posColors.blue },
  filterTabText: { color: posColors.mutedInk, fontSize: 13, fontWeight: "700" },
  filterTabTextActive: { color: posColors.blue },
  searchScroll: { flexGrow: 0, flexShrink: 0 },
  searchRow: { alignItems: "center", flexDirection: "row", gap: 8, padding: 10 },
  searchInput: { backgroundColor: "#FFFFFF", borderColor: posColors.border, borderWidth: 1, color: posColors.ink, flex: 1, minHeight: PAYMENT_RECOVERY_MIN_TOUCH_TARGET, paddingHorizontal: 10 },
  sectionTitle: { color: posColors.ink, fontSize: 15, fontWeight: "800", paddingHorizontal: 12, paddingVertical: 9 },
  recordList: { gap: 8, padding: 10 },
  recordRow: { backgroundColor: "#FAFAF8", borderColor: posColors.border, borderWidth: 1, minHeight: 96, padding: 11 },
  recordRowActive: { backgroundColor: posColors.blueSoft, borderColor: posColors.blue, borderWidth: 2 },
  recordTopLine: { alignItems: "center", flexDirection: "row", gap: 8, justifyContent: "space-between" },
  recordAmount: { color: posColors.ink, fontSize: 18, fontWeight: "800" },
  recordOrder: { color: posColors.ink, fontSize: 13, fontWeight: "700", marginTop: 8 },
  recordMeta: { color: posColors.mutedInk, fontSize: 12, marginTop: 3 },
  detailsPane: { backgroundColor: posColors.surface, borderColor: posColors.border, borderWidth: 1, flex: 1, minWidth: 400 },
  detailsContent: { padding: 16 },
  detailsHeading: { alignItems: "flex-start", borderBottomColor: posColors.border, borderBottomWidth: 1, flexDirection: "row", gap: 20, justifyContent: "space-between", paddingBottom: 14 },
  detailsHeadingCopy: { flex: 1 },
  mismatchWarning: { backgroundColor: posColors.redSoft, color: posColors.red, fontSize: 13, fontWeight: "700", lineHeight: 19, marginTop: 10, padding: 10 },
  statusHint: { color: posColors.mutedInk, fontSize: 14, lineHeight: 20, marginTop: 8 },
  fieldLabel: { color: posColors.mutedInk, fontSize: 11, fontWeight: "700", letterSpacing: 0.4, textTransform: "uppercase" },
  heroAmount: { color: posColors.ink, fontSize: 30, fontWeight: "900", marginTop: 2 },
  factGrid: { flexDirection: "row", flexWrap: "wrap", gap: 10, paddingVertical: 14 },
  fact: { backgroundColor: "#F8F7F3", borderColor: posColors.border, borderWidth: 1, minWidth: "46%", padding: 10 },
  factValue: { color: posColors.ink, fontSize: 13, fontWeight: "700", marginTop: 5 },
  detailsSection: { borderColor: posColors.border, borderWidth: 1, marginBottom: 10 },
  lineRow: { alignItems: "center", borderBottomColor: posColors.border, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: "row", justifyContent: "space-between", minHeight: 52, paddingHorizontal: 12, paddingVertical: 8 },
  lineCopy: { flex: 1 },
  lineName: { color: posColors.ink, fontSize: 14, fontWeight: "700" },
  lineAmount: { color: posColors.ink, fontSize: 14, fontWeight: "800" },
  eventRow: { flexDirection: "row", gap: 10, paddingHorizontal: 12, paddingVertical: 9 },
  eventMarker: { backgroundColor: posColors.blue, borderRadius: 4, height: 8, marginTop: 5, width: 8 },
  eventCopy: { flex: 1 },
  eventText: { color: posColors.ink, fontSize: 13, fontWeight: "600" },
  detailsActions: { flexDirection: "row", flexWrap: "wrap", gap: 10, justifyContent: "flex-end", paddingTop: 8 },
  actionError: { backgroundColor: posColors.redSoft, color: posColors.red, fontSize: 13, padding: 10 },
  statusBadge: { alignSelf: "flex-start", borderRadius: 2, paddingHorizontal: 8, paddingVertical: 5 },
  statusWarning: { backgroundColor: posColors.yellowSoft },
  statusDanger: { backgroundColor: posColors.redSoft },
  statusSuccess: { backgroundColor: posColors.greenSoft },
  statusBadgeText: { color: posColors.ink, fontSize: 12, fontWeight: "800" },
  centeredState: { alignItems: "center", flex: 1, gap: 10, justifyContent: "center", minHeight: 160, padding: 20 },
  emptyText: { color: posColors.mutedInk, fontSize: 13, lineHeight: 19, textAlign: "center" },
  actionButton: { alignItems: "center", justifyContent: "center", minHeight: PAYMENT_RECOVERY_MIN_TOUCH_TARGET, minWidth: 148, paddingHorizontal: 14, paddingVertical: 9 },
  actionButtonCompact: { minWidth: 88 },
  actionPrimary: { backgroundColor: posColors.blue },
  actionSecondary: { backgroundColor: posColors.blueSoft, borderColor: posColors.blue, borderWidth: 1 },
  actionQuiet: { backgroundColor: "#FFFFFF", borderColor: posColors.border, borderWidth: 1 },
  actionText: { color: "#FFFFFF", fontSize: 13, fontWeight: "800", textAlign: "center" },
  actionTextDark: { color: posColors.ink },
  disabled: { opacity: 0.42 },
  modalBackdrop: { alignItems: "center", backgroundColor: "rgba(16,37,58,0.48)", flex: 1, justifyContent: "center", padding: 24 },
  modalDismissArea: { ...StyleSheet.absoluteFillObject },
  modalCard: { backgroundColor: posColors.surface, borderColor: posColors.border, borderWidth: 1, maxHeight: "92%", maxWidth: 840, width: "86%" },
  modalContent: { padding: 20 },
  modalTitle: { color: posColors.ink, fontSize: 22, fontWeight: "900" },
  modalSubtitle: { color: posColors.mutedInk, fontSize: 13, lineHeight: 19, marginTop: 4 },
  modalOrder: { backgroundColor: posColors.blueSoft, color: posColors.ink, fontSize: 14, fontWeight: "800", marginTop: 12, padding: 10 },
  formLabel: { color: posColors.ink, fontSize: 13, fontWeight: "800", marginBottom: 6, marginTop: 14 },
  findingGrid: { flexDirection: "row", gap: 8 },
  findingOption: { alignItems: "flex-start", borderColor: posColors.border, borderWidth: 1, flex: 1, flexDirection: "row", gap: 8, minHeight: 86, padding: 10 },
  findingOptionSelected: { backgroundColor: posColors.blueSoft, borderColor: posColors.blue, borderWidth: 2 },
  radio: { borderColor: posColors.mutedInk, borderRadius: 8, borderWidth: 2, height: 16, marginTop: 2, width: 16 },
  radioSelected: { backgroundColor: posColors.blue, borderColor: posColors.blue, borderWidth: 4 },
  findingCopy: { flex: 1 },
  findingLabel: { color: posColors.ink, fontSize: 13, fontWeight: "800" },
  findingHint: { color: posColors.mutedInk, fontSize: 11, lineHeight: 16, marginTop: 3 },
  formField: { marginTop: 2 },
  textInput: { backgroundColor: "#FFFFFF", borderColor: posColors.border, borderWidth: 1, color: posColors.ink, minHeight: PAYMENT_RECOVERY_MIN_TOUCH_TARGET, paddingHorizontal: 10, paddingVertical: 8 },
  noteInput: { minHeight: 74, textAlignVertical: "top" },
  inputError: { borderColor: posColors.red },
  validationText: { color: posColors.red, fontSize: 12, marginTop: 4 },
  checkboxRow: { alignItems: "flex-start", borderColor: posColors.border, borderWidth: 1, flexDirection: "row", gap: 10, minHeight: PAYMENT_RECOVERY_MIN_TOUCH_TARGET, padding: 10 },
  checkbox: { alignItems: "center", borderColor: posColors.mutedInk, borderWidth: 2, height: 22, justifyContent: "center", width: 22 },
  checkboxChecked: { backgroundColor: posColors.blue, borderColor: posColors.blue },
  checkboxMark: { color: "#FFFFFF", fontSize: 15, fontWeight: "900" },
  checkboxLabel: { color: posColors.ink, flex: 1, fontSize: 13, lineHeight: 19 },
  authorizationHint: { color: posColors.blue, fontSize: 12, marginTop: 6 },
  auditNotice: { color: posColors.mutedInk, fontSize: 11, marginTop: 8 },
  modalActions: { flexDirection: "row", gap: 10, justifyContent: "flex-end", marginTop: 18 },
});
