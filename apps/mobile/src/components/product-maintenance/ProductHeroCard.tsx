import { memo, useState } from "react";
import { Image, Pressable, StyleSheet, View } from "react-native";
import { Card, Icon, IconButton, Text } from "react-native-paper";
import { ProductBarcodeImage } from "@/components/product-maintenance/ProductBarcodeImage";
import { resolveBarcodeDisclosure } from "@/modules/product-maintenance/product-query-presentation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const PRODUCT_GRADE_CONFIG: Record<string, { color: string }> = {
  A: { color: "#722ED1" },
  B: { color: "#1890FF" },
  C: { color: "#FA8C16" },
  D: { color: "#F5222D" },
};

interface ProductHeroCardProps {
  imageUrl?: string | null;
  productName?: string;
  itemNumber?: string | null;
  supplierName?: string | null;
  supplierCode?: string | null;
  barcode?: string | null;
  productType?: number | null;
  grade?: string | null;
  /** compact：编码页签下的一行摘要。 */
  variant?: "full" | "compact";
  /** compact 摘要里显示的主零售价（已格式化，不含 $）。 */
  mainRetailPrice?: string | null;
  onPressProductType?: () => void;
  /** 修改商品名称入口；不传（无权限、离线、设备模式）则不显示铅笔按钮。 */
  onPressEditName?: () => void;
  /** 商品进销入口；不传则不显示图标按钮。 */
  onOpenInsights?: () => void;
  insightsDisabled?: boolean;
}

export const ProductHeroCard = memo(function ProductHeroCard({
  imageUrl,
  productName,
  itemNumber,
  supplierName,
  supplierCode,
  barcode,
  productType,
  grade,
  variant = "full",
  mainRetailPrice,
  onPressProductType,
  onPressEditName,
  onOpenInsights,
  insightsDisabled = false,
}: ProductHeroCardProps) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  // 条码图默认折叠；组件在页面内常驻，展开状态随页面保留。
  const [barcodeExpanded, setBarcodeExpanded] = useState(false);
  const disclosure = resolveBarcodeDisclosure(barcode, barcodeExpanded);
  const normalizedGrade = grade?.trim().toUpperCase();
  const gradeColor = normalizedGrade ? PRODUCT_GRADE_CONFIG[normalizedGrade]?.color ?? "#98A2B3" : undefined;
  const productTypeLabel =
    productType === 0 ? t("hero.productType.normal")
    : productType === 1 ? t("hero.productType.set")
    : productType === 2 ? t("hero.productType.multi")
    : t("hero.productType.unknown");
  const supplierLine = [supplierCode, supplierName].filter(Boolean).join(" ") || t("common:na");
  const name = productName || t("hero.unnamedProduct");

  const editNameButton = onPressEditName ? (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("nameEdit.action")}
      onPress={onPressEditName}
      hitSlop={10}
      style={({ pressed }) => [styles.editNameButton, pressed ? styles.pressed : null]}
    >
      <Icon source="pencil-outline" size={16} color={HB_COLORS.action} />
    </Pressable>
  ) : null;

  const typeChip = (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${t("hero.productTypeLabel")} ${productTypeLabel}`}
      onPress={onPressProductType}
      disabled={!onPressProductType}
      hitSlop={4}
      style={({ pressed }) => [styles.typeChip, pressed && onPressProductType ? styles.pressed : null]}
    >
      <Text style={styles.typeChipText} numberOfLines={1}>
        {productTypeLabel}
      </Text>
      {onPressProductType ? <Icon source="chevron-down" size={14} color="#166534" /> : null}
    </Pressable>
  );

  if (variant === "compact") {
    // 主零售价是编码页签的比对基准，放在品名行右侧且不收缩；过长时只截断货号/条码摘要。
    const summary = [
      itemNumber || t("common:na"),
      disclosure.value ? t("hero.mainBarcode", { value: disclosure.value }) : null,
    ].filter(Boolean).join(" · ");
    return (
      <Card style={styles.card} mode="contained">
        <View style={styles.compactRow}>
          {imageUrl ? (
            <Image source={{ uri: imageUrl }} style={styles.compactImage} resizeMode="cover" />
          ) : (
            <View style={styles.compactImage} />
          )}
          <View style={styles.meta}>
            <View style={styles.compactNameRow}>
              <Text style={[styles.compactName, styles.compactNameText]} numberOfLines={1}>{name}</Text>
              {editNameButton}
              {mainRetailPrice ? <Text style={styles.compactPrice}>${mainRetailPrice}</Text> : null}
            </View>
            <Text style={styles.metaText} numberOfLines={1}>{summary}</Text>
          </View>
          {typeChip}
        </View>
      </Card>
    );
  }

  return (
    <Card style={styles.card} mode="contained">
      <View style={styles.content}>
        <View style={styles.heroRow}>
          {imageUrl ? (
            <Image source={{ uri: imageUrl }} style={styles.image} resizeMode="cover" />
          ) : (
            <View style={styles.image} />
          )}
          <View style={styles.meta}>
            <View style={styles.nameRow}>
              <Text style={[styles.name, styles.nameText]} numberOfLines={2}>{name}</Text>
              {editNameButton}
            </View>
            <Text style={styles.metaText} numberOfLines={1}>
              <Text style={styles.itemNumber}>{itemNumber || t("common:na")}</Text>
              {` · ${supplierLine}`}
            </Text>
            <View style={styles.chips}>
              {normalizedGrade ? (
                <View style={[styles.gradeChip, { backgroundColor: gradeColor }]}>
                  <Text style={styles.gradeChipText}>{t("common:grade", { grade: normalizedGrade })}</Text>
                </View>
              ) : null}
              {typeChip}
            </View>
          </View>
          {onOpenInsights ? (
            <IconButton
              icon="chart-timeline-variant"
              accessibilityLabel={t("common:tabs.productInsights")}
              size={20}
              mode="contained-tonal"
              containerColor="#EAF2FF"
              iconColor={HB_COLORS.action}
              disabled={insightsDisabled}
              onPress={onOpenInsights}
              style={styles.insightsButton}
            />
          ) : null}
        </View>

        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded: disclosure.showImage }}
          disabled={!disclosure.canToggle}
          onPress={() => setBarcodeExpanded((current) => !current)}
          style={({ pressed }) => [styles.barcodeRow, pressed ? styles.pressed : null]}
        >
          <Icon source="barcode" size={18} color={HB_COLORS.textSecondary} />
          <Text style={styles.barcodeValue} numberOfLines={1}>
            {disclosure.value || t("hero.noBarcode")}
          </Text>
          {disclosure.canToggle ? (
            <View style={styles.barcodeToggle}>
              <Text style={styles.barcodeToggleText}>
                {disclosure.showImage ? t("hero.hideBarcode") : t("hero.showBarcode")}
              </Text>
              <Icon
                source={disclosure.showImage ? "chevron-up" : "chevron-down"}
                size={16}
                color={HB_COLORS.action}
              />
            </View>
          ) : null}
        </Pressable>
        {disclosure.showImage ? (
          <View style={styles.barcodeImageWrap}>
            <ProductBarcodeImage value={disclosure.value} />
          </View>
        ) : null}
      </View>
    </Card>
  );
});

const styles = StyleSheet.create({
  card: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  content: {
    padding: HB_SPACING.sm,
    gap: HB_SPACING.xs,
  },
  heroRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.sm,
  },
  image: {
    width: 60,
    height: 60,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  meta: {
    flex: 1,
    minWidth: 0,
    gap: 3,
  },
  name: {
    fontSize: 15,
    lineHeight: 20,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  nameRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 6,
  },
  nameText: {
    flex: 1,
    minWidth: 0,
  },
  // 铅笔按钮视觉 28dp，靠 hitSlop 扩到约 48dp 的点击区；compact 行是 baseline 对齐，要显式居中。
  editNameButton: {
    alignSelf: "center",
    flexShrink: 0,
    width: 28,
    height: 28,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#EAF2FF",
  },
  metaText: {
    fontSize: 12,
    lineHeight: 17,
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  itemNumber: {
    color: "#1D4ED8",
    fontWeight: "700",
  },
  chips: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    gap: 6,
    marginTop: 2,
  },
  gradeChip: {
    borderRadius: 999,
    paddingHorizontal: 8,
    paddingVertical: 2,
  },
  gradeChipText: {
    fontSize: 12,
    lineHeight: 16,
    color: HB_COLORS.white,
    fontWeight: "800",
  },
  typeChip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    borderRadius: 999,
    paddingLeft: 8,
    paddingRight: 6,
    paddingVertical: 2,
    backgroundColor: "#DCFCE7",
    borderWidth: 1,
    borderColor: "#86EFAC",
    flexShrink: 0,
  },
  typeChipText: {
    fontSize: 12,
    lineHeight: 16,
    fontWeight: "700",
    color: "#166534",
  },
  insightsButton: {
    width: 36,
    height: 36,
    margin: 0,
    borderRadius: HB_RADIUS.control,
  },
  barcodeRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    minHeight: 36,
    paddingHorizontal: 10,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  barcodeValue: {
    flex: 1,
    minWidth: 0,
    fontSize: 14,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  barcodeToggle: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
  },
  barcodeToggleText: {
    fontSize: 12,
    fontWeight: "600",
    color: HB_COLORS.action,
  },
  barcodeImageWrap: {
    alignSelf: "center",
    width: 220,
    maxWidth: "100%",
  },
  compactRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  compactImage: {
    width: 40,
    height: 40,
    borderRadius: 6,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  compactName: {
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  compactNameRow: {
    flexDirection: "row",
    alignItems: "baseline",
    gap: HB_SPACING.xs,
  },
  compactNameText: {
    flexShrink: 1,
  },
  compactPrice: {
    flexShrink: 0,
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "700",
    color: HB_COLORS.action,
    fontVariant: ["tabular-nums"],
  },
  pressed: {
    opacity: 0.72,
  },
});
