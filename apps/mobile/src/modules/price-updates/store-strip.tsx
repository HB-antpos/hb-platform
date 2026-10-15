import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

export interface PriceUpdateStoreStripItem {
  storeCode: string;
  storeName: string;
  selected: boolean;
  /** 该分店未完成数；null = 汇总还没拿到，不显示数字。 */
  pendingCount: number | null;
  /** 管理分店且有超期任务：显示红点（与锁报表同口径）。 */
  overdueFlag: boolean;
}

interface PriceUpdateStoreStripProps {
  items: readonly PriceUpdateStoreStripItem[];
  onSelect: (storeCode: string) => void;
}

/**
 * 店长账号的分店切换条：名下每家分店一个按钮，带该店未完成数；全部完成的显示 ✓；
 * 管理分店有超期的挂红点。店多时横向滑动，点一下就切到那家店。
 */
export function PriceUpdateStoreStrip({ items, onSelect }: PriceUpdateStoreStripProps) {
  const { t } = useAppTranslation("priceUpdates");

  return (
    <ScrollView
      horizontal
      showsHorizontalScrollIndicator={false}
      contentContainerStyle={styles.strip}
      keyboardShouldPersistTaps="handled"
    >
      {items.map((item) => {
        const done = item.pendingCount === 0;
        const accessibilityLabel = done
          ? t("strip.accessibilityDone", { store: item.storeName })
          : item.overdueFlag
            ? t("strip.accessibilityOverdue", { store: item.storeName, count: item.pendingCount ?? 0 })
            : t("strip.accessibility", { store: item.storeName, count: item.pendingCount ?? 0 });
        return (
          <Pressable
            key={item.storeCode}
            accessibilityRole="button"
            accessibilityState={{ selected: item.selected }}
            accessibilityLabel={accessibilityLabel}
            onPress={() => onSelect(item.storeCode)}
            style={({ pressed }) => [
              styles.chip,
              item.selected ? styles.chipSelected : null,
              pressed ? styles.chipPressed : null,
            ]}
          >
            <Text
              variant="labelLarge"
              numberOfLines={1}
              style={[
                styles.name,
                item.selected ? styles.nameSelected : done ? styles.nameDone : null,
              ]}
            >
              {item.storeName}
            </Text>
            {item.pendingCount == null ? null : done ? (
              <MaterialCommunityIcons
                name="check-circle"
                size={16}
                color={item.selected ? HB_COLORS.white : HB_COLORS.success}
              />
            ) : (
              <View style={[styles.count, item.selected ? styles.countSelected : null]}>
                <Text
                  variant="labelSmall"
                  style={[styles.countText, item.selected ? styles.countTextSelected : null]}
                >
                  {item.pendingCount}
                </Text>
              </View>
            )}
            {item.overdueFlag ? <View style={styles.dot} /> : null}
          </Pressable>
        );
      })}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  strip: { gap: HB_SPACING.xs, paddingRight: HB_SPACING.md },
  chip: {
    height: 32,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingLeft: 12,
    paddingRight: 8,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  chipSelected: { backgroundColor: HB_COLORS.action, borderColor: HB_COLORS.action },
  chipPressed: { opacity: 0.85 },
  name: { fontWeight: "600", color: HB_COLORS.textPrimary, maxWidth: 140 },
  nameSelected: { color: HB_COLORS.white },
  nameDone: { color: HB_COLORS.textSecondary },
  count: {
    minWidth: 22,
    height: 20,
    borderRadius: 10,
    paddingHorizontal: 6,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#FEF0C7",
  },
  countSelected: { backgroundColor: "rgba(255,255,255,0.22)" },
  countText: { color: HB_COLORS.warning, fontWeight: "700", fontVariant: ["tabular-nums"] },
  countTextSelected: { color: HB_COLORS.white },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: "#F04438" },
});
