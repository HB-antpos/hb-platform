import {
  useEffect,
  useState,
  useSyncExternalStore,
} from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  FlatList,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  localHistoryText,
  resolveLocalHistoryLocale,
  type LocalHistoryCopyKey,
  type LocalHistoryLocale,
} from "./local-history-copy";
import { LOCAL_HISTORY_KEYWORD_MAX_LENGTH } from "./local-history-domain";
import {
  localHistoryBusinessDayRange,
  type LocalHistoryPresenter,
  type LocalHistoryReceiptPreviewState,
} from "./local-history-presenter";
import {
  LocalHistoryReceiptPreview,
} from "./local-history-receipt-preview";

import { quickHistoryDateRange } from "@hb/pos-domain/features/remote-history/quick-date-range";
import { HistoryQuickRangeRow } from "@/ui/controls/history-quick-range-row";
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { posColors } from "@/ui/theme";

export const LOCAL_HISTORY_MIN_TOUCH_TARGET = 44;

const LOCAL_HISTORY_COMPACT_BREAKPOINT = 900;

export type LocalHistoryLayout = Readonly<{
  compact: boolean;
  workspaceGap: number;
  workspacePadding: number;
  filterWidth: number;
}>;

export function localHistoryLayoutForWidth(
  width: number,
): LocalHistoryLayout {
  const compact = width < LOCAL_HISTORY_COMPACT_BREAKPOINT;
  return compact
    ? {
        compact: true,
        workspaceGap: 8,
        workspacePadding: 8,
        filterWidth: 142,
      }
    : {
        compact: false,
        workspaceGap: 14,
        workspacePadding: 14,
        filterWidth: 150,
      };
}

type LocalHistoryScreenProps = Readonly<{
  presenter: LocalHistoryScreenPresenter;
  onBack?(): void;
}>;

export type LocalHistoryScreenPresenter = Pick<
  LocalHistoryPresenter,
  | "capabilities"
  | "getState"
  | "subscribe"
  | "setFilters"
  | "refresh"
  | "selectOrder"
  | "loadMore"
  | "reprintSelected"
>;

type PresenterState = ReturnType<LocalHistoryScreenPresenter["getState"]>;
type LocalHistorySummary = PresenterState["rows"][number];
type LocalHistoryDetails = Extract<
  PresenterState["details"],
  Readonly<{ kind: "ready" }>
>["value"];
type LocalHistoryLine = LocalHistoryDetails["lines"][number];
type LocalHistoryTender = LocalHistoryDetails["tenders"][number];

export function LocalHistoryScreen({
  presenter,
  onBack,
}: LocalHistoryScreenProps) {
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { width } = useWindowDimensions();
  const layout = localHistoryLayoutForWidth(width);
  const { i18n } = useTranslation();
  const locale = resolveLocalHistoryLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t = copyFor(locale);
  const [fromDate, setFromDate] = useState(
    dateInputValue(
      state.filters.soldFromIso,
      state.businessTimeZone,
    ),
  );
  const [toDate, setToDate] = useState(
    dateInputValue(
      state.filters.soldToIso,
      state.businessTimeZone,
    ),
  );
  const [keyword, setKeyword] = useState(state.filters.keyword ?? "");
  const [filterError, setFilterError] = useState<
    "date" | "query" | null
  >(null);

  useEffect(() => {
    void presenter.refresh();
  }, [presenter]);

  const loading = state.kind === "loading";
  const hasRows = state.rows.length > 0;
  const applyFilters = (
    dates: Readonly<{ from: string; to: string }> = {
      from: fromDate,
      to: toDate,
    },
  ) => {
    const range = localHistoryBusinessDayRange(
      dates.from,
      dates.to,
      state.businessTimeZone,
    );
    if (!range) {
      setFilterError("date");
      return;
    }
    try {
      presenter.setFilters({
        ...range,
        keyword: nullableText(keyword),
      });
    } catch {
      setFilterError("query");
      return;
    }
    setFilterError(null);
    void presenter.refresh();
  };

  return (
    <SafeAreaView style={styles.safeArea} testID="local-history-screen">
      <View style={[styles.header, layout.compact && styles.headerCompact]}>
        {onBack ? (
          <ActionButton
            compact={layout.compact}
            label={t("action.back")}
            onPress={onBack}
            sound="navigate"
            testID="local-history-back"
            tone="quiet"
          />
        ) : null}
        <View style={styles.headerIdentity}>
          <Text
            numberOfLines={1}
            style={[styles.title, layout.compact && styles.titleCompact]}
          >
            {t("title")}
          </Text>
          <Text numberOfLines={1} style={styles.subtitle}>
            {t("subtitle")}
          </Text>
        </View>
        <ActionButton
          compact={layout.compact}
          disabled={loading}
          label={
            loading ? t("action.refreshing") : t("action.refresh")
          }
          onPress={() => {
            void presenter.refresh();
          }}
          testID="local-history-refresh"
        />
      </View>

      <PosKeyboardAwareScrollView
        contentContainerStyle={[
          styles.filters,
          layout.compact && styles.filtersCompact,
        ]}
        style={styles.filtersScroll}
        testID="local-history-filters-keyboard-scroll"
      >
        <DateFilterField
          compact={layout.compact}
          label={t("filters.from")}
          locale={locale}
          onChange={(value) => {
            if (!value) return;
            setFromDate(value);
            if (filterError === "date") setFilterError(null);
          }}
          testID="local-history-date-from"
          value={fromDate}
          width={layout.filterWidth}
        />
        <DateFilterField
          compact={layout.compact}
          label={t("filters.to")}
          locale={locale}
          onChange={(value) => {
            if (!value) return;
            setToDate(value);
            if (filterError === "date") setFilterError(null);
          }}
          testID="local-history-date-to"
          value={toDate}
          width={layout.filterWidth}
        />
        <HistoryQuickRangeRow
          disabled={loading}
          labels={{
            today: t("filters.quickToday"),
            yesterday: t("filters.quickYesterday"),
            last7days: t("filters.quickLast7Days"),
          }}
          onSelect={(kind) => {
            // 点了立即查询（对齐 WPF #608）。
            const range = quickHistoryDateRange(kind, new Date(), state.businessTimeZone);
            setFromDate(range.from);
            setToDate(range.to);
            applyFilters(range);
          }}
          testIDPrefix="local-history"
        />
        <FilterField
          compact={layout.compact}
          grow
          label={t("filters.keyword")}
          maxLength={LOCAL_HISTORY_KEYWORD_MAX_LENGTH}
          onChangeText={(value) => {
            setKeyword(value);
            if (filterError === "query") setFilterError(null);
          }}
          placeholder={t("filters.keywordPlaceholder")}
          testID="local-history-keyword"
          value={keyword}
        />
        <ActionButton
          compact={layout.compact}
          disabled={loading}
          label={t("action.apply")}
          onPress={() => applyFilters()}
          testID="local-history-apply-filters"
          tone="secondary"
        />
      </PosKeyboardAwareScrollView>
      {filterError ? (
        <Text
          style={styles.validation}
          testID={
            filterError === "date"
              ? "local-history-date-invalid"
              : "local-history-query-invalid"
          }
        >
          {t(
            filterError === "date"
              ? "filters.invalidDate"
              : "filters.invalidQuery",
          )}
        </Text>
      ) : null}

      {!hasRows && (state.kind === "idle" || loading) ? (
        <CenteredState
          loading
          message={t("state.loading")}
          testID="local-history-loading"
        />
      ) : null}
      {!hasRows && state.kind === "empty" ? (
        <CenteredState
          message={t("state.empty")}
          testID="local-history-empty"
        />
      ) : null}
      {!hasRows && state.kind === "failed" ? (
        <CenteredState
          message={t("state.failed")}
          testID="local-history-failed"
        />
      ) : null}
      {!hasRows && state.kind === "unauthorized" ? (
        <CenteredState
          message={t("state.unauthorized")}
          testID="local-history-unauthorized"
        />
      ) : null}

      {hasRows ? (
        <View
          style={[
            styles.workspace,
            {
              gap: layout.workspaceGap,
              padding: layout.workspacePadding,
            },
          ]}
          testID="local-history-workspace"
        >
          <View
            style={[
              styles.listPane,
              layout.compact && styles.paneCompact,
            ]}
            testID="local-history-list-pane"
          >
            <View style={styles.paneHeader}>
              <Text style={styles.paneTitle}>{t("list.title")}</Text>
              {loading || state.loadingMore ? (
                <ActivityIndicator color={posColors.orange} />
              ) : null}
            </View>
            <FlatList
              contentContainerStyle={styles.orderList}
              data={state.rows}
              keyExtractor={(row) => row.orderGuid}
              ListFooterComponent={
                state.hasMore ? (
                  <View style={styles.loadMore}>
                    <ActionButton
                      compact={layout.compact}
                      disabled={loading || state.loadingMore}
                      label={
                        state.loadingMore
                          ? t("action.loadingMore")
                          : t("action.loadMore")
                      }
                      onPress={() => {
                        void presenter.loadMore();
                      }}
                      testID="local-history-load-more"
                      tone="quiet"
                    />
                  </View>
                ) : null
              }
              renderItem={({ item }) => (
                <OrderRow
                  businessTimeZone={state.businessTimeZone}
                  locale={locale}
                  onPress={() => {
                    void presenter.selectOrder(item.orderGuid);
                  }}
                  row={item}
                  selected={state.selectedOrderGuid === item.orderGuid}
                />
              )}
              testID="local-history-list"
            />
          </View>
          <DetailsPane
            businessTimeZone={state.businessTimeZone}
            canReprint={presenter.capabilities.reprint}
            compact={layout.compact}
            details={state.details}
            locale={locale}
            onReprint={() => {
              void presenter.reprintSelected();
            }}
            receiptPreview={state.receiptPreview}
            reprint={state.reprint}
          />
        </View>
      ) : null}
    </SafeAreaView>
  );
}

export function LocalHistoryUnavailableScreen({
  onBack,
}: Readonly<{ onBack?(): void }>) {
  const { i18n } = useTranslation();
  const locale = resolveLocalHistoryLocale(
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
        testID="local-history-unavailable"
      />
    </SafeAreaView>
  );
}

function OrderRow({
  businessTimeZone,
  locale,
  onPress,
  row,
  selected,
}: Readonly<{
  businessTimeZone: string;
  locale: LocalHistoryLocale;
  onPress(): void;
  row: LocalHistorySummary;
  selected: boolean;
}>) {
  const t = copyFor(locale);
  return (
    <PosPressable
      accessibilityLabel={[
        t("list.sequence", { sequence: row.localSequence }),
        formatAud(row.actualAmountCents, locale),
        formatDateTime(row.soldAtIso, locale, businessTimeZone),
        row.cashierName,
        paymentSummaryLabel(row.paymentSummary, locale),
        orderStateLabel(row.state, locale),
      ].join(", ")}
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.orderRow, selected && styles.orderRowSelected]}
      testID={`local-history-order-${row.orderGuid}`}
    >
      <View style={styles.rowTop}>
        <Text style={styles.orderNumber}>
          {t("list.sequence", { sequence: row.localSequence })}
        </Text>
        <Text style={styles.orderAmount}>
          {formatAud(row.actualAmountCents, locale)}
        </Text>
      </View>
      <Text numberOfLines={1} style={styles.rowMeta}>
        {formatDateTime(row.soldAtIso, locale, businessTimeZone)} ·{" "}
        {shortOrderGuid(row.orderGuid)} · {row.cashierName}
      </Text>
      <View style={styles.rowBottom}>
        <Text style={styles.rowSecondary}>
          {t("list.items", { count: row.lineCount })}
        </Text>
        <Text numberOfLines={1} style={styles.rowSecondary}>
          {paymentSummaryLabel(row.paymentSummary, locale)}
        </Text>
        <Text
          numberOfLines={1}
          style={[styles.statusPill, orderStateTone(row.state)]}
        >
          {orderStateLabel(row.state, locale)}
        </Text>
      </View>
    </PosPressable>
  );
}

function DetailsPane({
  businessTimeZone,
  canReprint,
  compact,
  details,
  locale,
  onReprint,
  receiptPreview,
  reprint,
}: Readonly<{
  businessTimeZone: string;
  canReprint: boolean;
  compact: boolean;
  details: PresenterState["details"];
  locale: LocalHistoryLocale;
  onReprint(): void;
  receiptPreview: LocalHistoryReceiptPreviewState;
  reprint: PresenterState["reprint"];
}>) {
  const t = copyFor(locale);
  const [activePane, setActivePane] = useState<
    "details" | "receiptPreview"
  >("details");
  return (
    <View
      style={[
        styles.detailsPane,
        compact && styles.paneCompact,
      ]}
      testID="local-history-details"
    >
      <View style={styles.paneHeader}>
        <View style={styles.detailsTabs}>
          <PaneTab
            active={activePane === "details"}
            label={t("details.title")}
            onPress={() => setActivePane("details")}
            testID="local-history-details-tab"
          />
          <PaneTab
            active={activePane === "receiptPreview"}
            label={t("receiptPreview.title")}
            onPress={() => setActivePane("receiptPreview")}
            testID="local-history-receipt-preview-tab"
          />
        </View>
        <View style={styles.detailsHeaderActions}>
          {canReprint ? (
            <ActionButton
              compact
              disabled={reprint.kind === "submitting"}
              label={
                reprint.kind === "submitting"
                  ? t("action.reprinting")
                  : t("action.reprint")
              }
              onPress={onReprint}
              testID="local-history-reprint"
            />
          ) : null}
          {activePane === "details" && details.kind === "loading" ? (
            <ActivityIndicator color={posColors.orange} />
          ) : null}
          {activePane === "receiptPreview" && receiptPreview.kind === "loading" ? (
            <ActivityIndicator color={posColors.orange} />
          ) : null}
        </View>
      </View>
      <ReprintResult locale={locale} reprint={reprint} />
      {activePane === "details" ? (
        <DetailsContent
          businessTimeZone={businessTimeZone}
          compact={compact}
          details={details}
          locale={locale}
        />
      ) : (
        <ReceiptPreviewContent
          locale={locale}
          receiptPreview={receiptPreview}
        />
      )}
    </View>
  );
}

function PaneTab({
  active,
  label,
  onPress,
  testID,
}: Readonly<{
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
      style={[styles.paneTab, active && styles.paneTabActive]}
      testID={testID}
    >
      <Text numberOfLines={1} style={[styles.paneTabText, active && styles.paneTabTextActive]}>
        {label}
      </Text>
    </PosPressable>
  );
}

function DetailsContent({
  businessTimeZone,
  compact,
  details,
  locale,
}: Readonly<{
  businessTimeZone: string;
  compact: boolean;
  details: PresenterState["details"];
  locale: LocalHistoryLocale;
}>) {
  const t = copyFor(locale);
  if (details.kind === "idle") {
    return <DetailsState message={t("details.select")} />;
  }
  if (details.kind === "not-found") {
    return <DetailsState message={t("details.notFound")} />;
  }
  if (details.kind === "failed") {
    return <DetailsState message={t("details.failed")} />;
  }
  if (details.kind === "loading") {
    return <DetailsState message={t("state.loading")} />;
  }
  return (
    <LocalOrderDetails
      businessTimeZone={businessTimeZone}
      compact={compact}
      locale={locale}
      value={details.value}
    />
  );
}

function ReceiptPreviewContent({
  locale,
  receiptPreview,
}: Readonly<{
  locale: LocalHistoryLocale;
  receiptPreview: LocalHistoryReceiptPreviewState;
}>) {
  const t = copyFor(locale);
  if (receiptPreview.kind === "idle") {
    return <DetailsState message={t("receiptPreview.select")} />;
  }
  if (receiptPreview.kind === "loading") {
    return <DetailsState message={t("receiptPreview.loading")} />;
  }
  if (receiptPreview.kind === "not-found") {
    return <DetailsState message={t("receiptPreview.notFound")} />;
  }
  if (receiptPreview.kind === "failed") {
    return <DetailsState message={t("receiptPreview.failed")} />;
  }
  return <LocalHistoryReceiptPreview document={receiptPreview.document} />;
}

function ReprintResult({
  locale,
  reprint,
}: Readonly<{
  locale: LocalHistoryLocale;
  reprint: PresenterState["reprint"];
}>) {
  const t = copyFor(locale);
  if (reprint.kind === "succeeded") {
    return (
      <Text
        style={styles.reprintSuccess}
        testID="local-history-reprint-succeeded"
      >
        {t("reprint.succeeded")}
      </Text>
    );
  }
  if (reprint.kind === "failed") {
    return (
      <Text
        style={styles.reprintFailure}
        testID="local-history-reprint-failed"
      >
        {t("reprint.failed")}
      </Text>
    );
  }
  return null;
}

function LocalOrderDetails({
  businessTimeZone,
  compact,
  locale,
  value,
}: Readonly<{
  businessTimeZone: string;
  compact: boolean;
  locale: LocalHistoryLocale;
  value: LocalHistoryDetails;
}>) {
  const t = copyFor(locale);
  return (
    <ScrollView contentContainerStyle={styles.detailsContent}>
      <View style={styles.detailsIdentity}>
        <View style={styles.detailMain}>
          <Text style={styles.detailsOrder}>
            {t("list.sequence", { sequence: value.localSequence })}
          </Text>
          <Text numberOfLines={compact ? 2 : 1} style={styles.rowMeta}>
            {formatDateTime(
              value.soldAtIso,
              locale,
              businessTimeZone,
            )}{" "}
            ·{" "}
            {shortOrderGuid(value.orderGuid)} · {value.cashierName}
          </Text>
          <Text
            style={[
              styles.detailsStatus,
              orderStateTone(value.state),
            ]}
          >
            {orderStateLabel(value.state, locale)}
          </Text>
        </View>
        <View style={styles.detailsTotals}>
          <Text style={styles.detailsTotalLabel}>
            {t("details.actual")}
          </Text>
          <Text style={[styles.detailsTotal, compact && styles.detailsTotalCompact]}>
            {formatAud(value.actualAmountCents, locale)}
          </Text>
        </View>
      </View>

      <Text style={styles.sectionTitle}>{t("details.lines")}</Text>
      {value.lines.map((line) => (
        <OrderLine
          key={line.lineId}
          line={line}
          locale={locale}
        />
      ))}

      <Text style={styles.sectionTitle}>{t("details.payments")}</Text>
      {value.tenders.map((tender, index) => (
        <TenderRow
          key={`${tender.method}-${tender.amountCents}-${index}`}
          locale={locale}
          tender={tender}
        />
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

function OrderLine({
  line,
  locale,
}: Readonly<{
  line: LocalHistoryLine;
  locale: LocalHistoryLocale;
}>) {
  const t = copyFor(locale);
  return (
    <View style={styles.detailRow}>
      <View style={styles.detailMain}>
        <Text style={styles.detailName}>{line.displayName}</Text>
        <Text numberOfLines={1} style={styles.rowMeta}>
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
  );
}

function TenderRow({
  locale,
  tender,
}: Readonly<{
  locale: LocalHistoryLocale;
  tender: LocalHistoryTender;
}>) {
  return (
    <View style={styles.paymentRow}>
      <Text style={styles.detailName}>
        {localHistoryText(locale, `method.${tender.method}`)}
      </Text>
      <Text style={styles.orderAmount}>
        {formatAud(tender.amountCents, locale)}
      </Text>
    </View>
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
  locale: LocalHistoryLocale;
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
  compact,
  grow = false,
  label,
  maxLength,
  onChangeText,
  placeholder,
  testID,
  value,
}: Readonly<{
  compact: boolean;
  grow?: boolean;
  label: string;
  maxLength?: number;
  onChangeText(value: string): void;
  placeholder: string;
  testID: string;
  value: string;
}>) {
  return (
    <View
      style={[
        styles.filterField,
        grow && styles.filterFieldGrow,
        compact && grow && styles.filterFieldGrowCompact,
      ]}
    >
      <Text style={styles.fieldLabel}>{label}</Text>
      <PosKeyboardAwareTextInput
        autoCapitalize="none"
        autoCorrect={false}
        maxLength={maxLength}
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
  compact,
  label,
  locale,
  onChange,
  testID,
  value,
  width,
}: Readonly<{
  compact: boolean;
  label: string;
  locale: LocalHistoryLocale;
  onChange(value: string | null): void;
  testID: string;
  value: string;
  width: number;
}>) {
  return (
    <View
      style={[
        styles.filterField,
        { width },
        compact && styles.dateFilterCompact,
      ]}
    >
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
  compact = false,
  disabled = false,
  label,
  onPress,
  sound = "tap",
  testID,
  tone = "primary",
}: Readonly<{
  compact?: boolean;
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
        compact && styles.actionButtonCompact,
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
        numberOfLines={1}
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
  loading = false,
  message,
  onAction,
  testID,
}: Readonly<{
  actionLabel?: string;
  loading?: boolean;
  message: string;
  onAction?(): void;
  testID: string;
}>) {
  return (
    <View style={styles.centered} testID={testID}>
      {loading ? (
        <ActivityIndicator color={posColors.orange} size="large" />
      ) : null}
      <Text style={styles.centeredTitle}>{message}</Text>
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

function copyFor(locale: LocalHistoryLocale) {
  return (
    key: LocalHistoryCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => localHistoryText(locale, key, values);
}

function nullableText(value: string): string | null {
  const normalized = value.trim();
  return normalized.length === 0 ? null : normalized;
}

function dateInputValue(
  value: string,
  businessTimeZone: string,
): string {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "";
  const parts = new Intl.DateTimeFormat("en-AU", {
    timeZone: businessTimeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(date);
  const year = parts.find((part) => part.type === "year")?.value;
  const month = parts.find((part) => part.type === "month")?.value;
  const day = parts.find((part) => part.type === "day")?.value;
  if (!year || !month || !day) return "";
  return `${year}-${month}-${day}`;
}

function shortOrderGuid(orderGuid: string): string {
  return `#${orderGuid.slice(-8).toUpperCase()}`;
}

function formatAud(cents: number, locale: LocalHistoryLocale): string {
  return new Intl.NumberFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    style: "currency",
    currency: "AUD",
  }).format(cents / 100);
}

function formatDateTime(
  value: string,
  locale: LocalHistoryLocale,
  businessTimeZone: string,
): string {
  return new Intl.DateTimeFormat(
    locale === "zh" ? "zh-CN" : "en-AU",
    {
      dateStyle: "medium",
      timeStyle: "short",
      timeZone: businessTimeZone,
    },
  ).format(new Date(value));
}

function paymentSummaryLabel(
  summary: string | null,
  locale: LocalHistoryLocale,
): string {
  if (!summary?.trim()) {
    return localHistoryText(locale, "list.payment");
  }
  const methods = summary.split(",").map((part) => part.trim().toLowerCase());
  if (
    methods.some(
      (method) =>
        method !== "cash" &&
        method !== "card" &&
        method !== "voucher",
    )
  ) {
    return localHistoryText(locale, "list.payment");
  }
  return methods
    .map((method) =>
      localHistoryText(
        locale,
        `method.${method as "cash" | "card" | "voucher"}`,
      ),
    )
    .join(locale === "zh" ? "、" : ", ");
}

function orderStateLabel(
  state: LocalHistorySummary["state"],
  locale: LocalHistoryLocale,
): string {
  return localHistoryText(locale, `orderState.${state}`);
}

function orderStateTone(
  state: LocalHistorySummary["state"],
) {
  if (state === "Synced" || state === "CompletedLocal") {
    return styles.statusPositive;
  }
  if (
    state === "Rejected" ||
    state === "Blocked403"
  ) {
    return styles.statusNegative;
  }
  return styles.statusPending;
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
  headerCompact: {
    minHeight: 68,
    paddingHorizontal: 12,
    gap: 8,
  },
  headerIdentity: {
    flex: 1,
    minWidth: 0,
  },
  title: {
    color: posColors.ink,
    fontSize: 27,
    fontWeight: "800",
    letterSpacing: -0.4,
  },
  titleCompact: {
    fontSize: 22,
  },
  subtitle: {
    color: posColors.mutedInk,
    fontSize: 13,
    marginTop: 2,
  },
  filters: {
    flexDirection: "row",
    flexWrap: "wrap",
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
  filtersCompact: {
    gap: 8,
    paddingHorizontal: 12,
    paddingTop: 8,
    paddingBottom: 8,
  },
  filterField: {
    width: 150,
    gap: 5,
  },
  dateFilterCompact: {
    flexGrow: 0,
  },
  filterFieldGrow: {
    flex: 1,
    minWidth: 220,
  },
  filterFieldGrowCompact: {
    minWidth: 180,
  },
  fieldLabel: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
    textTransform: "uppercase",
    letterSpacing: 0.5,
  },
  input: {
    minHeight: LOCAL_HISTORY_MIN_TOUCH_TARGET,
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
  workspace: {
    flex: 1,
    flexDirection: "row",
  },
  listPane: {
    flex: 0.43,
    minWidth: 320,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: posColors.border,
    borderRadius: 12,
    backgroundColor: posColors.surface,
  },
  detailsPane: {
    flex: 0.57,
    minWidth: 400,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: posColors.border,
    borderRadius: 12,
    backgroundColor: posColors.surface,
  },
  paneCompact: {
    minWidth: 0,
  },
  paneHeader: {
    minHeight: 50,
    paddingHorizontal: 12,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: posColors.border,
  },
  detailsTabs: {
    flex: 1,
    minWidth: 0,
    flexDirection: "row",
    gap: 4,
  },
  paneTab: {
    flex: 1,
    minWidth: 0,
    minHeight: LOCAL_HISTORY_MIN_TOUCH_TARGET,
    paddingHorizontal: 8,
    alignItems: "center",
    justifyContent: "center",
    borderBottomWidth: 3,
    borderBottomColor: "transparent",
  },
  paneTabActive: {
    borderBottomColor: posColors.orange,
  },
  paneTabText: {
    color: posColors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
  },
  paneTabTextActive: {
    color: posColors.ink,
    fontWeight: "800",
  },
  detailsHeaderActions: {
    flexShrink: 0,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    marginLeft: 8,
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
    flexShrink: 1,
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
    gap: 8,
  },
  rowSecondary: {
    flexShrink: 1,
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },
  statusPill: {
    marginLeft: "auto",
    maxWidth: "45%",
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: 999,
    overflow: "hidden",
    fontSize: 11,
    fontWeight: "800",
  },
  statusPositive: {
    color: posColors.ink,
    backgroundColor: posColors.greenSoft,
  },
  statusPending: {
    color: posColors.ink,
    backgroundColor: posColors.yellowSoft,
  },
  statusNegative: {
    color: posColors.red,
    backgroundColor: posColors.redSoft,
  },
  loadMore: {
    alignItems: "center",
    paddingVertical: 4,
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
  detailsStatus: {
    alignSelf: "flex-start",
    marginTop: 7,
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: 999,
    overflow: "hidden",
    fontSize: 11,
    fontWeight: "800",
  },
  detailsTotals: {
    alignItems: "flex-end",
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
  detailsTotalCompact: {
    fontSize: 22,
  },
  reprintAction: {
    alignItems: "flex-start",
    gap: 6,
    marginTop: 4,
  },
  reprintSuccess: {
    paddingHorizontal: 16,
    paddingTop: 8,
    color: posColors.green,
    fontSize: 13,
  },
  reprintFailure: {
    paddingHorizontal: 16,
    paddingTop: 8,
    color: posColors.red,
    fontSize: 13,
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
    minWidth: 0,
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
    minHeight: LOCAL_HISTORY_MIN_TOUCH_TARGET,
    minWidth: 112,
    paddingHorizontal: 16,
    borderRadius: 8,
    borderWidth: 1,
    alignItems: "center",
    justifyContent: "center",
  },
  actionButtonCompact: {
    minWidth: 96,
    paddingHorizontal: 12,
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
