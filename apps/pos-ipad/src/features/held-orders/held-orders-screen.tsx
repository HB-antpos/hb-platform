import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Animated,
  AppState,
  FlatList,
  PanResponder,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from "react-native";

import {
  heldOrdersText,
  resolveHeldOrdersLocale,
  type HeldOrdersCopyKey,
} from "./held-orders-copy";
import {
  HeldOrdersPresenter,
  type HeldOrderViewStatus,
} from "./held-orders-presenter";

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPanResponderView } from "@/ui/controls/pos-pan-responder-view";
import { PosPill, type PosPillTone } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSegmented } from "@/ui/controls/pos-segmented";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

export const HELD_ORDERS_MIN_TOUCH_TARGET = 44;
export const HELD_ORDERS_AUTO_REFRESH_INTERVAL_MS = 10_000;
const HELD_ORDERS_DELETE_ACTION_WIDTH = 96;
const HELD_ORDERS_SWIPE_ACTIVATION_DISTANCE = 8;
const HELD_ORDERS_SWIPE_OPEN_DISTANCE = 44;

/** 状态标签的色调：只存色调名，颜色在渲染时由 PosPill 按当前主题取。 */
const HELD_ORDER_STATUS_TONE: Record<HeldOrderViewStatus, PosPillTone> = {
  "local-pending": "good",
  "claiming-here": "warning",
  "local-pending-publish": "neutral",
  "published-shareable": "info",
  "remote-pending": "info",
  blocked: "danger",
};

type HeldOrdersScreenProps = Readonly<{
  presenter: HeldOrdersPresenter;
  onBack?(): void;
}>;

export function HeldOrdersScreen({
  presenter,
  onBack,
}: HeldOrdersScreenProps) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { i18n } = useTranslation();
  const locale = resolveHeldOrdersLocale(i18n.resolvedLanguage ?? i18n.language);
  const dateFilter = state.dateFilter ?? "today";
  const sourceTab = state.sourceTab ?? "local";
  const shareBusyHoldIds = state.shareBusyHoldIds ?? [];
  const t = (
    key: HeldOrdersCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => heldOrdersText(locale, key, values);

  useEffect(() => {
    void presenter.refresh();
    presenter.startAutoRefresh(HELD_ORDERS_AUTO_REFRESH_INTERVAL_MS);
    const subscription = AppState.addEventListener("change", (next) => {
      if (next === "active") {
        presenter.startAutoRefresh(HELD_ORDERS_AUTO_REFRESH_INTERVAL_MS);
      } else {
        presenter.stopAutoRefresh();
      }
    });
    return () => {
      subscription.remove();
      presenter.stopAutoRefresh();
    };
  }, [presenter]);

  const [forceReleaseFor, setForceReleaseFor] = useState<string | null>(null);
  const [forceReleaseReason, setForceReleaseReason] = useState("");
  const [deleteFor, setDeleteFor] = useState<string | null>(null);
  const [revealedDeleteFor, setRevealedDeleteFor] = useState<string | null>(
    null,
  );

  const returnToSalesAfterRestore = async (
    action: () => ReturnType<HeldOrdersPresenter["recall"]>,
  ) => {
    const result = await action();
    if (
      result.ok &&
      (result.code === "recalled" || result.code === "recovered")
    ) {
      onBack?.();
    }
  };

  const showsOtherDevices = state.sharedEnabled && sourceTab === "other";
  const listVisible =
    state.kind === "ready" ||
    (state.kind === "loading" && state.rows.length > 0);

  return (
    <PosScreenFrame
      active="held-orders"
      header={
        <PosPageHeader
          actions={
            <>
              <PosButton
                disabled={state.busy || state.kind === "loading"}
                icon="refresh"
                label={t("action.refresh")}
                onPress={() => void presenter.refresh()}
                size="medium"
                testID="held-orders-refresh"
                tone="quiet"
              />
              <PosButton
                disabled={state.busy}
                icon="pause-circle-outline"
                label={t("action.hold")}
                onPress={() => void presenter.hold()}
                size="medium"
                testID="held-orders-hold"
              />
            </>
          }
          backLabel={t("action.back")}
          backTestID="held-orders-back"
          onBack={onBack}
          subtitle={t(state.sharedEnabled ? "subtitle.shared" : "subtitle")}
          title={t("title")}
        />
      }
      testID="held-orders-screen"
    >
      <View style={styles.workspace}>
        {/* 列表标题行：左边标题与数量，右边两组大分段筛选，不单独占一栏。 */}
        <View style={styles.listHeader}>
          <Text style={styles.listTitle}>{t("list.title")}</Text>
          {listVisible ? (
            <PosPill
              label={t("list.count", { count: state.rows.length })}
              style={styles.inlinePill}
            />
          ) : null}
          {state.busy || state.kind === "loading" ? (
            <ActivityIndicator
              color={posColors.accent}
              testID="held-orders-loading-indicator"
            />
          ) : null}
          {sourceTab === "other" && state.remoteRefreshing ? (
            <ActivityIndicator
              color={posColors.accent}
              testID="held-orders-remote-loading-indicator"
            />
          ) : null}
          <View style={styles.listHeaderSpacer} />
          <PosSegmented
            onChange={(next) => presenter.setDateFilter(next)}
            options={[
              {
                label: t("filter.today"),
                testID: "held-orders-filter-today",
                value: "today",
              },
              {
                label: t("filter.all"),
                testID: "held-orders-filter-all",
                value: "all",
              },
            ]}
            style={styles.dateSegmented}
            value={dateFilter}
          />
          {/* 「本机 / 非本机」只在开启共享挂单时出现。 */}
          {state.sharedEnabled ? (
            <PosSegmented
              onChange={(next) => presenter.setSourceTab(next)}
              options={[
                {
                  label: t("filter.local"),
                  testID: "held-orders-source-local",
                  value: "local",
                },
                {
                  label: t("filter.other"),
                  testID: "held-orders-source-other",
                  value: "other",
                },
              ]}
              style={styles.sourceSegmented}
              value={sourceTab}
            />
          ) : null}
        </View>

        {sourceTab === "other" && state.refreshError ? (
          <View
            style={[styles.banner, styles.bannerInfo]}
            testID="held-orders-refresh-error"
          >
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.blue}
              name="cloud-off-outline"
              size={24}
            />
            <Text style={[styles.bannerText, styles.bannerTextInfo]}>
              {t("error.shared-sync")}
            </Text>
          </View>
        ) : null}

        {state.lastAction ? (
          <View
            style={[
              styles.banner,
              state.lastAction.ok ? styles.bannerSuccess : styles.bannerDanger,
            ]}
          >
            <MaterialCommunityIcons
              accessible={false}
              color={state.lastAction.ok ? posColors.green : posColors.red}
              name={
                state.lastAction.ok
                  ? "check-circle-outline"
                  : "alert-circle-outline"
              }
              size={24}
            />
            <Text
              style={[
                styles.bannerText,
                state.lastAction.ok
                  ? styles.bannerTextSuccess
                  : styles.bannerTextDanger,
              ]}
              testID="held-orders-result"
            >
              {t(`result.${state.lastAction.code}`)}
            </Text>
          </View>
        ) : null}

        {state.kind === "loading" && !state.rows.length ? (
          <CenteredState message={t("loading")} testID="held-orders-loading" />
        ) : null}
        {state.kind === "unauthorized" ? (
          <CenteredState
            hint={t("unauthorized.hint")}
            icon="lock-alert-outline"
            message={t("unauthorized.title")}
            testID="held-orders-unauthorized"
          />
        ) : null}
        {state.kind === "failed" ? (
          <CenteredState
            actionLabel={t("action.refresh")}
            hint={t("failed.hint")}
            icon="alert-circle-outline"
            message={t("failed.title")}
            onAction={() => void presenter.refresh()}
            testID="held-orders-failed"
          />
        ) : null}
        {forceReleaseFor ? (
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.forceReleasePanelContent}
            showsVerticalScrollIndicator={false}
            style={styles.forceReleasePanel}
            testID="held-orders-force-release-panel"
          >
            <Text style={styles.panelTitle}>{t("forceRelease.title")}</Text>
            <PosKeyboardAwareTextInput
              accessibilityLabel={t("forceRelease.reasonAccessibility")}
              onChangeText={setForceReleaseReason}
              placeholder={t("forceRelease.reasonPlaceholder")}
              placeholderTextColor={posColors.placeholder}
              style={styles.forceReleaseInput}
              testID="held-orders-force-release-reason"
              value={forceReleaseReason}
            />
            <View style={styles.panelActions}>
              <PosButton
                label={t("action.back")}
                onPress={() => {
                  setForceReleaseFor(null);
                  setForceReleaseReason("");
                }}
                testID="held-orders-force-release-cancel"
                tone="quiet"
              />
              <PosButton
                disabled={!forceReleaseReason.trim() || state.busy}
                label={t("forceRelease.confirm")}
                onPress={() => {
                  const holdId = forceReleaseFor;
                  const reason = forceReleaseReason.trim();
                  setForceReleaseFor(null);
                  setForceReleaseReason("");
                  void presenter.forceRelease(holdId, reason);
                }}
                testID="held-orders-force-release-confirm"
                tone="danger"
              />
            </View>
          </PosKeyboardAwareScrollView>
        ) : null}
        {deleteFor ? (
          <View style={styles.deletePanel} testID="held-orders-delete-panel">
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.red}
              name="trash-can-outline"
              size={30}
            />
            <View style={styles.deleteCopy}>
              <Text style={styles.panelTitle}>{t("delete.title")}</Text>
              <Text style={styles.deleteHint}>{t("delete.hint")}</Text>
            </View>
            <View style={styles.panelActions}>
              <PosButton
                label={t("delete.cancel")}
                onPress={() => setDeleteFor(null)}
                testID="held-orders-delete-cancel"
                tone="quiet"
              />
              <PosButton
                disabled={state.busy}
                label={t("delete.confirm")}
                onPress={() => {
                  const holdId = deleteFor;
                  setDeleteFor(null);
                  void presenter.delete(holdId);
                }}
                testID="held-orders-delete-confirm"
                tone="danger"
              />
            </View>
          </View>
        ) : null}
        {state.kind === "ready" && !state.rows.length ? (
          <CenteredState
            hint={t(showsOtherDevices ? "empty.other.hint" : "empty.hint")}
            icon="clipboard-clock-outline"
            message={t(showsOtherDevices ? "empty.other.title" : "empty.title")}
            testID="held-orders-empty"
          />
        ) : null}
        {listVisible ? (
          <FlatList
            contentContainerStyle={styles.list}
            data={state.rows}
            keyExtractor={(item) => item.holdId}
            renderItem={({ item }) => (
              <HeldOrderRow
                busy={state.busy}
                deleteRevealed={revealedDeleteFor === item.holdId}
                forceReleaseSupported={presenter.supportsForceRelease()}
                locale={locale}
                localSourceSelected={sourceTab === "local"}
                onShare={() => void presenter.requestShare(item.holdId)}
                shareBusy={shareBusyHoldIds.includes(item.holdId)}
                onForceRelease={(holdId) => {
                  setRevealedDeleteFor(null);
                  setDeleteFor(null);
                  setForceReleaseFor(holdId);
                  setForceReleaseReason("");
                }}
                onDelete={(holdId) => {
                  setRevealedDeleteFor(null);
                  setForceReleaseFor(null);
                  setForceReleaseReason("");
                  setDeleteFor(holdId);
                }}
                onHideDelete={() =>
                  setRevealedDeleteFor((current) =>
                    current === item.holdId ? null : current,
                  )
                }
                onRecall={() =>
                  void returnToSalesAfterRestore(() =>
                    item.status === "published-shareable" ||
                    item.status === "local-pending-publish"
                      ? presenter.recallLocalShared(item.holdId)
                      : presenter.recall(item.holdId),
                  )
                }
                onRecover={() =>
                  void returnToSalesAfterRestore(() =>
                    presenter.recover(item.holdId),
                  )
                }
                onRelease={() => void presenter.release(item.holdId)}
                onRevealDelete={() => {
                  setDeleteFor(null);
                  setForceReleaseFor(null);
                  setForceReleaseReason("");
                  setRevealedDeleteFor(item.holdId);
                }}
                onTakeRemote={() =>
                  void returnToSalesAfterRestore(() =>
                    presenter.takeRemote(item.holdId),
                  )
                }
                pendingConfirmation={
                  deleteFor === item.holdId || forceReleaseFor === item.holdId
                }
                row={item}
              />
            )}
            // 没有挂单时列表收起，把空间让给居中的空状态。
            style={state.rows.length ? styles.listScroll : styles.listCollapsed}
            testID="held-orders-list"
          />
        ) : null}

        <View style={[styles.banner, styles.bannerInfo]}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.blue}
            name="information-outline"
            size={24}
          />
          <Text style={[styles.bannerText, styles.bannerTextInfo]}>
            {t(state.sharedEnabled ? "footer.shared" : "footer.local")}
          </Text>
        </View>
      </View>
    </PosScreenFrame>
  );
}

function HeldOrderRow({
  busy,
  deleteRevealed,
  forceReleaseSupported,
  locale,
  localSourceSelected,
  onShare,
  onForceRelease,
  onDelete,
  onHideDelete,
  onRecall,
  onRecover,
  onRelease,
  onRevealDelete,
  onTakeRemote,
  pendingConfirmation,
  row,
  shareBusy,
}: Readonly<{
  busy: boolean;
  deleteRevealed: boolean;
  forceReleaseSupported: boolean;
  locale: ReturnType<typeof resolveHeldOrdersLocale>;
  localSourceSelected: boolean;
  onShare(): void;
  onForceRelease(holdId: string): void;
  onDelete(holdId: string): void;
  onHideDelete(): void;
  onRecall(): void;
  onRecover(): void;
  onRelease(): void;
  onRevealDelete(): void;
  onTakeRemote(): void;
  /** 这一单正在等上方的删除 / 强制释放确认，行边框标红以便对上号。 */
  pendingConfirmation: boolean;
  row: import("./held-orders-presenter").HeldOrderViewRow;
  shareBusy: boolean;
}>) {
  usePosThemeVersion();
  const t = (
    key: HeldOrdersCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => heldOrdersText(locale, key, values);
  const claiming = row.status === "claiming-here";
  const remoteOnly = row.status === "remote-pending";
  const canDelete = row.local !== null && !claiming;
  const canRecall =
    row.status !== "blocked" || row.blockReason !== "LOCAL_DELETE_PENDING";
  const canShare =
    localSourceSelected &&
    row.local?.status === "Pending" &&
    row.isSyntheticSharedClaim !== true &&
    row.shareState === "NeedsEvaluation" &&
    row.shareRequestedAtIso === null;
  const itemCount = row.local?.itemCount ?? row.remote?.lineCount ?? 0;
  const amountCents = row.local?.actualAmountCents ?? row.remote?.actualCents ?? 0;
  const heldAtIso = row.local?.heldAtIso ?? row.remote?.heldAtIso ?? "";
  const statusKeyMap: Record<HeldOrderViewStatus, HeldOrdersCopyKey> = {
    "local-pending": "status.local-pending",
    "claiming-here": "status.claiming-here",
    "local-pending-publish": "status.local-pending-publish",
    "published-shareable": "status.published-shareable",
    "remote-pending": "status.remote-pending",
    blocked: "status.blocked",
  };
  const statusKey = statusKeyMap[row.status];
  // 本机挂单除了共享状态，还要让收银员一眼看出「这单能取回」。
  const showsRecallablePill =
    canRecall &&
    row.local !== null &&
    (row.status === "local-pending-publish" ||
      row.status === "published-shareable" ||
      row.status === "blocked");
  const remoteSource = row.remote
    ? t("remote.source", {
        device: row.remote.deviceCode,
        cashier: row.remote.cashierName,
      })
    : null;
  const meta = [
    row.local ? remoteSource : null,
    t(itemCount === 1 ? "list.itemsOne" : "list.items", { count: itemCount }),
    t("list.heldAt", { time: formatTime(heldAtIso, locale) }),
  ].filter((part): part is string => part !== null);
  const amountText = formatAud(amountCents, locale);
  const content = (
    <>
      <View style={styles.rowIdentity}>
        <View style={styles.rowTitleLine}>
          <Text numberOfLines={1} style={styles.sequence}>
            {row.local
              ? t("list.sequence", { sequence: row.local.localSequence })
              : (remoteSource ?? "—")}
          </Text>
          {showsRecallablePill ? (
            <PosPill
              label={t("status.local-pending")}
              style={styles.inlinePill}
              tone="good"
            />
          ) : null}
          <PosPill
            label={t(statusKey)}
            style={styles.inlinePill}
            tone={HELD_ORDER_STATUS_TONE[row.status]}
          />
        </View>
        <View style={styles.rowMetaLine}>
          <Text style={styles.rowMeta}>{meta.join(" · ")}</Text>
          {row.status === "blocked" ? (
            <Text
              style={styles.blockReason}
              testID={`held-order-blocked-reason-${row.holdId}`}
            >
              {t(blockReasonCopyKey(row.blockReason))}
            </Text>
          ) : null}
        </View>
      </View>
      <Text
        accessibilityLabel={`${t("list.amount")} ${amountText}`}
        style={styles.amount}
      >
        {amountText}
      </Text>
      <View style={styles.rowActions}>
        {canShare ? (
          <PosButton
            disabled={shareBusy || deleteRevealed}
            label={t("action.share")}
            onPress={onShare}
            style={styles.shareButton}
            testID={`held-order-share-${row.holdId}`}
            tone="quiet"
          />
        ) : null}
        {claiming ? (
          <PosButton
            disabled={busy}
            label={t("action.release")}
            onPress={onRelease}
            style={styles.sideActionButton}
            testID={`held-order-release-${row.holdId}`}
            tone="quiet"
          />
        ) : null}
        {/* 这一行的主按钮：正常单是橙色「取回销售 / 本机在线取单」，取单中的异常单是「恢复取单」。 */}
        {canRecall ? (
          <PosButton
            disabled={busy || deleteRevealed}
            icon={claiming ? "restore" : "cart-arrow-down"}
            label={t(
              claiming
                ? "action.recover"
                : remoteOnly
                  ? "action.take-remote"
                  : "action.recall",
            )}
            onPress={claiming ? onRecover : remoteOnly ? onTakeRemote : onRecall}
            style={styles.mainActionButton}
            testID={`held-order-action-${row.holdId}`}
            tone={claiming ? "secondary" : "primary"}
          />
        ) : null}
        {claiming && forceReleaseSupported ? (
          <PosButton
            disabled={busy}
            label={t("action.force-release")}
            onPress={() => onForceRelease(row.holdId)}
            style={styles.sideActionButton}
            testID={`held-order-force-release-${row.holdId}`}
            tone="danger"
          />
        ) : null}
        {/* 删除键直接摆在行尾，不必先左滑；点了先出确认面板。 */}
        {canDelete ? (
          <PosPressable
            accessibilityLabel={t("action.delete")}
            accessibilityRole="button"
            accessibilityState={{ disabled: busy }}
            disabled={busy}
            onPress={() => onDelete(row.holdId)}
            style={({ pressed }) => [
              styles.deleteIconButton,
              busy && styles.deleteIconButtonDisabled,
              pressed && !busy && styles.pressed,
            ]}
            testID={`held-order-delete-${row.holdId}`}
          >
            <MaterialCommunityIcons
              accessible={false}
              color={busy ? posColors.disabledInk : posColors.red}
              name="trash-can-outline"
              size={28}
            />
          </PosPressable>
        ) : null}
      </View>
    </>
  );
  const rowStyle = [
    styles.row,
    claiming && styles.rowClaiming,
    pendingConfirmation && styles.rowPendingConfirmation,
  ];

  if (!canDelete) {
    return (
      <View style={rowStyle} testID={`held-order-row-${row.holdId}`}>
        {content}
      </View>
    );
  }

  return (
    <SwipeDeleteRow
      busy={busy}
      deleteLabel={t("action.delete")}
      deleteTestID={`held-order-swipe-delete-${row.holdId}`}
      onDelete={() => onDelete(row.holdId)}
      onHide={onHideDelete}
      onReveal={onRevealDelete}
      open={deleteRevealed}
      rowStyle={rowStyle}
      rowTestID={`held-order-row-${row.holdId}`}
      swipeTestID={`held-order-swipe-${row.holdId}`}
    >
      {content}
    </SwipeDeleteRow>
  );
}

function SwipeDeleteRow({
  busy,
  children,
  deleteLabel,
  deleteTestID,
  onDelete,
  onHide,
  onReveal,
  open,
  rowStyle,
  rowTestID,
  swipeTestID,
}: Readonly<{
  busy: boolean;
  children: ReactNode;
  deleteLabel: string;
  deleteTestID: string;
  onDelete(): void;
  onHide(): void;
  onReveal(): void;
  open: boolean;
  rowStyle: StyleProp<ViewStyle>;
  rowTestID: string;
  swipeTestID: string;
}>) {
  usePosThemeVersion();
  const translateX = useRef(new Animated.Value(0)).current;
  const gestureStartX = useRef(0);
  const snapTo = useCallback(
    (toValue: number) => {
      Animated.timing(translateX, {
        duration: 140,
        toValue,
        useNativeDriver: true,
      }).start();
    },
    [translateX],
  );

  useEffect(() => {
    snapTo(open ? -HELD_ORDERS_DELETE_ACTION_WIDTH : 0);
  }, [open, snapTo]);

  useEffect(
    () => () => {
      translateX.stopAnimation();
    },
    [translateX],
  );

  const panResponder = useMemo(
    () =>
      PanResponder.create({
        // 仅横向手势接管 responder，避免左滑删除与列表纵向滚动互相抢占。
        onStartShouldSetPanResponder: () => false,
        onMoveShouldSetPanResponder: (_event, gesture) => {
          if (busy) return false;
          const horizontal = Math.abs(gesture.dx) > Math.abs(gesture.dy);
          if (!horizontal) return false;
          return open
            ? Math.abs(gesture.dx) >= HELD_ORDERS_SWIPE_ACTIVATION_DISTANCE
            : gesture.dx <= -HELD_ORDERS_SWIPE_ACTIVATION_DISTANCE;
        },
        onPanResponderGrant: () => {
          translateX.stopAnimation();
          gestureStartX.current = open
            ? -HELD_ORDERS_DELETE_ACTION_WIDTH
            : 0;
        },
        onPanResponderMove: (_event, gesture) => {
          const next = Math.max(
            -HELD_ORDERS_DELETE_ACTION_WIDTH,
            Math.min(0, gestureStartX.current + gesture.dx),
          );
          translateX.setValue(next);
        },
        onPanResponderRelease: (_event, gesture) => {
          const opens = open
            ? !(
                gesture.dx >= HELD_ORDERS_SWIPE_OPEN_DISTANCE ||
                gesture.vx > 0.35
              )
            : gesture.dx <= -HELD_ORDERS_SWIPE_OPEN_DISTANCE ||
              gesture.vx < -0.35;
          snapTo(opens ? -HELD_ORDERS_DELETE_ACTION_WIDTH : 0);
          if (opens) onReveal();
          else onHide();
        },
        onPanResponderTerminate: () => {
          snapTo(open ? -HELD_ORDERS_DELETE_ACTION_WIDTH : 0);
        },
        onPanResponderTerminationRequest: () => true,
        onShouldBlockNativeResponder: () => false,
      }),
    [busy, onHide, onReveal, open, snapTo, translateX],
  );

  return (
    <PosPanResponderView
      panHandlers={panResponder.panHandlers}
      style={styles.swipeShell}
      testID={swipeTestID}
    >
      <View style={styles.swipeDeleteTrack}>
        {open ? (
          <PosPressable
            accessibilityLabel={deleteLabel}
            accessibilityRole="button"
            accessibilityState={{ disabled: busy }}
            disabled={busy}
            onPress={() => {
              onHide();
              snapTo(0);
              onDelete();
            }}
            style={({ pressed }) => [
              styles.swipeDeleteButton,
              busy && styles.swipeDeleteButtonDisabled,
              pressed && !busy && styles.pressed,
            ]}
            testID={deleteTestID}
          >
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.red}
              name="trash-can-outline"
              size={26}
            />
            <Text style={styles.swipeDeleteText}>{deleteLabel}</Text>
          </PosPressable>
        ) : (
          <View
            accessibilityElementsHidden
            importantForAccessibility="no-hide-descendants"
            pointerEvents="none"
            style={styles.swipeDeleteButton}
          >
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.red}
              name="trash-can-outline"
              size={26}
            />
            <Text style={styles.swipeDeleteText}>{deleteLabel}</Text>
          </View>
        )}
      </View>
      <Animated.View
        style={[rowStyle, { transform: [{ translateX }] }]}
        testID={rowTestID}
      >
        {children}
      </Animated.View>
    </PosPanResponderView>
  );
}

function blockReasonCopyKey(reason: string | null): HeldOrdersCopyKey {
  switch (reason) {
    case "LEGACY_PAYLOAD_CORRUPTED":
      return "blocked.LEGACY_PAYLOAD_CORRUPTED";
    case "LEGACY_PAYLOAD_VERSION_UNSUPPORTED":
      return "blocked.LEGACY_PAYLOAD_VERSION_UNSUPPORTED";
    case "SHARED_CART_VERSION_UNSUPPORTED":
      return "blocked.SHARED_CART_VERSION_UNSUPPORTED";
    case "SHARED_CART_MODE_NOT_SALE":
      return "blocked.SHARED_CART_MODE_NOT_SALE";
    case "SHARED_CART_LINE_KIND_NOT_SALE":
      return "blocked.SHARED_CART_LINE_KIND_NOT_SALE";
    case "SHARED_CART_RETURN_ORIGINAL_NOT_EMPTY":
      return "blocked.SHARED_CART_RETURN_ORIGINAL_NOT_EMPTY";
    case "SHARED_CART_INVALID":
      return "blocked.SHARED_CART_INVALID";
    case "LOCAL_DELETE_PENDING":
      return "blocked.LOCAL_DELETE_PENDING";
    case "SHARED_HELD_ORDER_CANCELLED":
      return "blocked.SHARED_HELD_ORDER_CANCELLED";
    default:
      return "blocked.unknown";
  }
}

function CenteredState({
  actionLabel,
  hint,
  icon,
  message,
  onAction,
  testID,
}: Readonly<{
  actionLabel?: string;
  hint?: string;
  icon?: PosIconName;
  message: string;
  onAction?(): void;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.centered} testID={testID}>
      {icon ? (
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.mutedInk}
          name={icon}
          size={56}
        />
      ) : null}
      <Text style={styles.centeredTitle}>{message}</Text>
      {hint ? <Text style={styles.centeredHint}>{hint}</Text> : null}
      {actionLabel && onAction ? (
        <PosButton
          icon="refresh"
          label={actionLabel}
          onPress={onAction}
          style={styles.centeredAction}
          testID={`${testID}-action`}
        />
      ) : null}
    </View>
  );
}

function formatAud(cents: number, locale: ReturnType<typeof resolveHeldOrdersLocale>): string {
  return new Intl.NumberFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    style: "currency",
    currency: "AUD",
  }).format(cents / 100);
}

function formatTime(iso: string, locale: ReturnType<typeof resolveHeldOrdersLocale>): string {
  const timestamp = Date.parse(iso);
  if (!Number.isFinite(timestamp)) return "—";
  return new Intl.DateTimeFormat(locale === "zh" ? "zh-CN" : "en-AU", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(timestamp));
}

const styles = createPosStyles((colors) => ({
  workspace: { flex: 1, gap: 10, minHeight: 0, padding: 12 },
  listHeader: { alignItems: "center", flexDirection: "row", gap: 10 },
  listHeaderSpacer: { flex: 1 },
  listTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  inlinePill: { alignSelf: "center" },
  dateSegmented: { width: 220 },
  sourceSegmented: { width: 280 },
  banner: { alignItems: "center", borderRadius: 10, borderWidth: 1.5, flexDirection: "row", gap: 10, paddingHorizontal: 14, paddingVertical: 12 },
  bannerInfo: { backgroundColor: colors.blueSoft, borderColor: colors.blueBorder },
  bannerSuccess: { backgroundColor: colors.greenSoft, borderColor: colors.greenBorder },
  bannerDanger: { backgroundColor: colors.redSoft, borderColor: colors.redBorder },
  bannerText: { flex: 1, fontSize: 14, fontWeight: "700", lineHeight: 20 },
  bannerTextInfo: { color: colors.blue },
  bannerTextSuccess: { color: colors.green },
  bannerTextDanger: { color: colors.red },
  panelTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  panelActions: { alignItems: "center", flexDirection: "row", gap: 10, justifyContent: "flex-end" },
  forceReleasePanel: { backgroundColor: colors.surface, borderColor: colors.red, borderRadius: 12, borderWidth: 1.5, flexGrow: 0 },
  forceReleasePanelContent: { gap: 12, padding: 14 },
  forceReleaseInput: { backgroundColor: colors.surface, borderColor: colors.borderStrong, borderRadius: 10, borderWidth: 1.5, color: colors.ink, fontSize: 17, fontWeight: "700", minHeight: 60, paddingHorizontal: 14, paddingVertical: 10 },
  deletePanel: { alignItems: "center", backgroundColor: colors.redSoft, borderColor: colors.red, borderRadius: 12, borderWidth: 1.5, flexDirection: "row", gap: 14, padding: 14 },
  deleteCopy: { flex: 1, gap: 4, minWidth: 0 },
  deleteHint: { color: colors.mutedInk, fontSize: 14, fontWeight: "600", lineHeight: 20 },
  listScroll: { flex: 1 },
  listCollapsed: { flexGrow: 0 },
  list: { gap: 8, paddingBottom: 4 },
  swipeShell: { borderRadius: 12, overflow: "hidden", position: "relative" },
  swipeDeleteTrack: { backgroundColor: colors.redSoft, borderBottomRightRadius: 12, borderColor: colors.red, borderTopRightRadius: 12, borderWidth: 1.5, bottom: 0, position: "absolute", right: 0, top: 0, width: HELD_ORDERS_DELETE_ACTION_WIDTH },
  swipeDeleteButton: { alignItems: "center", flex: 1, gap: 4, justifyContent: "center", minHeight: HELD_ORDERS_MIN_TOUCH_TARGET, width: "100%" },
  swipeDeleteButtonDisabled: { opacity: 0.45 },
  swipeDeleteText: { color: colors.red, fontSize: 15, fontWeight: "900" },
  row: { alignItems: "center", backgroundColor: colors.surface, borderColor: colors.border, borderRadius: 12, borderWidth: 1.5, flexDirection: "row", gap: 14, minHeight: 100, paddingHorizontal: 14, paddingVertical: 12 },
  // 正在本机取单 / 恢复的异常单整行黄底。
  rowClaiming: { backgroundColor: colors.yellowSoft, borderColor: colors.yellowBorder },
  rowPendingConfirmation: { borderColor: colors.red },
  rowIdentity: { flex: 1, gap: 5, minWidth: 0 },
  rowTitleLine: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 8 },
  rowMetaLine: { alignItems: "center", columnGap: 8, flexDirection: "row", flexWrap: "wrap", rowGap: 2 },
  rowMeta: { color: colors.mutedInk, fontSize: 13.5, fontVariant: ["tabular-nums"], fontWeight: "600" },
  rowActions: { alignItems: "center", flexDirection: "row", flexShrink: 0, gap: 10 },
  sequence: { color: colors.ink, flexShrink: 1, fontSize: 19, fontWeight: "900" },
  blockReason: { color: colors.red, flexShrink: 1, fontSize: 13.5, fontWeight: "700" },
  amount: { color: colors.ink, flexShrink: 0, fontSize: 24, fontVariant: ["tabular-nums"], fontWeight: "900", minWidth: 120, textAlign: "right" },
  shareButton: { minWidth: 104 },
  sideActionButton: { minWidth: 130 },
  mainActionButton: { minWidth: 190 },
  deleteIconButton: { alignItems: "center", backgroundColor: colors.redSoft, borderColor: colors.red, borderRadius: 10, borderWidth: 1.5, height: 64, justifyContent: "center", width: 64 },
  deleteIconButtonDisabled: { backgroundColor: colors.disabledSurface, borderColor: colors.disabledBorder },
  pressed: { opacity: 0.72 },
  centered: { alignItems: "center", flex: 1, gap: 8, justifyContent: "center", padding: 32 },
  centeredTitle: { color: colors.ink, fontSize: 20, fontWeight: "900", textAlign: "center" },
  centeredHint: { color: colors.mutedInk, fontSize: 15, fontWeight: "600", lineHeight: 22, textAlign: "center" },
  centeredAction: { marginTop: 12, minWidth: 190 },
}));
