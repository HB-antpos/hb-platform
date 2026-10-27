import { useEffect, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  FlatList,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  remoteHistoryText,
  resolveRemoteHistoryLocale,
  type RemoteHistoryCopyKey,
  type RemoteHistoryLocale,
} from "./remote-history-copy";
import {
  type RemoteHistoryFilters,
  type RemoteHistoryPresenter,
} from "@hb/pos-domain/features/remote-history/remote-history-presenter";

import type {
  RemoteOrderHistoryDetails,
  RemoteOrderHistorySummary,
  RemoteOrderPaymentPreview,
} from "@hb/pos-domain/core/contracts/remote-history";
import { quickHistoryDateRange } from "@hb/pos-domain/features/remote-history/quick-date-range";
import { HistoryQuickRangeRow } from "@/ui/controls/history-quick-range-row";
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { posColors } from "@/ui/theme";

export const REMOTE_HISTORY_MIN_TOUCH_TARGET = 44;

type RemoteHistoryScreenProps = Readonly<{
  presenter: RemoteHistoryPresenter;
  onBack?(): void;
  onRefund?(orderGuid: string): void;
}>;

export function RemoteHistoryScreen({
  presenter,
  onBack,
  onRefund,
}: RemoteHistoryScreenProps) {
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { i18n } = useTranslation();
  const locale = resolveRemoteHistoryLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t = copyFor(locale);
  const [fromDate, setFromDate] = useState(
    dateInputValue(state.filters.soldFromIso),
  );
  const [toDate, setToDate] = useState(
    dateInputValue(state.filters.soldToIso),
  );
  const [deviceCode, setDeviceCode] = useState(
    state.filters.deviceCode ?? "",
  );
  const [keyword, setKeyword] = useState(state.filters.keyword ?? "");
  const [dateInvalid, setDateInvalid] = useState(false);

  useEffect(() => {
    void presenter.refresh();
  }, [presenter]);

  const loading = state.kind === "loading";
  const applyFilters = (
    dates: Readonly<{ from: string; to: string }> = {
      from: fromDate,
      to: toDate,
    },
  ) => {
    const range = localDateRange(dates.from, dates.to);
    if (!range) {
      setDateInvalid(true);
      return;
    }
    setDateInvalid(false);
    presenter.setFilters({
      ...range,
      deviceCode: nullableText(deviceCode),
      keyword: nullableText(keyword),
    });
    void presenter.refresh();
  };

  return (
    <SafeAreaView
      style={styles.safeArea}
      testID="remote-history-screen"
    >
      <View style={styles.header}>
        {onBack ? (
          <ActionButton
            label={t("action.back")}
            onPress={onBack}
            testID="remote-history-back"
            tone="quiet"
          />
        ) : null}
        <View style={styles.headerIdentity}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text style={styles.subtitle}>{t("subtitle")}</Text>
        </View>
        <ActionButton
          disabled={loading || state.kind === "offline"}
          label={loading ? t("action.refreshing") : t("action.refresh")}
          onPress={() => {
            void presenter.refresh();
          }}
          testID="remote-history-refresh"
        />
      </View>

      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.filters}
        style={styles.filtersScroll}
        testID="remote-history-filters-keyboard-scroll"
      >
        <DateFilterField
          label={t("filters.from")}
          locale={locale}
          onChange={(value) => {
            if (!value) return;
            setFromDate(value);
            setDateInvalid(false);
          }}
          testID="remote-history-date-from"
          value={fromDate}
        />
        <DateFilterField
          label={t("filters.to")}
          locale={locale}
          onChange={(value) => {
            if (!value) return;
            setToDate(value);
            setDateInvalid(false);
          }}
          testID="remote-history-date-to"
          value={toDate}
        />
        <HistoryQuickRangeRow
          disabled={loading || state.kind === "offline"}
          labels={{
            today: t("filters.quickToday"),
            yesterday: t("filters.quickYesterday"),
            last7days: t("filters.quickLast7Days"),
          }}
          onSelect={(kind) => {
            // 点了立即查询（对齐 WPF #608）。
            const range = quickHistoryDateRange(kind, new Date());
            setFromDate(range.from);
            setToDate(range.to);
            applyFilters(range);
          }}
          testIDPrefix="remote-history"
        />
        <FilterField
          label={t("filters.device")}
          onChangeText={setDeviceCode}
          placeholder={t("filters.devicePlaceholder")}
          testID="remote-history-device"
          value={deviceCode}
        />
        <FilterField
          grow
          label={t("filters.keyword")}
          onChangeText={setKeyword}
          placeholder={t("filters.keywordPlaceholder")}
          testID="remote-history-keyword"
          value={keyword}
        />
        <ActionButton
          disabled={loading || state.kind === "offline"}
          label={t("action.apply")}
          onPress={() => applyFilters()}
          testID="remote-history-apply-filters"
          tone="secondary"
        />
      </PosKeyboardAwareScrollView>
      {dateInvalid ? (
        <Text style={styles.validation} testID="remote-history-date-invalid">
          {t("filters.invalidDate")}
        </Text>
      ) : null}

      <View style={styles.readOnlyBanner}>
        <Text
          style={styles.readOnlyText}
          testID="remote-history-readonly-note"
        >
          {t("readonly.note")}
        </Text>
      </View>

      {state.kind === "offline" ? (
        <CenteredState
          hint={t("state.offlineHint")}
          message={t("state.offline")}
          testID="remote-history-offline"
        />
      ) : null}
      {state.kind === "unauthorized" ? (
        <CenteredState
          message={t("state.unauthorized")}
          testID="remote-history-unauthorized"
        />
      ) : null}
      {state.kind === "unavailable" ? (
        <CenteredState
          message={t("state.unavailable")}
          testID="remote-history-unavailable-state"
        />
      ) : null}
      {(state.kind === "idle" || state.kind === "loading") &&
      state.rows.length === 0 ? (
        <CenteredState
          loading
          message={t("state.loading")}
          testID="remote-history-loading"
        />
      ) : null}
      {state.kind === "empty" ? (
        <CenteredState
          message={t("state.empty")}
          testID="remote-history-empty"
        />
      ) : null}
      {state.kind === "failed" ? (
        <CenteredState
          message={t("state.failed")}
          testID="remote-history-failed"
        />
      ) : null}
      {(state.kind === "ready" ||
        (state.kind === "loading" && state.rows.length > 0)) ? (
        <View style={styles.workspace}>
          <View style={styles.listPane}>
            <View style={styles.paneHeader}>
              <Text style={styles.paneTitle}>{t("list.title")}</Text>
              {loading ? (
                <ActivityIndicator color={posColors.orange} />
              ) : null}
            </View>
            <FlatList
              contentContainerStyle={styles.orderList}
              data={state.rows}
              keyExtractor={(row) => row.orderGuid}
              renderItem={({ item }) => (
                <OrderRow
                  locale={locale}
                  onPress={() => {
                    void presenter.selectOrder(item.orderGuid);
                  }}
                  row={item}
                  selected={state.selectedOrderGuid === item.orderGuid}
                />
              )}
              testID="remote-history-list"
            />
          </View>
          <DetailsPane
            canReprint={presenter.capabilities.reprint}
            details={state.details}
            locale={locale}
            onRefund={onRefund}
            onReprint={() => {
              void presenter.reprintSelected();
            }}
            reprint={state.reprint}
          />
        </View>
      ) : null}
    </SafeAreaView>
  );
}

export function RemoteHistoryUnavailableScreen({
  onBack,
}: Readonly<{ onBack?(): void }>) {
  const { i18n } = useTranslation();
  const locale = resolveRemoteHistoryLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t = copyFor(locale);
  return (
    <SafeAreaView style={styles.safeArea}>
      <CenteredState
        {...(onBack
          ? { actionLabel: t("action.back"), onAction: onBack }
          : {})}
        message={t("state.unavailable")}
        testID="remote-history-unavailable"
      />
    </SafeAreaView>
  );
}

function OrderRow({
  locale,
  onPress,
  row,
  selected,
}: Readonly<{
  locale: RemoteHistoryLocale;
  onPress(): void;
  row: RemoteOrderHistorySummary;
  selected: boolean;
}>) {
  const t = copyFor(locale);
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.orderRow, selected && styles.orderRowSelected]}
      testID={`remote-history-order-${row.orderGuid}`}
    >
      <View style={styles.rowTop}>
        <Text style={styles.orderNumber}>
          {shortOrderGuid(row.orderGuid)}
        </Text>
        <Text style={styles.orderAmount}>{formatAud(row.actualAmountCents, locale)}</Text>
      </View>
      <Text style={styles.rowMeta}>
        {formatDateTime(row.soldAtIso, locale)} · {row.deviceCode} ·{" "}
        {row.cashierName}
      </Text>
      <View style={styles.rowBottom}>
        <Text style={styles.rowSecondary}>
          {t("list.items", { count: row.lineCount })}
        </Text>
        <Text style={styles.rowSecondary}>
          {row.paymentSummary ?? t("list.payment")}
        </Text>
        {row.statusLabel ? (
          <Text style={styles.statusPill}>{row.statusLabel}</Text>
        ) : null}
      </View>
    </PosPressable>
  );
}

function DetailsPane({
  canReprint,
  details,
  locale,
  onRefund,
  onReprint,
  reprint,
}: Readonly<{
  canReprint: boolean;
  details: RemoteHistoryPresenter["state"]["details"];
  locale: RemoteHistoryLocale;
  onRefund: ((orderGuid: string) => void) | undefined;
  onReprint(): void;
  reprint: RemoteHistoryPresenter["state"]["reprint"];
}>) {
  const t = copyFor(locale);
  return (
    <View style={styles.detailsPane} testID="remote-history-details">
      <View style={styles.paneHeader}>
        <Text style={styles.paneTitle}>{t("details.title")}</Text>
        {details.kind === "loading" ? (
          <ActivityIndicator color={posColors.orange} />
        ) : null}
      </View>
      {details.kind === "idle" ? (
        <DetailsState message={t("details.select")} />
      ) : null}
      {details.kind === "not-found" ? (
        <DetailsState message={t("details.notFound")} />
      ) : null}
      {details.kind === "failed" ? (
        <DetailsState message={t("details.failed")} />
      ) : null}
      {details.kind === "loading" ? (
        <DetailsState message={t("state.loading")} />
      ) : null}
      {details.kind === "ready" ? (
        <RemoteOrderDetails
          canReprint={canReprint}
          locale={locale}
          onRefund={onRefund}
          onReprint={onReprint}
          reprint={reprint}
          value={details.value}
        />
      ) : null}
    </View>
  );
}

function RemoteOrderDetails({
  canReprint,
  locale,
  onRefund,
  onReprint,
  reprint,
  value,
}: Readonly<{
  canReprint: boolean;
  locale: RemoteHistoryLocale;
  onRefund: ((orderGuid: string) => void) | undefined;
  onReprint(): void;
  reprint: RemoteHistoryPresenter["state"]["reprint"];
  value: RemoteOrderHistoryDetails;
}>) {
  const t = copyFor(locale);
  const canRefund = Boolean(onRefund && isOrdinarySale(value));
  return (
    <ScrollView contentContainerStyle={styles.detailsContent}>
      <View style={styles.detailsIdentity}>
        <View>
          <Text style={styles.detailsOrder}>
            {shortOrderGuid(value.orderGuid)}
          </Text>
          <Text style={styles.rowMeta}>
            {formatDateTime(value.soldAtIso, locale)} · {value.deviceCode} ·{" "}
            {value.cashierName}
          </Text>
        </View>
        <View style={styles.detailsTotals}>
          <Text style={styles.detailsTotalLabel}>{t("details.actual")}</Text>
          <Text style={styles.detailsTotal}>
            {formatAud(value.actualAmountCents, locale)}
          </Text>
        </View>
      </View>

      {canRefund || canReprint ? (
        <View style={styles.reprintAction}>
          <View style={styles.detailsActionButtons}>
            {canRefund ? (
              <ActionButton
                label={t("action.refund")}
                onPress={() => onRefund?.(value.orderGuid)}
                testID="remote-history-refund"
              />
            ) : null}
            {canReprint ? (
              <ActionButton
                disabled={reprint.kind === "submitting"}
                label={
                  reprint.kind === "submitting"
                    ? t("action.reprinting")
                    : t("action.reprint")
                }
                onPress={onReprint}
                testID="remote-history-reprint"
                tone="secondary"
              />
            ) : null}
          </View>
          {reprint.kind === "succeeded" ? (
            <Text style={styles.reprintSuccess} testID="remote-history-reprint-succeeded">
              {t("reprint.succeeded")}
            </Text>
          ) : null}
          {reprint.kind === "failed" ? (
            <Text style={styles.reprintFailure} testID="remote-history-reprint-failed">
              {t("reprint.failed")}
            </Text>
          ) : null}
        </View>
      ) : null}

      <Text style={styles.sectionTitle}>{t("details.lines")}</Text>
      {value.lines.map((line) => (
        <View key={line.orderLineGuid} style={styles.detailRow}>
          <View style={styles.detailMain}>
            <Text style={styles.detailName}>{line.displayName}</Text>
            <Text style={styles.rowMeta}>
              {[line.lookupCode, line.itemNumber, line.productCode]
                .filter((part): part is string => Boolean(part))
                .join(" · ")}
            </Text>
            <Text style={styles.rowSecondary}>
              {t("details.quantity", { quantity: line.quantity })} ·{" "}
              {formatAud(line.unitPriceCents, locale)}
            </Text>
          </View>
          <View style={styles.detailAmount}>
            <Text
              style={[
                styles.lineKind,
                line.kind === "return" && styles.lineKindReturn,
              ]}
            >
              {line.kind === "return" ? "↩" : "•"}
            </Text>
            <Text style={styles.orderAmount}>
              {formatAud(line.actualAmountCents, locale)}
            </Text>
            {line.discountCents !== 0 ? (
              <Text style={styles.discount}>
                {t("details.discount", {
                  amount: formatAud(line.discountCents, locale),
                })}
              </Text>
            ) : null}
          </View>
        </View>
      ))}

      <Text style={styles.sectionTitle}>{t("details.payments")}</Text>
      {value.payments.map((payment) => (
        <View key={payment.paymentGuid} style={styles.paymentRow}>
          <View>
            <Text style={styles.detailName}>
              {paymentDisplayName(payment, locale)}
            </Text>
          </View>
          <Text style={styles.orderAmount}>
            {formatAud(payment.amountCents, locale)}
          </Text>
        </View>
      ))}

      <View style={styles.summaryCard}>
        <SummaryRow
          label={t("details.total")}
          locale={locale}
          value={value.totalCents}
        />
        {value.discountCents !== 0 ? (
          <SummaryRow
            label={t("details.discount", { amount: "" }).trim()}
            locale={locale}
            value={value.discountCents}
          />
        ) : null}
        <SummaryRow
          emphasized
          label={t("details.actual")}
          locale={locale}
          value={value.actualAmountCents}
        />
      </View>
    </ScrollView>
  );
}

function isOrdinarySale(value: RemoteOrderHistoryDetails): boolean {
  return (
    value.actualAmountCents > 0 &&
    value.lines.length > 0 &&
    value.lines.every((line) => line.kind === "sale") &&
    value.payments.some((payment) => payment.amountCents > 0)
  );
}

function SummaryRow({
  emphasized = false,
  label,
  locale,
  value,
}: Readonly<{
  emphasized?: boolean;
  label: string;
  locale: RemoteHistoryLocale;
  value: number;
}>) {
  return (
    <View style={styles.summaryRow}>
      <Text style={[styles.summaryLabel, emphasized && styles.summaryStrong]}>
        {label}
      </Text>
      <Text style={[styles.summaryValue, emphasized && styles.summaryStrong]}>
        {formatAud(value, locale)}
      </Text>
    </View>
  );
}

function FilterField({
  grow = false,
  label,
  onChangeText,
  placeholder,
  testID,
  value,
}: Readonly<{
  grow?: boolean;
  label: string;
  onChangeText(value: string): void;
  placeholder: string;
  testID: string;
  value: string;
}>) {
  return (
    <View style={[styles.filterField, grow && styles.filterFieldGrow]}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <PosKeyboardAwareTextInput
        autoCapitalize="none"
        autoCorrect={false}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={posColors.mutedInk}
        style={styles.input}
        testID={testID}
        value={value}
      />
    </View>
  );
}

function DateFilterField({
  label,
  locale,
  onChange,
  testID,
  value,
}: Readonly<{
  label: string;
  locale: RemoteHistoryLocale;
  onChange(value: string | null): void;
  testID: string;
  value: string;
}>) {
  return (
    <View style={styles.filterField}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <PosDatePickerField
        accessibilityLabel={label}
        locale={locale}
        onChange={onChange}
        testID={testID}
        value={value}
      />
    </View>
  );
}

function ActionButton({
  disabled = false,
  label,
  onPress,
  sound = "tap",
  testID,
  tone = "primary",
}: Readonly<{
  disabled?: boolean;
  label: string;
  onPress(): void;
  sound?: "tap" | "navigate";
  testID: string;
  tone?: "primary" | "secondary" | "quiet";
}>) {
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={sound}
      style={[
        styles.actionButton,
        tone === "primary"
          ? styles.actionPrimary
          : tone === "secondary"
            ? styles.actionSecondary
            : styles.actionQuiet,
        disabled && styles.disabled,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.actionText,
          tone !== "primary" && styles.actionTextDark,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function CenteredState({
  actionLabel,
  hint,
  loading = false,
  message,
  onAction,
  testID,
}: Readonly<{
  actionLabel?: string;
  hint?: string;
  loading?: boolean;
  message: string;
  onAction?(): void;
  testID: string;
}>) {
  return (
    <View style={styles.centered} testID={testID}>
      {loading ? <ActivityIndicator color={posColors.orange} size="large" /> : null}
      <Text style={styles.centeredTitle}>{message}</Text>
      {hint ? <Text style={styles.centeredHint}>{hint}</Text> : null}
      {actionLabel && onAction ? (
        <ActionButton
          label={actionLabel}
          onPress={onAction}
          testID={`${testID}-back`}
          tone="quiet"
        />
      ) : null}
    </View>
  );
}

function DetailsState({ message }: Readonly<{ message: string }>) {
  return (
    <View style={styles.detailsState}>
      <Text style={styles.centeredHint}>{message}</Text>
    </View>
  );
}

function copyFor(locale: RemoteHistoryLocale) {
  return (
    key: RemoteHistoryCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => remoteHistoryText(locale, key, values);
}

function nullableText(value: string): string | null {
  const normalized = value.trim();
  return normalized.length === 0 ? null : normalized;
}

function dateInputValue(value: string): string {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "";
  const year = String(date.getFullYear()).padStart(4, "0");
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function localDateRange(
  fromText: string,
  toText: string,
): Pick<RemoteHistoryFilters, "soldFromIso" | "soldToIso"> | null {
  const from = parseLocalDate(fromText, false);
  const to = parseLocalDate(toText, true);
  if (!from || !to || from.getTime() > to.getTime()) return null;
  return {
    soldFromIso: from.toISOString(),
    soldToIso: to.toISOString(),
  };
}

function parseLocalDate(value: string, endOfDay: boolean): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/u.exec(value.trim());
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(
    year,
    month - 1,
    day,
    endOfDay ? 23 : 0,
    endOfDay ? 59 : 0,
    endOfDay ? 59 : 0,
    endOfDay ? 999 : 0,
  );
  return date.getFullYear() === year &&
    date.getMonth() === month - 1 &&
    date.getDate() === day
    ? date
    : null;
}

function shortOrderGuid(orderGuid: string): string {
  return `#${orderGuid.slice(-8).toUpperCase()}`;
}

function formatAud(cents: number, locale: RemoteHistoryLocale): string {
  return new Intl.NumberFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    style: "currency",
    currency: "AUD",
  }).format(cents / 100);
}

function formatDateTime(
  value: string,
  locale: RemoteHistoryLocale,
): string {
  return new Intl.DateTimeFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}

function paymentDisplayName(
  payment: RemoteOrderPaymentPreview,
  locale: RemoteHistoryLocale,
): string {
  const cardParts = [payment.cardType, payment.maskedCardNumber].filter(
    (part): part is string => Boolean(part),
  );
  if (cardParts.length > 0) return cardParts.join(" · ");
  return remoteHistoryText(locale, `method.${payment.method}`);
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: posColors.canvas,
  },
  header: {
    minHeight: 76,
    paddingHorizontal: 22,
    paddingVertical: 12,
    flexDirection: "row",
    alignItems: "center",
    gap: 14,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: posColors.border,
    backgroundColor: posColors.surface,
  },
  headerIdentity: {
    flex: 1,
  },
  title: {
    color: posColors.ink,
    fontSize: 27,
    fontWeight: "800",
    letterSpacing: -0.4,
  },
  subtitle: {
    color: posColors.mutedInk,
    fontSize: 13,
    marginTop: 2,
  },
  filters: {
    flexDirection: "row",
    alignItems: "flex-end",
    gap: 10,
    paddingHorizontal: 22,
    paddingTop: 12,
    paddingBottom: 10,
    backgroundColor: posColors.surface,
  },
  filtersScroll: {
    flexGrow: 0,
  },
  filterField: {
    width: 150,
    gap: 5,
  },
  filterFieldGrow: {
    flex: 1,
    minWidth: 190,
  },
  fieldLabel: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
    textTransform: "uppercase",
    letterSpacing: 0.5,
  },
  input: {
    minHeight: REMOTE_HISTORY_MIN_TOUCH_TARGET,
    borderWidth: 1,
    borderColor: posColors.border,
    borderRadius: 8,
    paddingHorizontal: 12,
    color: posColors.ink,
    backgroundColor: posColors.canvas,
    fontSize: 15,
  },
  validation: {
    paddingHorizontal: 22,
    paddingBottom: 8,
    color: posColors.red,
    backgroundColor: posColors.surface,
    fontSize: 13,
    fontWeight: "600",
  },
  readOnlyBanner: {
    paddingHorizontal: 22,
    paddingVertical: 8,
    backgroundColor: posColors.blueSoft,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: posColors.border,
  },
  readOnlyText: {
    color: posColors.blue,
    fontSize: 13,
    fontWeight: "600",
  },
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 14,
    padding: 14,
  },
  listPane: {
    flex: 0.43,
    minWidth: 360,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: posColors.border,
    borderRadius: 12,
    backgroundColor: posColors.surface,
  },
  detailsPane: {
    flex: 0.57,
    minWidth: 430,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: posColors.border,
    borderRadius: 12,
    backgroundColor: posColors.surface,
  },
  paneHeader: {
    minHeight: 50,
    paddingHorizontal: 16,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: posColors.border,
  },
  paneTitle: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
  },
  orderList: {
    padding: 10,
    gap: 8,
  },
  orderRow: {
    minHeight: 104,
    padding: 13,
    borderWidth: 1,
    borderColor: posColors.border,
    borderRadius: 9,
    backgroundColor: posColors.surface,
    gap: 7,
  },
  orderRowSelected: {
    borderColor: posColors.orange,
    borderLeftWidth: 5,
    backgroundColor: posColors.orangeSoft,
  },
  rowTop: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 10,
  },
  orderNumber: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  orderAmount: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  rowMeta: {
    color: posColors.mutedInk,
    fontSize: 12,
  },
  rowBottom: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  rowSecondary: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },
  statusPill: {
    marginLeft: "auto",
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: 999,
    color: posColors.green,
    backgroundColor: posColors.greenSoft,
    fontSize: 11,
    fontWeight: "800",
  },
  detailsContent: {
    padding: 16,
    gap: 10,
  },
  detailsIdentity: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "flex-start",
    gap: 16,
    paddingBottom: 8,
  },
  detailsOrder: {
    color: posColors.ink,
    fontSize: 22,
    fontWeight: "800",
  },
  detailsTotals: {
    alignItems: "flex-end",
  },
  reprintAction: {
    alignItems: "flex-start",
    gap: 6,
    marginTop: 14,
  },
  detailsActionButtons: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  reprintSuccess: {
    color: "#18794E",
    fontSize: 13,
  },
  reprintFailure: {
    color: "#B42318",
    fontSize: 13,
  },
  detailsTotalLabel: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
  detailsTotal: {
    color: posColors.orange,
    fontSize: 26,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  sectionTitle: {
    marginTop: 4,
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "800",
    textTransform: "uppercase",
    letterSpacing: 0.8,
  },
  detailRow: {
    minHeight: 70,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: posColors.border,
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 12,
  },
  detailMain: {
    flex: 1,
    gap: 3,
  },
  detailName: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "700",
  },
  detailAmount: {
    alignItems: "flex-end",
    gap: 3,
  },
  lineKind: {
    color: posColors.green,
    fontSize: 14,
    fontWeight: "800",
  },
  lineKindReturn: {
    color: posColors.red,
  },
  discount: {
    color: posColors.red,
    fontSize: 11,
  },
  paymentRow: {
    minHeight: 58,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: posColors.border,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  summaryCard: {
    marginTop: 8,
    padding: 14,
    borderRadius: 10,
    backgroundColor: posColors.canvas,
    gap: 8,
  },
  summaryRow: {
    flexDirection: "row",
    justifyContent: "space-between",
  },
  summaryLabel: {
    color: posColors.mutedInk,
    fontSize: 14,
  },
  summaryValue: {
    color: posColors.ink,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
  },
  summaryStrong: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
  },
  centered: {
    flex: 1,
    minHeight: 180,
    alignItems: "center",
    justifyContent: "center",
    gap: 10,
    padding: 28,
  },
  centeredTitle: {
    color: posColors.ink,
    fontSize: 19,
    fontWeight: "800",
    textAlign: "center",
  },
  centeredHint: {
    maxWidth: 520,
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 21,
    textAlign: "center",
  },
  detailsState: {
    flex: 1,
    minHeight: 220,
    alignItems: "center",
    justifyContent: "center",
    padding: 28,
  },
  actionButton: {
    minHeight: REMOTE_HISTORY_MIN_TOUCH_TARGET,
    minWidth: 112,
    paddingHorizontal: 16,
    borderRadius: 8,
    borderWidth: 1,
    alignItems: "center",
    justifyContent: "center",
  },
  actionPrimary: {
    borderColor: posColors.orange,
    backgroundColor: posColors.orange,
  },
  actionSecondary: {
    borderColor: posColors.blue,
    backgroundColor: posColors.blueSoft,
  },
  actionQuiet: {
    borderColor: posColors.border,
    backgroundColor: posColors.surface,
  },
  actionText: {
    color: "#FFFFFF",
    fontSize: 14,
    fontWeight: "800",
  },
  actionTextDark: {
    color: posColors.ink,
  },
  disabled: {
    opacity: 0.45,
  },
});
