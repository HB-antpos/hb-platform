import { useEffect } from "react";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";
import { AccountSwitcherSheet } from "./AccountSwitcherSheet";
import { ReloginSheet } from "./ReloginSheet";
import { SwitchingOverlay } from "./SwitchingOverlay";
import { loadSavedAccounts } from "./account-switch-service";
import { useAccountSwitchStore } from "./account-switch-store";
import { useAccountSwitchActions } from "./use-account-switch-actions";

/**
 * 多账号的全局弹层宿主：挂在外壳布局最上层（盖住底部导航栏），统一承载
 * 账号切换器、重新登录面板、切换中遮罩。页面只需 openSwitcher() / 提交表单，不用自己管弹层。
 * 只在个人账号会话（sessionKind=account）下渲染；设备 / 设备绑定账号 / iOS 审核不出现。
 */
export function AccountSwitchHost() {
  const { language } = useAppTranslation("accountSwitch");
  const user = useAuthStore((state) => state.user);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const enabled = isAuthenticated && Boolean(user) && sessionKind === "account";

  const accounts = useAccountSwitchStore((state) => state.accounts);
  const apiHost = useAccountSwitchStore((state) => state.apiHost);
  const switcherVisible = useAccountSwitchStore((state) => state.switcherVisible);
  const reloginEntry = useAccountSwitchStore((state) => state.reloginEntry);
  const openRelogin = useAccountSwitchStore((state) => state.openRelogin);
  const closeSwitcher = useAccountSwitchStore((state) => state.closeSwitcher);
  const closeRelogin = useAccountSwitchStore((state) => state.closeRelogin);

  const actions = useAccountSwitchActions({ onNeedsRelogin: openRelogin });

  // 进入个人账号会话 / 换账号后读一次保存列表；退出到别的会话类型时收起所有弹层。
  useEffect(() => {
    if (enabled) {
      void loadSavedAccounts();
    } else {
      useAccountSwitchStore.getState().closeSwitcher();
      useAccountSwitchStore.getState().closeRelogin();
    }
  }, [enabled, user?.userGUID]);

  return (
    <>
      {enabled && user && switcherVisible ? (
        <AccountSwitcherSheet
          user={user}
          accounts={accounts}
          apiHost={apiHost}
          language={language}
          onClose={closeSwitcher}
          onSelect={(entry) => {
            closeSwitcher();
            void actions.requestSwitch(entry);
          }}
          onRelogin={openRelogin}
          onRemove={actions.requestRemove}
          onSaveCurrent={() => void actions.saveCurrent()}
          onAdd={() => {
            closeSwitcher();
            actions.openAddAccount();
          }}
          onSignOutCurrent={() => {
            closeSwitcher();
            actions.requestSignOutCurrent();
          }}
          onSignOutAll={() => {
            closeSwitcher();
            actions.requestSignOutAll();
          }}
        />
      ) : null}
      {enabled && reloginEntry ? (
        <ReloginSheet
          entry={reloginEntry}
          apiHost={apiHost}
          onClose={closeRelogin}
          onSubmit={async (input) => {
            const message = await actions.submitCredentials(input);
            if (!message) closeRelogin();
            return message;
          }}
        />
      ) : null}
      <SwitchingOverlay />
    </>
  );
}
