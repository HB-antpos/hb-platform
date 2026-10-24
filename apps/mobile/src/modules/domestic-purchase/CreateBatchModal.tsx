import { useCallback, useMemo, useState } from "react";
import { FlatList, KeyboardAvoidingView, Modal, Platform, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, IconButton, SegmentedButtons, Text, TextInput, TouchableRipple } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { CreateBatchKeyboard, CreateBatchNumberInput } from "./CreateBatchNumberInput";
import { CreateBatchProductEditor } from "./CreateBatchProductEditor";
import { newProduct, validSubItems, type ProductDraft } from "./create-batch-draft";
import { sortSuppliersByName } from "@/shared/utils/name-sort";
import { ProductCreationType } from "./types";
import { useCreateBatch, type CreateBatchCallbacks } from "./use-create-batch";

/** 创建和选择共用一个原生 Modal，避免 Portal 菜单出现在遮罩下面。 */
export function CreateBatchModal(props: CreateBatchCallbacks) {
  const form = useCreateBatch(props);
  const { t, language } = form;
  const insets = useSafeAreaInsets();
  // 供应商选择列表按名字排序（接口原序不保证顺序）。
  const sortedSuppliers = useMemo(() => sortSuppliersByName(form.suppliers, language), [form.suppliers, language]);
  const [batchType, setBatchType] = useState(ProductCreationType.Normal);
  const [batchCount, setBatchCount] = useState("5");
  const [batchPrice, setBatchPrice] = useState("");
  const [batchMode, setBatchMode] = useState<"append" | "overwrite">("append");
  const [overwriteConfirmed, setOverwriteConfirmed] = useState(false);
  const filtering = form.query.trim().toLocaleLowerCase();
  const steps = [t("wizard.stepSupplier"), t("wizard.stepProducts"), t("wizard.stepConfirm")];
  const panelTitle = form.panel ? t(`wizard.panels.${form.panel}`) : t("create.title");
  const supplierLabel = form.supplier ? `${form.supplier.supplierCode} · ${form.supplier.supplierName}` : t("create.selectSupplier");
  const prefixLabel = form.prefix ? [form.prefix.prefixName, form.prefix.prefixDescription].filter(Boolean).join(" · ") : t("create.noPrefix");
  const choosing = form.panel === "supplier" || form.panel === "prefix" || form.panel === "templates";

  const openBatch = () => {
    setBatchType(ProductCreationType.Normal); setBatchCount("5"); setBatchPrice(""); setBatchMode("append"); setOverwriteConfirmed(false);
    form.openPanel("batch");
  };
  const batchAction = () => {
    if (batchMode === "overwrite" && !overwriteConfirmed) { setOverwriteConfirmed(true); return; }
    form.addBatch(batchType, batchCount, batchPrice, batchMode);
  };

  const { setProducts } = form;
  // 只更新目标草稿，保持其他行引用和回调稳定，避免批量录入时每次键入重绘全部输入框。
  const changeProduct = useCallback((key: string, patch: Partial<ProductDraft>) => {
    setProducts((items) => items.map((item) => item.key === key ? { ...item, ...patch } : item));
  }, [setProducts]);
  const deleteProduct = useCallback((key: string) => {
    setProducts((items) => items.filter((item) => item.key !== key));
  }, [setProducts]);

  const options = form.panel === "supplier"
    ? sortedSuppliers.map((supplier) => ({ key: supplier.supplierCode, title: `${supplier.supplierCode} · ${supplier.supplierName}`, description: "", selected: supplier.supplierCode === form.supplier?.supplierCode, select: () => form.selectSupplier(supplier) }))
    : form.panel === "prefix"
      ? [{ key: "__none__", title: t("create.noPrefix"), description: "", selected: !form.prefix, select: () => form.selectPrefix(null) }, ...form.prefixes.map((prefix) => ({ key: prefix.prefixCode || prefix.prefixName, title: prefix.prefixName || prefix.prefixCode, description: prefix.prefixDescription || "", selected: Boolean(form.prefix && prefix.prefixCode === form.prefix.prefixCode && prefix.prefixName === form.prefix.prefixName), select: () => form.selectPrefix(prefix) }))]
      : form.templates.map((template) => ({ key: template.templateId, title: template.templateName, description: `${template.setProductName} · ${t("wizard.subItems", { count: template.setQuantity })}`, selected: false, select: () => void form.apply(template.templateId) }));
  const filtered = options.filter((item) => `${item.title} ${item.description}`.toLocaleLowerCase().includes(filtering));
  const optionLoading = form.panel === "supplier" ? form.supplierLoading : form.panel === "prefix" ? form.prefixLoading : form.templateLoading;
  const optionError = form.panel === "supplier" ? form.supplierError : form.panel === "prefix" ? form.prefixError : form.templateError;
  const retryOptions = () => { if (form.panel === "supplier") void form.loadSuppliers(); else if (form.panel === "prefix") void form.loadPrefixes(); else void form.loadTemplates(); };

  return <Modal visible transparent animationType="slide" onRequestClose={form.dismiss} statusBarTranslucent>
    <View style={[styles.overlay, { paddingTop: insets.top + 8, paddingBottom: Math.max(insets.bottom, 8) }]}>
      <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : "height"} style={styles.frame}>
        <CreateBatchKeyboard nextLabel={t("create.next")} doneLabel={t("create.done")}>
          <View style={styles.surface}>
            <View style={styles.header}>
              <View style={styles.grow}>
                <Text variant="titleMedium" style={styles.title}>{panelTitle}</Text>
                {!form.panel ? <Text variant="bodySmall" style={styles.muted}>{t("wizard.stepProgress", { step: form.step + 1 })} · {steps[form.step]}</Text> : null}
              </View>
              <IconButton icon="close" disabled={form.busy} onPress={form.dismiss} accessibilityLabel={t(form.creationUncertain ? "wizard.returnToList" : "actions.cancel")} />
            </View>
            {!form.panel ? <View style={styles.progress}>{steps.map((_, index) => <View key={index} style={[styles.progressSegment, index <= form.step && styles.progressActive]} />)}</View> : null}

            <View style={styles.body} pointerEvents={form.busy || form.creationUncertain ? "none" : "auto"}>
              {choosing ? <View style={styles.selector}>
                <TextInput mode="outlined" dense label={t(form.panel === "supplier" ? "wizard.searchSupplier" : "wizard.searchOptions")} value={form.query} onChangeText={form.setQuery} autoCorrect={false} style={styles.search} right={<TextInput.Icon icon="magnify" />} />
                {optionLoading ? <View style={styles.feedback}><ActivityIndicator /><Text>{t("wizard.loading")}</Text></View>
                  : optionError ? <View style={styles.feedback}><Text style={styles.error}>{optionError}</Text><Button onPress={retryOptions}>{t("common:actions.retry")}</Button></View>
                  : <FlatList
                    data={filtered} keyExtractor={(item) => item.key} keyboardShouldPersistTaps="handled" keyboardDismissMode="on-drag"
                    contentContainerStyle={filtered.length ? undefined : styles.emptyList}
                    ListEmptyComponent={<View style={styles.feedback}><Text style={styles.muted}>{t(form.panel === "templates" && !filtering ? "wizard.noTemplates" : "wizard.noOptions")}</Text></View>}
                    renderItem={({ item }) => <TouchableRipple onPress={item.select} accessibilityRole="button" accessibilityState={{ selected: item.selected }} style={styles.option}>
                      <View style={styles.optionContent}>
                        <View style={styles.grow}><Text variant="bodyLarge">{item.title}</Text>{item.description ? <Text style={styles.muted}>{item.description}</Text> : null}</View>
                        <Icon source={item.selected ? "check-circle" : "chevron-right"} size={22} color={item.selected ? "#0958D9" : "#667085"} />
                      </View>
                    </TouchableRipple>}
                  />}
              </View> : !form.panel && form.step === 1 ? <FlatList
                data={form.products} keyExtractor={(item) => item.key}
                contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled" keyboardDismissMode="on-drag"
                initialNumToRender={3} maxToRenderPerBatch={3} windowSize={5} removeClippedSubviews={false}
                ListHeaderComponent={<View style={styles.productHeader}>
                  <Text style={styles.muted}>{supplierLabel} · {prefixLabel}</Text>
                  <View style={styles.tools}>
                    <Button mode="outlined" icon="plus" onPress={() => form.setProducts((items) => [...items, newProduct()])}>{t("wizard.addNormal")}</Button>
                    <Button mode="outlined" icon="plus" onPress={() => form.setProducts((items) => [...items, newProduct(ProductCreationType.Set)])}>{t("wizard.addSet")}</Button>
                    <Button onPress={openBatch}>{t("wizard.batchAdd")}</Button>
                    <Button icon="file-document-outline" onPress={() => form.openPanel("templates")}>{t("wizard.selectTemplate")}</Button>
                  </View>
                  <Text style={styles.muted}>{t("wizard.productHint")}</Text>
                </View>}
                ListEmptyComponent={<Text style={styles.muted}>{t("wizard.errors.emptyProducts")}</Text>}
                renderItem={({ item, index }) => <CreateBatchProductEditor product={item} index={index}
                  onChange={changeProduct} onDelete={deleteProduct} onSaveTemplate={form.openSaveTemplate} />}
              /> : <ScrollView key={`${form.panel}-${form.step}`} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled" keyboardDismissMode="on-drag">
                {form.panel === "batch" ? <>
                  <SegmentedButtons value={String(batchType)} onValueChange={(value) => { setBatchType(Number(value)); setOverwriteConfirmed(false); }} buttons={[{ value: "0", label: t("types.normal") }, { value: "1", label: t("types.set") }]} />
                  <CreateBatchNumberInput id="batch-count" order={0} integer label={t("create.count")} value={batchCount} onChangeText={(value) => { setBatchCount(value); setOverwriteConfirmed(false); }} />
                  <CreateBatchNumberInput id="batch-price" order={1} last label={t("create.privateLabelPrice")} value={batchPrice} onChangeText={(value) => { setBatchPrice(value); setOverwriteConfirmed(false); }} />
                  <Text style={styles.muted}>{t("wizard.batchHint")}</Text>
                  <SegmentedButtons value={batchMode} onValueChange={(value) => { setBatchMode(value as "append" | "overwrite"); setOverwriteConfirmed(false); }} buttons={[{ value: "append", label: t("wizard.append") }, { value: "overwrite", label: t("wizard.overwrite") }]} />
                  {overwriteConfirmed ? <Text accessibilityRole="alert" style={styles.error}>{t("wizard.overwriteWarning", { count: form.products.length })}</Text> : null}
                </> : form.panel === "saveTemplate" ? <>
                  <Text style={styles.muted}>{supplierLabel}</Text>
                  <TextInput mode="outlined" label={t("wizard.templateName")} value={form.templateName} onChangeText={form.setTemplateName} />
                  <Text style={styles.muted}>{t("wizard.templateHint")}</Text>
                </> : form.step === 0 ? <>
                  <Text variant="bodyMedium" style={styles.muted}>{t("wizard.supplierHint")}</Text>
                  <Choice title={t("create.selectSupplier")} value={supplierLabel} onPress={() => form.openPanel("supplier")} />
                  {form.supplierError ? <Text style={styles.error}>{form.supplierError}</Text> : null}
                  <Choice title={t("create.selectPrefix")} value={prefixLabel} disabled={!form.supplier} onPress={() => form.openPanel("prefix")} />
                  {form.prefixLoading ? <ActivityIndicator size="small" /> : null}
                  {form.prefixError ? <Text style={styles.error}>{form.prefixError}</Text> : null}
                  <Text style={styles.muted}>{t("wizard.prefixHint")}</Text>
                </> : <>
                  <Text variant="titleSmall">{supplierLabel}</Text>
                  <Text style={styles.muted}>{prefixLabel}</Text>
                  <View style={styles.summary}>
                    <Text>{t("wizard.summary", form.totals)}</Text>
                    <Text variant="titleMedium" style={styles.total}>{t("wizard.total", { count: form.totals.total })}</Text>
                  </View>
                  <Text style={styles.muted}>{t("wizard.confirmHint")}</Text>
                  {form.products.map((product, index) => <View key={product.key} style={styles.reviewItem}>
                    <Text variant="titleSmall">{index + 1}. {product.productName || t("wizard.unnamed")} · {t(product.productType === ProductCreationType.Set ? "types.set" : "types.normal")}</Text>
                    <Text style={styles.muted}>{t("fields.privateLabelPrice", { value: product.privateLabelPrice.trim() || "—" })}</Text>
                    {product.productType === ProductCreationType.Set ? <>
                      <Text>{t("wizard.setSummary", { count: Number(product.createCount), quantity: Number(product.setQuantity), price: product.setPrice.trim() || "—" })}</Text>
                      {validSubItems(product).map((sub, subIndex) => <Text key={sub.key} style={styles.reviewChild}>{subIndex + 1}. {sub.productName || t("wizard.unnamed")} · {t("fields.privateLabelPrice", { value: sub.privateLabelPrice.trim() || "—" })}</Text>)}
                    </> : null}
                  </View>)}
                </>}
              </ScrollView>}
            </View>

            <View style={styles.footer}>
              {form.notice ? <Text accessibilityRole="alert" style={styles.notice}>{form.notice}</Text> : null}
              {form.busy ? <Text style={styles.muted}>{t("wizard.processing")}</Text> : null}
              {form.creationUncertain ? <View style={styles.uncertainAction}>
                <Button mode="contained" icon="format-list-bulleted" onPress={form.returnToList} contentStyle={styles.button}>{t("wizard.returnToList")}</Button>
              </View> : <View style={styles.footerButtons}>
                <Button disabled={form.busy} onPress={form.panel || form.step > 0 ? form.back : form.dismiss} contentStyle={styles.button}>{t(form.panel || form.step > 0 ? "wizard.back" : "actions.cancel")}</Button>
                {!choosing ? <Button mode="contained" loading={form.busy} disabled={form.busy} contentStyle={styles.button}
                  onPress={form.panel === "batch" ? batchAction : form.panel === "saveTemplate" ? () => void form.saveTemplate() : form.step === 2 ? () => void form.submit() : form.next}>
                  {t(form.panel === "batch" ? overwriteConfirmed ? "wizard.confirmOverwrite" : "wizard.add" : form.panel === "saveTemplate" ? "wizard.saveTemplate" : form.step === 2 ? "actions.confirmCreate" : "create.next")}
                </Button> : null}
              </View>}
            </View>
          </View>
        </CreateBatchKeyboard>
      </KeyboardAvoidingView>
    </View>
  </Modal>;
}

function Choice({ title, value, onPress, disabled = false }: { title: string; value: string; onPress: () => void; disabled?: boolean }) {
  return <TouchableRipple onPress={onPress} disabled={disabled} accessibilityRole="button" accessibilityLabel={`${title}: ${value}`} style={[styles.choice, disabled && styles.disabled]}>
    <View style={styles.optionContent}>
      <View style={styles.grow}><Text variant="labelMedium" style={styles.muted}>{title}</Text><Text variant="bodyLarge">{value}</Text></View>
      <Icon source="chevron-right" size={24} color="#0958D9" />
    </View>
  </TouchableRipple>;
}

const styles = StyleSheet.create({
  overlay: { flex: 1, backgroundColor: "rgba(16,24,40,0.5)", paddingHorizontal: 8, alignItems: "center" },
  frame: { flex: 1, width: "100%", maxWidth: 640 },
  surface: { flex: 1, backgroundColor: "#FFFFFF", borderRadius: 18, overflow: "hidden", borderWidth: 1, borderColor: "#E4E7EC" },
  header: { flexDirection: "row", alignItems: "center", paddingLeft: 16, paddingRight: 4, paddingTop: 8, paddingBottom: 8, borderBottomWidth: StyleSheet.hairlineWidth, borderColor: "#EAECF0" },
  title: { fontWeight: "700", marginBottom: 4, color: "#101828" },
  grow: { flex: 1, gap: 4 },
  muted: { color: "#667085", lineHeight: 20 },
  progress: { flexDirection: "row", gap: 6, paddingHorizontal: 16, paddingBottom: 12 },
  progressSegment: { height: 3, flex: 1, borderRadius: 2, backgroundColor: "#EAECF0" },
  progressActive: { backgroundColor: "#0958D9" },
  body: { flex: 1, backgroundColor: "#F4F6F8" },
  content: { padding: 16, gap: 12, paddingBottom: 24 },
  selector: { flex: 1 },
  search: { margin: 12, backgroundColor: "#FFFFFF" },
  feedback: { alignItems: "center", justifyContent: "center", padding: 24, gap: 12 },
  emptyList: { flexGrow: 1, justifyContent: "center" },
  option: { backgroundColor: "#FFFFFF", paddingHorizontal: 16, paddingVertical: 12, borderBottomWidth: StyleSheet.hairlineWidth, borderColor: "#EAECF0" },
  optionContent: { flexDirection: "row", gap: 12, alignItems: "center", minHeight: 44 },
  choice: { padding: 14, borderRadius: 8, borderWidth: 1, borderColor: "#D0D5DD", backgroundColor: "#FFFFFF", minHeight: 64 },
  disabled: { opacity: 0.45 },
  productHeader: { gap: 12, padding: 12, borderRadius: 12, backgroundColor: "#FFFFFF", borderWidth: 1, borderColor: "#E4E7EC" },
  tools: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  footer: { paddingHorizontal: 16, paddingVertical: 10, borderTopWidth: StyleSheet.hairlineWidth, borderColor: "#D0D5DD", gap: 6, backgroundColor: "#FFFFFF" },
  footerButtons: { flexDirection: "row", justifyContent: "space-between", flexWrap: "wrap", gap: 4 },
  uncertainAction: { alignItems: "flex-end" },
  button: { minHeight: 44 },
  error: { color: "#B42318", lineHeight: 20 },
  notice: { color: "#344054", lineHeight: 20 },
  summary: { backgroundColor: "#EAF2FF", padding: 14, borderRadius: 10, gap: 8, borderWidth: 1, borderColor: "#B8D4FF" },
  total: { color: "#0958D9", fontWeight: "700" },
  reviewItem: { backgroundColor: "#FFFFFF", padding: 12, borderRadius: 10, gap: 8, borderWidth: 1, borderColor: "#E4E7EC" },
  reviewChild: { paddingLeft: 12, color: "#475467", lineHeight: 20 },
});
