import type { ComponentProps } from "react";
import { Redirect, useLocalSearchParams } from "expo-router";
import { buildAccessRedirectParams } from "@/modules/users/access-page-logic";

type RedirectHref = ComponentProps<typeof Redirect>["href"];

/**
 * 旧管理员授权路由：已合并进统一的账号授权页（/users/[userGuid]/access）。
 * 旧版 App、推送与书签仍可能指向这里，所以保留为重定向壳并透传参数（section=mobile 映射成 menu）。
 */
export default function UserAdminAccessRedirect() {
  const params = useLocalSearchParams();
  return (
    <Redirect
      href={
        {
          pathname: "/users/[userGuid]/access",
          params: buildAccessRedirectParams(params),
        } as unknown as RedirectHref
      }
    />
  );
}
