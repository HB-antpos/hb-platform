import { MaterialCommunityIcons } from "@expo/vector-icons";
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

import {
  HISTORY_QUICK_DATE_RANGES,
  quickHistoryDateRange,
  type HistoryQuickDateRange,
} from "@hb/pos-domain/features/remote-history/quick-date-range";
import { HistoryQuickRangeRow } from "@/ui/controls/history-quick-range-row";
import { PosButton } from "@/ui/controls/pos-button";
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPill, type PosPillTone } from "@/ui/controls/pos-pill";
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

export const LOCAL_HISTORY_MIN_TOUCH_TARGET = 44;

const LOCAL_HISTORY_COMPACT_BREAKPOINT = 900;
/** 宽度不足这个值时日期框收窄一档，让筛选条在 1024 宽的 iPad 上仍能一行排完。 */
const LOCAL_HISTORY_WIDE_BREAKPOINT = 1100;
/** 左侧订单列表的固定宽度（设计图 420）。 */
export const LOCAL_HISTORY_LIST_WIDTH = 420;

export type LocalHistoryLayout = Readonly<{
  compact: boolean;
  workspaceGap: number;
  workspacePadding: number;
  /** 开始 / 结束日期框的宽度。 */
  filterWidth: number;
  /** 左列表固定宽度；紧凑模式为 null，改为按比例分栏。 */
  listWidth: number | null;
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
        listWidth: null,
      }
    : {
        compact: false,
        workspaceGap: 12,
        workspacePadding: 12,
        filterWidth: width < LOCAL_HISTORY_WIDE_BREAKPOINT ? 150 : 166,
        listWidth: LOCAL_HISTORY_LIST_WIDTH,
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
type DetailsPaneKey = "details" | "receiptPreview";

export function LocalHistoryScreen({
  presenter,
  onBack,
}: LocalHistoryScreenProps) {
  usePosThemeVersion();
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
    <PosScreenFrame
      active="local-history"
      header={
        <PosPageHeader
          actions={
            <PosButton
              disabled={loading}
              icon="refresh"
              label={
                loading ? t("action.refreshing") : t("action.refresh")
              }
              loading={loading}
              onPress={() => {
                void presenter.refresh();
              }}
              size="medium"
              testID="local-history-refresh"
              tone="quiet"
            />
          }
          backLabel={t("action.back")}
          backTestID="local-history-back"
          onBack={onBack}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
      testID="local-history-screen"
    >
      {/* 筛选条：快捷范围、起止日期、搜索框、应用筛选一行排完；宽度不够时自动换行。 */}
      <PosKeyboardAwareScrollView
        contentContainerStyle={[
          styles.filters,
          layout.compact && styles.filtersCompact,
        ]}
        style={styles.filtersScroll}
        testID="local-history-filters-keyboard-scroll"
      >
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
          selected={matchingQuickRange(
            fromDate,
            toDate,
            state.businessTimeZone,
          )}
          testIDPrefix="local-history"
        />
        <View style={{ width: layout.filterWidth }}>
          <PosDatePickerField
            accessibilityLabel={t("filters.from")}
            locale={locale}
            onChange={(value) => {
              if (!value) return;
              setFromDate(value);
              if (filterError === "date") setFilterError(null);
            }}
            testID="local-history-date-from"
            value={fromDate}
          />
        </View>
        <Text style={styles.rangeSeparator}>
          {t("filters.rangeSeparator")}
        </Text>
        <View style={{ width: layout.filterWidth }}>
          <PosDatePickerField
            accessibilityLabel={t("filters.to")}
            locale={locale}
            onChange={(value) => {
              if (!value) return;
              setToDate(value);
              if (filterError === "date") setFilterError(null);
            }}
            testID="local-history-date-to"
            value={toDate}
          />
        </View>
        <SearchField
          compact={layout.compact}
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
        <PosButton
          disabled={loading}
          icon="filter-check-outline"
          label={t("action.apply")}
          onPress={() => applyFilters()}
          size="medium"
          testID="local-history-apply-filters"
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
          icon="text-box-search-outline"
          message={t("state.empty")}
          testID="local-history-empty"
        />
      ) : null}
      {!hasRows && state.kind === "failed" ? (
        <CenteredState
          icon="alert-circle-outline"
          message={t("state.failed")}
          testID="local-history-failed"
        />
      ) : null}
      {!hasRows && state.kind === "unauthorized" ? (
        <CenteredState
          icon="lock-outline"
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
          {/* 左：订单列表固定宽度，「加载更多」固定在列表下方，不随列表滚动。 */}
          <View
            style={[
              styles.listPane,
              layout.listWidth === null
                ? styles.listPaneCompact
                : { width: layout.listWidth },
            ]}
            testID="local-history-list-pane"
          >
            <FlatList
              accessibilityLabel={t("list.title")}
              contentContainerStyle={styles.orderList}
              data={state.rows}
              keyExtractor={(row) => row.orderGuid}
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
              style={styles.orderListScroll}
              testID="local-history-list"
            />
            {state.hasMore ? (
              <PosButton
                disabled={loading || state.loadingMore}
                icon="chevron-down"
                label={
                  state.loadingMore
                    ? t("action.loadingMore")
                    : t("action.loadMore")
                }
                loading={state.loadingMore}
                onPress={() => {
                  void presenter.loadMore();
                }}
                size="medium"
                testID="local-history-load-more"
                tone="quiet"
              />
            ) : null}
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
    </PosScreenFrame>
  );
}

export function LocalHistoryUnavailableScreen({
  onBack,
}: Readonly<{ onBack?(): void }>) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveLocalHistoryLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t = copyFor(locale);
  return (
    <PosScreenFrame
      active="local-history"
      header={
        <PosPageHeader
          backLabel={t("action.back")}
          backTestID="local-history-back"
          onBack={onBack}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
    >
      <CenteredState
        {...(onBack
          ? { actionLabel: t("action.back"), onAction: onBack }
          : {})}
        icon="alert-circle-outline"
        message={t("state.unavailable")}
        testID="local-history-unavailable"
      />
    </PosScreenFrame>
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
  usePosThemeVersion();
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
      style={({ pressed }) => [
        styles.orderRow,
        selected && styles.orderRowSelected,
        pressed && !selected && styles.orderRowPressed,
      ]}
      testID={`local-history-order-${row.orderGuid}`}
    >
      <View style={styles.orderRowMain}>
        <View style={styles.orderTitleRow}>
          <Text numberOfLines={1} style={styles.orderNumber}>
            {t("list.sequence", { sequence: row.localSequence })}
          </Text>
          <PosPill
            label={orderStateLabel(row.state, locale)}
            style={styles.pillInline}
            tone={orderStateTone(row.state)}
          />
        </View>
        <Text numberOfLines={1} style={styles.rowMeta}>
          {formatDateTime(row.soldAtIso, locale, businessTimeZone)} ·{" "}
          {shortOrderGuid(row.orderGuid)} · {row.cashierName}
        </Text>
        <View style={styles.rowBottom}>
          <Text style={styles.rowMeta}>
            {t("list.items", { count: row.lineCount })}
          </Text>
          <Text style={styles.rowMeta}>·</Text>
          <Text numberOfLines={1} style={[styles.rowMeta, styles.rowShrink]}>
            {paymentSummaryLabel(row.paymentSummary, locale)}
          </Text>
        </View>
      </View>
      <Text style={styles.orderAmount}>
        {formatAud(row.actualAmountCents, locale)}
      </Text>
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
  usePosThemeVersion();
  const t = copyFor(locale);
  const [activePane, setActivePane] = useState<DetailsPaneKey>("details");
  const value = details.kind === "ready" ? details.value : null;
  return (
    <View
      style={[
        styles.detailsPane,
        compact && styles.detailsPaneCompact,
      ]}
      testID="local-history-details"
    >
      {value ? (
        <View style={styles.detailsIdentity}>
          <View style={styles.detailMain}>
            <View style={styles.orderTitleRow}>
              <Text numberOfLines={1} style={styles.detailsOrder}>
                {t("list.sequence", { sequence: value.localSequence })}
              </Text>
              <PosPill
                label={orderStateLabel(value.state, locale)}
                style={styles.pillInline}
                tone={orderStateTone(value.state)}
              />
            </View>
            <Text numberOfLines={compact ? 2 : 1} style={styles.detailsMeta}>
              {formatDateTime(
                value.soldAtIso,
                locale,
                businessTimeZone,
              )}{" "}
              ·{" "}
              {shortOrderGuid(value.orderGuid)} · {value.cashierName}
            </Text>
          </View>
          <View style={styles.detailsTotals}>
            <Text style={styles.detailsTotalLabel}>
              {t("details.actual")}
            </Text>
            <Text
              style={[
                styles.detailsTotal,
                compact && styles.detailsTotalCompact,
              ]}
            >
              {formatAud(value.actualAmountCents, locale)}
            </Text>
          </View>
        </View>
      ) : null}

      {/* 两个页签做成整宽分段按钮；切换订单后保留当前页签。 */}
      <PosSegmented<DetailsPaneKey>
        onChange={setActivePane}
        options={[
          {
            icon: "format-list-bulleted",
            label: t("details.title"),
            testID: "local-history-details-tab",
            value: "details",
          },
          {
            icon: "receipt-text-outline",
            label: t("receiptPreview.title"),
            testID: "local-history-receipt-preview-tab",
            value: "receiptPreview",
          },
        ]}
        value={activePane}
      />

      <View style={styles.detailsBody}>
        {activePane === "details" ? (
          <DetailsContent details={details} locale={locale} />
        ) : (
          <ReceiptPreviewContent
            locale={locale}
            receiptPreview={receiptPreview}
          />
        )}
      </View>

      <ReprintResult locale={locale} reprint={reprint} />

      {/* 汇总与「重打小票」固定在详情底部，不随商品行滚动。 */}
      {value || canReprint ? (
        <View style={styles.detailsFooter}>
          {value ? (
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
                  tone="discount"
                  value={value.discountCents}
                />
              ) : null}
              <SummaryRow
                label={t("details.actual")}
                locale={locale}
                tone="emphasized"
                value={value.actualAmountCents}
              />
            </View>
          ) : (
            <View style={styles.detailsFooterSpacer} />
          )}
          {canReprint ? (
            <PosButton
              disabled={reprint.kind === "submitting"}
              icon="printer-outline"
              label={
                reprint.kind === "submitting"
                  ? t("action.reprinting")
                  : t("action.reprint")
              }
              loading={reprint.kind === "submitting"}
              onPress={onReprint}
              size={compact ? "medium" : "button"}
              style={[
                styles.reprintButton,
                compact && styles.reprintButtonCompact,
              ]}
              testID="local-history-reprint"
            />
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

function DetailsContent({
  details,
  locale,
}: Readonly<{
  details: PresenterState["details"];
  locale: LocalHistoryLocale;
}>) {
  usePosThemeVersion();
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
    return <DetailsState loading message={t("state.loading")} />;
  }
  return <LocalOrderDetails locale={locale} value={details.value} />;
}

function ReceiptPreviewContent({
  locale,
  receiptPreview,
}: Readonly<{
  locale: LocalHistoryLocale;
  receiptPreview: LocalHistoryReceiptPreviewState;
}>) {
  usePosThemeVersion();
  const t = copyFor(locale);
  if (receiptPreview.kind === "idle") {
    return <DetailsState message={t("receiptPreview.select")} />;
  }
  if (receiptPreview.kind === "loading") {
    return <DetailsState loading message={t("receiptPreview.loading")} />;
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
  usePosThemeVersion();
  const t = copyFor(locale);
  if (reprint.kind === "succeeded") {
    return (
      <Text
        style={[styles.reprintResult, styles.reprintSuccess]}
        testID="local-history-reprint-succeeded"
      >
        {t("reprint.succeeded")}
      </Text>
    );
  }
  if (reprint.kind === "failed") {
    return (
      <Text
        style={[styles.reprintResult, styles.reprintFailure]}
        testID="local-history-reprint-failed"
      >
        {t("reprint.failed")}
      </Text>
    );
  }
  return null;
}

function LocalOrderDetails({
  locale,
  value,
}: Readonly<{
  locale: LocalHistoryLocale;
  value: LocalHistoryDetails;
}>) {
  usePosThemeVersion();
  const t = copyFor(locale);
  return (
    <ScrollView contentContainerStyle={styles.detailsContent}>
      <Text style={styles.sectionTitle}>{t("details.lines")}</Text>
      <View>
        {value.lines.map((line, index) => (
          <OrderLine
            first={index === 0}
            key={line.lineId}
            line={line}
            locale={locale}
          />
        ))}
      </View>

      <Text style={styles.sectionTitle}>{t("details.payments")}</Text>
      <View>
        {value.tenders.map((tender, index) => (
          <TenderRow
            first={index === 0}
            key={`${tender.method}-${tender.amountCents}-${index}`}
            locale={locale}
            tender={tender}
          />
        ))}
      </View>
    </ScrollView>
  );
}

function OrderLine({
  first,
  line,
  locale,
}: Readonly<{
  first: boolean;
  line: LocalHistoryLine;
  locale: LocalHistoryLocale;
}>) {
  usePosThemeVersion();
  const t = copyFor(locale);
  const codes = [line.lookupCode, line.itemNumber, line.productCode]
    .filter((part): part is string => Boolean(part))
    .join(" · ");
  // 行折扣紧跟在条码 / 货号后面，用红色标出。
  const discount =
    line.discountCents !== 0
      ? t("details.discount", {
          amount: formatAud(line.discountCents, locale),
        })
      : null;
  return (
    <View style={[styles.itemRow, first && styles.itemRowFirst]}>
      <View style={styles.detailMain}>
        <Text style={styles.itemName}>{line.displayName}</Text>
        {codes || discount ? (
          <Text numberOfLines={1} style={styles.itemMeta}>
            {codes}
            {discount ? (
              <Text style={styles.itemDiscount}>
                {codes ? " · " : ""}
                {discount}
              </Text>
            ) : null}
          </Text>
        ) : null}
      </View>
      <Text style={styles.itemQuantity}>
        {t("details.quantity", { quantity: line.quantity })} ·{" "}
        {formatAud(line.unitPriceCents, locale)}
      </Text>
      <View style={styles.itemAmount}>
        {/* 退货行用红色回车图标标出，销售行不加标记。 */}
        {line.kind === "return" ? (
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.red}
            name="keyboard-return"
            size={18}
          />
        ) : null}
        <Text
          style={[
            styles.itemValue,
            line.kind === "return" && styles.itemValueReturn,
          ]}
        >
          {formatAud(line.actualAmountCents, locale)}
        </Text>
      </View>
    </View>
  );
}

function TenderRow({
  first,
  locale,
  tender,
}: Readonly<{
  first: boolean;
  locale: LocalHistoryLocale;
  tender: LocalHistoryTender;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.itemRow, first && styles.itemRowFirst]}>
      <Text style={[styles.itemName, styles.detailMain]}>
        {localHistoryText(locale, `method.${tender.method}`)}
      </Text>
      <Text style={styles.itemValue}>
        {formatAud(tender.amountCents, locale)}
      </Text>
    </View>
  );
}

function SummaryRow({
  label,
  locale,
  tone = "normal",
  value,
}: Readonly<{
  label: string;
  locale: LocalHistoryLocale;
  tone?: "normal" | "discount" | "emphasized";
  value: number;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.summaryRow}>
      <Text style={styles.summaryLabel}>{label}</Text>
      <Text
        style={[
          styles.summaryValue,
          tone === "discount" && styles.summaryDiscount,
          tone === "emphasized" && styles.summaryStrong,
        ]}
      >
        {formatAud(value, locale)}
      </Text>
    </View>
  );
}

/** 搜索框：放大镜图标 + 输入框，外框与日期框同高（56）。 */
function SearchField({
  compact,
  label,
  maxLength,
  onChangeText,
  placeholder,
  testID,
  value,
}: Readonly<{
  compact: boolean;
  label: string;
  maxLength?: number;
  onChangeText(value: string): void;
  placeholder: string;
  testID: string;
  value: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.searchField, compact && styles.searchFieldCompact]}>
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.placeholder}
        name="magnify"
        size={22}
      />
      <PosKeyboardAwareTextInput
        accessibilityLabel={label}
        autoCapitalize="none"
        autoCorrect={false}
        maxLength={maxLength}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={posColors.placeholder}
        selectionColor={posColors.accent}
        style={styles.searchInput}
        testID={testID}
        value={value}
      />
    </View>
  );
}

function CenteredState({
  actionLabel,
  icon,
  loading = false,
  message,
  onAction,
  testID,
}: Readonly<{
  actionLabel?: string;
  icon?: "alert-circle-outline" | "lock-outline" | "text-box-search-outline";
  loading?: boolean;
  message: string;
  onAction?(): void;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.centered} testID={testID}>
      {loading ? (
        <ActivityIndicator color={posColors.accent} size="large" />
      ) : icon ? (
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.mutedInk}
          name={icon}
          size={44}
        />
      ) : null}
      <Text style={styles.centeredTitle}>{message}</Text>
      {actionLabel && onAction ? (
        <PosButton
          icon="arrow-left"
          label={actionLabel}
          onPress={onAction}
          size="medium"
          sound="navigate"
          testID={`${testID}-back`}
        />
      ) : null}
    </View>
  );
}

function DetailsState({
  loading = false,
  message,
}: Readonly<{ loading?: boolean; message: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.detailsState}>
      {loading ? <ActivityIndicator color={posColors.accent} /> : null}
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

/**
 * 当前起止日期正好等于哪个快捷范围（只用于高亮分段按钮）。
 * 时区解析失败时不高亮，不影响筛选本身。
 */
function matchingQuickRange(
  from: string,
  to: string,
  businessTimeZone: string,
): HistoryQuickDateRange | null {
  try {
    const now = new Date();
    return (
      HISTORY_QUICK_DATE_RANGES.find((kind) => {
        const range = quickHistoryDateRange(kind, now, businessTimeZone);
        return range.from === from && range.to === to;
      }) ?? null
    );
  } catch {
    return null;
  }
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

/** 同步状态对应的标签色：已完成 / 已同步为绿，被拒 / 受阻为红，其余（待同步、同步中）为黄。 */
function orderStateTone(
  state: LocalHistorySummary["state"],
): PosPillTone {
  if (state === "Synced" || state === "CompletedLocal") {
    return "good";
  }
  if (
    state === "Rejected" ||
    state === "Blocked403"
  ) {
    return "danger";
  }
  return "warning";
}

const styles = createPosStyles((colors) => ({
  // ---------- 筛选条 ----------
  filters: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    paddingHorizontal: 12,
    paddingTop: 12,
  },
  filtersCompact: {
    gap: 8,
    paddingHorizontal: 8,
    paddingTop: 8,
  },
  filtersScroll: {
    flexGrow: 0,
  },
  rangeSeparator: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "900",
  },
  searchField: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flex: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: posTouch.medium,
    minWidth: 180,
    paddingHorizontal: 14,
  },
  searchFieldCompact: {
    minWidth: 160,
  },
  searchInput: {
    alignSelf: "stretch",
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontWeight: "600",
    minHeight: LOCAL_HISTORY_MIN_TOUCH_TARGET,
    minWidth: 0,
    padding: 0,
  },
  validation: {
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    paddingHorizontal: 14,
    paddingTop: 8,
  },

  // ---------- 列表 + 详情 ----------
  workspace: {
    flex: 1,
    flexDirection: "row",
    minHeight: 0,
  },
  listPane: {
    flexShrink: 0,
    gap: 10,
    minHeight: 0,
  },
  // 紧凑模式（宽度 < 900）：两栏按比例分，允许继续收窄。
  listPaneCompact: {
    flex: 0.43,
    flexShrink: 1,
    minWidth: 0,
  },
  orderListScroll: {
    flex: 1,
    minHeight: 0,
  },
  orderList: {
    gap: 6,
  },
  orderRow: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 12,
    minHeight: 84,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  orderRowPressed: {
    backgroundColor: colors.surfaceAlt,
  },
  // 选中行描边加粗 1pt，内边距相应减 1，避免选中时内容跳动。
  orderRowSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
    borderWidth: 2.5,
    paddingHorizontal: 13,
    paddingVertical: 7,
  },
  orderRowMain: {
    flex: 1,
    gap: 3,
    minWidth: 0,
  },
  orderTitleRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    minWidth: 0,
  },
  orderNumber: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 16,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  pillInline: {
    alignSelf: "center",
    flexShrink: 0,
  },
  orderAmount: {
    color: colors.ink,
    flexShrink: 0,
    fontSize: 18,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  rowMeta: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
  },
  rowBottom: {
    alignItems: "center",
    flexDirection: "row",
    gap: 5,
  },
  rowShrink: {
    flexShrink: 1,
  },
  detailsPane: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flex: 1,
    gap: 10,
    minHeight: 0,
    minWidth: 0,
    paddingHorizontal: 16,
    paddingVertical: 14,
  },
  detailsPaneCompact: {
    flex: 0.57,
    paddingHorizontal: 10,
    paddingVertical: 10,
  },
  detailsIdentity: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 16,
    justifyContent: "space-between",
  },
  detailMain: {
    flex: 1,
    gap: 3,
    minWidth: 0,
  },
  detailsOrder: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 22,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  detailsMeta: {
    color: colors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    marginTop: 1,
  },
  detailsTotals: {
    alignItems: "flex-end",
    flexShrink: 0,
  },
  detailsTotalLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
  },
  detailsTotal: {
    color: colors.ink,
    fontSize: 40,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -1,
    lineHeight: 44,
  },
  detailsTotalCompact: {
    fontSize: 28,
    lineHeight: 32,
  },
  detailsBody: {
    flex: 1,
    minHeight: 0,
  },
  detailsContent: {
    gap: 8,
    paddingBottom: 4,
  },
  sectionTitle: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
    marginHorizontal: 2,
    marginTop: 2,
  },
  itemRow: {
    alignItems: "center",
    borderTopColor: colors.border,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: 12,
    minHeight: 48,
    paddingVertical: 6,
  },
  itemRowFirst: {
    borderTopWidth: 0,
  },
  itemName: {
    color: colors.ink,
    fontSize: 14.5,
    fontWeight: "700",
  },
  itemMeta: {
    color: colors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
  },
  itemDiscount: {
    color: colors.red,
  },
  itemQuantity: {
    color: colors.mutedInk,
    flexShrink: 0,
    fontSize: 13.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  itemAmount: {
    alignItems: "center",
    flexDirection: "row",
    flexShrink: 0,
    gap: 4,
    justifyContent: "flex-end",
    minWidth: 76,
  },
  itemValue: {
    color: colors.ink,
    fontSize: 14.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    textAlign: "right",
  },
  itemValueReturn: {
    color: colors.red,
  },
  reprintResult: {
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    fontSize: 14,
    fontWeight: "700",
    overflow: "hidden",
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  reprintSuccess: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
    color: colors.green,
  },
  reprintFailure: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    color: colors.red,
  },
  detailsFooter: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 12,
  },
  detailsFooterSpacer: {
    flex: 1,
  },
  summaryCard: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minWidth: 180,
    paddingHorizontal: 14,
    paddingVertical: 6,
  },
  summaryRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    minHeight: 30,
  },
  summaryLabel: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
  },
  summaryValue: {
    color: colors.ink,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  summaryDiscount: {
    color: colors.red,
  },
  summaryStrong: {
    fontSize: 17,
    fontWeight: "900",
  },
  // 设计图 230 宽，高度随左侧汇总卡拉伸（三行汇总时约 96）；
  // 详情栏很窄时允许收到 150，把宽度让给汇总金额。
  reprintButton: {
    flexShrink: 1,
    minWidth: 150,
    width: 230,
  },
  reprintButtonCompact: {
    width: 150,
  },

  // ---------- 空 / 加载 / 错误 / 无权限 ----------
  centered: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flex: 1,
    gap: 14,
    justifyContent: "center",
    margin: 12,
    minHeight: 180,
    padding: 28,
  },
  centeredTitle: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
    textAlign: "center",
  },
  centeredHint: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 22,
    maxWidth: 520,
    textAlign: "center",
  },
  detailsState: {
    alignItems: "center",
    flex: 1,
    gap: 12,
    justifyContent: "center",
    minHeight: 160,
    padding: 24,
  },
}));
