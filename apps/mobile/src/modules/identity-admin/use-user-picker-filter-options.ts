import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { localizeAccessRoleName } from "@/modules/users/access-permission-presentation";
import type { AppLanguage } from "@/shared/i18n/types";
import { sortStoresByName } from "@/shared/utils/name-sort";
import { fetchAccessStoreCatalog, fetchRoleFilterCatalog } from "./api";
import type { PickerOption } from "./user-picker-filter-bar";
import { managedIdentityStores, sortRolesByLevel } from "./user-logic";
import { useIdentitySession } from "./user-hooks";

/**
 * 选人弹层的分店 / 角色选项。口径与「人员管理」列表完全一致（同样的接口、同样的权限门禁、同样的排序），
 * 并使用同一组 react-query key，所以先打开过人员列表时这里直接命中缓存，不会重复请求。
 * - 分店：门店级管理者只能选自己管理的分店；其他人拿全部分店。
 * - 角色：服务端按「等级严格低于当前登录人」返回（管理员为全部）。
 */
export function useUserPickerFilterOptions({ enabled, language }: { enabled: boolean; language: AppLanguage }) {
  const { user, access, actorKey } = useIdentitySession();
  const scoped = access.isStoreLevelManager;
  const canReadRoleOptions = access.canReadRole || access.hasPermission("Users.ManageRoles");

  const storesQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "store-options"],
    queryFn: ({ signal }) => fetchAccessStoreCatalog(actorKey, signal),
    enabled: enabled && !scoped,
    retry: false,
  });
  const rolesQuery = useQuery({
    queryKey: ["identity-admin", actorKey, "role-filter-options"],
    queryFn: ({ signal }) => fetchRoleFilterCatalog(actorKey, signal),
    enabled: enabled && canReadRoleOptions,
    retry: false,
  });

  const storeOptions = useMemo<PickerOption[]>(
    () => sortStoresByName(scoped ? managedIdentityStores(user) : storesQuery.data ?? [], language).map((store) => ({
      value: store.storeGUID,
      label: `${store.storeName} · ${store.storeCode}`,
    })),
    [language, scoped, storesQuery.data, user],
  );
  const roleOptions = useMemo<PickerOption[]>(
    () => sortRolesByLevel(rolesQuery.data ?? []).map((role) => ({
      value: role.roleGUID,
      label: localizeAccessRoleName(role.roleName, language),
    })),
    [language, rolesQuery.data],
  );
  const myStoreGuids = useMemo(() => managedIdentityStores(user).map((store) => store.storeGUID), [user]);

  return {
    storeOptions,
    roleOptions,
    myStoreGuids,
    showStore: scoped ? storeOptions.length > 0 : true,
    showRole: canReadRoleOptions,
    loadFailed: storesQuery.isError || rolesQuery.isError,
    retry: () => {
      if (storesQuery.isError) void storesQuery.refetch();
      if (rolesQuery.isError) void rolesQuery.refetch();
    },
  };
}
