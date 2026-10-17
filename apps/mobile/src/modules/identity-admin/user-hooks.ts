import { useEffect, useState } from "react";
import { useMutation, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import en from "@/locales/en/identityUsers.json";
import zh from "@/locales/zh/identityUsers.json";
import { setIdentityUserLoginEnabled } from "./api";
import { isIdentitySessionAllowed } from "./user-logic";

export function useIdentityUserCopy() {
  const { language } = useAppTranslation();
  return language === "zh" ? zh : en;
}

export function useIdentitySession(permission = "Users.View") {
  const user = useAuthStore(s => s.user);
  const access = useAuthStore(s => s.access);
  const sessionKind = useAuthStore(s => s.sessionKind);
  const isAuthenticated = useAuthStore(s => s.isAuthenticated);
  const iosReviewOfflineGuardActive = useAuthStore(s => s.iosReviewOfflineGuardActive);
  const allowed = isIdentitySessionAllowed({ sessionKind, isAuthenticated, iosReviewOfflineGuardActive }, access.hasPermission(permission));
  return { user, access, allowed, sessionKind, isAuthenticated, actorKey: user?.userGUID ?? "" };
}

/**
 * 账号数据变化后刷新相关缓存：管理员侧该用户详情（["identity-admin", actor, "user", guid]）与用户列表
 * （["identity-admin", actor, "users", …]，不含 "users-count"），以及员工侧列表 / 资料——
 * 员工侧同样显示「不能登录」，两边必须一起刷新（员工侧 key 以 storeUsers / storeUserProfile 开头）。
 */
export function invalidateIdentityUserCaches(queryClient: QueryClient, actorKey: string, userGuid: string) {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "user", userGuid] }),
    queryClient.invalidateQueries({ queryKey: ["identity-admin", actorKey, "users"] }),
    queryClient.invalidateQueries({ queryKey: ["storeUsers"] }),
    queryClient.invalidateQueries({ queryKey: ["storeUserProfile"] }),
  ]);
}

/**
 * 管理员开关账号「允许登录」（PUT /Users/guid/{guid}/login-enabled，Users.Edit）。
 * 无论成功还是失败（含结果未知的超时 / 5xx）都刷新详情与列表，让界面显示服务端的真实状态；
 * onSettled 返回的 Promise 会被等待，mutateAsync 在刷新完成后才 resolve，开关不会先弹回旧值再跳变。
 */
export function useSetIdentityUserLoginEnabled(actorKey: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userGuid, loginEnabled }: { userGuid: string; loginEnabled: boolean }) =>
      setIdentityUserLoginEnabled(userGuid, loginEnabled, actorKey),
    onSettled: (_data, _error, variables) => invalidateIdentityUserCaches(queryClient, actorKey, variables.userGuid),
  });
}

export function useIdentitySearch(value: string) {
  const [search, setSearch] = useState(value.trim());
  useEffect(() => { const timer = setTimeout(() => setSearch(value.trim()), 300); return () => clearTimeout(timer); }, [value]);
  return search;
}

export function identityDate(value?: string) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}
