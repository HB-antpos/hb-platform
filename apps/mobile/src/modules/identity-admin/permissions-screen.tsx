import { useMemo, useState } from "react";
import { FlatList, Platform, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Icon, Text } from "react-native-paper";
import { ListCardSegment, PeopleScreenFrame, PeopleSearchBox, type PeopleHubChrome } from "@/modules/people/people-frame";
import { FilterChipRow, FloatingAction, PEOPLE_TONES, PeopleTag } from "@/modules/people/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { filterPermissionListItems, groupPermissionsByCategory, type PermissionListItem } from "./permission-logic";
import { usePermissionCopy, usePermissionItems, usePermissionRoleCounts, usePermissionSession } from "./permission-hooks";
import {
  buildPermissionCategoryChips,
  buildPermissionListRows,
  resolvePermissionRoleCount,
  type PermissionListRow,
} from "./role-permission-list-logic";
import { AdminEmpty, AdminError } from "./ui";
import { useIdentitySearch } from "./user-hooks";

/**
 * 「人员管理」里的「权限」页签（设计稿 A6③）：分类芯片（带数量）、按分类分组的权限卡片、
 * 权限码等宽字体、「被几个角色使用」。点击进入现有权限详情（permissions/[code]）；
 * 新增仍走现有「新建权限」页，右下角悬浮「新增权限」。
 */
export function PermissionsList({ chrome }: { chrome: PeopleHubChrome }) {
  const router = useRouter();
  const { t } = useAppTranslation(["people", "common"]);
  const { appLanguage } = usePermissionCopy();
  const { allowed, actorKey, capabilities } = usePermissionSession();
  const [input, setInput] = useState("");
  const search = useIdentitySearch(input);
  const [categoryKey, setCategoryKey] = useState("");

  const permissions = usePermissionItems({ actorKey, enabled: allowed, appLanguage });
  const roleCountsQuery = usePermissionRoleCounts({ actorKey, enabled: allowed });

  const allGroups = useMemo(() => groupPermissionsByCategory(permissions.items), [permissions.items]);
  const visibleGroups = useMemo(
    () => groupPermissionsByCategory(filterPermissionListItems(permissions.items, { keyword: search, categoryKey })),
    [categoryKey, permissions.items, search]
  );
  const rows = useMemo(() => buildPermissionListRows(visibleGroups), [visibleGroups]);
  const chips = useMemo(
    () => buildPermissionCategoryChips(allGroups, permissions.items.length, t("people:permList.all")),
    [allGroups, permissions.items.length, t]
  );
  const refreshAll = () => {
    void permissions.refetch();
    void roleCountsQuery.refetch();
  };

  if (!allowed) {
    return (
      <PeopleScreenFrame title={t("people:hub.title")} chrome={chrome}>
        <AdminEmpty text={t("people:permList.noAccess")} />
      </PeopleScreenFrame>
    );
  }

  return (
    <PeopleScreenFrame
      title={t("people:hub.title")}
      subtitle={
        permissions.items.length
          ? t("people:subtitle.permissions", { count: permissions.items.length, categories: allGroups.length })
          : null
      }
      chrome={chrome}
    >
      <View style={styles.filterArea}>
        <PeopleSearchBox value={input} onChangeText={setInput} placeholder={t("people:search.permissions")} />
        {allGroups.length > 0 ? (
          <FilterChipRow items={chips} value={categoryKey} onChange={setCategoryKey} />
        ) : null}
      </View>

      {permissions.isPending ? (
        <ActivityIndicator style={styles.loader} />
      ) : (
        <FlatList
          data={rows}
          keyExtractor={(row) => row.key}
          keyboardShouldPersistTaps="handled"
          refreshControl={<RefreshControl refreshing={permissions.isRefetching} onRefresh={refreshAll} />}
          contentContainerStyle={styles.listContent}
          ListHeaderComponent={
            roleCountsQuery.error ? <Text style={styles.roleCountHint}>{t("people:permList.roleCountUnavailable")}</Text> : null
          }
          ListEmptyComponent={
            permissions.error ? (
              <AdminError error={permissions.error} onRetry={refreshAll} />
            ) : (
              <AdminEmpty text={t("people:permList.empty")} />
            )
          }
          renderItem={({ item: row }) =>
            row.kind === "header" ? (
              <Text style={styles.groupTitle}>{`${row.title} · ${row.count}`}</Text>
            ) : (
              <PermissionRow
                row={row}
                roleCount={resolvePermissionRoleCount(roleCountsQuery.data, row.item.code)}
                onPress={() => router.push({ pathname: "/(shell)/permissions/[code]", params: { code: row.item.code } })}
              />
            )
          }
        />
      )}

      {capabilities.canManage ? (
        <FloatingAction label={t("people:permList.add")} icon="plus" onPress={() => router.push("/(shell)/permissions/new")} />
      ) : null}
    </PeopleScreenFrame>
  );
}

function PermissionRow({
  row,
  roleCount,
  onPress,
}: {
  row: Extract<PermissionListRow, { kind: "item" }>;
  roleCount: number | null;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("people");
  const item: PermissionListItem = row.item;
  const tone = PEOPLE_TONES.gray;
  return (
    <ListCardSegment position={row.position}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${item.name} ${item.code}`}
        onPress={onPress}
        style={[styles.row, row.position !== "first" && row.position !== "only" && styles.rowDivider]}
      >
        <View style={[styles.keyIcon, { backgroundColor: tone.bg }]}>
          <Icon source="key-outline" size={19} color={tone.fg} />
        </View>
        <View style={styles.rowMain}>
          <View style={styles.nameRow}>
            <Text style={styles.permissionName} numberOfLines={1}>{item.name}</Text>
            <PeopleTag
              label={item.isSystem ? t("permList.system") : t("permList.custom")}
              tone={item.isSystem ? "gray" : "blue"}
              icon={item.isSystem ? "lock-outline" : undefined}
            />
          </View>
          {/* 权限码用等宽字体，方便核对大小写与点号分段。 */}
          <Text style={styles.permissionCode} numberOfLines={1}>{item.code}</Text>
        </View>
        {roleCount !== null ? <Text style={styles.roleCount}>{t("permList.roleCount", { count: roleCount })}</Text> : null}
        <Icon source="chevron-right" size={20} color="#98A2B3" />
      </Pressable>
    </ListCardSegment>
  );
}

const styles = StyleSheet.create({
  filterArea: {
    backgroundColor: HB_COLORS.white,
    gap: 8,
    paddingBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
  },
  groupTitle: { color: "#667085", fontSize: 12.5, fontWeight: "700", paddingBottom: 6, paddingHorizontal: 4, paddingTop: 12 },
  keyIcon: { alignItems: "center", borderRadius: 10, height: 36, justifyContent: "center", width: 36 },
  listContent: { padding: HB_SPACING.md, paddingBottom: 112 },
  loader: { margin: 32 },
  nameRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  permissionCode: { color: HB_COLORS.textSecondary, fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }), fontSize: 12, marginTop: 2 },
  permissionName: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 14.5, fontWeight: "700" },
  roleCount: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  roleCountHint: { color: HB_COLORS.textSecondary, fontSize: 12, paddingBottom: 4, paddingHorizontal: 4 },
  row: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 56, paddingHorizontal: 14, paddingVertical: 10 },
  rowDivider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  rowMain: { flex: 1, minWidth: 0 },
});
