import { useEffect, useMemo, useState } from "react";
import { ActivityIndicator, Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { fetchActiveLocalSuppliers } from "@/modules/local-supplier-invoices/api";
import {
  buildHeaderFormValues,
  buildHeaderSavePayload,
  isHeaderDirty,
  isValidDateOnly,
  type InvoiceHeaderFormValues,
} from "@/modules/local-supplier-invoices/detail";
import type { LocalSupplierInvoice, UpdateInvoiceHeaderRequest } from "@/modules/local-supplier-invoices/types";
import type { Store } from "@/modules/shop/types";
import { sortSuppliersByName } from "@/shared/utils/name-sort";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

type Translate = (key: string, options?: Record<string, unknown>) => string;
type Page = "main" | "store" | "supplier";

/**
 * 单据表头编辑（需 LocalPurchase.Edit）：分店、供应商、订单日期、入库日期、备注。
 * 与 Web 一致：只提交这 5 项；改分店 / 供应商后明细会由后端级联，保存后调用方要重读明细。
 */
export function HeaderEditSheet({
  visible,
  invoice,
  stores,
  language,
  saving,
  onSave,
  onDismiss,
  t,
}: {
  visible: boolean;
  invoice: LocalSupplierInvoice;
  stores: Store[];
  language: string;
  saving: boolean;
  onSave: (payload: UpdateInvoiceHeaderRequest) => void;
  onDismiss: () => void;
  t: Translate;
}) {
  const [values, setValues] = useState<InvoiceHeaderFormValues>(() => buildHeaderFormValues(invoice));
  const [page, setPage] = useState<Page>("main");
  const [suppliers, setSuppliers] = useState<{ supplierCode: string; supplierName: string }[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!visible) return;
    setValues(buildHeaderFormValues(invoice));
    setPage("main");
    setError(null);
    // 打开时用最新表头重置；invoice 在编辑期间被刷新不应冲掉输入，所以只依赖 visible。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);

  useEffect(() => {
    if (!visible || page !== "supplier" || suppliers) return;
    let cancelled = false;
    void fetchActiveLocalSuppliers()
      .then((list) => { if (!cancelled) setSuppliers(sortSuppliersByName(list, language)); })
      .catch(() => { if (!cancelled) setSuppliers([]); });
    return () => { cancelled = true; };
  }, [language, page, suppliers, visible]);

  const dirty = isHeaderDirty(values, invoice);
  const storeName = useMemo(
    () => stores.find((store) => store.storeCode === values.storeCode)?.storeName || values.storeCode || "--",
    [stores, values.storeCode]
  );
  const supplierName = useMemo(
    () => suppliers?.find((s) => s.supplierCode === values.supplierCode)?.supplierName
      || (values.supplierCode === invoice.supplierCode ? invoice.supplierName : "")
      || values.supplierCode
      || "--",
    [invoice.supplierCode, invoice.supplierName, suppliers, values.supplierCode]
  );

  const save = () => {
    if (!isValidDateOnly(values.orderDate)) { setError(t("detail.edit.dateFormatHint")); return; }
    if (values.inboundDate.trim() && !isValidDateOnly(values.inboundDate.trim())) { setError(t("detail.edit.dateFormatHint")); return; }
    setError(null);
    onSave(buildHeaderSavePayload(values));
  };

  return (
    <BusinessSheet
      visible={visible}
      title={page === "store" ? t("detail.edit.pickStore") : page === "supplier" ? t("detail.edit.pickSupplier") : t("detail.edit.headerTitle")}
      subtitle={invoice.invoiceNo}
      onDismiss={onDismiss}
      onBack={page === "main" ? undefined : () => setPage("main")}
      footer={page === "main" ? (
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.side} onPress={onDismiss} disabled={saving}>{t("detail.edit.cancel")}</Button>
          <Button mode="contained" style={styles.main} onPress={save} disabled={!dirty || saving} loading={saving}>{t("detail.edit.save")}</Button>
        </View>
      ) : undefined}
    >
      {page === "main" ? (
        <View style={styles.stack}>
          <PickRow label={t("detail.edit.store")} value={storeName} onPress={() => setPage("store")} />
          <PickRow label={t("detail.edit.supplier")} value={supplierName} onPress={() => setPage("supplier")} />
          <View style={styles.two}>
            <TextField label={t("detail.edit.orderDate")} value={values.orderDate} onChangeText={(v) => setValues({ ...values, orderDate: v })} placeholder="YYYY-MM-DD" />
            <TextField label={t("detail.edit.inboundDate")} value={values.inboundDate} onChangeText={(v) => setValues({ ...values, inboundDate: v })} placeholder={t("detail.edit.inboundEmpty")} />
          </View>
          <TextField label={t("detail.edit.remarks")} value={values.remarks} onChangeText={(v) => setValues({ ...values, remarks: v })} multiline />
          {error ? <Text style={styles.error}>{error}</Text> : null}
        </View>
      ) : null}

      {page === "store" ? (
        <View>
          {stores.length === 0 ? <Text style={styles.empty}>{t("detail.edit.noOptions")}</Text> : null}
          {stores.map((store) => (
            <OptionRow
              key={store.storeCode}
              label={store.storeName || store.storeCode}
              selected={store.storeCode === values.storeCode}
              onPress={() => { setValues({ ...values, storeCode: store.storeCode }); setPage("main"); }}
            />
          ))}
        </View>
      ) : null}

      {page === "supplier" ? (
        <View>
          {suppliers == null ? <ActivityIndicator /> : null}
          {suppliers && suppliers.length === 0 ? <Text style={styles.empty}>{t("detail.edit.noOptions")}</Text> : null}
          {suppliers?.map((supplier) => (
            <OptionRow
              key={supplier.supplierCode}
              label={supplier.supplierName || supplier.supplierCode}
              selected={supplier.supplierCode === values.supplierCode}
              onPress={() => { setValues({ ...values, supplierCode: supplier.supplierCode }); setPage("main"); }}
            />
          ))}
        </View>
      ) : null}
    </BusinessSheet>
  );
}

function PickRow({ label, value, onPress }: { label: string; value: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.pick}>
      <Text style={styles.label}>{label}</Text>
      <View style={styles.pickRight}>
        <Text style={styles.pickValue} numberOfLines={1}>{value}</Text>
        <Text style={styles.chevron}>›</Text>
      </View>
    </Pressable>
  );
}

function OptionRow({ label, selected, onPress }: { label: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="radio" accessibilityState={{ selected }} onPress={onPress} style={styles.option}>
      <Text style={[styles.pickValue, selected ? styles.selected : null]} numberOfLines={1}>{label}</Text>
      <Text style={styles.selected}>{selected ? "✓" : ""}</Text>
    </Pressable>
  );
}

function TextField({
  label,
  value,
  onChangeText,
  placeholder,
  multiline,
}: {
  label: string;
  value: string;
  onChangeText: (value: string) => void;
  placeholder?: string;
  multiline?: boolean;
}) {
  return (
    <View style={styles.field}>
      <Text style={styles.label}>{label}</Text>
      <TextInput
        accessibilityLabel={label}
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={HB_COLORS.textSecondary}
        multiline={multiline}
        autoCorrect={false}
        style={[styles.input, multiline ? styles.inputMulti : null]}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  stack: { gap: HB_SPACING.xs },
  two: { flexDirection: "row", gap: HB_SPACING.xs },
  field: { flex: 1, gap: 3 },
  label: { fontSize: 12, color: HB_COLORS.textSecondary },
  input: {
    minHeight: 40,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: 10,
    fontSize: 14,
    color: HB_COLORS.textPrimary,
    backgroundColor: HB_COLORS.white,
  },
  inputMulti: { minHeight: 72, paddingTop: 8, textAlignVertical: "top" },
  pick: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  pickRight: { flexDirection: "row", alignItems: "center", gap: 4, flexShrink: 1 },
  pickValue: { fontSize: 14, color: HB_COLORS.textPrimary, flexShrink: 1 },
  chevron: { fontSize: 18, color: HB_COLORS.textSecondary },
  option: { minHeight: 48, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  selected: { color: HB_COLORS.action, fontWeight: "700" },
  empty: { fontSize: 13, color: HB_COLORS.textSecondary, paddingVertical: HB_SPACING.sm },
  error: { fontSize: 12, color: HB_COLORS.danger },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  side: { flex: 1 },
  main: { flex: 2 },
});
