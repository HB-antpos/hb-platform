// 店长「现金」入口：总览 / 按日 / 记录 三个页签。
// 进入先取 cash/context（可操作分店、权限能力、阈值、日结是否接入），按 capabilities 控制按钮，不自己推断角色。
import { useCallback, useMemo, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, SegmentedButtons, Text } from "react-native-paper";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { canViewStoreCash } from "./access";
import { DailyPanel } from "./DailyPanel";
import { resolveCashErrorMessage } from "./errors";
import { OverviewPanel } from "./OverviewPanel";
import { RecordsPanel } from "./RecordsPanel";
import { resolveActiveStore } from "./store-selection";
import { invalidateCashData, useCashContextQuery } from "./use-store-cash";
import { CashScreenFrame, cashUiStyles } from "./ui";

type CashTab = "overview" | "daily" | "records";

export function StoreCashScreen() {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const router = useRouter();
  const queryClient = useQueryClient();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const isReview = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const globalStoreCode = useCartStore((state) => state.selectedStore?.storeCode);
  const allowed = canViewStoreCash(isAuthenticated, hasPermission, isReview);
  const contextQuery = useCashContextQuery(allowed);
  const [tab, setTab] = useState<CashTab>("overview");
  const [requestedStoreCode, setRequestedStoreCode] = useState<string | null>(null);
  const [pickerVisible, setPickerVisible] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/workbench");
  }, [router]);

  const context = contextQuery.data;
  const store = useMemo(
    () => (context ? resolveActiveStore(context.stores, requestedStoreCode, globalStoreCode) : null),
    [context, globalStoreCode, requestedStoreCode],
  );
  const pickerStores = useMemo(
    () => (context?.stores ?? []).map((item) => ({ storeCode: item.storeCode, storeName: item.storeName || item.storeCode })),
    [context?.stores],
  );

  // 只有用户下拉时才显示刷新指示；切页签、后台重取不让页面顶部跳出转圈
  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await Promise.all([contextQuery.refetch(), invalidateCashData(queryClient)]);
    } finally {
      setRefreshing(false);
    }
  }, [contextQuery, queryClient]);

  const title = t("title");

  if (!allowed) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <StateMessage text={t("states.notAllowed")} />
      </CashScreenFrame>
    );
  }
  if (contextQuery.isPending) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <View style={styles.center}>
          <ActivityIndicator />
          <Text style={styles.muted}>{t("states.loading")}</Text>
        </View>
      </CashScreenFrame>
    );
  }
  if (contextQuery.isError || !context) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <StateMessage
          text={resolveCashErrorMessage(contextQuery.error, { t, language, fallbackKey: "storeCash:states.loadFailed" })}
          actionLabel={t("common:actions.retry")}
          onAction={() => void contextQuery.refetch()}
        />
      </CashScreenFrame>
    );
  }
  if (!store) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <StateMessage text={t("states.noStores")} />
      </CashScreenFrame>
    );
  }

  const canSwitchStore = context.stores.length > 1;

  return (
    <CashScreenFrame title={title} onBack={goBack}>
      <ScrollView
        style={cashUiStyles.scroll}
        contentContainerStyle={cashUiStyles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void refresh()} />}
      >
        {/* 可操作分店多于一个时门店卡片可点，打开分店列表切换 */}
        <Pressable
          accessibilityRole={canSwitchStore ? "button" : undefined}
          accessibilityLabel={canSwitchStore ? t("store.switchLabel", { store: store.storeName }) : undefined}
          disabled={!canSwitchStore}
          onPress={() => setPickerVisible(true)}
          style={styles.storeCard}
        >
          <MaterialCommunityIcons name="store-outline" size={26} color={HB_COLORS.action} />
          <View style={styles.storeInfo}>
            <Text style={styles.muted}>{t("store.current")}</Text>
            <Text variant="titleMedium" numberOfLines={1}>{store.storeName || store.storeCode}</Text>
          </View>
          {canSwitchStore ? (
            <View style={styles.switchStore}>
              <Text style={styles.switchStoreText}>{t("store.switch")}</Text>
              <MaterialCommunityIcons name="chevron-right" size={18} color={HB_COLORS.action} />
            </View>
          ) : null}
        </Pressable>

        <SegmentedButtons
          value={tab}
          onValueChange={(value) => setTab(value as CashTab)}
          density="small"
          buttons={[
            { value: "overview", label: t("tabs.overview"), labelStyle: styles.tabLabel },
            { value: "daily", label: t("tabs.daily"), labelStyle: styles.tabLabel },
            { value: "records", label: t("tabs.records"), labelStyle: styles.tabLabel },
          ]}
        />

        {tab === "overview" ? <OverviewPanel context={context} store={store} /> : null}
        {tab === "daily" ? <DailyPanel context={context} store={store} /> : null}
        {tab === "records" ? <RecordsPanel store={store} /> : null}
      </ScrollView>

      <StorePickerModal
        visible={pickerVisible}
        presentation="sheet"
        stores={pickerStores}
        selectedStoreCode={store.storeCode}
        title={t("common:labels.selectStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setPickerVisible(false)}
        onSelectStore={(picked) => {
          setPickerVisible(false);
          if (picked) setRequestedStoreCode(picked.storeCode);
        }}
      />
    </CashScreenFrame>
  );
}

function StateMessage({ text, actionLabel, onAction }: { text: string; actionLabel?: string; onAction?: () => void }) {
  return (
    <View style={styles.center}>
      <Text style={styles.message}>{text}</Text>
      {actionLabel && onAction ? <Button mode="outlined" onPress={onAction}>{actionLabel}</Button> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
  muted: { color: HB_COLORS.textSecondary },
  message: { color: HB_COLORS.textPrimary, textAlign: "center" },
  storeCard: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.sheet,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    minHeight: 64,
  },
  storeInfo: { flex: 1, minWidth: 0 },
  switchStore: { flexDirection: "row", alignItems: "center", minHeight: 44, paddingLeft: HB_SPACING.xxs },
  switchStoreText: { color: HB_COLORS.action, fontWeight: "600", fontSize: 14 },
  tabLabel: { fontSize: 14, marginHorizontal: 0 },
});
