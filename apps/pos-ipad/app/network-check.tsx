import { type Href, useRouter } from "expo-router";
import { useEffect, useMemo, useState } from "react";

import { resolveHbposApiUrl } from "@/core/runtime/runtime-config";
import {
  createExpoNetworkCheckPorts,
  describeNetworkCheckApp,
  describeNetworkCheckDevice,
  NetworkCheckScreen,
} from "@/features/network-check";
import { resolveCurrentApiBaseUrl } from "@/ui/shell/network-status-bridge";

/**
 * 网络检测不经过设备 / 收银员门禁：只发只读 GET、不带任何令牌，
 * 启动失败、待审批、未登录时都要能用来排查网络。
 */
export default function NetworkCheckRoute() {
  const router = useRouter();
  const [apiBaseUrl, setApiBaseUrl] = useState<string | null>(null);
  const ports = useMemo(createExpoNetworkCheckPorts, []);
  const context = useMemo(
    () => ({
      appLabel: describeNetworkCheckApp(),
      deviceLabel: describeNetworkCheckDevice(),
      serverLabel: apiBaseUrl ?? "-",
    }),
    [apiBaseUrl],
  );

  useEffect(() => {
    let active = true;
    // 与业务请求同一套解析：钥匙串里保存的地址优先，app.config 兜底。
    void resolveCurrentApiBaseUrl()
      .catch(() => resolveHbposApiUrl(undefined))
      .then((url) => {
        if (active) setApiBaseUrl(url);
      });
    return () => {
      active = false;
    };
  }, []);

  return (
    <NetworkCheckScreen
      apiBaseUrl={apiBaseUrl}
      context={context}
      onBack={() =>
        router.canGoBack() ? router.back() : router.replace("/" as Href)
      }
      ports={ports}
    />
  );
}
