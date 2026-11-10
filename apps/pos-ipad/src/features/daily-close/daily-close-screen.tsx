import {
  useCallback,
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type Ref,
} from "react";
import { useTranslation } from "react-i18next";
import {
  Keyboard,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  Text,
  View,
  type FocusEvent,
  type TextInput,
} from "react-native";

import {
  dailyCloseText,
  resolveDailyCloseLocale,
  type DailyCloseLocale,
} from "./daily-close-copy";
import {
  type DailyClosePresenter,
  type DailyCloseState,
  type DailyCloseStatusCode,
} from "./daily-close-presenter";

import type {
  AudCashDenominationCents,
  DailyCloseArchive,
  DailyCloseTenderBreakdown,
} from "@/core/contracts";
import { PosButton } from "@/ui/controls/pos-button";
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import { PosPill } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosTextInput } from "@/ui/controls/pos-text-input";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

export const DAILY_CLOSE_MIN_TOUCH_TARGET = 44;
export const DAILY_CLOSE_KEYBOARD_AVOIDER_ENABLED = Platform.OS !== "ios";
export const DAILY_CLOSE_KEYBOARD_AVOIDING_BEHAVIOR = "height";

const DAILY_CLOSE_KEYBOARD_SCROLL_OFFSET = 16;

export type DailyCloseScreenPresenter = Pick<
  DailyClosePresenter,
  | "getState"
  | "load"
  | "reprintSelected"
  | "saveAndPrint"
  | "selectArchive"
  | "setBusinessDate"
  | "setCount"
  | "showCount"
  | "showHistory"
  | "subscribe"
> &
  Readonly<{ getState(): DailyCloseState }>;

type DailyCloseScreenProps = Readonly<{
  onBack?(): void;
  presenter: DailyCloseScreenPresenter;
}>;

export function DailyCloseScreen({ onBack, presenter }: DailyCloseScreenProps) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const [businessDate, setBusinessDate] = useState(state.businessDate);
  const { i18n } = useTranslation();
  const locale = resolveDailyCloseLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const firstCountInputRef = useRef<TextInput>(null);
  const summaryScrollRef = useRef<ScrollView>(null);

  const revealSummaryInput = useCallback((event: FocusEvent) => {
    // 自动 inset 负责腾出键盘空间；这里再把实际焦点精确滚到键盘上沿。
    summaryScrollRef.current?.scrollResponderScrollNativeHandleToKeyboard(
      event.target,
      DAILY_CLOSE_KEYBOARD_SCROLL_OFFSET,
      true,
    );
  }, []);

  useEffect(() => {
    setBusinessDate(state.businessDate);
  }, [state.businessDate]);

  useEffect(() => {
    void presenter.load();
  }, [presenter]);

  useEffect(() => {
    if (state.activePane === "history") Keyboard.dismiss();
  }, [state.activePane]);

  const refresh = () => {
    if (!presenter.setBusinessDate(businessDate)) return;
    void presenter.load();
  };

  const showCount = () => {
    presenter.showCount();
    // 重新取得第一响应者，确保从 History 返回或重复点 Count 时再次请求系统键盘。
    firstCountInputRef.current?.blur();
    firstCountInputRef.current?.focus();
  };

  return (
    <PosScreenFrame
      active="daily-close"
      header={
        <PosPageHeader
          actions={
            <>
              <Text style={styles.dateLabel}>
                {dailyCloseText(locale, "businessDate.accessibility")}
              </Text>
              <PosDatePickerField
                accessibilityLabel={dailyCloseText(
                  locale,
                  "businessDate.accessibility",
                )}
                disabled={state.busy}
                locale={locale}
                onChange={(value) => {
                  if (value) setBusinessDate(value);
                }}
                testID="daily-close-business-date"
                value={businessDate}
              />
              <PosButton
                disabled={state.busy}
                icon="refresh"
                label={
                  state.kind === "loading"
                    ? dailyCloseText(locale, "action.refreshing")
                    : dailyCloseText(locale, "action.refresh")
                }
                onPress={refresh}
                size="medium"
                testID="daily-close-refresh"
                tone="quiet"
              />
              <PaneSwitch
                activePane={state.activePane}
                disabled={state.busy}
                locale={locale}
                onShowCount={showCount}
                onShowHistory={() => presenter.showHistory()}
              />
            </>
          }
          backLabel={dailyCloseText(locale, "action.back")}
          backTestID="daily-close-back"
          onBack={onBack}
          subtitle={dailyCloseText(locale, "subtitle")}
          title={dailyCloseText(locale, "title")}
        />
      }
      testID="daily-close-screen"
    >
      <KeyboardAvoidingView
        behavior={DAILY_CLOSE_KEYBOARD_AVOIDING_BEHAVIOR}
        // iOS 由 ScrollView 的 keyboard inset 负责避让，避免重复计算同一键盘高度。
        enabled={DAILY_CLOSE_KEYBOARD_AVOIDER_ENABLED}
        style={styles.keyboardAvoider}
        testID="daily-close-keyboard-avoider"
      >
        <View style={styles.page}>
          {state.statusCode ? (
            <StatusBanner locale={locale} statusCode={state.statusCode} />
          ) : null}

          <View style={styles.workspace} testID="daily-close-workspace">
            <View style={styles.mainColumn}>
              {/*
                汇总与点钞放在同一个滚动容器里：软键盘弹出时整列一起上移，
                才能把正在输入的面额露在键盘上方（面额输入框不能移出这个容器）。
              */}
              <ScrollView
                automaticallyAdjustKeyboardInsets
                contentContainerStyle={styles.mainScrollContent}
                keyboardDismissMode="interactive"
                keyboardShouldPersistTaps="handled"
                ref={summaryScrollRef}
                style={styles.mainScroll}
                testID="daily-close-summary-scroll"
              >
                <SummaryCard locale={locale} state={state} />

                {state.access.canSave ? (
                  <View style={[styles.card, styles.countCard]}>
                    <View style={styles.countHeader}>
                      <Text style={styles.cardTitle}>
                        {dailyCloseText(locale, "count.title")}
                      </Text>
                      <Text numberOfLines={3} style={styles.countHint}>
                        {dailyCloseText(locale, "count.hint")}
                      </Text>
                      <PosPill
                        label={dailyCloseText(locale, "count.notes", {
                          amount: money(state.notesSubtotalCents),
                        })}
                        style={styles.countPill}
                      />
                      <PosPill
                        label={dailyCloseText(locale, "count.coins", {
                          amount: money(state.coinsSubtotalCents),
                        })}
                        style={styles.countPill}
                      />
                    </View>

                    <View style={styles.denominationGrid}>
                      {state.counts.map((count, index) => (
                        <DenominationField
                          autoFocus={
                            index === 0 &&
                            state.activePane === "count" &&
                            !state.busy
                          }
                          count={count.quantity}
                          denominationCents={count.denominationCents}
                          disabled={state.busy}
                          inputRef={
                            index === 0 ? firstCountInputRef : undefined
                          }
                          key={count.denominationCents}
                          locale={locale}
                          onChange={(quantity) =>
                            presenter.setCount(
                              count.denominationCents,
                              quantity,
                            )
                          }
                          onFocus={revealSummaryInput}
                          subtotalCents={count.subtotalCents}
                        />
                      ))}
                    </View>
                  </View>
                ) : (
                  <View style={styles.permissionNote}>
                    <Text style={styles.permissionText}>
                      {dailyCloseText(locale, "permission.viewOnly")}
                    </Text>
                  </View>
                )}
              </ScrollView>

              {/* 保存栏固定在左栏底部，不随汇总和面额滚动。 */}
              {state.access.canSave ? (
                <View style={styles.saveBar}>
                  <View>
                    <Text style={styles.saveLabel}>
                      {dailyCloseText(locale, "count.countedCash")}
                    </Text>
                    <Text
                      adjustsFontSizeToFit
                      numberOfLines={1}
                      style={styles.countedAmount}
                    >
                      {money(state.countedCashCents)}
                    </Text>
                  </View>
                  <View style={styles.varianceGroup}>
                    <Text style={styles.saveLabel}>
                      {dailyCloseText(locale, "count.varianceLabel")}
                    </Text>
                    <Text
                      adjustsFontSizeToFit
                      numberOfLines={1}
                      style={[
                        styles.varianceAmount,
                        state.varianceCents === 0
                          ? styles.varianceBalanced
                          : styles.varianceDifferent,
                      ]}
                    >
                      {signedMoney(state.varianceCents)}
                    </Text>
                  </View>
                  <PosButton
                    disabled={
                      state.busy || !state.summary || state.kind !== "ready"
                    }
                    icon="content-save-check-outline"
                    label={
                      state.busy
                        ? dailyCloseText(locale, "action.working")
                        : dailyCloseText(locale, "action.save")
                    }
                    onPress={() => void presenter.saveAndPrint()}
                    size="large"
                    style={styles.saveButton}
                    testID="daily-close-save"
                    tone="primary"
                  />
                </View>
              ) : null}
            </View>

            <View style={[styles.card, styles.archivePanel]}>
              <Text style={styles.cardTitle}>
                {dailyCloseText(locale, "history.title")}
              </Text>
              <Text style={styles.archiveHint}>
                {dailyCloseText(locale, "history.hint")}
              </Text>

              <ScrollView
                contentContainerStyle={styles.archiveScrollContent}
                style={styles.archiveScroll}
              >
                {state.archives.length === 0 ? (
                  <Text style={styles.historyEmpty}>
                    {dailyCloseText(locale, "history.empty")}
                  </Text>
                ) : (
                  state.archives.map((archive) => (
                    <ArchiveRow
                      archive={archive}
                      key={archive.closeId}
                      onPress={() => presenter.selectArchive(archive.closeId)}
                      selected={
                        state.selectedArchive?.closeId === archive.closeId
                      }
                    />
                  ))
                )}

                {state.selectedArchive ? (
                  <ArchiveDetails
                    archive={state.selectedArchive}
                    locale={locale}
                  />
                ) : null}
              </ScrollView>

              {state.access.canReprint ? (
                <PosButton
                  disabled={state.busy || !state.selectedArchive}
                  icon="printer-outline"
                  label={
                    state.busy
                      ? dailyCloseText(locale, "action.working")
                      : dailyCloseText(locale, "action.reprint")
                  }
                  onPress={() => void presenter.reprintSelected()}
                  size="button"
                  testID="daily-close-reprint"
                />
              ) : null}
            </View>
          </View>
        </View>
      </KeyboardAvoidingView>
    </PosScreenFrame>
  );
}

export function DailyCloseUnavailableScreen({
  onBack,
}: Readonly<{ onBack(): void }>) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveDailyCloseLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  return (
    <PosScreenFrame active="daily-close">
      <View style={styles.unavailable}>
        <View style={[styles.card, styles.unavailableCard]}>
          <Text style={styles.unavailableTitle}>
            {dailyCloseText(locale, "unavailable.title")}
          </Text>
          <Text style={styles.unavailableBody}>
            {dailyCloseText(locale, "unavailable.subtitle")}
          </Text>
          <PosButton
            icon="arrow-left"
            label={dailyCloseText(locale, "unavailable.back")}
            onPress={onBack}
            size="large"
            sound="navigate"
            style={styles.unavailableAction}
            testID="daily-close-unavailable-back"
            tone="primary"
          />
        </View>
      </View>
    </PosScreenFrame>
  );
}

/**
 * 「点钞 / 历史」切换。外观与 PosSegmented 一致，但已选中的一段再点一次仍会回调：
 * 收银员收起键盘后再点「点钞」要能重新唤起键盘，而 PosSegmented 对已选中段不回调。
 */
function PaneSwitch({
  activePane,
  disabled,
  locale,
  onShowCount,
  onShowHistory,
}: Readonly<{
  activePane: DailyCloseState["activePane"];
  disabled: boolean;
  locale: DailyCloseLocale;
  onShowCount(): void;
  onShowHistory(): void;
}>) {
  usePosThemeVersion();
  const options = [
    {
      label: dailyCloseText(locale, "action.count"),
      onPress: onShowCount,
      pane: "count",
      testID: "daily-close-show-count",
    },
    {
      label: dailyCloseText(locale, "action.history"),
      onPress: onShowHistory,
      pane: "history",
      testID: "daily-close-show-history",
    },
  ] as const;
  return (
    <View accessibilityRole="radiogroup" style={styles.paneSwitch}>
      {options.map((option, index) => {
        const selected = option.pane === activePane;
        return (
          <PosPressable
            accessibilityLabel={option.label}
            accessibilityRole="radio"
            accessibilityState={{ checked: selected, disabled, selected }}
            disabled={disabled}
            key={option.pane}
            onPress={option.onPress}
            sound="navigate"
            style={({ pressed }) => [
              styles.paneSegment,
              index > 0 && styles.paneSegmentDivider,
              selected && styles.paneSegmentSelected,
              pressed && !selected && styles.paneSegmentPressed,
            ]}
            testID={option.testID}
          >
            <Text
              adjustsFontSizeToFit
              minimumFontScale={0.8}
              numberOfLines={1}
              style={[
                styles.paneSegmentLabel,
                {
                  color: selected
                    ? posColors.onInverse
                    : disabled
                      ? posColors.disabledInk
                      : posColors.mutedInk,
                },
              ]}
            >
              {option.label}
            </Text>
          </PosPressable>
        );
      })}
    </View>
  );
}

/** 当日汇总：左边付款方式表（有分期时再加一张分期表），右边 4 个指标。 */
function SummaryCard({
  locale,
  state,
}: Readonly<{ locale: DailyCloseLocale; state: DailyCloseState }>) {
  usePosThemeVersion();
  const summary = state.summary;
  return (
    <View style={[styles.card, styles.summaryCard]}>
      <View style={styles.summaryTables}>
        <View style={styles.summaryTitleRow}>
          <Text style={styles.cardTitle}>
            {dailyCloseText(locale, "summary.title")}
          </Text>
          <Text numberOfLines={2} style={styles.summaryHint}>
            {dailyCloseText(locale, "summary.hint")}
          </Text>
        </View>

        {summary ? (
          <>
            <TenderTable
              firstColumnLabel={dailyCloseText(locale, "tender.method")}
              locale={locale}
              rowTestIDPrefix="daily-close-tender"
              tenders={summary.tenders}
            />
            {summary.installmentPaymentCount > 0 ? (
              <TenderTable
                firstColumnLabel={dailyCloseText(locale, "installment.title", {
                  count: summary.installmentPaymentCount,
                })}
                locale={locale}
                rowTestIDPrefix="daily-close-installment"
                tenders={summary.installmentPayments}
                testID="daily-close-installments"
              />
            ) : null}
          </>
        ) : (
          <View style={styles.emptyCard}>
            <Text style={styles.emptyTitle}>
              {state.kind === "loading"
                ? dailyCloseText(locale, "summary.loading")
                : dailyCloseText(locale, "summary.empty")}
            </Text>
          </View>
        )}
      </View>

      {summary ? (
        <View style={styles.metrics}>
          <Metric
            label={dailyCloseText(locale, "metric.orders")}
            value={String(summary.orderCount)}
          />
          <Metric
            label={dailyCloseText(locale, "metric.returnQuantity")}
            value={summary.returnQuantity}
          />
          <Metric
            label={dailyCloseText(locale, "metric.cashRounding")}
            value={signedMoney(summary.cashRoundingCents)}
          />
          {/* 应有现金是点钞时要对的数，加粗描边突出。 */}
          <Metric
            emphasized
            label={dailyCloseText(locale, "metric.expectedCash")}
            value={money(summary.expectedCashCents)}
          />
        </View>
      ) : null}
    </View>
  );
}

function TenderTable({
  firstColumnLabel,
  locale,
  rowTestIDPrefix,
  tenders,
  testID,
}: Readonly<{
  firstColumnLabel: string;
  locale: DailyCloseLocale;
  rowTestIDPrefix: string;
  tenders: readonly DailyCloseTenderBreakdown[];
  testID?: string;
}>) {
  usePosThemeVersion();
  return (
    <View testID={testID}>
      <View style={styles.tenderHeader}>
        <Text
          numberOfLines={2}
          style={[styles.tenderHeadText, styles.tenderMethodCell]}
        >
          {firstColumnLabel}
        </Text>
        <Text
          numberOfLines={1}
          style={[styles.tenderHeadText, styles.tenderSalesCell]}
        >
          {dailyCloseText(locale, "tender.sales")}
        </Text>
        <Text
          numberOfLines={1}
          style={[styles.tenderHeadText, styles.tenderRefundCell]}
        >
          {dailyCloseText(locale, "tender.refunds")}
        </Text>
        <Text
          numberOfLines={1}
          style={[styles.tenderHeadText, styles.tenderNetCell]}
        >
          {dailyCloseText(locale, "tender.net")}
        </Text>
      </View>
      {tenders.map((tender) => (
        <TenderRow
          key={tender.method}
          locale={locale}
          tender={tender}
          testID={`${rowTestIDPrefix}-${tender.method}`}
        />
      ))}
    </View>
  );
}

function TenderRow({
  locale,
  tender,
  testID,
}: Readonly<{
  locale: DailyCloseLocale;
  tender: DailyCloseTenderBreakdown;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.tenderRow} testID={testID}>
      <Text
        numberOfLines={2}
        style={[styles.tenderText, styles.tenderMethodCell]}
      >
        {tenderName(tender.method, locale)}
      </Text>
      <Text
        adjustsFontSizeToFit
        numberOfLines={1}
        style={[styles.tenderText, styles.tenderAmount, styles.tenderSalesCell]}
      >
        {money(tender.salesCents)}
      </Text>
      <Text
        adjustsFontSizeToFit
        numberOfLines={1}
        style={[
          styles.tenderText,
          styles.tenderAmount,
          styles.tenderRefundCell,
          // 有退款时标红，没有退款的 $0.00 保持普通颜色。
          tender.refundCents !== 0 && styles.tenderRefunded,
        ]}
      >
        {money(tender.refundCents)}
      </Text>
      <Text
        adjustsFontSizeToFit
        numberOfLines={1}
        style={[
          styles.tenderText,
          styles.tenderAmount,
          styles.tenderNetCell,
          styles.tenderNet,
        ]}
      >
        {money(tender.netCents)}
      </Text>
    </View>
  );
}

function Metric({
  emphasized = false,
  label,
  value,
}: Readonly<{ emphasized?: boolean; label: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={[styles.metric, emphasized && styles.metricEmphasized]}>
      <Text numberOfLines={1} style={styles.metricLabel}>
        {label}
      </Text>
      <Text adjustsFontSizeToFit numberOfLines={1} style={styles.metricValue}>
        {value}
      </Text>
    </View>
  );
}

function DenominationField({
  autoFocus,
  count,
  denominationCents,
  disabled,
  inputRef,
  locale,
  onChange,
  onFocus,
  subtotalCents,
}: Readonly<{
  autoFocus: boolean;
  count: number;
  denominationCents: AudCashDenominationCents;
  disabled: boolean;
  inputRef?: Ref<TextInput> | undefined;
  locale: DailyCloseLocale;
  onChange(quantity: number): void;
  onFocus(event: FocusEvent): void;
  subtotalCents: number;
}>) {
  usePosThemeVersion();
  // 仅用于高亮当前正在输入的面额格，不参与任何业务判断。
  const [focused, setFocused] = useState(false);
  return (
    <View
      style={styles.denominationCell}
      testID={`daily-close-denomination-${denominationCents}`}
    >
      <View
        style={[styles.denomination, focused && styles.denominationFocused]}
        testID={`daily-close-denomination-box-${denominationCents}`}
      >
        <View style={styles.denominationCopy}>
          <Text numberOfLines={1} style={styles.denominationLabel}>
            {denominationLabel(denominationCents)}
          </Text>
          <Text
            adjustsFontSizeToFit
            numberOfLines={1}
            style={styles.denominationSubtotal}
          >
            {money(subtotalCents)}
          </Text>
        </View>
        <PosTextInput
          accessibilityLabel={dailyCloseText(
            locale,
            "denomination.accessibility",
            {
              denomination: denominationLabel(denominationCents),
            },
          )}
          autoFocus={autoFocus}
          editable={!disabled}
          keyboardType="number-pad"
          onBlur={() => setFocused(false)}
          onChangeText={(value) => {
            if (value === "") {
              onChange(0);
              return;
            }
            if (!/^\d+$/.test(value)) return;
            const quantity = Number(value);
            if (Number.isSafeInteger(quantity)) onChange(quantity);
          }}
          onFocus={(event) => {
            setFocused(true);
            onFocus(event);
          }}
          ref={inputRef}
          selectTextOnFocus
          showSoftInputOnFocus
          style={[styles.countInput, focused && styles.countInputFocused]}
          testID={`daily-close-count-${denominationCents}`}
          value={String(count)}
        />
      </View>
    </View>
  );
}

function ArchiveRow({
  archive,
  onPress,
  selected,
}: Readonly<{
  archive: DailyCloseArchive;
  onPress(): void;
  selected: boolean;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.archiveRow, selected && styles.archiveRowSelected]}
      testID={`daily-close-history-${archive.closeId}`}
    >
      <View style={styles.archiveCopy}>
        <Text numberOfLines={1} style={styles.archiveTitle}>
          {archive.businessDate}{" "}
          <Text style={styles.archiveId}>{shortId(archive.closeId)}</Text>
        </Text>
        <Text numberOfLines={1} style={styles.archiveMeta}>
          {archive.savedAtIso.replace("T", " ").slice(0, 19)} ·{" "}
          {archive.savedCashierName}
        </Text>
      </View>
      <Text style={styles.archiveAmount}>
        {money(archive.countedCashCents)}
      </Text>
    </PosPressable>
  );
}

function ArchiveDetails({
  archive,
  locale,
}: Readonly<{
  archive: DailyCloseArchive;
  locale: DailyCloseLocale;
}>) {
  usePosThemeVersion();
  return (
    <>
      <Text style={styles.detailTitle}>
        {dailyCloseText(locale, "archive.selected")}
      </Text>
      <View style={styles.archiveDetails}>
        <DetailRow
          first
          label={dailyCloseText(locale, "archive.label.terminal")}
          value={archive.deviceCode}
        />
        <DetailRow
          label={dailyCloseText(locale, "archive.label.cashier")}
          value={archive.savedCashierName}
        />
        <DetailRow
          label={dailyCloseText(locale, "archive.label.ordersReturns")}
          value={`${archive.orderCount} · ${archive.returnQuantity}`}
        />
        {archive.installmentPaymentCount > 0 ? (
          <DetailRow
            label={dailyCloseText(locale, "archive.label.installments")}
            value={dailyCloseText(locale, "archive.value.installments", {
              count: archive.installmentPaymentCount,
              amount: money(
                archive.installmentPayments.reduce(
                  (total, payment) => total + payment.netCents,
                  0,
                ),
              ),
            })}
          />
        ) : null}
        <DetailRow
          label={dailyCloseText(locale, "archive.label.rounding")}
          value={signedMoney(archive.cashRoundingCents)}
        />
        <DetailRow
          label={dailyCloseText(locale, "archive.label.expected")}
          value={money(archive.expectedCashCents)}
        />
        <DetailRow
          label={dailyCloseText(locale, "archive.label.counted")}
          strong
          value={money(archive.countedCashCents)}
        />
        <DetailRow
          label={dailyCloseText(locale, "count.varianceLabel")}
          strong
          tone={archive.varianceCents === 0 ? "balanced" : "different"}
          value={signedMoney(archive.varianceCents)}
        />
      </View>
    </>
  );
}

function DetailRow({
  first = false,
  label,
  strong = false,
  tone,
  value,
}: Readonly<{
  first?: boolean;
  label: string;
  strong?: boolean;
  tone?: "balanced" | "different";
  value: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.detailRow, !first && styles.detailRowDivider]}>
      <Text numberOfLines={1} style={styles.detailLabel}>
        {label}
      </Text>
      <Text
        adjustsFontSizeToFit
        numberOfLines={1}
        style={[
          styles.detailValue,
          strong && styles.detailValueStrong,
          tone === "balanced" && styles.varianceBalanced,
          tone === "different" && styles.varianceDifferent,
        ]}
      >
        {value}
      </Text>
    </View>
  );
}

function StatusBanner({
  locale,
  statusCode,
}: Readonly<{
  locale: DailyCloseLocale;
  statusCode: DailyCloseStatusCode;
}>) {
  usePosThemeVersion();
  const success =
    statusCode === "saved-printed" || statusCode === "reprint-printed";
  return (
    <View
      accessibilityLiveRegion="polite"
      style={[
        styles.status,
        success ? styles.statusSuccess : styles.statusWarning,
      ]}
      testID={`daily-close-status-${statusCode}`}
    >
      <Text style={styles.statusText}>{statusMessage(statusCode, locale)}</Text>
    </View>
  );
}

function statusMessage(
  statusCode: DailyCloseStatusCode,
  locale: DailyCloseLocale,
): string {
  return dailyCloseText(locale, `status.${statusCode}`);
}

function tenderName(
  method: DailyCloseTenderBreakdown["method"],
  locale: DailyCloseLocale,
): string {
  return dailyCloseText(locale, `method.${method}`);
}

function denominationLabel(value: AudCashDenominationCents): string {
  return value >= 100 ? `$${value / 100}` : `${value}c`;
}

function money(cents: number): string {
  const sign = cents < 0 ? "-" : "";
  const value = Math.abs(cents);
  return `${sign}$${Math.floor(value / 100)}.${String(value % 100).padStart(
    2,
    "0",
  )}`;
}

function signedMoney(cents: number): string {
  return cents > 0 ? `+${money(cents)}` : money(cents);
}

function shortId(closeId: string): string {
  return closeId.length <= 12 ? closeId : closeId.slice(0, 12);
}

const styles = createPosStyles((colors) => ({
  keyboardAvoider: {
    flex: 1,
  },
  page: {
    flex: 1,
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  dateLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
  },
  mainColumn: {
    flex: 1,
    gap: 12,
    minHeight: 0,
    minWidth: 0,
  },
  mainScroll: {
    flex: 1,
  },
  mainScrollContent: {
    // 内容不满一屏时让点钞卡片撑到保存栏上沿。
    flexGrow: 1,
    gap: 12,
  },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    padding: 14,
  },
  cardTitle: {
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
  },
  summaryCard: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 18,
  },
  summaryTables: {
    flex: 1,
    gap: 6,
    minWidth: 0,
  },
  summaryTitleRow: {
    alignItems: "baseline",
    flexDirection: "row",
    gap: 10,
  },
  summaryHint: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 12.5,
    fontWeight: "600",
  },
  tenderHeader: {
    alignItems: "center",
    flexDirection: "row",
    minHeight: 26,
  },
  tenderRow: {
    alignItems: "center",
    borderTopColor: colors.border,
    borderTopWidth: 1,
    flexDirection: "row",
    minHeight: 38,
  },
  tenderHeadText: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "800",
  },
  tenderText: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
  },
  tenderAmount: {
    fontVariant: ["tabular-nums"],
  },
  tenderMethodCell: {
    flex: 1,
    minWidth: 0,
  },
  tenderSalesCell: {
    textAlign: "right",
    width: 98,
  },
  tenderRefundCell: {
    textAlign: "right",
    width: 90,
  },
  tenderNetCell: {
    textAlign: "right",
    width: 98,
  },
  tenderRefunded: {
    color: colors.red,
  },
  tenderNet: {
    fontWeight: "900",
  },
  metrics: {
    alignContent: "stretch",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    width: 290,
  },
  metric: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 64,
    paddingHorizontal: 12,
    paddingVertical: 8,
    width: 141,
  },
  metricEmphasized: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderWidth: 2,
  },
  metricLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "800",
  },
  metricValue: {
    color: colors.ink,
    fontSize: 24,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    marginTop: 2,
  },
  emptyCard: {
    alignItems: "center",
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderStyle: "dashed",
    borderWidth: 1,
    padding: 24,
  },
  emptyTitle: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
    textAlign: "center",
  },
  countCard: {
    flexGrow: 1,
    gap: 8,
  },
  countHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  countHint: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 16,
  },
  countPill: {
    alignSelf: "center",
  },
  denominationGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    // 每格自带 4 的内边距，这里用负边距抵消，得到 8 的格间距且与卡片内边对齐。
    marginHorizontal: -4,
  },
  denominationCell: {
    padding: 4,
    width: "25%",
  },
  denomination: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 8,
    height: 72,
    paddingLeft: 12,
    paddingRight: 8,
  },
  denominationFocused: {
    borderColor: colors.accent,
  },
  denominationCopy: {
    flex: 1,
    minWidth: 0,
  },
  denominationLabel: {
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
  },
  denominationSubtotal: {
    color: colors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  countInput: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.small,
    borderWidth: 1.5,
    color: colors.ink,
    fontSize: 22,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    height: 52,
    minHeight: DAILY_CLOSE_MIN_TOUCH_TARGET,
    paddingHorizontal: 4,
    paddingVertical: 0,
    textAlign: "center",
    width: 74,
  },
  countInputFocused: {
    backgroundColor: colors.surface,
    borderColor: colors.accent,
    borderWidth: 2.5,
  },
  saveBar: {
    alignItems: "center",
    backgroundColor: colors.accentSoft,
    borderRadius: posRadius.large,
    flexDirection: "row",
    gap: 22,
    paddingLeft: 18,
    paddingRight: 12,
    paddingVertical: 10,
  },
  saveLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  countedAmount: {
    color: colors.ink,
    fontSize: 40,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    lineHeight: 44,
  },
  varianceGroup: {
    flex: 1,
    minWidth: 0,
  },
  varianceAmount: {
    fontSize: 28,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    lineHeight: 44,
  },
  varianceBalanced: {
    color: colors.green,
  },
  varianceDifferent: {
    color: colors.red,
  },
  saveButton: {
    width: 270,
  },
  permissionNote: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: posRadius.large,
    borderWidth: 1,
    padding: 16,
  },
  permissionText: {
    color: colors.blue,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 21,
  },
  archivePanel: {
    gap: 8,
    width: 340,
  },
  archiveHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 17,
  },
  archiveScroll: {
    flex: 1,
  },
  archiveScrollContent: {
    gap: 8,
    paddingBottom: 4,
  },
  historyEmpty: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
    paddingVertical: 24,
    textAlign: "center",
  },
  archiveRow: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    justifyContent: "space-between",
    minHeight: 78,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  archiveRowSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
    borderWidth: 2,
  },
  archiveCopy: {
    flex: 1,
    gap: 3,
    minWidth: 0,
  },
  archiveTitle: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  archiveId: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "700",
  },
  archiveMeta: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
  },
  archiveAmount: {
    color: colors.ink,
    fontSize: 18,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  detailTitle: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
    marginTop: 6,
  },
  archiveDetails: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    paddingHorizontal: 14,
    paddingVertical: 4,
  },
  detailRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    minHeight: 34,
  },
  detailRowDivider: {
    borderTopColor: colors.border,
    borderTopWidth: 1,
  },
  detailLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
  },
  detailValue: {
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    textAlign: "right",
  },
  detailValueStrong: {
    fontWeight: "900",
  },
  paneSwitch: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    minHeight: posTouch.medium,
    overflow: "hidden",
    width: 190,
  },
  paneSegment: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    minHeight: posTouch.medium,
    paddingHorizontal: 12,
  },
  paneSegmentDivider: {
    borderLeftColor: colors.border,
    borderLeftWidth: 1,
  },
  paneSegmentSelected: {
    backgroundColor: colors.inverseSurface,
  },
  paneSegmentPressed: {
    backgroundColor: colors.surfaceAlt,
  },
  paneSegmentLabel: {
    fontSize: 15.5,
    fontWeight: "800",
  },
  status: {
    borderRadius: posRadius.medium,
    borderWidth: 1,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  statusSuccess: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
  },
  statusWarning: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
  },
  statusText: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  unavailable: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    padding: 32,
  },
  unavailableCard: {
    alignItems: "center",
    gap: 12,
    maxWidth: 560,
    padding: 28,
    width: "100%",
  },
  unavailableTitle: {
    color: colors.ink,
    fontSize: 23,
    fontWeight: "900",
    textAlign: "center",
  },
  unavailableBody: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 22,
    textAlign: "center",
  },
  unavailableAction: {
    alignSelf: "stretch",
    marginTop: 8,
  },
}));
