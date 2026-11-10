import * as Application from "expo-application";
import * as Crypto from "expo-crypto";
import { type Href, useRouter } from "expo-router";
import { useMemo } from "react";

import { describeNetworkCheckApp, describeNetworkCheckDevice } from "@/features/network-check";
import { ZellerProbeScreen, ZellerSdkHost, type ZellerSdkVendor } from "@/features/zeller-probe";

/**
 * Zeller 真机验证页（开发工具），用地址 hbpos-ipad://zeller-probe 打开，收银界面里没有入口。
 * 不经过设备 / 收银员门禁：它不读写任何 POS 数据，只调用 Zeller SDK。
 *
 * 中文注释：正式版（production 构建）不提供，页面只显示说明，也不加载 SDK。
 * 这里的扣款是真实扣款，而且不建订单、不进日结，不能让门店在正式包里误用。
 */
const PROBE_AVAILABLE = process.env.EXPO_PUBLIC_HBPOS_BUILD_PROFILE !== "production";

const PROBE_PORTS = {
  newGuid: () => Crypto.randomUUID(),
  nowMs: () => Date.now(),
  nowIso: () => new Date().toISOString(),
};

export default function ZellerProbeRoute() {
  const router = useRouter();
  const onBack = () => (router.canGoBack() ? router.back() : router.replace("/" as Href));
  const vendor = useMemo<ZellerSdkVendor>(
    () => ({
      vendorName: "Hot Bargain",
      vendorApplicationName: "HB POS iPad",
      vendorApplicationVersion: Application.nativeApplicationVersion ?? "0.0.0",
      vendorDeviceType: "tablet",
    }),
    [],
  );
  const context = useMemo(
    () => [
      "Zeller terminal check",
      `app: ${describeNetworkCheckApp()}`,
      `device: ${describeNetworkCheckDevice()}`,
      "sdk: @zeller-public/payments-sdk-react-native 0.2.5",
    ],
    [],
  );

  if (!PROBE_AVAILABLE) {
    return (
      <ZellerProbeScreen
        context={context}
        onBack={onBack}
        ports={PROBE_PORTS}
        sdk={{ status: "unavailable" }}
      />
    );
  }

  return (
    <ZellerSdkHost vendor={vendor}>
      {(sdk) => (
        <ZellerProbeScreen context={context} onBack={onBack} ports={PROBE_PORTS} sdk={sdk} />
      )}
    </ZellerSdkHost>
  );
}
