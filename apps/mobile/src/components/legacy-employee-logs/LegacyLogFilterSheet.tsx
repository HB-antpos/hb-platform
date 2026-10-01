import { useEffect, useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Button, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
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

/** 筛选抽屉：本地草稿，点「查看结果」才回写；至少选一家分店才能应用（后端分店必填）。 */
export function LegacyLogFilterSheet({ visible, filters, stores, employees, devices, onClose, onApply, onReset }: LegacyLogFilterSheetProps) {
  const { t } = useAppTranslation("legacyEmployeeLogs");
  const [draft, setDraft] = useState(filters);
  const [storeQuery, setStoreQuery] = useState("");

  useEffect(() => {
    if (visible) {
      setDraft(filters);
      setStoreQuery("");
    }
  }, [filters, visible]);

  const patch = (partial: Partial<LegacyLogFilters>) => setDraft((current) => ({ ...current, ...partial }));
  const visibleStores = useMemo(() => {
    const query = storeQuery.trim().toLowerCase();
    return query
      ? stores.filter((store) => store.storeCode.toLowerCase().includes(query) || store.storeName?.toLowerCase().includes(query))
      : stores;
  }, [storeQuery, stores]);
  const toggleStore = (code: string) =>
    patch({
      storeCodes: draft.storeCodes.includes(code) ? draft.storeCodes.filter((item) => item !== code) : [...draft.storeCodes, code],
      // 分店变化后员工与设备选项不再可靠，一并清空。
      employeeId: null,
      employeeName: null,
      deviceCode: null,
    });

  return (
    <BusinessSheet
      visible={visible}
      title={t("filters.title")}
      onDismiss={onClose}
      footer={
        <View style={styles.footer}>
          <Button mode="outlined" onPress={onReset} style={styles.footerButton}>
            {t("actions.reset")}
          </Button>
          <Button mode="contained" disabled={draft.storeCodes.length === 0} onPress={() => onApply(draft)} style={[styles.footerButton, styles.footerPrimary]}>
            {draft.storeCodes.length === 0 ? t("filters.storeRequired") : t("actions.apply")}
          </Button>
        </View>
      }
    >
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

        <View style={styles.headRow}>
          <Text style={LEGACY_UI.sectionLabel}>{t("filters.stores")}</Text>
          <View style={styles.inline}>
            <Text style={styles.hint}>{t("filters.storesSelected", { count: draft.storeCodes.length })}</Text>
            <Button compact onPress={() => patch({ storeCodes: stores.map((store) => store.storeCode), employeeId: null, employeeName: null, deviceCode: null })}>
              {t("filters.selectAll")}
            </Button>
            <Button compact onPress={() => patch({ storeCodes: [], employeeId: null, employeeName: null, deviceCode: null })}>
              {t("filters.clear")}
            </Button>
          </View>
        </View>
        {stores.length > 6 ? (
          <TextInput
            mode="outlined"
            dense
            value={storeQuery}
            onChangeText={setStoreQuery}
            placeholder={t("filters.searchStore")}
            accessibilityLabel={t("filters.searchStore")}
            left={<TextInput.Icon icon="magnify" />}
            style={styles.storeSearch}
          />
        ) : null}
        <View style={styles.wrap}>
          {visibleStores.map((store) => (
            <Option
              key={store.storeCode}
              label={store.storeName || store.storeCode}
              count={store.storeName ? store.storeCode : undefined}
              selected={draft.storeCodes.includes(store.storeCode)}
              onPress={() => toggleStore(store.storeCode)}
            />
          ))}
        </View>

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
  inline: { flexDirection: "row", alignItems: "center" },
  hint: { fontSize: 12, color: HB_COLORS.textSecondary, flexShrink: 1, textAlign: "right" },
  wrap: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  storeSearch: { marginBottom: HB_SPACING.xs },
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
