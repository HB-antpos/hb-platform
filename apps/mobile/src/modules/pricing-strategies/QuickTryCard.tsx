import { useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text, TextInput } from "react-native-paper";
import { OptionPickerSheet } from "@/components/ui/OptionPickerSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { evaluatePricing } from "./api";
import { money, parseDecimal } from "./logic";
import { PRICING_COLORS, useApiErrorText } from "./shared";
import type { NamedOption, PricingEvaluateResult } from "./types";

/**
 * 快速试算：选分店、供应商（都可不选），输入进货价，看命中哪条策略、零售价与实际成率。
 * 结果里的策略名可以点，直接进入该策略的编辑页（与 Web 一致）。
 */
export function QuickTryCard({
  stores,
  suppliers,
  onOpenStrategy,
}: {
  stores: NamedOption[];
  suppliers: NamedOption[];
  onOpenStrategy: (id: string) => void;
}) {
  const { t } = useAppTranslation("pricingStrategies");
  const errorText = useApiErrorText();
  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [supplierCode, setSupplierCode] = useState<string | null>(null);
  const [priceText, setPriceText] = useState("");
  const [picker, setPicker] = useState<"store" | "supplier" | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<PricingEvaluateResult | null>(null);
  const [error, setError] = useState("");
  const storeName = stores.find((store) => store.code === storeCode)?.name ?? storeCode;
  const supplierName = suppliers.find((supplier) => supplier.code === supplierCode)?.name ?? supplierCode;

  const calculate = async () => {
    const purchasePrice = parseDecimal(priceText);
    if (!(purchasePrice > 0)) {
      setError(t("quickTry.invalidPrice"));
      setResult(null);
      return;
    }
    setBusy(true);
    setError("");
    try {
      setResult(await evaluatePricing({ purchasePrice, storeCode, supplierCode }));
    } catch (cause) {
      setResult(null);
      setError(errorText(cause, "quickTry.failed"));
    } finally {
      setBusy(false);
    }
  };

  // 选择器里第一项「不限」用空字符串表示，选中后回到 null。
  const anyValue = "";
  const pickerOptions =
    picker === "store"
      ? [{ value: anyValue, label: t("quickTry.anyStore") }, ...stores.map((store) => ({ value: store.code, label: store.name }))]
      : [{ value: anyValue, label: t("quickTry.anySupplier") }, ...suppliers.map((supplier) => ({ value: supplier.code, label: supplier.name }))];

  return (
    <View style={styles.card}>
      <View style={styles.titleRow}>
        <Text style={styles.title}>{t("quickTry.title")}</Text>
        <Text style={styles.hint}>{t("quickTry.hint")}</Text>
      </View>
      <View style={styles.row}>
        <SelectBox label={storeName ?? t("quickTry.anyStore")} placeholder={!storeCode} onPress={() => setPicker("store")} accessibilityLabel={t("quickTry.store")} />
        <SelectBox
          label={supplierName ?? t("quickTry.anySupplier")}
          placeholder={!supplierCode}
          onPress={() => setPicker("supplier")}
          accessibilityLabel={t("quickTry.supplier")}
        />
      </View>
      <View style={styles.row}>
        <TextInput
          mode="outlined"
          dense
          value={priceText}
          onChangeText={setPriceText}
          onSubmitEditing={() => void calculate()}
          keyboardType="decimal-pad"
          returnKeyType="done"
          placeholder={t("quickTry.purchasePrice")}
          accessibilityLabel={t("quickTry.purchasePrice")}
          right={<TextInput.Affix text={t("quickTry.purchasePrice")} />}
          style={styles.priceInput}
          outlineStyle={styles.outline}
        />
        <Button mode="contained" onPress={() => void calculate()} loading={busy} disabled={busy} style={styles.calcButton}>
          {t("actions.calculate")}
        </Button>
      </View>
      {error ? (
        <Text style={styles.error} accessibilityLiveRegion="polite">
          {error}
        </Text>
      ) : null}
      {result ? (
        <View style={styles.result} accessibilityLiveRegion="polite">
          <View>
            <Text style={styles.resultLabel}>{t("quickTry.retailPrice")}</Text>
            <Text style={styles.resultPrice}>{money(result.retailPrice)}</Text>
          </View>
          <View style={styles.resultMeta}>
            <Text style={styles.resultLine}>
              {`${t("quickTry.rate")} `}
              <Text style={styles.bold}>{result.effectiveRate.toFixed(4)}</Text>
            </Text>
            {result.strategyId ? (
              <Pressable accessibilityRole="link" onPress={() => onOpenStrategy(result.strategyId!)} hitSlop={6}>
                <Text style={styles.resultLine}>
                  {`${t("quickTry.hit")} `}
                  <Text style={styles.link}>{`${result.strategyName ?? result.strategyId} ›`}</Text>
                </Text>
              </Pressable>
            ) : (
              <Text style={styles.resultLine}>{t("quickTry.noHit")}</Text>
            )}
          </View>
        </View>
      ) : null}
      <OptionPickerSheet
        visible={picker !== null}
        title={picker === "store" ? t("quickTry.store") : t("quickTry.supplier")}
        cancelLabel={t("actions.cancel")}
        options={pickerOptions}
        selectedValue={(picker === "store" ? storeCode : supplierCode) ?? anyValue}
        searchable
        onSelect={(value) => {
          if (picker === "store") setStoreCode(value || null);
          else setSupplierCode(value || null);
          setPicker(null);
          setResult(null);
        }}
        onDismiss={() => setPicker(null)}
      />
    </View>
  );
}

function SelectBox({ label, placeholder, onPress, accessibilityLabel }: { label: string; placeholder: boolean; onPress: () => void; accessibilityLabel: string }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={`${accessibilityLabel}: ${label}`} onPress={onPress} style={styles.select}>
      <Text numberOfLines={1} style={[styles.selectText, placeholder ? styles.placeholder : null]}>
        {label}
      </Text>
      <Icon source="chevron-down" size={16} color={PRICING_COLORS.muted} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: HB_COLORS.surface,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
    marginHorizontal: HB_SPACING.md,
    marginBottom: HB_SPACING.sm,
    gap: HB_SPACING.xs,
  },
  titleRow: { flexDirection: "row", justifyContent: "space-between", alignItems: "baseline", gap: HB_SPACING.xs },
  title: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  hint: { flexShrink: 1, fontSize: 12, color: PRICING_COLORS.muted },
  row: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "center" },
  select: {
    flex: 1,
    minWidth: 0,
    height: 40,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.white,
    paddingHorizontal: 10,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
  },
  selectText: { flex: 1, fontSize: 14, color: HB_COLORS.textPrimary },
  placeholder: { color: PRICING_COLORS.muted },
  priceInput: { flex: 1, backgroundColor: HB_COLORS.white, fontVariant: ["tabular-nums"] },
  outline: { borderRadius: HB_RADIUS.control },
  calcButton: { borderRadius: HB_RADIUS.control, marginTop: 6 },
  error: { fontSize: 12, color: HB_COLORS.danger },
  result: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, padding: 10, borderRadius: HB_RADIUS.control, backgroundColor: PRICING_COLORS.blueBg },
  resultLabel: { fontSize: 12, color: PRICING_COLORS.muted },
  resultPrice: { fontSize: 26, lineHeight: 30, fontWeight: "700", color: HB_COLORS.brand, fontVariant: ["tabular-nums"] },
  resultMeta: { flex: 1, minWidth: 0, gap: 2 },
  resultLine: { fontSize: 12, color: HB_COLORS.textSecondary },
  bold: { fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  link: { color: HB_COLORS.brand, fontWeight: "600" },
});
