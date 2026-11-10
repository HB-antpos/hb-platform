import { MaterialCommunityIcons } from "@expo/vector-icons";
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

import {
  paymentRecoveryText,
  resolvePaymentRecoveryLocale,
  type PaymentRecoveryCopyKey,
  type PaymentRecoveryLocale,
} from "./payment-recovery-copy";
import {
  EMPTY_MANUAL_VERIFICATION_DRAFT,
  MANUAL_NOTE_PRESETS,
  appendManualNotePreset,
  isManualEvidencePlaceholder,
  validateManualVerification,
  normalizeVerificationNote,
  type ManualVerificationDraft,
} from "./payment-recovery-presenter";
import type {
  ManualPaymentFinding,
  PaymentRecoveryCenterService,
  PaymentRecoveryCenterState,
  PaymentRecoveryEvent,
  PaymentRecoveryFilter,
  PaymentRecoveryRecord,
  PaymentRecoveryStatus,
  SquareRefundSettlementView,
} from "./payment-recovery-types";

import { PosButton } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSegmented } from "@/ui/controls/pos-segmented";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

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
  usePosThemeVersion();
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

  // 有恢复、人工核实或退款结案在进行时，返回键与侧栏跳转一起锁住，避免中途离开。
  const busy = state.action !== "idle";
  // 人工核实面板是页面内覆盖层，只盖住内容区、盖不到左侧栏；面板开着时也锁住侧栏，
  // 免得填到一半误点侧栏离开。返回键本身被面板遮住，可用条件不变。
  const manualPanelOpen = manualVisible && manualRecord !== null;
  // 待恢复角标：与「待恢复」分段使用同一套归类规则，只统计尚未处理且不是明确失败的记录。
  const pendingCount = state.records.filter((record) => recordMatchesFilter(record, "pending")).length;
  const errorMessage = state.errorCode && !state.loading ? errorText(t, state.errorCode) : null;

  return (
    <PosScreenFrame
      active="payment-recovery"
      header={
        <PosPageHeader
          backDisabled={busy}
          backLabel={t("action.back")}
          backTestID="payment-recovery-back-to-sale"
          onBack={() => onBack?.()}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
      navigationLocked={busy || manualPanelOpen}
      testID="payment-recovery-screen"
    >
      <View style={[styles.workspace, compact && styles.workspaceCompact]}>
        <View style={[styles.listPane, compact && styles.listPaneCompact]} testID="payment-recovery-list-pane">
          <PosSegmented
            onChange={selectFilter}
            options={(["pending", "failed", "resolved"] as const).map((filter) => ({
              value: filter,
              label: t(`filter.${filter}`),
              testID: `payment-recovery-filter-${filter}`,
              ...(filter === "pending" ? { badge: pendingCount } : {}),
            }))}
            value={state.filter}
          />
          {/* 搜索框需置于键盘感知滚动容器内，软键盘弹出时才不会遮挡输入。 */}
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.searchRow}
            keyboardShouldPersistTaps="handled"
            scrollEnabled={false}
            style={styles.searchScroll}
          >
            <View style={styles.searchField}>
              <MaterialCommunityIcons accessible={false} color={posColors.mutedInk} name="magnify" size={22} />
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("search.label")}
                autoCapitalize="none"
                onChangeText={setKeyword}
                onSubmitEditing={applySearch}
                placeholder={t("search.placeholder")}
                placeholderTextColor={posColors.placeholder}
                returnKeyType="search"
                style={styles.searchInput}
                testID="payment-recovery-search"
                value={keyword}
              />
            </View>
            <PosButton
              disabled={state.refreshing}
              icon="refresh"
              label={state.refreshing ? t("action.refreshing") : t("action.refresh")}
              onPress={applySearch}
              size="medium"
              testID="payment-recovery-refresh"
              tone="quiet"
            />
          </PosKeyboardAwareScrollView>
          <RefundSettlementPanel
            action={state.action}
            locale={locale}
            onCheck={() => void service.checkRefundSettlements().catch(() => undefined)}
            onResolve={(attemptId, note) => service.acknowledgeRefundSettlementFailure({ attemptId, note })}
            settlements={state.refundSettlements}
          />
          <Text style={styles.sectionLabel}>{t("list.title")}</Text>
          <ScrollView
            contentContainerStyle={styles.recordList}
            style={styles.recordScroll}
            testID="payment-recovery-record-list"
          >
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
            <RecoveryDetails compact={compact} locale={locale} record={selected} />
          ) : (
            <CenteredState message={t("details.select")} />
          )}
          {errorMessage ? (
            <Text accessibilityRole="alert" style={styles.actionError}>
              {errorMessage}
            </Text>
          ) : null}
          {/* 两个处理动作固定在详情底部，不随明细滚动。 */}
          {selected ? (
            <RecoveryActions
              action={state.action}
              compact={compact}
              locale={locale}
              onManual={() => {
                setManualRecord(selected);
                setManualVisible(true);
              }}
              onRecover={() => void service.recoverOriginalPayment(selected.id).catch(() => undefined)}
              record={selected}
            />
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
    </PosScreenFrame>
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
  usePosThemeVersion();
  const t = (key: PaymentRecoveryCopyKey) => paymentRecoveryText(locale, key);
  return (
    <PosPressable
      accessibilityLabel={`${t(statusKey(record.status))}, ${formatAud(record.amountCents)}, ${record.orderGuid}`}
      accessibilityRole="button"
      accessibilityState={{ selected: active }}
      onPress={onPress}
      sound="navigate"
      style={({ pressed }) => [styles.recordRow, active && styles.recordRowActive, pressed && styles.pressed]}
      testID={`payment-recovery-record-${record.id}`}
    >
      <View style={styles.recordCopy}>
        <StatusBadge locale={locale} status={record.status} />
        <Text numberOfLines={1} style={styles.recordOrder}>{record.orderGuid}</Text>
        <Text numberOfLines={1} style={styles.recordMeta}>
          {formatDateTime(record.occurredAtIso, locale)} · {record.terminalName ?? t("details.unavailable")}
        </Text>
      </View>
      <Text style={styles.recordAmount}>{formatAud(record.amountCents)}</Text>
    </PosPressable>
  );
}

/** 详情正文：先给结论（状态、提醒、待核实金额），再给四项凭据、商品明细和处理记录。 */
function RecoveryDetails({
  compact,
  locale,
  record,
}: Readonly<{
  compact: boolean;
  locale: PaymentRecoveryLocale;
  record: PaymentRecoveryRecord;
}>) {
  usePosThemeVersion();
  const t = (key: PaymentRecoveryCopyKey, values?: Readonly<Record<string, string | number>>) =>
    paymentRecoveryText(locale, key, values);
  const resolved = isResolved(record.status);
  return (
    <ScrollView
      contentContainerStyle={styles.detailsContent}
      style={styles.detailsScroll}
      testID="payment-recovery-details-scroll"
    >
      <View style={styles.detailsHeading}>
        <View style={styles.detailsHeadingCopy}>
          <StatusBadge large locale={locale} status={record.status} />
          {/* 未处理完的记录用红字提醒不要重复扣款；已处理的只做普通说明。 */}
          <Text style={[styles.statusHint, !resolved && styles.statusHintAlert]}>{t(statusHintKey(record.status))}</Text>
        </View>
        <View style={styles.heroBlock}>
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
        <Fact label={t("details.order")} value={record.orderGuid} wide />
        <Fact label={t("details.transaction")} value={record.transactionReference ?? t("details.unavailable")} />
        <Fact label={t("details.terminal")} value={record.terminalName ?? t("details.unavailable")} />
        <Fact label={t("details.receipt")} value={record.receiptReference ?? t("details.unavailable")} />
      </View>

      <View style={[styles.detailsColumns, compact && styles.detailsColumnsCompact]}>
        <View style={styles.itemsColumn}>
          <Text style={styles.sectionLabel}>{t("details.items")}</Text>
          {record.lines.map((line) => (
            <View key={line.id} style={styles.lineRow}>
              <Text style={styles.lineName}>{line.name}</Text>
              <Text style={styles.lineQuantity}>{t("details.quantity", { quantity: line.quantity })}</Text>
              <Text style={styles.lineAmount}>{formatAud(line.amountCents)}</Text>
            </View>
          ))}
        </View>
        <View style={[styles.historyColumn, compact && styles.historyColumnCompact]}>
          <Text style={styles.sectionLabel}>{t("details.history")}</Text>
          {record.events.length === 0 ? (
            <Text style={styles.emptyText}>{t("details.noHistory")}</Text>
          ) : (
            record.events.map((event) => <RecoveryEventRow event={event} key={event.id} locale={locale} />)
          )}
        </View>
      </View>
    </ScrollView>
  );
}

/** 详情底部固定的两个动作；可用条件与原实现一致，只调整了位置和尺寸。 */
function RecoveryActions({
  action,
  compact,
  locale,
  onManual,
  onRecover,
  record,
}: Readonly<{
  action: PaymentRecoveryCenterState["action"];
  compact: boolean;
  locale: PaymentRecoveryLocale;
  onManual(): void;
  onRecover(): void;
  record: PaymentRecoveryRecord;
}>) {
  usePosThemeVersion();
  const t = (key: PaymentRecoveryCopyKey) => paymentRecoveryText(locale, key);
  const resolved = isResolved(record.status);
  const canResume = record.status === "manual-unpaid" || record.status === "payment-failed";
  return (
    <View style={[styles.detailsActions, compact && styles.detailsActionsCompact]}>
      <PosButton
        disabled={action !== "idle"}
        fill={!compact}
        icon="credit-card-refresh-outline"
        label={action === "recovering" ? t("action.recovering") : canResume ? t("action.resume") : t("action.recover")}
        numberOfLines={2}
        onPress={onRecover}
        size="large"
        testID="payment-recovery-recover-original"
        tone="primary"
      />
      {!resolved && record.status !== "charged-order-incomplete" && record.status !== "review-required" && <PosButton
        disabled={action !== "idle"}
        icon="account-check-outline"
        label={t("action.manual")}
        numberOfLines={2}
        onPress={onManual}
        size="large"
        style={!compact && styles.manualAction}
        testID="payment-recovery-open-manual"
      />}
    </View>
  );
}

function RecoveryEventRow({ event, locale }: Readonly<{ event: PaymentRecoveryEvent; locale: PaymentRecoveryLocale }>) {
  usePosThemeVersion();
  return (
    <View style={styles.eventRow}>
      <View style={styles.eventMarker} />
      <View style={styles.eventCopy}>
        <Text style={styles.eventText}>
          {paymentRecoveryText(locale, `event.${event.code}`, event.params)}
        </Text>
        <Text style={styles.eventMeta}>
          {paymentRecoveryText(locale, `details.source.${event.source}`)} · {formatDateTime(event.occurredAtIso, locale)}
        </Text>
        {(["evidenceReference", "note", "supervisorName"] as const).map((field) => {
          const value = event.params?.[field];
          const key = field === "evidenceReference" ? "details.evidence" : field === "note" ? "details.note" : "details.supervisor";
          if (field === "evidenceReference" && isManualEvidencePlaceholder(value === undefined ? null : String(value))) return null;
          return value ? <Text key={field} selectable style={styles.eventMeta}>{paymentRecoveryText(locale, key, { value })}</Text> : null;
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
  action: PaymentRecoveryCenterState["action"];
  locale: PaymentRecoveryLocale;
  onClose(): void;
  onSubmit(draft: ManualVerificationDraft, record: PaymentRecoveryRecord): Promise<void>;
  record: PaymentRecoveryRecord | null;
  visible: boolean;
}>) {
  usePosThemeVersion();
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

  // 备注预设按核实结果提供：已扣款 2 条、未扣款 3 条、仍无法确认没有。
  const presets = draft.finding ? MANUAL_NOTE_PRESETS[draft.finding] : [];
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
                  placeholderTextColor={posColors.placeholder}
                  style={[styles.textInput, styles.amountInput, validation?.errors.amount && styles.inputError]}
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

            <FormField label={t("manual.note")}>
              <PosKeyboardAwareTextInput
                accessibilityLabel={t("manual.note")}
                multiline
                maxLength={1000}
                onChangeText={(value) => update("note", value)}
                placeholder={t("manual.notePlaceholder")}
                placeholderTextColor={posColors.placeholder}
                style={[styles.textInput, styles.noteInput]}
                testID="payment-recovery-manual-note"
                value={draft.note}
              />
              {presets.length > 0 ? (
                <View style={styles.presetRow} testID="payment-recovery-manual-presets">
                  {presets.map((id) => (
                    <PosButton
                      key={id}
                      label={t(`manual.preset.${id}`)}
                      onPress={() => update("note", appendManualNotePreset(
                        draft.note,
                        t(`manual.preset.${id}`),
                        locale === "zh" ? "；" : "; ",
                      ))}
                      size="small"
                      testID={`payment-recovery-manual-preset-${id}`}
                    />
                  ))}
                </View>
              ) : null}
            </FormField>

            {record.terminalAmountMismatchCents && draft.finding === "unpaid" ? (
              <>
                <CheckboxRow
                  checked={draft.terminalChargeAcknowledged}
                  label={t("manual.terminalCharge", { charged: formatAud(record.terminalAmountMismatchCents) })}
                  onPress={() => update("terminalChargeAcknowledged", !draft.terminalChargeAcknowledged)}
                  testID="payment-recovery-manual-terminal-charge"
                />
                {validation?.errors.terminalChargeAcknowledgement ? (
                  <ValidationText>{t("validation.terminalCharge")}</ValidationText>
                ) : null}
              </>
            ) : null}
            <Text style={styles.formLabel}>{t("manual.authorization")}</Text>
            <CheckboxRow
              checked={draft.confirmedByOperator}
              label={t("manual.authorizationConfirm")}
              onPress={() => update("confirmedByOperator", !draft.confirmedByOperator)}
              testID="payment-recovery-manual-confirmation"
            />
            <Text style={styles.authorizationHint}>{t("manual.authorizationHint")}</Text>
            <Text style={styles.auditNotice}>{t("manual.auditNotice")}</Text>

            <View style={styles.modalActions}>
              <PosButton
                label={t("action.close")}
                onPress={onClose}
                size="large"
                testID="payment-recovery-manual-cancel"
                tone="quiet"
              />
              <PosButton
                disabled={!validation?.valid || submitting}
                fill
                label={submitting ? t("action.submitting") : t(submitKey)}
                numberOfLines={2}
                onPress={() => void onSubmit(draft, record).catch(() => undefined)}
                size="large"
                testID="payment-recovery-manual-submit"
                tone="primary"
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
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="radio"
      accessibilityState={{ selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.findingOption, selected && styles.findingOptionSelected, disabled && styles.findingOptionDisabled]}
      testID={testID}
    >
      <View style={[styles.radio, selected && styles.radioSelected]}>
        {selected ? <View style={styles.radioDot} /> : null}
      </View>
      <View style={styles.findingCopy}>
        <Text style={[styles.findingLabel, disabled && styles.disabledText]}>{label}</Text>
        <Text style={[styles.findingHint, disabled && styles.disabledText]}>{hint}</Text>
      </View>
    </PosPressable>
  );
}

/** 整行可点的勾选框：行高 56，勾选块 28，便于在柜台上准确点中。 */
function CheckboxRow({ checked, label, onPress, testID }: Readonly<{
  checked: boolean | undefined;
  label: string;
  onPress(): void;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="checkbox"
      accessibilityState={{ checked }}
      onPress={onPress}
      style={[styles.checkboxRow, checked && styles.checkboxRowChecked]}
      testID={testID}
    >
      <View style={[styles.checkbox, checked && styles.checkboxChecked]}>
        {checked ? (
          <MaterialCommunityIcons accessible={false} color={posColors.onInverse} name="check-bold" size={20} />
        ) : null}
      </View>
      <Text style={styles.checkboxLabel}>{label}</Text>
    </PosPressable>
  );
}

type StatusTone = "danger" | "warning" | "good";

/** 状态标签；详情顶部用 large，列表行用默认尺寸。 */
function StatusBadge({ large = false, locale, status }: Readonly<{
  large?: boolean;
  locale: PaymentRecoveryLocale;
  status: PaymentRecoveryStatus;
}>) {
  usePosThemeVersion();
  const tone = statusTone(status);
  return (
    <View
      style={[
        styles.statusBadge,
        large && styles.statusBadgeLarge,
        tone === "danger" ? styles.statusDanger : tone === "good" ? styles.statusSuccess : styles.statusWarning,
      ]}
    >
      <Text
        numberOfLines={1}
        style={[
          styles.statusBadgeText,
          large && styles.statusBadgeTextLarge,
          tone === "danger" ? styles.statusDangerText : tone === "good" ? styles.statusSuccessText : styles.statusWarningText,
        ]}
      >
        {paymentRecoveryText(locale, statusKey(status))}
      </Text>
    </View>
  );
}

function Fact({ label, value, wide = false }: Readonly<{
  label: string;
  value: string;
  /** 订单号是完整 GUID，比其它几项长，占两份宽度。 */
  wide?: boolean;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.fact, wide && styles.factWide]}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <Text selectable style={styles.factValue}>{value}</Text>
    </View>
  );
}

function FormField({ children, label }: Readonly<{ children: React.ReactNode; label: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.formField}>
      <Text style={styles.formLabel}>{label}</Text>
      {children}
    </View>
  );
}

function ValidationText({ children }: Readonly<{ children: string }>) {
  usePosThemeVersion();
  return <Text accessibilityRole="alert" style={styles.validationText}>{children}</Text>;
}

function CenteredState({ loading = false, message }: Readonly<{ loading?: boolean; message: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.centeredState}>
      {loading ? <ActivityIndicator color={posColors.accent} size="small" /> : null}
      <Text style={styles.emptyText}>{message}</Text>
    </View>
  );
}

/**
 * Square 退款结算（对齐 WPF #674）：退货单已当场完成，这里只展示仍在结算或被 Square 拒绝的退款。
 * 被拒的需主管授权、填写实际退款方式后结案；检查只读查询同一笔退款，绝不重发。
 */
function RefundSettlementPanel({
  action,
  locale,
  onCheck,
  onResolve,
  settlements,
}: Readonly<{
  action: PaymentRecoveryCenterState["action"];
  locale: PaymentRecoveryLocale;
  onCheck(): void;
  onResolve(attemptId: string, note: string): Promise<void>;
  settlements: readonly SquareRefundSettlementView[];
}>) {
  usePosThemeVersion();
  const t = (key: PaymentRecoveryCopyKey, values?: Readonly<Record<string, string | number>>) =>
    paymentRecoveryText(locale, key, values);
  const [notes, setNotes] = useState<Readonly<Record<string, string>>>({});
  if (settlements.length === 0) return null;
  const busy = action !== "idle";
  const hasSettling = settlements.some((item) => item.status === "settling");
  return (
    <View style={styles.refundPanel} testID="payment-recovery-refund-settlements">
      <View style={styles.refundPanelHeader}>
        <View style={styles.refundPanelCopy}>
          <Text style={styles.refundTitle}>{t("refund.title")}</Text>
          <Text style={styles.refundSubtitle}>{t("refund.subtitle")}</Text>
        </View>
        {hasSettling ? (
          <PosButton
            disabled={busy}
            label={action === "checking-refunds" ? t("refund.checking") : t("refund.check")}
            onPress={onCheck}
            size="small"
            testID="payment-recovery-refund-check"
            tone="quiet"
          />
        ) : null}
      </View>
      {/* 备注输入需置于键盘感知滚动容器内，软键盘弹出时才不会遮挡。 */}
      <PosKeyboardAwareScrollView keyboardShouldPersistTaps="handled" nestedScrollEnabled style={styles.refundList}>
        {settlements.map((item) => {
          const note = notes[item.attemptId] ?? "";
          const rejected = item.status === "rejected";
          return (
            <View
              key={item.attemptId}
              style={[styles.refundRow, rejected ? styles.refundRowRejected : styles.refundRowSettling]}
              testID={`payment-recovery-refund-${item.attemptId}`}
            >
              <View style={styles.refundTopLine}>
                <Text style={[styles.refundStatus, rejected ? styles.statusDangerText : styles.statusWarningText]}>
                  {t(rejected ? "refund.status.rejected" : "refund.status.settling")}
                </Text>
                <Text style={styles.refundAmount}>{formatAud(item.amountCents)}</Text>
              </View>
              <Text selectable style={styles.refundMeta}>
                {t("refund.order", { order: shortGuid(item.returnOrderGuid) })}
              </Text>
              <Text style={styles.refundMeta}>
                {item.lastCheckedAtIso
                  ? t("refund.lastChecked", { time: formatDateTime(item.lastCheckedAtIso, locale) })
                  : t("refund.neverChecked")}
              </Text>
              {rejected ? (
                <View style={styles.refundResolve}>
                  <PosKeyboardAwareTextInput
                    accessibilityLabel={t("refund.noteLabel")}
                    editable={!busy}
                    maxLength={1000}
                    onChangeText={(value) => setNotes((current) => ({ ...current, [item.attemptId]: value }))}
                    placeholder={t("refund.notePlaceholder")}
                    placeholderTextColor={posColors.placeholder}
                    style={styles.textInput}
                    testID={`payment-recovery-refund-note-${item.attemptId}`}
                    value={note}
                  />
                  <PosButton
                    disabled={busy || note.trim().length === 0}
                    label={action === "resolving-refund" ? t("refund.resolving") : t("refund.resolve")}
                    onPress={() => {
                      void onResolve(item.attemptId, note.trim())
                        .then(() => setNotes((current) => ({ ...current, [item.attemptId]: "" })))
                        .catch(() => undefined);
                    }}
                    size="medium"
                    testID={`payment-recovery-refund-resolve-${item.attemptId}`}
                  />
                </View>
              ) : null}
            </View>
          );
        })}
      </PosKeyboardAwareScrollView>
    </View>
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

/**
 * 状态配色：结果未知、明确失败、待复核都属于「不能再扣款、必须先处理」，用红色；
 * 已扣款待补单用黄色提醒；已处理用绿色。
 */
function statusTone(status: PaymentRecoveryStatus): StatusTone {
  if (isResolved(status)) return "good";
  if (status === "charged-order-incomplete") return "warning";
  return "danger";
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

const styles = createPosStyles((colors) => ({
  // —— 页面骨架：左列表固定宽度，右详情占满 ——
  workspace: { flex: 1, flexDirection: "row", gap: 12, minHeight: 0, padding: 12 },
  workspaceCompact: { gap: 8, padding: 8 },
  listPane: { gap: 10, minHeight: 0, width: 410 },
  listPaneCompact: { width: 330 },
  searchScroll: { flexGrow: 0, flexShrink: 0 },
  searchRow: { alignItems: "center", flexDirection: "row", gap: 10 },
  searchField: { alignItems: "center", backgroundColor: colors.surface, borderColor: colors.borderStrong, borderRadius: posRadius.medium, borderWidth: 1.5, flex: 1, flexDirection: "row", gap: 8, minHeight: posTouch.medium, minWidth: 0, paddingHorizontal: 12 },
  searchInput: { color: colors.ink, flex: 1, fontSize: 16, minHeight: posTouch.medium - 3, minWidth: 0, paddingVertical: 0 },
  sectionLabel: { color: colors.mutedInk, fontSize: 13, fontWeight: "800", letterSpacing: 0.4 },

  // —— Square 退款结算面板 ——
  refundPanel: { backgroundColor: colors.surface, borderColor: colors.border, borderRadius: posRadius.large, borderWidth: 1, flexShrink: 0, gap: 8, padding: 12 },
  refundPanelHeader: { alignItems: "center", flexDirection: "row", gap: 10 },
  refundPanelCopy: { flex: 1, minWidth: 0 },
  refundTitle: { color: colors.ink, fontSize: 16, fontWeight: "900" },
  refundSubtitle: { color: colors.mutedInk, fontSize: 12.5, lineHeight: 17, marginTop: 2 },
  refundList: { flexGrow: 0, maxHeight: 260 },
  refundRow: { backgroundColor: colors.surfaceAlt, borderLeftWidth: 4, borderRadius: posRadius.medium, gap: 3, marginBottom: 8, padding: 12 },
  refundRowRejected: { borderLeftColor: colors.red },
  refundRowSettling: { borderLeftColor: colors.yellow },
  refundTopLine: { alignItems: "center", flexDirection: "row", gap: 8, justifyContent: "space-between" },
  refundStatus: { flex: 1, fontSize: 14.5, fontWeight: "800" },
  refundAmount: { color: colors.ink, fontSize: 17, fontVariant: ["tabular-nums"], fontWeight: "900" },
  refundMeta: { color: colors.mutedInk, fontSize: 12.5, lineHeight: 18 },
  refundResolve: { gap: 8, marginTop: 8 },

  // —— 支付记录列表：每行高 92，状态和金额最大 ——
  recordScroll: { flex: 1, minHeight: 0 },
  recordList: { flexGrow: 1, gap: 8, paddingBottom: 4 },
  recordRow: { alignItems: "center", backgroundColor: colors.surface, borderColor: colors.border, borderRadius: posRadius.large, borderWidth: 1.5, flexDirection: "row", gap: 12, minHeight: 92, paddingHorizontal: 14, paddingVertical: 12 },
  recordRowActive: { backgroundColor: colors.selectedSurface, borderColor: colors.accent, borderWidth: 2 },
  recordCopy: { flex: 1, minWidth: 0 },
  recordAmount: { color: colors.ink, fontSize: 22, fontVariant: ["tabular-nums"], fontWeight: "900" },
  recordOrder: { color: colors.ink, fontSize: 14, fontWeight: "800", marginTop: 6 },
  recordMeta: { color: colors.mutedInk, fontSize: 12.5, marginTop: 3 },
  pressed: { opacity: 0.72 },

  // —— 详情：结论在上，凭据、明细、处理记录在下，动作固定在底部 ——
  detailsPane: { backgroundColor: colors.surface, borderColor: colors.border, borderRadius: posRadius.large, borderWidth: 1, flex: 1, minHeight: 0, minWidth: 0, overflow: "hidden" },
  detailsScroll: { flex: 1, minHeight: 0 },
  detailsContent: { gap: 12, padding: 16 },
  detailsHeading: { alignItems: "flex-start", flexDirection: "row", gap: 20, justifyContent: "space-between" },
  detailsHeadingCopy: { flex: 1, minWidth: 0 },
  statusHint: { color: colors.mutedInk, fontSize: 16, fontWeight: "700", lineHeight: 22, marginTop: 8 },
  statusHintAlert: { color: colors.red, fontWeight: "800" },
  heroBlock: { alignItems: "flex-end" },
  fieldLabel: { color: colors.mutedInk, fontSize: 12.5, fontWeight: "700" },
  heroAmount: { color: colors.ink, fontSize: 40, fontVariant: ["tabular-nums"], fontWeight: "900", lineHeight: 46 },
  mismatchWarning: { backgroundColor: colors.redSoft, borderRadius: posRadius.medium, color: colors.red, fontSize: 15, fontWeight: "800", lineHeight: 21, padding: 12 },
  factGrid: { backgroundColor: colors.surfaceAlt, borderColor: colors.border, borderRadius: posRadius.medium, borderWidth: 1, columnGap: 16, flexDirection: "row", flexWrap: "wrap", paddingHorizontal: 14, paddingVertical: 10, rowGap: 10 },
  fact: { flexBasis: 100, flexGrow: 1, minWidth: 0 },
  factWide: { flexBasis: 180, flexGrow: 2 },
  factValue: { color: colors.ink, fontSize: 15.5, fontWeight: "800", marginTop: 3 },
  detailsColumns: { alignItems: "flex-start", flexDirection: "row", gap: 20 },
  detailsColumnsCompact: { alignItems: "stretch", flexDirection: "column", gap: 14 },
  itemsColumn: { flex: 1, minWidth: 0 },
  historyColumn: { flexShrink: 0, width: 230 },
  historyColumnCompact: { width: "auto" },
  lineRow: { alignItems: "center", borderBottomColor: colors.border, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: "row", gap: 12, minHeight: 52, paddingVertical: 8 },
  lineName: { color: colors.ink, flex: 1, fontSize: 15.5, fontWeight: "700" },
  lineQuantity: { color: colors.mutedInk, fontSize: 14, fontVariant: ["tabular-nums"], minWidth: 64, textAlign: "right" },
  lineAmount: { color: colors.ink, fontSize: 16, fontVariant: ["tabular-nums"], fontWeight: "800", minWidth: 84, textAlign: "right" },
  eventRow: { flexDirection: "row", gap: 10, paddingVertical: 8 },
  eventMarker: { backgroundColor: colors.blue, borderRadius: 4, height: 8, marginTop: 7, width: 8 },
  eventCopy: { flex: 1, minWidth: 0 },
  eventText: { color: colors.ink, fontSize: 15, fontWeight: "700" },
  eventMeta: { color: colors.mutedInk, fontSize: 12.5, lineHeight: 18, marginTop: 2 },
  detailsActions: { borderTopColor: colors.border, borderTopWidth: 1, flexDirection: "row", gap: 12, padding: 14 },
  detailsActionsCompact: { flexDirection: "column", gap: 10, padding: 10 },
  manualAction: { width: 230 },
  actionError: { backgroundColor: colors.redSoft, color: colors.red, fontSize: 15, fontWeight: "700", lineHeight: 21, paddingHorizontal: 16, paddingVertical: 10 },

  // —— 状态标签 ——
  statusBadge: { alignItems: "center", alignSelf: "flex-start", borderRadius: 13, justifyContent: "center", maxWidth: "100%", minHeight: 26, paddingHorizontal: 10 },
  statusBadgeLarge: { borderRadius: 16, minHeight: 32, paddingHorizontal: 14 },
  statusBadgeText: { fontSize: 13, fontWeight: "800" },
  statusBadgeTextLarge: { fontSize: 15 },
  statusWarning: { backgroundColor: colors.yellowSoft },
  statusWarningText: { color: colors.yellowInk },
  statusDanger: { backgroundColor: colors.redSoft },
  statusDangerText: { color: colors.red },
  statusSuccess: { backgroundColor: colors.greenSoft },
  statusSuccessText: { color: colors.green },

  centeredState: { alignItems: "center", flex: 1, gap: 10, justifyContent: "center", minHeight: 160, padding: 20 },
  emptyText: { color: colors.mutedInk, fontSize: 15, lineHeight: 21, textAlign: "center" },

  // —— 人工核实弹窗：仍是页面内覆盖层，选项、输入、勾选和按钮全部放大 ——
  modalBackdrop: { alignItems: "center", backgroundColor: colors.overlay, flex: 1, justifyContent: "center", padding: 24 },
  modalDismissArea: { ...StyleSheet.absoluteFillObject },
  modalCard: { backgroundColor: colors.surface, borderColor: colors.border, borderRadius: posRadius.sheet, borderWidth: 1, maxHeight: "92%", maxWidth: 880, overflow: "hidden", width: "86%" },
  modalContent: { padding: 22 },
  modalTitle: { color: colors.ink, fontSize: 23, fontWeight: "900" },
  modalSubtitle: { color: colors.mutedInk, fontSize: 14, lineHeight: 20, marginTop: 4 },
  modalOrder: { backgroundColor: colors.surfaceAlt, borderRadius: posRadius.medium, color: colors.ink, fontSize: 16, fontWeight: "800", marginTop: 12, overflow: "hidden", padding: 12 },
  formLabel: { color: colors.ink, fontSize: 15, fontWeight: "800", marginBottom: 8, marginTop: 16 },
  findingGrid: { flexDirection: "row", gap: 10 },
  findingOption: { alignItems: "flex-start", backgroundColor: colors.surface, borderColor: colors.borderStrong, borderRadius: posRadius.large, borderWidth: 1.5, flex: 1, flexDirection: "row", gap: 10, minHeight: 104, padding: 14 },
  findingOptionSelected: { backgroundColor: colors.selectedSurface, borderColor: colors.accent, borderWidth: 2 },
  findingOptionDisabled: { backgroundColor: colors.disabledSurface, borderColor: colors.disabledBorder },
  disabledText: { color: colors.disabledInk },
  radio: { alignItems: "center", borderColor: colors.mutedInk, borderRadius: 12, borderWidth: 2, height: 24, justifyContent: "center", marginTop: 1, width: 24 },
  radioSelected: { borderColor: colors.accent },
  radioDot: { backgroundColor: colors.accent, borderRadius: 6, height: 12, width: 12 },
  findingCopy: { flex: 1, minWidth: 0 },
  findingLabel: { color: colors.ink, fontSize: 17, fontWeight: "900" },
  findingHint: { color: colors.mutedInk, fontSize: 13, lineHeight: 18, marginTop: 4 },
  formField: { marginTop: 2 },
  textInput: { backgroundColor: colors.surface, borderColor: colors.borderStrong, borderRadius: posRadius.medium, borderWidth: 1.5, color: colors.ink, fontSize: 16, minHeight: posTouch.medium, paddingHorizontal: 14, paddingVertical: 10 },
  amountInput: { fontSize: 22, fontVariant: ["tabular-nums"], fontWeight: "800", minHeight: posTouch.button },
  noteInput: { minHeight: 96, textAlignVertical: "top" },
  presetRow: { flexDirection: "row", flexWrap: "wrap", gap: 8, marginTop: 10 },
  inputError: { borderColor: colors.red },
  validationText: { color: colors.red, fontSize: 13, fontWeight: "700", marginTop: 6 },
  checkboxRow: { alignItems: "center", backgroundColor: colors.surface, borderColor: colors.borderStrong, borderRadius: posRadius.medium, borderWidth: 1.5, flexDirection: "row", gap: 12, minHeight: posTouch.medium, paddingHorizontal: 14, paddingVertical: 10 },
  checkboxRowChecked: { backgroundColor: colors.selectedSurface },
  checkbox: { alignItems: "center", borderColor: colors.mutedInk, borderRadius: 6, borderWidth: 2, height: 28, justifyContent: "center", width: 28 },
  checkboxChecked: { backgroundColor: colors.inverseSurface, borderColor: colors.inverseSurface },
  checkboxLabel: { color: colors.ink, flex: 1, fontSize: 15, fontWeight: "600", lineHeight: 21 },
  authorizationHint: { color: colors.blue, fontSize: 13, lineHeight: 18, marginTop: 8 },
  auditNotice: { color: colors.mutedInk, fontSize: 12.5, lineHeight: 18, marginTop: 6 },
  modalActions: { flexDirection: "row", gap: 12, marginTop: 20 },
}));
