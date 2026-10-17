// 多账号的界面动作：把「拦截提示 / 确认弹窗 / 失败文案 / 切换后跳转」集中在一处，页面只管布局。
// 真正的切换逻辑在 switch-flow（有单测），这里只负责与用户对话。

import { useCallback } from "react";
import { Alert } from "react-native";
import { useRouter } from "expo-router";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { resolveDefaultTabRoute } from "@/modules/navigation/default-route";
import { getFriendlyLoginErrorDescriptor } from "@/modules/auth/login-errors";
import { useAccountSwitchStore } from "./account-switch-store";
import {
  SAVED_ACCOUNTS_LIMIT,
  addAccountAndSwitch,
  checkSwitchBlocker,
  isCurrentAccountSaved,
  removeSavedAccount,
  saveCurrentToList,
  signOutAll,
  signOutCurrent,
  switchAccount,
} from "./account-switch-service";
import type { AddAccountInput, SwitchFailure } from "./switch-flow";
import type { VaultEntry } from "./vault";

interface Options {
  /** 目标账号的保存登录失效时，打开「重新登录」面板。 */
  onNeedsRelogin: (entry: VaultEntry) => void;
}

/** 重新登录时用户在确认框里点了取消。 */
export const RELOGIN_CANCELLED = "relogin-cancelled" as const;

export function useAccountSwitchActions({ onNeedsRelogin }: Options) {
  const { t } = useAppTranslation(["accountSwitch", "login"]);
  const router = useRouter();
  const user = useAuthStore((state) => state.user);
  const accounts = useAccountSwitchStore((state) => state.accounts);

  const currentName = user?.fullName || user?.username || "";
  const currentSaved = isCurrentAccountSaved(accounts, user?.userGUID);

  /** 打开「添加账号」页（设置路由的另一种视图，手势返回 / 安卓返回键自然回到设置首页）。 */
  const openAddAccount = useCallback(() => {
    router.push(
      { pathname: "/(shell)/settings", params: { view: "add-account" } } as unknown as Parameters<typeof router.push>[0]
    );
  }, [router]);

  /** 切换成功后回到默认首页（与登录成功后的落点一致）。 */
  const goToDefaultRoute = useCallback(() => {
    router.replace(
      resolveDefaultTabRoute({
        isDeviceMode: false,
        isWarehouseStaffOnly: useAuthStore.getState().access.isWarehouseStaffOnly,
        routeNames: useAppNavigationStore.getState().items.map((item) => item.routeName),
      }) as Parameters<typeof router.replace>[0],
      { withAnchor: true }
    );
  }, [router]);

  /** 班中等被拦截：只提示一句，不解释原因、不展开细节。 */
  const showBlocked = useCallback(
    (reason: "onShift" | "busy") => {
      Alert.alert(
        t(reason === "onShift" ? "blocked.onShiftTitle" : "blocked.busyTitle"),
        t(reason === "onShift" ? "blocked.onShiftMessage" : "blocked.busyMessage"),
        [{ text: t("blocked.ok") }]
      );
    },
    [t]
  );

  const failureMessage = useCallback(
    (reason: SwitchFailure, name: string) => {
      switch (reason) {
        case "needsRelogin":
          return t("failures.needsRelogin", { name });
        case "networkError":
          return t("failures.networkError");
        case "identityMismatch":
          return t("failures.identityMismatch");
        case "vaultFull":
          return t("failures.vaultFull", { max: SAVED_ACCOUNTS_LIMIT });
        case "activationFailed":
          return t("failures.activationFailed");
        case "sameAccount":
          return t("failures.sameAccount");
        case "unknownAccount":
          return t("failures.unknownAccount");
        case "notAccountSession":
          return t("failures.notAccountSession");
        default:
          return t("failures.generic");
      }
    },
    [t]
  );

  /** 把失败结果告诉用户；返回 true 表示已处理（拦截 / 弹窗 / 打开重登面板）。 */
  const reportFailure = useCallback(
    (reason: SwitchFailure, entry?: VaultEntry) => {
      if (reason === "onShift" || reason === "busy") {
        showBlocked(reason);
        return;
      }
      if (reason === "needsRelogin" && entry) {
        // 凭据失效：直接引导到重新登录面板，不再弹错误。
        onNeedsRelogin(entry);
        return;
      }
      if (reason === "activationFailed") {
        // 激活到一半失败时本机已清场，回到登录页。
        router.replace("/(auth)/login");
      }
      Alert.alert(t("sheet.title"), failureMessage(reason, entry?.fullName || entry?.username || ""));
    },
    [failureMessage, onNeedsRelogin, router, showBlocked, t]
  );

  const runSwitch = useCallback(
    async (entry: VaultEntry, saveCurrent: boolean) => {
      const result = await switchAccount(entry, { saveCurrent });
      if (result.ok) {
        goToDefaultRoute();
        return;
      }
      reportFailure(result.reason, entry);
    },
    [goToDefaultRoute, reportFailure]
  );

  /** 点列表里的账号：先查拦截；当前账号没保存则先确认；再切换。 */
  const requestSwitch = useCallback(
    async (entry: VaultEntry) => {
      const blocker = await checkSwitchBlocker();
      if (blocker) {
        showBlocked(blocker);
        return;
      }
      if (currentSaved) {
        await runSwitch(entry, false);
        return;
      }
      Alert.alert(t("confirmUnsaved.title"), t("confirmUnsaved.message", { name: currentName }), [
        { text: t("confirmUnsaved.cancel"), style: "cancel" },
        { text: t("confirmUnsaved.discardAndSwitch"), onPress: () => void runSwitch(entry, false) },
        { text: t("confirmUnsaved.saveAndSwitch"), onPress: () => void runSwitch(entry, true) },
      ]);
    },
    [currentName, currentSaved, runSwitch, showBlocked, t]
  );

  /** 把当前（未保存的）账号存进切换列表。 */
  const saveCurrent = useCallback(async () => {
    const result = await saveCurrentToList();
    if (!result.ok) {
      Alert.alert(t("sheet.title"), failureMessage(result.reason, currentName));
    }
  }, [currentName, failureMessage, t]);

  const requestRemove = useCallback(
    (entry: VaultEntry) => {
      const name = entry.fullName || entry.username;
      Alert.alert(t("remove.title"), t("remove.message", { name }), [
        { text: t("remove.cancel"), style: "cancel" },
        { text: t("remove.action"), style: "destructive", onPress: () => void removeSavedAccount(entry.userGuid) },
      ]);
    },
    [t]
  );

  /** 添加账号 / 重新登录：成功返回 null；登录失败返回要显示在表单里的文案。 */
  const submitCredentials = useCallback(
    async (input: AddAccountInput): Promise<string | null> => {
      const blocker = await checkSwitchBlocker();
      if (blocker) {
        showBlocked(blocker);
        return null;
      }
      const result = await addAccountAndSwitch(input);
      if (result.ok) {
        goToDefaultRoute();
        return null;
      }
      if (result.reason === "loginFailed") {
        const descriptor = getFriendlyLoginErrorDescriptor(result.error);
        return t(`login:${descriptor.key}`, descriptor.values);
      }
      if (result.reason === "onShift" || result.reason === "busy") {
        showBlocked(result.reason);
        return null;
      }
      return failureMessage(result.reason, input.username);
    },
    [failureMessage, goToDefaultRoute, showBlocked, t]
  );

  /**
   * 重新登录（保存的登录失效后输密码）：当前账号没保存时，登录成功会让它退出，所以先确认；
   * 返回 RELOGIN_CANCELLED 表示用户取消，面板保持打开。
   */
  const submitRelogin = useCallback(
    (input: AddAccountInput): Promise<string | null | typeof RELOGIN_CANCELLED> => {
      if (currentSaved) return submitCredentials(input);
      return new Promise((resolve) => {
        Alert.alert(t("confirmUnsaved.title"), t("confirmUnsaved.message", { name: currentName }), [
          { text: t("confirmUnsaved.cancel"), style: "cancel", onPress: () => resolve(RELOGIN_CANCELLED) },
          {
            text: t("confirmUnsaved.discardAndSwitch"),
            onPress: () => void submitCredentials({ ...input, saveCurrent: false }).then(resolve),
          },
          {
            text: t("confirmUnsaved.saveAndSwitch"),
            onPress: () => void submitCredentials({ ...input, saveCurrent: true }).then(resolve),
          },
        ], { cancelable: true, onDismiss: () => resolve(RELOGIN_CANCELLED) });
      });
    },
    [currentName, currentSaved, submitCredentials, t]
  );

  const finishSignOut = useCallback(
    (result: Awaited<ReturnType<typeof signOutCurrent>>) => {
      if (!result.ok) {
        reportFailure(result.reason);
        return;
      }
      if (result.switchedTo) goToDefaultRoute();
      else router.replace("/(auth)/login");
    },
    [goToDefaultRoute, reportFailure, router]
  );

  const requestSignOutCurrent = useCallback(() => {
    Alert.alert(t("signOut.currentTitle", { name: currentName }), t("signOut.currentMessage"), [
      { text: t("signOut.cancel"), style: "cancel" },
      {
        text: t("signOut.currentAction"),
        style: "destructive",
        onPress: () => void signOutCurrent().then(finishSignOut),
      },
    ]);
  }, [currentName, finishSignOut, t]);

  const requestSignOutAll = useCallback(() => {
    Alert.alert(t("signOut.allTitle"), t("signOut.allMessage"), [
      { text: t("signOut.cancel"), style: "cancel" },
      {
        text: t("signOut.allAction"),
        style: "destructive",
        onPress: () => void signOutAll().then(() => router.replace("/(auth)/login")),
      },
    ]);
  }, [router, t]);

  return {
    accounts,
    currentName,
    currentSaved,
    requestSwitch,
    openAddAccount,
    saveCurrent,
    requestRemove,
    submitCredentials,
    submitRelogin,
    requestSignOutCurrent,
    requestSignOutAll,
    // 设置页「退出登录」在列表里还有别的账号时用：先问退出当前还是全部。
    finishSignOut,
  };
}
