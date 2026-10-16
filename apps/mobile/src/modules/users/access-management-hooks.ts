import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { accountBoundRequestConfig } from "@/modules/auth/account-bound-request";
import { normalizeShopStoresApiResponse } from "@/modules/shop/store-normalization";
import { apiClient } from "@/shared/api/client";
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

/**
 * 授权页读写的都是账号级敏感数据：账号会话下把当前操作者绑定进请求，
 * 登录身份在请求途中变化（切账号 / 登出）时由拦截器直接拒绝，避免用别的身份把改动写出去。
 * 设备会话与 iOS 审核会话不传（审核会话带这个绑定会被拦截器拒绝）。
 */
function boundRequestConfig(actorKey?: string | null) {
  const key = actorKey?.trim();
  return key ? accountBoundRequestConfig(key) : undefined;
}

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

export function useUserAccessStores(
  userGuid?: string | null,
  enabled = true,
  actorKey?: string | null,
) {
  return useQuery({
    queryKey: userAccessStoresQueryKey(userGuid),
    enabled: Boolean(enabled && userGuid),
    queryFn: () => fetchUserAccessStores(userGuid!, boundRequestConfig(actorKey)),
    retry: false,
  });
}

export function useUserAccessRoles(
  userGuid?: string | null,
  enabled = true,
  actorKey?: string | null,
) {
  return useQuery({
    queryKey: userAccessRolesQueryKey(userGuid),
    enabled: Boolean(enabled && userGuid),
    queryFn: () => fetchUserAccessRoles(userGuid!, boundRequestConfig(actorKey)),
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
  actorKey?: string | null,
) {
  return useQuery({
    queryKey: userAccessPermissionAccessQueryKey(userGuid),
    enabled: Boolean(enabled && userGuid),
    queryFn: () =>
      fetchUserAccessPermissionAccess(userGuid!, boundRequestConfig(actorKey)),
    retry: false,
  });
}

export function useAccessRoleCatalog(enabled = true, actorKey?: string | null) {
  return useQuery({
    queryKey: accessRoleCatalogQueryKey,
    enabled,
    queryFn: () => fetchAccessRoleCatalog(boundRequestConfig(actorKey)),
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

export function useAccessStoreCatalog(enabled = true, actorKey?: string | null) {
  return useQuery({
    queryKey: accessStoreCatalogQueryKey,
    enabled,
    queryFn: () => {
      const config = boundRequestConfig(actorKey);
      // 账号会话下全分店目录也绑定操作者；其余会话沿用默认加载器。
      return config
        ? fetchAccessStoreCatalog(async () =>
            normalizeShopStoresApiResponse(
              (await apiClient.get("/stores/all-by-name", config)).data,
            ),
          )
        : fetchAccessStoreCatalog();
    },
    retry: false,
  });
}

export function useAssignUserAccessStores(actorKey?: string | null) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AssignUserAccessStoresInput) =>
      assignUserAccessStores(input, boundRequestConfig(actorKey)),
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

export function useAssignUserAccessRoles(actorKey?: string | null) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AssignUserAccessRolesInput) =>
      assignUserAccessRoles(input, boundRequestConfig(actorKey)),
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

export function useAssignUserDirectPermissions(actorKey?: string | null) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AssignUserDirectPermissionsInput) =>
      assignUserDirectPermissions(input, boundRequestConfig(actorKey)),
    onSuccess: async (_, input) => {
      await queryClient.invalidateQueries({
        queryKey: userAccessPermissionAccessQueryKey(input.userGuid),
      });
    },
  });
}

/** 复制权限第一步：只算差异不写库，不触发任何缓存失效。 */
export function usePreviewCopyUserDirectPermissions(actorKey?: string | null) {
  return useMutation({
    mutationFn: (input: CopyUserDirectPermissionsInput) =>
      previewCopyUserDirectPermissions(input, boundRequestConfig(actorKey)),
  });
}

/** 复制权限第二步：原子写入；成功后刷新来源与所有目标的权限数据以及员工列表。 */
export function useCopyUserDirectPermissions(actorKey?: string | null) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CopyUserDirectPermissionsInput) =>
      copyUserDirectPermissions(input, boundRequestConfig(actorKey)),
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
