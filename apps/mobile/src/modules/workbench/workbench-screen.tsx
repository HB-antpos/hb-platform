import { useCallback, useMemo, useState, type ComponentProps } from "react";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter } from "expo-router";
import { useQueryClient } from "@tanstack/react-query";
import {
  Platform,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  View,
} from "react-native";
import {
  ActivityIndicator,
  Button,
  Divider,
  Surface,
  Text,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useAppNavigationAccess } from "@/modules/navigation/access-context";
import { TAB_PATHS } from "@/modules/navigation/default-route";
import { useAppNavigationStore } from "@/modules/navigation/store";
import {
  buildWorkbenchSections,
  type WorkbenchNavigationItem,
} from "@/modules/navigation/workbench";
import { useCartSummary } from "@/modules/shop/use-cart-summary";
import { useStores } from "@/modules/shop/use-stores";
import type { Store } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { useDeviceStore } from "@/store/device-store";

// 常用功能按顺序取当前账号可见的前 4 项；价格更新占原购物车的位置（购物车仍在「全部功能」里）。
const QUICK_ACTION_ROUTE_ORDER = [
  "product-query",
  "home",
  "price-updates",
  "warehouse",
  "attendance-personal",
  "orders",
  "domestic-purchase",
  "reports",
] as const;

type MaterialIconName = ComponentProps<typeof MaterialCommunityIcons>["name"];

function formatStoreLabel(
  storeName: string | null | undefined,
  storeCode: string | null | undefined,
  fallback: string
) {
  const name = storeName?.trim();
  const code = storeCode?.trim();
  if (name && code && name !== code) {
    return `${name} · ${code}`;
  }
  return name || code || fallback;
}

interface FunctionButtonProps {
  item: WorkbenchNavigationItem;
  label: string;
  pendingCount?: number;
  onPress: () => void;
  compact?: boolean;
}

function FunctionButton({
  item,
  label,
  pendingCount = 0,
  onPress,
  compact = false,
}: FunctionButtonProps) {
  const { t } = useAppTranslation("workbench");
  const visiblePendingCount = pendingCount > 99 ? "99+" : String(pendingCount);

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("accessibility.openFunction", { title: label })}
      onPress={onPress}
      style={({ pressed }) => [
        compact ? styles.quickAction : styles.functionRow,
        pressed ? styles.pressed : null,
      ]}
    >
      <View style={compact ? styles.quickIcon : styles.rowIcon}>
        <MaterialCommunityIcons
          name={item.icon as MaterialIconName}
          color={HB_COLORS.action}
          size={compact ? 23 : 21}
        />
        {/* 常用功能格子窄：角标挂在图标右上角，不挤占文字宽度（英文 Price updates 否则会折行）。 */}
        {compact && pendingCount > 0 ? (
          <View style={[styles.countBadge, styles.iconCornerBadge]}>
            <Text variant="labelSmall" style={styles.countBadgeText}>
              {visiblePendingCount}
            </Text>
          </View>
        ) : null}
      </View>
      <Text
        variant={compact ? "labelLarge" : "bodyMedium"}
        style={compact ? styles.quickLabel : styles.functionLabel}
      >
        {label}
      </Text>
      {!compact && pendingCount > 0 ? (
        <View style={styles.countBadge}>
          <Text variant="labelSmall" style={styles.countBadgeText}>
            {visiblePendingCount}
          </Text>
        </View>
      ) : null}
      {!compact ? (
        <MaterialCommunityIcons
          name="chevron-right"
          color={HB_COLORS.textSecondary}
          size={21}
        />
      ) : null}
    </Pressable>
  );
}

interface AccessStateProps {
  kind: "loading" | "error" | "empty";
  onRetry: () => void;
}

function AccessState({ kind, onRetry }: AccessStateProps) {
  const { t } = useAppTranslation("workbench");
  const isLoading = kind === "loading";
  const title = t(`states.${kind}Title`);
  const description = t(`states.${kind}Description`);

  return (
    <Surface style={styles.stateSurface} elevation={0}>
      {isLoading ? (
        <ActivityIndicator
          color={HB_COLORS.action}
          accessibilityLabel={title}
        />
      ) : (
        <MaterialCommunityIcons
          name={kind === "error" ? "alert-circle-outline" : "shield-lock-outline"}
          color={kind === "error" ? HB_COLORS.danger : HB_COLORS.textSecondary}
          size={30}
        />
      )}
      <Text variant="titleMedium" style={styles.stateTitle}>
        {title}
      </Text>
      <Text variant="bodyMedium" style={styles.stateDescription}>
        {description}
      </Text>
      {!isLoading ? (
        <Button
          icon="refresh"
          mode="contained"
          buttonColor={HB_COLORS.action}
          onPress={onRetry}
          style={styles.retryButton}
        >
          {t("actions.retry")}
        </Button>
      ) : null}
    </Surface>
  );
}

export function WorkbenchScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["workbench", "common"]);
  const {
    orderedVisibleRouteNames,
    navigationErrorMessage,
    navigationLoading,
    pendingProfileReviewCount,
    pendingPriceUpdateCount,
    isDeviceMode,
    isWarehouseStaffOnly,
  } = useAppNavigationAccess();
  // 工作台入口角标按路由名查表；未登记的路由不显示角标。
  const pendingCountByRouteName: Record<string, number> = {
    "employee-profile-review": pendingProfileReviewCount,
    "price-updates": pendingPriceUpdateCount,
  };
  const fetchMenu = useAppNavigationStore((state) => state.fetchMenu);
  const refreshCurrentUser = useAuthStore((state) => state.refreshCurrentUser);
  const queryClient = useQueryClient();
  const [refreshing, setRefreshing] = useState(false);
  const currentUser = useAuthStore((state) => state.user);
  const deviceSession = useDeviceStore((state) => state.session);
  const cartSummary = useCartStore((state) => state.cartSummary);
  const {
    stores,
    selectedStore,
    selectedStoreCode,
    selectStore,
    isLoading: storesLoading,
    isError: storesLoadFailed,
    isHydratingSelection,
  } = useStores();
  const cartSummaryQuery = useCartSummary(selectedStoreCode);
  const [storePickerVisible, setStorePickerVisible] = useState(false);

  const sections = useMemo(
    () =>
      navigationLoading || navigationErrorMessage
        ? []
        : buildWorkbenchSections(orderedVisibleRouteNames),
    [navigationErrorMessage, navigationLoading, orderedVisibleRouteNames]
  );
  const allItems = useMemo(
    () => sections.flatMap((section) => section.items),
    [sections]
  );
  const itemsByRoute = useMemo(
    () => new Map(allItems.map((item) => [item.routeName, item])),
    [allItems]
  );
  const quickActions = useMemo(
    () =>
      QUICK_ACTION_ROUTE_ORDER.map((routeName) => itemsByRoute.get(routeName))
        .filter((item): item is WorkbenchNavigationItem => Boolean(item))
        .slice(0, 4),
    [itemsByRoute]
  );

  const effectiveStoreCode = selectedStoreCode
    ?? (isDeviceMode ? deviceSession?.storeCode : null);
  const effectiveStoreName = selectedStore?.storeName
    ?? (isDeviceMode ? deviceSession?.storeName : null);
  const storeLabel = formatStoreLabel(
    effectiveStoreName,
    effectiveStoreCode,
    t("summary.noStore")
  );
  const cartMatchesStore = Boolean(
    effectiveStoreCode
    && cartSummary
    && cartSummary.storeCode === effectiveStoreCode
  );
  const queriedCart = cartSummaryQuery.data ?? null;
  const queriedCartMatchesStore = Boolean(
    effectiveStoreCode
    && selectedStoreCode === effectiveStoreCode
    && queriedCart
    && (!queriedCart.storeCode || queriedCart.storeCode === effectiveStoreCode)
  );
  const cartSummaryFailed = cartSummaryQuery.isError || cartSummaryQuery.isRefetchError;
  const scopedCart = cartSummaryFailed
    ? null
    : queriedCartMatchesStore
      ? queriedCart
      : cartMatchesStore
        ? cartSummary
        : null;
  const cartSkuCount = scopedCart
    ? scopedCart.totalSku ?? scopedCart.items.length ?? 0
    : !cartSummaryFailed && cartSummaryQuery.isSuccess
      ? 0
      : null;
  const cartSkuLabel = cartSkuCount == null
    ? "—"
    : t("summary.skuCount", { count: cartSkuCount });
  // 与 navigateTo 的守卫一致：只有菜单就绪且账号获授购物车时，指标才可点击。
  const canOpenCart = itemsByRoute.has("cart")
    && !navigationLoading
    && !navigationErrorMessage;
  const cartSummaryLoading = Boolean(
    effectiveStoreCode
    && !scopedCart
    && (cartSummaryQuery.isLoading || cartSummaryQuery.isFetching)
  );
  const storeSelectionLoading = storesLoading || isHydratingSelection;
  const canSwitchStore = !isDeviceMode
    && !storeSelectionLoading
    && !storesLoadFailed
    && stores.length > 1;
  const identityName = isDeviceMode
    ? deviceSession?.systemDeviceNumber?.trim()
      || deviceSession?.storeName?.trim()
      || t("session.device")
    : currentUser?.fullName?.trim()
      || currentUser?.username?.trim()
      || t("session.account");

  const navigateTo = useCallback(
    (routeName: string) => {
      const path = TAB_PATHS[routeName];
      if (
        !path
        || !itemsByRoute.has(routeName)
        || navigationLoading
        || navigationErrorMessage
      ) {
        return;
      }
      // 工作台功能是 Shell Stack 的二级页面，push 后才能使用原生手势逐级返回。
      router.push(path as Parameters<typeof router.push>[0]);
    },
    [itemsByRoute, navigationErrorMessage, navigationLoading, router]
  );

  const handleSelectStore = useCallback(
    async (store: Store | null) => {
      if (!store || !canSwitchStore) {
        setStorePickerVisible(false);
        return;
      }

      if (store.storeCode === selectedStoreCode) {
        setStorePickerVisible(false);
        return;
      }

      try {
        await selectStore(store);
        setStorePickerVisible(false);
      } catch (error) {
        // 保留弹层，避免持久化失败时向用户暗示切店已经完整成功。
        console.error("[workbench] failed to persist selected store", {
          error,
          storeCode: store.storeCode,
        });
      }
    },
    [canSwitchStore, selectStore, selectedStoreCode]
  );

  const retryMenu = useCallback(() => {
    void fetchMenu();
  }, [fetchMenu]);

  // 下拉刷新：后台改了角色权限或分店分配后，重新拉取当前账号权限、菜单和分店，无需重新登录。
  const handleRefresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await refreshCurrentUser().catch((error) => {
        console.warn("[workbench] failed to refresh current user", error);
        return false;
      });
      await Promise.all([
        fetchMenu({ background: true }),
        queryClient.invalidateQueries({ queryKey: ["userStores"] }),
      ]);
    } finally {
      setRefreshing(false);
    }
  }, [fetchMenu, queryClient, refreshCurrentUser]);

  const accessState = navigationLoading
    ? "loading"
    : navigationErrorMessage
      ? "error"
      : allItems.length === 0
        ? "empty"
        : null;

  return (
    <SafeAreaView
      style={styles.safeArea}
      edges={["top", "left", "right"]}
    >
      <ScrollView
        contentContainerStyle={styles.content}
        contentInsetAdjustmentBehavior="automatic"
        showsVerticalScrollIndicator={false}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void handleRefresh()} />}
      >
        <View style={styles.header}>
          <View style={styles.headingRow}>
            <View style={styles.headingCopy}>
              <Text variant="headlineSmall" style={styles.title}>
                {t("title")}
              </Text>
              <Text variant="titleMedium" style={styles.greeting}>
                {t("greeting", { name: identityName })}
              </Text>
            </View>
            <View style={styles.modeTag}>
              <MaterialCommunityIcons
                name={
                  isDeviceMode
                    ? "cellphone-link"
                    : isWarehouseStaffOnly
                      ? "warehouse"
                      : "account-outline"
                }
                color={HB_COLORS.action}
                size={15}
              />
              <Text variant="labelMedium" style={styles.modeText}>
                {t(
                  isDeviceMode
                    ? "session.device"
                    : isWarehouseStaffOnly
                      ? "session.warehouse"
                      : "session.account"
                )}
              </Text>
            </View>
          </View>
          <Text variant="bodyMedium" style={styles.subtitle}>
            {t("subtitle")}
          </Text>
        </View>

        <Surface style={styles.summarySurface} elevation={0}>
          <Pressable
            accessibilityRole={canSwitchStore ? "button" : undefined}
            accessibilityLabel={t(
              canSwitchStore
                ? "accessibility.changeStore"
                : "accessibility.currentStore",
              { store: storeLabel }
            )}
            accessibilityHint={canSwitchStore ? t("accessibility.changeStoreHint") : undefined}
            disabled={!canSwitchStore}
            onPress={() => setStorePickerVisible(true)}
            style={({ pressed }) => [
              styles.storeSummary,
              pressed && canSwitchStore ? styles.storeSummaryPressed : null,
            ]}
          >
            <MaterialCommunityIcons
              name="store-marker-outline"
              color={HB_COLORS.textSecondary}
              size={21}
            />
            <View style={styles.storeCopy}>
              <Text variant="labelMedium" style={styles.summaryLabel}>
                {t("summary.currentStore")}
              </Text>
              <Text variant="bodyMedium" style={styles.storeValue} numberOfLines={1}>
                {storeLabel}
              </Text>
            </View>
            {storeSelectionLoading && !isDeviceMode ? (
              <ActivityIndicator
                accessibilityLabel={t("accessibility.storesLoading")}
                color={HB_COLORS.action}
                size={18}
              />
            ) : canSwitchStore ? (
              <View style={styles.storeAction}>
                <Text variant="labelMedium" style={styles.storeActionText}>
                  {t("summary.switchStore")}
                </Text>
                <MaterialCommunityIcons
                  name="chevron-right"
                  color={HB_COLORS.action}
                  size={20}
                />
              </View>
            ) : null}
          </Pressable>
          <Divider />
          <View style={styles.metricsRow}>
            <View style={styles.metric}>
              <Text variant="labelMedium" style={styles.summaryLabel}>
                {t("summary.authorizedFunctions")}
              </Text>
              <Text variant="titleMedium" style={styles.metricValue}>
                {t("summary.functionCount", { count: allItems.length })}
              </Text>
            </View>
            <View style={styles.metricDivider} />
            {/* 购物车 SKU 指标可直接点进购物车；无购物车权限或菜单未就绪时保持纯展示，不暴露伪入口。 */}
            <Pressable
              accessibilityRole={canOpenCart ? "button" : undefined}
              accessibilityLabel={canOpenCart
                ? t("accessibility.openCart", { value: cartSkuLabel })
                : undefined}
              disabled={!canOpenCart}
              onPress={() => navigateTo("cart")}
              style={({ pressed }) => [
                styles.metric,
                styles.cartMetric,
                pressed && canOpenCart ? styles.storeSummaryPressed : null,
              ]}
            >
              <View style={styles.cartMetricCopy}>
                <Text variant="labelMedium" style={styles.summaryLabel}>
                  {t("summary.cartSku")}
                </Text>
                {cartSummaryLoading ? (
                  <ActivityIndicator
                    accessibilityLabel={t("summary.cartLoading")}
                    color={HB_COLORS.action}
                    size={20}
                    style={styles.metricActivity}
                  />
                ) : (
                  <Text variant="titleMedium" style={styles.metricValue}>
                    {cartSkuLabel}
                  </Text>
                )}
              </View>
              {canOpenCart ? (
                <MaterialCommunityIcons
                  name="chevron-right"
                  color={HB_COLORS.action}
                  size={20}
                />
              ) : null}
            </Pressable>
          </View>
        </Surface>

        {accessState ? (
          <AccessState
            kind={accessState}
            onRetry={retryMenu}
          />
        ) : (
          <>
            {quickActions.length > 0 ? (
              <View style={styles.sectionBlock}>
                <View style={styles.sectionHeading}>
                  <Text variant="titleMedium" style={styles.sectionTitle}>
                    {t("quickActions.title")}
                  </Text>
                  <Text variant="bodySmall" style={styles.sectionCaption}>
                    {t("quickActions.caption")}
                  </Text>
                </View>
                <Surface style={styles.quickSurface} elevation={0}>
                  {quickActions.map((item) => (
                    <FunctionButton
                      key={item.routeName}
                      item={item}
                      label={t(item.labelKey)}
                      pendingCount={pendingCountByRouteName[item.routeName] ?? 0}
                      compact
                      onPress={() => navigateTo(item.routeName)}
                    />
                  ))}
                </Surface>
              </View>
            ) : null}

            <View style={styles.sectionBlock}>
              <View style={styles.sectionHeading}>
                <Text variant="titleMedium" style={styles.sectionTitle}>
                  {t("allFunctions.title")}
                </Text>
                <Text variant="bodySmall" style={styles.sectionCaption}>
                  {t("allFunctions.caption")}
                </Text>
              </View>
              <View style={styles.functionSections}>
                <Surface style={styles.functionSurface} elevation={0}>
                  {sections.map((section, sectionIndex) => (
                    <View key={section.key}>
                      {sectionIndex > 0 ? <Divider style={styles.groupDivider} /> : null}
                      <View
                        accessible
                        accessibilityRole="header"
                        accessibilityLabel={t("accessibility.functionCount", {
                          title: t(section.titleKey),
                          count: section.items.length,
                        })}
                        style={styles.functionSectionHeader}
                      >
                        <Text variant="titleSmall" style={styles.functionSectionTitle}>
                          {t(section.titleKey)}
                        </Text>
                        <Text variant="labelMedium" style={styles.functionSectionCount}>
                          {t("allFunctions.sectionCount", { count: section.items.length })}
                        </Text>
                      </View>
                      <Divider />
                      {section.items.map((item, index) => (
                        <View key={item.routeName}>
                          {index > 0 ? <Divider style={styles.insetDivider} /> : null}
                          <FunctionButton
                            item={item}
                            label={t(item.labelKey)}
                            pendingCount={pendingCountByRouteName[item.routeName] ?? 0}
                            onPress={() => navigateTo(item.routeName)}
                          />
                        </View>
                      ))}
                    </View>
                  ))}
                </Surface>
              </View>
            </View>
          </>
        )}
      </ScrollView>
      <StorePickerModal
        visible={storePickerVisible}
        stores={stores}
        selectedStoreCode={selectedStoreCode}
        title={t("summary.storePickerTitle")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={handleSelectStore}
        renderStoreLabel={(store) => (
          <View style={styles.pickerStoreLabelWrap}>
            <Text variant="bodyMedium" style={styles.pickerStoreLabel} numberOfLines={1}>
              {formatStoreLabel(store.storeName, store.storeCode, store.storeCode)}
            </Text>
          </View>
        )}
      />
    </SafeAreaView>
  );
}

const INTERACTIVE_HEIGHT = Platform.OS === "android" ? 48 : 44;

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: HB_COLORS.background,
  },
  content: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    paddingBottom: HB_SPACING.lg,
    gap: HB_SPACING.md,
  },
  header: {
    gap: HB_SPACING.xs,
  },
  headingRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
  },
  headingCopy: {
    flex: 1,
    gap: 2,
  },
  title: {
    color: HB_COLORS.textPrimary,
    fontWeight: "700",
  },
  greeting: {
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  subtitle: {
    color: HB_COLORS.textSecondary,
  },
  modeTag: {
    minHeight: 28,
    maxWidth: "44%",
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: HB_SPACING.xxs,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: HB_SPACING.xxs,
    borderRadius: 999,
    backgroundColor: "#EAF2FF",
  },
  modeText: {
    flexShrink: 1,
    color: HB_COLORS.action,
    fontWeight: "700",
    textAlign: "center",
  },
  summarySurface: {
    overflow: "hidden",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surface,
  },
  storeSummary: {
    minHeight: 58,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  storeSummaryPressed: {
    backgroundColor: "#EAF2FF",
  },
  storeCopy: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  summaryLabel: {
    color: HB_COLORS.textSecondary,
    fontWeight: "600",
  },
  storeValue: {
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  storeAction: {
    flexShrink: 0,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xxs,
  },
  storeActionText: {
    color: HB_COLORS.action,
    fontWeight: "700",
  },
  metricsRow: {
    flexDirection: "row",
    alignItems: "stretch",
  },
  metric: {
    flex: 1,
    minWidth: 0,
    gap: HB_SPACING.xxs,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  metricDivider: {
    width: StyleSheet.hairlineWidth,
    backgroundColor: HB_COLORS.outline,
  },
  metricValue: {
    color: HB_COLORS.textPrimary,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  cartMetric: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
  cartMetricCopy: {
    flex: 1,
    minWidth: 0,
    gap: HB_SPACING.xxs,
  },
  metricActivity: {
    alignSelf: "flex-start",
  },
  pickerStoreLabelWrap: {
    minHeight: INTERACTIVE_HEIGHT,
    justifyContent: "center",
  },
  pickerStoreLabel: {
    color: HB_COLORS.textPrimary,
    fontWeight: "500",
  },
  sectionBlock: {
    gap: HB_SPACING.xs,
  },
  sectionHeading: {
    minHeight: 24,
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  sectionTitle: {
    color: HB_COLORS.textPrimary,
    fontWeight: "700",
  },
  sectionCaption: {
    flexShrink: 1,
    color: HB_COLORS.textSecondary,
    textAlign: "right",
  },
  quickSurface: {
    overflow: "hidden",
    flexDirection: "row",
    flexWrap: "wrap",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surface,
  },
  quickAction: {
    width: "50%",
    minHeight: 72,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderRightWidth: StyleSheet.hairlineWidth,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  iconCornerBadge: {
    position: "absolute",
    top: -8,
    right: -10,
    minWidth: 20,
    minHeight: 18,
    paddingHorizontal: 5,
    borderWidth: 1.5,
    borderColor: HB_COLORS.white,
  },
  quickIcon: {
    width: 34,
    height: 34,
    flexShrink: 0,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#EAF2FF",
  },
  quickLabel: {
    flex: 1,
    color: HB_COLORS.textPrimary,
    fontWeight: "600",
  },
  pressed: {
    backgroundColor: "#EAF2FF",
  },
  functionSections: {
    width: "100%",
  },
  functionSurface: {
    overflow: "hidden",
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surface,
  },
  functionSectionHeader: {
    minHeight: INTERACTIVE_HEIGHT,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.xs,
  },
  functionSectionTitle: {
    flexShrink: 1,
    color: HB_COLORS.textPrimary,
    fontWeight: "700",
  },
  functionSectionCount: {
    color: HB_COLORS.textSecondary,
  },
  functionRow: {
    minHeight: INTERACTIVE_HEIGHT,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.xs,
  },
  rowIcon: {
    width: 28,
    height: 28,
    flexShrink: 0,
    alignItems: "center",
    justifyContent: "center",
  },
  functionLabel: {
    flex: 1,
    color: HB_COLORS.textPrimary,
    fontWeight: "500",
  },
  insetDivider: {
    marginLeft: 56,
  },
  groupDivider: {
    height: 1,
    backgroundColor: HB_COLORS.outline,
  },
  countBadge: {
    minWidth: 24,
    minHeight: 20,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 6,
    borderRadius: 999,
    backgroundColor: "#FEE4E2",
  },
  countBadgeText: {
    color: HB_COLORS.danger,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  stateSurface: {
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.lg,
    paddingVertical: HB_SPACING.lg,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surface,
  },
  stateTitle: {
    color: HB_COLORS.textPrimary,
    fontWeight: "700",
    textAlign: "center",
  },
  stateDescription: {
    color: HB_COLORS.textSecondary,
    textAlign: "center",
  },
  retryButton: {
    minHeight: INTERACTIVE_HEIGHT,
    justifyContent: "center",
    marginTop: HB_SPACING.xxs,
  },
});
