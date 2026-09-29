import type { LayoutChangeEvent } from "react-native";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { LINKED_COLORS } from "@/modules/product-report/linked-colors";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

export type LinkedAnchorKey = "branches" | "suppliers" | "products";

export interface LinkedAnchor {
  key: LinkedAnchorKey;
  count: number;
  /** 该区块正在按新条件重取，数量暂不可信，显示省略号。 */
  updating: boolean;
}

interface LinkedFilterBarProps {
  /** 已选分店名称；null 表示全部分店。 */
  branchName: string | null;
  /** 已选供应商名称；null 表示未选。 */
  supplierName: string | null;
  hint: string;
  branchPickerDisabled: boolean;
  anchors: readonly LinkedAnchor[];
  onOpenBranchPicker: () => void;
  onClearBranch: () => void;
  onPressSupplier: () => void;
  onClearSupplier: () => void;
  onClearAll: () => void;
  onPressAnchor: (key: LinkedAnchorKey) => void;
  onLayout?: (event: LayoutChangeEvent) => void;
}

// chip 只有 36pt 高，上下扩大点击区域到 44pt 触控下限。
const CHIP_HIT_SLOP = { top: 4, bottom: 4 };
const CLEAR_HIT_SLOP = { top: 4, bottom: 4, right: 6 };

const ANCHOR_COLORS: Record<LinkedAnchorKey, string> = {
  branches: LINKED_COLORS.branch,
  suppliers: LINKED_COLORS.supplier,
  products: LINKED_COLORS.product,
};

const ANCHOR_LABEL_KEYS: Record<LinkedAnchorKey, string> = {
  branches: "productReport.linked.anchorBranches",
  suppliers: "productReport.linked.anchorSuppliers",
  products: "productReport.linked.anchorProducts",
};

/**
 * 商品报告吸顶联动条：第一行是分店、供应商选择 chip（与表内选中行同色）；
 * 有选择时第二行显示各区块当前行数，点按跳到该区块。受影响的表可能在屏幕外，
 * 行数变化本身就是「联动已生效」的反馈。
 */
export function LinkedFilterBar({
  branchName,
  supplierName,
  hint,
  branchPickerDisabled,
  anchors,
  onOpenBranchPicker,
  onClearBranch,
  onPressSupplier,
  onClearSupplier,
  onClearAll,
  onPressAnchor,
  onLayout,
}: LinkedFilterBarProps) {
  const { t } = useAppTranslation("common");
  const hasSelection = Boolean(branchName || supplierName);

  return (
    <View style={styles.bar} onLayout={onLayout}>
      <View style={styles.selectionRow}>
        {branchName ? (
          <View style={[styles.chip, styles.branchChip]}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("productReport.linked.branchChipA11y", { name: branchName })}
              disabled={branchPickerDisabled}
              hitSlop={CHIP_HIT_SLOP}
              onPress={onOpenBranchPicker}
              style={styles.chipBody}
            >
              <View style={[styles.dot, { backgroundColor: LINKED_COLORS.branch }]} />
              <Text variant="labelSmall" style={styles.chipLabel}>{t("productReport.linked.branchLabel")}</Text>
              <Text variant="labelLarge" numberOfLines={1} style={[styles.chipValue, { color: LINKED_COLORS.branchText }]}>
                {branchName}
              </Text>
            </Pressable>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("productReport.linked.clearBranch")}
              hitSlop={CLEAR_HIT_SLOP}
              onPress={onClearBranch}
              style={styles.chipClear}
            >
              <Icon source="close" size={14} color="#475467" />
            </Pressable>
          </View>
        ) : (
          <Button mode="outlined" compact icon="store-outline" disabled={branchPickerDisabled} onPress={onOpenBranchPicker}>
            {t("productReport.filters.allStores")}
          </Button>
        )}
        {supplierName ? (
          <View style={[styles.chip, styles.supplierChip]}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("productReport.linked.supplierChipA11y", { name: supplierName })}
              hitSlop={CHIP_HIT_SLOP}
              onPress={onPressSupplier}
              style={styles.chipBody}
            >
              <View style={[styles.dot, { backgroundColor: LINKED_COLORS.supplier }]} />
              <Text variant="labelSmall" style={styles.chipLabel}>{t("productReport.linked.supplierLabel")}</Text>
              <Text variant="labelLarge" numberOfLines={1} style={[styles.chipValue, { color: LINKED_COLORS.supplierText }]}>
                {supplierName}
              </Text>
            </Pressable>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("productReport.linked.clearSupplier")}
              hitSlop={CLEAR_HIT_SLOP}
              onPress={onClearSupplier}
              style={styles.chipClear}
            >
              <Icon source="close" size={14} color="#475467" />
            </Pressable>
          </View>
        ) : null}
        {hasSelection ? null : (
          <Text variant="bodySmall" numberOfLines={2} style={styles.hint}>{hint}</Text>
        )}
        {branchName && supplierName ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("productReport.linked.clearAllA11y")}
            hitSlop={CHIP_HIT_SLOP}
            onPress={onClearAll}
            style={styles.clearAll}
          >
            <Text variant="labelLarge" style={styles.clearAllText}>{t("productReport.linked.clearAll")}</Text>
          </Pressable>
        ) : null}
      </View>
      {hasSelection ? (
        <View style={styles.anchorRow}>
          {anchors.map((anchor) => {
            const section = t(ANCHOR_LABEL_KEYS[anchor.key]);
            return (
              <Pressable
                key={anchor.key}
                accessibilityRole="button"
                accessibilityLabel={anchor.updating
                  ? t("productReport.linked.anchorUpdatingA11y", { section })
                  : t("productReport.linked.anchorA11y", { section, count: anchor.count })}
                onPress={() => onPressAnchor(anchor.key)}
                style={styles.anchor}
              >
                <View style={[styles.dot, { backgroundColor: ANCHOR_COLORS[anchor.key] }]} />
                <Text variant="labelMedium" style={styles.anchorLabel}>{section}</Text>
                <Text variant="labelMedium" style={anchor.updating ? styles.anchorCountUpdating : styles.anchorCount}>
                  {anchor.updating ? "…" : anchor.count.toLocaleString("en-AU")}
                </Text>
              </Pressable>
            );
          })}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    // 吸顶时要盖住下方滚动内容：铺满内容区左右边距并使用与页面相同的底色。
    marginHorizontal: -16,
    paddingHorizontal: 16,
    paddingVertical: 6,
    gap: 6,
    backgroundColor: "#F7F8FA",
    borderBottomWidth: 1,
    borderBottomColor: "#E5E7EB",
  },
  selectionRow: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  chip: {
    height: 36,
    flexShrink: 1,
    minWidth: 0,
    maxWidth: 168,
    flexDirection: "row",
    alignItems: "center",
    borderWidth: 1,
    borderRadius: 8,
  },
  branchChip: {
    borderColor: LINKED_COLORS.branchBorder,
    backgroundColor: LINKED_COLORS.branchBackground,
  },
  supplierChip: {
    borderColor: LINKED_COLORS.supplierBorder,
    backgroundColor: LINKED_COLORS.supplierBackground,
  },
  chipBody: {
    height: 34,
    flexShrink: 1,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    paddingLeft: 10,
    paddingRight: 2,
  },
  dot: {
    width: 6,
    height: 6,
    borderRadius: 2,
  },
  chipLabel: {
    color: "#475467",
  },
  chipValue: {
    flexShrink: 1,
    minWidth: 0,
    fontWeight: "600",
  },
  chipClear: {
    width: 30,
    height: 34,
    alignItems: "center",
    justifyContent: "center",
  },
  hint: {
    flex: 1,
    minWidth: 0,
    color: "#6B7280",
  },
  clearAll: {
    marginLeft: "auto",
    height: 36,
    justifyContent: "center",
    paddingHorizontal: 6,
  },
  clearAllText: {
    color: "#0958D9",
    fontWeight: "600",
  },
  anchorRow: {
    flexDirection: "row",
    gap: 6,
  },
  anchor: {
    flex: 1,
    height: 36,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 5,
    borderWidth: 1,
    borderColor: "#E5E7EB",
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
  },
  anchorLabel: {
    color: "#374151",
    fontWeight: "600",
  },
  anchorCount: {
    color: "#111827",
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  anchorCountUpdating: {
    color: "#6B7280",
    fontWeight: "700",
  },
});
