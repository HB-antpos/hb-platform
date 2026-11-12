import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS as C } from "@/shared/theme/tokens";
import { splitStoreLabel } from "@/shared/utils/store-picker-logic";
import { AdminStorePickerPage } from "./admin-store-picker-page";
import { USER_PICKER_SORT_ORDER, type UserPickerFilters, type UserPickerPageKind } from "./user-picker-logic";

export interface PickerOption {
  value: string;
  label: string;
}

type SecondaryPage = Exclude<UserPickerPageKind, "main">;

// 选中态的浅蓝底 / 描边：与分店选择页的 chipSelected 同色。
const SELECTED_BG = "#EFF6FF";
const SELECTED_LINE = "#B2CCFF";

/**
 * 选人弹层的筛选条：分店 / 角色 / 排序三个 chip。
 * 没选时灰框 + ▾，选了变蓝底只显示所选值（带小图标区分分店 / 角色）并带 ✕，一点就清掉；
 * 「仅看已选」时整条置灰（disabled），因为那时列表来自本地已选名单，不按分店 / 角色过滤。
 */
export function UserPickerFilterBar({
  filters,
  storeOptions,
  roleOptions,
  showStore,
  showRole,
  disabled,
  onOpen,
  onClear,
}: {
  filters: UserPickerFilters;
  storeOptions: readonly PickerOption[];
  roleOptions: readonly PickerOption[];
  showStore: boolean;
  showRole: boolean;
  disabled: boolean;
  onOpen: (page: SecondaryPage) => void;
  onClear: (kind: "store" | "role") => void;
}) {
  const { t } = useAppTranslation("people");
  const store = storeOptions.find((option) => option.value === filters.storeGuid);
  const role = roleOptions.find((option) => option.value === filters.roleGuid);
  return (
    <View style={styles.bar}>
      {showStore ? (
        <FilterChip
          icon="storefront-outline"
          label={t("sheet.store")}
          value={store ? splitStoreLabel(store.label).name : filters.storeGuid ? t("sheet.store") : t("sheet.all")}
          active={filters.storeGuid !== ""}
          disabled={disabled}
          clearLabel={t("sheet.clearStore")}
          onPress={() => onOpen("store")}
          onClear={() => onClear("store")}
        />
      ) : null}
      {showRole ? (
        <FilterChip
          icon="account-outline"
          label={t("sheet.role")}
          value={role ? role.label : filters.roleGuid ? t("sheet.role") : t("sheet.all")}
          active={filters.roleGuid !== ""}
          disabled={disabled}
          clearLabel={t("picker.clearRole")}
          onPress={() => onOpen("role")}
          onClear={() => onClear("role")}
        />
      ) : null}
      <FilterChip
        icon="swap-vertical"
        label={t("sheet.sort")}
        value={t(`admin.sort.${filters.sort}`)}
        active={false}
        showLabel={false}
        disabled={disabled}
        clearLabel=""
        onPress={() => onOpen("sort")}
        onClear={() => undefined}
      />
    </View>
  );
}

function FilterChip({
  icon,
  label,
  value,
  active,
  showLabel = true,
  disabled,
  clearLabel,
  onPress,
  onClear,
}: {
  icon: string;
  label: string;
  value: string;
  active: boolean;
  /** 没选值时是否写「分店 / 角色」字样；排序 chip 只用图标。 */
  showLabel?: boolean;
  disabled: boolean;
  clearLabel: string;
  onPress: () => void;
  onClear: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${label} ${value}`}
      accessibilityState={{ disabled, selected: active }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.chip, active && styles.chipActive, disabled && styles.chipDisabled]}
    >
      <Icon source={icon} size={16} color={active ? C.action : C.textSecondary} />
      {!active && showLabel ? <Text style={styles.chipKey}>{label}</Text> : null}
      <Text style={[styles.chipValue, active && styles.chipValueActive]} numberOfLines={1}>{value}</Text>
      {active ? (
        <Pressable accessibilityRole="button" accessibilityLabel={clearLabel} hitSlop={8} disabled={disabled} onPress={onClear}>
          <Icon source="close" size={16} color={C.action} />
        </Pressable>
      ) : (
        <Icon source="chevron-down" size={16} color={C.textSecondary} />
      )}
    </Pressable>
  );
}

/** 二级页标题（弹层标题栏用）。 */
export function useUserPickerPageTitle(kind: SecondaryPage): string {
  const { t } = useAppTranslation("people");
  return kind === "store" ? t("sheet.storePageTitle") : kind === "role" ? t("picker.rolePageTitle") : t("picker.sortPageTitle");
}

/** 二级选择页：选一项立即回传（调用方切回主页），不需要「确定」按钮。 */
export function UserPickerPage({
  kind,
  filters,
  storeOptions,
  roleOptions,
  myStoreGuids,
  onChange,
}: {
  kind: SecondaryPage;
  filters: UserPickerFilters;
  storeOptions: readonly PickerOption[];
  roleOptions: readonly PickerOption[];
  myStoreGuids: readonly string[];
  onChange: (next: Partial<UserPickerFilters>) => void;
}) {
  const { t } = useAppTranslation("people");
  if (kind === "store") {
    return (
      <AdminStorePickerPage
        options={[...storeOptions]}
        myStoreGuids={myStoreGuids}
        selected={filters.storeGuid}
        onSelect={(storeGuid) => onChange({ storeGuid })}
      />
    );
  }
  if (kind === "role") {
    const rows = [{ value: "", label: t("picker.allRoles") }, ...roleOptions];
    return (
      <View style={styles.list}>
        {rows.map((option) => (
          <OptionRow key={option.value || "all"} label={option.label} selected={filters.roleGuid === option.value} onPress={() => onChange({ roleGuid: option.value })} />
        ))}
      </View>
    );
  }
  return (
    <View style={styles.list}>
      {USER_PICKER_SORT_ORDER.map((key) => (
        <OptionRow
          key={key}
          label={t(`admin.sort.${key}`)}
          caption={t(`picker.sortHint.${key}`)}
          selected={filters.sort === key}
          onPress={() => onChange({ sort: key })}
        />
      ))}
    </View>
  );
}

function OptionRow({ label, caption, selected, onPress }: { label: string; caption?: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="radio" accessibilityState={{ selected, checked: selected }} onPress={onPress} style={styles.row}>
      <View style={styles.rowText}>
        <Text style={[styles.rowLabel, selected && styles.rowLabelSelected]} numberOfLines={1}>{label}</Text>
        {caption ? <Text style={styles.rowCaption} numberOfLines={1}>{caption}</Text> : null}
      </View>
      {selected ? <Icon source="check" size={20} color={C.brand} /> : <View style={styles.checkSpacer} />}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  // 值很长（如 Warehouse Manager）时允许换到第二行，不截断。
  bar: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  chip: {
    alignItems: "center",
    backgroundColor: C.white,
    borderColor: C.outline,
    borderRadius: 18,
    borderWidth: 1,
    flexDirection: "row",
    gap: 4,
    minHeight: 36,
    paddingHorizontal: 10,
  },
  chipActive: { backgroundColor: SELECTED_BG, borderColor: SELECTED_LINE },
  chipDisabled: { opacity: 0.45 },
  chipKey: { color: C.textSecondary, fontSize: 14 },
  chipValue: { color: C.textPrimary, fontSize: 14, fontWeight: "700", maxWidth: 140 },
  chipValueActive: { color: C.action },
  list: { backgroundColor: C.white, borderColor: C.outlineMuted, borderRadius: 12, borderWidth: 1, overflow: "hidden" },
  row: { alignItems: "center", borderBottomColor: C.outlineMuted, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: "row", gap: 8, minHeight: 52, paddingHorizontal: 14 },
  rowText: { flex: 1, gap: 1, minWidth: 0 },
  rowLabel: { color: C.textPrimary, fontSize: 15 },
  rowLabelSelected: { color: C.action, fontWeight: "700" },
  rowCaption: { color: C.textSecondary, fontSize: 12.5 },
  checkSpacer: { width: 20 },
});
