// 「人员管理」Hub 的「资料」页签：员工个人信息维护列表。
// 搜索（姓名 / 账号，服务端）+ 分店 chip + 角色 chip（点开选择面板）+「待补全」chip（本地按列表字段过滤）。
// 分页沿用人员页签的做法：服务端分页，下拉到底再取下一页并累加；筛选变化即换 key 从第一页重来。
// 数据范围（管理员全部分店 / 店长限管理分店、排除本人与受保护角色）全部由服务端强制，这里只负责显示。
import { useCallback, useEffect, useMemo, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { ActivityIndicator, Icon, Text } from "react-native-paper";
import { AdminEmpty, AdminError } from "@/modules/identity-admin/ui";
import { IdentitySelectionSheet } from "@/modules/identity-admin/selection-sheet";
import { useIdentitySession } from "@/modules/identity-admin/user-hooks";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { sortStoresByName } from "@/shared/utils/name-sort";
import { PERMISSIONS } from "@/shared/utils/access";
import { buildProfileRoute } from "../detail/detail-navigation";
import { resolveRowPosition } from "../list-position";
import {
  ChipScroller,
  CountChip,
  DropdownChip,
  ListCardSegment,
  LoadMoreFooter,
  PeopleScreenFrame,
  PeopleSearchBox,
  type PeopleHubChrome,
} from "../people-frame";
import { PeopleTag, PersonAvatar, PersonRow } from "../ui";
import { fetchManagedProfileFilterOptions, fetchManagedProfilePage } from "./managed-profile-api";
import {
  DEFAULT_MANAGED_PROFILE_FILTERS,
  buildManagedProfileFilterKey,
  buildManagedProfileRow,
  filterPendingItems,
  flattenManagedProfilePages,
  resolveManagedProfileNextPage,
  resolveStoreChip,
  shouldAutoLoadMoreForPending,
  type ManagedProfileFilters,
  type ManagedProfileRowModel,
} from "./managed-profile-logic";

type SheetKind = "store" | "role" | null;

export function ManagedProfileList({ chrome }: { chrome: PeopleHubChrome }) {
  const router = useRouter();
  const { t, language } = useAppTranslation(["peopleProfiles", "people", "common"]);
  const { access, allowed, actorKey } = useIdentitySession(PERMISSIONS.EmployeeProfiles.ViewManagedStore);

  const [input, setInput] = useState("");
  const [search, setSearch] = useState("");
  // 搜索词防抖 300ms，避免每敲一个字就换一次查询 key。
  useEffect(() => {
    const timer = setTimeout(() => setSearch(input.trim()), 300);
    return () => clearTimeout(timer);
  }, [input]);
  const [selectedStoreGuid, setSelectedStoreGuid] = useState("");
  const [roleGuid, setRoleGuid] = useState("");
  const [pendingOnly, setPendingOnly] = useState(false);
  const [sheet, setSheet] = useState<SheetKind>(null);

  // 筛选下拉：服务端已按调用者范围裁剪（店长只有管理分店、且不含管理员 / 店长 / 仓库经理角色）。
  const optionsQuery = useQuery({
    queryKey: ["managed-profiles", actorKey, "filter-options"],
    enabled: allowed,
    retry: false,
    staleTime: 60_000,
    queryFn: ({ signal }) => fetchManagedProfileFilterOptions(actorKey, signal),
  });
  const stores = useMemo(
    // 分店选项统一按分店名字排序（接口原序不保证）。
    () => sortStoresByName(optionsQuery.data?.stores ?? [], language),
    [language, optionsQuery.data?.stores],
  );
  const storeChip = resolveStoreChip(stores, selectedStoreGuid);
  const filters = useMemo<ManagedProfileFilters>(
    () => ({ ...DEFAULT_MANAGED_PROFILE_FILTERS, search, storeGuid: storeChip.storeGuid, roleGuid, pendingOnly }),
    [pendingOnly, roleGuid, search, storeChip.storeGuid],
  );

  const listQuery = useInfiniteQuery({
    queryKey: ["managed-profiles", actorKey, "list", ...buildManagedProfileFilterKey(filters)],
    enabled: allowed,
    retry: false,
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) => fetchManagedProfilePage(filters, pageParam, actorKey, signal),
    getNextPageParam: (lastPage) => resolveManagedProfileNextPage(lastPage),
  });
  const { refetch, fetchNextPage, hasNextPage, isFetchingNextPage, isFetching } = listQuery;

  const loaded = useMemo(() => flattenManagedProfilePages(listQuery.data?.pages ?? []), [listQuery.data]);
  const visible = useMemo(() => filterPendingItems(loaded, pendingOnly), [loaded, pendingOnly]);
  const rows = useMemo(() => visible.map(buildManagedProfileRow), [visible]);
  const total = listQuery.data?.pages[0]?.total;
  // 全部页都加载完才知道准确的「待补全」人数；没加载完不显示，避免给出偏小的数字。
  const fullyLoaded = Boolean(listQuery.data) && !hasNextPage;
  const pendingCount = useMemo(() => filterPendingItems(loaded, true).length, [loaded]);

  // 「待补全」是本地过滤：命中太少且后面还有页时自动继续翻页，避免出现「空列表但其实后面还有人」。
  useEffect(() => {
    if (shouldAutoLoadMoreForPending({ pendingOnly, visibleCount: visible.length, hasNextPage: Boolean(hasNextPage), isFetching })) {
      void fetchNextPage();
    }
  }, [fetchNextPage, hasNextPage, isFetching, pendingOnly, visible.length]);

  const openProfile = useCallback(
    (row: ManagedProfileRowModel) => {
      router.push(buildProfileRoute({ userGuid: row.key }) as unknown as Parameters<typeof router.push>[0]);
    },
    [router],
  );

  const storeLabel = access.isAdmin ? t("peopleProfiles:list.allStores") : t("peopleProfiles:list.allManagedStores");
  const selectedStore = stores.find((store) => store.storeGuid.toLowerCase() === storeChip.storeGuid.toLowerCase());
  const roles = useMemo(
    () =>
      (optionsQuery.data?.roles ?? []).map((role) => ({
        value: role.roleGuid,
        label: localizeAccessRoleName(role.roleName, language),
      })),
    [language, optionsQuery.data?.roles],
  );
  const selectedRole = roles.find((role) => role.value.toLowerCase() === roleGuid.toLowerCase());
  const storeOptions = useMemo(
    () => [
      { value: "", label: storeLabel },
      ...stores.map((store) => ({ value: store.storeGuid, label: store.storeName || store.storeCode })),
    ],
    [storeLabel, stores],
  );
  const roleOptions = useMemo(
    () => [{ value: "", label: t("peopleProfiles:list.allRoles") }, ...roles],
    [roles, t],
  );

  if (!allowed) {
    return (
      <PeopleScreenFrame title={t("people:hub.title")} chrome={chrome}>
        <AdminEmpty text={t("people:admin.noAccess")} />
      </PeopleScreenFrame>
    );
  }

  const renderItem = ({ item, index }: { item: ManagedProfileRowModel; index: number }) => (
    <ListCardSegment position={resolveRowPosition(index, rows.length)}>
      <PersonRow
        first={index === 0}
        avatar={<PersonAvatar initials={item.initials} seed={item.key} />}
        name={item.name}
        nameBadges={
          item.role ? <PeopleTag label={localizeAccessRoleName(item.role.roleName, language)} tone={item.role.tone} /> : undefined
        }
        meta={item.storeNames.join(" · ")}
        right={<ProfileStatus row={item} />}
        onPress={() => openProfile(item)}
        accessibilityLabel={`${item.name} ${item.pending ? t("peopleProfiles:row.statusPending") : t("peopleProfiles:row.statusComplete")}`}
      />
    </ListCardSegment>
  );

  return (
    <PeopleScreenFrame title={t("people:hub.title")} chrome={chrome}>
      <View style={styles.filterArea}>
        <PeopleSearchBox value={input} onChangeText={setInput} placeholder={t("peopleProfiles:list.search")} />
        <ChipScroller>
          {storeChip.locked ? (
            // 只有一家可选分店：默认选中并锁定，不可点开。
            <PeopleTag icon="lock-outline" label={selectedStore?.storeName || selectedStore?.storeCode || ""} tone="blue" />
          ) : (
            <DropdownChip
              label={t("peopleProfiles:list.filterStore", { value: selectedStore?.storeName || storeLabel })}
              active={Boolean(storeChip.storeGuid)}
              onPress={() => (optionsQuery.isError ? void optionsQuery.refetch() : setSheet("store"))}
            />
          )}
          <DropdownChip
            label={t("peopleProfiles:list.filterRole", { value: selectedRole?.label ?? t("peopleProfiles:list.allRoles") })}
            active={Boolean(roleGuid)}
            onPress={() => (optionsQuery.isError ? void optionsQuery.refetch() : setSheet("role"))}
          />
          <CountChip
            label={t("peopleProfiles:list.pending")}
            count={fullyLoaded ? pendingCount : null}
            selected={pendingOnly}
            onPress={() => setPendingOnly((current) => !current)}
          />
        </ChipScroller>
        {optionsQuery.isError ? (
          <Pressable accessibilityRole="button" onPress={() => void optionsQuery.refetch()}>
            <Text style={styles.optionsFailed}>{t("peopleProfiles:list.optionsFailed")}</Text>
          </Pressable>
        ) : null}
      </View>

      <View style={styles.countRow}>
        <Text style={styles.countText}>
          {typeof total === "number" ? t("peopleProfiles:list.count", { count: total }) : "–"}
          {fullyLoaded && pendingCount > 0 ? ` · ${t("peopleProfiles:list.countPending", { count: pendingCount })}` : ""}
        </Text>
      </View>

      {listQuery.isPending ? (
        <ActivityIndicator style={styles.loader} />
      ) : (
        <FlatList
          data={rows}
          keyExtractor={(row) => row.key}
          renderItem={renderItem}
          keyboardShouldPersistTaps="handled"
          refreshControl={<RefreshControl refreshing={listQuery.isRefetching && !isFetchingNextPage} onRefresh={() => void refetch()} />}
          contentContainerStyle={styles.listContent}
          onEndReachedThreshold={0.4}
          onEndReached={() => {
            if (hasNextPage && !isFetchingNextPage) void fetchNextPage();
          }}
          ListEmptyComponent={
            listQuery.isError ? (
              <AdminError error={listQuery.error} onRetry={() => void refetch()} />
            ) : isFetching ? null : (
              <AdminEmpty text={pendingOnly ? t("peopleProfiles:list.emptyPending") : t("peopleProfiles:list.empty")} />
            )
          }
          ListFooterComponent={
            // 翻页失败时保留已加载的行，只在末尾给出重试入口。
            listQuery.isError && rows.length > 0 ? (
              <AdminError error={listQuery.error} onRetry={() => void fetchNextPage()} />
            ) : (
              <LoadMoreFooter
                loading={isFetchingNextPage}
                endLabel={!hasNextPage && rows.length > 0 ? t("peopleProfiles:list.endOfList", { count: rows.length }) : null}
              />
            )
          }
        />
      )}

      {sheet === "store" ? (
        <IdentitySelectionSheet
          title={t("peopleProfiles:list.storeSheetTitle")}
          value={storeChip.storeGuid}
          options={storeOptions}
          searchable={storeOptions.length > 8}
          onSelect={(value) => {
            setSelectedStoreGuid(value);
            setSheet(null);
          }}
          onDismiss={() => setSheet(null)}
        />
      ) : null}
      {sheet === "role" ? (
        <IdentitySelectionSheet
          title={t("peopleProfiles:list.roleSheetTitle")}
          value={roleGuid}
          options={roleOptions}
          searchable={roleOptions.length > 8}
          onSelect={(value) => {
            setRoleGuid(value);
            setSheet(null);
          }}
          onDismiss={() => setSheet(null)}
        />
      ) : null}
    </PeopleScreenFrame>
  );
}

/** 行右侧的资料状态：待补全（琥珀点）/ 已完善（绿点）+ 缺哪些。 */
function ProfileStatus({ row }: { row: ManagedProfileRowModel }) {
  const { t } = useAppTranslation("peopleProfiles");
  const color = row.pending ? HB_COLORS.warning : HB_COLORS.success;
  const missingText = row.noProfile
    ? t("row.noProfile")
    : row.missing.map((key) => t(`missing.${key}`)).join(" · ");
  return (
    <View style={styles.status}>
      <View style={styles.statusLine}>
        <View style={[styles.dot, { backgroundColor: color }]} />
        <Text style={[styles.statusText, { color }]}>{row.pending ? t("row.statusPending") : t("row.statusComplete")}</Text>
      </View>
      {row.pending && missingText ? <Text style={styles.missingText} numberOfLines={2}>{missingText}</Text> : null}
      <Icon source="chevron-right" size={18} color="#98A2B3" />
    </View>
  );
}

const styles = StyleSheet.create({
  filterArea: {
    backgroundColor: HB_COLORS.white,
    gap: 8,
    paddingBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
  },
  optionsFailed: { color: HB_COLORS.danger, fontSize: 12.5 },
  countRow: { paddingHorizontal: HB_SPACING.md + 4, paddingTop: HB_SPACING.xs },
  countText: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  listContent: { padding: HB_SPACING.md, paddingBottom: 112 },
  loader: { margin: 32 },
  status: { alignItems: "flex-end", gap: 2, maxWidth: 132 },
  statusLine: { alignItems: "center", flexDirection: "row", gap: 5 },
  dot: { borderRadius: 4, height: 8, width: 8 },
  statusText: { fontSize: 12.5, fontWeight: "700" },
  missingText: { color: HB_COLORS.textSecondary, fontSize: 11.5, textAlign: "right" },
});
