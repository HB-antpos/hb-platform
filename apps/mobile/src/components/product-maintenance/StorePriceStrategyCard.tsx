import { memo, type ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Card, Icon, Text } from "react-native-paper";
import {
  getMarginTrendArrow,
  getMarginTrendColor,
  type MarginTrend,
} from "@/modules/product-maintenance/product-query-presentation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 修改前的显示值；为 null 表示该字段未改动。 */
export interface StorePriceOriginalValues {
  purchasePrice?: string | null;
  retailPrice?: string | null;
  discountPercent?: string | null;
  discountedRetailPrice?: string | null;
}

interface StorePriceStrategyCardProps {
  storeName?: string | null;
  canSelectStore?: boolean;
  storeLocked?: boolean;
  onStorePress?: () => void;
  purchasePrice?: string;
  retailPrice?: string;
  retailGp?: string;
  retailGpTrend?: MarginTrend;
  discountPercent?: string;
  discountedRetailPrice?: string;
  discountedRetailGp?: string;
  discountedRetailGpTrend?: MarginTrend;
  originalValues?: StorePriceOriginalValues;
  autoPricing: boolean;
  isSpecialProduct: boolean;
  rate?: string;
  strategySourceLabel?: string | null;
  strategyRuleLabel?: string | null;
  evaluatingRate?: boolean;
  /** 卡片底部附加入口（同步其它分店）。 */
  footer?: ReactNode;
  onEditPurchasePrice: () => void;
  onEditRetailPrice: () => void;
  onEditDiscountPercent: () => void;
  onEditDiscountedRetailPrice: () => void;
  onToggleAutoPricing: (value: boolean) => void;
  onToggleSpecial: (value: boolean) => void;
  /** 离线态只读：价格单元与自动/特殊开关全部不可编辑，打印不受影响。 */
  readOnly?: boolean;
}

/** 门店名 + 下拉；锁定或不可切换时只显示名称，不可点。 */
export function StoreSwitchButton({
  storeName,
  canSelectStore = false,
  storeLocked = false,
  onPress,
}: {
  storeName?: string | null;
  canSelectStore?: boolean;
  storeLocked?: boolean;
  onPress?: () => void;
}) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const name = storeName || t("common:na");
  const selectable = canSelectStore && !storeLocked && Boolean(onPress);

  return (
    <Pressable
      accessibilityRole={selectable ? "button" : undefined}
      accessibilityLabel={t("currentStore", { store: name })}
      accessibilityHint={storeLocked ? t("actions.invoiceStoreLocked") : undefined}
      disabled={!selectable}
      onPress={onPress}
      hitSlop={6}
      style={({ pressed }) => [styles.storeButton, pressed && selectable ? styles.pressed : null]}
    >
      {storeLocked ? <Icon source="lock-outline" size={14} color={HB_COLORS.action} /> : null}
      <Text variant="titleSmall" style={styles.storeName} numberOfLines={2}>
        {name}
      </Text>
      {selectable ? <Icon source="chevron-down" size={18} color={HB_COLORS.textSecondary} /> : null}
    </Pressable>
  );
}

// 门店切换按钮随查询忙碌状态更新；未变化的价格单元独立复用，避免整卡重绘。
const TogglePill = memo(function TogglePill({ label, value, onToggle, readOnly = false }: { label: string; value: boolean; onToggle: (value: boolean) => void; readOnly?: boolean }) {
  const { t } = useAppTranslation("productQuery");
  return (
    <Pressable
      accessibilityRole="switch"
      accessibilityState={{ checked: value, disabled: readOnly }}
      accessibilityLabel={label}
      onPress={readOnly ? undefined : () => onToggle(!value)}
      hitSlop={4}
      style={({ pressed }) => [styles.pill, value ? styles.pillOn : null, readOnly ? styles.readOnly : null, pressed ? styles.pressed : null]}
    >
      <View style={[styles.pillDot, value ? styles.pillDotOn : null]} />
      <Text style={[styles.pillText, value ? styles.pillTextOn : null]} numberOfLines={1}>
        {label} {value ? t("storePrice.on") : t("storePrice.off")}
      </Text>
    </Pressable>
  );
});

const PriceField = memo(function PriceField({
  label,
  value,
  original,
  onPress,
  valueColor,
  muted = false,
  readOnly = false,
}: {
  label: string;
  value?: string;
  original?: string | null;
  onPress: () => void;
  valueColor?: string;
  muted?: boolean;
  readOnly?: boolean;
}) {
  const { t } = useAppTranslation("productQuery");
  const changed = original != null;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${label} ${value || "--"}`}
      accessibilityState={{ disabled: readOnly }}
      onPress={readOnly ? undefined : onPress}
      style={({ pressed }) => [
        styles.field,
        muted ? styles.fieldMuted : null,
        changed ? styles.fieldChanged : null,
        readOnly ? styles.readOnly : null,
        pressed ? styles.pressed : null,
      ]}
    >
      <Text style={[styles.fieldLabel, changed ? styles.fieldLabelChanged : null]} numberOfLines={1}>
        {changed ? t("storePrice.original", { label, value: original }) : label}
      </Text>
      <Text style={[styles.fieldValue, valueColor ? { color: valueColor } : null]} numberOfLines={1}>
        {value || "--"}
      </Text>
    </Pressable>
  );
});

const Metric = memo(function Metric({ label, value, color, wide = false }: { label: string; value: string; color?: string; wide?: boolean }) {
  // 策略文字（如「默认全局 0 - 5」）远长于倍率/毛利数字：加宽该格、字号略小，最多两行，避免被截断。
  return (
    <View style={[styles.metric, wide ? styles.metricWide : null]}>
      <Text style={styles.metricLabel} numberOfLines={1}>{label}</Text>
      <Text
        style={[styles.metricValue, wide ? styles.metricValueWide : null, color ? { color } : null]}
        numberOfLines={wide ? 2 : 1}
      >
        {value}
      </Text>
    </View>
  );
});

export const StorePriceStrategyCard = memo(function StorePriceStrategyCard({
  storeName,
  canSelectStore = false,
  storeLocked = false,
  onStorePress,
  purchasePrice,
  retailPrice,
  retailGp,
  retailGpTrend = null,
  discountPercent,
  discountedRetailPrice,
  discountedRetailGp,
  discountedRetailGpTrend = null,
  originalValues,
  autoPricing,
  isSpecialProduct,
  rate,
  strategySourceLabel,
  strategyRuleLabel,
  evaluatingRate = false,
  footer,
  onEditPurchasePrice,
  onEditRetailPrice,
  onEditDiscountPercent,
  onEditDiscountedRetailPrice,
  onToggleAutoPricing,
  onToggleSpecial,
  readOnly = false,
}: StorePriceStrategyCardProps) {
  const { t } = useAppTranslation("productQuery");
  const strategyText = evaluatingRate
    ? t("storePrice.calculating")
    : [strategySourceLabel, strategyRuleLabel].filter(Boolean).join(" ");
  const gpColor = getMarginTrendColor(retailGpTrend, HB_COLORS.action);
  const discountedGpColor = getMarginTrendColor(discountedRetailGpTrend, HB_COLORS.action);

  return (
    <Card style={styles.card} mode="contained">
      <View style={styles.header}>
        <View style={styles.headerStore}>
          <StoreSwitchButton
            storeName={storeName || t("storePrice.fallbackTitle")}
            canSelectStore={canSelectStore}
            storeLocked={storeLocked}
            onPress={onStorePress}
          />
        </View>
        <View style={styles.pills}>
          <TogglePill readOnly={readOnly} label={t("storePrice.autoPricing")} value={autoPricing} onToggle={onToggleAutoPricing} />
          <TogglePill readOnly={readOnly} label={t("storePrice.specialProduct")} value={isSpecialProduct} onToggle={onToggleSpecial} />
        </View>
      </View>

      <View style={styles.body}>
        <View style={styles.grid}>
          <PriceField readOnly={readOnly}
            label={t("storePrice.grid.purchase")}
            value={purchasePrice}
            original={originalValues?.purchasePrice}
            onPress={onEditPurchasePrice}
          />
          <PriceField readOnly={readOnly}
            label={t("storePrice.grid.retail")}
            value={retailPrice}
            original={originalValues?.retailPrice}
            onPress={onEditRetailPrice}
          />
        </View>
        <View style={styles.grid}>
          <PriceField readOnly={readOnly}
            label={t("storePrice.grid.discountPercent")}
            value={discountPercent}
            original={originalValues?.discountPercent}
            onPress={onEditDiscountPercent}
            valueColor={HB_COLORS.danger}
          />
          {/* 折后价由零售价和折扣推算，视觉上按只读灰底呈现；仍保留原来的点击反推折扣入口。 */}
          <PriceField readOnly={readOnly}
            label={t("storePrice.grid.discountedRetail")}
            value={discountedRetailPrice}
            original={originalValues?.discountedRetailPrice}
            onPress={onEditDiscountedRetailPrice}
            valueColor={HB_COLORS.success}
            muted
          />
        </View>

        <View style={styles.metrics}>
          <Metric label={t("storePrice.rateLabel")} value={rate || "--"} />
          <View style={styles.metricDivider} />
          <Metric
            label={t("storePrice.gp")}
            value={`${retailGp || "--"}${getMarginTrendArrow(retailGpTrend)}`}
            color={gpColor}
          />
          <View style={styles.metricDivider} />
          <Metric
            label={t("storePrice.discountedGp")}
            value={`${discountedRetailGp || "--"}${getMarginTrendArrow(discountedRetailGpTrend)}`}
            color={discountedGpColor}
          />
          <View style={styles.metricDivider} />
          <Metric label={t("storePrice.strategy")} value={strategyText || "--"} color={HB_COLORS.textSecondary} wide />
        </View>
      </View>

      {footer ? <View style={styles.footer}>{footer}</View> : null}
    </Card>
  );
});

const styles = StyleSheet.create({
  readOnly: {
    opacity: 0.6,
  },
  card: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
    overflow: "hidden",
  },
  header: {
    flexDirection: "row",
    // 窄屏放不下「分店名 + 两个开关」时，开关整体换到下一行，分店名不再被挤成省略号。
    flexWrap: "wrap",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingTop: HB_SPACING.sm,
    paddingBottom: HB_SPACING.xs,
  },
  headerStore: {
    // 至少给分店名 140dp 的基础宽度并允许增长；剩余空间不够容纳开关时由 header 换行
    flexGrow: 1,
    flexShrink: 1,
    flexBasis: 140,
    minWidth: 0,
  },
  storeButton: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    minHeight: 32,
    alignSelf: "flex-start",
    maxWidth: "100%",
  },
  storeName: {
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    flexShrink: 1,
  },
  pills: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    flexShrink: 0,
  },
  pill: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    minHeight: 30,
    paddingHorizontal: 10,
    borderRadius: 999,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  pillOn: {
    borderColor: "#91CAFF",
    backgroundColor: "#EAF2FF",
  },
  pillDot: {
    width: 6,
    height: 6,
    borderRadius: 3,
    backgroundColor: "#98A2B3",
  },
  pillDotOn: {
    backgroundColor: HB_COLORS.brand,
  },
  pillText: {
    fontSize: 12,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
  },
  pillTextOn: {
    color: HB_COLORS.action,
  },
  body: {
    paddingHorizontal: HB_SPACING.sm,
    paddingBottom: HB_SPACING.sm,
    gap: HB_SPACING.xs,
  },
  grid: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  field: {
    flex: 1,
    minWidth: 0,
    minHeight: 52,
    justifyContent: "center",
    gap: 2,
    paddingHorizontal: 10,
    paddingVertical: 6,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  fieldMuted: {
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  fieldChanged: {
    borderWidth: 2,
    borderColor: HB_COLORS.brand,
    paddingHorizontal: 9,
    paddingVertical: 5,
  },
  fieldLabel: {
    fontSize: 11,
    lineHeight: 14,
    color: HB_COLORS.textSecondary,
  },
  fieldLabelChanged: {
    color: HB_COLORS.action,
    fontWeight: "600",
  },
  fieldValue: {
    fontSize: 17,
    lineHeight: 22,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  metrics: {
    flexDirection: "row",
    alignItems: "center",
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    paddingVertical: 6,
  },
  metric: {
    flex: 1,
    minWidth: 0,
    alignItems: "center",
    gap: 1,
    paddingHorizontal: 4,
  },
  metricWide: {
    flex: 1.8,
  },
  metricValueWide: {
    fontSize: 12,
    lineHeight: 16,
    fontWeight: "600",
    textAlign: "center",
  },
  metricDivider: {
    width: StyleSheet.hairlineWidth,
    alignSelf: "stretch",
    backgroundColor: HB_COLORS.outline,
  },
  metricLabel: {
    fontSize: 11,
    lineHeight: 14,
    color: HB_COLORS.textSecondary,
  },
  metricValue: {
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "700",
    color: HB_COLORS.action,
    fontVariant: ["tabular-nums"],
  },
  footer: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  pressed: {
    opacity: 0.72,
  },
});
