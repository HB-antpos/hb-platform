// 保险库的真实存储后端：Keychain（expo-secure-store）+ AsyncStorage 哨兵。
// 凭据只在「设备解锁时可用」且不随备份迁移到别的设备（WHEN_UNLOCKED_THIS_DEVICE_ONLY）。

import * as SecureStore from "expo-secure-store";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  createVaultStore,
  VAULT_SENTINEL_KEY,
  type VaultSecureBackend,
  type VaultSentinelBackend,
} from "./vault-store";

const secureBackend: VaultSecureBackend = {
  get: (key) => SecureStore.getItemAsync(key),
  set: (key, value) =>
    SecureStore.setItemAsync(key, value, { keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY }),
  delete: (key) => SecureStore.deleteItemAsync(key),
};

const sentinelBackend: VaultSentinelBackend = {
  has: async () => (await AppAsyncStorage.getString(VAULT_SENTINEL_KEY)) === "1",
  set: () => AppAsyncStorage.setString(VAULT_SENTINEL_KEY, "1"),
  clear: () => AppAsyncStorage.removeItem(VAULT_SENTINEL_KEY),
};

export const accountVault = createVaultStore(secureBackend, sentinelBackend);
