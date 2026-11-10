import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  FlatList,
  ScrollView,
  Text,
  View,
} from "react-native";

import { businessDayUtcRange } from "@hb/pos-sync/features/sync-history/business-day-range";
import {
  resolveSyncHistoryLocale,
  syncHistoryText,
  type SyncHistoryCopyKey,
  type SyncHistoryLocale,
} from "./sync-history-copy";
import {
  type LocalSyncHistoryFilters,
  type LocalSyncHistoryOrderState,
} from "@hb/pos-sync/features/sync-history/sync-history-domain";
import {
  type SyncHistoryPresenter,
  type SyncHistoryPresenterState,
  type SyncHistoryRow,
} from "@hb/pos-sync/features/sync-history/sync-history-presenter";

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import { PosPill, type PosPillTone } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
} from "@/ui/theme";

export const SYNC_HISTORY_MIN_TOUCH_TARGET = 44;

/** 左侧筛选栏的固定宽度（设计图 300）。 */
export const SYNC_HISTORY_FILTERS_WIDTH = 300;
/** 行内勾选框的边长（设计图 34）。 */
export const SYNC_HISTORY_CHECKBOX_SIZE = 34;

const FILTER_STATES: readonly LocalSyncHistoryOrderState[] = [
  "CompletedLocal",
  "PendingSync",
  "Syncing",
  "Synced",
  "Blocked403",
  "Rejected",
];

type SyncHistoryScreenProps = Readonly<{
  /** presenter 生命周期归调用方；路由卸载时调用 destroy，屏幕只负责订阅与首刷。 */
  presenter: SyncHistoryPresenter;
  /** 门店业务时区；未注入时仅为兼容旧调用方而使用终端当前 IANA 时区。 */
  businessTimeZone?: string;
  onExport(serializedJson: string): void | Promise<void>;
  onBack?(): void;
}>;

export function SyncHistoryScreen({
  presenter,
  businessTimeZone,
  onExport,
  onBack,
}: SyncHistoryScreenProps) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { i18n } = useTranslation();
  const locale = resolveSyncHistoryLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t = (
    key: SyncHistoryCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => syncHistoryText(locale, key, values);
  const [dateFrom, setDateFrom] = useState(
    dateInputValue(state.filters.dateFromIso),
  );
  const [dateTo, setDateTo] = useState(
    dateInputValue(state.filters.dateToIso),
  );
  const [exportStatus, setExportStatus] = useState<
    "idle" | "exporting" | "success" | "failed"
  >("idle");
  const exportInFlight = useRef(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    void presenter.refresh();
    return () => {
      mounted.current = false;
    };
  }, [presenter]);

  const loading = state.kind === "loading";
  const selectedCount = state.selectedOrderGuids.length;
  const canManualRetransmit =
    state.access.canView && state.access.canManualRetransmit;

  const applyFilters = (
    states: readonly LocalSyncHistoryOrderState[] = state.filters.states,
  ) => {
    presenter.setFilters(
      buildFilters(dateFrom, dateTo, states, businessTimeZone),
    );
    void presenter.refresh();
  };

  const toggleState = (candidate: LocalSyncHistoryOrderState) => {
    const current = state.filters.states;
    const next = current.includes(candidate)
      ? current.filter((value) => value !== candidate)
      : [...current, candidate];
    applyFilters(next);
  };

  const retransmitDateRange = () => {
    presenter.setFilters(
      buildFilters(
        dateFrom,
        dateTo,
        state.filters.states,
        businessTimeZone,
      ),
    );
    void presenter.requestRetransmitDateRange();
  };

  const exportSupportBundle = async () => {
    if (!state.access.canExport || exportInFlight.current) return;
    exportInFlight.current = true;
    setExportStatus("exporting");
    try {
      const serializedJson = await presenter.serializeSupportExport();
      await onExport(serializedJson);
      if (mounted.current) setExportStatus("success");
    } catch {
      // 中文注释：失败提示绝不拼接异常或导出正文，避免把诊断内容回显到收银屏。
      if (mounted.current) setExportStatus("failed");
    } finally {
      exportInFlight.current = false;
    }
  };

  // 「已选 N 笔」里的数字单独放大成主操作色，其余文字沿用同一条文案。
  const [selectionPrefix, selectionNumber, selectionSuffix] = splitAroundCount(
    t("selection.count", { count: selectedCount }),
    selectedCount,
  );

  return (
    <PosScreenFrame
      active="sync-history"
      header={
        <PosPageHeader
          actions={
            <>
              <View
                style={styles.pendingCard}
                testID="sync-history-pending-count"
              >
                <Text style={styles.pendingLabel}>{t("pending.label")}</Text>
                <Text style={styles.pendingCount}>
                  {t("pending.count", { count: state.pendingCount })}
                </Text>
              </View>
              <PosButton
                disabled={loading}
                icon="refresh"
                label={loading ? t("action.refreshing") : t("action.refresh")}
                loading={loading}
                onPress={() => {
                  void presenter.refresh();
                }}
                size="medium"
                testID="sync-history-refresh"
                tone="quiet"
              />
            </>
          }
          backLabel={t("action.back")}
          backTestID="sync-history-back"
          onBack={onBack}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
      testID="sync-history-screen"
    >
      <View style={styles.workspace} testID="sync-history-workspace">
        {/* 左栏只管筛选：状态大按钮点了立即查，日期改完点「应用筛选」。 */}
        <View style={styles.filtersPane}>
          <ScrollView
            contentContainerStyle={styles.filtersPaneContent}
            keyboardShouldPersistTaps="handled"
            showsVerticalScrollIndicator
            style={styles.filtersScroll}
            testID="sync-history-filters-scroll"
          >
            <Text style={styles.paneTitle}>{t("filters.title")}</Text>
            <Text style={styles.sectionLabel}>{t("filters.statusHint")}</Text>
            <View style={styles.filterGrid}>
              <FilterChip
                label={t("filters.all")}
                onPress={() => applyFilters([])}
                selected={state.filters.states.length === 0}
                testID="sync-history-filter-all"
              />
              {FILTER_STATES.map((status) => (
                <FilterChip
                  key={status}
                  label={t(`status.${status}`)}
                  onPress={() => toggleState(status)}
                  selected={state.filters.states.includes(status)}
                  testID={`sync-history-filter-${status}`}
                />
              ))}
            </View>

            <Text style={[styles.sectionLabel, styles.sectionLabelSpaced]}>
              {t("filters.dates")}
            </Text>
            <DateField
              label={t("filters.dateFrom")}
              locale={locale}
              onChange={setDateFrom}
              testID="sync-history-date-from"
              value={dateFrom}
            />
            <DateField
              label={t("filters.dateTo")}
              locale={locale}
              onChange={setDateTo}
              testID="sync-history-date-to"
              value={dateTo}
            />
          </ScrollView>
          {/* 「应用筛选」固定在筛选栏底部，屏幕矮时上面的内容可滚动。 */}
          <PosButton
            disabled={loading}
            icon="filter-check-outline"
            label={t("filters.apply")}
            onPress={() => applyFilters()}
            size="medium"
            testID="sync-history-apply-filters"
          />
        </View>

        <View accessibilityLabel={t("list.title")} style={styles.listPane}>
          <RetransmitNotices locale={locale} state={state} />

          <View style={styles.listBody}>
            {state.kind === "loading" && state.rows.length === 0 ? (
              <CenteredState
                loading
                message={t("loading.title")}
                testID="sync-history-loading"
              />
            ) : null}
            {state.kind === "empty" ? (
              <CenteredState
                hint={t("empty.hint")}
                icon="text-box-search-outline"
                message={t("empty.title")}
                testID="sync-history-empty"
              />
            ) : null}
            {state.kind === "failed" ? (
              <CenteredState
                actionLabel={t("action.refresh")}
                hint={t("failed.hint")}
                icon="alert-circle-outline"
                message={`${t("failed.title")} · ${state.errorCode}`}
                onAction={() => {
                  void presenter.refresh();
                }}
                testID="sync-history-failed"
              />
            ) : null}
            {(state.kind === "ready" ||
              (state.kind === "loading" && state.rows.length > 0)) ? (
              <FlatList
                contentContainerStyle={styles.orderList}
                data={state.rows}
                keyExtractor={(row) => row.orderGuid}
                renderItem={({ item }) => (
                  <SyncHistoryOrderRow
                    canSelect={
                      canManualRetransmit &&
                      item.retransmit.kind === "allowed"
                    }
                    locale={locale}
                    onSelected={(selected) =>
                      presenter.setSelected(item.orderGuid, selected)
                    }
                    row={item}
                  />
                )}
                style={styles.orderListScroll}
                testID="sync-history-list"
              />
            ) : null}
            {/* 已有列表时的刷新进度浮在列表右上角，不挤动列表。 */}
            {loading && state.rows.length > 0 ? (
              <View style={styles.inlineLoading}>
                <ActivityIndicator
                  color={posColors.accent}
                  testID="sync-history-inline-loading"
                />
              </View>
            ) : null}
          </View>

          {exportStatus === "success" ? (
            <Notice
              message={t("export.success")}
              testID="sync-history-export-success"
              tone="success"
            />
          ) : null}
          {exportStatus === "failed" ? (
            <Notice
              message={t("export.failed")}
              testID="sync-history-export-failed"
              tone="danger"
            />
          ) : null}

          {/* 三个操作固定在列表下方一排：补传已选是本页唯一的主按钮。 */}
          <View style={styles.actionBar} testID="sync-history-action-bar">
            <Text
              style={styles.selectionCount}
              testID="sync-history-selection-count"
            >
              {selectionPrefix}
              <Text style={styles.selectionNumber}>{selectionNumber}</Text>
              {selectionSuffix}
            </Text>
            <PosButton
              disabled={loading || !canManualRetransmit || selectedCount === 0}
              fill
              icon="cloud-upload-outline"
              label={t("action.retransmitSelected")}
              onPress={() => {
                void presenter.requestRetransmitSelected();
              }}
              testID="sync-history-retransmit-selected"
              tone="primary"
            />
            <PosButton
              disabled={
                loading ||
                !canManualRetransmit ||
                !dateFrom ||
                !dateTo
              }
              icon="calendar-range"
              label={t("action.retransmitRange")}
              onPress={retransmitDateRange}
              style={styles.actionBarSecondary}
              testID="sync-history-retransmit-range"
            />
            <PosButton
              disabled={exportStatus === "exporting" || !state.access.canExport}
              icon="export-variant"
              label={
                exportStatus === "exporting"
                  ? t("action.exporting")
                  : t("action.export")
              }
              loading={exportStatus === "exporting"}
              onPress={() => {
                void exportSupportBundle();
              }}
              style={styles.actionBarSecondary}
              testID="sync-history-export"
              tone="quiet"
            />
          </View>
          <PosButton
            disabled={loading || state.nextBeforeLocalSequence === null}
            icon="chevron-down"
            label={t("action.loadMore")}
            onPress={() => {
              void presenter.loadNextPage();
            }}
            size="medium"
            testID="sync-history-load-more"
            tone="quiet"
          />
        </View>
      </View>
    </PosScreenFrame>
  );
}

function SyncHistoryOrderRow({
  canSelect,
  locale,
  onSelected,
  row,
}: Readonly<{
  canSelect: boolean;
  locale: SyncHistoryLocale;
  onSelected(selected: boolean): void;
  row: SyncHistoryRow;
}>) {
  usePosThemeVersion();
  const t = (
    key: SyncHistoryCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => syncHistoryText(locale, key, values);
  const gateKey = row.retransmit.kind === "blocked"
    ? gateCopyKey(row.retransmit.reason)
    : null;
  const errorCode = row.outbox?.lastErrorCode ?? null;
  // 数量异常是需要人工处理的长提示，单独占一行完整显示。
  const quantityUnsupported = errorCode === "ORDER_SYNC_QUANTITY_UNSUPPORTED";
  // 补传队列一行：状态 · 已尝试次数 · 下次尝试时间。
  const outboxSummary = [
    `${t("list.outbox")}${locale === "zh" ? "：" : ": "}${
      row.outbox ? t(`outbox.${row.outbox.state}`) : "—"
    }`,
    row.outbox
      ? t("list.attempts", { count: row.outbox.attemptCount })
      : null,
    row.outbox?.nextAttemptAtIso
      ? t("list.nextAttempt", {
          time: formatDateTime(row.outbox.nextAttemptAtIso, locale),
        })
      : null,
  ]
    .filter((part): part is string => Boolean(part))
    .join(" · ");

  return (
    <PosPressable
      accessibilityLabel={`${t("list.order", { orderGuid: row.orderGuid })}, ${t(`status.${row.state}`)}`}
      accessibilityRole="checkbox"
      accessibilityState={{
        checked: row.isSelected,
        disabled: !canSelect,
      }}
      {...(!canSelect ? { disabled: true } : {})}
      onPress={() => onSelected(!row.isSelected)}
      style={({ pressed }) => [
        styles.orderRow,
        !canSelect && styles.orderRowDisabled,
        row.isSelected && styles.orderRowSelected,
        pressed && styles.pressed,
      ]}
      testID={`sync-history-row-${row.orderGuid}`}
    >
      <View
        style={[
          styles.checkbox,
          !canSelect && styles.checkboxDisabled,
          row.isSelected && styles.checkboxChecked,
        ]}
        testID={`sync-history-select-${row.orderGuid}`}
      >
        {row.isSelected ? (
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.onAccent}
            name="check"
            size={24}
          />
        ) : null}
      </View>
      <View style={styles.orderIdentity}>
        <View style={styles.orderTitleRow}>
          <Text numberOfLines={1} style={styles.sequence}>
            {t("list.sequence", { sequence: row.localSequence })}
          </Text>
          <StatusBadge locale={locale} state={row.state} />
        </View>
        <Text numberOfLines={1} style={styles.rowMeta}>
          {t("list.order", { orderGuid: row.orderGuid })} ·{" "}
          {formatDateTime(row.soldAtIso, locale)} · {row.storeCode} ·{" "}
          {row.deviceCode}
        </Text>
        {/* 不能补传的原因写在行内，后面接补传队列信息。 */}
        <View style={styles.rowDetailLine}>
          {gateKey ? (
            <>
              <Text
                style={[
                  styles.gateText,
                  row.state === "Rejected" && styles.gateTextDanger,
                ]}
                testID={`sync-history-gate-${row.orderGuid}`}
              >
                {t(gateKey)}
              </Text>
              <Text style={styles.rowMeta}>·</Text>
            </>
          ) : null}
          <Text numberOfLines={1} style={[styles.rowMeta, styles.rowShrink]}>
            {outboxSummary}
          </Text>
          {errorCode && !quantityUnsupported ? (
            <>
              <Text style={styles.rowMeta}>·</Text>
              <Text
                numberOfLines={1}
                style={[styles.safeError, styles.rowShrink]}
                testID={`sync-history-error-${row.orderGuid}`}
              >
                {t("list.error", { code: errorCode })}
              </Text>
            </>
          ) : null}
        </View>
        {errorCode && quantityUnsupported ? (
          <Text
            numberOfLines={4}
            style={styles.quantityWarning}
            testID={`sync-history-error-${row.orderGuid}`}
          >
            {t("list.quantityUnsupported", { code: errorCode })}
          </Text>
        ) : null}
      </View>
      <View style={styles.amountColumn}>
        <Text style={styles.amount}>
          {formatAud(row.actualAmountCents, locale)}
        </Text>
        {row.discountCents !== 0 ? (
          <Text style={styles.amountMeta}>
            {t("list.discount")} −{formatAud(row.discountCents, locale)}
          </Text>
        ) : null}
        <Text numberOfLines={2} style={styles.amountMeta}>
          {t("list.tender")} · {row.tenderSummary || "—"}
        </Text>
      </View>
    </PosPressable>
  );
}

function RetransmitNotices({
  locale,
  state,
}: Readonly<{
  locale: SyncHistoryLocale;
  state: SyncHistoryPresenterState;
}>) {
  usePosThemeVersion();
  const result = state.lastRetransmit;
  if (!result) return null;
  const t = (
    key: SyncHistoryCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => syncHistoryText(locale, key, values);
  const summary =
    result.kind === "requested"
      ? t("retransmit.requested", { count: result.requestedCount })
      : result.kind === "nothing-eligible"
        ? t("retransmit.none")
        : t("retransmit.failed");

  return (
    <View style={styles.noticeStack}>
      <Notice
        message={summary}
        testID="sync-history-retransmit-result"
        tone={result.kind === "requested" ? "success" : "warning"}
      />
      {result.reauthenticationRequiredCount > 0 ? (
        <Notice
          message={t("gate.reauthentication", {
            count: result.reauthenticationRequiredCount,
          })}
          testID="sync-history-reauthentication-required"
          tone="warning"
        />
      ) : null}
      {result.supervisorRequiredCount > 0 ? (
        <Notice
          message={t("gate.supervisor", {
            count: result.supervisorRequiredCount,
          })}
          testID="sync-history-supervisor-required"
          tone="danger"
        />
      ) : null}
    </View>
  );
}

/** 状态筛选大按钮（高 52，可多选）：选中为深色实底。 */
function FilterChip({
  label,
  onPress,
  selected,
  testID,
}: Readonly<{
  label: string;
  onPress(): void;
  selected: boolean;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="checkbox"
      accessibilityState={{ checked: selected }}
      onPress={onPress}
      style={({ pressed }) => [
        styles.filterChip,
        selected && styles.filterChipSelected,
        pressed && styles.pressed,
      ]}
      testID={testID}
    >
      <Text
        numberOfLines={2}
        style={[
          styles.filterChipText,
          selected && styles.filterChipTextSelected,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function DateField({
  label,
  locale,
  onChange,
  testID,
  value,
}: Readonly<{
  label: string;
  locale: SyncHistoryLocale;
  onChange(value: string | null): void;
  testID: string;
  value: string | null;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.dateField}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <PosDatePickerField
        accessibilityLabel={label}
        allowClear
        locale={locale}
        onChange={onChange}
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
    <View style={styles.centeredState} testID={testID}>
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
          icon="refresh"
          label={actionLabel}
          onPress={onAction}
          size="medium"
          style={styles.centeredAction}
          testID={`${testID}-action`}
        />
      ) : null}
    </View>
  );
}

const NOTICE_ICONS: Readonly<
  Record<"success" | "warning" | "danger", PosIconName>
> = {
  danger: "alert-circle-outline",
  success: "check-circle-outline",
  warning: "alert-outline",
};

function Notice({
  message,
  testID,
  tone,
}: Readonly<{
  message: string;
  testID: string;
  tone: "success" | "warning" | "danger";
}>) {
  usePosThemeVersion();
  const color =
    tone === "success"
      ? posColors.green
      : tone === "danger"
        ? posColors.red
        : posColors.yellowInk;
  return (
    <View
      accessibilityRole="alert"
      style={[
        styles.notice,
        tone === "success" && styles.noticeSuccess,
        tone === "warning" && styles.noticeWarning,
        tone === "danger" && styles.noticeDanger,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={color}
        name={NOTICE_ICONS[tone]}
        size={22}
      />
      <Text style={[styles.noticeText, { color }]}>{message}</Text>
    </View>
  );
}

function StatusBadge({
  locale,
  state,
}: Readonly<{
  locale: SyncHistoryLocale;
  state: LocalSyncHistoryOrderState;
}>) {
  usePosThemeVersion();
  // 被拒为红，待同步 / 需重新认证为黄，已同步为绿，其余（本地完成、同步中）为蓝。
  const tone: PosPillTone =
    state === "Rejected"
      ? "danger"
      : state === "Blocked403" || state === "PendingSync"
        ? "warning"
        : state === "Synced"
          ? "good"
          : "info";
  return (
    <PosPill
      label={syncHistoryText(locale, `status.${state}`)}
      style={styles.pillInline}
      tone={tone}
    />
  );
}

/** 把带数量的文案拆成「前缀 / 数字 / 后缀」，方便单独放大数字；找不到数字时整句当前缀。 */
function splitAroundCount(
  text: string,
  count: number,
): readonly [string, string, string] {
  const token = String(count);
  const index = text.indexOf(token);
  if (index < 0) return [text, "", ""];
  return [text.slice(0, index), token, text.slice(index + token.length)];
}

function gateCopyKey(
  reason:
    | "synced"
    | "syncing"
    | "reauthentication-required"
    | "supervisor-required"
    | "no-pending-outbox",
): SyncHistoryCopyKey {
  switch (reason) {
    case "synced":
      return "gate.synced";
    case "syncing":
      return "gate.syncing";
    case "reauthentication-required":
      return "gate.reauthenticationRow";
    case "supervisor-required":
      return "gate.supervisorRow";
    case "no-pending-outbox":
      return "gate.noPending";
  }
}

function buildFilters(
  dateFrom: string | null,
  dateTo: string | null,
  states: readonly LocalSyncHistoryOrderState[],
  businessTimeZone?: string,
): LocalSyncHistoryFilters {
  const range = businessDayUtcRange(
    dateFrom ?? "",
    dateTo ?? "",
    businessTimeZone,
  );
  if (!range) {
    // 非 ISO 哨兵会被 presenter 在查询 Port 之前拒绝，绝不退化成无日期筛选。
    return {
      dateFromIso: "invalid-business-date-range",
      dateToIso: "invalid-business-date-range",
      states,
    };
  }
  return {
    ...range,
    states,
  };
}

function dateInputValue(value: string | null): string | null {
  return value?.slice(0, 10) ?? null;
}

function formatAud(valueCents: number, locale: SyncHistoryLocale): string {
  return new Intl.NumberFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    currency: "AUD",
    style: "currency",
  }).format(valueCents / 100);
}

function formatDateTime(value: string, locale: SyncHistoryLocale): string {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "—";
  return new Intl.DateTimeFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    month: "2-digit",
    year: "numeric",
  }).format(date);
}

const styles = createPosStyles((colors) => ({
  // ---------- 页头：待同步数量 ----------
  pendingCard: {
    alignItems: "center",
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: 52,
    paddingHorizontal: 16,
  },
  pendingLabel: {
    color: colors.yellowInk,
    fontSize: 15,
    fontWeight: "800",
  },
  pendingCount: {
    color: colors.ink,
    fontSize: 24,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },

  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },

  // ---------- 左栏：筛选 ----------
  filtersPane: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 10,
    minHeight: 0,
    paddingHorizontal: 16,
    paddingVertical: 14,
    width: SYNC_HISTORY_FILTERS_WIDTH,
  },
  filtersScroll: {
    flex: 1,
    minHeight: 0,
  },
  filtersPaneContent: {
    gap: 8,
  },
  paneTitle: {
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  sectionLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
    marginHorizontal: 2,
    marginTop: 2,
  },
  sectionLabelSpaced: {
    marginTop: 8,
  },
  filterGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  // 两列排布；第 7 个（最后一个）独占一行。
  filterChip: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexBasis: "47%",
    flexGrow: 1,
    justifyContent: "center",
    minHeight: 52,
    paddingHorizontal: 8,
  },
  filterChipSelected: {
    backgroundColor: colors.inverseSurface,
    borderColor: colors.inverseSurface,
  },
  filterChipText: {
    color: colors.mutedInk,
    fontSize: 14.5,
    fontWeight: "800",
    textAlign: "center",
  },
  filterChipTextSelected: {
    color: colors.onInverse,
  },
  dateField: {
    gap: 5,
  },
  fieldLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },

  // ---------- 右栏：列表 + 底部操作 ----------
  listPane: {
    flex: 1,
    gap: 10,
    minHeight: 0,
    minWidth: 0,
  },
  listBody: {
    flex: 1,
    minHeight: 0,
  },
  orderListScroll: {
    flex: 1,
    minHeight: 0,
  },
  orderList: {
    gap: 6,
  },
  inlineLoading: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 18,
    borderWidth: 1,
    padding: 6,
    pointerEvents: "none",
    position: "absolute",
    right: 10,
    top: 10,
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
  // 不能补传的行整体置灰。
  orderRowDisabled: {
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
  checkbox: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.small,
    borderWidth: 2,
    flexShrink: 0,
    height: SYNC_HISTORY_CHECKBOX_SIZE,
    justifyContent: "center",
    width: SYNC_HISTORY_CHECKBOX_SIZE,
  },
  checkboxChecked: {
    backgroundColor: colors.accent,
    borderColor: colors.accent,
  },
  checkboxDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  orderIdentity: {
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
  sequence: {
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
  rowMeta: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
  },
  rowShrink: {
    flexShrink: 1,
  },
  rowDetailLine: {
    alignItems: "center",
    columnGap: 5,
    flexDirection: "row",
    flexWrap: "wrap",
    minWidth: 0,
  },
  gateText: {
    color: colors.yellowInk,
    fontSize: 12.5,
    fontWeight: "800",
  },
  gateTextDanger: {
    color: colors.red,
  },
  safeError: {
    color: colors.red,
    fontSize: 12.5,
    fontWeight: "700",
  },
  quantityWarning: {
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 18,
    marginTop: 2,
  },
  amountColumn: {
    alignItems: "flex-end",
    flexShrink: 0,
    gap: 2,
    maxWidth: 190,
  },
  amount: {
    color: colors.ink,
    fontSize: 18,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  amountMeta: {
    color: colors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    textAlign: "right",
  },
  actionBar: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  actionBarSecondary: {
    flexShrink: 1,
  },
  selectionCount: {
    color: colors.ink,
    fontSize: 16,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    minWidth: 96,
  },
  selectionNumber: {
    color: colors.accent,
    fontSize: 22,
  },

  // ---------- 提示条 ----------
  noticeStack: {
    gap: 6,
  },
  notice: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: SYNC_HISTORY_MIN_TOUCH_TARGET,
    paddingHorizontal: 14,
    paddingVertical: 9,
  },
  noticeDanger: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
  },
  noticeSuccess: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
  },
  noticeWarning: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
  },
  noticeText: {
    flex: 1,
    fontSize: 14,
    fontWeight: "700",
  },

  // ---------- 空 / 加载 / 错误 ----------
  centeredState: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flex: 1,
    gap: 12,
    justifyContent: "center",
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
    maxWidth: 440,
    textAlign: "center",
  },
  centeredAction: {
    marginTop: 4,
    minWidth: 160,
  },
  pressed: {
    opacity: 0.72,
  },
}));
