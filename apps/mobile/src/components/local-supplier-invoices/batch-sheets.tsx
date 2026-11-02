import { useEffect, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Checkbox, Switch, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { DetailTag } from "@/components/local-supplier-invoices/detail-tag";
import { ACTION_TONE } from "@/components/local-supplier-invoices/invoice-detail-format";
import {
  DetailAction,
  buildBatchEditFields,
  createEmptyBatchEditDraft,
  type BatchEditDraft,
  type BatchEditFields,
  type ExecuteChoice,
  type ExecuteGroups,
  type NewProductType,
} from "@/modules/local-supplier-invoices/detail";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

type Translate = (key: string, options?: Record<string, unknown>) => string;

const SELECTABLE_ACTIONS = [
  DetailAction.None,
  DetailAction.CreateProduct,
  DetailAction.UpdatePurchasePrice,
  DetailAction.WaitForOperation,
  DetailAction.UpdateItemNumber,
  DetailAction.AddMultiCode,
];

// ---------------------------------------------------------------------------
// 批量设置操作类型：点一项即提交（与单行改操作类型一样立即落库）
// ---------------------------------------------------------------------------

export function BatchActionSheet({
  visible,
  count,
  busy,
  onPick,
  onDismiss,
  t,
}: {
  visible: boolean;
  count: number;
  busy: boolean;
  onPick: (action: number) => void;
  onDismiss: () => void;
  t: Translate;
}) {
  return (
    <BusinessSheet visible={visible} title={t("detail.batch.setActionTitle")} subtitle={t("detail.batch.selectedCount", { count })} onDismiss={onDismiss} dismissable={!busy}>
      <View style={styles.stack}>
        {SELECTABLE_ACTIONS.map((action) => (
          <Pressable
            key={action}
            accessibilityRole="button"
            accessibilityState={{ disabled: busy }}
            disabled={busy}
            style={styles.optionRow}
            onPress={() => onPick(action)}
          >
            {/* DetailTag 自带 alignSelf: flex-start，包一层才能和右侧箭头一起垂直居中。 */}
            <View style={styles.tagWrap}>
              <DetailTag label={t(`detail.action.${action}`)} tone={ACTION_TONE[action] ?? "gray"} />
            </View>
            <Text style={styles.chevron}>›</Text>
          </Pressable>
        ))}
        <Text style={styles.noteText}>{t("detail.batch.setActionNote")}</Text>
      </View>
    </BusinessSheet>
  );
}

// ---------------------------------------------------------------------------
// 批量编辑：每个字段一个「要改」开关（与 Web 批量编辑弹窗一致）
// ---------------------------------------------------------------------------

export function BatchEditSheet({
  visible,
  count,
  busy,
  onSubmit,
  onDismiss,
  t,
}: {
  visible: boolean;
  count: number;
  busy: boolean;
  onSubmit: (fields: BatchEditFields) => void;
  onDismiss: () => void;
  t: Translate;
}) {
  const [draft, setDraft] = useState<BatchEditDraft>(createEmptyBatchEditDraft);
  const [error, setError] = useState<string | null>(null);

  // 每次打开都从空白开始，避免沿用上一次的勾选误改别的行。
  useEffect(() => {
    if (visible) {
      setDraft(createEmptyBatchEditDraft());
      setError(null);
    }
  }, [visible]);

  const patch = (next: Partial<BatchEditDraft>) => {
    setDraft((current) => ({ ...current, ...next }));
    setError(null);
  };

  const submit = () => {
    const result = buildBatchEditFields(draft);
    if (!result.ok) {
      setError(t(`detail.batch.editError.${result.error}`));
      return;
    }
    onSubmit(result.fields);
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t("detail.batch.editTitle")}
      subtitle={t("detail.batch.selectedCount", { count })}
      onDismiss={onDismiss}
      dismissable={!busy}
      footer={(
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.footerSide} disabled={busy} onPress={onDismiss}>{t("detail.edit.cancel")}</Button>
          <Button mode="contained" style={styles.footerMain} loading={busy} disabled={busy} onPress={submit}>
            {t("detail.batch.editSubmit", { count })}
          </Button>
        </View>
      )}
    >
      <View style={styles.stack}>
        <ToggleField
          label={t("detail.edit.purchasePrice")}
          enabled={draft.updatePurchasePrice}
          onToggle={(value) => patch({ updatePurchasePrice: value })}
        >
          <TextInput
            style={styles.input}
            value={draft.purchasePrice}
            keyboardType="decimal-pad"
            placeholder="0.00"
            onChangeText={(value) => patch({ purchasePrice: value })}
          />
        </ToggleField>
        <ToggleField
          label={t("detail.edit.retailPrice")}
          enabled={draft.updateRetailPrice}
          onToggle={(value) => patch({ updateRetailPrice: value })}
        >
          <TextInput
            style={styles.input}
            value={draft.retailPrice}
            keyboardType="decimal-pad"
            placeholder="0.00"
            onChangeText={(value) => patch({ retailPrice: value })}
          />
        </ToggleField>
        <ToggleField
          label={t("detail.pricingSheet.auto")}
          enabled={draft.updateIsAutoPricing}
          onToggle={(value) => patch({ updateIsAutoPricing: value })}
        >
          <Switch value={draft.isAutoPricing} onValueChange={(value) => patch({ isAutoPricing: value })} style={styles.switch} />
        </ToggleField>
        <ToggleField
          label={t("detail.pricingSheet.special")}
          enabled={draft.updateIsSpecialProduct}
          onToggle={(value) => patch({ updateIsSpecialProduct: value })}
        >
          <Switch value={draft.isSpecialProduct} onValueChange={(value) => patch({ isSpecialProduct: value })} style={styles.switch} />
        </ToggleField>
        <ToggleField
          label={t("detail.pricingSheet.discount")}
          enabled={draft.updateDiscountRate}
          onToggle={(value) => patch({ updateDiscountRate: value })}
        >
          <TextInput
            style={styles.input}
            value={draft.discountPercent}
            keyboardType="decimal-pad"
            placeholder="%"
            onChangeText={(value) => patch({ discountPercent: value })}
          />
        </ToggleField>
        {error ? <Text style={styles.error}>{error}</Text> : null}
        <Text style={styles.noteText}>{t("detail.batch.editNote")}</Text>
      </View>
    </BusinessSheet>
  );
}

/** 左边勾选「要改这一项」，右边是值；没勾时值控件隐藏，避免误以为会被提交。 */
function ToggleField({
  label,
  enabled,
  onToggle,
  children,
}: {
  label: string;
  enabled: boolean;
  onToggle: (value: boolean) => void;
  children: React.ReactNode;
}) {
  return (
    <View style={styles.toggleRow}>
      <Pressable
        accessibilityRole="checkbox"
        accessibilityState={{ checked: enabled }}
        onPress={() => onToggle(!enabled)}
        style={styles.toggleLabel}
      >
        <Checkbox.Android status={enabled ? "checked" : "unchecked"} onPress={() => onToggle(!enabled)} color={HB_COLORS.brand} />
        <Text style={styles.rowLabel}>{label}</Text>
      </Pressable>
      {enabled ? <View style={styles.toggleValue}>{children}</View> : null}
    </View>
  );
}

// ---------------------------------------------------------------------------
// 批量执行确认：按类勾选（新建 / 涨价 / 降价 / 持平），带副码新品选主档类型
// ---------------------------------------------------------------------------

export interface BatchExecuteSheetData {
  /** true = 来自勾选；false = 整单待执行。 */
  fromSelection: boolean;
  groups: ExecuteGroups;
  webOnlyCount: number;
  noActionCount: number;
  newWithAdditionalCount: number;
}

export function BatchExecuteSheet({
  data,
  busy,
  onConfirm,
  onDismiss,
  t,
}: {
  data: BatchExecuteSheetData | null;
  busy: boolean;
  onConfirm: (choice: ExecuteChoice, newProductType: NewProductType) => void;
  onDismiss: () => void;
  t: Translate;
}) {
  const [choice, setChoice] = useState<ExecuteChoice>({ includeCreate: true, includeUp: true, includeDown: true, includeFlat: true });
  const [productType, setProductType] = useState<NewProductType>(2);

  useEffect(() => {
    if (data) {
      setChoice({ includeCreate: true, includeUp: true, includeDown: true, includeFlat: true });
      setProductType(2);
    }
  }, [data]);

  if (!data) return null;
  const { groups } = data;
  const lines: { key: keyof ExecuteChoice; label: string; count: number; tone?: "danger" | "action" }[] = [
    { key: "includeCreate", label: t("detail.batch.execCreate"), count: groups.createGuids.length },
    { key: "includeUp", label: t("detail.batch.execUp"), count: groups.upGuids.length, tone: "danger" },
    { key: "includeDown", label: t("detail.batch.execDown"), count: groups.downGuids.length, tone: "action" },
    { key: "includeFlat", label: t("detail.batch.execFlat"), count: groups.flatGuids.length },
  ];
  const chosenCount = lines.reduce((sum, line) => sum + (choice[line.key] ? line.count : 0), 0);
  const createChosen = choice.includeCreate && groups.createGuids.length > 0;

  return (
    <BusinessSheet
      visible
      title={t(data.fromSelection ? "detail.batch.execTitleSelected" : "detail.batch.execTitlePending")}
      onDismiss={onDismiss}
      dismissable={!busy}
      footer={(
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.footerSide} disabled={busy} onPress={onDismiss}>{t("detail.edit.cancel")}</Button>
          <Button
            mode="contained"
            style={styles.footerMain}
            loading={busy}
            disabled={busy || chosenCount === 0}
            onPress={() => onConfirm(choice, productType)}
          >
            {t("detail.batch.execSubmit", { count: chosenCount })}
          </Button>
        </View>
      )}
    >
      <View style={styles.stack}>
        {lines.filter((line) => line.count > 0).map((line) => (
          <Pressable
            key={line.key}
            accessibilityRole="checkbox"
            accessibilityState={{ checked: choice[line.key], disabled: busy }}
            disabled={busy}
            style={styles.optionRow}
            onPress={() => setChoice((current) => ({ ...current, [line.key]: !current[line.key] }))}
          >
            <View style={styles.toggleLabel}>
              <Checkbox.Android
                status={choice[line.key] ? "checked" : "unchecked"}
                disabled={busy}
                onPress={() => setChoice((current) => ({ ...current, [line.key]: !current[line.key] }))}
                color={HB_COLORS.brand}
              />
              <Text style={[styles.rowLabel, line.tone === "danger" ? styles.danger : line.tone === "action" ? styles.action : null]}>
                {line.label}
              </Text>
            </View>
            <Text style={styles.count}>{line.count}</Text>
          </Pressable>
        ))}

        {createChosen && data.newWithAdditionalCount > 0 ? (
          <View style={styles.note}>
            <Text style={styles.noteText}>{t("detail.batch.execProductTypeHint", { count: data.newWithAdditionalCount })}</Text>
            <View style={styles.segment}>
              {([2, 1] as NewProductType[]).map((value) => (
                <Pressable
                  key={value}
                  accessibilityRole="radio"
                  accessibilityState={{ selected: productType === value }}
                  onPress={() => setProductType(value)}
                  style={[styles.segmentItem, productType === value ? styles.segmentItemOn : null]}
                >
                  <Text style={[styles.segmentText, productType === value ? styles.segmentTextOn : null]}>
                    {t(value === 2 ? "detail.productType.multi" : "detail.productType.bundle")}
                  </Text>
                </Pressable>
              ))}
            </View>
          </View>
        ) : null}

        <View style={styles.note}>
          {createChosen ? <Text style={styles.noteText}>{t("detail.batch.execNoHq")}</Text> : null}
          {(choice.includeUp || choice.includeDown || choice.includeFlat)
            && groups.upGuids.length + groups.downGuids.length + groups.flatGuids.length > 0 ? (
              <Text style={styles.noteText}>{t("detail.batch.execStoreOnly")}</Text>
            ) : null}
          {data.webOnlyCount > 0 ? <Text style={styles.noteText}>{t("detail.batch.execWebOnly", { count: data.webOnlyCount })}</Text> : null}
          {data.noActionCount > 0 ? <Text style={styles.noteText}>{t("detail.batch.execNoAction", { count: data.noActionCount })}</Text> : null}
        </View>
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  stack: { gap: HB_SPACING.xs },
  optionRow: {
    minHeight: 46,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  tagWrap: { alignSelf: "center" },
  chevron: { fontSize: 18, color: HB_COLORS.textSecondary },
  count: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  toggleRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  toggleLabel: { flexDirection: "row", alignItems: "center", gap: 2, flexShrink: 1 },
  toggleValue: { width: 140, alignItems: "flex-end" },
  rowLabel: { fontSize: 14, color: HB_COLORS.textPrimary },
  danger: { color: HB_COLORS.danger, fontWeight: "700" },
  action: { color: HB_COLORS.action, fontWeight: "700" },
  input: {
    alignSelf: "stretch",
    height: 40,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: 10,
    fontSize: 14,
    textAlign: "right",
    color: HB_COLORS.textPrimary,
    backgroundColor: HB_COLORS.white,
  },
  // RN iOS Switch 自带 alignSelf: flex-start，值区是纵向容器，要显式靠右与输入框对齐（行本身已垂直居中）。
  switch: { alignSelf: "flex-end" },
  error: { fontSize: 12, color: HB_COLORS.danger },
  note: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.control, padding: HB_SPACING.xs, gap: 4 },
  noteText: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  segment: { flexDirection: "row", gap: 6 },
  segmentItem: {
    flex: 1,
    minHeight: 36,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  segmentItemOn: { borderColor: HB_COLORS.brand, backgroundColor: "#EAF2FF" },
  segmentText: { fontSize: 13, color: HB_COLORS.textSecondary },
  segmentTextOn: { color: "#073B83", fontWeight: "700" },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerSide: { flex: 1 },
  footerMain: { flex: 2 },
});
