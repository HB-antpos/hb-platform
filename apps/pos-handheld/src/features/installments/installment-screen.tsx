import {
  useEffect,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
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
  installmentText,
  resolveInstallmentLocale,
  type InstallmentLocale,
} from "./installment-copy";
import type {
  InstallmentDatePreset,
  InstallmentDetails,
  InstallmentPaymentMethod,
} from "./installment-models";
import type {
  InstallmentPresenter,
  InstallmentPresenterState,
  InstallmentStatusCode,
} from "./installment-presenter";

import type { InstallmentStatus, InstallmentSummary } from "@/core/contracts";
import { PosDatePickerField } from "@/ui/controls/pos-date-picker-field";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import {
  HandheldScreenFrame,
  HandheldStateSurface,
} from "@/ui/handheld";
import { posColors } from "@/ui/theme";

export const INSTALLMENTS_MIN_TOUCH_TARGET = 48;

export type InstallmentLayout = Readonly<{
  compact: boolean;
  listFlex: number;
  detailsFlex: number;
  pagePadding: number;
  workspaceGap: number;
}>;

export function installmentLayoutForWidth(_width: number): InstallmentLayout {
  return {
    compact: true,
    listFlex: 1,
    detailsFlex: 1,
    pagePadding: 8,
    workspaceGap: 0,
  };
}

export type InstallmentScreenPresenter = Pick<
  InstallmentPresenter,
  | "capabilities"
  | "cancelWithRefund"
  | "confirmPickup"
  | "getState"
  | "load"
  | "loadMore"
  | "recoverBlocking"
  | "reprintSelected"
  | "retryDetails"
  | "select"
  | "setCancelReason"
  | "setCancelRefundMode"
  | "setDateFilter"
  | "setDeviceScope"
  | "setPickupNote"
  | "setSearchQuery"
  | "setStatusFilter"
  | "setVoidReason"
  | "subscribe"
  | "voidSelected"
> &
  Readonly<{ getState(): InstallmentPresenterState }>;

export type InstallmentScreenProps = Readonly<{
  onBack?(): void;
  onStartCreate?(): boolean;
  onStartRepayment?(installmentGuid: string): boolean;
  presenter: InstallmentScreenPresenter;
}>;

type ConfirmationKind = "cancel" | "void" | "pickup";
type DangerMode = "cancel" | "void" | null;
type ActionBlockReason =
  | "offline"
  | "permission"
  | "busy"
  | "recovery"
  | "unavailable";

/**
 * 分期页只负责可见状态、筛选和动作意图；支付准备、UUID 校验与恢复边界仍由
 * 路由和 runtime 执行，页面只消费成功/失败结果。
 */
export function InstallmentScreen({
  onBack,
  onStartCreate,
  onStartRepayment,
  presenter,
}: InstallmentScreenProps) {
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const layout = installmentLayoutForWidth(0);
  const { i18n } = useTranslation();
  const locale = resolveInstallmentLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const [compactDetailsOpen, setCompactDetailsOpen] = useState(
    Boolean(state.selectedGuid),
  );
  const [routeFailure, setRouteFailure] = useState(false);
  const [moreOpen, setMoreOpen] = useState(false);
  const [dangerMode, setDangerMode] = useState<DangerMode>(null);
  const [confirmation, setConfirmation] = useState<ConfirmationKind | null>(
    null,
  );

  useEffect(() => {
    if (state.online) void presenter.load();
  }, [presenter, state.online]);

  useEffect(() => {
    if (!state.selectedGuid) setCompactDetailsOpen(false);
    setMoreOpen(false);
    setDangerMode(null);
    setConfirmation(null);
    setRouteFailure(false);
  }, [state.selectedGuid]);

  const detailsOwnStatus = Boolean(
    state.selectedGuid &&
      !state.details &&
      !state.detailsLoading &&
      [
        "details-failed",
        "details-unavailable",
        "service-unavailable",
      ].includes(state.statusCode ?? ""),
  );
  const reprintBelongsToDetails = Boolean(
    state.details &&
      "installmentGuid" in state.reprint &&
      state.reprint.installmentGuid === state.details.installmentGuid,
  );
  const reprintSubmitting =
    reprintBelongsToDetails && state.reprint.kind === "submitting";
  const showHeaderReprint = Boolean(
    compactDetailsOpen &&
      state.details &&
      (presenter.capabilities.reprint || reprintSubmitting),
  );

  const openOrder = (installmentGuid: string): void => {
    setCompactDetailsOpen(true);
    void presenter.select(installmentGuid);
  };
  const startCreate = (): void => {
    setRouteFailure(false);
    if (!state.createDraft) {
      onBack?.();
      return;
    }
    if (!onStartCreate?.()) setRouteFailure(true);
  };

  return (
    <HandheldStateSurface
      slug={
        state.online && compactDetailsOpen
          ? "installment-detail"
          : "installments-list"
      }
      style={styles.stateSurface}
    >
      <HandheldScreenFrame
        contentStyle={styles.frameContent}
        testID="installments-screen"
      >
        <View style={styles.page}>
          <Header
            canCreate={state.access.canCreate}
            createDraftAvailable={Boolean(state.createDraft)}
            detailsOpen={compactDetailsOpen}
            installmentNumber={state.details?.installmentNumber}
            locale={locale}
            onBack={
              compactDetailsOpen
                ? () => setCompactDetailsOpen(false)
                : onBack
            }
            onPrimary={startCreate}
            onRefresh={() => void presenter.load()}
            onReprint={() => void presenter.reprintSelected()}
            online={state.online}
            primaryAvailable={Boolean(
              state.createDraft ? onStartCreate : onBack,
            )}
            refreshing={state.kind === "loading"}
            reprintSubmitting={reprintSubmitting}
            showReprint={showHeaderReprint}
            writeLocked={
              state.busy ||
              state.reprint.kind === "submitting" ||
              state.recoveryRequired ||
              !state.online
            }
          />

        {routeFailure ? (
          <NoticeBanner
            message={installmentText(locale, "navigation.failed")}
            testID="installments-navigation-failed"
            tone="danger"
          />
        ) : null}
        {state.statusCode && !detailsOwnStatus ? (
          <StatusBanner locale={locale} statusCode={state.statusCode} />
        ) : null}
        {state.recoveryRequired && state.online ? (
          <View style={styles.recoveryRow}>
            <Text style={styles.recoveryText}>
              {installmentText(locale, "blocked.recovery")}
            </Text>
            <ActionButton
              disabled={
                state.busy || state.reprint.kind === "submitting"
              }
              label={installmentText(locale, "action.recover")}
              onPress={() => void presenter.recoverBlocking()}
              testID="installments-recover-blocking-action"
              tone="danger"
            />
          </View>
        ) : null}

        {!state.online ? (
          <OfflineWorkspace
            busy={state.busy}
            locale={locale}
            onRetry={() => void presenter.load()}
          />
        ) : (
          <View
            style={[
              styles.workspace,
              {
                gap: layout.workspaceGap,
                padding: layout.pagePadding,
              },
            ]}
            testID="installments-workspace"
          >
            {layout.compact && compactDetailsOpen ? null : (
              <HistoryPane
                compact={layout.compact}
                flex={layout.listFlex}
                locale={locale}
                onOpenOrder={openOrder}
                presenter={presenter}
                state={state}
              />
            )}
            {!layout.compact || compactDetailsOpen ? (
              <DetailsPane
                flex={layout.detailsFlex}
                locale={locale}
                presenter={presenter}
                state={state}
              />
            ) : null}
          </View>
        )}
        {/* 主操作与详情滚动区分离，长单据也不会把续付入口推离拇指可达区。 */}
        {state.online && compactDetailsOpen && state.details ? (
          <DetailsActions
            confirmation={confirmation}
            dangerMode={dangerMode}
            details={state.details}
            locale={locale}
            moreOpen={moreOpen}
            onRouteFailure={() => setRouteFailure(true)}
            onStartRepayment={onStartRepayment}
            presenter={presenter}
            setConfirmation={setConfirmation}
            setDangerMode={setDangerMode}
            setMoreOpen={setMoreOpen}
            state={state}
          />
        ) : null}
        </View>
      </HandheldScreenFrame>
    </HandheldStateSurface>
  );
}

function Header({
  canCreate,
  createDraftAvailable,
  detailsOpen,
  installmentNumber,
  locale,
  onBack,
  onPrimary,
  onRefresh,
  onReprint,
  online,
  primaryAvailable,
  refreshing,
  reprintSubmitting,
  showReprint,
  writeLocked,
}: Readonly<{
  canCreate: boolean;
  createDraftAvailable: boolean;
  detailsOpen: boolean;
  installmentNumber: string | undefined;
  locale: InstallmentLocale;
  onBack: (() => void) | undefined;
  onPrimary(): void;
  onRefresh(): void;
  onReprint(): void;
  online: boolean;
  primaryAvailable: boolean;
  refreshing: boolean;
  reprintSubmitting: boolean;
  showReprint: boolean;
  writeLocked: boolean;
}>) {
  const primaryDisabled = createDraftAvailable
    ? writeLocked || !primaryAvailable
    : !primaryAvailable;
  return (
    <View style={styles.header} testID="installments-header">
      {onBack ? (
        <ActionButton
          label={installmentText(locale, "action.back")}
          onPress={onBack}
          sound="navigate"
          testID={detailsOpen ? "installments-details-back" : "installments-back"}
          tone="quiet"
        />
      ) : null}
      <View style={styles.headerIdentity}>
        <Text numberOfLines={1} style={styles.title}>
          {installmentText(locale, detailsOpen ? "details.title" : "title")}
        </Text>
        <View style={styles.connectionState}>
          <View
            style={[
              styles.connectionDot,
              online ? styles.connectionDotOnline : styles.connectionDotOffline,
            ]}
          />
          <Text style={styles.connectionText}>
            {installmentText(locale, online ? "online" : "offlineShort")}
          </Text>
        </View>
      </View>
      <View style={styles.headerActions}>
        {detailsOpen && showReprint && installmentNumber ? (
          <ActionButton
            accessibilityHint={installmentText(locale, "reprint.hint")}
            accessibilityLabel={installmentText(
              locale,
              "reprint.accessibility",
              { installmentNumber },
            )}
            disabled={reprintSubmitting}
            label={installmentText(
              locale,
              reprintSubmitting ? "action.reprinting" : "action.reprint",
            )}
            onPress={onReprint}
            testID="installment-reprint"
            tone="secondary"
          />
        ) : null}
        {!detailsOpen ? (
          <ActionButton
            disabled={refreshing || !online}
            label={installmentText(
              locale,
              refreshing ? "action.refreshing" : "action.refresh",
            )}
            onPress={onRefresh}
            testID="installments-refresh"
            tone="secondary"
          />
        ) : null}
        {!detailsOpen && canCreate ? (
          <ActionButton
            disabled={primaryDisabled}
            label={installmentText(
              locale,
              createDraftAvailable ? "action.newShort" : "action.goToSales",
            )}
            onPress={onPrimary}
            sound="navigate"
            testID="installments-primary-action"
          />
        ) : null}
      </View>
    </View>
  );
}

function OfflineWorkspace({
  busy,
  locale,
  onRetry,
}: Readonly<{
  busy: boolean;
  locale: InstallmentLocale;
  onRetry(): void;
}>) {
  return (
    <View style={styles.fullState} testID="installments-offline-state">
      <Text style={styles.fullStateTitle}>
        {installmentText(locale, "offlineShort")}
      </Text>
      <Text style={styles.fullStateText}>
        {installmentText(locale, "offline")}
      </Text>
      <ActionButton
        disabled={busy}
        label={installmentText(locale, "action.retry")}
        onPress={onRetry}
        testID="installments-offline-retry"
        tone="secondary"
      />
    </View>
  );
}

function HistoryPane({
  compact,
  flex,
  locale,
  onOpenOrder,
  presenter,
  state,
}: Readonly<{
  compact: boolean;
  flex: number;
  locale: InstallmentLocale;
  onOpenOrder(installmentGuid: string): void;
  presenter: InstallmentScreenPresenter;
  state: InstallmentPresenterState;
}>) {
  const primaryStatuses: readonly {
    label: string;
    status: InstallmentStatus | null;
  }[] = [
    { label: installmentText(locale, "filter.all"), status: null },
    { label: installmentText(locale, "filter.active"), status: "Active" },
    { label: installmentText(locale, "filter.paid"), status: "PaidOff" },
    { label: installmentText(locale, "filter.picked"), status: "PickedUp" },
  ];
  const advancedFilterActive =
    state.statusFilter === "Cancelled" ||
    state.dateFilter.preset !== "all" ||
    state.deviceScope !== "store";
  const datePreset = state.dateFilter.preset;
  const advancedDateLabel =
    datePreset === "all"
      ? null
      : datePreset === "custom" &&
          state.dateFilter.fromDate &&
          state.dateFilter.toDate
        ? installmentText(locale, "date.customRange", {
            from: state.dateFilter.fromDate,
            to: state.dateFilter.toDate,
          })
      : installmentText(
          locale,
          ({
            today: "date.today",
            last7: "date.last7",
            last30: "date.last30",
            custom: "date.custom",
          } as const)[datePreset],
        );
  const advancedFilterSummary = [
    state.statusFilter === "Cancelled"
      ? installmentText(locale, "filter.cancelled")
      : null,
    advancedDateLabel,
    state.deviceScope === "device"
      ? installmentText(locale, "scope.device")
      : null,
  ]
    .filter((label): label is string => label !== null)
    .join(" · ");
  const [advancedFiltersOpen, setAdvancedFiltersOpen] = useState(
    advancedFilterActive,
  );
  const [customDatesOpen, setCustomDatesOpen] = useState(
    state.dateFilter.preset === "custom",
  );
  const [fromDate, setFromDate] = useState(state.dateFilter.fromDate);
  const [toDate, setToDate] = useState(state.dateFilter.toDate);

  useEffect(() => {
    setFromDate(state.dateFilter.fromDate);
    setToDate(state.dateFilter.toDate);
    if (state.dateFilter.preset === "custom") setCustomDatesOpen(true);
  }, [state.dateFilter]);

  useEffect(() => {
    if (advancedFilterActive) setAdvancedFiltersOpen(true);
  }, [advancedFilterActive]);

  const chooseDatePreset = (preset: InstallmentDatePreset): void => {
    if (preset === "custom") {
      setCustomDatesOpen(true);
      return;
    }
    setCustomDatesOpen(false);
    void presenter.setDateFilter({ preset, fromDate: null, toDate: null });
  };

  const filtered = hasActiveFilters(state);
  const loadingWithoutRows =
    state.orders.length === 0 &&
    (state.kind === "idle" || state.kind === "loading");
  const failedWithoutRows =
    state.orders.length === 0 && state.kind === "failed";
  const unauthorizedWithoutRows =
    state.orders.length === 0 && state.kind === "unauthorized";

  return (
    <View
      style={[styles.pane, styles.historyPane, { flex }]}
      testID="installments-history-pane"
    >
      <View style={styles.panelHeader}>
        <View>
          <Text style={styles.panelTitle}>
            {installmentText(locale, "history.records")}
          </Text>
          <Text style={styles.panelMeta} testID="installments-result-count">
            {installmentText(
              locale,
              state.orders.length === 1
                ? "history.countOne"
                : "history.count",
              { count: state.orders.length },
            )}
          </Text>
        </View>
        {state.kind === "loading" && state.orders.length > 0 ? (
          <ActivityIndicator color={posColors.orange} />
        ) : null}
      </View>

      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.searchRow}
        style={styles.searchKeyboardScroll}
        testID="installment-history-search-keyboard-scroll"
      >
        <PosKeyboardAwareTextInput
          accessibilityLabel={installmentText(locale, "search.accessibility")}
          autoCapitalize="none"
          autoCorrect={false}
          onChangeText={(value) => presenter.setSearchQuery(value)}
          onSubmitEditing={() => void presenter.load()}
          placeholder={installmentText(locale, "search.placeholder")}
          placeholderTextColor={posColors.mutedInk}
          returnKeyType="search"
          style={styles.searchInput}
          testID="installments-search"
          value={state.query}
        />
        <ActionButton
          disabled={state.kind === "loading"}
          label={installmentText(locale, "action.search")}
          onPress={() => void presenter.load()}
          testID="installments-search-submit"
        />
      </PosKeyboardAwareScrollView>

      <FilterGroup label={installmentText(locale, "filter.statusLabel")}>
        <ScrollView
          horizontal
          contentContainerStyle={styles.filterRow}
          showsHorizontalScrollIndicator={false}
        >
          {primaryStatuses.map((item) => (
            <ActionButton
              compact
              key={item.label}
              label={item.label}
              onPress={() => void presenter.setStatusFilter(item.status)}
              selected={state.statusFilter === item.status}
              testID={`installments-filter-${item.status ?? "all"}`}
              tone="secondary"
            />
          ))}
          <ActionButton
            compact
            expanded={advancedFiltersOpen}
            label={installmentText(
              locale,
              advancedFiltersOpen ? "filter.close" : "filter.more",
            )}
            onPress={() => setAdvancedFiltersOpen((open) => !open)}
            selected={advancedFilterActive}
            testID="installments-filter-toggle"
            tone="secondary"
          />
        </ScrollView>
      </FilterGroup>

      {!advancedFiltersOpen && advancedFilterActive ? (
        <Text
          style={styles.activeFilterSummary}
          testID="installments-filter-summary"
        >
          {advancedFilterSummary}
        </Text>
      ) : null}

      {advancedFiltersOpen ? (
        <View
          style={styles.advancedFilters}
          testID="installments-filter-advanced"
        >
          <ActionButton
            compact
            label={installmentText(locale, "filter.cancelled")}
            onPress={() => void presenter.setStatusFilter("Cancelled")}
            selected={state.statusFilter === "Cancelled"}
            testID="installments-filter-Cancelled"
            tone="secondary"
          />
          <View
            style={[
              styles.filterColumns,
              compact && styles.filterColumnsCompact,
            ]}
          >
            <FilterGroup label={installmentText(locale, "date.label")}>
              <ScrollView
                horizontal
                contentContainerStyle={styles.filterRow}
                showsHorizontalScrollIndicator={false}
              >
                {(
                  ["all", "today", "last7", "last30", "custom"] as const
                ).map((preset) => (
                  <ActionButton
                    compact
                    key={preset}
                    label={installmentText(locale, `date.${preset}`)}
                    onPress={() => chooseDatePreset(preset)}
                    selected={state.dateFilter.preset === preset}
                    testID={`installments-date-${preset}`}
                    tone="secondary"
                  />
                ))}
              </ScrollView>
            </FilterGroup>
            <FilterGroup label={installmentText(locale, "scope.label")}>
              <View style={styles.filterRow}>
                {(["store", "device"] as const).map((scope) => (
                  <ActionButton
                    compact
                    key={scope}
                    label={installmentText(locale, `scope.${scope}`)}
                    onPress={() => void presenter.setDeviceScope(scope)}
                    selected={state.deviceScope === scope}
                    testID={`installments-scope-${scope}`}
                    tone="secondary"
                  />
                ))}
              </View>
            </FilterGroup>
          </View>

          {customDatesOpen ? (
            <View style={styles.customDateRow} testID="installments-custom-dates">
              <PosDatePickerField
                accessibilityLabel={installmentText(locale, "date.from")}
                allowClear
                locale={locale}
                onChange={setFromDate}
                testID="installments-date-from"
                value={fromDate}
              />
              <PosDatePickerField
                accessibilityLabel={installmentText(locale, "date.to")}
                allowClear
                locale={locale}
                onChange={setToDate}
                testID="installments-date-to"
                value={toDate}
              />
              <ActionButton
                compact
                label={installmentText(locale, "date.apply")}
                onPress={() =>
                  void presenter.setDateFilter({
                    preset: "custom",
                    fromDate,
                    toDate,
                  })
                }
                testID="installments-date-apply"
              />
            </View>
          ) : null}
        </View>
      ) : null}

      <FlatList
        contentContainerStyle={[
          styles.orderList,
          state.orders.length === 0 && styles.emptyList,
        ]}
        data={state.orders}
        keyExtractor={(order) => order.installmentGuid}
        keyboardShouldPersistTaps="handled"
        ListEmptyComponent={
          loadingWithoutRows ? (
            <CenteredState
              loading
              message={installmentText(locale, "history.loading")}
              testID="installments-history-loading"
            />
          ) : failedWithoutRows ? (
            <CenteredState
              actionLabel={installmentText(locale, "action.retry")}
              message={installmentText(locale, "history.failed")}
              onAction={() => void presenter.load()}
              testID="installments-history-failed"
            />
          ) : unauthorizedWithoutRows ? (
            <CenteredState
              message={installmentText(locale, "history.permission")}
              testID="installments-history-unauthorized"
            />
          ) : (
            <CenteredState
              message={installmentText(
                locale,
                filtered ? "history.emptyFiltered" : "history.emptyInitial",
              )}
              testID={
                filtered
                  ? "installments-history-empty-filtered"
                  : "installments-history-empty-initial"
              }
            />
          )
        }
        ListFooterComponent={
          state.orders.length > 0 && state.hasMore ? (
            <View style={styles.loadMoreRow}>
              <ActionButton
                disabled={state.loadingMore || state.kind === "loading"}
                label={installmentText(
                  locale,
                  state.loadingMore
                    ? "action.loadingMore"
                    : "action.loadMore",
                )}
                onPress={() => void presenter.loadMore()}
                testID="installments-load-more"
                tone="quiet"
              />
            </View>
          ) : null
        }
        renderItem={({ item }) => (
          <OrderRow
            locale={locale}
            onPress={() => onOpenOrder(item.installmentGuid)}
            order={item}
            selected={state.selectedGuid === item.installmentGuid}
          />
        )}
        testID="installments-list"
      />
    </View>
  );
}

function FilterGroup({
  children,
  label,
}: Readonly<{ children: ReactNode; label: string }>) {
  return (
    <View style={styles.filterGroup}>
      <Text style={styles.filterLabel}>{label}</Text>
      {children}
    </View>
  );
}

function OrderRow({
  locale,
  onPress,
  order,
  selected,
}: Readonly<{
  locale: InstallmentLocale;
  onPress(): void;
  order: InstallmentSummary;
  selected: boolean;
}>) {
  return (
    <PosPressable
      accessibilityLabel={[
        order.installmentNumber,
        order.customerName,
        installmentText(locale, "balance.label"),
        money(order.balanceCents),
        statusLabel(order.status, locale),
      ].join(", ")}
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={({ pressed }) => [
        styles.orderRow,
        selected && styles.orderRowSelected,
        pressed && styles.pressed,
      ]}
      testID={`installment-row-${order.installmentGuid}`}
    >
      <View style={styles.orderIdentity}>
        <Text style={styles.orderNumber}>{order.installmentNumber}</Text>
        <Text numberOfLines={1} style={styles.orderCustomer}>
          {order.customerName} · {order.customerPhone ?? "—"}
        </Text>
        <Text numberOfLines={1} style={styles.orderMeta}>
          {displayDate(order.updatedAtIso, locale)} · {order.cashierName} ·{" "}
          {order.deviceCode}
        </Text>
      </View>
      <View style={styles.orderAmounts}>
        <StatusPill locale={locale} status={order.status} />
        <Text
          style={[
            styles.balanceAmount,
            order.status === "Active"
              ? styles.balanceAmountActive
              : styles.balanceAmountSettled,
          ]}
          testID={`installment-row-balance-${order.installmentGuid}`}
        >
          {money(order.balanceCents)}
        </Text>
        <Text style={styles.balanceLabel}>
          {installmentText(locale, "balance.label")}
        </Text>
      </View>
    </PosPressable>
  );
}

function DetailsPane({
  flex,
  locale,
  presenter,
  state,
}: Readonly<{
  flex: number;
  locale: InstallmentLocale;
  presenter: InstallmentScreenPresenter;
  state: InstallmentPresenterState;
}>) {
  if (state.detailsLoading) {
    return (
      <View style={[styles.pane, styles.detailsPane, { flex }]} testID="installments-details-pane">
        <CenteredState
          loading
          message={installmentText(locale, "details.loading")}
          testID="installments-details-loading"
        />
      </View>
    );
  }
  if (!state.selectedGuid) {
    return (
      <View style={[styles.pane, styles.detailsPane, { flex }]} testID="installments-details-pane">
        <CenteredState
          message={installmentText(locale, "details.empty")}
          testID="installments-details-empty"
        />
      </View>
    );
  }
  if (!state.details) {
    const failed = ["details-failed", "service-unavailable"].includes(
      state.statusCode ?? "",
    );
    return (
      <View style={[styles.pane, styles.detailsPane, { flex }]} testID="installments-details-pane">
        <CenteredState
          {...(failed
            ? {
                actionLabel: installmentText(locale, "action.retryDetails"),
                onAction: () => void presenter.retryDetails(),
              }
            : {})}
          message={installmentText(
            locale,
            failed ? "details.failed" : "details.offlineHint",
          )}
          testID={
            failed
              ? "installments-details-failed"
              : "installments-details-unavailable"
          }
        />
      </View>
    );
  }

  const details = state.details;
  return (
    <View style={[styles.pane, styles.detailsPane, { flex }]} testID="installments-details-pane">
      <View style={styles.detailsBody} testID="installment-details-body">
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.detailsContent}
          style={styles.detailsScroll}
          testID="installment-details"
        >
          <InstallmentDetailsContent
            details={details}
            locale={locale}
            reprint={state.reprint}
          />
        </PosKeyboardAwareScrollView>
      </View>
    </View>
  );
}

function InstallmentDetailsContent({
  details,
  locale,
  reprint,
}: Readonly<{
  details: InstallmentDetails;
  locale: InstallmentLocale;
  reprint: InstallmentPresenterState["reprint"];
}>) {
  const reprintBelongsToDetails =
    "installmentGuid" in reprint &&
    reprint.installmentGuid === details.installmentGuid;
  const paymentProgress =
    details.totalCents > 0
      ? Math.min(
          100,
          Math.max(0, Math.round((details.paidCents / details.totalCents) * 100)),
        )
      : 0;
  return (
    <>
      <View
        style={styles.balanceSummary}
        testID="installment-balance-summary"
      >
        <View style={styles.summaryHeading}>
          <StatusPill locale={locale} status={details.status} />
          <Text style={styles.detailNumber}>{details.installmentNumber}</Text>
        </View>
        <Text numberOfLines={1} style={styles.detailCustomer}>
          {details.customerName} · {details.customerPhone ?? "—"}
        </Text>
        <View style={styles.balanceHero}>
          <Text style={styles.balanceHeroLabel}>
            {installmentText(locale, "metric.amountDue")}
          </Text>
          <Text
            style={styles.balanceHeroAmount}
            testID="installment-balance-amount"
          >
            {money(details.balanceCents)}
          </Text>
        </View>
        <View
          accessibilityLabel={installmentText(
            locale,
            "metric.paymentProgress",
            {
              paid: money(details.paidCents),
              total: money(details.totalCents),
            },
          )}
          accessibilityRole="progressbar"
          accessibilityValue={{ min: 0, max: 100, now: paymentProgress }}
          style={styles.progressTrack}
          testID="installment-payment-progress"
        >
          <View
            style={[styles.progressFill, { width: `${paymentProgress}%` }]}
          />
        </View>
        <View style={styles.summaryFooter}>
          <Text style={styles.summaryPaidText}>
            {installmentText(locale, "metric.paid")} {" "}
            <Text testID="installment-paid-amount">
              {money(details.paidCents)}
            </Text>{" "}
            / {installmentText(locale, "metric.total")} {" "}
            <Text testID="installment-total-amount">
              {money(details.totalCents)}
            </Text>
          </Text>
          <Text style={styles.summaryDownText}>
            {installmentText(locale, "metric.downShort", {
              amount: money(details.downPaymentCents),
            })}
          </Text>
        </View>
      </View>

      {reprintBelongsToDetails && reprint.kind === "succeeded" ? (
        <Text
          accessibilityLiveRegion="polite"
          style={[styles.reprintFeedback, styles.reprintSuccess]}
          testID="installment-reprint-succeeded"
        >
          {installmentText(locale, "reprint.succeeded")}
        </Text>
      ) : null}
      {reprintBelongsToDetails && reprint.kind === "failed" ? (
        <Text
          accessibilityLiveRegion="polite"
          style={[styles.reprintFeedback, styles.reprintFailure]}
          testID="installment-reprint-failed"
        >
          {installmentText(locale, "reprint.failed")}
        </Text>
      ) : null}

      <Section
        title={installmentText(locale, "section.itemsCount", {
          count: details.lines.length,
        })}
      >
        {details.lines.map((line) => (
          <View key={line.installmentLineGuid} style={styles.detailRecord}>
            <View style={styles.recordHeading}>
              <Text style={styles.factPrimary}>{line.displayName}</Text>
              <Text style={styles.factAmount}>{money(line.actualAmountCents)}</Text>
            </View>
            <Text style={styles.factSecondary}>× {line.quantity}</Text>
            <InfoGrid>
              <InfoFact
                label={installmentText(locale, "field.productCode")}
                value={line.productCode}
              />
              <InfoFact
                label={installmentText(locale, "field.lookupCode")}
                value={line.lookupCode}
              />
              <InfoFact
                label={installmentText(locale, "field.referenceCode")}
                value={line.referenceCode ?? "—"}
              />
              <InfoFact
                label={installmentText(locale, "field.itemNumber")}
                value={line.itemNumber ?? "—"}
              />
              <InfoFact
                label={installmentText(locale, "field.unitPrice")}
                value={money(line.unitPriceCents)}
              />
              <InfoFact
                label={installmentText(locale, "field.discount")}
                value={money(line.discountCents)}
              />
              <InfoFact
                label={installmentText(locale, "field.actual")}
                value={money(line.actualAmountCents)}
              />
            </InfoGrid>
          </View>
        ))}
      </Section>

      <Section
        title={installmentText(locale, "section.paymentsCount", {
          count: details.payments.length,
        })}
      >
        {details.payments.length === 0 ? (
          <Text style={styles.emptyHint}>
            {installmentText(locale, "payments.empty")}
          </Text>
        ) : (
          details.payments.map((payment) => (
            <View key={payment.paymentGuid} style={styles.detailRecord}>
              <View style={styles.recordHeading}>
                <Text style={styles.factPrimary}>
                  {methodLabel(payment.method, locale)} ·{" "}
                  {paymentStatusLabel(payment.status, locale)}
                </Text>
                <Text style={styles.factAmount}>{money(payment.amountCents)}</Text>
              </View>
              <InfoGrid>
                <InfoFact
                  label={installmentText(locale, "field.paymentMethod")}
                  value={methodLabel(payment.method, locale)}
                />
                <InfoFact
                  label={installmentText(locale, "field.paymentStatus")}
                  value={paymentStatusLabel(payment.status, locale)}
                />
                <InfoFact
                  label={installmentText(locale, "field.paymentTime")}
                  value={displayDate(payment.recordedAtIso, locale)}
                />
                <InfoFact
                  label={installmentText(locale, "field.cashierId")}
                  value={payment.cashierId}
                />
                <InfoFact
                  label={installmentText(locale, "field.device")}
                  value={payment.deviceCode}
                />
                {payment.cardType || payment.maskedCardNumber ? (
                  <InfoFact
                    label={installmentText(locale, "field.card")}
                    value={[payment.cardType, payment.maskedCardNumber]
                      .filter(Boolean)
                      .join(" · ")}
                  />
                ) : null}
              </InfoGrid>
            </View>
          ))
        )}
      </Section>

      <Section title={installmentText(locale, "section.overview")}>
        <InfoGrid>
          <InfoFact
            label={installmentText(locale, "field.created")}
            value={displayDate(details.createdAtIso, locale)}
          />
          <InfoFact
            label={installmentText(locale, "field.cashier")}
            value={details.cashierName}
          />
          <InfoFact
            label={installmentText(locale, "field.device")}
            value={details.deviceCode}
          />
          <InfoFact
            label={installmentText(locale, "field.minimumDownPayment")}
            value={money(details.minimumDownPaymentCents)}
          />
        </InfoGrid>
      </Section>

      {details.note ? (
        <Section title={installmentText(locale, "section.note")}>
          <Text style={styles.noteText}>{details.note}</Text>
        </Section>
      ) : null}

      {details.pickupInfo ? (
        <Section title={installmentText(locale, "section.pickup")}>
          <View style={styles.completedNote}>
            <Text style={styles.completedTitle}>
              {installmentText(locale, "completed.picked")}
            </Text>
            <InfoGrid>
              <InfoFact
                label={installmentText(locale, "field.pickupBy")}
                value={details.pickupInfo.pickedUpBy}
              />
              <InfoFact
                label={installmentText(locale, "field.pickupTime")}
                value={displayDate(details.pickupInfo.pickedUpAtIso, locale)}
              />
              {details.pickupInfo.note ? (
                <InfoFact
                  label={installmentText(locale, "field.pickupNote")}
                  value={details.pickupInfo.note}
                />
              ) : null}
            </InfoGrid>
          </View>
        </Section>
      ) : null}

      {details.cancellationInfo ? (
        <Section title={installmentText(locale, "section.cancellation")}>
          <View style={styles.cancelledNote}>
            <Text style={styles.completedTitle}>
              {cancellationLabel(details.cancellationInfo.kind, locale)}
            </Text>
            <InfoGrid>
              <InfoFact
                label={installmentText(locale, "field.cancelledBy")}
                value={details.cancellationInfo.cancelledBy}
              />
              <InfoFact
                label={installmentText(locale, "field.cancelledTime")}
                value={displayDate(
                  details.cancellationInfo.cancelledAtIso,
                  locale,
                )}
              />
              <InfoFact
                label={installmentText(locale, "field.cancelReason")}
                value={details.cancellationInfo.reason ?? "—"}
              />
            </InfoGrid>
          </View>
        </Section>
      ) : null}
    </>
  );
}

function DetailsActions({
  confirmation,
  dangerMode,
  details,
  locale,
  moreOpen,
  onRouteFailure,
  onStartRepayment,
  presenter,
  setConfirmation,
  setDangerMode,
  setMoreOpen,
  state,
}: Readonly<{
  confirmation: ConfirmationKind | null;
  dangerMode: DangerMode;
  details: InstallmentDetails;
  locale: InstallmentLocale;
  moreOpen: boolean;
  onRouteFailure(): void;
  onStartRepayment: ((installmentGuid: string) => boolean) | undefined;
  presenter: InstallmentScreenPresenter;
  setConfirmation(value: ConfirmationKind | null): void;
  setDangerMode(value: DangerMode): void;
  setMoreOpen(value: boolean): void;
  state: InstallmentPresenterState;
}>) {
  const selectedDetailsWritable =
    presenter.capabilities.selectedDetailsWritable;
  const selectedDetailsRepayable =
    presenter.capabilities.selectedDetailsRepayable;
  const selectedDetailsCancelRefundable =
    presenter.capabilities.selectedDetailsCancelRefundable;
  const selectedDetailsVoidable =
    presenter.capabilities.selectedDetailsVoidable;
  const selectedDetailsPickupConfirmable =
    presenter.capabilities.selectedDetailsPickupConfirmable;
  if (details.status === "PickedUp" || details.status === "Cancelled") {
    return null;
  }

  if (details.status === "PaidOff") {
    // 中文注释：已付清未提货可提货，也可取消并全额退款；作废（不退款）不对付清单开放。
    if (!selectedDetailsPickupConfirmable && !selectedDetailsCancelRefundable) {
      return null;
    }
    const blocked = selectedDetailsPickupConfirmable
      ? actionBlockReason(state, state.access.canConfirmPickup, true)
      : null;
    const cancelBlocked = selectedDetailsCancelRefundable
      ? actionBlockReason(state, state.access.canCancel, true)
      : null;
    return (
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.actionDockContent}
        style={styles.actionDock}
        testID="installment-action-dock"
      >
        {selectedDetailsPickupConfirmable ? (
          <>
            <Text style={styles.actionDockTitle}>
              {installmentText(locale, "pickup.title")}
            </Text>
            <PosKeyboardAwareTextInput
              accessibilityLabel={installmentText(
                locale,
                "pickup.noteAccessibility",
              )}
              editable={!blocked}
              onChangeText={(value) => presenter.setPickupNote(value)}
              placeholder={installmentText(locale, "pickup.notePlaceholder")}
              style={styles.textInput}
              testID="installment-pickup-note"
              value={state.pickupNote}
            />
          </>
        ) : null}
        <View style={styles.actionDockButtons}>
          {selectedDetailsPickupConfirmable ? (
            <View style={styles.primaryActionGrow}>
              <ActionButton
                disabled={Boolean(blocked)}
                label={installmentText(locale, "action.confirmPickup")}
                onPress={() => {
                  setMoreOpen(false);
                  setDangerMode(null);
                  setConfirmation("pickup");
                }}
                testID="installment-confirm-pickup"
                wide
              />
            </View>
          ) : null}
          {selectedDetailsCancelRefundable ? (
            <ActionButton
              disabled={Boolean(cancelBlocked)}
              label={installmentText(
                locale,
                moreOpen ? "action.closeMore" : "action.more",
              )}
              onPress={() => {
                setMoreOpen(!moreOpen);
                setDangerMode(null);
                setConfirmation(null);
              }}
              testID="installment-more-actions"
              tone="secondary"
            />
          ) : null}
        </View>
        {!selectedDetailsWritable && selectedDetailsCancelRefundable ? (
          <Text
            style={styles.crossDeviceNotice}
            testID="installment-cross-device-notice"
          >
            {installmentText(locale, "details.crossDeviceActionNotice")}
          </Text>
        ) : null}
        {blocked ? (
          <ActionBlockNotice locale={locale} reason={blocked} />
        ) : cancelBlocked ? (
          <ActionBlockNotice locale={locale} reason={cancelBlocked} />
        ) : null}
        {confirmation === "pickup" && !blocked ? (
          <ConfirmationStrip
            kind="pickup"
            locale={locale}
            onCancel={() => setConfirmation(null)}
            onConfirm={() => {
              setConfirmation(null);
              void presenter.confirmPickup();
            }}
          />
        ) : null}
        {moreOpen && !cancelBlocked && selectedDetailsCancelRefundable ? (
          <CancellationPanel
            canCancel
            canVoid={false}
            confirmation={confirmation}
            dangerMode={dangerMode}
            locale={locale}
            presenter={presenter}
            setConfirmation={setConfirmation}
            setDangerMode={setDangerMode}
            state={state}
          />
        ) : null}
      </PosKeyboardAwareScrollView>
    );
  }

  if (
    !selectedDetailsRepayable &&
    !selectedDetailsCancelRefundable &&
    !selectedDetailsVoidable
  ) {
    return null;
  }

  const repaymentBlocked = selectedDetailsRepayable
    ? actionBlockReason(
        state,
        state.access.canAddRepayment,
        Boolean(onStartRepayment),
      )
    : null;
  const moreAvailable =
    selectedDetailsCancelRefundable || selectedDetailsVoidable;
  const moreBlocked = moreAvailable
    ? actionBlockReason(state, state.access.canCancel, true)
    : null;
  return (
    <PosKeyboardAwareScrollView
      contentContainerStyle={styles.actionDockContent}
      style={styles.actionDock}
      testID="installment-action-dock"
    >
      <View style={styles.actionDockButtons}>
        {selectedDetailsRepayable ? (
          <View style={styles.primaryActionGrow}>
            <ActionButton
              disabled={Boolean(repaymentBlocked)}
              label={installmentText(
                locale,
                state.busy ? "action.working" : "action.continuePayment",
              ) + (state.busy ? "" : `  ${money(details.balanceCents)}`)}
              onPress={() => {
                if (!onStartRepayment?.(details.installmentGuid)) {
                  onRouteFailure();
                }
              }}
              testID="installment-continue-to-payment"
              wide
            />
          </View>
        ) : null}
        {moreAvailable ? (
          <ActionButton
            disabled={Boolean(moreBlocked)}
            label={installmentText(
              locale,
              moreOpen ? "action.closeMore" : "action.more",
            )}
            onPress={() => {
              setMoreOpen(!moreOpen);
              setDangerMode(null);
              setConfirmation(null);
            }}
            testID="installment-more-actions"
            tone="secondary"
          />
        ) : null}
      </View>
      {!selectedDetailsWritable ? (
        <Text
          style={styles.crossDeviceNotice}
          testID="installment-cross-device-notice"
        >
          {installmentText(
            locale,
            moreAvailable
              ? "details.crossDeviceActionNotice"
              : "details.crossDeviceRepaymentNotice",
          )}
        </Text>
      ) : null}
      {repaymentBlocked ? (
        <ActionBlockNotice locale={locale} reason={repaymentBlocked} />
      ) : moreBlocked ? (
        <ActionBlockNotice locale={locale} reason={moreBlocked} />
      ) : null}
      {moreOpen && !moreBlocked ? (
        <CancellationPanel
          canCancel={selectedDetailsCancelRefundable}
          canVoid={selectedDetailsVoidable}
          confirmation={confirmation}
          dangerMode={dangerMode}
          locale={locale}
          presenter={presenter}
          setConfirmation={setConfirmation}
          setDangerMode={setDangerMode}
          state={state}
        />
      ) : null}
    </PosKeyboardAwareScrollView>
  );
}

function CancellationPanel({
  canCancel,
  canVoid,
  confirmation,
  dangerMode,
  locale,
  presenter,
  setConfirmation,
  setDangerMode,
  state,
}: Readonly<{
  canCancel: boolean;
  canVoid: boolean;
  confirmation: ConfirmationKind | null;
  dangerMode: DangerMode;
  locale: InstallmentLocale;
  presenter: InstallmentScreenPresenter;
  setConfirmation(value: ConfirmationKind | null): void;
  setDangerMode(value: DangerMode): void;
  state: InstallmentPresenterState;
}>) {
  if (!dangerMode) {
    return (
      <View style={styles.moreMenu} testID="installment-more-menu">
        {canCancel ? (
          <ActionButton
            label={installmentText(locale, "action.refundCancel")}
            onPress={() => setDangerMode("cancel")}
            testID="installment-more-cancel"
            tone="dangerQuiet"
          />
        ) : null}
        {canVoid ? (
          <ActionButton
            label={installmentText(locale, "action.void")}
            onPress={() => setDangerMode("void")}
            testID="installment-more-void"
            tone="dangerQuiet"
          />
        ) : null}
      </View>
    );
  }

  const cancelMode = dangerMode === "cancel";
  return (
    <View style={styles.dangerPanel} testID={`installment-${dangerMode}-panel`}>
      <View style={styles.dangerPanelHeader}>
        <Text style={styles.actionDockTitle}>
          {installmentText(locale, cancelMode ? "cancel.title" : "action.void")}
        </Text>
        <ActionButton
          compact
          label={installmentText(locale, "action.back")}
          onPress={() => {
            setDangerMode(null);
            setConfirmation(null);
          }}
          testID="installment-danger-back"
          tone="quiet"
        />
      </View>
      <PosKeyboardAwareTextInput
        accessibilityLabel={installmentText(
          locale,
          cancelMode ? "cancel.reasonAccessibility" : "void.reasonAccessibility",
        )}
        onChangeText={(value) =>
          cancelMode
            ? presenter.setCancelReason(value)
            : presenter.setVoidReason(value)
        }
        placeholder={installmentText(
          locale,
          cancelMode ? "cancel.reasonPlaceholder" : "void.reasonPlaceholder",
        )}
        style={styles.textInput}
        testID={cancelMode ? "installment-cancel-reason" : "installment-void-reason"}
        value={cancelMode ? state.cancelReason : state.voidReason}
      />
      {cancelMode ? (
        <RefundModeSelector locale={locale} presenter={presenter} />
      ) : null}
      <ActionButton
        label={installmentText(
          locale,
          cancelMode ? "action.refundCancel" : "action.void",
        )}
        onPress={() => setConfirmation(dangerMode)}
        testID={cancelMode ? "installment-cancel-refund" : "installment-void"}
        tone="danger"
        wide
      />
      {confirmation === dangerMode ? (
        <ConfirmationStrip
          kind={dangerMode}
          locale={locale}
          messageKey={
            cancelMode &&
            presenter.capabilities.selectedDetailsCancelRefundMode === "voucher"
              ? "confirmation.cancelVoucher"
              : undefined
          }
          onCancel={() => setConfirmation(null)}
          onConfirm={() => {
            const action = cancelMode
              ? presenter.cancelWithRefund()
              : presenter.voidSelected();
            setConfirmation(null);
            void action;
          }}
        />
      ) : null}
    </View>
  );
}

/**
 * 取消面板内的退款方式选择：原路退回 / 全部退代金券。
 * 含刷卡原付款时原路退不可选，presenter 会强制退代金券，这里只展示原因。
 */
function RefundModeSelector({
  locale,
  presenter,
}: Readonly<{
  locale: InstallmentLocale;
  presenter: InstallmentScreenPresenter;
}>) {
  const originalRouteAvailable =
    presenter.capabilities.selectedDetailsOriginalRouteRefundable;
  const refundMode = presenter.capabilities.selectedDetailsCancelRefundMode;
  const options = ["original-route", "voucher"] as const;
  return (
    <View style={styles.refundModeGroup} testID="installment-cancel-refund-mode">
      <Text style={styles.filterLabel}>
        {installmentText(locale, "cancel.refundModeLabel")}
      </Text>
      <View style={styles.refundModeOptions}>
        {options.map((option) => (
          <ActionButton
            compact
            disabled={option === "original-route" && !originalRouteAvailable}
            key={option}
            label={installmentText(locale, `cancel.refundMode.${option}`)}
            onPress={() => presenter.setCancelRefundMode(option)}
            selected={refundMode === option}
            testID={`installment-cancel-refund-mode-${option}`}
            tone="secondary"
          />
        ))}
      </View>
      <Text
        style={styles.refundModeHint}
        testID="installment-cancel-refund-mode-hint"
      >
        {installmentText(
          locale,
          originalRouteAvailable
            ? `cancel.refundModeHint.${refundMode}`
            : "cancel.cardRequiresVoucher",
        )}
      </Text>
    </View>
  );
}

function ConfirmationStrip({
  kind,
  locale,
  messageKey,
  onCancel,
  onConfirm,
}: Readonly<{
  kind: ConfirmationKind;
  locale: InstallmentLocale;
  messageKey?: "confirmation.cancelVoucher" | undefined;
  onCancel(): void;
  onConfirm(): void;
}>) {
  return (
    <View
      accessibilityLiveRegion="assertive"
      style={styles.confirmation}
      testID={`installment-confirm-${kind}`}
    >
      <Text style={styles.confirmationText}>
        {installmentText(locale, messageKey ?? `confirmation.${kind}`)}
      </Text>
      <View style={styles.confirmationActions}>
        <ActionButton
          label={installmentText(locale, "action.back")}
          onPress={onCancel}
          testID="installment-confirm-operation-cancel"
          tone="quiet"
        />
        <ActionButton
          label={installmentText(locale, "action.confirm")}
          onPress={onConfirm}
          testID="installment-confirm-operation-submit"
          tone={kind === "pickup" ? "primary" : "danger"}
        />
      </View>
    </View>
  );
}

function ActionBlockNotice({
  locale,
  reason,
}: Readonly<{
  locale: InstallmentLocale;
  reason: ActionBlockReason;
}>) {
  return (
    <Text
      accessibilityLiveRegion="polite"
      style={styles.blockedText}
      testID={`installment-action-blocked-${reason}`}
    >
      {installmentText(locale, `blocked.${reason}`)}
    </Text>
  );
}

function actionBlockReason(
  state: InstallmentPresenterState,
  permitted: boolean,
  available: boolean,
): ActionBlockReason | null {
  if (state.recoveryRequired) return "recovery";
  if (!state.online) return "offline";
  if (state.busy || state.reprint.kind === "submitting") return "busy";
  if (!permitted) return "permission";
  if (!available) return "unavailable";
  return null;
}

function InfoGrid({ children }: Readonly<{ children: ReactNode }>) {
  return <View style={styles.infoGrid}>{children}</View>;
}

function InfoFact({
  label,
  value,
}: Readonly<{ label: string; value: string }>) {
  return (
    <View style={styles.infoFact}>
      <Text style={styles.infoLabel}>{label}</Text>
      <Text selectable style={styles.infoValue}>
        {value}
      </Text>
    </View>
  );
}

function Section({
  children,
  title,
}: Readonly<{ children: ReactNode; title: string }>) {
  return (
    <View style={styles.section}>
      <Text style={styles.sectionTitle}>{title}</Text>
      {children}
    </View>
  );
}

function StatusPill({
  locale,
  status,
}: Readonly<{ locale: InstallmentLocale; status: InstallmentStatus }>) {
  return (
    <View
      style={[
        styles.statusPill,
        status === "Active"
          ? styles.statusActive
          : status === "PaidOff"
            ? styles.statusPaid
            : status === "PickedUp"
              ? styles.statusPicked
              : styles.statusCancelled,
      ]}
    >
      <Text style={styles.statusText}>{statusLabel(status, locale)}</Text>
    </View>
  );
}

function StatusBanner({
  locale,
  statusCode,
}: Readonly<{
  locale: InstallmentLocale;
  statusCode: InstallmentStatusCode;
}>) {
  const danger = [
    "action-failed",
    "authorization-declined",
    "cancel-refund-method-unsupported",
    "cancel-refund-mode-unsupported",
    "claim-review-required",
    "conflict",
    "details-failed",
    "history-failed",
    "invalid-create",
    "invalid-date-filter",
    "invalid-repayment",
    "online-required",
    "payment-recovery-required",
    "permission-required",
    "service-unavailable",
  ].includes(statusCode);
  return (
    <NoticeBanner
      assertive={
        statusCode === "payment-recovery-required" ||
        statusCode === "claim-review-required"
      }
      message={statusMessage(statusCode, locale)}
      testID={
        statusCode === "payment-recovery-required"
          ? "installments-payment-recovery-required"
          : `installments-status-${statusCode}`
      }
      tone={danger ? "danger" : "success"}
    />
  );
}

function NoticeBanner({
  assertive = false,
  message,
  testID,
  tone,
}: Readonly<{
  assertive?: boolean;
  message: string;
  testID: string;
  tone: "danger" | "success";
}>) {
  return (
    <View
      accessibilityLiveRegion={assertive ? "assertive" : "polite"}
      style={[
        styles.noticeBanner,
        tone === "danger" ? styles.noticeDanger : styles.noticeSuccess,
      ]}
      testID={testID}
    >
      <Text style={styles.noticeText}>{message}</Text>
    </View>
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
    <View
      accessibilityLiveRegion="polite"
      style={styles.centeredState}
      testID={testID}
    >
      {loading ? <ActivityIndicator color={posColors.orange} size="large" /> : null}
      <Text style={styles.fullStateText}>{message}</Text>
      {actionLabel && onAction ? (
        <ActionButton
          label={actionLabel}
          onPress={onAction}
          testID={`${testID}-action`}
          tone="secondary"
        />
      ) : null}
    </View>
  );
}

export function InstallmentsUnavailableScreen({
  onBack,
}: Readonly<{ onBack(): void }>) {
  const { i18n } = useTranslation();
  const locale = resolveInstallmentLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  return (
    <SafeAreaView style={styles.safeArea} testID="installments-runtime-unavailable">
      <View style={styles.fullState}>
        <Text style={styles.fullStateTitle}>
          {installmentText(locale, "unavailable.title")}
        </Text>
        <Text style={styles.fullStateText}>
          {installmentText(locale, "unavailable.subtitle")}
        </Text>
        <ActionButton
          label={installmentText(locale, "action.backToSales")}
          onPress={onBack}
          sound="navigate"
          testID="installments-unavailable-back"
          wide
        />
      </View>
    </SafeAreaView>
  );
}

function ActionButton({
  accessibilityHint,
  accessibilityLabel,
  compact = false,
  disabled = false,
  expanded,
  label,
  onPress,
  selected = false,
  sound,
  testID,
  tone = "primary",
  wide = false,
}: Readonly<{
  accessibilityHint?: string;
  accessibilityLabel?: string;
  compact?: boolean;
  disabled?: boolean;
  expanded?: boolean;
  label: string;
  onPress(): void;
  selected?: boolean;
  sound?: "tap" | "navigate" | "danger";
  testID: string;
  tone?: "primary" | "secondary" | "quiet" | "danger" | "dangerQuiet";
  wide?: boolean;
}>) {
  return (
    <PosPressable
      accessibilityHint={accessibilityHint}
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ disabled, expanded, selected }}
      disabled={disabled}
      onPress={onPress}
      sound={sound ?? (tone.startsWith("danger") ? "danger" : "tap")}
      style={({ pressed }) => [
        styles.button,
        compact && styles.buttonCompact,
        wide && styles.buttonWide,
        tone === "secondary" && styles.buttonSecondary,
        tone === "quiet" && styles.buttonQuiet,
        tone === "danger" && styles.buttonDanger,
        tone === "dangerQuiet" && styles.buttonDangerQuiet,
        selected && styles.buttonSelected,
        disabled && styles.disabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={testID}
    >
      <Text
        numberOfLines={2}
        style={[
          styles.buttonText,
          (tone === "secondary" || tone === "quiet") &&
            !selected &&
            styles.buttonTextDark,
          tone === "dangerQuiet" && styles.buttonTextDanger,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function hasActiveFilters(state: InstallmentPresenterState): boolean {
  return (
    state.query.trim().length > 0 ||
    state.statusFilter !== null ||
    state.deviceScope !== "store" ||
    state.dateFilter.preset !== "all"
  );
}

function money(cents: number): string {
  return `$${(cents / 100).toFixed(2)}`;
}

function displayDate(iso: string, locale: InstallmentLocale): string {
  const parsed = new Date(iso);
  return Number.isFinite(parsed.getTime())
    ? parsed.toLocaleString(locale === "zh" ? "zh-AU" : "en-AU", {
        day: "2-digit",
        month: "2-digit",
        year: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      })
    : iso;
}

function statusLabel(
  status: InstallmentStatus,
  locale: InstallmentLocale,
): string {
  return installmentText(locale, `status.${status}`);
}

function methodLabel(
  method: InstallmentPaymentMethod,
  locale: InstallmentLocale,
): string {
  return installmentText(locale, `method.${method}`);
}

function paymentStatusLabel(
  status: "Recorded" | "Voided",
  locale: InstallmentLocale,
): string {
  return installmentText(locale, `paymentStatus.${status}`);
}

function cancellationLabel(
  kind: "RefundCancel" | "VoidCancel",
  locale: InstallmentLocale,
): string {
  return installmentText(locale, `cancellation.${kind}`);
}

function statusMessage(
  code: InstallmentStatusCode,
  locale: InstallmentLocale,
): string {
  return installmentText(locale, `status.${code}`);
}

const styles = StyleSheet.create({
  stateSurface: {
    flex: 1,
  },
  frameContent: {
    gap: 0,
    paddingHorizontal: 0,
    paddingVertical: 0,
  },
  safeArea: {
    flex: 1,
    backgroundColor: posColors.canvas,
  },
  page: {
    flex: 1,
    minHeight: 0,
  },
  header: {
    minHeight: 76,
    maxHeight: 80,
    paddingHorizontal: 10,
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    backgroundColor: posColors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  headerIdentity: {
    flex: 1,
    minWidth: 0,
  },
  title: {
    color: posColors.ink,
    fontSize: 22,
    fontWeight: "800",
  },
  connectionState: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    marginTop: 3,
  },
  connectionDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
  },
  connectionDotOnline: {
    backgroundColor: posColors.green,
  },
  connectionDotOffline: {
    backgroundColor: posColors.mutedInk,
  },
  connectionText: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
  headerActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  workspace: {
    flex: 1,
    minHeight: 0,
    flexDirection: "column",
  },
  pane: {
    minWidth: 0,
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderWidth: 1,
  },
  historyPane: {
    padding: 10,
  },
  detailsPane: {
    minHeight: 0,
  },
  detailsBody: {
    flex: 1,
    minHeight: 0,
  },
  panelHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginBottom: 8,
  },
  panelTitle: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "800",
  },
  panelMeta: {
    color: posColors.mutedInk,
    fontSize: 12,
    marginTop: 2,
  },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  searchKeyboardScroll: {
    flexGrow: 0,
  },
  searchInput: {
    flex: 1,
    minHeight: INSTALLMENTS_MIN_TOUCH_TARGET,
    borderColor: posColors.border,
    borderWidth: 1,
    color: posColors.ink,
    backgroundColor: posColors.canvas,
    paddingHorizontal: 11,
    fontSize: 14,
    borderRadius: 6,
  },
  advancedFilters: {
    backgroundColor: posColors.canvas,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    gap: 2,
    padding: 8,
  },
  activeFilterSummary: {
    alignSelf: "flex-start",
    backgroundColor: posColors.blueSoft,
    borderRadius: 6,
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "700",
    paddingHorizontal: 9,
    paddingVertical: 6,
  },
  filterColumns: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 10,
  },
  filterColumnsCompact: {
    flexDirection: "column",
    gap: 2,
  },
  filterGroup: {
    minWidth: 0,
  },
  filterLabel: {
    color: posColors.mutedInk,
    fontSize: 10,
    fontWeight: "800",
    marginTop: 7,
  },
  filterRow: {
    flexDirection: "row",
    gap: 6,
    paddingVertical: 5,
  },
  customDateRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 7,
    paddingBottom: 7,
  },
  orderList: {
    gap: 0,
    paddingTop: 4,
    paddingBottom: 10,
  },
  emptyList: {
    flexGrow: 1,
  },
  orderRow: {
    minHeight: 90,
    borderColor: posColors.border,
    borderBottomWidth: 1,
    padding: 10,
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 10,
    backgroundColor: posColors.surface,
  },
  orderRowSelected: {
    backgroundColor: posColors.orangeSoft,
    borderColor: posColors.orange,
  },
  orderIdentity: {
    flex: 1,
    minWidth: 0,
  },
  orderNumber: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  orderCustomer: {
    color: posColors.ink,
    fontSize: 13,
    marginTop: 3,
  },
  orderMeta: {
    color: posColors.mutedInk,
    fontSize: 11,
    marginTop: 4,
  },
  orderAmounts: {
    alignItems: "flex-end",
    minWidth: 102,
  },
  balanceAmount: {
    fontSize: 18,
    fontWeight: "800",
    marginTop: 5,
  },
  balanceAmountActive: {
    color: posColors.orange,
  },
  balanceAmountSettled: {
    color: posColors.green,
  },
  balanceLabel: {
    color: posColors.mutedInk,
    fontSize: 10,
  },
  loadMoreRow: {
    alignItems: "center",
    paddingVertical: 8,
  },
  detailsScroll: {
    flex: 1,
  },
  detailsContent: {
    flexGrow: 1,
    padding: 12,
    gap: 14,
  },
  balanceSummary: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    gap: 8,
    padding: 12,
  },
  summaryHeading: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  detailNumber: {
    color: posColors.ink,
    flex: 1,
    fontSize: 18,
    fontWeight: "800",
  },
  detailCustomer: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "700",
  },
  balanceHero: {
    gap: 2,
    marginTop: 2,
  },
  balanceHeroLabel: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
  balanceHeroAmount: {
    color: posColors.orange,
    fontSize: 32,
    fontWeight: "800",
    letterSpacing: -0.5,
  },
  progressTrack: {
    backgroundColor: posColors.border,
    borderRadius: 3,
    height: 6,
    overflow: "hidden",
    width: "100%",
  },
  progressFill: {
    backgroundColor: posColors.orange,
    borderRadius: 3,
    height: "100%",
  },
  summaryFooter: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    justifyContent: "space-between",
  },
  summaryPaidText: {
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "700",
  },
  summaryDownText: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
  reprintFeedback: {
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 18,
  },
  reprintSuccess: {
    color: posColors.green,
  },
  reprintFailure: {
    color: posColors.red,
  },
  section: {
    borderTopColor: posColors.border,
    borderTopWidth: 1,
    paddingTop: 10,
    gap: 7,
  },
  sectionTitle: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  detailRecord: {
    borderBottomColor: posColors.border,
    borderBottomWidth: StyleSheet.hairlineWidth,
    paddingVertical: 7,
    gap: 6,
  },
  recordHeading: {
    minHeight: INSTALLMENTS_MIN_TOUCH_TARGET,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 10,
  },
  factPrimary: {
    flex: 1,
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  factSecondary: {
    color: posColors.mutedInk,
    fontSize: 11,
  },
  factAmount: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  infoGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 7,
  },
  infoFact: {
    minWidth: 126,
    flexGrow: 1,
    flexBasis: "30%",
    paddingVertical: 3,
  },
  infoLabel: {
    color: posColors.mutedInk,
    fontSize: 9,
    fontWeight: "700",
  },
  infoValue: {
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "700",
    marginTop: 2,
  },
  noteText: {
    color: posColors.ink,
    fontSize: 13,
    lineHeight: 19,
  },
  completedNote: {
    backgroundColor: posColors.greenSoft,
    borderLeftColor: posColors.green,
    borderLeftWidth: 4,
    padding: 10,
    gap: 7,
  },
  cancelledNote: {
    backgroundColor: posColors.redSoft,
    borderLeftColor: posColors.red,
    borderLeftWidth: 4,
    padding: 10,
    gap: 7,
  },
  completedTitle: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  actionDock: {
    backgroundColor: posColors.surface,
    borderTopColor: posColors.border,
    borderTopWidth: 1,
    flexGrow: 0,
    maxHeight: "60%",
    shadowColor: posColors.ink,
    shadowOffset: { width: 0, height: -2 },
    shadowOpacity: 0.08,
    shadowRadius: 4,
    elevation: 4,
  },
  actionDockContent: {
    gap: 8,
    paddingHorizontal: 10,
    paddingVertical: 8,
  },
  actionDockTitle: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  crossDeviceNotice: {
    backgroundColor: posColors.blueSoft,
    borderLeftColor: posColors.blue,
    borderLeftWidth: 4,
    color: posColors.ink,
    fontSize: 12,
    lineHeight: 18,
    padding: 8,
  },
  actionDockButtons: {
    flexDirection: "row",
    alignItems: "stretch",
    gap: 8,
  },
  primaryActionGrow: {
    flex: 1,
  },
  moreMenu: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    borderTopColor: posColors.border,
    borderTopWidth: 1,
    paddingTop: 8,
  },
  dangerPanel: {
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
    borderWidth: 1,
    padding: 10,
    gap: 8,
  },
  refundModeGroup: {
    gap: 6,
  },
  refundModeOptions: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  refundModeHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
  },
  dangerPanelHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  textInput: {
    minHeight: INSTALLMENTS_MIN_TOUCH_TARGET,
    borderColor: posColors.border,
    borderWidth: 1,
    color: posColors.ink,
    backgroundColor: posColors.surface,
    paddingHorizontal: 11,
    fontSize: 14,
  },
  confirmation: {
    borderTopColor: posColors.red,
    borderTopWidth: 1,
    paddingTop: 8,
    gap: 8,
  },
  confirmationText: {
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 18,
  },
  confirmationActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
  blockedText: {
    color: posColors.mutedInk,
    fontSize: 11,
    lineHeight: 16,
    fontWeight: "700",
  },
  statusPill: {
    paddingHorizontal: 8,
    paddingVertical: 4,
    borderRadius: 6,
    borderWidth: 1,
  },
  statusActive: {
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.blue,
  },
  statusPaid: {
    backgroundColor: posColors.greenSoft,
    borderColor: posColors.green,
  },
  statusPicked: {
    backgroundColor: posColors.greenSoft,
    borderColor: posColors.green,
  },
  statusCancelled: {
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
  },
  statusText: {
    color: posColors.ink,
    fontSize: 10,
    fontWeight: "800",
  },
  noticeBanner: {
    borderLeftWidth: 4,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  noticeDanger: {
    backgroundColor: posColors.redSoft,
    borderLeftColor: posColors.red,
  },
  noticeSuccess: {
    backgroundColor: posColors.greenSoft,
    borderLeftColor: posColors.green,
  },
  noticeText: {
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "700",
  },
  recoveryRow: {
    minHeight: 54,
    paddingHorizontal: 14,
    flexDirection: "row",
    justifyContent: "flex-end",
    alignItems: "center",
    gap: 10,
    backgroundColor: posColors.redSoft,
    borderBottomColor: posColors.red,
    borderBottomWidth: 1,
  },
  recoveryText: {
    flex: 1,
    color: posColors.ink,
    fontSize: 12,
    fontWeight: "700",
  },
  fullState: {
    flex: 1,
    alignSelf: "center",
    justifyContent: "center",
    alignItems: "center",
    maxWidth: 620,
    padding: 28,
    gap: 12,
  },
  fullStateTitle: {
    color: posColors.ink,
    fontSize: 22,
    fontWeight: "800",
    textAlign: "center",
  },
  fullStateText: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    textAlign: "center",
  },
  centeredState: {
    flex: 1,
    minHeight: 150,
    justifyContent: "center",
    alignItems: "center",
    padding: 20,
    gap: 10,
  },
  emptyHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
  },
  button: {
    minHeight: INSTALLMENTS_MIN_TOUCH_TARGET,
    minWidth: INSTALLMENTS_MIN_TOUCH_TARGET,
    paddingHorizontal: 13,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: posColors.orange,
    borderColor: posColors.orange,
    borderRadius: 6,
    borderWidth: 1,
  },
  buttonCompact: {
    paddingHorizontal: 9,
  },
  buttonWide: {
    width: "100%",
  },
  buttonSecondary: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
  },
  buttonQuiet: {
    backgroundColor: "transparent",
    borderColor: posColors.border,
  },
  buttonDanger: {
    backgroundColor: posColors.red,
    borderColor: posColors.red,
  },
  buttonDangerQuiet: {
    backgroundColor: posColors.surface,
    borderColor: posColors.red,
  },
  buttonSelected: {
    backgroundColor: posColors.orange,
    borderColor: posColors.orange,
  },
  buttonText: {
    color: "#FFFFFF",
    fontSize: 12,
    fontWeight: "800",
    textAlign: "center",
  },
  buttonTextDark: {
    color: posColors.ink,
  },
  buttonTextDanger: {
    color: posColors.red,
  },
  disabled: {
    opacity: 0.42,
  },
  pressed: {
    opacity: 0.72,
  },
});
