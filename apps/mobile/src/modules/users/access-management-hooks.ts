import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  assignUserAccessRoles,
  assignUserAccessStores,
  assignUserDirectPermissions,
  copyUserDirectPermissions,
  fetchAccessPermissionCatalog,
  fetchAccessRoleCatalog,
  fetchAccessStoreCatalog,
  fetchUserAccessPermissionState,
  fetchUserAccessPermissionAccess,
  fetchUserAccessRoles,
  fetchUserAccessStores,
  previewCopyUserDirectPermissions,
} from "./access-management-api";
import type {
  AssignUserAccessRolesInput,
  AssignUserAccessStoresInput,
  AssignUserDirectPermissionsInput,
  CopyUserDirectPermissionsInput,
} from "./access-management-types";

const USER_ACCESS_QUERY_ROOT = "userAccessManagement";

export function userAccessStoresQueryKey(userGuid?: string | null) {
  return [USER_ACCESS_QUERY_ROOT, "stores", userGuid ?? ""] as const;
}

export function userAccessRolesQueryKey(userGuid?: string | null) {
  return [USER_ACCESS_QUERY_ROOT, "roles", userGuid ?? ""] as const;
}

export function userAccessPermissionStateQueryKey(userGuid?: string | null) {
  return [USER_ACCESS_QUERY_ROOT, "permissionState", userGuid ?? ""] as const;
}

export function userAccessPermissionAccessQueryKey(userGuid?: string | null) {
  return [USER_ACCESS_QUERY_ROOT, "permissionAccess", userGuid ?? ""] as const;
}

export const accessRoleCatalogQueryKey = [
  USER_ACCESS_QUERY_ROOT,
  "roleCatalog",
] as const;

export const accessPermissionCatalogQueryKey = [
  USER_ACCESS_QUERY_ROOT,
  "permissionCatalog",
] as const;

export const accessStoreCatalogQueryKey = [
  USER_ACCESS_QUERY_ROOT,
  "storeCatalog",
] as const;

export function useUserAccessStores(userGuid?: string | null, enabled = true) {
  return useQuery({
    queryKey: userAccessStoresQueryKey(userGuid),
    enabled: Boolean(enabled && userGuid),
    queryFn: () => fetchUserAccessStores(userGuid!),
    retry: false,
  });
}

export function useUserAccessRoles(userGuid?: string | null, enabled = true) {
  return useQuery({
    queryKey: userAccessRolesQueryKey(userGuid),
    enabled: Boolean(enabled && userGuid),
    queryFn: () => fetchUserAccessRoles(userGuid!),
    retry: false,
  });
}

export function useUserAccessPermissionState(
  userGuid?: string | null,
  enabled = true,
) {
  return useQuery({
    queryKey: userAccessPermissionStateQueryKey(userGuid),
    enabled: Boolean(enabled && userGuid),
    queryFn: () => fetchUserAccessPermissionState(userGuid!),
    retry: false,
  });
}

export function useUserAccessPermissionAccess(
  userGuid?: string | null,
  enabled = true,
) {
  return useQuery({
    queryKey: userAccessPermissionAccessQueryKey(userGuid),
    enabled: Boolean(enabled && userGuid),
    queryFn: () => fetchUserAccessPermissionAccess(userGuid!),
    retry: false,
  });
}

export function useAccessRoleCatalog(enabled = true) {
  return useQuery({
    queryKey: accessRoleCatalogQueryKey,
    enabled,
    queryFn: fetchAccessRoleCatalog,
    retry: false,
  });
}

export function useAccessPermissionCatalog(enabled = true) {
  return useQuery({
    queryKey: accessPermissionCatalogQueryKey,
    enabled,
    queryFn: fetchAccessPermissionCatalog,
    retry: false,
  });
}

export function useAccessStoreCatalog(enabled = true) {
  return useQuery({
    queryKey: accessStoreCatalogQueryKey,
    enabled,
    queryFn: () => fetchAccessStoreCatalog(),
    retry: false,
  });
}

export function useAssignUserAccessStores() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AssignUserAccessStoresInput) =>
      assignUserAccessStores(input),
    onSuccess: async (_, input) => {
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: userAccessStoresQueryKey(input.userGuid),
        }),
        queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
      ]);
    },
  });
}

export function useAssignUserAccessRoles() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AssignUserAccessRolesInput) =>
      assignUserAccessRoles(input),
    onSuccess: async (_, input) => {
      // 角色变化会重算继承权限，两个 query 必须一起失效。
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: userAccessRolesQueryKey(input.userGuid),
        }),
        queryClient.invalidateQueries({
          queryKey: userAccessPermissionStateQueryKey(input.userGuid),
        }),
        queryClient.invalidateQueries({
          queryKey: userAccessPermissionAccessQueryKey(input.userGuid),
        }),
        queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
      ]);
    },
  });
}

export function useAssignUserDirectPermissions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AssignUserDirectPermissionsInput) =>
      assignUserDirectPermissions(input),
    onSuccess: async (_, input) => {
      await queryClient.invalidateQueries({
        queryKey: userAccessPermissionAccessQueryKey(input.userGuid),
      });
    },
  });
}

/** 复制权限第一步：只算差异不写库，不触发任何缓存失效。 */
export function usePreviewCopyUserDirectPermissions() {
  return useMutation({
    mutationFn: (input: CopyUserDirectPermissionsInput) =>
      previewCopyUserDirectPermissions(input),
  });
}

/** 复制权限第二步：原子写入；成功后刷新来源与所有目标的权限数据以及员工列表。 */
export function useCopyUserDirectPermissions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CopyUserDirectPermissionsInput) =>
      copyUserDirectPermissions(input),
    onSuccess: async (_, input) => {
      const involvedGuids = Array.from(
        new Set([input.sourceUserGuid, ...input.targetUserGuids]),
      );
      await Promise.all([
        ...involvedGuids.flatMap((guid) => [
          queryClient.invalidateQueries({
            queryKey: userAccessPermissionStateQueryKey(guid),
          }),
          queryClient.invalidateQueries({
            queryKey: userAccessPermissionAccessQueryKey(guid),
          }),
        ]),
        queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
      ]);
    },
  });
}
