import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createStoreUser,
  fetchStoreUserDetail,
  fetchStoreUserProfile,
  fetchStoreUsers,
  resetStoreUserPassword,
  sendStoreUserPasswordSetupEmail,
  updateStoreUser,
  updateStoreUserStatus,
} from "@/modules/users/api";
import type {
  StoreUserCreatePayload,
  StoreUserPasswordPayload,
  StoreUserStatusPayload,
  StoreUserUpdatePayload,
} from "@/modules/users/types";

export function storeUsersQueryKey(storeCode?: string | null, keyword?: string) {
  return ["storeUsers", storeCode ?? "", keyword?.trim() ?? ""] as const;
}

export function useStoreUsers(storeCode?: string | null, keyword?: string) {
  return useQuery({
    queryKey: storeUsersQueryKey(storeCode, keyword),
    enabled: storeCode !== undefined,
    queryFn: () => fetchStoreUsers({ storeCode, keyword }),
  });
}

export function useStoreUserDetail(userGuid?: string | null, storeCode?: string | null) {
  return useQuery({
    queryKey: ["storeUserDetail", storeCode ?? "", userGuid ?? ""],
    enabled: Boolean(userGuid && storeCode),
    queryFn: () => fetchStoreUserDetail(userGuid!, storeCode!),
  });
}

export function useStoreUserProfile(userGuid?: string | null, storeCode?: string | null) {
  return useQuery({
    queryKey: ["storeUserProfile", storeCode ?? "", userGuid ?? ""],
    enabled: Boolean(userGuid && storeCode),
    queryFn: () => fetchStoreUserProfile(userGuid!, storeCode!),
  });
}

export function useStoreUserMutations(storeCode?: string | null, keyword?: string) {
  const queryClient = useQueryClient();

  const invalidateUsers = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: storeUsersQueryKey(storeCode, keyword) }),
      queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
    ]);
  };

  const updateMutation = useMutation({
    mutationFn: (payload: StoreUserUpdatePayload) => updateStoreUser(payload),
    onSuccess: async (_, variables) => {
      await Promise.all([
        invalidateUsers(),
        queryClient.invalidateQueries({
          queryKey: ["storeUserDetail", variables.storeCode, variables.userGuid],
        }),
        queryClient.invalidateQueries({
          queryKey: ["storeUserProfile", variables.storeCode, variables.userGuid],
        }),
      ]);
    },
  });

  const createMutation = useMutation({
    mutationFn: (payload: StoreUserCreatePayload) => createStoreUser(payload),
    onSuccess: invalidateUsers,
  });

  const statusMutation = useMutation({
    mutationFn: (payload: StoreUserStatusPayload) => updateStoreUserStatus(payload),
    onSuccess: async (_, variables) => {
      await Promise.all([
        invalidateUsers(),
        queryClient.invalidateQueries({
          queryKey: ["storeUserDetail", variables.storeCode, variables.userGuid],
        }),
        queryClient.invalidateQueries({
          queryKey: ["storeUserProfile", variables.storeCode, variables.userGuid],
        }),
      ]);
    },
  });

  const passwordMutation = useMutation({
    mutationFn: (payload: StoreUserPasswordPayload) => resetStoreUserPassword(payload),
  });

  // 店长重置密码改为给员工发设置密码验证码邮件，店长不再经手新密码。
  const setupEmailMutation = useMutation({
    mutationFn: (payload: { userGuid: string; storeCode: string; email?: string }) =>
      sendStoreUserPasswordSetupEmail(payload),
    onSuccess: async (_, variables) => {
      // 补了邮箱时刷新列表与详情，界面上的「未设置」立即变成新邮箱。
      if (!variables.email) return;
      await Promise.all([
        invalidateUsers(),
        queryClient.invalidateQueries({
          queryKey: ["storeUserDetail", variables.storeCode, variables.userGuid],
        }),
        queryClient.invalidateQueries({
          queryKey: ["storeUserProfile", variables.storeCode, variables.userGuid],
        }),
      ]);
    },
  });

  return {
    createMutation,
    updateMutation,
    statusMutation,
    passwordMutation,
    setupEmailMutation,
  };
}
