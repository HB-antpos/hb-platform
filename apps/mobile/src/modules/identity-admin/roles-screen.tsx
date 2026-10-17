import { useEffect, useMemo, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { ActivityIndicator, Icon, Text } from "react-native-paper";
import { ListCardSegment, LoadMoreFooter, PeopleScreenFrame, PeopleSearchBox, type PeopleHubChrome } from "@/modules/people/people-frame";
import { resolveRowPosition, type ListRowPosition } from "@/modules/people/list-position";
import { FloatingAction, PEOPLE_TONES, PeopleTag } from "@/modules/people/ui";
import { localizeAccessRoleDescription, localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { resolveNextPage } from "./admin-user-list-logic";
import { fetchIdentityRoles } from "./api";
import { getRoleCapabilities } from "./role-logic";
import { flattenRolePages, resolveRoleListTags, resolveRoleVisual, type RoleListTagKey } from "./role-permission-list-logic";
import type { IdentityRole } from "./types";
import { AdminEmpty, AdminError } from "./ui";
import { useIdentitySearch, useIdentitySession } from "./user-hooks";

/** 角色总共十来个：一页取 50 个，基本一次取完；真的更多时下拉再加载。 */
const ROLE_PAGE_SIZE = 50;

const TAG_STYLE: Record<RoleListTagKey, { tone: "violet" | "blue" | "gray"; icon?: string }> = {
  system: { tone: "gray", icon: "lock-outline" },
  derived: { tone: "blue", icon: "source-branch" },
  disabled: { tone: "gray" },
};

/**
 * 「人员管理」里的「角色」页签（设计稿 A6①）：卡片列表，系统 / 派生 / 停用标签，每个角色显示人数。
 * 点击进入现有角色详情（roles/[roleGuid]）；右下角悬浮「新增角色」。
 * 只有满足 roles 入口门禁（account 会话、非设备、非 iOS 审核、Roles.View）才会被 Hub 渲染。
 */
export function RolesList({ chrome }: { chrome: PeopleHubChrome }) {
  const router = useRouter();
  const { t, language } = useAppTranslation(["people", "common"]);
  const { access, allowed, actorKey } = useIdentitySession("Roles.View");
  const [input, setInput] = useState("");
  const search = useIdentitySearch(input);
  const capabilities = useMemo(
    () => getRoleCapabilities({ isDeviceMode: !allowed, isAdmin: access.isAdmin, hasPermission: access.hasPermission }),
    [access.hasPermission, access.isAdmin, allowed]
  );

  const rolesQuery = useInfiniteQuery({
    queryKey: ["identity-admin", actorKey, "roles", "infinite", search],
    enabled: allowed,
    retry: false,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) =>
      fetchIdentityRoles({ page: pageParam, pageSize: ROLE_PAGE_SIZE, searchKeyword: search || undefined }, actorKey, signal),
    getNextPageParam: (lastPage) => resolveNextPage(lastPage),
  });
  const { refetch, fetchNextPage, hasNextPage, isFetchingNextPage } = rolesQuery;
  const roles = useMemo(() => flattenRolePages(rolesQuery.data?.pages ?? []), [rolesQuery.data]);

  // 标题栏「角色 · N 个」是总数：搜索时保留上一次没有搜索词的总数。
  const total = rolesQuery.data?.pages[0]?.total;
  const [headerTotal, setHeaderTotal] = useState<number | null>(null);
  useEffect(() => {
    if (typeof total === "number" && !search) setHeaderTotal(total);
  }, [search, total]);

  if (!allowed) {
    return (
      <PeopleScreenFrame title={t("people:hub.title")} chrome={chrome}>
        <AdminEmpty text={t("people:rolesList.noAccess")} />
      </PeopleScreenFrame>
    );
  }

  const openRole = (role: IdentityRole) =>
    router.push({ pathname: "/roles/[roleGuid]", params: { roleGuid: role.roleGUID } });

  return (
    <PeopleScreenFrame
      title={t("people:hub.title")}
      subtitle={headerTotal !== null ? t("people:subtitle.roles", { count: headerTotal }) : null}
      chrome={chrome}
    >
      <View style={styles.filterArea}>
        <PeopleSearchBox value={input} onChangeText={setInput} placeholder={t("people:search.roles")} />
      </View>

      {rolesQuery.isPending ? (
        <ActivityIndicator style={styles.loader} />
      ) : (
        <FlatList
          data={roles}
          keyExtractor={(role) => role.roleGUID}
          keyboardShouldPersistTaps="handled"
          refreshControl={<RefreshControl refreshing={rolesQuery.isRefetching && !isFetchingNextPage} onRefresh={() => void refetch()} />}
          contentContainerStyle={styles.listContent}
          onEndReachedThreshold={0.4}
          onEndReached={() => {
            if (hasNextPage && !isFetchingNextPage) void fetchNextPage();
          }}
          ListEmptyComponent={
            rolesQuery.isError ? (
              <AdminError error={rolesQuery.error} onRetry={() => void refetch()} />
            ) : (
              <AdminEmpty text={t("people:rolesList.empty")} />
            )
          }
          ListFooterComponent={<LoadMoreFooter loading={isFetchingNextPage} />}
          renderItem={({ item, index }) => (
            <RoleRow
              role={item}
              position={resolveRowPosition(index, roles.length)}
              language={language}
              onPress={() => openRole(item)}
            />
          )}
        />
      )}

      {capabilities.canCreate ? (
        <FloatingAction
          label={t("people:rolesList.add")}
          icon="plus"
          onPress={() => router.push({ pathname: "/roles/[roleGuid]", params: { roleGuid: "new" } })}
        />
      ) : null}
    </PeopleScreenFrame>
  );
}

function RoleRow({
  role,
  position,
  language,
  onPress,
}: {
  role: IdentityRole;
  position: ListRowPosition;
  language: "zh" | "en";
  onPress: () => void;
}) {
  const { t } = useAppTranslation("people");
  const visual = resolveRoleVisual(role.roleName);
  const tone = PEOPLE_TONES[visual.tone];
  const tags = resolveRoleListTags(role.roleName, role.isActive);
  const name = localizeAccessRoleName(role.roleName, language);
  // 店长由「管理」分店派生：说明这件事比后端的通用描述更有用。
  const description = tags.includes("derived")
    ? t("rolesList.derivedHint")
    : localizeAccessRoleDescription(role.roleName, role.description, language);
  return (
    <ListCardSegment position={position}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${name} ${t("rolesList.memberCount", { count: role.userCount })}`}
        onPress={onPress}
        style={[styles.row, position !== "first" && position !== "only" && styles.rowDivider]}
      >
        <View style={[styles.roleIcon, { backgroundColor: tone.bg }]}>
          <Icon source={visual.icon} size={19} color={tone.fg} />
        </View>
        <View style={styles.rowMain}>
          <View style={styles.nameRow}>
            <Text style={styles.roleName} numberOfLines={1}>{name}</Text>
            {tags.map((key) => (
              <PeopleTag key={key} label={t(`rolesList.tags.${key}`)} tone={TAG_STYLE[key].tone} icon={TAG_STYLE[key].icon} />
            ))}
          </View>
          {description ? <Text style={styles.roleDescription} numberOfLines={1}>{description}</Text> : null}
        </View>
        <Text style={styles.memberCount}>{t("rolesList.memberCount", { count: role.userCount })}</Text>
        <Icon source="chevron-right" size={20} color="#98A2B3" />
      </Pressable>
    </ListCardSegment>
  );
}

const styles = StyleSheet.create({
  filterArea: {
    backgroundColor: HB_COLORS.white,
    paddingBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
  },
  listContent: { padding: HB_SPACING.md, paddingBottom: 112 },
  loader: { margin: 32 },
  memberCount: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  nameRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  roleDescription: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 2 },
  roleIcon: { alignItems: "center", borderRadius: 10, height: 36, justifyContent: "center", width: 36 },
  roleName: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 15, fontWeight: "700" },
  row: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 56, paddingHorizontal: 14, paddingVertical: 10 },
  rowDivider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  rowMain: { flex: 1, minWidth: 0 },
});
