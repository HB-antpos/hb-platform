import { useEffect, useMemo, useState } from "react";
import { ActivityIndicator, Image, Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Switch, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { DetailTag } from "@/components/local-supplier-invoices/detail-tag";
import {
  ACTION_TONE,
  formatMoney,
} from "@/components/local-supplier-invoices/invoice-detail-format";
import {
  DetailAction,
  applyInlineEdit,
  buildPricingEditorChanges,
  buildPricingEditorDraft,
  canLinkMatchedProduct,
  getBarcodeStatusFilter,
  getPricingSummary,
  getProductStatusFilter,
  getPurchasePriceChange,
  recalculateInvoiceDetailAmount,
  parseNonNegativeNumber,
  discountRateToPercent,
  type PricingEditorDraft,
} from "@/modules/local-supplier-invoices/detail";
import type { BarcodeMatchedProduct, LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

type Translate = (key: string, options?: Record<string, unknown>) => string;
type Page = "main" | "pricing" | "action" | "match";

/** 管理员可手动选择的操作类型；99「已执行」只由后端批量执行写回，不开放手选（与 Web 一致）。 */
const SELECTABLE_ACTIONS = [
  DetailAction.None,
  DetailAction.CreateProduct,
  DetailAction.UpdatePurchasePrice,
  DetailAction.WaitForOperation,
  DetailAction.UpdateItemNumber,
  DetailAction.AddMultiCode,
] as const;

const numText = (value: number | null | undefined) => (value == null ? "" : String(value));

interface EditLineSheetProps {
  /** null 表示关闭。 */
  detail: LocalSupplierInvoiceItem | null;
  /** 本单有任何未保存修改（含其他行）：选用匹配商品会重读明细，必须先保存。 */
  hasUnsavedEdit: boolean;
  /** 应用到本单（只改本地，等「保存修改」落库）。 */
  onApply: (next: LocalSupplierInvoiceItem) => void;
  /** 改操作类型：立即落库，不计入未保存修改（与 Web 一致）。 */
  onChangeAction: (detail: LocalSupplierInvoiceItem, action: number) => Promise<boolean>;
  onDelete: (detail: LocalSupplierInvoiceItem) => void;
  loadMatches: (detail: LocalSupplierInvoiceItem) => Promise<BarcodeMatchedProduct[]>;
  onLink: (detail: LocalSupplierInvoiceItem, product: BarcodeMatchedProduct) => Promise<boolean>;
  onDismiss: () => void;
  t: Translate;
}

export function EditLineSheet({
  detail,
  hasUnsavedEdit,
  onApply,
  onChangeAction,
  onDelete,
  loadMatches,
  onLink,
  onDismiss,
  t,
}: EditLineSheetProps) {
  const [page, setPage] = useState<Page>("main");
  // working 承载「定价」页已应用的改动，最后与主页字段一起应用到本单。
  const [working, setWorking] = useState<LocalSupplierInvoiceItem | null>(detail);
  const [fields, setFields] = useState({ productName: "", itemNumber: "", barcode: "", quantity: "", purchasePrice: "", retailPrice: "" });
  const [error, setError] = useState<string | null>(null);
  const [pricingDraft, setPricingDraft] = useState<PricingEditorDraft | null>(null);
  const [discountText, setDiscountText] = useState("");
  const [busy, setBusy] = useState(false);
  const [matches, setMatches] = useState<BarcodeMatchedProduct[] | null>(null);
  const [matchError, setMatchError] = useState(false);

  const guid = detail?.detailGuid ?? null;
  useEffect(() => {
    if (!detail) return;
    setPage("main");
    setWorking(detail);
    setFields({
      productName: detail.productName ?? "",
      itemNumber: detail.itemNumber ?? "",
      barcode: detail.barcode ?? "",
      quantity: numText(detail.quantity),
      purchasePrice: numText(detail.purchasePrice),
      retailPrice: numText(detail.retailPrice),
    });
    setError(null);
    setMatches(null);
    setMatchError(false);
    // 只在打开另一行时重置，避免行内异步刷新把用户输入冲掉。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [guid]);

  const current = working ?? detail;
  const amountPreview = useMemo(() => {
    const qty = parseNonNegativeNumber(fields.quantity);
    const price = parseNonNegativeNumber(fields.purchasePrice);
    return qty == null || price == null ? null : recalculateInvoiceDetailAmount(qty, price);
  }, [fields.purchasePrice, fields.quantity]);

  if (!detail || !current) return null;

  const priceChange = getPurchasePriceChange({
    ...current,
    purchasePrice: parseNonNegativeNumber(fields.purchasePrice) ?? current.purchasePrice,
  });
  const pricing = getPricingSummary(current);
  const productStatus = getProductStatusFilter(current);
  const barcodeStatus = getBarcodeStatusFilter(current);
  const canLink = canLinkMatchedProduct(current, { isAdmin: true, hasUnsavedEdit });

  const apply = () => {
    let next = current;
    if (!fields.productName.trim()) {
      setError(t("detail.edit.emptyName"));
      return;
    }
    const text: (["productName" | "itemNumber" | "barcode", string])[] = [
      ["productName", fields.productName],
      ["itemNumber", fields.itemNumber],
      ["barcode", fields.barcode],
    ];
    for (const [field, value] of text) {
      if (value.trim() !== (detail[field] ?? "")) {
        const result = applyInlineEdit(next, field, value);
        if (!result.ok) { setError(t("detail.edit.invalid")); return; }
        next = result.item;
      }
    }
    const numbers: (["quantity" | "purchasePrice" | "retailPrice", string])[] = [
      ["quantity", fields.quantity],
      ["purchasePrice", fields.purchasePrice],
      ["retailPrice", fields.retailPrice],
    ];
    for (const [field, value] of numbers) {
      // 只处理用户改过的字段：原值为空且仍留空的格子不能被当成非法输入。
      if (value.trim() === numText(detail[field])) continue;
      const result = applyInlineEdit(next, field, value);
      if (!result.ok) { setError(t("detail.edit.invalid")); return; }
      next = result.item;
    }
    setError(null);
    onApply(next);
  };

  const openPricing = () => {
    setPricingDraft(buildPricingEditorDraft(current));
    const percent = discountRateToPercent(current.discountRate);
    setDiscountText(percent == null || percent === 0 ? "" : String(percent));
    setPage("pricing");
  };

  const applyPricing = () => {
    if (!pricingDraft) return;
    const draft: PricingEditorDraft = {
      ...pricingDraft,
      discountPercent: discountText.trim() === "" ? null : parseNonNegativeNumber(discountText),
    };
    if (discountText.trim() !== "" && (draft.discountPercent == null || draft.discountPercent > 100)) {
      setError(t("detail.edit.invalid"));
      return;
    }
    let next = current;
    for (const change of buildPricingEditorChanges(current, draft)) {
      const result = applyInlineEdit(next, change.field, change.value);
      if (!result.ok) { setError(t("detail.edit.invalid")); return; }
      next = result.item;
    }
    setWorking(next);
    setError(null);
    setPage("main");
  };

  const openMatches = () => {
    setPage("match");
    if (matches || !detail.barcode) return;
    setMatchError(false);
    void loadMatches(detail)
      .then(setMatches)
      .catch(() => { setMatches([]); setMatchError(true); });
  };

  const title = page === "pricing" ? t("detail.pricingSheet.title")
    : page === "action" ? t("detail.edit.activity")
      : page === "match" ? t("detail.match.title") : t("detail.edit.rowTitle");
  const subtitle = [current.itemNumber, current.barcode].filter(Boolean).join(" · ");

  const footer = page === "main" ? (
    <View style={styles.footer}>
      <Button mode="outlined" textColor={HB_COLORS.danger} style={styles.footerSide} onPress={() => onDelete(detail)}>
        {t("detail.edit.delete")}
      </Button>
      <Button mode="contained" style={styles.footerMain} onPress={apply}>{t("detail.edit.apply")}</Button>
    </View>
  ) : page === "pricing" ? (
    <View style={styles.footer}>
      <Button mode="outlined" style={styles.footerSide} onPress={() => { setError(null); setPage("main"); }}>{t("detail.edit.cancel")}</Button>
      <Button mode="contained" style={styles.footerMain} onPress={applyPricing}>{t("detail.edit.save")}</Button>
    </View>
  ) : undefined;

  return (
    <BusinessSheet
      visible
      title={title}
      subtitle={subtitle}
      onDismiss={onDismiss}
      onBack={page === "main" ? undefined : () => { setError(null); setPage("main"); }}
      footer={footer}
    >
      {page === "main" ? (
        <View style={styles.stack}>
          <Field label={t("detail.edit.productName")} value={fields.productName} onChangeText={(v) => setFields({ ...fields, productName: v })} />
          <View style={styles.two}>
            <Field label={t("detail.edit.itemNumber")} value={fields.itemNumber} onChangeText={(v) => setFields({ ...fields, itemNumber: v })} />
            <Field label={t("detail.edit.barcode")} value={fields.barcode} keyboardType="number-pad" onChangeText={(v) => setFields({ ...fields, barcode: v })} />
          </View>
          <View style={styles.three}>
            <Field label={t("detail.edit.quantity")} value={fields.quantity} keyboardType="decimal-pad" onChangeText={(v) => setFields({ ...fields, quantity: v })} />
            <Field label={t("detail.edit.purchasePrice")} value={fields.purchasePrice} keyboardType="decimal-pad" onChangeText={(v) => setFields({ ...fields, purchasePrice: v })} />
            <Field label={t("detail.edit.retailPrice")} value={fields.retailPrice} keyboardType="decimal-pad" onChangeText={(v) => setFields({ ...fields, retailPrice: v })} />
          </View>
          {error ? <Text style={styles.error}>{error}</Text> : null}
          <Row label={t("detail.edit.amountAuto")} value={formatMoney(amountPreview)} bold />
          <Row
            label={t("detail.edit.priceVsLast")}
            value={priceChange.kind === "up" || priceChange.kind === "down"
              ? t(`detail.priceChange.${priceChange.kind}`, {
                percent: (Math.abs(priceChange.ratio ?? 0) * 100).toFixed(1),
                last: priceChange.last?.toFixed(2) ?? "--",
              })
              : priceChange.kind === "flat" ? t("detail.priceChange.flat")
                : priceChange.kind === "noHistory" ? t("detail.priceChange.noHistory") : t("detail.priceChange.noPrice")}
            tone={priceChange.kind === "up" ? "danger" : priceChange.kind === "down" ? "action" : undefined}
          />
          <PressRow
            label={t("detail.edit.pricing")}
            value={pricing.mode === "unset"
              ? t("detail.pricing.empty")
              : `${pricing.mode === "auto" ? t("detail.pricing.autoRate", { rate: pricing.floatRateText }) : pricing.mode === "autoNoRate" ? t("detail.pricing.auto") : t("detail.pricing.manual")}${pricing.newAutoRetailPrice != null ? ` · ${t("detail.pricing.newPrice", { price: formatMoney(pricing.newAutoRetailPrice) })}` : ""}`}
            onPress={openPricing}
          />
          <PressRow
            label={t("detail.edit.activity")}
            onPress={() => setPage("action")}
            valueNode={<DetailTag label={t(`detail.action.${current.activityType in ACTION_TONE ? current.activityType : 0}`)} tone={ACTION_TONE[current.activityType] ?? "gray"} />}
          />
          <PressRow
            label={t("detail.edit.match")}
            value={`${t(productStatus === "exists" ? "detail.productStatus.exists" : productStatus === "notExists" ? "detail.productStatus.notExists" : productStatus === "createdHere" ? "detail.productStatus.createdHere" : "detail.productStatus.notDetected")}${barcodeStatus === "notDetected" ? "" : ` · ${t(barcodeStatus === "normal" ? "detail.barcodeStatus.normal" : barcodeStatus === "noMatch" ? "detail.barcodeStatus.noMatch" : "detail.barcodeStatus.multiMatch", { count: current.barcodeMatchCount })}`}`}
            onPress={openMatches}
          />
        </View>
      ) : null}

      {page === "pricing" && pricingDraft ? (
        <View style={styles.stack}>
          <SwitchRow label={t("detail.pricingSheet.auto")} value={pricingDraft.autoPricing} onChange={(v) => setPricingDraft({ ...pricingDraft, autoPricing: v })} />
          <View style={styles.two}>
            <Field
              label={t("detail.pricingSheet.floatRate")}
              value={numText(pricingDraft.pricingFloatRate)}
              keyboardType="decimal-pad"
              placeholder={t("detail.pricingSheet.keepHint")}
              onChangeText={(v) => setPricingDraft({ ...pricingDraft, pricingFloatRate: v.trim() === "" ? null : parseNonNegativeNumber(v) ?? pricingDraft.pricingFloatRate })}
            />
            <Field
              label={t("detail.pricingSheet.newAutoPrice")}
              value={numText(pricingDraft.newAutoRetailPrice)}
              keyboardType="decimal-pad"
              placeholder={t("detail.pricingSheet.keepHint")}
              onChangeText={(v) => setPricingDraft({ ...pricingDraft, newAutoRetailPrice: v.trim() === "" ? null : parseNonNegativeNumber(v) ?? pricingDraft.newAutoRetailPrice })}
            />
          </View>
          <SwitchRow label={t("detail.pricingSheet.special")} value={pricingDraft.isSpecialProduct} onChange={(v) => setPricingDraft({ ...pricingDraft, isSpecialProduct: v })} />
          <Field label={t("detail.pricingSheet.discount")} value={discountText} keyboardType="decimal-pad" placeholder={t("detail.pricingSheet.keepHint")} onChangeText={setDiscountText} />
          {error ? <Text style={styles.error}>{error}</Text> : null}
          <View style={styles.note}>
            {pricing.storeStatus !== "hidden" ? (
              <Text style={styles.noteText}>
                {t("detail.pricingSheet.storeNow", {
                  state: t(`detail.pricing.${pricing.storeStatus === "auto" ? "storeAuto" : pricing.storeStatus === "manual" ? "storeManual" : "storeNone"}`),
                })}
              </Text>
            ) : null}
            <Text style={styles.noteText}>{t("detail.pricingSheet.storeNote")}</Text>
          </View>
        </View>
      ) : null}

      {page === "action" ? (
        <View style={styles.stack}>
          {SELECTABLE_ACTIONS.map((action) => (
            <Pressable
              key={action}
              accessibilityRole="radio"
              accessibilityState={{ selected: current.activityType === action, disabled: busy }}
              disabled={busy}
              style={styles.radioRow}
              onPress={() => {
                setBusy(true);
                void onChangeAction(detail, action)
                  .then((ok) => { if (ok) { setWorking({ ...current, activityType: action }); setPage("main"); } })
                  .finally(() => setBusy(false));
              }}
            >
              <DetailTag label={t(`detail.action.${action}`)} tone={ACTION_TONE[action] ?? "gray"} />
              <Text style={styles.radioMark}>{current.activityType === action ? "✓" : ""}</Text>
            </Pressable>
          ))}
        </View>
      ) : null}

      {page === "match" ? (
        <View style={styles.stack}>
          {!detail.barcode ? <Text style={styles.noteText}>{t("detail.match.noBarcode")}</Text> : null}
          {detail.barcode && matches == null ? <ActivityIndicator /> : null}
          {matchError ? <Text style={styles.error}>{t("detail.match.loadFailed")}</Text> : null}
          {matches && matches.length === 0 && !matchError ? <Text style={styles.noteText}>{t("detail.match.empty")}</Text> : null}
          {matches && matches.length > 0 && productStatus === "notExists" ? (
            <View style={[styles.note, styles.noteWarn]}>
              <Text style={styles.noteText}>
                {t("detail.match.occupied", { item: matches[0].itemNumber || matches[0].productCode, supplier: matches[0].supplierName || matches[0].supplierCode })}
              </Text>
            </View>
          ) : null}
          {matches?.map((product) => (
            <View key={product.productCode} style={styles.matchCard}>
              <View style={styles.matchTop}>
                {product.productImage ? <Image source={{ uri: product.productImage }} style={styles.matchThumb} /> : null}
                <View style={styles.matchBody}>
                  <Text style={styles.matchName} numberOfLines={2}>{product.productName || "--"}</Text>
                  <Text style={styles.matchMeta} numberOfLines={2}>
                    {product.itemNumber || product.productCode} · {t("detail.match.supplierLabel")} {product.supplierName || product.supplierCode || "--"}
                  </Text>
                </View>
                {product.itemNumber && product.itemNumber.toLowerCase() === (detail.itemNumber ?? "").toLowerCase()
                  ? <DetailTag label={t("detail.match.sameItem")} tone="green" /> : null}
              </View>
              <Button
                mode="contained"
                compact
                disabled={!canLink || busy}
                onPress={() => {
                  setBusy(true);
                  void onLink(detail, product).finally(() => setBusy(false));
                }}
              >
                {t("detail.match.use")}
              </Button>
              {!canLink && productStatus === "notExists" && hasUnsavedEdit ? <Text style={styles.matchMeta}>{t("detail.match.blockedUnsaved")}</Text> : null}
            </View>
          ))}
        </View>
      ) : null}
    </BusinessSheet>
  );
}

function Field({
  label,
  value,
  onChangeText,
  keyboardType,
  placeholder,
}: {
  label: string;
  value: string;
  onChangeText: (value: string) => void;
  keyboardType?: "number-pad" | "decimal-pad";
  placeholder?: string;
}) {
  return (
    <View style={styles.field}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <TextInput
        accessibilityLabel={label}
        value={value}
        onChangeText={onChangeText}
        keyboardType={keyboardType}
        placeholder={placeholder}
        placeholderTextColor={HB_COLORS.textSecondary}
        style={styles.input}
        autoCorrect={false}
      />
    </View>
  );
}

function Row({ label, value, bold, tone }: { label: string; value: string; bold?: boolean; tone?: "danger" | "action" }) {
  return (
    <View style={styles.row}>
      <Text style={styles.rowLabel}>{label}</Text>
      <Text style={[styles.rowValue, bold ? styles.bold : null, tone === "danger" ? styles.danger : tone === "action" ? styles.action : null]}>{value}</Text>
    </View>
  );
}

function PressRow({ label, value, valueNode, onPress }: { label: string; value?: string; valueNode?: React.ReactNode; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" style={styles.row} onPress={onPress}>
      <Text style={styles.rowLabel}>{label}</Text>
      <View style={styles.rowRight}>
        {valueNode ?? <Text style={styles.rowValue} numberOfLines={1}>{value}</Text>}
        <Text style={styles.chevron}>›</Text>
      </View>
    </Pressable>
  );
}

function SwitchRow({ label, value, onChange }: { label: string; value: boolean; onChange: (value: boolean) => void }) {
  return (
    <View style={styles.row}>
      <Text style={styles.rowLabel}>{label}</Text>
      <Switch value={value} onValueChange={onChange} />
    </View>
  );
}

const styles = StyleSheet.create({
  stack: { gap: HB_SPACING.xs },
  two: { flexDirection: "row", gap: HB_SPACING.xs },
  three: { flexDirection: "row", gap: HB_SPACING.xs },
  field: { flex: 1, gap: 3 },
  fieldLabel: { fontSize: 11.5, color: HB_COLORS.textSecondary },
  input: {
    height: 40,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: 10,
    fontSize: 14,
    color: HB_COLORS.textPrimary,
    backgroundColor: HB_COLORS.white,
  },
  row: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  rowLabel: { fontSize: 13, color: HB_COLORS.textPrimary },
  rowRight: { flexDirection: "row", alignItems: "center", gap: 4, flexShrink: 1 },
  rowValue: { fontSize: 13, color: HB_COLORS.textSecondary, flexShrink: 1, textAlign: "right" },
  chevron: { fontSize: 18, color: HB_COLORS.textSecondary },
  bold: { fontWeight: "700", color: HB_COLORS.textPrimary },
  danger: { color: HB_COLORS.danger, fontWeight: "700" },
  action: { color: HB_COLORS.action, fontWeight: "700" },
  error: { fontSize: 12, color: HB_COLORS.danger },
  note: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.control, padding: HB_SPACING.xs, gap: 4 },
  noteWarn: { backgroundColor: "#FFFAEB" },
  noteText: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  radioRow: { minHeight: 44, flexDirection: "row", alignItems: "center", justifyContent: "space-between", borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  radioMark: { fontSize: 16, color: HB_COLORS.action, fontWeight: "700" },
  matchCard: { borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, padding: HB_SPACING.xs, gap: HB_SPACING.xs, backgroundColor: HB_COLORS.white },
  matchTop: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "flex-start" },
  matchThumb: { width: 40, height: 40, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  matchBody: { flex: 1, gap: 2 },
  matchName: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  matchMeta: { fontSize: 11.5, color: HB_COLORS.textSecondary },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerSide: { flex: 1 },
  footerMain: { flex: 2 },
});
