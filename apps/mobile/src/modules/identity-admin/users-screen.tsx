import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useFocusEffect, useRouter } from "expo-router";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { ActivityIndicator, Icon, Text } from "react-native-paper";
import { CountChip, ChipScroller, DropdownChip, LoadMoreFooter, PeopleScreenFrame, PeopleSearchBox, type PeopleHubChrome } from "@/modules/people/people-frame";
import { PersonListRow } from "@/modules/people/person-list-row";
import {
  buildIdentityPersonItem,
  type IdentityPersonFormatters,
  type PersonListItem,
} from "@/modules/people/person-list-item";
import { resolveRowPosition } from "@/modules/people/list-position";
import { FloatingAction } from "@/modules/people/ui";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { sortStoresByName } from "@/shared/utils/name-sort";
import {
  DEFAULT_ADMIN_USER_FILTERS,
  buildAdminCountQuery,
  buildAdminFilterKey,
  buildAdminUserQuery,
  flattenUserPages,
  isAdminSheetDefault,
  isUnfilteredAdminList,
  resolveNextPage,
  type AdminStatusFilter,
  type AdminUserFilters,
} from "./admin-user-list-logic";
import { fetchAccessRoleCatalog, fetchAccessStoreCatalog, fetchIdentityUsers } from "./api";
import { AdminUserFilterSheet } from "./admin-user-filter-sheet";
import { AdminEmpty, AdminError } from "./ui";
import type { IdentityUser } from "./types";
import { managedIdentityStores, selectableIdentityRoles } from "./user-logic";
import { useIdentitySearch, useIdentitySession } from "./user-hooks";

interface AdminUserEntry {
  user: IdentityUser;
  person: PersonListItem;
}

/**
 * 「人员管理」里管理员（全局用户查看 + account 会话）看到的「人员」页签：全局用户列表（设计稿 A1②）。
 * 保留原 /Users/optimized 的服务端筛选与分页参数；分页改成「下拉加载更多」并累加页数据，
 * 分店 / 角色 / 状态 / 排序在一个底部弹层里一次选好（AdminUserFilterSheet）。
 */
export function AdminUserList({ chrome, active }: { chrome: PeopleHubChrome; active: boolean }) {
  const router = useRouter();
  const { t, language } = useAppTranslation(["people", "common"]);
  const { user, access, allowed, actorKey } = useIdentitySession();
  const [input, setInput] = useState("");
  const search = useIdentitySearch(input);
  const [applied, setApplied] = useState<Omit<AdminUserFilters, "search">>({
    storeGuid: DEFAULT_ADMIN_USER_FILTERS.storeGuid,
    roleGuid: DEFAULT_ADMIN_USER_FILTERS.roleGuid,
    status: DEFAULT_ADMIN_USER_FILTERS.status,
    sort: DEFAULT_ADMIN_USER_FILTERS.sort,
  });
  const [sheetVisible, setSheetVisible] = useState(false);
  const filters = useMemo<AdminUserFilters>(() => ({ search, ...applied }), [applied, search]);
  const scoped = access.isStoreLevelManager;

  // 分店 / 角色选项：与原列表同一组接口与权限门禁（门店级管理者只能选自己管理的分店与「店员」角色）。
  const storesQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "store-options"],
    queryFn: ({ signal }) => fetchAccessStoreCatalog(actorKey, signal),
    enabled: allowed && !scoped,
    retry: false,
  });
  const canReadRoleOptions = access.canReadRole || access.hasPermission("Users.ManageRoles");
  const rolesQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "role-options"],
    queryFn: ({ signal }) => fetchAccessRoleCatalog(actorKey, signal),
    enabled: allowed && canReadRoleOptions,
    retry: false,
  });
  const storeOptions = useMemo(
    () =>
      // 分店筛选项统一按分店名字排序（接口 / 用户资料里的原序不保证）。
      sortStoresByName(scoped ? managedIdentityStores(user) : storesQuery.data ?? [], language).map((store) => ({
        value: store.storeGUID,
        label: `${store.storeName} · ${store.storeCode}`,
      })),
    [language, scoped, storesQuery.data, user]
  );
  const roleOptions = useMemo(
    () =>
      selectableIdentityRoles(rolesQuery.data ?? [], scoped).map((role) => ({
        value: role.roleGUID,
        label: localizeAccessRoleName(role.roleName, language),
      })),
    [language, rolesQuery.data, scoped]
  );

  // 服务端分页：翻页参数沿用原来的 page / pageSize，下拉到底再取下一页并累加。筛选变化即换 key，从第一页重新开始。
  const usersQuery = useInfiniteQuery({
    queryKey: ["identity-admin", actorKey, "users", "infinite", ...buildAdminFilterKey(filters)],
    enabled: allowed,
    retry: false,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => fetchIdentityUsers(buildAdminUserQuery(filters, pageParam), actorKey, signal),
    getNextPageParam: (lastPage) => resolveNextPage(lastPage),
  });
  const { refetch, fetchNextPage, hasNextPage, isFetchingNextPage } = usersQuery;

  // 「启用 N / 停用 N」芯片上的人数：只带搜索 / 分店 / 角色，不带状态，所以点选状态时数字不会变。
  const activeCountQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "users-count", filters.search, filters.storeGuid, filters.roleGuid, "active"],
    enabled: allowed,
    retry: false,
    staleTime: 30_000,
    queryFn: ({ signal }) => fetchIdentityUsers(buildAdminCountQuery(filters, true), actorKey, signal),
    select: (result) => result.total,
  });
  const inactiveCountQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "users-count", filters.search, filters.storeGuid, filters.roleGuid, "inactive"],
    enabled: allowed,
    retry: false,
    staleTime: 30_000,
    queryFn: ({ signal }) => fetchIdentityUsers(buildAdminCountQuery(filters, false), actorKey, signal),
    select: (result) => result.total,
  });

  // 从详情页 / 新增页回来时刷新一次（首次进入由查询自己取，不重复请求；页签被隐藏时不刷新）。
  const focusedOnce = useRef(false);
  useFocusEffect(
    useCallback(() => {
      if (!focusedOnce.current) {
        focusedOnce.current = true;
        return;
      }
      if (allowed && active) void refetch();
    }, [active, allowed, refetch])
  );

  const items = useMemo(() => flattenUserPages(usersQuery.data?.pages ?? []), [usersQuery.data]);
  const total = usersQuery.data?.pages[0]?.total;

  // 标题栏「全部分店 · N 人」是总人数，只在没有任何筛选时才用当前结果更新，筛选后保留上一次的总数。
  const [headerTotal, setHeaderTotal] = useState<number | null>(null);
  useEffect(() => {
    if (typeof total === "number" && isUnfilteredAdminList(filters)) setHeaderTotal(total);
  }, [filters, total]);

  const formatters = useMemo<IdentityPersonFormatters>(
    () => ({
      roleName: (roleName) => localizeAccessRoleName(roleName, language),
      stores: (summary) => {
        if (summary.scope === "all") return t("people:adminStores.all");
        if (summary.scope === "none") return t("people:adminStores.none");
        const base =
          summary.relation === "manage"
            ? t("people:adminStores.manage", { name: summary.firstName })
            : summary.relation === "view"
              ? t("people:adminStores.view", { name: summary.firstName })
              : summary.firstName;
        return summary.extraCount > 0 ? `${base} +${summary.extraCount}` : base;
      },
      lastLogin: (login) =>
        login ? t(`people:login.${login.unit}`, { count: "count" in login ? login.count : 0 }) : t("people:login.never"),
    }),
    [language, t]
  );
  const entries = useMemo<AdminUserEntry[]>(() => {
    const now = Date.now();
    return items.map((item) => ({ user: item, person: buildIdentityPersonItem(item, formatters, now) }));
  }, [formatters, items]);

  const openDetail = useCallback(
    (entry: AdminUserEntry) => {
      // 旧的预填参数只作首屏占位，详情页会按 userGuid 自己拉取目标。
      router.push({
        pathname: "/users/[userGuid]",
        params: {
          userGuid: entry.user.userGUID,
          userName: entry.person.name,
          username: entry.user.username,
          targetStatus: entry.user.isActive ? "1" : "0",
          roleNames: entry.user.roleNames.join("|"),
        },
      } as unknown as Parameters<typeof router.push>[0]);
    },
    [router]
  );
  const openCreate = useCallback(() => {
    // 管理员版新增：不带 storeCode（带 storeCode 会进入店长版表单）。
    router.push({ pathname: "/users/new" } as unknown as Parameters<typeof router.push>[0]);
  }, [router]);

  const selectedStoreLabel = storeOptions.find((option) => option.value === filters.storeGuid)?.label.split(" · ")[0];
  const selectedRoleLabel = roleOptions.find((option) => option.value === filters.roleGuid)?.label;
  const toggleStatus = useCallback((status: AdminStatusFilter) => {
    setApplied((current) => ({ ...current, status: current.status === status ? "all" : status }));
  }, []);
  const showStoreSection = scoped ? storeOptions.length > 0 : true;
  const showRoleSection = canReadRoleOptions;
  const canCreate = access.isAdmin && access.hasPermission("Users.Create");

  if (!allowed) {
    return (
      <PeopleScreenFrame title={t("people:hub.title")} chrome={chrome}>
        <AdminEmpty text={t("people:admin.noAccess")} />
      </PeopleScreenFrame>
    );
  }

  const renderItem = ({ item, index }: { item: AdminUserEntry; index: number }) => (
    <PersonListRow
      item={item.person}
      position={resolveRowPosition(index, entries.length)}
      onPress={() => openDetail(item)}
      right={<Icon source="chevron-right" size={20} color="#98A2B3" />}
    />
  );

  return (
    <PeopleScreenFrame
      title={t("people:hub.title")}
      subtitle={headerTotal !== null ? t("people:subtitle.adminAll", { count: headerTotal }) : t("people:subtitle.adminAllNoCount")}
      chrome={chrome}
    >
      <View style={styles.filterArea}>
        <PeopleSearchBox value={input} onChangeText={setInput} placeholder={t("people:search.admin")} />
        {/* 分店、角色做成下拉芯片，和状态芯片在同一行；点开同一个筛选弹层。 */}
        <ChipScroller>
          {showStoreSection ? (
            <DropdownChip
              label={t("people:admin.filterStore", { value: selectedStoreLabel ?? t("people:sheet.all") })}
              active={Boolean(filters.storeGuid)}
              onPress={() => setSheetVisible(true)}
            />
          ) : null}
          {showRoleSection ? (
            <DropdownChip
              label={t("people:admin.filterRole", { value: selectedRoleLabel ?? t("people:sheet.all") })}
              active={Boolean(filters.roleGuid)}
              onPress={() => setSheetVisible(true)}
            />
          ) : null}
          <CountChip
            label={t("people:admin.status.active")}
            count={activeCountQuery.data ?? null}
            selected={filters.status === "active"}
            onPress={() => toggleStatus("active")}
          />
          <CountChip
            label={t("people:admin.status.inactive")}
            count={inactiveCountQuery.data ?? null}
            selected={filters.status === "inactive"}
            onPress={() => toggleStatus("inactive")}
          />
        </ChipScroller>
      </View>

      {/* 计数与排序合成一行。 */}
      <View style={styles.countRow}>
        <Text style={styles.countText}>
          <Text style={styles.countNumber}>{typeof total === "number" ? total : "–"}</Text>
          {t("people:admin.countSuffix")}
        </Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("people:sheet.title")}
          onPress={() => setSheetVisible(true)}
          style={styles.sortButton}
          hitSlop={8}
        >
          <Icon source="sort" size={17} color={isAdminSheetDefault(filters) ? HB_COLORS.textSecondary : HB_COLORS.action} />
          <Text style={styles.sortText}>{t(`people:admin.sort.${filters.sort}`)}</Text>
        </Pressable>
      </View>

      {usersQuery.isPending ? (
        <ActivityIndicator style={styles.loader} />
      ) : (
        <FlatList
          data={entries}
          keyExtractor={(entry) => entry.user.userGUID}
          renderItem={renderItem}
          keyboardShouldPersistTaps="handled"
          refreshControl={<RefreshControl refreshing={usersQuery.isRefetching && !isFetchingNextPage} onRefresh={() => void refetch()} />}
          contentContainerStyle={styles.listContent}
          onEndReachedThreshold={0.4}
          onEndReached={() => {
            if (hasNextPage && !isFetchingNextPage) void fetchNextPage();
          }}
          ListEmptyComponent={
            usersQuery.isError ? (
              <AdminError error={usersQuery.error} onRetry={() => void refetch()} />
            ) : (
              <AdminEmpty text={t("people:admin.empty")} />
            )
          }
          ListFooterComponent={
            // 翻页失败时保留已加载的行，只在末尾给出重试入口。
            usersQuery.isError && entries.length > 0 ? (
              <AdminError error={usersQuery.error} onRetry={() => void fetchNextPage()} />
            ) : (
              <LoadMoreFooter
                loading={isFetchingNextPage}
                endLabel={!hasNextPage && entries.length > 0 ? t("people:admin.endOfList", { count: entries.length }) : null}
              />
            )
          }
        />
      )}

      {canCreate ? <FloatingAction label={t("people:admin.create")} icon="plus" onPress={openCreate} /> : null}

      <AdminUserFilterSheet
        visible={sheetVisible}
        filters={filters}
        actorKey={actorKey}
        allowed={allowed}
        storeOptions={storeOptions}
        roleOptions={roleOptions}
        showStoreSection={showStoreSection}
        showRoleSection={showRoleSection}
        storesError={storesQuery.isError}
        rolesError={rolesQuery.isError}
        onRetry={() => {
          if (storesQuery.isError) void storesQuery.refetch();
          if (rolesQuery.isError) void rolesQuery.refetch();
        }}
        onApply={(next) => {
          setApplied({ storeGuid: next.storeGuid, roleGuid: next.roleGuid, status: next.status, sort: next.sort });
          setSheetVisible(false);
        }}
        onDismiss={() => setSheetVisible(false)}
      />
    </PeopleScreenFrame>
  );
}

const styles = StyleSheet.create({
  countNumber: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  countRow: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    paddingHorizontal: HB_SPACING.md + 4,
    paddingTop: HB_SPACING.xs,
  },
  countText: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  filterArea: {
    backgroundColor: HB_COLORS.white,
    gap: 8,
    paddingBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
  },
  listContent: { padding: HB_SPACING.md, paddingBottom: 112 },
  loader: { margin: 32 },
  sortButton: { alignItems: "center", flexDirection: "row", gap: 3 },
  sortText: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
});
