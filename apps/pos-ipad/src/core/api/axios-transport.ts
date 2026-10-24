import { create, type AxiosInstance } from "axios";

import {
  createAxiosHbposTransport as createSharedAxiosHbposTransport,
  type HbposAuthenticationFailureHandler,
  type HbposRequestCredentialProvider,
  type HbposTransport,
} from "@hb/pos-api-client/transport";
import { sharedApiTransportSelector } from "@hb/pos-api-client/transport-failover";

export type {
  HbposAuthenticationFailureHandler,
  HbposRequestCredentialProvider,
  HbposRequestCredentials,
} from "@hb/pos-api-client/transport";

const DEFAULT_TRANSPORT_POLICY = {
  cashierHeader: "override-explicit",
  responseHeaders: "omit",
} as const;
const FRESH_CASHIER_TRANSPORT_POLICY = {
  cashierHeader: "preserve-explicit",
  responseHeaders: "omit",
} as const;
/**
 * 多入口故障转移：配置地址（设备身份）是 hotbargain.vip 时，实际请求按后台为本店设置的首选入口走
 * （EdgeOne pos.hotbargain.vip / AWS 悉尼 pos-syd.hotbargain.vip / 直连 hotbargain.vip，默认 EdgeOne），
 * 首选不通时读请求依次换到备用入口；不在入口组内的地址（本地开发、局域网）不受影响。
 * 所有 transport 共用同一个进程内选择器，状态一致。
 */
const FAILOVER = { selector: sharedApiTransportSelector } as const;

export function createAxiosHbposTransport(
  baseUrl: string,
  credentialProvider: HbposRequestCredentialProvider,
  instance: AxiosInstance = create({ baseURL: baseUrl, timeout: 15_000 }),
  authenticationFailureHandler?: HbposAuthenticationFailureHandler,
): HbposTransport {
  return createSharedAxiosHbposTransport(
    baseUrl,
    credentialProvider,
    instance,
    authenticationFailureHandler,
    DEFAULT_TRANSPORT_POLICY,
    FAILOVER,
  );
}

/** 设备注册重置仅使用本次在线登录所得的短时票据，不能回退到持久缓存。 */
export function createFreshCashierAxiosHbposTransport(
  baseUrl: string,
  credentialProvider: HbposRequestCredentialProvider,
  instance: AxiosInstance = create({ baseURL: baseUrl, timeout: 15_000 }),
  authenticationFailureHandler?: HbposAuthenticationFailureHandler,
): HbposTransport {
  return createSharedAxiosHbposTransport(
    baseUrl,
    credentialProvider,
    instance,
    authenticationFailureHandler,
    FRESH_CASHIER_TRANSPORT_POLICY,
    FAILOVER,
  );
}
