/**
 * 管理员版「新增账号」的两个底部选择弹层：角色（多选）与分店（每家三档：未分配 / 查看 / 管理）。
 *
 * 弹层里的改动即时写回表单（不另设「确认」步骤），「完成」只负责关闭；
 * 「管理」档的店长角色由后端按管理分店派生，所以角色清单里不出现店长角色（由调用方过滤）。
 */
import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Checkbox, Icon, Text, TextInput } from "react-native-paper";

import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { getUserAccessStoreState, setUserAccessStoreState } from "@/modules/users/access-management";
import type { AccessStoreOption, UserAccessStoreAssignment, UserAccessStoreState } from "@/modules/users/access-management-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

export interface RoleOption {
  roleGUID: string;
  roleName: string;
  description?: string;
}

function SheetSearch({ value, onChange, placeholder }: { value: string; onChange: (value: string) => void; placeholder: string }) {
  return (
    <TextInput
      mode="outlined"
      dense
      value={value}
      onChangeText={onChange}
      placeholder={placeholder}
      accessibilityLabel={placeholder}
      left={<TextInput.Icon icon="magnify" />}
      autoCapitalize="none"
      autoCorrect={false}
      returnKeyType="search"
      outlineStyle={styles.searchOutline}
      right={value ? <TextInput.Icon icon="close" onPress={() => onChange("")} /> : undefined}
    />
  );
}

export function RolePickerSheet({
  visible,
  title,
  roles,
  selected,
  onChange,
  onDismiss,
  loading,
  errorText,
}: {
  visible: boolean;
  title: string;
  roles: RoleOption[];
  selected: string[];
  onChange: (next: string[]) => void;
  onDismiss: () => void;
  loading?: boolean;
  errorText?: string;
}) {
  const { t } = useAppTranslation("peopleForm");
  const [search, setSearch] = useState("");
  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return needle ? roles.filter((role) => role.roleName.toLowerCase().includes(needle)) : roles;
  }, [roles, search]);

  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={t("adminCreate.rolesSheetHint")}
      onDismiss={onDismiss}
      footer={<Button mode="contained" buttonColor={HB_COLORS.action} onPress={onDismiss}>{t("form.done")}</Button>}
    >
      <SheetSearch value={search} onChange={setSearch} placeholder={t("form.searchOptions")} />
      {errorText ? <Text style={styles.errorText}>{errorText}</Text> : null}
      {loading ? <Text style={styles.muted}>{t("form.loading")}</Text> : null}
      {!loading && !errorText && filtered.length === 0 ? <Text style={styles.muted}>{t("form.noOptions")}</Text> : null}
      {filtered.map((role) => {
        const checked = selected.includes(role.roleGUID);
        return (
          <Pressable
            key={role.roleGUID}
            accessibilityRole="checkbox"
            accessibilityState={{ checked }}
            onPress={() => onChange(checked ? selected.filter((guid) => guid !== role.roleGUID) : [...selected, role.roleGUID])}
            style={styles.row}
          >
            <View style={styles.rowText}>
              <Text style={styles.rowTitle}>{role.roleName}</Text>
              {role.description ? <Text style={styles.rowSubtitle} numberOfLines={2}>{role.description}</Text> : null}
            </View>
            <Checkbox status={checked ? "checked" : "unchecked"} />
          </Pressable>
        );
      })}
    </BusinessSheet>
  );
}

const STORE_STATES: UserAccessStoreState[] = ["unassigned", "view", "manage"];

export function StoreAssignmentSheet({
  visible,
  title,
  stores,
  assignments,
  onChange,
  onDismiss,
  loading,
  errorText,
}: {
  visible: boolean;
  title: string;
  stores: AccessStoreOption[];
  assignments: UserAccessStoreAssignment[];
  onChange: (next: UserAccessStoreAssignment[]) => void;
  onDismiss: () => void;
  loading?: boolean;
  errorText?: string;
}) {
  const { t } = useAppTranslation("peopleForm");
  const [search, setSearch] = useState("");
  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return needle
      ? stores.filter((store) => `${store.storeName} ${store.storeCode}`.toLowerCase().includes(needle))
      : stores;
  }, [stores, search]);
  const manageCount = assignments.filter((assignment) => assignment.isPrimary).length;

  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={t("adminCreate.storesSheetHint", { total: assignments.length, manage: manageCount })}
      onDismiss={onDismiss}
      footer={<Button mode="contained" buttonColor={HB_COLORS.action} onPress={onDismiss}>{t("form.done")}</Button>}
    >
      <SheetSearch value={search} onChange={setSearch} placeholder={t("form.searchOptions")} />
      {errorText ? <Text style={styles.errorText}>{errorText}</Text> : null}
      {loading ? <Text style={styles.muted}>{t("form.loading")}</Text> : null}
      {!loading && !errorText && filtered.length === 0 ? <Text style={styles.muted}>{t("form.noOptions")}</Text> : null}
      {filtered.map((store) => {
        const state = getUserAccessStoreState(assignments, store.storeGUID);
        return (
          <View key={store.storeGUID} style={styles.storeRow}>
            <View style={styles.rowText}>
              <Text style={styles.rowTitle} numberOfLines={1}>{store.storeName}</Text>
              <Text style={styles.rowSubtitle} numberOfLines={1}>
                {store.isActive === false ? `${store.storeCode} · ${t("adminCreate.storeInactive")}` : store.storeCode}
              </Text>
            </View>
            <View style={styles.stateGroup} accessibilityRole="radiogroup">
              {STORE_STATES.map((option) => {
                const selected = option === state;
                return (
                  <Pressable
                    key={option}
                    accessibilityRole="radio"
                    accessibilityLabel={`${store.storeName} ${t(`adminCreate.storeState.${option}`)}`}
                    accessibilityState={{ selected }}
                    onPress={() => onChange(setUserAccessStoreState(assignments, store.storeGUID, option))}
                    style={[styles.stateItem, selected && (option === "manage" ? styles.stateManage : styles.stateSelected)]}
                  >
                    {selected && option === "manage" ? <Icon source="shield-account-outline" size={13} color={HB_COLORS.action} /> : null}
                    <Text style={[styles.stateText, selected && (option === "manage" ? styles.stateTextManage : styles.stateTextSelected)]}>
                      {t(`adminCreate.storeState.${option}`)}
                    </Text>
                  </Pressable>
                );
              })}
            </View>
          </View>
        );
      })}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  searchOutline: { borderColor: HB_COLORS.outlineMuted, borderRadius: 8 },
  muted: { color: HB_COLORS.textSecondary, fontSize: 13, paddingVertical: 8, textAlign: "center" },
  errorText: { color: HB_COLORS.danger, fontSize: 13 },
  row: { alignItems: "center", borderBottomColor: HB_COLORS.outlineMuted, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: "row", gap: 8, minHeight: 52, paddingVertical: 6 },
  rowText: { flex: 1, minWidth: 0 },
  rowTitle: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "700" },
  rowSubtitle: { color: HB_COLORS.textSecondary, fontSize: 12, marginTop: 1 },
  storeRow: { alignItems: "center", borderBottomColor: HB_COLORS.outlineMuted, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: "row", gap: 8, minHeight: 56, paddingVertical: 6 },
  stateGroup: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 8, flexDirection: "row", gap: 2, padding: 2 },
  stateItem: { alignItems: "center", borderRadius: 6, flexDirection: "row", gap: 2, justifyContent: "center", minHeight: 32, minWidth: 48, paddingHorizontal: 8 },
  stateSelected: { backgroundColor: HB_COLORS.white },
  stateManage: { backgroundColor: "#EFF6FF" },
  stateText: { color: HB_COLORS.textSecondary, fontSize: 12.5, fontWeight: "600" },
  stateTextSelected: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  stateTextManage: { color: HB_COLORS.action, fontWeight: "800" },
});
