import { useEffect, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Button, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { StoreFilterRow } from "@/components/ui/StoreFilterRow";
import { StoreMultiPickPage } from "@/components/ui/StoreMultiPickPage";
import { LEGACY_RANGE_PRESETS } from "@/modules/legacy-employee-logs/logic";
import type { LegacyLogFilters } from "@/modules/legacy-employee-logs/types";
import type { Store } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { LEGACY_UI } from "./ui";

export interface LegacyLogFilterSheetProps {
  visible: boolean;
  filters: LegacyLogFilters;
  stores: Store[];
  employees: { employeeId: string | null; employeeName: string | null; count: number }[];
  devices: { deviceCode: string | null; count: number }[];
  onClose: () => void;
  onApply: (filters: LegacyLogFilters) => void;
  onReset: () => void;
}

/**
 * 筛选抽屉：本地草稿，点「查看结果」才回写；至少选一家分店才能应用（后端分店必填）。
 * 分店有几十家，主表单只放一行摘要选择器，点开进「选择分店」第二步（可搜索的多选列表），
 * 员工、设备、关键字等其它筛选不会被分店挤到折叠线以下。
 */
export function LegacyLogFilterSheet({ visible, filters, stores, employees, devices, onClose, onApply, onReset }: LegacyLogFilterSheetProps) {
  const { t } = useAppTranslation("legacyEmployeeLogs");
  const [draft, setDraft] = useState(filters);
  // 是否在「选择分店」第二步
  const [pickingStores, setPickingStores] = useState(false);

  useEffect(() => {
    if (visible) {
      setDraft(filters);
      setPickingStores(false);
    }
  }, [filters, visible]);

  const patch = (partial: Partial<LegacyLogFilters>) => setDraft((current) => ({ ...current, ...partial }));
  // 分店变化后员工与设备选项不再可靠，一并清空。
  const clearDependents = { employeeId: null, employeeName: null, deviceCode: null };
  const selectedStore = draft.storeCodes.length === 1 ? stores.find((store) => store.storeCode === draft.storeCodes[0]) : undefined;
  const storeSummary =
    draft.storeCodes.length === 0
      ? { label: t("filters.storeRequired"), caption: t("filters.storesTotal", { count: stores.length }) }
      : draft.storeCodes.length === 1
        ? { label: selectedStore?.storeName || draft.storeCodes[0], caption: selectedStore?.storeName ? draft.storeCodes[0] : "" }
        : { label: t("filters.storesSelected", { count: draft.storeCodes.length }), caption: "" };
  const toggleStore = (code: string) =>
    patch({
      storeCodes: draft.storeCodes.includes(code) ? draft.storeCodes.filter((item) => item !== code) : [...draft.storeCodes, code],
      ...clearDependents,
    });

  return (
    <BusinessSheet
      visible={visible}
      title={pickingStores ? t("filters.storePageTitle") : t("filters.title")}
      // 选分店这一步里点关闭 / 背景只退回主表单，不丢掉草稿
      onDismiss={() => (pickingStores ? setPickingStores(false) : onClose())}
      onBack={pickingStores ? () => setPickingStores(false) : undefined}
      footer={
        pickingStores ? (
          <Button mode="contained" disabled={draft.storeCodes.length === 0} onPress={() => setPickingStores(false)}>
            {draft.storeCodes.length === 0 ? t("filters.storeRequired") : t("filters.storesDone")}
          </Button>
        ) : (
          <View style={styles.footer}>
            <Button mode="outlined" onPress={onReset} style={styles.footerButton}>
              {t("actions.reset")}
            </Button>
            <Button mode="contained" disabled={draft.storeCodes.length === 0} onPress={() => onApply(draft)} style={[styles.footerButton, styles.footerPrimary]}>
              {draft.storeCodes.length === 0 ? t("filters.storeRequired") : t("actions.apply")}
            </Button>
          </View>
        )
      }
    >
      {pickingStores ? (
        <StoreMultiPickPage
          stores={stores}
          selected={draft.storeCodes}
          onToggle={toggleStore}
          onClear={() => patch({ storeCodes: [], ...clearDependents })}
          onSelectAll={() => patch({ storeCodes: stores.map((store) => store.storeCode), ...clearDependents })}
          labels={{
            searchPlaceholder: t("filters.searchStore"),
            emptyText: t("filters.storeEmpty"),
            selectedText: t("filters.storesSelected", { count: draft.storeCodes.length }),
            selectAllLabel: t("filters.selectAll"),
            clearLabel: t("filters.clear"),
          }}
        />
      ) : (
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <View style={styles.headRow}>
          <Text style={LEGACY_UI.sectionLabel}>{t("filters.timeRange")}</Text>
          <Text style={styles.hint}>{t("filters.timeHint")}</Text>
        </View>
        <View style={styles.wrap}>
          {LEGACY_RANGE_PRESETS.map((preset) => (
            <Option key={preset} label={t(`presets.${preset}`)} selected={draft.preset === preset} onPress={() => patch({ preset })} />
          ))}
        </View>

        <Text style={LEGACY_UI.sectionLabel}>{t("filters.stores")}</Text>
        <StoreFilterRow
          label={storeSummary.label}
          caption={storeSummary.caption}
          active={draft.storeCodes.length > 0}
          accessibilityLabel={t("filters.storePageTitle")}
          clearLabel={t("filters.clear")}
          onPress={() => setPickingStores(true)}
          onClear={() => patch({ storeCodes: [], ...clearDependents })}
        />

        {employees.length > 0 ? (
          <>
            <View style={styles.headRow}>
              <Text style={LEGACY_UI.sectionLabel}>{t("filters.employee")}</Text>
              <Text style={styles.hint}>{t("filters.optionsHint")}</Text>
            </View>
            <View style={styles.wrap}>
              <Option label={t("filters.employeeAll")} selected={!draft.employeeId} onPress={() => patch({ employeeId: null, employeeName: null })} />
              {employees
                .filter((row) => row.employeeId)
                .map((row) => (
                  <Option
                    key={row.employeeId!}
                    label={row.employeeName || row.employeeId!}
                    count={row.count.toLocaleString("en-US")}
                    selected={draft.employeeId === row.employeeId}
                    onPress={() => patch({ employeeId: row.employeeId, employeeName: row.employeeName })}
                  />
                ))}
            </View>
          </>
        ) : null}

        {devices.length > 0 ? (
          <>
            <Text style={LEGACY_UI.sectionLabel}>{t("filters.device")}</Text>
            <View style={styles.wrap}>
              <Option label={t("filters.deviceAll")} selected={!draft.deviceCode} onPress={() => patch({ deviceCode: null })} />
              {devices
                .filter((row) => row.deviceCode)
                .map((row) => (
                  <Option key={row.deviceCode!} label={row.deviceCode!} selected={draft.deviceCode === row.deviceCode} onPress={() => patch({ deviceCode: row.deviceCode })} />
                ))}
            </View>
          </>
        ) : null}

        <Text style={LEGACY_UI.sectionLabel}>{t("filters.keyword")}</Text>
        <TextInput
          mode="outlined"
          dense
          value={draft.keyword}
          onChangeText={(keyword) => patch({ keyword })}
          placeholder={t("filters.keywordPlaceholder")}
          accessibilityLabel={t("filters.keyword")}
          maxLength={100}
        />
      </ScrollView>
      )}
    </BusinessSheet>
  );
}

function Option({ label, count, selected, onPress }: { label: string; count?: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.option, selected ? styles.optionOn : null]}
    >
      <Text style={[styles.optionText, selected ? styles.optionTextOn : null]}>{label}</Text>
      {count ? <Text style={[styles.optionCount, selected ? styles.optionTextOn : null]}>{count}</Text> : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  content: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.md },
  headRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  hint: { fontSize: 12, color: HB_COLORS.textSecondary, flexShrink: 1, textAlign: "right" },
  wrap: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  option: {
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    minHeight: 36,
    paddingHorizontal: 12,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  optionOn: { borderColor: HB_COLORS.action, backgroundColor: "#EAF2FF" },
  optionText: { fontSize: 13, color: HB_COLORS.textPrimary },
  optionTextOn: { color: HB_COLORS.action },
  optionCount: { fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  footerPrimary: { flex: 2 },
});
