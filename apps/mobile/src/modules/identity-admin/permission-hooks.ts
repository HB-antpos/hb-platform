import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import en from "@/locales/en/identityPermissions.json";
import zh from "@/locales/zh/identityPermissions.json";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { AppLanguage } from "@/shared/i18n/types";
import { localizeAccessPermission, localizeAccessPermissionCategory } from "@/modules/users/access-permission-presentation";
import { fetchIdentityPermissionCatalog, fetchIdentityPermissionRoleCounts, fetchIdentitySysPermissions } from "./api";
import { buildPermissionListItems, getPermissionCapabilities, type PermissionListItem } from "./permission-logic";
import { useIdentitySession } from "./user-hooks";

export type PermissionCopy = typeof zh;

export function interpolatePermissionCopy(value: string, params: Record<string, string | number> = {}) {
  return Object.entries(params).reduce((text, [key, replacement]) => text.replace(`{{${key}}}`, String(replacement)), value);
}

export function usePermissionCopy() {
  const { language } = useAppTranslation();
  const appLanguage: AppLanguage = language.startsWith("zh") ? "zh" : "en";
  return { copy: appLanguage === "zh" ? zh : en, appLanguage };
}

/** 权限管理页共用的会话、能力判定与查询键前缀。 */
export function usePermissionSession() {
  const session = useIdentitySession("Roles.View");
  const capabilities = useMemo(() => getPermissionCapabilities({
    isDeviceMode: !session.allowed,
    isAdmin: session.access.isAdmin,
    hasPermission: session.access.hasPermission,
  }), [session.access.hasPermission, session.access.isAdmin, session.allowed]);
  return { ...session, capabilities };
}

export const permissionQueryKeys = {
  catalog: (actorKey: string) => ["identity-admin", actorKey, "permissionCatalog"] as const,
  sysPermissions: (actorKey: string) => ["identity-admin", actorKey, "sysPermissions"] as const,
  roleCounts: (actorKey: string) => ["identity-admin", actorKey, "permissionRoleCounts"] as const,
  permissionRoles: (actorKey: string, code: string) => ["identity-admin", actorKey, "permissionRoles", code.toLocaleLowerCase()] as const,
  allRoles: (actorKey: string) => ["identity-admin", actorKey, "permissionAllRoles"] as const,
  permissionUsers: (actorKey: string, code: string) => ["identity-admin", actorKey, "permissionUsers", code.toLocaleLowerCase()] as const,
  // filterKey = 搜索词 / 分店 / 角色 / 排序（buildUserPickerFilterKey），任何一个变化都从第一页重新取。
  permissionUserCandidates: (actorKey: string, filterKey: readonly string[]) => ["identity-admin", actorKey, "permissionUserCandidates", ...filterKey] as const,
};

/**
 * 合并目录与数据库权限表，并按当前语言本地化名称与分类。
 * 目录与权限表任一失败都视为列表不可用；角色数由调用方单独查询，失败不影响列表。
 */
export function usePermissionItems({ actorKey, enabled, appLanguage }: { actorKey: string; enabled: boolean; appLanguage: AppLanguage }) {
  const catalogQuery = useQuery({
    queryKey: permissionQueryKeys.catalog(actorKey),
    enabled,
    retry: false,
    queryFn: ({ signal }) => fetchIdentityPermissionCatalog(actorKey, signal),
  });
  const sysQuery = useQuery({
    queryKey: permissionQueryKeys.sysPermissions(actorKey),
    enabled,
    retry: false,
    queryFn: ({ signal }) => fetchIdentitySysPermissions(actorKey, signal),
  });
  const items = useMemo<PermissionListItem[]>(() => {
    if (!catalogQuery.data || !sysQuery.data) return [];
    return buildPermissionListItems(catalogQuery.data.categories, sysQuery.data).map((item) => {
      const localized = localizeAccessPermission(
        { name: item.code, displayName: item.name, description: item.description, category: item.categoryKey, isSystemPermission: item.isSystem },
        appLanguage,
      );
      return {
        ...item,
        name: localized.name,
        // 仅在后端提供了说明时才展示，避免把通用兜底文案当成真实说明写回详情页。
        ...(item.description ? { description: localized.description } : {}),
        categoryName: localizeAccessPermissionCategory(item.categoryKey || item.categoryName, appLanguage),
      };
    });
  }, [appLanguage, catalogQuery.data, sysQuery.data]);

  return {
    items,
    catalog: catalogQuery.data,
    isPending: catalogQuery.isPending || sysQuery.isPending,
    isRefetching: catalogQuery.isRefetching || sysQuery.isRefetching,
    error: catalogQuery.error ?? sysQuery.error,
    refetch: () => Promise.all([catalogQuery.refetch(), sysQuery.refetch()]),
  };
}

export function usePermissionRoleCounts({ actorKey, enabled }: { actorKey: string; enabled: boolean }) {
  return useQuery({
    queryKey: permissionQueryKeys.roleCounts(actorKey),
    enabled,
    retry: false,
    queryFn: ({ signal }) => fetchIdentityPermissionRoleCounts(actorKey, signal),
  });
}
