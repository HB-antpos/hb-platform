import axios from "axios";
import { buildTransportApiBaseUrl, ensureApiTransportHydrated } from "@/shared/api/transport";
import { DeviceAccountStorage } from "./device-account-storage-runtime";
import { normalizeMobileDeviceAccountToken } from "./device-activation-contract";

export async function exchangeStoredDeviceAccountToken() {
  const binding = await DeviceAccountStorage.loadBinding();
  if (!binding) {
    throw new Error("DEVICE_ACCOUNT_BINDING_NOT_FOUND");
  }
  await ensureApiTransportHydrated();
  // 凭据仍只发往绑定时确定的服务器（身份 host 是 binding.apiHost）；
  // 传输入口（边缘/直连）是同一后端，只决定经哪个域名到达，不改变信任边界。
  const response = await axios.post(
    `${buildTransportApiBaseUrl(binding.apiHost)}/mobile/v1/device-session/exchange`,
    {
      hardwareId: binding.hardwareId,
      credential: binding.credential,
    },
    {
      timeout: 30_000,
      headers: { "Content-Type": "application/json" },
    },
  );
  const envelope = response.data as { data?: unknown; Data?: unknown };
  return normalizeMobileDeviceAccountToken(
    envelope?.data ?? envelope?.Data ?? response.data,
  );
}
