import { useCallback, useEffect, useMemo, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Icon, IconButton, Snackbar, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import type { Store } from "@/modules/shop/types";
import { getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { getManageableStoresForSession, getPosEnabledStores, isStoreManageable } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import { FloatingAction, FilterChipRow, SectionTitle, StickyActionBar } from "@/modules/people/ui";
import {
  DropdownChip,
  HeaderIconButton,
  PeopleScreenFrame,
  PeopleSearchBox,
  type PeopleHubChrome,
} from "@/modules/people/people-frame";
import { PersonListRow } from "@/modules/people/person-list-row";
import { buildStaffPersonItem } from "@/modules/people/person-list-item";
import { resolvePersonName } from "@/modules/people/person-name";
import { useStoreUsers, type StoreUserListItem } from "@/modules/users";
import { StaffBarcodeBatchDialog, StaffBarcodeDialog } from "@/modules/users/staff-barcode/StaffBarcodeDialogs";
import { canManageStaffBarcode } from "@/modules/users/staff-barcode/eligibility";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import { StoreSwitchSheet } from "@/modules/users/store-switch-sheet";
import {
  STAFF_STATUS_FILTER_ORDER,
  buildStaffListRows,
  countStaffByStore,
  countStaffStatuses,
  filterStoreStaff,
  resolveStoreListSubtitleKind,
  type StaffListRow,
  type StaffStatusFilter,
} from "@/modules/users/store-staff-list-logic";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { PERMISSIONS } from "@/shared/utils/access";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

const NO_USERS: StoreUserListItem[] = [];

/**
 * 「人员管理」里店长（以及没有全局用户查看权限的账号）看到的「人员」页签：本店店员列表。
 * 由原 app/(shell)/users/index.tsx 的列表逻辑迁入：分店范围、「我」置顶、设备模式、批量打印、个人码资格等规则一条不丢；
 * 列表只负责浏览、搜索、筛选、进详情、点二维码、批量打印、新增。编辑、重置密码、停用、授权都在详情页 / 编辑页。
 */
export function StoreStaffList({ chrome }: { chrome: PeopleHubChrome }) {
  const router = useRouter();
  const { t, language } = useAppTranslation(["userManagement", "common"]);
  const { t: tp } = useAppTranslation("people");
  const access = useAuthStore((state) => state.access);
  const currentUser = useAuthStore((state) => state.user);
  const authenticated = useAuthStore((state) => state.isAuthenticated);
  const actorGuid = currentUser?.userGUID || currentUser?.userGuid || "";
  const {
    stores,
    selectedStoreCode: rememberedStoreCode,
    isSelectedStoreAuto,
    isDeviceMode,
    isHydratingSelection,
    isLoading: storesLoading,
    selectStore,
  } = useStores();
  // 仅收窄选择候选，设备绑定与店员操作权限继续使用原分店范围。
  const posEnabledStores = useMemo(() => getPosEnabledStores(stores), [stores]);
  const [managedStoreCode, setManagedStoreCode] = useState<string | null>(null);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [keywordInput, setKeywordInput] = useState("");
  const [statusFilter, setStatusFilter] = useState<StaffStatusFilter>("all");
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [barcodeUser, setBarcodeUser] = useState<StoreUserListItem | null>(null);
  const [batchSelecting, setBatchSelecting] = useState(false);
  const [batchVisible, setBatchVisible] = useState(false);
  const [selectedUserGuids, setSelectedUserGuids] = useState<Set<string>>(new Set());
  const deviceBoundStoreCode = getDeviceBoundStoreCode({
    isDeviceMode,
    selectedStoreCode: rememberedStoreCode,
  });

  // 员工管理入口认 Users.ViewStoreStaff；Users.View（全局用户查看）持有者同样放行。
  const canViewUsers = access.isAdmin
    || access.canReadUser
    || access.hasPermission(PERMISSIONS.Users.ViewStoreStaff);
  // 店长持有本店店员专用权限；全局用户权限（Users.Create 等）同样可以管理店员。
  const canCreateUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Create)
    || access.hasPermission(PERMISSIONS.Users.CreateStoreStaff);
  const canEditUsers = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.Edit)
    || access.hasPermission(PERMISSIONS.Users.EditStoreStaff);
  const canResetPasswords = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.ResetPassword)
    || access.hasPermission(PERMISSIONS.Users.ResetStoreStaffPassword);
  const manageableStores = useMemo(
    () =>
      getManageableStoresForSession({
        stores,
        isDeviceMode,
        deviceBoundStore: deviceBoundStoreCode ? stores.find((store) => store.storeCode === deviceBoundStoreCode) ?? null : null,
        isAdmin: access.isAdmin,
      }),
    [access.isAdmin, deviceBoundStoreCode, isDeviceMode, stores]
  );
  // 候选分店：店长只能看自己「管理分店」里的店员（后端对非管理分店直接拒绝），界面就不要列出「仅查看」的分店；
  // 管理员不受限。条码打印等仍按完整的 posEnabledStores 判断资格。
  const pickerStores = useMemo(
    () =>
      access.isAdmin
        ? posEnabledStores
        : posEnabledStores.filter((store) => isStoreManageable(store.storeCode, manageableStores)),
    [access.isAdmin, manageableStores, posEnabledStores]
  );

  useEffect(() => {
    if (isHydratingSelection || storesLoading) {
      return;
    }

    setManagedStoreCode((current) => {
      if (deviceBoundStoreCode) {
        return deviceBoundStoreCode;
      }

      if (current && pickerStores.some((store) => store.storeCode === current)) {
        return current;
      }

      // 全局自动默认的分店不算用户选择，仍默认全部分店
      const selectedAssignedStore = rememberedStoreCode && !isSelectedStoreAuto
        ? pickerStores.find((store) => store.storeCode === rememberedStoreCode)
        : null;
      if (selectedAssignedStore) {
        return selectedAssignedStore.storeCode;
      }

      return null;
    });
  }, [deviceBoundStoreCode, isHydratingSelection, isSelectedStoreAuto, rememberedStoreCode, pickerStores, storesLoading]);

  const managedStore = useMemo(
    () => stores.find((store) => store.storeCode === managedStoreCode) ?? null,
    [managedStoreCode, stores]
  );
  const selectedStoreCanCreate = canCreateUsers && isStoreManageable(managedStoreCode, manageableStores);
  const selectedStoreCanManageUsers =
    (canCreateUsers || canEditUsers || canResetPasswords) &&
    isStoreManageable(managedStoreCode, manageableStores);

  // 搜索在客户端做（姓名各段 / 昵称 / 账号 / 邮箱 / 电话），所以服务端永远取该分店的全部店员，不再把关键词发给服务端。
  const usersQuery = useStoreUsers(
    canViewUsers ? (isDeviceMode ? deviceBoundStoreCode : managedStoreCode) : undefined,
    ""
  );
  // 切换分店弹层里的各店人数：只在弹层打开时才取「全部已分配分店」的名单（与「全部」视图共用同一份缓存）。
  const storeCountsQuery = useStoreUsers(storePickerVisible && canViewUsers && !isDeviceMode ? null : undefined, "");
  const storeCounts = useMemo(
    () => (storeCountsQuery.data ? countStaffByStore(storeCountsQuery.data) : null),
    [storeCountsQuery.data]
  );

  const resolveUserStoreCode = useCallback(
    (user: StoreUserListItem) => managedStoreCode || user.storeCode || null,
    [managedStoreCode]
  );
  const canManageUserBarcode = useCallback(
    (user: StoreUserListItem) => canManageStaffBarcode({
      authenticated,
      deviceOnly: isDeviceMode,
      canEditUsers,
      canManagePosStore: isStoreManageable(resolveUserStoreCode(user), manageableStores)
        && posEnabledStores.some((store) => store.storeCode === resolveUserStoreCode(user)),
      actorGuid,
      actorRoles: currentUser?.roleNames ?? [],
      targetGuid: user.userGUID,
      targetStatus: user.status,
      targetRoles: user.roleNames,
    }),
    [actorGuid, authenticated, canEditUsers, currentUser?.roleNames, isDeviceMode, manageableStores, posEnabledStores, resolveUserStoreCode]
  );

  const openCreatePage = useCallback(() => {
    // 创建必须绑定当前已选且可管理的分店，不能回落到“全部已分配分店”。
    if (!managedStoreCode || !selectedStoreCanCreate) {
      setSnackbarMessage(t("messages.selectManageableStoreFirst"));
      return;
    }

    router.push({
      pathname: "/users/new",
      params: { storeCode: managedStoreCode },
    } as unknown as Parameters<typeof router.push>[0]);
  }, [managedStoreCode, router, selectedStoreCanCreate, t]);

  // 点整行进统一详情页；旧的预填参数只作首屏占位，详情页会按 userGuid 自己拉取目标。
  const openStaffDetail = useCallback(
    (user: StoreUserListItem) => {
      const targetStoreCode = resolveUserStoreCode(user);
      if (!targetStoreCode) {
        setSnackbarMessage(t("messages.selectStoreFirst"));
        return;
      }

      router.push({
        pathname: "/users/[userGuid]",
        params: {
          userGuid: user.userGUID,
          storeCode: targetStoreCode,
          userName: resolvePersonName(user).primary,
          username: user.username,
          targetStatus: String(user.status),
          roleNames: user.roleNames.join("|"),
        },
      } as unknown as Parameters<typeof router.push>[0]);
    },
    [resolveUserStoreCode, router, t]
  );

  const handleSelectManagedStore = useCallback(
    async (store: Store | null) => {
      // 批量选择只能属于一个明确分店，切店时必须清空，避免跨店打印。
      setSelectedUserGuids(new Set());
      setBatchSelecting(false);
      setManagedStoreCode(deviceBoundStoreCode ?? store?.storeCode ?? null);
      setStorePickerVisible(false);

      try {
        await selectStore(store);
      } catch (error) {
        console.warn("[store-users] failed to persist store selection", error);
      }
    },
    [deviceBoundStoreCode, selectStore]
  );

  const { refetch: refetchUsers } = usersQuery;
  const handleRefresh = useCallback(async () => {
    try {
      await refetchUsers();
    } catch (error) {
      console.warn("[store-users] refresh failed", error);
      setSnackbarMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "messages.refreshFailed" }));
    }
  }, [language, refetchUsers, t]);

  const users = usersQuery.data ?? NO_USERS;
  const keyword = keywordInput.trim();
  const statusCounts = useMemo(() => countStaffStatuses(users), [users]);
  const filteredUsers = useMemo(
    () => filterStoreStaff(users, { keyword, statusFilter }),
    [keyword, statusFilter, users]
  );
  const listRows = useMemo(
    () =>
      buildStaffListRows(filteredUsers, actorGuid, {
        me: t("list.me"),
        staffSection: (count) => t("list.staffSection", { count }),
        sortedByName: t("list.sortedByName"),
      }),
    [actorGuid, filteredUsers, t]
  );

  const selectedUsers = useMemo(
    () => users.filter((user) => selectedUserGuids.has(user.userGUID) && canManageUserBarcode(user)),
    [canManageUserBarcode, selectedUserGuids, users]
  );
  const canBatchPrint =
    !isDeviceMode && canEditUsers && managedStoreCode && isStoreManageable(managedStoreCode, manageableStores);

  const subtitle = useMemo(() => {
    const kind = resolveStoreListSubtitleKind({
      managedStoreCode,
      hasSelectedStore: Boolean(managedStore),
      canManageSelectedStore: selectedStoreCanManageUsers,
    });
    if (kind === "allStores") return tp("subtitle.allStores");
    if (kind === "noStore" || !managedStore) return t("currentStore.empty");
    return tp(kind === "manageable" ? "subtitle.storeManageable" : "subtitle.storeViewOnly", {
      name: managedStore.storeName || managedStore.storeCode,
      code: managedStore.storeCode,
    });
  }, [managedStore, managedStoreCode, selectedStoreCanManageUsers, t, tp]);

  const toggleSelection = useCallback((userGuid: string) => {
    setSelectedUserGuids((current) => {
      const next = new Set(current);
      if (next.has(userGuid)) next.delete(userGuid);
      else next.add(userGuid);
      return next;
    });
  }, []);

  const renderRow = useCallback(
    ({ item: row }: { item: StaffListRow }) => {
      if (row.kind === "header") {
        return (
          <View style={styles.sectionHeader}>
            <SectionTitle title={row.hint ? `${row.label} · ${row.hint}` : row.label} />
          </View>
        );
      }

      const user = row.user;
      const isSelf = row.isSelf;
      const employmentType = user.employmentType
        ? t(`detail.employmentTypes.${user.employmentType}`, user.employmentType)
        : t("fields.positionValue");
      // 本人行显示角色（如店长），店员行沿用雇佣类型。
      const roleSummary = user.roleNames.length
        ? user.roleNames.map((roleName) => localizeAccessRoleName(roleName, language)).join(" / ")
        : employmentType;
      const person = buildStaffPersonItem(user, {
        isSelf,
        // 「全部已分配分店」里要看出每个人属于哪家店。
        storeLabel: !managedStoreCode && user.storeCode ? (user.storeName || user.storeCode) : null,
        typeLabel: isSelf ? roleSummary : employmentType,
      });
      const canUseBarcode = !isSelf && canManageUserBarcode(user);
      const selectedForBatch = selectedUserGuids.has(user.userGUID);

      return (
        <PersonListRow
          item={person}
          position={row.position}
          testID="compact-staff-row"
          onPress={isSelf ? undefined : () => (batchSelecting && canUseBarcode ? toggleSelection(user.userGUID) : openStaffDetail(user))}
          leading={
            batchSelecting && canUseBarcode ? (
              <IconButton
                icon={selectedForBatch ? "checkbox-marked" : "checkbox-blank-outline"}
                size={22}
                iconColor={selectedForBatch ? HB_COLORS.action : HB_COLORS.textSecondary}
                style={styles.checkbox}
                accessibilityLabel={t("staffBarcode.batch.selectEmployee", { name: person.name })}
                onPress={() => toggleSelection(user.userGUID)}
              />
            ) : null
          }
          right={
            isSelf ? undefined : (
              <View style={styles.rowRight}>
                {canUseBarcode && !batchSelecting ? (
                  <Pressable
                    style={styles.codeAction}
                    accessibilityRole="button"
                    accessibilityLabel={`${person.name} · ${t("staffBarcode.actions.open")}`}
                    hitSlop={4}
                    onPress={() => setBarcodeUser(user)}
                  >
                    <Icon source="qrcode" size={22} color={HB_COLORS.textSecondary} />
                  </Pressable>
                ) : null}
                <Icon source="chevron-right" size={20} color="#98A2B3" />
              </View>
            )
          }
        />
      );
    },
    [
      batchSelecting,
      canManageUserBarcode,
      language,
      managedStoreCode,
      openStaffDetail,
      selectedUserGuids,
      t,
      toggleSelection,
    ]
  );

  if (!canViewUsers) {
    return (
      <PeopleScreenFrame title={tp("hub.title")} chrome={chrome}>
        <EmptyState
          title={t("messages.noAccessTitle")}
          description={t("messages.noAccessDescription")}
          primaryAction={{
            label: t("common:actions.goToSettings"),
            icon: "cog-outline",
            onPress: () => router.navigate("/(shell)/settings"),
          }}
        />
      </PeopleScreenFrame>
    );
  }

  const statusLabels: Record<StaffStatusFilter, string> = {
    all: tp("statusChips.all"),
    active: tp("statusChips.active"),
    disabled: tp("statusChips.disabled"),
    pendingFirstLogin: tp("statusChips.pendingFirstLogin"),
    cannotLogin: tp("statusChips.cannotLogin"),
    noEmail: tp("statusChips.noEmail"),
  };

  return (
    <PeopleScreenFrame
      title={tp("hub.title")}
      subtitle={subtitle}
      chrome={chrome}
      right={
        canBatchPrint ? (
          <HeaderIconButton
            icon={batchSelecting ? "close" : "printer-outline"}
            label={batchSelecting ? t("staffBarcode.batch.cancelSelection") : t("staffBarcode.batch.entry")}
            selected={batchSelecting}
            onPress={() => {
              setBatchSelecting((current) => !current);
              setSelectedUserGuids(new Set());
            }}
          />
        ) : null
      }
    >
      <View style={styles.filterArea}>
        {/* 分店切换 + 搜索合成一行：店名、编码、可管理只出现一次。 */}
        <View style={styles.storeSearchRow}>
          <DropdownChip
            size="large"
            icon="storefront-outline"
            active
            // 芯片上用短名「全部分店」，给右边的搜索框留位置；弹层与副标题里仍写完整的「全部已分配分店」。
            label={managedStore?.storeName || tp("storeChip.all")}
            accessibilityLabel={`${t("currentStore.select")}：${managedStore?.storeName || t("currentStore.allRelated")}`}
            onPress={() => setStorePickerVisible(true)}
          />
          <PeopleSearchBox
            style={styles.searchFlex}
            value={keywordInput}
            onChangeText={setKeywordInput}
            placeholder={tp("search.staff")}
          />
        </View>
        {/* 状态芯片带数量，放不下时横向滑动（不换行）。 */}
        <FilterChipRow
          items={STAFF_STATUS_FILTER_ORDER.map((key) => ({ key, label: statusLabels[key], count: statusCounts[key] }))}
          value={statusFilter}
          onChange={setStatusFilter}
        />
      </View>

      <FlatList
        data={listRows}
        keyExtractor={(row) => row.key}
        renderItem={renderRow}
        keyboardShouldPersistTaps="handled"
        refreshControl={
          <RefreshControl refreshing={usersQuery.isFetching && !usersQuery.isLoading} onRefresh={handleRefresh} />
        }
        contentContainerStyle={styles.listContent}
        ListEmptyComponent={
          usersQuery.isLoading ? (
            <View style={styles.loadingWrap}>
              <ActivityIndicator />
            </View>
          ) : usersQuery.isError ? (
            <EmptyState
              title={t("messages.loadFailedTitle")}
              description={resolveLocalizedErrorMessage(usersQuery.error, {
                t,
                language,
                fallbackKey: "messages.loadFailedDescription",
              })}
              primaryAction={{
                label: t("common:actions.retry"),
                icon: "refresh",
                onPress: () => void handleRefresh(),
              }}
            />
          ) : users.length === 0 ? (
            <EmptyState title={t("messages.emptyTitle")} description={t("messages.emptyDescription")} />
          ) : keyword ? (
            <EmptyState
              title={t("messages.emptySearchTitle")}
              description={t("messages.emptySearchDescription", { keyword })}
            />
          ) : (
            // 有员工但被状态筛选筛空了。
            <EmptyState title={tp("empty.filteredTitle")} description={tp("empty.filteredDescription")} />
          )
        }
        ListFooterComponent={
          usersQuery.isLoading || usersQuery.isError ? null : (
            // 后端不返回其他店长；明示这一点，避免店长误以为名单不全。
            <View style={styles.onlyStaffNote} testID="staff-scope-note">
              <Icon source="lock-outline" size={16} color={HB_COLORS.textSecondary} />
              <Text variant="bodySmall" style={styles.onlyStaffNoteText}>{t("list.onlyStaffNote")}</Text>
            </View>
          )
        }
      />

      {batchSelecting ? (
        <View style={styles.bottomBar}>
          <StickyActionBar summary={tp("batch.selected", { count: selectedUsers.length })}>
            <Button
              mode="outlined"
              onPress={() => {
                setBatchSelecting(false);
                setSelectedUserGuids(new Set());
              }}
            >
              {t("staffBarcode.batch.cancelSelection")}
            </Button>
            <Button mode="contained" onPress={() => setBatchVisible(true)} disabled={!selectedUsers.length}>
              {t("staffBarcode.batch.confirm", { count: selectedUsers.length })}
            </Button>
          </StickyActionBar>
        </View>
      ) : canCreateUsers && selectedStoreCanCreate ? (
        // 只有「可管理的明确分店」才显示新增；全部已分配分店 / 仅查看的分店不显示（原来是点了才弹提示）。
        <FloatingAction label={t("actions.create")} icon="plus" onPress={openCreatePage} />
      ) : null}

      <StaffBarcodeDialog
        actorGuid={actorGuid}
        storeCode={barcodeUser ? resolveUserStoreCode(barcodeUser) ?? "" : ""}
        user={barcodeUser}
        visible={Boolean(barcodeUser)}
        onDismiss={() => setBarcodeUser(null)}
      />
      {managedStoreCode ? (
        <StaffBarcodeBatchDialog
          actorGuid={actorGuid}
          storeCode={managedStoreCode}
          users={selectedUsers}
          visible={batchVisible}
          onDismiss={() => {
            setBatchVisible(false);
            setBatchSelecting(false);
            setSelectedUserGuids(new Set());
          }}
        />
      ) : null}

      <StoreSwitchSheet
        visible={storePickerVisible}
        stores={pickerStores}
        manageableStores={manageableStores}
        selectedStoreCode={managedStoreCode}
        includeAllOption={!deviceBoundStoreCode}
        counts={storeCounts}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={handleSelectManagedStore}
      />

      <Snackbar visible={Boolean(snackbarMessage)} onDismiss={() => setSnackbarMessage("")} duration={2500}>
        {snackbarMessage}
      </Snackbar>
    </PeopleScreenFrame>
  );
}

const styles = StyleSheet.create({
  bottomBar: { bottom: 0, left: 0, position: "absolute", right: 0 },
  checkbox: { margin: 0 },
  codeAction: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 44,
    minWidth: 40,
  },
  filterArea: {
    backgroundColor: HB_COLORS.white,
    gap: 8,
    paddingBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
  },
  listContent: {
    paddingBottom: 112,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: 4,
  },
  loadingWrap: {
    alignItems: "center",
    paddingVertical: 24,
  },
  onlyStaffNote: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 6,
    paddingHorizontal: 4,
    paddingTop: 12,
  },
  onlyStaffNoteText: {
    color: HB_COLORS.textSecondary,
    flex: 1,
  },
  rowRight: { alignItems: "center", flexDirection: "row" },
  searchFlex: { flex: 1, minWidth: 0 },
  sectionHeader: { paddingBottom: 6, paddingTop: 10 },
  storeSearchRow: { alignItems: "center", flexDirection: "row", gap: 8 },
});
