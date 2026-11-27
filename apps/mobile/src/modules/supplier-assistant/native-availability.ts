import { TurboModuleRegistry } from "react-native";

/**
 * 订货助手依赖 react-native-webview 的原生模块。
 *
 * 中文注释：
 * 1. react-native-webview 在模块加载时就强制取 RNCWebViewModule 原生模块（取不到即抛错），
 *    原生包里没有这个模块会直接抛错。main 上的 JS 会通过 OTA 发到还没装 WebView 的旧原生包，
 *    所以任何地方都不能在顶层 import react-native-webview，必须先用这里探测。
 * 2. 探测不到时：菜单里隐藏订货助手入口，路由页显示「请更新 App」。
 * 3. 用非强制的 TurboModuleRegistry.get：新架构（本 App 开启）与旧架构桥接都能查到。
 */
export const SUPPLIER_ASSISTANT_ROUTE_NAME = "supplier-assistant";

let cached: boolean | null = null;

export function isSupplierAssistantNativeAvailable(): boolean {
  if (cached === null) {
    try {
      cached = TurboModuleRegistry.get("RNCWebViewModule") != null;
    } catch {
      cached = false;
    }
  }
  return cached;
}
