import { useEffect, useMemo, useRef, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Card, Checkbox, Icon, Switch, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { getSyncTargets, syncToOtherStores } from "@/modules/price-updates/api";
import { buildSyncToOtherStoresMessage } from "@/modules/price-updates/price-notification";
import { formatDiscountLabel, formatMoney } from "@/modules/price-updates/presentation";
import { priceUpdateCountQueryKey } from "@/modules/price-updates/query-keys";
import {
  DEFAULT_SYNC_FIELDS,
  describeSyncTargetChange,
  getDefaultSelectedSyncTargets,
  getSelectableSyncTargetCodes,
  resolveEffectiveSyncFields,
  willSyncWarehousePrice,
  type SyncFieldSelection,
} from "@/modules/price-updates/sync-targets";
import type { SyncTargetStore } from "@/modules/price-updates/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface SyncToOtherStoresSheetProps {
  visible: boolean;
  productCode: string;
  sourceStoreCode: string;
  sourceStoreName?: string | null;
  onDismiss: () => void;
  /** 同步成功：message 已按 X-Price-Notification 头拼好，调用方直接放进 Snackbar。 */
  onSynced: (message: string) => void;
}

export function SyncToOtherStoresSheet({
  visible,
  productCode,
  sourceStoreCode,
  sourceStoreName,
  onDismiss,
  onSynced,
}: SyncToOtherStoresSheetProps) {
  const { t, language } = useAppTranslation(["priceUpdates", "common"]);
  const queryClient = useQueryClient();
  const [fields, setFields] = useState<SyncFieldSelection>(DEFAULT_SYNC_FIELDS);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [submitting, setSubmitting] = useState(false);
  const [errorMessage, setErrorMessage] = useState("");
  const initializedForRef = useRef<unknown>(null);

  const targetsQuery = useQuery({
    queryKey: ["priceUpdates", "syncTargets", productCode, sourceStoreCode],
    enabled: visible && Boolean(productCode) && Boolean(sourceStoreCode),
    queryFn: () => getSyncTargets(productCode, sourceStoreCode),
    // 各分店现价随时会变，每次打开都重新取，不复用上次面板的缓存。
    staleTime: 0,
    gcTime: 0,
  });
  const data = targetsQuery.data;

  useEffect(() => {
    if (!visible) {
      initializedForRef.current = null;
      setFields(DEFAULT_SYNC_FIELDS);
      setErrorMessage("");
      return;
    }
    // 只在每份新数据到达时套用一次默认勾选，之后不覆盖用户的手动选择。
    if (data && initializedForRef.current !== data) {
      initializedForRef.current = data;
      setSelected(getDefaultSelectedSyncTargets(data.targets));
    }
  }, [data, visible]);

  const selectableCodes = useMemo(() => getSelectableSyncTargetCodes(data?.targets ?? []), [data?.targets]);
  const selectedCodes = useMemo(
    () => selectableCodes.filter((code) => selected.has(code)),
    [selectableCodes, selected]
  );
  const allSelected = selectableCodes.length > 0 && selectedCodes.length === selectableCodes.length;
  // 仓库商品不同步进货价（见 resolveEffectiveSyncFields）：请求、校验、预览都用生效后的字段。
  const warehouseApplicable = data?.warehouseApplicable === true;
  const effectiveFields = useMemo(
    () => resolveEffectiveSyncFields(fields, warehouseApplicable),
    [fields, warehouseApplicable]
  );
  const syncsWarehouse = willSyncWarehousePrice(effectiveFields, warehouseApplicable);
  const anyFieldSelected =
    effectiveFields.syncRetailPrice || effectiveFields.syncDiscountRate || effectiveFields.syncPurchasePrice;

  const toggleTarget = (storeCode: string) => {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(storeCode)) next.delete(storeCode);
      else next.add(storeCode);
      return next;
    });
  };

  const handleSubmit = async () => {
    if (submitting || !selectedCodes.length || !anyFieldSelected) return;
    setSubmitting(true);
    setErrorMessage("");
    try {
      const result = await syncToOtherStores({
        productCode,
        sourceStoreCode,
        targetStoreCodes: selectedCodes,
        ...effectiveFields,
      });
      void queryClient.invalidateQueries({ queryKey: priceUpdateCountQueryKey() });
      onSynced(
        buildSyncToOtherStoresMessage(result.updatedStoreCount, result.notification, t, result.warehouse.updated)
      );
    } catch (error) {
      setErrorMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "syncSheet.failed" }));
    } finally {
      setSubmitting(false);
    }
  };

  const renderChange = (target: SyncTargetStore) => {
    const change = describeSyncTargetChange(
      target,
      { retailPrice: data?.sourceRetailPrice ?? null, discountRate: data?.sourceDiscountRate ?? null },
      effectiveFields
    );
    if (change.kind === "noRecord") return null;
    if (change.kind === "price") {
      return (
        <Text variant="bodySmall" style={styles.changeText}>
          {`${formatMoney(change.from)} → ${formatMoney(change.to)}`}
        </Text>
      );
    }
    return (
      <Text variant="bodySmall" style={styles.secondary}>
        {t(change.kind === "same" ? "syncSheet.same" : "syncSheet.discountOnly")}
      </Text>
    );
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t("syncSheet.title")}
      subtitle={t("syncSheet.subtitle")}
      onDismiss={onDismiss}
      dismissable={!submitting}
      footer={(
        <View style={styles.footer}>
          <Text variant="bodySmall" style={styles.secondary}>{t("syncSheet.footerHint")}</Text>
          {errorMessage ? <Text variant="bodySmall" style={styles.errorText}>{errorMessage}</Text> : null}
          <Button
            mode="contained"
            icon="store-cog-outline"
            loading={submitting}
            disabled={submitting || !selectedCodes.length || !anyFieldSelected}
            onPress={() => void handleSubmit()}
          >
            {t("syncSheet.submit", { count: selectedCodes.length })}
          </Button>
        </View>
      )}
    >
      <View style={styles.sourceBox}>
        <Text variant="labelSmall" style={styles.secondary}>{t("syncSheet.source")}</Text>
        <Text variant="bodyMedium" style={styles.sourceText}>
          {[
            sourceStoreName || sourceStoreCode,
            data ? formatMoney(data.sourceRetailPrice) : null,
            data ? formatDiscountLabel(data.sourceDiscountRate, t) : null,
          ].filter(Boolean).join(" · ")}
        </Text>
      </View>

      <View style={styles.fieldRow}>
        {([
          ["syncRetailPrice", "syncSheet.fields.retailPrice"],
          ["syncDiscountRate", "syncSheet.fields.discountRate"],
          ["syncPurchasePrice", "syncSheet.fields.purchasePrice"],
        ] as const)
          // 仓库商品的进货价由仓库统一管理，不提供同步开关。
          .filter(([key]) => !(warehouseApplicable && key === "syncPurchasePrice"))
          .map(([key, labelKey]) => (
          <View key={key} style={styles.fieldSwitch}>
            <Text variant="bodySmall">{t(labelKey)}</Text>
            <Switch
              value={fields[key]}
              disabled={submitting}
              onValueChange={(value) => setFields((current) => ({ ...current, [key]: value }))}
            />
          </View>
        ))}
      </View>

      {syncsWarehouse ? (
        // 仓库商品：同步会把仓库价也改成本店的值，并让未同步且价格不同的分店收到「需改价」，提前说清楚。
        <View style={styles.warehouseNotice}>
          <Icon source="warehouse" size={18} color={HB_COLORS.warning} />
          <Text variant="bodySmall" style={[styles.flex, styles.warningText]}>
            {t("syncSheet.warehouseNotice", {
              price: formatMoney(data?.sourceRetailPrice ?? null),
            })}
          </Text>
        </View>
      ) : null}

      <View style={styles.listHeader}>
        <Text variant="titleSmall" style={styles.flex}>
          {t("syncSheet.targets", { selected: selectedCodes.length, total: selectableCodes.length })}
        </Text>
        <Button
          compact
          mode="text"
          disabled={submitting || !selectableCodes.length}
          onPress={() => setSelected(allSelected ? new Set() : new Set(selectableCodes))}
        >
          {t(allSelected ? "syncSheet.clearAll" : "syncSheet.selectAll")}
        </Button>
      </View>

      {targetsQuery.isLoading ? (
        <ActivityIndicator style={styles.loader} />
      ) : targetsQuery.isError ? (
        <View style={styles.loader}>
          <Text variant="bodySmall" style={styles.errorText}>
            {resolveLocalizedErrorMessage(targetsQuery.error, { t, language, fallbackKey: "syncSheet.loadFailed" })}
          </Text>
          <Button compact mode="text" icon="refresh" onPress={() => void targetsQuery.refetch()}>
            {t("common:actions.retry")}
          </Button>
        </View>
      ) : !data?.targets.length ? (
        <Text variant="bodySmall" style={[styles.secondary, styles.loader]}>{t("syncSheet.empty")}</Text>
      ) : (
        data.targets.map((target) => {
          const disabled = !target.hasRecord || submitting;
          const checked = target.hasRecord && selected.has(target.storeCode);
          return (
            <Pressable
              key={target.storeCode}
              disabled={disabled}
              onPress={() => toggleTarget(target.storeCode)}
              accessibilityRole="checkbox"
              accessibilityState={{ checked, disabled }}
              style={[styles.targetRow, !target.hasRecord ? styles.targetRowDisabled : null]}
            >
              <Checkbox.Android
                status={checked ? "checked" : "unchecked"}
                disabled={disabled}
                color={HB_COLORS.action}
                onPress={() => toggleTarget(target.storeCode)}
              />
              <View style={styles.targetText}>
                <Text variant="bodyMedium" numberOfLines={1}>{target.storeName}</Text>
                {!target.hasRecord ? (
                  <Text variant="labelSmall" style={styles.secondary}>{t("syncSheet.noRecord")}</Text>
                ) : target.isSpecialProduct ? (
                  <Text variant="labelSmall" style={styles.warningText}>{t("syncSheet.specialProduct")}</Text>
                ) : null}
              </View>
              {renderChange(target)}
            </Pressable>
          );
        })
      )}
    </BusinessSheet>
  );
}

interface SyncToOtherStoresSectionProps {
  productCode: string;
  storeCode: string;
  storeName?: string | null;
  /** 分店价格有未保存修改：入口变为「保存并同步」，先保存成功才打开面板。 */
  hasUnsavedChanges: boolean;
  disabled?: boolean;
  /** card：独立卡片（默认）；inline：嵌在价格卡底部的一行入口。 */
  variant?: "card" | "inline";
  onSaveBeforeSync: () => Promise<boolean>;
  onMessage: (message: string) => void;
}

/** 商品维护页入口卡片 + 面板。显示条件（账号会话、权限码、可管理分店）由调用方判断。 */
export function SyncToOtherStoresSection({
  productCode,
  storeCode,
  storeName,
  hasUnsavedChanges,
  disabled = false,
  variant = "card",
  onSaveBeforeSync,
  onMessage,
}: SyncToOtherStoresSectionProps) {
  const { t } = useAppTranslation("priceUpdates");
  const [sheetVisible, setSheetVisible] = useState(false);
  const [saving, setSaving] = useState(false);

  // 切换商品或分店后旧面板的目标列表已不适用，必须关闭。
  useEffect(() => {
    setSheetVisible(false);
  }, [productCode, storeCode]);

  const handleOpen = async () => {
    if (saving) return;
    if (hasUnsavedChanges) {
      setSaving(true);
      try {
        // 同步读取的是服务端已保存的本店价，未保存的修改必须先落库。
        if (!(await onSaveBeforeSync())) return;
      } finally {
        setSaving(false);
      }
    }
    setSheetVisible(true);
  };

  const entryDisabled = disabled || saving;

  return (
    <>
      {variant === "inline" ? (
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ disabled: entryDisabled, busy: saving }}
          disabled={entryDisabled}
          onPress={() => void handleOpen()}
          style={({ pressed }) => [
            styles.inlineEntry,
            entryDisabled ? styles.inlineEntryDisabled : null,
            pressed ? styles.inlineEntryPressed : null,
          ]}
        >
          {saving ? (
            <ActivityIndicator size={16} color={HB_COLORS.action} />
          ) : (
            <Icon source="swap-horizontal" size={18} color={HB_COLORS.action} />
          )}
          <Text variant="labelLarge" style={styles.inlineEntryText} numberOfLines={1}>
            {t(hasUnsavedChanges ? "syncSheet.inlineSaveAndSync" : "syncSheet.inlineEntry")}
          </Text>
          <Icon source="chevron-right" size={18} color={HB_COLORS.textSecondary} />
        </Pressable>
      ) : (
        <Card mode="contained" style={styles.entryCard}>
          <Card.Content style={styles.entryContent}>
            <View style={styles.flex}>
              <Text variant="titleSmall" style={styles.entryTitle}>{t("syncSheet.entryTitle")}</Text>
              <Text variant="bodySmall" style={styles.secondary}>{t("syncSheet.entrySubtitle")}</Text>
            </View>
            <Button
              compact
              mode="outlined"
              icon="store-cog-outline"
              loading={saving}
              disabled={disabled || saving}
              onPress={() => void handleOpen()}
            >
              {t(hasUnsavedChanges ? "syncSheet.entrySaveAndSync" : "syncSheet.entryAction")}
            </Button>
          </Card.Content>
        </Card>
      )}
      <SyncToOtherStoresSheet
        visible={sheetVisible}
        productCode={productCode}
        sourceStoreCode={storeCode}
        sourceStoreName={storeName}
        onDismiss={() => setSheetVisible(false)}
        onSynced={(message) => {
          setSheetVisible(false);
          onMessage(message);
        }}
      />
    </>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  secondary: { color: HB_COLORS.textSecondary },
  warningText: { color: HB_COLORS.warning },
  errorText: { color: HB_COLORS.danger },
  sourceBox: { gap: HB_SPACING.xxs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  sourceText: { fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  warehouseNotice: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  fieldRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.sm },
  fieldSwitch: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xxs },
  listHeader: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  loader: { paddingVertical: HB_SPACING.md, alignItems: "center" },
  targetRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xxs, minHeight: 48, paddingRight: HB_SPACING.xs, borderRadius: HB_RADIUS.control, borderWidth: 1, borderColor: HB_COLORS.outlineMuted },
  targetRowDisabled: { opacity: 0.55 },
  targetText: { flex: 1, minWidth: 0 },
  changeText: { fontVariant: ["tabular-nums"], fontWeight: "600", color: HB_COLORS.textPrimary },
  footer: { gap: HB_SPACING.xs },
  entryCard: { borderRadius: 12, borderWidth: 1, borderColor: "#E4E7EC", backgroundColor: "#fff" },
  entryContent: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingVertical: 12 },
  entryTitle: { fontWeight: "700", color: "#111827" },
  inlineEntry: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 44, paddingHorizontal: HB_SPACING.sm },
  inlineEntryDisabled: { opacity: 0.5 },
  inlineEntryPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  inlineEntryText: { flex: 1, minWidth: 0, color: HB_COLORS.action, fontWeight: "600" },
});
