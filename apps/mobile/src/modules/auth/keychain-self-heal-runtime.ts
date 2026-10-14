import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { SecureStorage } from "@/shared/storage/secure";
import {
  ensureKeychainHealthy,
  KEYCHAIN_DIRTY_STORAGE_KEY,
  type KeychainSelfHealOutcome,
} from "./keychain-self-heal";
import { LOGIN_LOCAL_STEP_TIMEOUT_MS, withLoginStepTimeout } from "./login-diagnostics";

/** 记下「Keychain 有嫌疑」：下次登录前会强制清理。存 AsyncStorage，Keychain 坏了也写得进去。 */
export function markKeychainDirty() {
  return AppAsyncStorage.setString(KEYCHAIN_DIRTY_STORAGE_KEY, "1").catch(() => undefined);
}

/**
 * 登录联网前调用：自检 Keychain，异常或有标记就自动清理。
 * 永远不抛错、不无限等待——自检本身出问题也不能拦住登录，超时按未修复处理并留标记。
 */
export async function prepareKeychainForLogin(): Promise<KeychainSelfHealOutcome> {
  try {
    return await withLoginStepTimeout(
      ensureKeychainHealthy({
        isDirty: async () => (await AppAsyncStorage.getString(KEYCHAIN_DIRTY_STORAGE_KEY)) === "1",
        markDirty: () => AppAsyncStorage.setString(KEYCHAIN_DIRTY_STORAGE_KEY, "1"),
        clearDirty: () => AppAsyncStorage.removeItem(KEYCHAIN_DIRTY_STORAGE_KEY),
        probeReadWrite: () => SecureStorage.probeReadWrite(),
        listResidue: () => SecureStorage.listAccountSessionResidue(),
        forceClearAccountSession: () => SecureStorage.forceClearAccountSession(),
      }),
      "localClear",
      LOGIN_LOCAL_STEP_TIMEOUT_MS,
    );
  } catch (error) {
    console.warn("[auth] 登录前 Keychain 自检异常", error);
    await markKeychainDirty();
    return "unrepaired";
  }
}
