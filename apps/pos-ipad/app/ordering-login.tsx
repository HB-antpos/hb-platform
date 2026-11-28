import { Redirect, router, type Href } from "expo-router";
import { useTranslation } from "react-i18next";

import {
  OrderingLoginScreen,
  signInOrdering,
  useOrderingSessionStore,
} from "@/features/ordering";
import { toggleAppLanguage } from "@/i18n";
import { usePosThemeVersion } from "@/ui/theme";

/** 订货登录：员工 hotbargain.vip 账号；不看设备是否注册。 */
export default function OrderingLoginRoute() {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const user = useOrderingSessionStore((state) => state.user);
  const signIn = useOrderingSessionStore((state) => state.signIn);

  if (user) return <Redirect href={"/ordering" as Href} />;

  return (
    <OrderingLoginScreen
      language={i18n.resolvedLanguage ?? i18n.language}
      onBack={() => router.dismissTo("/" as Href)}
      onSignedIn={(signedIn) => {
        signIn(signedIn);
        router.replace("/ordering" as Href);
      }}
      onSwitchLanguage={() => void toggleAppLanguage()}
      signIn={(credentials) => signInOrdering(credentials)}
    />
  );
}
