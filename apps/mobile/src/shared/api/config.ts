import { EDGE_API_HOST } from "@/shared/api/transport-failover";
import { AppAsyncStorage } from "@/shared/storage/async-storage";

const API_HOST_STORAGE_KEY = "hbweb_api_host";
export const DEFAULT_API_HOST = normalizeApiHost(process.env.EXPO_PUBLIC_API_BASE_URL) || "hotbargain.vip";
export const API_PROTOCOL = "http";
// 本机常同时跑多个 worktree 的后端（5002/5003/...），开发时可用 EXPO_PUBLIC_API_PORT 指向其中一个；未设置时仍是发布端口。
export const API_PORT = process.env.EXPO_PUBLIC_API_PORT?.trim() || "5002";
export const API_PATH = "/api";
const PRODUCTION_API_HOST = "hotbargain.vip";
// 同样经 Nginx HTTPS 代理（443，不带端口）的主机：生产域名 + 走 EdgeOne 的边缘入口 + AWS 悉尼测速服务器。
// 其余主机仍按「明文 HTTP + 5002 端口」拼，用于局域网调试；iOS 的 ATS 会拦截域名的明文 HTTP，所以域名主机必须在这里登记。
// 边缘入口只作为传输层入口使用（见 transport.ts），不会成为设备绑定等身份比较使用的 host。
const HTTPS_PROXY_API_HOSTS: ReadonlySet<string> = new Set([PRODUCTION_API_HOST, EDGE_API_HOST, "hotbargain.group"]);
export const DEFAULT_API_BASE_URL = buildApiBaseUrl(DEFAULT_API_HOST);
// 服务器设置弹窗使用的预设地址，线上地址必须放在首位作为发布默认选项。
export const API_HOST_PRESETS = [
  { key: "production", host: "hotbargain.vip", labelKey: "apiHost.presets.production" },
  { key: "local", host: "192.168.31.247", labelKey: "apiHost.presets.local" },
] as const;

let cachedApiHost = DEFAULT_API_HOST;
let hasLoadedStoredApiHost = false;
let storedApiHostLoad: Promise<string> | null = null;
let apiHostRevision = 0;

export function normalizeApiHost(input?: string | null) {
  const raw = input?.trim();
  if (!raw) {
    return "";
  }

  try {
    const candidate = raw.includes("://") ? raw : `${API_PROTOCOL}://${raw}`;
    const url = new URL(candidate);
    return url.hostname.trim();
  } catch {
    return raw
      .replace(/^https?:\/\//i, "")
      .split("/")[0]
      .split(":")[0]
      .trim();
  }
}

export function buildApiBaseUrl(host: string) {
  // 生产域名通过 Nginx HTTPS 代理进入 5002，移动端不再直连明文端口。
  if (HTTPS_PROXY_API_HOSTS.has(host)) {
    return `https://${host}${API_PATH}`;
  }
  return `${API_PROTOCOL}://${host}:${API_PORT}${API_PATH}`;
}

export function getCurrentApiHost() {
  return cachedApiHost;
}

export async function getStoredApiHost() {
  if (hasLoadedStoredApiHost) {
    return cachedApiHost;
  }

  if (storedApiHostLoad) {
    return storedApiHostLoad;
  }

  const revisionAtStart = apiHostRevision;
  const load = AppAsyncStorage.getString(API_HOST_STORAGE_KEY)
    .then((value) => {
      const storedHost = normalizeApiHost(value);
      const loadedHost = storedHost || DEFAULT_API_HOST;
      // setStoredApiHost() 可能在桥接读取期间完成；旧读取不能覆盖新主机。
      if (revisionAtStart === apiHostRevision) {
        cachedApiHost = loadedHost;
        hasLoadedStoredApiHost = true;
      }
      return cachedApiHost;
    })
    .catch((error) => {
      // 读取失败不能污染后续请求；下一次调用应重新读取。
      if (revisionAtStart === apiHostRevision) {
        hasLoadedStoredApiHost = false;
      }
      throw error;
    });

  storedApiHostLoad = load;
  void load.then(
    () => {
      if (storedApiHostLoad === load) storedApiHostLoad = null;
    },
    () => {
      if (storedApiHostLoad === load) storedApiHostLoad = null;
    },
  );
  return load;
}

export async function setStoredApiHost(input: string) {
  const host = normalizeApiHost(input) || DEFAULT_API_HOST;
  // 保存成功后才发布新地址，避免并发请求使用最终未能保存的服务器。
  await AppAsyncStorage.setString(API_HOST_STORAGE_KEY, host);
  apiHostRevision += 1;
  cachedApiHost = host;
  hasLoadedStoredApiHost = true;
  return host;
}
