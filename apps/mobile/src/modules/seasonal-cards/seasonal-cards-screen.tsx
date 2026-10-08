import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, SegmentedButtons, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { SeasonalCardHistoryView } from "./components/HistoryView";
import { SeasonalCardSubmitView } from "./components/SubmitView";

type ViewMode = "submit" | "history";

/**
 * 节日贺卡（季节卡片）剩余数量：顶部分店 +「填报 / 历史记录」切换。
 * 分店沿用原逻辑：设备模式锁定绑定分店，否则默认当前选中分店，可手动切换；填报与历史共用。
 */
export function SeasonalCardsScreen() {
  const { t } = useAppTranslation(["seasonalCards", "common"]);
  const access = useAuthStore((state) => state.access);
  const selectedStore = useCartStore((state) => state.selectedStore);
  const { stores, selectedStoreCode, isDeviceMode } = useStores();
  const canView = access.canViewSeasonalCardRemaining;
  const canSubmit = access.canSubmitSeasonalCardRemaining;
  const [viewMode, setViewMode] = useState<ViewMode>(canSubmit ? "submit" : "history");
  const [storeCode, setStoreCode] = useState("");
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [snackbar, setSnackbar] = useState("");
  const deviceBoundStoreCode = getDeviceBoundStoreCode({ isDeviceMode, selectedStoreCode });

  useEffect(() => {
    if (!canSubmit && canView) {
      setViewMode("history");
    }
  }, [canSubmit, canView]);

  useEffect(() => {
    if (deviceBoundStoreCode) {
      setStoreCode(deviceBoundStoreCode);
      return;
    }
    if (isDeviceMode) {
      setStoreCode("");
      return;
    }
    if (selectedStore?.storeCode) {
      setStoreCode((current) => current || selectedStore.storeCode);
    }
  }, [deviceBoundStoreCode, isDeviceMode, selectedStore?.storeCode]);

  const currentStore = useMemo(
    () => stores.find((store) => store.storeCode === storeCode) ?? null,
    [storeCode, stores]
  );
  const storeLabel = currentStore?.storeName || storeCode || t("store.select");
  const showSubmit = viewMode === "submit" && canSubmit;
  const [historyVisited, setHistoryVisited] = useState(false);
  useEffect(() => {
    if (!showSubmit) {
      setHistoryVisited(true);
    }
  }, [showSubmit]);
  const historyMounted = canView && (historyVisited || !showSubmit);

  if (!canView && !canSubmit) {
    return (
      <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
        <EmptyState
          title={t("messages.noAccessTitle")}
          description={t("messages.noAccessDescription")}
        />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <View style={styles.titleRow}>
          <Text variant="headlineSmall" style={styles.title}>
            {t("title")}
          </Text>
          {/* 不用 Paper Button：它的文字只显示一行，长分店名会被截断。 */}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={`${t("labels.storeCode")} ${storeLabel}`}
            accessibilityState={{ disabled: Boolean(deviceBoundStoreCode) }}
            disabled={Boolean(deviceBoundStoreCode)}
            onPress={() => setStorePickerVisible(true)}
            style={({ pressed }) => [styles.storeChip, pressed ? styles.storeChipPressed : null]}
          >
            <Icon source="storefront-outline" size={16} color={HB_COLORS.textSecondary} />
            <Text style={styles.storeChipText}>{storeLabel}</Text>
            {deviceBoundStoreCode ? null : (
              <Icon source="chevron-down" size={16} color={HB_COLORS.textSecondary} />
            )}
          </Pressable>
        </View>
        {canSubmit && canView ? (
          <SegmentedButtons
            value={viewMode}
            onValueChange={(value) => setViewMode(value as ViewMode)}
            buttons={[
              { value: "submit", label: t("viewModes.submit") },
              { value: "history", label: t("viewModes.history") },
            ]}
          />
        ) : null}
      </View>

      {/* 两个视图切换时保持挂载，避免来回切换丢掉已填的数量和历史筛选；历史首次打开时才挂载。 */}
      {canSubmit ? (
        <View style={[styles.pane, showSubmit ? null : styles.paneHidden]}>
          <SeasonalCardSubmitView storeCode={storeCode} canSubmit={canSubmit} onNotify={setSnackbar} />
        </View>
      ) : null}
      {historyMounted ? (
        <View style={[styles.pane, showSubmit ? styles.paneHidden : null]}>
          <SeasonalCardHistoryView
            storeCode={storeCode}
            enabled={canView && (!isDeviceMode || Boolean(deviceBoundStoreCode))}
          />
        </View>
      ) : null}

      <StorePickerModal
        presentation="sheet"
        visible={storePickerVisible}
        stores={stores}
        selectedStoreCode={storeCode || null}
        title={t("store.pickerTitle")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={(store) => {
          setStoreCode(deviceBoundStoreCode ?? store?.storeCode ?? "");
          setStorePickerVisible(false);
        }}
      />

      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { ...BUSINESS_UI.screen },
  header: {
    ...BUSINESS_UI.header,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
    gap: HB_SPACING.sm,
  },
  titleRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  title: { ...BUSINESS_UI.title },
  pane: { flex: 1 },
  paneHidden: { display: "none" },
  storeChip: {
    minHeight: 44,
    maxWidth: "100%",
    flexShrink: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
    borderRadius: 22,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  storeChipPressed: { opacity: 0.8 },
  storeChipText: { flexShrink: 1, fontSize: 14, lineHeight: 20, color: HB_COLORS.textPrimary },
});
