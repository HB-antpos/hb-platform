import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  FlatList,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
  type ScrollView,
} from "react-native";

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

export const INSTALLMENTS_MIN_TOUCH_TARGET = 44;

const INSTALLMENTS_COMPACT_BREAKPOINT = 900;
/** 宽屏左侧分期列表的固定宽度；右侧详情占满剩余空间。 */
const INSTALLMENTS_LIST_WIDTH = 400;

export type InstallmentLayout = Readonly<{
  compact: boolean;
  /** 宽屏时列表列的固定宽度；窄屏单栏时为 null，列表占满整行。 */
  listWidth: number | null;
  pagePadding: number;
  workspaceGap: number;
}>;

export function installmentLayoutForWidth(width: number): InstallmentLayout {
  return width < INSTALLMENTS_COMPACT_BREAKPOINT
    ? {
        compact: true,
        listWidth: null,
        pagePadding: 8,
        workspaceGap: 8,
      }
    : {
        compact: false,
        listWidth: INSTALLMENTS_LIST_WIDTH,
        pagePadding: 12,
        workspaceGap: 12,
      };
}

export type InstallmentScreenPresenter = Pick<
  InstallmentPresenter,
  | "capabilities"
  | "cancelWithRefund"
  | "closeSupervisorResolve"
  | "confirmPickup"
  | "confirmSupervisorResolve"
  | "getState"
  | "load"
  | "loadMore"
  | "openSupervisorResolve"
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
  | "setSupervisorResolveReason"
  | "setSupervisorResolveReference"
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
/** 状态分段的取值：「全部」对应 presenter 的 null 筛选。 */
type StatusFilterValue = "all" | InstallmentStatus;

/**
 * 详情动作区的可用性模型：由服务端逐项放行的 capability、收银员权限和当前阻塞原因算出。
 * 页面上「滚动区里的表单」和「底部固定的按钮栏」共用同一份结果，避免两处判断不一致。
 */
type DetailsActionModel =
  | Readonly<{
      kind: "pickup";
      pickupConfirmable: boolean;
      cancelRefundable: boolean;
      pickupBlocked: ActionBlockReason | null;
      cancelBlocked: ActionBlockReason | null;
      writable: boolean;
    }>
  | Readonly<{
      kind: "repay";
      repayable: boolean;
      cancelRefundable: boolean;
      voidable: boolean;
      moreAvailable: boolean;
      repaymentBlocked: ActionBlockReason | null;
      moreBlocked: ActionBlockReason | null;
      writable: boolean;
    }>;

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
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { width } = useWindowDimensions();
  const layout = installmentLayoutForWidth(width);
  const { i18n } = useTranslation();
  const locale = resolveInstallmentLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const [compactDetailsOpen, setCompactDetailsOpen] = useState(false);
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

  // 页头右侧主入口：购物车里有草稿时是「新建分期」，没有时是「去销售添加商品」。
  const createDraftAvailable = Boolean(state.createDraft);
  const primaryAvailable = Boolean(state.createDraft ? onStartCreate : onBack);
  const writeLocked =
    state.busy ||
    state.reprint.kind === "submitting" ||
    state.recoveryRequired ||
    !state.online;
  const primaryDisabled = createDraftAvailable
    ? writeLocked || !primaryAvailable
    : !primaryAvailable;
  const refreshing = state.kind === "loading";
  // 窄屏单栏：列表与详情二选一；宽屏两栏同时显示。
  const showHistory = !(layout.compact && compactDetailsOpen);
  const showDetails = !layout.compact || compactDetailsOpen;

  return (
    <PosScreenFrame
      active="installments"
      header={
        <PosPageHeader
          actions={
            <>
              <ConnectionPill locale={locale} online={state.online} />
              <PosButton
                disabled={refreshing || !state.online}
                icon="refresh"
                label={installmentText(
                  locale,
                  refreshing ? "action.refreshing" : "action.refresh",
                )}
                onPress={() => void presenter.load()}
                size="medium"
                testID="installments-refresh"
                tone="quiet"
              />
              {state.access.canCreate ? (
                <PosButton
                  disabled={primaryDisabled}
                  icon={createDraftAvailable ? "plus" : "cart-plus"}
                  label={installmentText(
                    locale,
                    createDraftAvailable ? "action.new" : "action.goToSales",
                  )}
                  onPress={startCreate}
                  size="medium"
                  sound="navigate"
                  testID="installments-primary-action"
                />
              ) : null}
            </>
          }
          backLabel={installmentText(locale, "action.back")}
          backTestID="installments-back"
          onBack={onBack}
          subtitle={installmentText(locale, "subtitle")}
          testID="installments-header"
          title={installmentText(locale, "title")}
        />
      }
      testID="installments-screen"
    >
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
          <PosButton
            disabled={state.busy || state.reprint.kind === "submitting"}
            label={installmentText(locale, "action.recover")}
            onPress={() => void presenter.recoverBlocking()}
            size="medium"
            sound="danger"
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
          {showHistory ? (
            <HistoryFilters
              compact={layout.compact}
              locale={locale}
              presenter={presenter}
              state={state}
            />
          ) : null}
          <View style={[styles.panes, { gap: layout.workspaceGap }]}>
            {showHistory ? (
              <HistoryPane
                listWidth={layout.listWidth}
                locale={locale}
                onOpenOrder={openOrder}
                presenter={presenter}
                state={state}
              />
            ) : null}
            {showDetails ? (
              <DetailsPane
                compact={layout.compact}
                confirmation={confirmation}
                dangerMode={dangerMode}
                locale={locale}
                moreOpen={moreOpen}
                onBackToList={() => setCompactDetailsOpen(false)}
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
        </View>
      )}
    </PosScreenFrame>
  );
}

/** 页头里的联网状态标签：分期全部操作都依赖在线服务，所以一直显示。 */
function ConnectionPill({
  locale,
  online,
}: Readonly<{ locale: InstallmentLocale; online: boolean }>) {
  usePosThemeVersion();
  return (
    <View
      style={[
        styles.connectionPill,
        online ? styles.connectionPillOnline : styles.connectionPillOffline,
      ]}
    >
      <View
        style={[
          styles.connectionDot,
          online ? styles.connectionDotOnline : styles.connectionDotOffline,
        ]}
      />
      <Text
        style={[
          styles.connectionText,
          online ? styles.connectionTextOnline : styles.connectionTextOffline,
        ]}
      >
        {installmentText(locale, online ? "online" : "offlineShort")}
      </Text>
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
  usePosThemeVersion();
  return (
    <View style={styles.fullState} testID="installments-offline-state">
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.mutedInk}
        name="wifi-off"
        size={44}
      />
      <Text style={styles.fullStateTitle}>
        {installmentText(locale, "offlineShort")}
      </Text>
      <Text style={styles.fullStateText}>
        {installmentText(locale, "offline")}
      </Text>
      <PosButton
        disabled={busy}
        icon="refresh"
        label={installmentText(locale, "action.retry")}
        onPress={onRetry}
        testID="installments-offline-retry"
      />
    </View>
  );
}

/**
 * 筛选区两行排完：第一行 搜索 + 查询 + 状态分段；第二行 日期分段 + 范围分段。
 * 选「自定义」时在下面展开起止日期，点「应用」才真正筛选。
 */
function HistoryFilters({
  compact,
  locale,
  presenter,
  state,
}: Readonly<{
  compact: boolean;
  locale: InstallmentLocale;
  presenter: InstallmentScreenPresenter;
  state: InstallmentPresenterState;
}>) {
  usePosThemeVersion();
  const statuses: readonly {
    label: string;
    status: InstallmentStatus | null;
  }[] = [
    { label: installmentText(locale, "filter.all"), status: null },
    { label: installmentText(locale, "filter.active"), status: "Active" },
    { label: installmentText(locale, "filter.paid"), status: "PaidOff" },
    { label: installmentText(locale, "filter.picked"), status: "PickedUp" },
    { label: installmentText(locale, "filter.cancelled"), status: "Cancelled" },
  ];
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

  const chooseDatePreset = (preset: InstallmentDatePreset): void => {
    if (preset === "custom") {
      setCustomDatesOpen(true);
      return;
    }
    setCustomDatesOpen(false);
    void presenter.setDateFilter({ preset, fromDate: null, toDate: null });
  };

  // 自定义日期展开期间高亮「自定义」：这样再点其它预设才会收起日期选择并立即生效。
  const datePresetValue: InstallmentDatePreset = customDatesOpen
    ? "custom"
    : state.dateFilter.preset;

  return (
    <View style={styles.filters} testID="installments-filters">
      {/* 搜索框需置于键盘感知滚动容器内，软键盘弹出时才不会遮挡输入。 */}
      <PosKeyboardAwareScrollView
        contentContainerStyle={[
          styles.filterRow,
          compact && styles.filterRowCompact,
        ]}
        scrollEnabled={false}
        style={styles.searchKeyboardScroll}
        testID="installment-history-search-keyboard-scroll"
      >
        <View
          style={[styles.searchField, compact && styles.searchFieldCompact]}
        >
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.mutedInk}
            name="magnify"
            size={22}
          />
          <PosKeyboardAwareTextInput
            accessibilityLabel={installmentText(locale, "search.accessibility")}
            autoCapitalize="none"
            autoCorrect={false}
            onChangeText={(value) => presenter.setSearchQuery(value)}
            onSubmitEditing={() => void presenter.load()}
            placeholder={installmentText(locale, "search.placeholder")}
            placeholderTextColor={posColors.placeholder}
            returnKeyType="search"
            style={styles.searchInput}
            testID="installments-search"
            value={state.query}
          />
        </View>
        <PosButton
          disabled={state.kind === "loading"}
          label={installmentText(locale, "action.search")}
          onPress={() => void presenter.load()}
          size="medium"
          testID="installments-search-submit"
        />
        <PosSegmented<StatusFilterValue>
          accessibilityLabel={installmentText(locale, "filter.statusLabel")}
          onChange={(value) =>
            void presenter.setStatusFilter(value === "all" ? null : value)
          }
          options={statuses.map((item) => ({
            value: item.status ?? "all",
            label: item.label,
            testID: `installments-filter-${item.status ?? "all"}`,
          }))}
          style={compact ? styles.segmentedFullRow : styles.segmentedFill}
          value={state.statusFilter ?? "all"}
        />
      </PosKeyboardAwareScrollView>

      <View style={[styles.filterRow, compact && styles.filterRowCompact]}>
        <Text style={styles.filterLabel}>
          {installmentText(locale, "date.label")}
        </Text>
        <PosSegmented<InstallmentDatePreset>
          accessibilityLabel={installmentText(locale, "date.label")}
          onChange={chooseDatePreset}
          options={(
            ["all", "today", "last7", "last30", "custom"] as const
          ).map((preset) => ({
            value: preset,
            label: installmentText(locale, `date.${preset}`),
            testID: `installments-date-${preset}`,
          }))}
          style={compact ? styles.segmentedFullRow : styles.segmentedFill}
          value={datePresetValue}
        />
        <Text style={styles.filterLabel}>
          {installmentText(locale, "scope.label")}
        </Text>
        <PosSegmented<InstallmentPresenterState["deviceScope"]>
          accessibilityLabel={installmentText(locale, "scope.label")}
          onChange={(scope) => void presenter.setDeviceScope(scope)}
          options={(["store", "device"] as const).map((scope) => ({
            value: scope,
            label: installmentText(locale, `scope.${scope}`),
            testID: `installments-scope-${scope}`,
          }))}
          style={compact ? styles.segmentedFill : styles.scopeSegmented}
          value={state.deviceScope}
        />
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
          <PosButton
            label={installmentText(locale, "date.apply")}
            onPress={() =>
              void presenter.setDateFilter({
                preset: "custom",
                fromDate,
                toDate,
              })
            }
            size="medium"
            testID="installments-date-apply"
          />
        </View>
      ) : null}
    </View>
  );
}

function HistoryPane({
  listWidth,
  locale,
  onOpenOrder,
  presenter,
  state,
}: Readonly<{
  listWidth: number | null;
  locale: InstallmentLocale;
  onOpenOrder(installmentGuid: string): void;
  presenter: InstallmentScreenPresenter;
  state: InstallmentPresenterState;
}>) {
  usePosThemeVersion();
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
      style={[
        styles.historyPane,
        listWidth === null ? styles.paneFill : { width: listWidth },
      ]}
      testID="installments-history-pane"
    >
      <View style={styles.panelHeader}>
        <Text style={styles.panelTitle}>
          {installmentText(locale, "history.title")}
        </Text>
        <PosPill
          label={installmentText(
            locale,
            state.orders.length === 1 ? "history.countOne" : "history.count",
            { count: state.orders.length },
          )}
          style={styles.countPill}
          testID="installments-result-count"
        />
        {state.kind === "loading" && state.orders.length > 0 ? (
          <ActivityIndicator color={posColors.accent} />
        ) : null}
      </View>

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
        renderItem={({ item }) => (
          <OrderRow
            locale={locale}
            onPress={() => onOpenOrder(item.installmentGuid)}
            order={item}
            selected={state.selectedGuid === item.installmentGuid}
          />
        )}
        style={styles.orderListScroll}
        testID="installments-list"
      />
      {/* 「加载更多」固定在列表下方，不用滚到底才看得到。 */}
      {state.orders.length > 0 && state.hasMore ? (
        <PosButton
          disabled={state.loadingMore || state.kind === "loading"}
          icon="chevron-down"
          label={installmentText(
            locale,
            state.loadingMore ? "action.loadingMore" : "action.loadMore",
          )}
          onPress={() => void presenter.loadMore()}
          size="small"
          testID="installments-load-more"
          tone="quiet"
        />
      ) : null}
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
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={[
        order.installmentNumber,
        order.customerName,
        statusLabel(order.status, locale),
        installmentText(locale, "balance.label"),
        money(order.balanceCents),
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
        <View style={styles.orderTitleLine}>
          <Text numberOfLines={1} style={styles.orderNumber}>
            {order.installmentNumber}
          </Text>
          <StatusPill locale={locale} status={order.status} />
        </View>
        <Text numberOfLines={1} style={styles.orderCustomer}>
          {order.customerName} · {order.customerPhone ?? "—"}
        </Text>
        <Text numberOfLines={1} style={styles.orderMeta}>
          {displayDate(order.updatedAtIso, locale)} · {order.cashierName} ·{" "}
          {order.deviceCode}
        </Text>
      </View>
      <View style={styles.orderAmounts}>
        <Text style={styles.balanceAmount}>{money(order.balanceCents)}</Text>
        <Text style={styles.balanceLabel}>
          {installmentText(locale, "balance.label")}
        </Text>
      </View>
    </PosPressable>
  );
}

function DetailsPane({
  compact,
  confirmation,
  dangerMode,
  locale,
  moreOpen,
  onBackToList,
  onRouteFailure,
  onStartRepayment,
  presenter,
  setConfirmation,
  setDangerMode,
  setMoreOpen,
  state,
}: Readonly<{
  compact: boolean;
  confirmation: ConfirmationKind | null;
  dangerMode: DangerMode;
  locale: InstallmentLocale;
  moreOpen: boolean;
  onBackToList(): void;
  onRouteFailure(): void;
  onStartRepayment: ((installmentGuid: string) => boolean) | undefined;
  presenter: InstallmentScreenPresenter;
  setConfirmation(value: ConfirmationKind | null): void;
  setDangerMode(value: DangerMode): void;
  setMoreOpen(value: boolean): void;
  state: InstallmentPresenterState;
}>) {
  usePosThemeVersion();
  const detailsScrollRef = useRef<ScrollView>(null);

  // 取货备注和危险操作表单排在详情滚动区的最后。出现确认条或展开危险操作时，
  // 自动滚到底，让它们紧贴底部按钮栏露出来，不用收银员自己去找。
  useEffect(() => {
    if (!dangerMode && !confirmation) return undefined;
    const frame = requestAnimationFrame(() => {
      detailsScrollRef.current?.scrollToEnd?.({ animated: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [confirmation, dangerMode]);

  const compactBack = compact ? (
    <View style={styles.compactDetailsHeader}>
      <PosButton
        icon="arrow-left"
        label={installmentText(locale, "action.back")}
        onPress={onBackToList}
        size="medium"
        sound="navigate"
        testID="installments-details-back"
        tone="quiet"
      />
      <Text style={styles.compactDetailsTitle}>
        {installmentText(locale, "details.empty")}
      </Text>
    </View>
  ) : null;

  if (state.detailsLoading) {
    return (
      <View style={styles.detailsPane} testID="installments-details-pane">
        {compactBack}
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
      <View style={styles.detailsPane} testID="installments-details-pane">
        {compactBack}
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
      <View style={styles.detailsPane} testID="installments-details-pane">
        {compactBack}
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
  const actions = resolveDetailsActionModel(
    details,
    presenter,
    state,
    Boolean(onStartRepayment),
  );
  return (
    <View style={styles.detailsPane} testID="installments-details-pane">
      {compactBack}
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.detailsContent}
        ref={detailsScrollRef}
        style={styles.detailsScroll}
        testID="installment-details"
      >
        <InstallmentDetailsContent
          canReprint={presenter.capabilities.reprint}
          details={details}
          locale={locale}
          onReprint={() => void presenter.reprintSelected()}
          reprint={state.reprint}
        />
        <SupervisorResolvePanel
          locale={locale}
          presenter={presenter}
          state={state}
        />
        {/* 带输入框的部分留在键盘感知滚动区里，软键盘弹出时才不会被挡住。 */}
        <DetailsActionForms
          actions={actions}
          confirmation={confirmation}
          dangerMode={dangerMode}
          locale={locale}
          moreOpen={moreOpen}
          presenter={presenter}
          setConfirmation={setConfirmation}
          setDangerMode={setDangerMode}
          state={state}
        />
      </PosKeyboardAwareScrollView>
      {/* 主按钮栏固定在详情底部，不随内容滚动。 */}
      <DetailsActionDock
        actions={actions}
        confirmation={confirmation}
        dangerMode={dangerMode}
        details={details}
        locale={locale}
        moreOpen={moreOpen}
        onRouteFailure={onRouteFailure}
        onStartRepayment={onStartRepayment}
        presenter={presenter}
        setConfirmation={setConfirmation}
        setDangerMode={setDangerMode}
        setMoreOpen={setMoreOpen}
        state={state}
      />
    </View>
  );
}

function InstallmentDetailsContent({
  canReprint,
  details,
  locale,
  onReprint,
  reprint,
}: Readonly<{
  canReprint: boolean;
  details: InstallmentDetails;
  locale: InstallmentLocale;
  onReprint(): void;
  reprint: InstallmentPresenterState["reprint"];
}>) {
  usePosThemeVersion();
  const reprintBelongsToDetails =
    "installmentGuid" in reprint &&
    reprint.installmentGuid === details.installmentGuid;
  const reprintSubmitting =
    reprintBelongsToDetails && reprint.kind === "submitting";
  const showReprint = canReprint || reprintSubmitting;
  return (
    <>
      <View style={styles.detailHeading}>
        <View style={styles.detailIdentity}>
          <View style={styles.detailTitleLine}>
            <Text numberOfLines={1} style={styles.detailNumber}>
              {details.installmentNumber}
            </Text>
            <StatusPill locale={locale} status={details.status} />
          </View>
          <Text numberOfLines={1} style={styles.detailCustomer}>
            {details.customerName} · {details.customerPhone ?? "—"}
          </Text>
          {/* 原「分期信息」四项并入标题下方一行，不再单占一个分区。 */}
          <MetaRow>
            <MetaPair
              label={installmentText(locale, "field.created")}
              value={displayDate(details.createdAtIso, locale)}
            />
            <MetaPair
              label={installmentText(locale, "field.cashier")}
              value={details.cashierName}
            />
            <MetaPair
              label={installmentText(locale, "field.device")}
              value={details.deviceCode}
            />
            <MetaPair
              label={installmentText(locale, "field.minimumDownPayment")}
              value={money(details.minimumDownPaymentCents)}
            />
          </MetaRow>
        </View>
        {showReprint ? (
          <PosButton
            accessibilityHint={installmentText(locale, "reprint.hint")}
            accessibilityLabel={installmentText(
              locale,
              "reprint.accessibility",
              { installmentNumber: details.installmentNumber },
            )}
            disabled={reprintSubmitting}
            icon="printer-outline"
            label={installmentText(
              locale,
              reprintSubmitting ? "action.reprinting" : "action.reprint",
            )}
            onPress={onReprint}
            size="small"
            testID="installment-reprint"
            tone="quiet"
          />
        ) : null}
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

      <View style={styles.metrics}>
        <Metric
          label={installmentText(locale, "metric.total")}
          value={money(details.totalCents)}
        />
        <Metric
          label={installmentText(locale, "metric.down")}
          value={money(details.downPaymentCents)}
        />
        <Metric
          label={installmentText(locale, "metric.paid")}
          tone="paid"
          value={money(details.paidCents)}
        />
        <Metric
          label={installmentText(locale, "metric.balance")}
          tone="balance"
          value={money(details.balanceCents)}
        />
      </View>

      <Section title={installmentText(locale, "section.items")}>
        {details.lines.map((line) => (
          <View key={line.installmentLineGuid} style={styles.detailRecord}>
            <View style={styles.recordCopy}>
              <Text style={styles.recordTitle}>{line.displayName}</Text>
              <MetaRow>
                <MetaPair
                  label={installmentText(locale, "field.productCode")}
                  value={line.productCode}
                />
                <MetaPair
                  label={installmentText(locale, "field.lookupCode")}
                  value={line.lookupCode}
                />
                <MetaPair
                  label={installmentText(locale, "field.referenceCode")}
                  value={line.referenceCode ?? "—"}
                />
                <MetaPair
                  label={installmentText(locale, "field.itemNumber")}
                  value={line.itemNumber ?? "—"}
                />
                <MetaPair
                  label={installmentText(locale, "field.unitPrice")}
                  value={money(line.unitPriceCents)}
                />
                <MetaPair
                  label={installmentText(locale, "field.discount")}
                  value={money(line.discountCents)}
                />
                <MetaPair
                  label={installmentText(locale, "field.actual")}
                  value={money(line.actualAmountCents)}
                />
              </MetaRow>
            </View>
            <Text style={styles.recordQuantity}>× {line.quantity}</Text>
            <Text style={styles.recordAmount}>
              {money(line.actualAmountCents)}
            </Text>
          </View>
        ))}
      </Section>

      <Section title={installmentText(locale, "section.payments")}>
        {details.payments.length === 0 ? (
          <Text style={styles.emptyHint}>
            {installmentText(locale, "payments.empty")}
          </Text>
        ) : (
          details.payments.map((payment) => (
            <View key={payment.paymentGuid} style={styles.detailRecord}>
              <View style={styles.recordCopy}>
                {/*
                  标题已包含付款方式与状态，下方只列时间、收银员、设备和卡信息；
                  「方式」「状态」两个字段名保留给读屏使用。
                */}
                <Text
                  accessibilityLabel={[
                    `${installmentText(locale, "field.paymentMethod")} ${methodLabel(payment.method, locale)}`,
                    `${installmentText(locale, "field.paymentStatus")} ${paymentStatusLabel(payment.status, locale)}`,
                  ].join(", ")}
                  style={styles.recordTitle}
                >
                  {methodLabel(payment.method, locale)} ·{" "}
                  {paymentStatusLabel(payment.status, locale)}
                </Text>
                <MetaRow>
                  <MetaPair
                    label={installmentText(locale, "field.paymentTime")}
                    value={displayDate(payment.recordedAtIso, locale)}
                  />
                  <MetaPair
                    label={installmentText(locale, "field.cashierId")}
                    value={payment.cashierId}
                  />
                  <MetaPair
                    label={installmentText(locale, "field.device")}
                    value={payment.deviceCode}
                  />
                  {payment.cardType || payment.maskedCardNumber ? (
                    <MetaPair
                      label={installmentText(locale, "field.card")}
                      value={[payment.cardType, payment.maskedCardNumber]
                        .filter(Boolean)
                        .join(" · ")}
                    />
                  ) : null}
                </MetaRow>
              </View>
              <Text style={styles.recordAmount}>
                {money(payment.amountCents)}
              </Text>
            </View>
          ))
        )}
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
            <MetaRow>
              <MetaPair
                label={installmentText(locale, "field.pickupBy")}
                value={details.pickupInfo.pickedUpBy}
              />
              <MetaPair
                label={installmentText(locale, "field.pickupTime")}
                value={displayDate(details.pickupInfo.pickedUpAtIso, locale)}
              />
              {details.pickupInfo.note ? (
                <MetaPair
                  label={installmentText(locale, "field.pickupNote")}
                  value={details.pickupInfo.note}
                />
              ) : null}
            </MetaRow>
          </View>
        </Section>
      ) : null}

      {details.cancellationInfo ? (
        <Section title={installmentText(locale, "section.cancellation")}>
          <View style={styles.cancelledNote}>
            <Text style={styles.completedTitle}>
              {cancellationLabel(details.cancellationInfo.kind, locale)}
            </Text>
            <MetaRow>
              <MetaPair
                label={installmentText(locale, "field.cancelledBy")}
                value={details.cancellationInfo.cancelledBy}
              />
              <MetaPair
                label={installmentText(locale, "field.cancelledTime")}
                value={displayDate(
                  details.cancellationInfo.cancelledAtIso,
                  locale,
                )}
              />
              <MetaPair
                label={installmentText(locale, "field.cancelReason")}
                value={details.cancellationInfo.reason ?? "—"}
              />
            </MetaRow>
          </View>
        </Section>
      ) : null}
    </>
  );
}

/**
 * 算出当前详情可以做哪些动作、各自被什么原因阻塞。
 * 判断条件与改版前逐条一致：已取货 / 已取消没有动作；已付清走取货（可另行退款取消，不提供作废）；
 * 其余走续付，退款取消和作废收在「更多操作」里。
 */
function resolveDetailsActionModel(
  details: InstallmentDetails,
  presenter: InstallmentScreenPresenter,
  state: InstallmentPresenterState,
  repaymentRouteAvailable: boolean,
): DetailsActionModel | null {
  const writable = presenter.capabilities.selectedDetailsWritable;
  const repayable = presenter.capabilities.selectedDetailsRepayable;
  const cancelRefundable =
    presenter.capabilities.selectedDetailsCancelRefundable;
  const voidable = presenter.capabilities.selectedDetailsVoidable;
  const pickupConfirmable =
    presenter.capabilities.selectedDetailsPickupConfirmable;
  if (details.status === "PickedUp" || details.status === "Cancelled") {
    return null;
  }

  if (details.status === "PaidOff") {
    // 中文注释：已付清未提货可提货，也可取消并全额退款；作废（不退款）不对付清单开放。
    if (!pickupConfirmable && !cancelRefundable) return null;
    return {
      kind: "pickup",
      pickupConfirmable,
      cancelRefundable,
      pickupBlocked: pickupConfirmable
        ? actionBlockReason(state, state.access.canConfirmPickup, true)
        : null,
      cancelBlocked: cancelRefundable
        ? actionBlockReason(state, state.access.canCancel, true)
        : null,
      writable,
    };
  }

  if (!repayable && !cancelRefundable && !voidable) return null;
  const moreAvailable = cancelRefundable || voidable;
  return {
    kind: "repay",
    repayable,
    cancelRefundable,
    voidable,
    moreAvailable,
    repaymentBlocked: repayable
      ? actionBlockReason(
          state,
          state.access.canAddRepayment,
          repaymentRouteAvailable,
        )
      : null,
    moreBlocked: moreAvailable
      ? actionBlockReason(state, state.access.canCancel, true)
      : null,
    writable,
  };
}

/** 「更多操作」是否已展开且允许显示其内容（菜单或危险操作表单）。 */
function moreActionsVisible(
  actions: DetailsActionModel,
  moreOpen: boolean,
): boolean {
  if (!moreOpen) return false;
  return actions.kind === "pickup"
    ? !actions.cancelBlocked && actions.cancelRefundable
    : !actions.moreBlocked;
}

/**
 * 详情滚动区末尾的表单：已付清时的取货备注，以及从「更多操作」进入的退款取消 / 作废表单。
 * 这些都带输入框，必须留在键盘感知滚动区里。
 */
function DetailsActionForms({
  actions,
  confirmation,
  dangerMode,
  locale,
  moreOpen,
  presenter,
  setConfirmation,
  setDangerMode,
  state,
}: Readonly<{
  actions: DetailsActionModel | null;
  confirmation: ConfirmationKind | null;
  dangerMode: DangerMode;
  locale: InstallmentLocale;
  moreOpen: boolean;
  presenter: InstallmentScreenPresenter;
  setConfirmation(value: ConfirmationKind | null): void;
  setDangerMode(value: DangerMode): void;
  state: InstallmentPresenterState;
}>) {
  usePosThemeVersion();
  if (!actions) return null;
  const showPickupNote = actions.kind === "pickup" && actions.pickupConfirmable;
  const showDangerPanel =
    Boolean(dangerMode) && moreActionsVisible(actions, moreOpen);
  if (!showPickupNote && !showDangerPanel) return null;
  return (
    <View style={styles.actionForms} testID="installment-action-forms">
      {actions.kind === "pickup" && actions.pickupConfirmable ? (
        <View style={styles.pickupPanel}>
          <Text style={styles.actionDockTitle}>
            {installmentText(locale, "pickup.title")}
          </Text>
          <PosKeyboardAwareTextInput
            accessibilityLabel={installmentText(
              locale,
              "pickup.noteAccessibility",
            )}
            editable={!actions.pickupBlocked}
            onChangeText={(value) => presenter.setPickupNote(value)}
            placeholder={installmentText(locale, "pickup.notePlaceholder")}
            placeholderTextColor={posColors.placeholder}
            style={styles.textInput}
            testID="installment-pickup-note"
            value={state.pickupNote}
          />
        </View>
      ) : null}
      {showDangerPanel && dangerMode ? (
        <DangerPanel
          confirmation={confirmation}
          dangerMode={dangerMode}
          locale={locale}
          presenter={presenter}
          setConfirmation={setConfirmation}
          setDangerMode={setDangerMode}
          state={state}
        />
      ) : null}
    </View>
  );
}

/**
 * 详情底部固定的动作栏：主按钮（继续付款 / 确认已取货）+「更多操作」。
 * 提示、取货二次确认条和「更多操作」展开的两个入口显示在按钮上方。
 */
function DetailsActionDock({
  actions,
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
  actions: DetailsActionModel | null;
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
  usePosThemeVersion();
  if (!actions) return null;

  const toggleMore = (): void => {
    setMoreOpen(!moreOpen);
    setDangerMode(null);
    setConfirmation(null);
  };
  // 展开后、尚未选具体危险操作时，在按钮上方列出两个入口；选定后表单显示在滚动区末尾。
  const showMoreMenu = moreActionsVisible(actions, moreOpen) && !dangerMode;
  const moreButton = (disabled: boolean): ReactNode => (
    <PosButton
      disabled={disabled}
      label={installmentText(
        locale,
        moreOpen ? "action.closeMore" : "action.more",
      )}
      onPress={toggleMore}
      size="large"
      testID="installment-more-actions"
      tone="quiet"
      trailingIcon={moreOpen ? "chevron-down" : "chevron-up"}
    />
  );

  if (actions.kind === "pickup") {
    const blocked = actions.pickupBlocked;
    const cancelBlocked = actions.cancelBlocked;
    return (
      <View style={styles.actionDock} testID="installment-action-dock">
        {!actions.writable && actions.cancelRefundable ? (
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
        {showMoreMenu ? (
          <MoreMenu
            canCancel
            canVoid={false}
            locale={locale}
            setDangerMode={setDangerMode}
          />
        ) : null}
        <View style={styles.actionDockButtons}>
          {actions.pickupConfirmable ? (
            <PosButton
              disabled={Boolean(blocked)}
              fill
              icon="package-variant-closed-check"
              label={installmentText(locale, "action.confirmPickup")}
              onPress={() => {
                setMoreOpen(false);
                setDangerMode(null);
                setConfirmation("pickup");
              }}
              size="large"
              testID="installment-confirm-pickup"
              tone="primary"
            />
          ) : null}
          {actions.cancelRefundable
            ? moreButton(Boolean(cancelBlocked))
            : null}
        </View>
      </View>
    );
  }

  const repaymentBlocked = actions.repaymentBlocked;
  const moreBlocked = actions.moreBlocked;
  return (
    <View style={styles.actionDock} testID="installment-action-dock">
      {!actions.writable ? (
        <Text
          style={styles.crossDeviceNotice}
          testID="installment-cross-device-notice"
        >
          {installmentText(
            locale,
            actions.moreAvailable
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
      {showMoreMenu ? (
        <MoreMenu
          canCancel={actions.cancelRefundable}
          canVoid={actions.voidable}
          locale={locale}
          setDangerMode={setDangerMode}
        />
      ) : null}
      <View style={styles.actionDockButtons}>
        {actions.repayable ? (
          <PosButton
            disabled={Boolean(repaymentBlocked)}
            fill
            icon="cash-plus"
            label={installmentText(
              locale,
              state.busy ? "action.working" : "action.continuePayment",
            )}
            onPress={() => {
              if (!onStartRepayment?.(details.installmentGuid)) {
                onRouteFailure();
              }
            }}
            size="large"
            testID="installment-continue-to-payment"
            tone="primary"
          />
        ) : null}
        {actions.moreAvailable ? moreButton(Boolean(moreBlocked)) : null}
      </View>
    </View>
  );
}

/** 「更多操作」展开后的两个危险入口；点了才进入对应表单，仍需填原因并二次确认。 */
function MoreMenu({
  canCancel,
  canVoid,
  locale,
  setDangerMode,
}: Readonly<{
  canCancel: boolean;
  canVoid: boolean;
  locale: InstallmentLocale;
  setDangerMode(value: DangerMode): void;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.moreMenu} testID="installment-more-menu">
      {canCancel ? (
        <PosButton
          fill
          label={installmentText(locale, "action.refundCancel")}
          onPress={() => setDangerMode("cancel")}
          sound="danger"
          testID="installment-more-cancel"
          tone="danger"
        />
      ) : null}
      {canVoid ? (
        <PosButton
          fill
          label={installmentText(locale, "action.void")}
          onPress={() => setDangerMode("void")}
          sound="danger"
          testID="installment-more-void"
          tone="danger"
        />
      ) : null}
    </View>
  );
}

/** 退款取消 / 作废表单：填原因（取消还要选退款方式）→ 点危险按钮 → 二次确认条。 */
function DangerPanel({
  confirmation,
  dangerMode,
  locale,
  presenter,
  setConfirmation,
  setDangerMode,
  state,
}: Readonly<{
  confirmation: ConfirmationKind | null;
  dangerMode: "cancel" | "void";
  locale: InstallmentLocale;
  presenter: InstallmentScreenPresenter;
  setConfirmation(value: ConfirmationKind | null): void;
  setDangerMode(value: DangerMode): void;
  state: InstallmentPresenterState;
}>) {
  usePosThemeVersion();
  const cancelMode = dangerMode === "cancel";
  return (
    <View style={styles.dangerPanel} testID={`installment-${dangerMode}-panel`}>
      <View style={styles.dangerPanelHeader}>
        <Text style={[styles.actionDockTitle, styles.grow]}>
          {installmentText(locale, cancelMode ? "cancel.title" : "action.void")}
        </Text>
        <PosButton
          label={installmentText(locale, "action.back")}
          onPress={() => {
            setDangerMode(null);
            setConfirmation(null);
          }}
          size="small"
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
        placeholderTextColor={posColors.placeholder}
        style={styles.textInput}
        testID={cancelMode ? "installment-cancel-reason" : "installment-void-reason"}
        value={cancelMode ? state.cancelReason : state.voidReason}
      />
      {cancelMode ? (
        <RefundModeSelector locale={locale} presenter={presenter} />
      ) : null}
      <PosButton
        label={installmentText(
          locale,
          cancelMode ? "action.refundCancel" : "action.void",
        )}
        onPress={() => setConfirmation(dangerMode)}
        sound="danger"
        testID={cancelMode ? "installment-cancel-refund" : "installment-void"}
        tone="danger"
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
  usePosThemeVersion();
  const originalRouteAvailable =
    presenter.capabilities.selectedDetailsOriginalRouteRefundable;
  const refundMode = presenter.capabilities.selectedDetailsCancelRefundMode;
  const options = ["original-route", "voucher"] as const;
  return (
    <View style={styles.refundModeGroup} testID="installment-cancel-refund-mode">
      <Text style={styles.fieldLabel}>
        {installmentText(locale, "cancel.refundModeLabel")}
      </Text>
      <View style={styles.refundModeOptions}>
        {options.map((option) => (
          <ChoiceButton
            disabled={option === "original-route" && !originalRouteAvailable}
            key={option}
            label={installmentText(locale, `cancel.refundMode.${option}`)}
            onPress={() => presenter.setCancelRefundMode(option)}
            selected={refundMode === option}
            testID={`installment-cancel-refund-mode-${option}`}
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

/**
 * 二选一的选项按钮（退款方式）。保留「按钮 + selected」的无障碍语义，
 * 已选项再点一次仍会回调，行为与改版前一致。
 */
function ChoiceButton({
  disabled = false,
  label,
  onPress,
  selected,
  testID,
}: Readonly<{
  disabled?: boolean;
  label: string;
  onPress(): void;
  selected: boolean;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled, selected }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.choice,
        selected && styles.choiceSelected,
        disabled && styles.choiceDisabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={testID}
    >
      <Text
        numberOfLines={2}
        style={[
          styles.choiceText,
          selected && styles.choiceTextSelected,
          disabled && styles.choiceTextDisabled,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

/**
 * 卡状态不明的刷卡续付占用该分期时，让有分期取消权限的主管核实后结案（对齐 WPF #1194）。
 * 只展示安全描述符（金额、通道、发起设备、时间），不含卡引用。没有权限只提示找主管，不展示结案按钮。
 */
function SupervisorResolvePanel({
  locale,
  presenter,
  state,
}: Readonly<{
  locale: InstallmentLocale;
  presenter: InstallmentScreenPresenter;
  state: InstallmentPresenterState;
}>) {
  usePosThemeVersion();
  const pending = state.pendingCardRepayment;
  if (!pending || !state.online) return null;
  const dialog = state.supervisorResolve;
  const detail = [
    `${installmentText(locale, "supervisor.detailAmount")}: ${money(pending.amountCents)}`,
    `${installmentText(locale, "supervisor.detailProvider")}: ${pending.provider}`,
    ...(pending.claimantDeviceCode
      ? [`${installmentText(locale, "supervisor.detailDevice")}: ${pending.claimantDeviceCode}`]
      : []),
    `${installmentText(locale, "supervisor.detailUpdated")}: ${localMinute(pending.updatedAtIso)}`,
  ].join(" · ");
  return (
    <View style={styles.dangerPanel} testID="installment-supervisor-panel">
      <Text style={styles.actionDockTitle}>
        {installmentText(locale, "supervisor.title")}
      </Text>
      <Text style={styles.confirmationText}>
        {installmentText(locale, "supervisor.hint")}
      </Text>
      <Text style={styles.confirmationText} testID="installment-supervisor-details">
        {detail}
      </Text>
      {dialog.kind === "closed" ? (
        state.access.canCancel ? (
          <View style={styles.confirmationActions}>
            <PosButton
              disabled={state.busy}
              fill
              label={installmentText(locale, "supervisor.chargedAction")}
              onPress={() => presenter.openSupervisorResolve("charged")}
              sound="danger"
              testID="installment-supervisor-charged"
              tone="danger"
            />
            <PosButton
              disabled={state.busy}
              fill
              label={installmentText(locale, "supervisor.notChargedAction")}
              onPress={() => presenter.openSupervisorResolve("not-charged")}
              sound="danger"
              testID="installment-supervisor-not-charged"
              tone="danger"
            />
          </View>
        ) : (
          <Text style={styles.supervisorNotice} testID="installment-supervisor-needs-permission">
            {installmentText(locale, "supervisor.needsPermission")}
          </Text>
        )
      ) : (
        <View style={styles.confirmation} testID="installment-supervisor-dialog">
          <Text style={styles.actionDockTitle}>
            {installmentText(
              locale,
              dialog.decision === "charged"
                ? "supervisor.dialogCharged"
                : "supervisor.dialogNotCharged",
            )}
          </Text>
          <Text style={styles.confirmationText}>
            {installmentText(
              locale,
              dialog.decision === "charged"
                ? "supervisor.chargedWarning"
                : "supervisor.notChargedWarning",
            )}
          </Text>
          <PosKeyboardAwareTextInput
            accessibilityLabel={installmentText(locale, "supervisor.reasonAccessibility")}
            onChangeText={(value) => presenter.setSupervisorResolveReason(value)}
            placeholder={installmentText(locale, "supervisor.reasonPlaceholder")}
            placeholderTextColor={posColors.placeholder}
            style={styles.textInput}
            testID="installment-supervisor-reason"
            value={dialog.reason}
          />
          <PosKeyboardAwareTextInput
            accessibilityLabel={installmentText(locale, "supervisor.referenceAccessibility")}
            onChangeText={(value) => presenter.setSupervisorResolveReference(value)}
            placeholder={installmentText(locale, "supervisor.referencePlaceholder")}
            placeholderTextColor={posColors.placeholder}
            style={styles.textInput}
            testID="installment-supervisor-reference"
            value={dialog.reference}
          />
          <View style={styles.confirmationActions}>
            <PosButton
              disabled={state.busy}
              label={installmentText(locale, "action.back")}
              onPress={() => presenter.closeSupervisorResolve()}
              size="medium"
              testID="installment-supervisor-back"
              tone="quiet"
            />
            <PosButton
              disabled={state.busy}
              fill
              label={installmentText(locale, "supervisor.submit")}
              onPress={() => void presenter.confirmSupervisorResolve()}
              size="medium"
              sound="danger"
              testID="installment-supervisor-submit"
              tone="danger"
            />
          </View>
        </View>
      )}
    </View>
  );
}

/** ISO 时间转终端本地时区的「年-月-日 时:分」，无法解析时原样返回。 */
function localMinute(iso: string): string {
  const date = new Date(iso);
  if (!Number.isFinite(date.getTime())) return iso;
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
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
  usePosThemeVersion();
  const danger = kind !== "pickup";
  return (
    <View
      accessibilityLiveRegion="assertive"
      style={[styles.confirmation, !danger && styles.confirmationNeutral]}
      testID={`installment-confirm-${kind}`}
    >
      <Text style={styles.confirmationText}>
        {installmentText(locale, messageKey ?? `confirmation.${kind}`)}
      </Text>
      <View style={styles.confirmationActions}>
        <PosButton
          label={installmentText(locale, "action.back")}
          onPress={onCancel}
          size="medium"
          testID="installment-confirm-operation-cancel"
          tone="quiet"
        />
        {/* 橙色主按钮每屏只留给底部主操作；这里的确认键用深色（取货）或危险色（取消、作废）。 */}
        <PosButton
          fill
          label={installmentText(locale, "action.confirm")}
          onPress={onConfirm}
          size="medium"
          sound={danger ? "danger" : "tap"}
          testID="installment-confirm-operation-submit"
          tone={danger ? "danger" : "inverse"}
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
  usePosThemeVersion();
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

/** 一行「标签 值」小字，放不下自动换行；每个值单独成文本，便于选中复制。 */
function MetaRow({ children }: Readonly<{ children: ReactNode }>) {
  usePosThemeVersion();
  return <View style={styles.metaRow}>{children}</View>;
}

function MetaPair({
  label,
  value,
}: Readonly<{ label: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.metaPair}>
      <Text style={styles.metaLabel}>{label}</Text>
      <Text selectable style={styles.metaValue}>
        {value}
      </Text>
    </View>
  );
}

function Section({
  children,
  title,
}: Readonly<{ children: ReactNode; title: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.section}>
      <Text style={styles.sectionTitle}>{title}</Text>
      {children}
    </View>
  );
}

function Metric({
  label,
  tone = "plain",
  value,
}: Readonly<{
  label: string;
  /** paid：已付金额用绿色；balance：余额卡加粗描边。 */
  tone?: "plain" | "paid" | "balance";
  value: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.metric, tone === "balance" && styles.metricBalance]}>
      <Text numberOfLines={1} style={styles.metricLabel}>
        {label}
      </Text>
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.7}
        numberOfLines={1}
        style={[styles.metricValue, tone === "paid" && styles.metricValuePaid]}
      >
        {value}
      </Text>
    </View>
  );
}

function StatusPill({
  locale,
  status,
}: Readonly<{ locale: InstallmentLocale; status: InstallmentStatus }>) {
  usePosThemeVersion();
  return (
    <PosPill
      label={statusLabel(status, locale)}
      style={styles.statusPill}
      tone={statusPillTone(status)}
    />
  );
}

/** 进行中=提醒黄，已付清=绿，已取货=蓝，已取消=中性灰。 */
function statusPillTone(status: InstallmentStatus): PosPillTone {
  if (status === "Active") return "warning";
  if (status === "PaidOff") return "good";
  if (status === "PickedUp") return "info";
  return "neutral";
}

function StatusBanner({
  locale,
  statusCode,
}: Readonly<{
  locale: InstallmentLocale;
  statusCode: InstallmentStatusCode;
}>) {
  usePosThemeVersion();
  const danger = [
    "action-failed",
    "authorization-declined",
    "cancel-refund-method-unsupported",
    "cancel-refund-mode-unsupported",
    "claim-busy",
    "claim-review-required",
    "conflict",
    "details-failed",
    "history-failed",
    "invalid-create",
    "invalid-date-filter",
    "invalid-repayment",
    "online-required",
    "payment-method-unsupported",
    "payment-recovery-required",
    "permission-required",
    "service-unavailable",
    "supervisor-reason-required",
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
  usePosThemeVersion();
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
  usePosThemeVersion();
  return (
    <View
      accessibilityLiveRegion="polite"
      style={styles.centeredState}
      testID={testID}
    >
      {loading ? <ActivityIndicator color={posColors.accent} size="large" /> : null}
      <Text style={styles.fullStateText}>{message}</Text>
      {actionLabel && onAction ? (
        <PosButton
          icon="refresh"
          label={actionLabel}
          onPress={onAction}
          size="medium"
          testID={`${testID}-action`}
        />
      ) : null}
    </View>
  );
}

export function InstallmentsUnavailableScreen({
  onBack,
}: Readonly<{ onBack(): void }>) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveInstallmentLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  return (
    <PosScreenFrame
      active="installments"
      header={<PosPageHeader title={installmentText(locale, "title")} />}
      testID="installments-runtime-unavailable"
    >
      <View style={styles.fullState}>
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.mutedInk}
          name="alert-circle-outline"
          size={44}
        />
        <Text style={styles.fullStateTitle}>
          {installmentText(locale, "unavailable.title")}
        </Text>
        <Text style={styles.fullStateText}>
          {installmentText(locale, "unavailable.subtitle")}
        </Text>
        <PosButton
          icon="arrow-left"
          label={installmentText(locale, "action.backToSales")}
          onPress={onBack}
          size="large"
          sound="navigate"
          style={styles.fullStateAction}
          testID="installments-unavailable-back"
          tone="primary"
        />
      </View>
    </PosScreenFrame>
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

const styles = createPosStyles((colors) => ({
  // —— 页头里的联网状态 ——
  connectionPill: {
    minHeight: 34,
    paddingHorizontal: 12,
    borderRadius: 17,
    flexDirection: "row",
    alignItems: "center",
    gap: 7,
  },
  connectionPillOnline: {
    backgroundColor: colors.greenSoft,
  },
  connectionPillOffline: {
    backgroundColor: colors.surfaceSunken,
  },
  connectionDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
  },
  connectionDotOnline: {
    backgroundColor: colors.green,
  },
  connectionDotOffline: {
    backgroundColor: colors.mutedInk,
  },
  connectionText: {
    fontSize: 14,
    fontWeight: "800",
  },
  connectionTextOnline: {
    color: colors.green,
  },
  connectionTextOffline: {
    color: colors.mutedInk,
  },

  // —— 骨架：筛选两行在上，下面左列表 + 右详情 ——
  workspace: {
    flex: 1,
    minHeight: 0,
  },
  panes: {
    flex: 1,
    minHeight: 0,
    flexDirection: "row",
    alignItems: "stretch",
  },
  paneFill: {
    flex: 1,
  },

  // —— 筛选区 ——
  filters: {
    gap: 10,
  },
  searchKeyboardScroll: {
    flexGrow: 0,
    flexShrink: 0,
  },
  filterRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  filterRowCompact: {
    flexWrap: "wrap",
  },
  searchField: {
    width: 300,
    minHeight: posTouch.medium,
    paddingHorizontal: 12,
    borderColor: colors.borderStrong,
    borderWidth: 1.5,
    borderRadius: posRadius.medium,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  searchFieldCompact: {
    width: "auto",
    flex: 1,
    minWidth: 220,
  },
  searchInput: {
    flex: 1,
    minWidth: 0,
    minHeight: posTouch.medium - 3,
    paddingVertical: 0,
    color: colors.ink,
    fontSize: 16,
  },
  segmentedFill: {
    flex: 1,
    minWidth: 0,
  },
  segmentedFullRow: {
    width: "100%",
  },
  scopeSegmented: {
    width: 220,
  },
  filterLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  customDateRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 10,
  },

  // —— 左列表 ——
  historyPane: {
    minWidth: 0,
    minHeight: 0,
    gap: 8,
  },
  panelHeader: {
    minHeight: 32,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  panelTitle: {
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
  },
  countPill: {
    alignSelf: "center",
  },
  orderListScroll: {
    flex: 1,
    minHeight: 0,
  },
  orderList: {
    gap: 8,
    paddingBottom: 4,
  },
  emptyList: {
    flexGrow: 1,
  },
  orderRow: {
    minHeight: 88,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderColor: colors.border,
    borderWidth: 1.5,
    borderRadius: posRadius.large,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 10,
  },
  orderRowSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
    borderWidth: 2,
  },
  orderIdentity: {
    flex: 1,
    minWidth: 0,
  },
  orderTitleLine: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  orderNumber: {
    flexShrink: 1,
    color: colors.ink,
    fontSize: 16.5,
    fontWeight: "800",
  },
  orderCustomer: {
    color: colors.mutedInk,
    fontSize: 14,
    marginTop: 4,
  },
  orderMeta: {
    color: colors.mutedInk,
    fontSize: 12.5,
    marginTop: 3,
  },
  orderAmounts: {
    alignItems: "flex-end",
  },
  balanceAmount: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  balanceLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    marginTop: 2,
  },
  statusPill: {
    alignSelf: "center",
    flexShrink: 0,
  },

  // —— 右详情 ——
  detailsPane: {
    flex: 1,
    minWidth: 0,
    minHeight: 0,
    overflow: "hidden",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderWidth: 1,
    borderRadius: posRadius.large,
  },
  compactDetailsHeader: {
    minHeight: 72,
    paddingHorizontal: 10,
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  compactDetailsTitle: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "800",
  },
  detailsScroll: {
    flex: 1,
  },
  detailsContent: {
    flexGrow: 1,
    padding: 14,
    gap: 10,
  },
  detailHeading: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "flex-start",
    gap: 12,
  },
  detailIdentity: {
    flex: 1,
    minWidth: 0,
  },
  detailTitleLine: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  detailNumber: {
    flexShrink: 1,
    color: colors.ink,
    fontSize: 21,
    fontWeight: "900",
  },
  detailCustomer: {
    color: colors.mutedInk,
    fontSize: 14.5,
    marginTop: 3,
    marginBottom: 3,
  },
  reprintFeedback: {
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  reprintSuccess: {
    color: colors.green,
  },
  reprintFailure: {
    color: colors.red,
  },
  metrics: {
    flexDirection: "row",
    gap: 10,
  },
  metric: {
    flex: 1,
    minWidth: 0,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderColor: colors.border,
    borderWidth: 1,
    borderRadius: posRadius.medium,
    backgroundColor: colors.surface,
  },
  // 余额是这张单最要紧的数字：加粗描边，和另外三张卡区分开。
  metricBalance: {
    borderColor: colors.ink,
    borderWidth: 2,
  },
  metricLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "700",
  },
  metricValue: {
    color: colors.ink,
    fontSize: 25,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
    marginTop: 2,
  },
  metricValuePaid: {
    color: colors.green,
  },
  section: {
    gap: 2,
  },
  sectionTitle: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
    letterSpacing: 0.4,
    marginTop: 4,
  },
  detailRecord: {
    minHeight: 52,
    paddingVertical: 8,
    borderBottomColor: colors.border,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  recordCopy: {
    flex: 1,
    minWidth: 0,
  },
  recordTitle: {
    color: colors.ink,
    fontSize: 15.5,
    fontWeight: "800",
  },
  recordQuantity: {
    color: colors.mutedInk,
    fontSize: 14.5,
    fontVariant: ["tabular-nums"],
  },
  recordAmount: {
    minWidth: 84,
    color: colors.ink,
    fontSize: 16,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
    textAlign: "right",
  },
  metaRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    columnGap: 14,
    rowGap: 2,
    marginTop: 2,
  },
  metaPair: {
    flexDirection: "row",
    alignItems: "baseline",
    gap: 5,
  },
  metaLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
  },
  metaValue: {
    color: colors.ink,
    fontSize: 13,
    fontWeight: "700",
  },
  noteText: {
    color: colors.ink,
    fontSize: 15,
    lineHeight: 21,
  },
  completedNote: {
    backgroundColor: colors.greenSoft,
    borderLeftColor: colors.green,
    borderLeftWidth: 4,
    borderRadius: posRadius.small,
    padding: 12,
    gap: 4,
  },
  cancelledNote: {
    backgroundColor: colors.redSoft,
    borderLeftColor: colors.red,
    borderLeftWidth: 4,
    borderRadius: posRadius.small,
    padding: 12,
    gap: 4,
  },
  completedTitle: {
    color: colors.ink,
    fontSize: 15.5,
    fontWeight: "800",
  },
  emptyHint: {
    color: colors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    paddingVertical: 8,
  },

  // —— 动作区：表单在滚动区末尾，按钮栏固定在底部 ——
  actionForms: {
    marginTop: "auto",
    gap: 10,
  },
  pickupPanel: {
    gap: 8,
    paddingTop: 10,
    borderTopColor: colors.border,
    borderTopWidth: 1,
  },
  actionDock: {
    padding: 12,
    gap: 10,
    borderTopColor: colors.border,
    borderTopWidth: 1,
    backgroundColor: colors.surface,
  },
  grow: {
    flex: 1,
  },
  actionDockTitle: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "800",
  },
  actionDockButtons: {
    flexDirection: "row",
    alignItems: "stretch",
    gap: 12,
  },
  crossDeviceNotice: {
    backgroundColor: colors.blueSoft,
    borderLeftColor: colors.blue,
    borderLeftWidth: 4,
    borderRadius: posRadius.small,
    color: colors.ink,
    fontSize: 14,
    lineHeight: 20,
    padding: 10,
  },
  blockedText: {
    color: colors.mutedInk,
    fontSize: 13.5,
    lineHeight: 19,
    fontWeight: "700",
  },
  moreMenu: {
    flexDirection: "row",
    gap: 12,
  },
  dangerPanel: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
    borderWidth: 1,
    borderRadius: posRadius.large,
    padding: 12,
    gap: 10,
  },
  dangerPanelHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 10,
  },
  fieldLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  refundModeGroup: {
    gap: 8,
  },
  refundModeOptions: {
    flexDirection: "row",
    gap: 10,
  },
  refundModeHint: {
    color: colors.ink,
    fontSize: 13.5,
    lineHeight: 19,
  },
  choice: {
    flex: 1,
    minHeight: posTouch.medium,
    paddingHorizontal: 14,
    alignItems: "center",
    justifyContent: "center",
    borderColor: colors.borderStrong,
    borderWidth: 1.5,
    borderRadius: posRadius.medium,
    backgroundColor: colors.surface,
  },
  choiceSelected: {
    backgroundColor: colors.inverseSurface,
    borderColor: colors.inverseSurface,
  },
  choiceDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  choiceText: {
    color: colors.ink,
    fontSize: 15.5,
    fontWeight: "800",
    textAlign: "center",
  },
  choiceTextSelected: {
    color: colors.onInverse,
  },
  choiceTextDisabled: {
    color: colors.disabledInk,
  },
  textInput: {
    minHeight: posTouch.medium,
    paddingHorizontal: 14,
    borderColor: colors.borderStrong,
    borderWidth: 1.5,
    borderRadius: posRadius.medium,
    color: colors.ink,
    backgroundColor: colors.surface,
    fontSize: 16,
  },
  confirmation: {
    borderTopColor: colors.redBorder,
    borderTopWidth: 1,
    paddingTop: 10,
    gap: 10,
  },
  // 取货确认条放在底部按钮栏里，不属于危险操作，用普通提示底色。
  confirmationNeutral: {
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderTopColor: colors.border,
    borderWidth: 1,
    borderRadius: posRadius.medium,
    padding: 12,
  },
  confirmationText: {
    color: colors.ink,
    fontSize: 14.5,
    fontWeight: "700",
    lineHeight: 21,
  },
  confirmationActions: {
    flexDirection: "row",
    gap: 10,
  },
  supervisorNotice: {
    color: colors.ink,
    fontSize: 14.5,
    fontWeight: "700",
    lineHeight: 21,
  },

  // —— 横幅与整页状态 ——
  noticeBanner: {
    borderLeftWidth: 4,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  noticeDanger: {
    backgroundColor: colors.redSoft,
    borderLeftColor: colors.red,
  },
  noticeSuccess: {
    backgroundColor: colors.greenSoft,
    borderLeftColor: colors.green,
  },
  noticeText: {
    color: colors.ink,
    fontSize: 14.5,
    fontWeight: "700",
    lineHeight: 20,
  },
  recoveryRow: {
    minHeight: 72,
    paddingHorizontal: 14,
    paddingVertical: 8,
    flexDirection: "row",
    justifyContent: "flex-end",
    alignItems: "center",
    gap: 12,
    backgroundColor: colors.redSoft,
    borderBottomColor: colors.redBorder,
    borderBottomWidth: 1,
  },
  recoveryText: {
    flex: 1,
    color: colors.ink,
    fontSize: 14.5,
    fontWeight: "700",
    lineHeight: 20,
  },
  fullState: {
    flex: 1,
    alignSelf: "center",
    justifyContent: "center",
    alignItems: "center",
    maxWidth: 620,
    padding: 28,
    gap: 14,
  },
  fullStateTitle: {
    color: colors.ink,
    fontSize: 23,
    fontWeight: "900",
    textAlign: "center",
  },
  fullStateText: {
    color: colors.mutedInk,
    fontSize: 15,
    lineHeight: 22,
    textAlign: "center",
  },
  fullStateAction: {
    alignSelf: "stretch",
    minWidth: 280,
  },
  centeredState: {
    flex: 1,
    minHeight: 150,
    justifyContent: "center",
    alignItems: "center",
    padding: 20,
    gap: 12,
  },
  pressed: {
    opacity: 0.72,
  },
}));
