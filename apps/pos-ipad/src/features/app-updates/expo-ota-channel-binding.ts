import type {
  ExpoOtaChannelBinding,
  ExpoOtaChannelBindingStore,
} from "./expo-ota-update-port";

/** 只依赖同步 KV 的最小接口，便于测试注入。 */
export type SyncKeyValueStore = Readonly<{
  getItemSync(key: string): string | null;
  setItemSync(key: string, value: string): void;
  removeItemSync(key: string): boolean;
}>;

export const EXPO_OTA_CHANNEL_BINDING_KEY = "hbpos.ipad.ota-channel-binding.v1";

/**
 * 「已下载 OTA 的 channel + 下载它的原生包 build 号」存在本机 KV（与 expo-updates 的
 * UserDefaults 覆盖并存）。必须同步读写：端口在启动早期同步构造。
 * 内容不涉密（只是 channel 名与 build 号）。
 */
export function createExpoOtaChannelBindingStore(
  storage: SyncKeyValueStore,
): ExpoOtaChannelBindingStore {
  return Object.freeze({
    read(): ExpoOtaChannelBinding | null {
      const raw = storage.getItemSync(EXPO_OTA_CHANNEL_BINDING_KEY);
      if (raw === null) return null;
      try {
        const parsed = JSON.parse(raw) as Record<string, unknown>;
        if (
          typeof parsed.channel === "string" &&
          parsed.channel.length > 0 &&
          typeof parsed.nativeBuild === "string" &&
          parsed.nativeBuild.length > 0
        ) {
          return Object.freeze({
            channel: parsed.channel,
            nativeBuild: parsed.nativeBuild,
          });
        }
      } catch {
        // 损坏的记录按「没有记录」处理：启动时会清掉覆盖，回到原生包自带版本。
      }
      return null;
    },
    write(binding: ExpoOtaChannelBinding | null): void {
      if (binding === null) {
        storage.removeItemSync(EXPO_OTA_CHANNEL_BINDING_KEY);
        return;
      }
      storage.setItemSync(
        EXPO_OTA_CHANNEL_BINDING_KEY,
        JSON.stringify({
          channel: binding.channel,
          nativeBuild: binding.nativeBuild,
        }),
      );
    },
  });
}
