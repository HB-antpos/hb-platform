// 多账号切换的真实装配：把 switch-flow 的依赖接到 auth-store / Keychain / 推送 / 网络上，并驱动界面状态。
// 界面只调用本文件导出的函数；逻辑与顺序全部在 switch-flow（有单测）里。

import { useAuthStore } from "@/store/auth-store";
import { SecureStorage } from "@/shared/storage/secure";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { runPushLogoutHook } from "@/modules/push/push-logout-hook";
import { readActiveShiftContext } from "@/modules/attendance/location-tracking-control";
import { collectOptionalLoginDeviceLocation } from "@/modules/attendance/required-location";
import { isIosReviewUsername } from "@/modules/ios-review/config";
import { waitForTokenRefreshIdle } from "@/shared/api/client";
import type { CurrentUser } from "@/modules/auth/types";
import { beginAccountSwitch, endAccountSwitch } from "./account-switch-gate";
import { useAccountSwitchStore } from "./account-switch-store";
import { hasPendingWork } from "./pending-work";
import { currentApiHost, rawFetchCurrentUser, rawLogin, rawLogout, rawRefresh } from "./raw-auth";
import {
  addAccount,
  forgetSavedAccount,
  saveCurrentAccount,
  signOutCurrentAccount,
  switchToAccount,
  type AddAccountInput,
  type AddAccountResult,
  type SignOutResult,
  type SwitchDeps,
  type SwitchResult,
} from "./switch-flow";
import { VAULT_MAX_ENTRIES, findEntry, pickNextUsableEntry, sortForDisplay, type VaultEntry } from "./vault";
import { accountVault } from "./vault-backend";

/** 切换后等菜单的上限：菜单有降级与后台重试，超时不阻塞进入首页。 */
const MENU_WAIT_MS = 8_000;

function setStage(stage: number) {
  useAccountSwitchStore.getState().setStage(stage);
}

let apiHostCache = "";

function buildDeps(): SwitchDeps<CurrentUser> {
  return {
    now: () => Date.now(),
    apiHost: () => apiHostCache,
    vault: accountVault,
    readCurrent: async () => {
      const state = useAuthStore.getState();
      if (!state.isAuthenticated || !state.user) return null;
      const refreshToken = await SecureStorage.getRefreshToken();
      if (!refreshToken) return null;
      return { user: state.user, refreshToken, sessionKind: state.sessionKind };
    },
    // 班中 = 本机存有进行中的班次上下文（上班打卡写入、下班打卡清除）。
    isOnShift: async () => Boolean(await readActiveShiftContext().catch(() => null)),
    hasPendingWork,
    refreshWithToken: (refreshToken) => {
      setStage(0);
      return rawRefresh(refreshToken);
    },
    fetchUserWithToken: rawFetchCurrentUser,
    loginWithCredentials: async (username, password) => {
      setStage(0);
      if (isIosReviewUsername(username)) {
        // 审核账号走本地离线认证，不是真实账号，不开放添加。
        throw Object.assign(new Error("Invalid username or password"), { response: { status: 401 } });
      }
      const audit = await collectOptionalLoginDeviceLocation(null);
      const token = await rawLogin({ username, password: password.trim(), passwordFormat: "raw", ...audit });
      try {
        const user = await rawFetchCurrentUser(token.accessToken);
        return { accessToken: token.accessToken, refreshToken: token.refreshToken, user };
      } catch (error) {
        // 登录成功但读不到用户：这个会话白开了，作废它。
        void rawLogout(token.refreshToken);
        throw error;
      }
    },
    settleTokenRefresh: waitForTokenRefreshIdle,
    revokeSession: rawLogout,
    notifyLeaving: () => {
      setStage(1);
      return runPushLogoutHook();
    },
    activate: (session) => useAuthStore.getState().activateAccountSession(session),
    fullLogout: () => useAuthStore.getState().logout(),
    gate: { begin: beginAccountSwitch, end: endAccountSwitch },
  };
}

/** 切换前的拦截检查（班中 / 有未完成的操作）；界面在弹「保存并切换」确认之前先调用，免得先问再拒。 */
export async function checkSwitchBlocker(): Promise<"onShift" | "busy" | null> {
  const deps = buildDeps();
  if (await deps.isOnShift()) return "onShift";
  if (deps.hasPendingWork()) return "busy";
  return null;
}

/** 个人账号会话（非设备 / 设备绑定账号 / iOS 审核）才开放多账号。 */
export function isAccountSwitchAvailable(): boolean {
  const state = useAuthStore.getState();
  return state.isAuthenticated && state.sessionKind === "account" && !state.iosReviewOfflineGuardActive;
}

/** 读取已保存账号列表（最近使用在前）并同步到界面状态。 */
export async function loadSavedAccounts(): Promise<VaultEntry[]> {
  apiHostCache = await currentApiHost();
  const accounts = sortForDisplay(await accountVault.load().catch(() => []));
  useAccountSwitchStore.getState().setAccounts(accounts, apiHostCache);
  return accounts;
}

export function isCurrentAccountSaved(accounts: readonly VaultEntry[], currentGuid: string | null | undefined): boolean {
  return Boolean(findEntry(accounts, currentGuid));
}

export const SAVED_ACCOUNTS_LIMIT = VAULT_MAX_ENTRIES;

async function loadMenuQuietly() {
  setStage(2);
  try {
    await Promise.race([
      useAppNavigationStore.getState().fetchMenu(),
      new Promise<void>((resolve) => setTimeout(resolve, MENU_WAIT_MS)),
    ]);
  } catch {
    // 菜单自带降级与后台重试，失败不影响切换结果。
  }
}

/** 包一层界面状态：遮罩开 → 执行 → 成功则等菜单 → 遮罩关，并刷新列表。 */
async function withSwitchingOverlay<T extends { ok: boolean }>(label: string | null, run: () => Promise<T>): Promise<T> {
  apiHostCache = await currentApiHost();
  const ui = useAccountSwitchStore.getState();
  ui.setSwitching(true, label);
  try {
    const result = await run();
    if (result.ok) {
      await loadMenuQuietly();
      setStage(3);
    }
    return result;
  } finally {
    useAccountSwitchStore.getState().setSwitching(false);
    await loadSavedAccounts();
  }
}

export function switchAccount(entry: VaultEntry, options: { saveCurrent?: boolean } = {}): Promise<SwitchResult> {
  return withSwitchingOverlay(entry.fullName || entry.username, () =>
    switchToAccount(buildDeps(), entry.userGuid, options)
  );
}

export function addAccountAndSwitch(input: AddAccountInput): Promise<AddAccountResult> {
  return withSwitchingOverlay(null, () => addAccount(buildDeps(), input));
}

export async function saveCurrentToList() {
  apiHostCache = await currentApiHost();
  const result = await saveCurrentAccount(buildDeps());
  await loadSavedAccounts();
  return result;
}

export async function removeSavedAccount(userGuid: string) {
  apiHostCache = await currentApiHost();
  await forgetSavedAccount(buildDeps(), userGuid);
  await loadSavedAccounts();
}

/** 退出当前账号：有下一个可用账号就切过去，否则回登录页（由界面据结果跳转）。 */
export async function signOutCurrent(): Promise<SignOutResult> {
  apiHostCache = await currentApiHost();
  const currentGuid = useAuthStore.getState().user?.userGUID;
  const accounts = await accountVault.load().catch(() => [] as VaultEntry[]);
  const next = pickNextUsableEntry(accounts, currentGuid, Date.now(), apiHostCache);
  // 没有可切的下一个账号时就是普通退出（回登录页），不盖「正在切换」遮罩。
  if (!next) return signOutCurrentAccount(buildDeps());
  return withSwitchingOverlay(null, () => signOutCurrentAccount(buildDeps()));
}

/** 退出全部：清空已保存账号，再走原来的完整退出。 */
export async function signOutAll(): Promise<void> {
  await accountVault.clear();
  useAccountSwitchStore.getState().reset();
  await useAuthStore.getState().logout();
}

/** 供「清除本机登录缓存」等入口顺带清空保险库。 */
export async function clearAccountVault(): Promise<void> {
  await accountVault.clear().catch(() => undefined);
  useAccountSwitchStore.getState().reset();
}
