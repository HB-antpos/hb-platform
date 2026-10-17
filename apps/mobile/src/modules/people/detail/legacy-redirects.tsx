import type { ComponentProps } from "react";
import { Redirect, useLocalSearchParams } from "expo-router";
import { buildLegacyRedirect, buildLoginRecordsRoute } from "./detail-navigation";

type RedirectHref = ComponentProps<typeof Redirect>["href"];

/**
 * 旧路由重定向壳：旧版 App、推送、书签与其他页面的 router.push 仍可能指向旧地址，
 * 所以旧路由文件都保留，只负责把请求（连同 storeCode 与预填参数）转到新路由。
 *
 * - /staff/[userGuid]、/user-admin/[userGuid] → /users/[userGuid]（"new" → /users/new）
 */
export function LegacyDetailRedirect() {
  const params = useLocalSearchParams();
  return <Redirect href={buildLegacyRedirect(params) as unknown as RedirectHref} />;
}

/** /user-admin/[userGuid]/login-records → /users/[userGuid]/login-records */
export function LegacyLoginRecordsRedirect() {
  const params = useLocalSearchParams<{ userGuid?: string | string[] }>();
  const userGuid = (Array.isArray(params.userGuid) ? params.userGuid[0] : params.userGuid)?.trim() ?? "";
  return <Redirect href={buildLoginRecordsRoute(userGuid) as unknown as RedirectHref} />;
}
