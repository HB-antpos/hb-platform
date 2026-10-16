import { useQuery } from "@tanstack/react-query";
import { fetchIdentityUserDetail } from "@/modules/identity-admin/api";
import { fetchStoreUserDetail } from "@/modules/users/api";

/**
 * 授权页「目标用户自取」：页面只靠 userGuid 进入时也能拿到姓名、状态与角色，
 * 路由参数（userName / targetStatus / roleNames）只作首屏预填，受保护角色 / 停用校验以这里的数据为准。
 * 查询键放在 userAccessManagement 根下，授权保存后统一失效。
 */
const TARGET_QUERY_ROOT = ["userAccessManagement", "target"] as const;

export function accessIdentityTargetQueryKey(actorKey: string, userGuid: string) {
  return [...TARGET_QUERY_ROOT, "identity", actorKey, userGuid] as const;
}

export function accessStoreUserTargetQueryKey(storeCode: string, userGuid: string) {
  return [...TARGET_QUERY_ROOT, "storeUser", storeCode, userGuid] as const;
}

/** 管理员 / 持有 Users.View 的账号会话：读 /Users/guid/{guid}，可以看到任何账号（含管理员、店长）。 */
export function useAccessIdentityTarget(input: {
  userGuid: string;
  actorKey: string;
  enabled: boolean;
}) {
  return useQuery({
    queryKey: accessIdentityTargetQueryKey(input.actorKey, input.userGuid),
    enabled: Boolean(input.enabled && input.userGuid && input.actorKey),
    queryFn: ({ signal }) => fetchIdentityUserDetail(input.userGuid, input.actorKey, signal),
    retry: false,
  });
}

/** 店长 / 设备账号 / iOS 审核会话：读店员接口，后端只放行本店店员，找不到即视为不可管理。 */
export function useAccessStoreUserTarget(input: {
  userGuid: string;
  storeCode: string;
  enabled: boolean;
}) {
  return useQuery({
    queryKey: accessStoreUserTargetQueryKey(input.storeCode, input.userGuid),
    enabled: Boolean(input.enabled && input.userGuid),
    queryFn: () => fetchStoreUserDetail(input.userGuid, input.storeCode),
    retry: false,
  });
}
