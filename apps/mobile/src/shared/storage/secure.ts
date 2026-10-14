import * as SecureStore from "expo-secure-store";
import { clearSecureAccountSession } from "./secure-session-cleanup";
import { clearCashierPrintSecureSession } from "./cashier-print-secure-session";

const KEYS = {
  ACCESS_TOKEN: "hbweb_access_token",
  REFRESH_TOKEN: "hbweb_refresh_token",
  USER: "hbweb_user",
  CASHIER_BARCODE_PRINT_PENDING: "hbweb_cashier_barcode_print_pending",
  // 仅供读写自检使用，用完即删。
  PROBE: "hbweb_keychain_probe",
} as const;

export const SecureStorage = {
  async setToken(token: string) {
    await SecureStore.setItemAsync(KEYS.ACCESS_TOKEN, token);
  },
  async getToken() {
    return SecureStore.getItemAsync(KEYS.ACCESS_TOKEN);
  },
  async removeToken() {
    await SecureStore.deleteItemAsync(KEYS.ACCESS_TOKEN);
  },
  async setRefreshToken(token: string) {
    await SecureStore.setItemAsync(KEYS.REFRESH_TOKEN, token);
  },
  async getRefreshToken() {
    return SecureStore.getItemAsync(KEYS.REFRESH_TOKEN);
  },
  async removeRefreshToken() {
    await SecureStore.deleteItemAsync(KEYS.REFRESH_TOKEN);
  },
  async setUser(user: object) {
    await SecureStore.setItemAsync(KEYS.USER, JSON.stringify(user));
  },
  async getUser<T = unknown>() {
    const d = await SecureStore.getItemAsync(KEYS.USER);
    return d ? (JSON.parse(d) as T) : null;
  },
  async setCashierBarcodePrintPending(value: string) {
    await SecureStore.setItemAsync(KEYS.CASHIER_BARCODE_PRINT_PENDING, value);
  },
  async getCashierBarcodePrintPending() {
    return SecureStore.getItemAsync(KEYS.CASHIER_BARCODE_PRINT_PENDING);
  },
  async removeCashierBarcodePrintPending() {
    await SecureStore.deleteItemAsync(KEYS.CASHIER_BARCODE_PRINT_PENDING);
  },
  async clearCashierBarcodePrintSession() {
    await clearCashierPrintSecureSession(this);
  },
  async removeUser() {
    await SecureStore.deleteItemAsync(KEYS.USER);
  },
  async clearAll() {
    await clearSecureAccountSession(this);
  },
  /**
   * 列出 Keychain 里仍残留的账号会话项（只返回键名，不读内容、不解析 JSON）。
   * 手动「清除本机登录缓存」后用它核对是否真的删干净。
   */
  async listAccountSessionResidue() {
    const names: string[] = [];
    for (const key of [KEYS.ACCESS_TOKEN, KEYS.REFRESH_TOKEN, KEYS.USER, KEYS.CASHIER_BARCODE_PRINT_PENDING]) {
      if ((await SecureStore.getItemAsync(key)) !== null) names.push(key);
    }
    return names;
  },
  /** Keychain 写入 → 读回 → 删除的自检；任一步失败或读回不一致都会抛错。 */
  async probeReadWrite() {
    const value = `probe-${Date.now()}`;
    await SecureStore.setItemAsync(KEYS.PROBE, value);
    try {
      const readBack = await SecureStore.getItemAsync(KEYS.PROBE);
      if (readBack !== value) {
        throw new Error("keychain read-back mismatch");
      }
    } finally {
      await SecureStore.deleteItemAsync(KEYS.PROBE).catch(() => undefined);
    }
  },
};
