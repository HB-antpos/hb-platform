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
  StyleSheet,
  Text,
  View,
  type FocusEvent,
  type StyleProp,
  type TextInput,
  type ViewStyle,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

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
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosTextInput } from "@/ui/controls/pos-text-input";
import { HandheldStateSurface } from "@/ui/handheld/handheld-design-states";
import { posColors } from "@/ui/theme";

export const DAILY_CLOSE_MIN_TOUCH_TARGET = 48;
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
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const [businessDate, setBusinessDate] = useState(state.businessDate);
  const [view, setView] = useState<"count" | "summary" | "history">(
    state.access.canSave ? "count" : "summary",
  );
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

  useEffect(() => {
    if (view === "count") firstCountInputRef.current?.focus();
  }, [view]);

  const refresh = () => {
    if (!presenter.setBusinessDate(businessDate)) return;
    void presenter.load();
  };

  const showCount = () => {
    const isAlreadyCounting = view === "count";
    presenter.showCount();
    setView("count");
    if (isAlreadyCounting) {
      // 当前输入仍已挂载时重新取得第一响应者；从其他视图返回则交给挂载后的 effect。
      firstCountInputRef.current?.blur();
      firstCountInputRef.current?.focus();
    }
  };

  const showHistory = () => {
    presenter.showHistory();
    setView("history");
  };

  return (
    <SafeAreaView style={styles.safeArea} testID="daily-close-screen">
      <KeyboardAvoidingView
        behavior={DAILY_CLOSE_KEYBOARD_AVOIDING_BEHAVIOR}
        // iOS 由 ScrollView 的 keyboard inset 负责避让，避免重复计算同一键盘高度。
        enabled={DAILY_CLOSE_KEYBOARD_AVOIDER_ENABLED}
        style={styles.keyboardAvoider}
        testID="daily-close-keyboard-avoider"
      >
        <View style={styles.page}>
          <View style={styles.header}>
            <View style={styles.titleGroup}>
              <Text style={styles.eyebrow}>
                {dailyCloseText(locale, "eyebrow")}
              </Text>
              <Text style={styles.title}>
                {dailyCloseText(locale, "title")}
              </Text>
              <Text style={styles.subtitle}>
                {dailyCloseText(locale, "subtitle")}
              </Text>
            </View>
            <View style={styles.headerActions}>
              {onBack ? (
                <ActionButton
                  label={dailyCloseText(locale, "action.back")}
                  onPress={onBack}
                  sound="navigate"
                  testID="daily-close-back"
                  tone="quiet"
                />
              ) : null}
              <ActionButton
                disabled={state.busy}
                label={dailyCloseText(locale, "action.count")}
                onPress={showCount}
                sound="navigate"
                selected={view === "count"}
                testID="daily-close-show-count"
                tone="secondary"
              />
              <ActionButton
                disabled={state.busy}
                label={dailyCloseText(locale, "action.history")}
                onPress={showHistory}
                sound="navigate"
                selected={view === "history"}
                testID="daily-close-show-history"
                tone="secondary"
              />
            </View>
          </View>

          {state.statusCode ? (
            <StatusBanner locale={locale} statusCode={state.statusCode} />
          ) : null}

          <View style={styles.workspace} testID="daily-close-workspace">
            {view !== "history" ? (
              <HandheldStateSurface
                slug={
                  view === "count"
                    ? "daily-close-count"
                    : "daily-close-summary"
                }
                style={styles.stateSurface}
              >
                <ScrollView
                  automaticallyAdjustKeyboardInsets
                  contentContainerStyle={styles.paneContent}
                  keyboardDismissMode="interactive"
                  keyboardShouldPersistTaps="handled"
                  ref={summaryScrollRef}
                  style={[styles.pane, styles.summaryPane]}
                  testID="daily-close-summary-scroll"
                >
              {view === "summary" ? (
                <ActionButton
                  label={dailyCloseText(locale, "action.backToCount")}
                  onPress={showCount}
                  sound="navigate"
                  testID="daily-close-summary-back"
                  tone="quiet"
                  wide
                />
              ) : null}
              <View style={styles.sectionHeader}>
                <View>
                  <Text style={styles.sectionTitle}>
                    {dailyCloseText(locale, "summary.title")}
                  </Text>
                  <Text style={styles.sectionHint}>
                    {dailyCloseText(locale, "summary.hint")}
                  </Text>
                </View>
                <View style={styles.dateControls}>
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
                  <ActionButton
                    disabled={state.busy}
                    label={
                      state.kind === "loading"
                        ? dailyCloseText(locale, "action.refreshing")
                        : dailyCloseText(locale, "action.refresh")
                    }
                    onPress={refresh}
                    testID="daily-close-refresh"
                  />
                </View>
              </View>

              {view === "summary" && state.summary ? (
                <>
                  <View style={styles.tenderCard}>
                    <View style={styles.tenderHeader}>
                      <Text style={styles.tenderMethod}>
                        {dailyCloseText(locale, "tender.method")}
                      </Text>
                      <Text style={styles.tenderValues}>
                        {dailyCloseText(locale, "tender.values")}
                      </Text>
                    </View>
                    {state.summary.tenders.map((tender) => (
                      <TenderRow
                        key={tender.method}
                        locale={locale}
                        tender={tender}
                      />
                    ))}
                  </View>

                  <View style={styles.metrics}>
                    <Metric
                      label={dailyCloseText(locale, "metric.orders")}
                      value={String(state.summary.orderCount)}
                    />
                    <Metric
                      label={dailyCloseText(locale, "metric.returnQuantity")}
                      value={state.summary.returnQuantity}
                    />
                    <Metric
                      label={dailyCloseText(locale, "metric.cashRounding")}
                      value={signedMoney(state.summary.cashRoundingCents)}
                    />
                    <Metric
                      label={dailyCloseText(locale, "metric.expectedCash")}
                      value={money(state.summary.expectedCashCents)}
                    />
                  </View>
                </>
              ) : !state.summary ? (
                <View style={styles.emptyCard}>
                  <Text style={styles.emptyTitle}>
                    {state.kind === "loading"
                      ? dailyCloseText(locale, "summary.loading")
                      : dailyCloseText(locale, "summary.empty")}
                  </Text>
                </View>
              ) : null}

              {state.access.canSave && view === "count" ? (
                <View style={styles.countSection}>
                  <View style={styles.sectionHeader}>
                    <View>
                      <Text style={styles.sectionTitle}>
                        {dailyCloseText(locale, "count.title")}
                      </Text>
                      <Text style={styles.sectionHint}>
                        {dailyCloseText(locale, "count.hint")}
                      </Text>
                    </View>
                    <View style={styles.countTotals}>
                      <Text style={styles.countTotalText}>
                        {dailyCloseText(locale, "count.notes", {
                          amount: money(state.notesSubtotalCents),
                        })}
                      </Text>
                      <Text style={styles.countTotalText}>
                        {dailyCloseText(locale, "count.coins", {
                          amount: money(state.coinsSubtotalCents),
                        })}
                      </Text>
                    </View>
                  </View>

                  <View style={styles.denominationGrid}>
                    {state.counts.map((count, index) => (
                      <DenominationField
                        autoFocus={
                          index === 0 &&
                          view === "count" &&
                          !state.busy
                        }
                        count={count.quantity}
                        denominationCents={count.denominationCents}
                        disabled={state.busy}
                        inputRef={index === 0 ? firstCountInputRef : undefined}
                        key={count.denominationCents}
                        locale={locale}
                        onChange={(quantity) =>
                          presenter.setCount(count.denominationCents, quantity)
                        }
                        onFocus={revealSummaryInput}
                        subtotalCents={count.subtotalCents}
                      />
                    ))}
                  </View>

                  <ActionButton
                    disabled={state.busy || !state.summary || state.kind !== "ready"}
                    label={dailyCloseText(locale, "action.review")}
                    onPress={() => setView("summary")}
                    testID="daily-close-review-summary"
                    wide
                  />
                </View>
              ) : null}

              {view === "summary" ? (
                state.access.canSave ? (
                  <View style={styles.saveBar}>
                    <View>
                      <Text style={styles.countedLabel}>
                        {dailyCloseText(locale, "count.countedCash")}
                      </Text>
                      <Text style={styles.countedAmount}>
                        {money(state.countedCashCents)}
                      </Text>
                      <Text
                        style={[
                          styles.variance,
                          state.varianceCents === 0
                            ? styles.varianceBalanced
                            : styles.varianceDifferent,
                        ]}
                      >
                        {dailyCloseText(locale, "count.variance", {
                          amount: signedMoney(state.varianceCents),
                        })}
                      </Text>
                    </View>
                    <ActionButton
                      disabled={state.busy || !state.summary || state.kind !== "ready"}
                      label={
                        state.busy
                          ? dailyCloseText(locale, "action.working")
                          : dailyCloseText(locale, "action.save")
                      }
                      onPress={() => void presenter.saveAndPrint()}
                      testID="daily-close-save"
                      wide
                    />
                  </View>
                ) : (
                <View style={styles.permissionNote}>
                  <Text style={styles.permissionText}>
                    {dailyCloseText(locale, "permission.viewOnly")}
                  </Text>
                </View>
                )
              ) : null}
                </ScrollView>
              </HandheldStateSurface>
            ) : null}

            {view === "history" ? (
              <ScrollView
              contentContainerStyle={styles.paneContent}
              style={[styles.pane, styles.historyPane]}
            >
              <Text style={styles.sectionTitle}>
                {dailyCloseText(locale, "history.title")}
              </Text>
              <Text style={styles.sectionHint}>
                {dailyCloseText(locale, "history.hint")}
              </Text>

              <View style={styles.historyList}>
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
              </View>

              {state.selectedArchive ? (
                <ArchiveDetails
                  archive={state.selectedArchive}
                  locale={locale}
                />
              ) : null}

              {state.access.canReprint ? (
                <ActionButton
                  disabled={state.busy || !state.selectedArchive}
                  label={
                    state.busy
                      ? dailyCloseText(locale, "action.working")
                      : dailyCloseText(locale, "action.reprint")
                  }
                  onPress={() => void presenter.reprintSelected()}
                  testID="daily-close-reprint"
                  tone="secondary"
                  wide
                />
              ) : null}
              </ScrollView>
            ) : null}
          </View>
        </View>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

export function DailyCloseUnavailableScreen({
  onBack,
}: Readonly<{ onBack(): void }>) {
  const { i18n } = useTranslation();
  const locale = resolveDailyCloseLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  return (
    <SafeAreaView style={styles.safeArea}>
      <View style={styles.unavailable}>
        <Text style={styles.title}>
          {dailyCloseText(locale, "unavailable.title")}
        </Text>
        <Text style={styles.subtitle}>
          {dailyCloseText(locale, "unavailable.subtitle")}
        </Text>
        <ActionButton
          label={dailyCloseText(locale, "unavailable.back")}
          onPress={onBack}
          testID="daily-close-unavailable-back"
          wide
        />
      </View>
    </SafeAreaView>
  );
}

function TenderRow({
  locale,
  tender,
}: Readonly<{
  locale: DailyCloseLocale;
  tender: DailyCloseTenderBreakdown;
}>) {
  return (
    <View style={styles.tenderRow}>
      <Text style={styles.tenderMethod}>
        {tenderName(tender.method, locale)}
      </Text>
      <Text style={styles.tenderValues}>
        {money(tender.salesCents)} / {money(tender.refundCents)} /{" "}
        {money(tender.netCents)}
      </Text>
    </View>
  );
}

function Metric({ label, value }: Readonly<{ label: string; value: string }>) {
  return (
    <View style={styles.metric}>
      <Text style={styles.metricLabel}>{label}</Text>
      <Text style={styles.metricValue}>{value}</Text>
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
  return (
    <View style={styles.denomination}>
      <View>
        <Text style={styles.denominationLabel}>
          {denominationLabel(denominationCents)}
        </Text>
        <Text style={styles.denominationSubtotal}>{money(subtotalCents)}</Text>
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
        onChangeText={(value) => {
          if (value === "") {
            onChange(0);
            return;
          }
          if (!/^\d+$/.test(value)) return;
          const quantity = Number(value);
          if (Number.isSafeInteger(quantity)) onChange(quantity);
        }}
        onFocus={onFocus}
        ref={inputRef}
        selectTextOnFocus
        showSoftInputOnFocus
        style={styles.countInput}
        testID={`daily-close-count-${denominationCents}`}
        value={String(count)}
      />
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
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.archiveRow, selected && styles.archiveRowSelected]}
      testID={`daily-close-history-${archive.closeId}`}
    >
      <View>
        <Text style={styles.archiveTitle}>
          {archive.businessDate} · {shortId(archive.closeId)}
        </Text>
        <Text style={styles.archiveMeta}>
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
  return (
    <View style={styles.archiveDetails}>
      <Text style={styles.detailTitle}>
        {dailyCloseText(locale, "archive.selected")}
      </Text>
      <Text style={styles.detailLine}>
        {dailyCloseText(locale, "archive.terminal", {
          value: archive.deviceCode,
        })}
      </Text>
      <Text style={styles.detailLine}>
        {dailyCloseText(locale, "archive.cashier", {
          value: archive.savedCashierName,
        })}
      </Text>
      <Text style={styles.detailLine}>
        {dailyCloseText(locale, "archive.ordersReturns", {
          orders: archive.orderCount,
          returns: archive.returnQuantity,
        })}
      </Text>
      <Text style={styles.detailLine}>
        {dailyCloseText(locale, "archive.rounding", {
          amount: signedMoney(archive.cashRoundingCents),
        })}
      </Text>
      <Text style={styles.detailLine}>
        {dailyCloseText(locale, "archive.expected", {
          amount: money(archive.expectedCashCents),
        })}
      </Text>
      <Text style={styles.detailLine}>
        {dailyCloseText(locale, "archive.counted", {
          amount: money(archive.countedCashCents),
        })}
      </Text>
      <Text
        style={[
          styles.detailVariance,
          archive.varianceCents === 0
            ? styles.varianceBalanced
            : styles.varianceDifferent,
        ]}
      >
        {dailyCloseText(locale, "count.variance", {
          amount: signedMoney(archive.varianceCents),
        })}
      </Text>
    </View>
  );
}

function ActionButton({
  disabled = false,
  label,
  onPress,
  selected = false,
  sound = "tap",
  style,
  testID,
  tone = "primary",
  wide = false,
}: Readonly<{
  disabled?: boolean;
  label: string;
  onPress(): void;
  selected?: boolean;
  sound?: "tap" | "navigate";
  style?: StyleProp<ViewStyle>;
  testID: string;
  tone?: "primary" | "secondary" | "quiet";
  wide?: boolean;
}>) {
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled, selected }}
      disabled={disabled}
      onPress={onPress}
      sound={sound}
      style={[
        styles.action,
        tone === "secondary" && styles.actionSecondary,
        tone === "quiet" && styles.actionQuiet,
        selected && styles.actionSelected,
        wide && styles.actionWide,
        disabled && styles.actionDisabled,
        style,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.actionText,
          tone !== "primary" && styles.actionTextSecondary,
          selected && styles.actionTextSelected,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function StatusBanner({
  locale,
  statusCode,
}: Readonly<{
  locale: DailyCloseLocale;
  statusCode: DailyCloseStatusCode;
}>) {
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

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: posColors.canvas,
  },
  keyboardAvoider: {
    flex: 1,
  },
  page: {
    flex: 1,
    paddingHorizontal: 16,
    paddingVertical: 8,
    gap: 12,
  },
  header: {
    flexDirection: "column",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 18,
  },
  titleGroup: {
    flex: 1,
    maxWidth: 760,
  },
  eyebrow: {
    color: posColors.orange,
    fontSize: 12,
    fontWeight: "800",
    letterSpacing: 1.2,
  },
  title: {
    color: posColors.ink,
    fontSize: 30,
    fontWeight: "900",
    letterSpacing: -0.7,
    marginTop: 3,
  },
  subtitle: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 4,
  },
  headerActions: {
    flexDirection: "row",
    flexWrap: "wrap",
    justifyContent: "flex-start",
    gap: 8,
  },
  workspace: {
    flex: 1,
    flexDirection: "column",
    gap: 8,
    minHeight: 0,
  },
  pane: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
  },
  summaryPane: {
    flex: 1,
  },
  historyPane: {
    flex: 1,
  },
  paneContent: {
    padding: 16,
    gap: 12,
  },
  sectionHeader: {
    flexDirection: "column",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 12,
  },
  sectionTitle: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "800",
  },
  sectionHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 17,
    marginTop: 2,
  },
  dateControls: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    gap: 8,
  },
  tenderCard: {
    borderColor: posColors.border,
    borderRadius: 12,
    borderWidth: 1,
    overflow: "hidden",
  },
  tenderHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    backgroundColor: posColors.blueSoft,
    paddingHorizontal: 12,
    paddingVertical: 9,
  },
  tenderRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    borderTopColor: posColors.border,
    borderTopWidth: 1,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  tenderMethod: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "700",
  },
  tenderValues: {
    color: posColors.ink,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
  },
  metrics: {
    flexDirection: "column",
    gap: 8,
  },
  metric: {
    flex: 1,
    backgroundColor: posColors.canvas,
    borderRadius: 10,
    padding: 11,
  },
  metricLabel: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontWeight: "700",
  },
  metricValue: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
    marginTop: 4,
  },
  emptyCard: {
    alignItems: "center",
    borderColor: posColors.border,
    borderRadius: 12,
    borderStyle: "dashed",
    borderWidth: 1,
    padding: 24,
  },
  emptyTitle: {
    color: posColors.mutedInk,
    fontSize: 14,
    textAlign: "center",
  },
  countSection: {
    borderTopColor: posColors.border,
    borderTopWidth: 1,
    gap: 12,
    paddingTop: 14,
  },
  countTotals: {
    alignItems: "flex-end",
  },
  countTotalText: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
  },
  denominationGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  denomination: {
    flexBasis: "47%",
    flexGrow: 1,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    backgroundColor: posColors.canvas,
    borderRadius: 10,
    paddingLeft: 10,
  },
  denominationLabel: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  denominationSubtotal: {
    color: posColors.mutedInk,
    fontSize: 10,
    fontVariant: ["tabular-nums"],
  },
  countInput: {
    minHeight: DAILY_CLOSE_MIN_TOUCH_TARGET,
    width: 54,
    borderColor: posColors.border,
    borderLeftWidth: 1,
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
    paddingHorizontal: 8,
    textAlign: "center",
  },
  saveBar: {
    flexDirection: "column",
    alignItems: "stretch",
    justifyContent: "space-between",
    backgroundColor: posColors.orangeSoft,
    borderRadius: 12,
    padding: 13,
  },
  countedLabel: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontWeight: "700",
  },
  countedAmount: {
    color: posColors.ink,
    fontSize: 26,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  variance: {
    fontSize: 12,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  varianceBalanced: {
    color: posColors.green,
  },
  varianceDifferent: {
    color: posColors.red,
  },
  permissionNote: {
    backgroundColor: posColors.blueSoft,
    borderRadius: 10,
    padding: 12,
  },
  permissionText: {
    color: posColors.blue,
    fontSize: 12,
    fontWeight: "700",
  },
  historyList: {
    gap: 7,
  },
  historyEmpty: {
    color: posColors.mutedInk,
    fontSize: 13,
    paddingVertical: 20,
    textAlign: "center",
  },
  archiveRow: {
    minHeight: DAILY_CLOSE_MIN_TOUCH_TARGET,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderColor: posColors.border,
    borderRadius: 10,
    borderWidth: 1,
    paddingHorizontal: 11,
    paddingVertical: 9,
  },
  archiveRowSelected: {
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.blue,
  },
  archiveTitle: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  archiveMeta: {
    color: posColors.mutedInk,
    fontSize: 10,
    marginTop: 2,
  },
  archiveAmount: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  archiveDetails: {
    backgroundColor: posColors.canvas,
    borderRadius: 12,
    gap: 5,
    padding: 12,
  },
  detailTitle: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
    marginBottom: 3,
  },
  detailLine: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
  },
  detailVariance: {
    fontSize: 13,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  action: {
    minHeight: DAILY_CLOSE_MIN_TOUCH_TARGET,
    minWidth: 100,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: posColors.orange,
    borderColor: posColors.orange,
    borderRadius: 6,
    borderWidth: 1,
    paddingHorizontal: 14,
    paddingVertical: 9,
  },
  actionSecondary: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
  },
  actionQuiet: {
    backgroundColor: "transparent",
    borderColor: posColors.border,
  },
  actionSelected: {
    backgroundColor: posColors.blue,
    borderColor: posColors.blue,
  },
  actionWide: {
    minWidth: 0,
    width: "100%",
  },
  actionDisabled: {
    opacity: 0.42,
  },
  actionText: {
    color: "#FFFFFF",
    fontSize: 13,
    fontWeight: "800",
    textAlign: "center",
  },
  actionTextSecondary: {
    color: posColors.ink,
  },
  actionTextSelected: {
    color: "#FFFFFF",
  },
  status: {
    borderRadius: 9,
    paddingHorizontal: 12,
    paddingVertical: 9,
  },
  statusSuccess: {
    backgroundColor: posColors.greenSoft,
  },
  statusWarning: {
    backgroundColor: posColors.redSoft,
  },
  statusText: {
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "700",
  },
  unavailable: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    gap: 16,
    padding: 32,
  },
  stateSurface: {
    flex: 1,
    minHeight: 0,
    width: "100%",
  },
});
