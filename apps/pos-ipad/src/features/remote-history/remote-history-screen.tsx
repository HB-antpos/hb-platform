import { MaterialCommunityIcons } from "@expo/vector-icons";
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
import {
  HISTORY_QUICK_DATE_RANGES,
  quickHistoryDateRange,
  type HistoryQuickDateRange,
} from "@hb/pos-domain/features/remote-history/quick-date-range";
import { HistoryQuickRangeRow } from "@/ui/controls/history-quick-range-row";
import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPill } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

export const REMOTE_HISTORY_MIN_TOUCH_TARGET = 44;

/** 左侧订单列表的固定宽度，与本机历史一致（设计图 420）。 */
export const REMOTE_HISTORY_LIST_WIDTH = 420;

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
  usePosThemeVersion();
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
  // 离线时远程历史整体不可查：筛选条和刷新一起置灰，恢复在线后自动可用。
  const offline = state.kind === "offline";
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
  const selectedRow =
    state.rows.find((row) => row.orderGuid === state.selectedOrderGuid) ??
    null;

  return (
    <PosScreenFrame
      active="remote-history"
      header={
        <PosPageHeader
          actions={
            <PosButton
              disabled={loading || offline}
              icon="refresh"
              label={loading ? t("action.refreshing") : t("action.refresh")}
              loading={loading}
              onPress={() => {
                void presenter.refresh();
              }}
              size="medium"
              testID="remote-history-refresh"
              tone="quiet"
            />
          }
          backLabel={t("action.back")}
          backTestID="remote-history-back"
          onBack={onBack}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
      testID="remote-history-screen"
    >
      {/* 筛选条与本机历史同一套排法，多一个「终端」筛选框。 */}
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.filters}
        style={styles.filtersScroll}
        testID="remote-history-filters-keyboard-scroll"
      >
        <HistoryQuickRangeRow
          disabled={loading || offline}
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
          selected={matchingQuickRange(fromDate, toDate)}
          testIDPrefix="remote-history"
        />
        <View style={styles.dateField}>
          <PosDatePickerField
            accessibilityLabel={t("filters.from")}
            disabled={offline}
            locale={locale}
            onChange={(value) => {
              if (!value) return;
              setFromDate(value);
              setDateInvalid(false);
            }}
            testID="remote-history-date-from"
            value={fromDate}
          />
        </View>
        <Text style={styles.rangeSeparator}>
          {t("filters.rangeSeparator")}
        </Text>
        <View style={styles.dateField}>
          <PosDatePickerField
            accessibilityLabel={t("filters.to")}
            disabled={offline}
            locale={locale}
            onChange={(value) => {
              if (!value) return;
              setToDate(value);
              setDateInvalid(false);
            }}
            testID="remote-history-date-to"
            value={toDate}
          />
        </View>
        <FilterInput
          disabled={offline}
          icon="tablet"
          label={t("filters.device")}
          onChangeText={setDeviceCode}
          placeholder={t("filters.devicePlaceholder")}
          testID="remote-history-device"
          value={deviceCode}
        />
        <FilterInput
          disabled={offline}
          grow
          icon="magnify"
          label={t("filters.keyword")}
          onChangeText={setKeyword}
          placeholder={t("filters.keywordPlaceholder")}
          testID="remote-history-keyword"
          value={keyword}
        />
        <PosButton
          disabled={loading || offline}
          icon="filter-check-outline"
          label={t("action.apply")}
          onPress={() => applyFilters()}
          size="medium"
          testID="remote-history-apply-filters"
        />
      </PosKeyboardAwareScrollView>
      {dateInvalid ? (
        <Text style={styles.validation} testID="remote-history-date-invalid">
          {t("filters.invalidDate")}
        </Text>
      ) : null}

      {/* 只读说明常驻在筛选条下方。 */}
      <View style={styles.readOnlyBanner}>
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.blue}
          name="information-outline"
          size={24}
        />
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
          icon="wifi-off"
          message={t("state.offline")}
          testID="remote-history-offline"
        />
      ) : null}
      {state.kind === "unauthorized" ? (
        <CenteredState
          icon="lock-outline"
          message={t("state.unauthorized")}
          testID="remote-history-unauthorized"
        />
      ) : null}
      {state.kind === "unavailable" ? (
        <CenteredState
          icon="alert-circle-outline"
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
          icon="text-box-search-outline"
          message={t("state.empty")}
          testID="remote-history-empty"
        />
      ) : null}
      {state.kind === "failed" ? (
        <CenteredState
          icon="alert-circle-outline"
          message={t("state.failed")}
          testID="remote-history-failed"
        />
      ) : null}
      {(state.kind === "ready" ||
        (state.kind === "loading" && state.rows.length > 0)) ? (
        <View style={styles.workspace} testID="remote-history-workspace">
          <View style={styles.listPane} testID="remote-history-list-pane">
            <FlatList
              accessibilityLabel={t("list.title")}
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
              style={styles.orderListScroll}
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
            statusLabel={selectedRow?.statusLabel ?? null}
          />
        </View>
      ) : null}
    </PosScreenFrame>
  );
}

export function RemoteHistoryUnavailableScreen({
  onBack,
}: Readonly<{ onBack?(): void }>) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveRemoteHistoryLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t = copyFor(locale);
  return (
    <PosScreenFrame
      active="remote-history"
      header={
        <PosPageHeader
          backLabel={t("action.back")}
          backTestID="remote-history-back"
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
        testID="remote-history-unavailable"
      />
    </PosScreenFrame>
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
  usePosThemeVersion();
  const t = copyFor(locale);
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={({ pressed }) => [
        styles.orderRow,
        selected && styles.orderRowSelected,
        pressed && !selected && styles.orderRowPressed,
      ]}
      testID={`remote-history-order-${row.orderGuid}`}
    >
      <View style={styles.orderRowMain}>
        <View style={styles.orderTitleRow}>
          <Text numberOfLines={1} style={styles.orderNumber}>
            {shortOrderGuid(row.orderGuid)}
          </Text>
          {row.statusLabel ? (
            <PosPill
              label={row.statusLabel}
              style={styles.pillInline}
              tone="good"
            />
          ) : null}
        </View>
        <Text numberOfLines={1} style={styles.rowMeta}>
          {formatDateTime(row.soldAtIso, locale)} · {row.deviceCode} ·{" "}
          {row.cashierName}
        </Text>
        <View style={styles.rowBottom}>
          <Text style={styles.rowMeta}>
            {t(row.lineCount === 1 ? "list.itemsOne" : "list.items", { count: row.lineCount })}
          </Text>
          <Text style={styles.rowMeta}>·</Text>
          <Text numberOfLines={1} style={[styles.rowMeta, styles.rowShrink]}>
            {row.paymentSummary ?? t("list.payment")}
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
  canReprint,
  details,
  locale,
  onRefund,
  onReprint,
  reprint,
  statusLabel,
}: Readonly<{
  canReprint: boolean;
  details: RemoteHistoryPresenter["state"]["details"];
  locale: RemoteHistoryLocale;
  onRefund: ((orderGuid: string) => void) | undefined;
  onReprint(): void;
  reprint: RemoteHistoryPresenter["state"]["reprint"];
  /** 列表里当前选中行的状态标签，详情头部沿用同一个标签。 */
  statusLabel: string | null;
}>) {
  usePosThemeVersion();
  const t = copyFor(locale);
  const value = details.kind === "ready" ? details.value : null;
  const canRefund = Boolean(value && onRefund && isOrdinarySale(value));
  return (
    <View
      accessibilityLabel={t("details.title")}
      style={styles.detailsPane}
      testID="remote-history-details"
    >
      {value ? (
        <View style={styles.detailsIdentity}>
          <View style={styles.detailMain}>
            <View style={styles.orderTitleRow}>
              <Text numberOfLines={1} style={styles.detailsOrder}>
                {shortOrderGuid(value.orderGuid)}
              </Text>
              {statusLabel ? (
                <PosPill
                  label={statusLabel}
                  style={styles.pillInline}
                  tone="good"
                />
              ) : null}
            </View>
            <Text numberOfLines={1} style={styles.detailsMeta}>
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
      ) : null}

      <View style={styles.detailsBody}>
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
          <DetailsState loading message={t("state.loading")} />
        ) : null}
        {value ? <RemoteOrderDetails locale={locale} value={value} /> : null}
      </View>

      {/* 操作固定在详情底部：可退款时两个按钮各占一半，否则只显示重打。 */}
      {value && (canRefund || canReprint) ? (
        <View style={styles.detailsFooter}>
          {reprint.kind === "succeeded" ? (
            <Text
              style={[styles.reprintResult, styles.reprintSuccess]}
              testID="remote-history-reprint-succeeded"
            >
              {t("reprint.succeeded")}
            </Text>
          ) : null}
          {reprint.kind === "failed" ? (
            <Text
              style={[styles.reprintResult, styles.reprintFailure]}
              testID="remote-history-reprint-failed"
            >
              {t("reprint.failed")}
            </Text>
          ) : null}
          <View style={styles.detailsActions}>
            {canRefund ? (
              <PosButton
                fill
                icon="cash-refund"
                label={t("action.refund")}
                onPress={() => onRefund?.(value.orderGuid)}
                testID="remote-history-refund"
              />
            ) : null}
            {canReprint ? (
              <PosButton
                disabled={reprint.kind === "submitting"}
                fill
                icon="printer-outline"
                label={
                  reprint.kind === "submitting"
                    ? t("action.reprinting")
                    : t("action.reprint")
                }
                loading={reprint.kind === "submitting"}
                onPress={onReprint}
                testID="remote-history-reprint"
              />
            ) : null}
          </View>
        </View>
      ) : null}
    </View>
  );
}

function RemoteOrderDetails({
  locale,
  value,
}: Readonly<{
  locale: RemoteHistoryLocale;
  value: RemoteOrderHistoryDetails;
}>) {
  usePosThemeVersion();
  const t = copyFor(locale);
  return (
    <ScrollView contentContainerStyle={styles.detailsContent}>
      <Text style={styles.sectionTitle}>{t("details.lines")}</Text>
      <View>
        {value.lines.map((line, index) => {
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
            <View
              key={line.orderLineGuid}
              style={[styles.itemRow, index === 0 && styles.itemRowFirst]}
            >
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
        })}
      </View>

      <Text style={styles.sectionTitle}>{t("details.payments")}</Text>
      <View>
        {value.payments.map((payment, index) => {
          const card = paymentCardLabel(payment);
          return (
            <View
              key={payment.paymentGuid}
              style={[styles.itemRow, index === 0 && styles.itemRowFirst]}
            >
              <View style={styles.detailMain}>
                <Text style={styles.itemName}>
                  {remoteHistoryText(locale, `method.${payment.method}`)}
                </Text>
                {card ? (
                  <Text numberOfLines={1} style={styles.itemMeta}>
                    {card}
                  </Text>
                ) : null}
              </View>
              <Text style={styles.itemValue}>
                {formatAud(payment.amountCents, locale)}
              </Text>
            </View>
          );
        })}
      </View>

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
  label,
  locale,
  tone = "normal",
  value,
}: Readonly<{
  label: string;
  locale: RemoteHistoryLocale;
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

/** 筛选输入框：图标 + 输入框，外框与日期框同高（56）；没有可见标签，用途写在无障碍标签里。 */
function FilterInput({
  disabled,
  grow = false,
  icon,
  label,
  onChangeText,
  placeholder,
  testID,
  value,
}: Readonly<{
  disabled: boolean;
  grow?: boolean;
  icon: PosIconName;
  label: string;
  onChangeText(value: string): void;
  placeholder: string;
  testID: string;
  value: string;
}>) {
  usePosThemeVersion();
  return (
    <View
      style={[
        styles.inputField,
        grow ? styles.inputFieldGrow : styles.inputFieldFixed,
        disabled && styles.inputFieldDisabled,
      ]}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={disabled ? posColors.disabledInk : posColors.placeholder}
        name={icon}
        size={22}
      />
      <PosKeyboardAwareTextInput
        accessibilityLabel={label}
        accessibilityState={{ disabled }}
        autoCapitalize="none"
        autoCorrect={false}
        editable={!disabled}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={
          disabled ? posColors.disabledInk : posColors.placeholder
        }
        selectionColor={posColors.accent}
        style={[styles.input, disabled && styles.inputDisabled]}
        testID={testID}
        value={value}
      />
    </View>
  );
}

function CenteredState({
  actionLabel,
  hint,
  icon,
  loading = false,
  message,
  onAction,
  testID,
}: Readonly<{
  actionLabel?: string;
  hint?: string;
  icon?: PosIconName;
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
      {hint ? <Text style={styles.centeredHint}>{hint}</Text> : null}
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

/** 当前起止日期正好等于哪个快捷范围（按设备本地日历日，只用于高亮分段按钮）。 */
function matchingQuickRange(
  from: string,
  to: string,
): HistoryQuickDateRange | null {
  const now = new Date();
  return (
    HISTORY_QUICK_DATE_RANGES.find((kind) => {
      const range = quickHistoryDateRange(kind, now);
      return range.from === from && range.to === to;
    }) ?? null
  );
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

/** 刷卡付款的卡种与脱敏卡号（例如 "VISA · **** 1234"）；非刷卡付款返回 null。 */
function paymentCardLabel(payment: RemoteOrderPaymentPreview): string | null {
  const cardParts = [payment.cardType, payment.maskedCardNumber].filter(
    (part): part is string => Boolean(part),
  );
  return cardParts.length > 0 ? cardParts.join(" · ") : null;
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
  filtersScroll: {
    flexGrow: 0,
  },
  dateField: {
    width: 158,
  },
  rangeSeparator: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "900",
  },
  inputField: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: posTouch.medium,
    paddingHorizontal: 14,
  },
  inputFieldFixed: {
    width: 136,
  },
  inputFieldGrow: {
    flex: 1,
    minWidth: 150,
  },
  inputFieldDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  input: {
    alignSelf: "stretch",
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontWeight: "600",
    minHeight: REMOTE_HISTORY_MIN_TOUCH_TARGET,
    minWidth: 0,
    padding: 0,
  },
  inputDisabled: {
    color: colors.disabledInk,
  },
  validation: {
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    paddingHorizontal: 14,
    paddingTop: 8,
  },
  readOnlyBanner: {
    alignItems: "center",
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    marginHorizontal: 12,
    marginTop: 10,
    paddingHorizontal: 14,
    paddingVertical: 9,
  },
  readOnlyText: {
    color: colors.blue,
    flex: 1,
    fontSize: 14,
    fontWeight: "700",
  },

  // ---------- 列表 + 详情 ----------
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  listPane: {
    flexShrink: 0,
    minHeight: 0,
    width: REMOTE_HISTORY_LIST_WIDTH,
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
  summaryCard: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    marginTop: 4,
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
  detailsFooter: {
    gap: 10,
  },
  detailsActions: {
    flexDirection: "row",
    gap: 12,
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

  // ---------- 空 / 加载 / 错误 / 离线 / 无权限 ----------
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
