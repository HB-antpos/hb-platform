import { memo } from "react";
import { Image, Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { DetailTag } from "@/components/local-supplier-invoices/detail-tag";
import {
  ACTION_TONE,
  formatMoney,
  formatQuantity,
  HQ_BUCKET_TONE,
} from "@/components/local-supplier-invoices/invoice-detail-format";
import {
  getBarcodeStatusFilter,
  getDetailProgressBucket,
  getPricingSummary,
  getProductStatusFilter,
  getPurchasePriceChange,
} from "@/modules/local-supplier-invoices/detail";
import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

type Translate = (key: string, options?: Record<string, unknown>) => string;

interface DetailRowCardProps {
  detail: LocalSupplierInvoiceItem;
  /** 已改未存的字段名拼成的串（"quantity|purchasePrice"），用原始类型保证 memo 生效。 */
  editedKey: string;
  /** 有「编辑」权限：显示匹配状态/操作类型/定价（Web 的编辑视图）；否则显示「总部处理」。 */
  showWorkbench: boolean;
  /** 管理员：点卡片可编辑。 */
  canEditRow: boolean;
  /** 多选模式（二期 B）：点卡片切换勾选，不再打开编辑。 */
  selectionMode?: boolean;
  selected?: boolean;
  onToggleSelect?: (detail: LocalSupplierInvoiceItem) => void;
  onPress: (detail: LocalSupplierInvoiceItem) => void;
  onCopy: (label: string, value: string) => void;
  onOpenProduct: (detail: LocalSupplierInvoiceItem) => void;
  t: Translate;
}

function EditedValue({ edited, children }: { edited: boolean; children: React.ReactNode }) {
  if (!edited) return <>{children}</>;
  // 已改未存：浅蓝底 + 右上角圆点（与 Web renderEditedValue 对应）。
  return (
    <View style={styles.edited}>
      {children}
      <View style={styles.editedDot} />
    </View>
  );
}

export const DetailRowCard = memo(function DetailRowCard({
  detail,
  editedKey,
  showWorkbench,
  canEditRow,
  selectionMode = false,
  selected = false,
  onToggleSelect,
  onPress,
  onCopy,
  onOpenProduct,
  t,
}: DetailRowCardProps) {
  const edited = new Set(editedKey ? editedKey.split("|") : []);
  const priceChange = getPurchasePriceChange(detail);
  const pricing = getPricingSummary(detail);
  const productStatus = getProductStatusFilter(detail);
  const barcodeStatus = getBarcodeStatusFilter(detail);
  const bucket = getDetailProgressBucket(detail);
  const action = detail.activityType;
  const hasAdditional = (detail.additionalBarcodes?.length ?? 0) > 0;

  const priceChangeText = (() => {
    const percent = priceChange.ratio == null ? "" : (Math.abs(priceChange.ratio) * 100).toFixed(1);
    const last = priceChange.last == null ? "--" : priceChange.last.toFixed(2);
    switch (priceChange.kind) {
      case "up":
      case "down":
        return t(`detail.priceChange.${priceChange.kind}`, { percent, last });
      case "flat": return t("detail.priceChange.flat");
      case "noHistory": return t("detail.priceChange.noHistory");
      case "none": return t("detail.priceChange.noPrice");
    }
  })();
  const priceChangeStyle = priceChange.kind === "up"
    ? (priceChange.level === "light" ? styles.upLight : styles.up)
    : priceChange.kind === "down" ? styles.down : styles.neutral;

  const productStatusTag = (() => {
    switch (productStatus) {
      case "createdHere": return <DetailTag label={t("detail.productStatus.createdHere")} tone="green" />;
      case "exists": return (
        <DetailTag
          label={(detail.existingProductCount ?? 0) > 1
            ? t("detail.productStatus.existsCount", { count: detail.existingProductCount })
            : t("detail.productStatus.exists")}
          tone="green"
        />
      );
      case "notExists": return (
        <DetailTag
          label={t(showWorkbench ? "detail.productStatus.notExists" : "detail.productStatus.notCreated")}
          tone="red"
        />
      );
      default: return <DetailTag label={t("detail.productStatus.notDetected")} tone="gray" />;
    }
  })();
  const barcodeStatusTag = (() => {
    switch (barcodeStatus) {
      case "normal": return <DetailTag label={t("detail.barcodeStatus.normal")} tone="gray" />;
      case "noMatch": return <DetailTag label={t("detail.barcodeStatus.noMatch")} tone="red" />;
      case "multiMatch": return <DetailTag label={t("detail.barcodeStatus.multiMatch", { count: detail.barcodeMatchCount })} tone="orange" />;
      default: return null;
    }
  })();

  const pricingText = (() => {
    if (pricing.mode === "unset") return t("detail.pricing.empty");
    const base = pricing.mode === "auto"
      ? t("detail.pricing.autoRate", { rate: pricing.floatRateText })
      : pricing.mode === "autoNoRate" ? t("detail.pricing.auto") : t("detail.pricing.manual");
    const parts = [base];
    if (pricing.isSpecial) parts.push(t("detail.pricing.special"));
    if (pricing.discount) parts.push(t("detail.pricing.discount", { percent: pricing.discount.text }));
    return parts.join(" · ");
  })();
  const storeStatusTag = pricing.storeStatus === "hidden" ? null : (
    <DetailTag
      label={t(`detail.pricing.${pricing.storeStatus === "auto" ? "storeAuto" : pricing.storeStatus === "manual" ? "storeManual" : "storeNone"}`)}
      tone="gray"
    />
  );

  const body = (
    <View style={[styles.card, selectionMode && selected ? styles.cardSelected : null]}>
      <View style={styles.top}>
        {selectionMode ? (
          <View style={styles.check}>
            <Icon
              source={selected ? "checkbox-marked" : "checkbox-blank-outline"}
              size={22}
              color={selected ? HB_COLORS.brand : HB_COLORS.textSecondary}
            />
          </View>
        ) : null}
        {detail.productImage ? (
          <Image source={{ uri: detail.productImage }} style={styles.thumb} />
        ) : (
          <View style={[styles.thumb, styles.thumbEmpty]}>
            <Text style={styles.thumbText} numberOfLines={2}>{detail.itemNumber || t("labels.noImage")}</Text>
          </View>
        )}
        <View style={styles.topBody}>
          <Text style={styles.name} numberOfLines={2}>{detail.productName || "--"}</Text>
          {(detail.productType === 1 || detail.productType === 2 || hasAdditional) ? (
            <View style={styles.tags}>
              {detail.productType === 1 ? <DetailTag label={t("detail.productType.bundle")} tone="blue" /> : null}
              {detail.productType === 2 ? <DetailTag label={t("detail.productType.multi")} tone="purple" /> : null}
              {hasAdditional ? (
                <DetailTag label={t("detail.productType.additional", { count: detail.additionalBarcodes.length })} tone="cyan" />
              ) : null}
            </View>
          ) : null}
          <View style={styles.metaRow}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`${t("actions.copyItemNumber")} ${detail.itemNumber || "--"}`}
              onPress={() => onCopy(t("labels.itemNumber"), detail.itemNumber)}
              hitSlop={6}
            >
              <Text style={styles.meta} numberOfLines={1}>
                <Text style={edited.has("itemNumber") ? styles.metaEdited : undefined}>{detail.itemNumber || "--"}</Text>
              </Text>
            </Pressable>
            <Text style={styles.meta}> · </Text>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`${t("actions.copyBarcode")} ${detail.barcode || "--"}`}
              onPress={() => onCopy(t("labels.barcode"), detail.barcode)}
              hitSlop={6}
              style={styles.metaShrink}
            >
              <Text style={styles.meta} numberOfLines={1}>
                <Text style={edited.has("barcode") ? styles.metaEdited : undefined}>{detail.barcode || "--"}</Text>
              </Text>
            </Pressable>
          </View>
        </View>
      </View>

      <View style={styles.nums}>
        <View style={styles.num}>
          <Text style={styles.numLabel}>{t("labels.quantity")}</Text>
          <EditedValue edited={edited.has("quantity")}><Text style={styles.numValue}>{formatQuantity(detail.quantity)}</Text></EditedValue>
        </View>
        <View style={[styles.num, styles.numWide]}>
          <Text style={styles.numLabel}>{t("labels.purchasePrice")}</Text>
          <EditedValue edited={edited.has("purchasePrice")}><Text style={styles.numValue}>{formatMoney(detail.purchasePrice)}</Text></EditedValue>
          <Text style={[styles.change, priceChangeStyle]} numberOfLines={1}>{priceChangeText}</Text>
        </View>
        <View style={styles.num}>
          <Text style={styles.numLabel}>{t("detail.retailPrice")}</Text>
          <EditedValue edited={edited.has("retailPrice")}><Text style={styles.numValue}>{formatMoney(detail.retailPrice)}</Text></EditedValue>
        </View>
        <View style={styles.num}>
          <Text style={styles.numLabel}>{t("labels.subtotal")}</Text>
          <EditedValue edited={edited.has("quantity") || edited.has("purchasePrice")}><Text style={styles.numValue}>{formatMoney(detail.amount)}</Text></EditedValue>
        </View>
      </View>

      {showWorkbench ? (
        <>
          <View style={styles.line}>
            <Text style={styles.lineText} numberOfLines={1}>
              {t("detail.pricing.label")} {pricingText}
              {pricing.newAutoRetailPrice != null ? ` · ${t("detail.pricing.newPrice", { price: formatMoney(pricing.newAutoRetailPrice) })}` : ""}
            </Text>
            {storeStatusTag}
          </View>
          <View style={styles.line}>
            <View style={styles.statusTags}>{productStatusTag}{barcodeStatusTag}</View>
            <DetailTag label={t(`detail.action.${action in ACTION_TONE ? action : 0}`)} tone={ACTION_TONE[action] ?? "gray"} />
          </View>
        </>
      ) : (
        <View style={styles.line}>
          <View style={styles.statusTags}>
            <Text style={styles.lineText}>{t("detail.hq.title")}</Text>
            {productStatus === "notExists" ? <DetailTag label={t("detail.productStatus.notCreated")} tone="red" /> : null}
          </View>
          <DetailTag label={t(`detail.hq.${bucket}`)} tone={HQ_BUCKET_TONE[bucket]} />
        </View>
      )}

      <View style={styles.footer}>
        <Button compact mode="text" icon="chevron-right" contentStyle={styles.openContent} textColor={HB_COLORS.brand} onPress={() => onOpenProduct(detail)}>
          {t("actions.openProduct")}
        </Button>
      </View>
    </View>
  );

  if (selectionMode) {
    return (
      <Pressable
        accessibilityRole="checkbox"
        accessibilityState={{ checked: selected }}
        onPress={() => onToggleSelect?.(detail)}
      >
        {body}
      </Pressable>
    );
  }
  if (!canEditRow) return body;
  return (
    <Pressable accessibilityRole="button" accessibilityHint={t("detail.edit.rowTitle")} onPress={() => onPress(detail)}>
      {body}
    </Pressable>
  );
});

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    padding: HB_SPACING.sm,
    gap: HB_SPACING.xs,
  },
  cardSelected: { borderColor: HB_COLORS.brand, backgroundColor: "#F5F9FF" },
  top: { flexDirection: "row", gap: HB_SPACING.xs },
  check: { justifyContent: "center" },
  thumb: { width: 44, height: 44, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  thumbEmpty: { alignItems: "center", justifyContent: "center", padding: 2 },
  thumbText: { fontSize: 10, color: HB_COLORS.textSecondary, textAlign: "center" },
  topBody: { flex: 1, gap: 3 },
  name: { fontSize: 14, lineHeight: 19, fontWeight: "700", color: HB_COLORS.textPrimary },
  tags: { flexDirection: "row", flexWrap: "wrap", gap: 4 },
  metaRow: { flexDirection: "row", alignItems: "center" },
  metaShrink: { flexShrink: 1 },
  meta: { fontSize: 11.5, color: HB_COLORS.textSecondary },
  metaEdited: { color: HB_COLORS.action, fontWeight: "700" },
  nums: {
    flexDirection: "row",
    gap: 4,
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
    paddingTop: HB_SPACING.xs,
  },
  num: { flex: 1, gap: 1 },
  numWide: { flex: 1.5 },
  numLabel: { fontSize: 10.5, color: HB_COLORS.textSecondary },
  numValue: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  change: { fontSize: 10.5, fontWeight: "700" },
  up: { color: HB_COLORS.danger },
  upLight: { color: "#D92D20" },
  down: { color: HB_COLORS.action },
  neutral: { color: HB_COLORS.textSecondary, fontWeight: "400" },
  edited: { backgroundColor: "#EAF2FF", borderRadius: 4, paddingHorizontal: 4, alignSelf: "flex-start" },
  editedDot: { position: "absolute", right: -2, top: -2, width: 6, height: 6, borderRadius: 3, backgroundColor: HB_COLORS.brand },
  line: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  lineText: { flex: 1, fontSize: 11.5, color: HB_COLORS.textSecondary },
  statusTags: { flex: 1, flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: 4 },
  footer: { alignItems: "flex-end", marginTop: -4 },
  openContent: { flexDirection: "row-reverse", minHeight: 32 },
});
