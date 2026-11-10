import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { View } from "react-native";

import type { ZellerProbeTerminal } from "@/features/zeller-probe/zeller-probe-runner";
import {
  ZellerSdkHost,
  type ZellerSdkHostState,
  type ZellerSdkVendor,
} from "@/features/zeller-probe/zeller-sdk-host";
import { createPosStyles, usePosThemeVersion } from "@/ui/theme";

/**
 * 给一整个页面提供 Zeller 终端：页面里任何位置的组件都可以用 useZellerTerminalBoundary() 取到 SDK 客户端。
 *
 * 中文注释：为什么不能把 SDK 的 Provider 直接包在用到它的那张卡片外面——
 * 1. SDK 的登录 / 刷卡窗口是 Provider 自己渲染的绝对定位弹层，铺满 Provider 所在的容器。
 *    包在卡片外面，窗口就会被挤在卡片（还在滚动区）里；所以 Provider 必须挂在整页的根上。
 * 2. 又不能在 SDK 加载好之后才把页面「包进」Provider：那会改变组件树结构，整页重新挂载，页面状态全丢。
 *    所以这里让 SDK 作为页面的「兄弟节点」挂在同一个根容器里，页面本身的位置始终不变。
 * 3. SDK 只在有组件调用 activate() 之后才加载（它的入口会替换全局 URL，还会触碰 5 个原生库），
 *    不打开 Zeller 页签的人完全不受影响。
 */

export type ZellerTerminalBoundaryState = ZellerSdkHostState | Readonly<{ status: "idle" }>;

export type ZellerTerminalBoundaryValue = Readonly<{
  state: ZellerTerminalBoundaryState;
  /** 开始加载 SDK；重复调用无副作用。 */
  activate(): void;
  /** 打开网络检测页；页面没有提供时为 undefined。 */
  onOpenNetworkCheck?: () => void;
}>;

const ZellerTerminalContext = createContext<ZellerTerminalBoundaryValue | null>(null);

/**
 * 直接提供一个现成的 Zeller 终端状态，不加载 SDK。给界面测试用；
 * 正式代码一律用 ZellerTerminalBoundary。
 */
export const ZellerTerminalBoundaryContext = ZellerTerminalContext;

/** 不在 ZellerTerminalBoundary 之内时返回 null：调用方据此隐藏 Zeller 相关界面。 */
export function useZellerTerminalBoundary(): ZellerTerminalBoundaryValue | null {
  return useContext(ZellerTerminalContext);
}

export type ZellerTerminalBoundaryProps = Readonly<{
  vendor: ZellerSdkVendor;
  onOpenNetworkCheck?: () => void;
  children: ReactNode;
}>;

export function ZellerTerminalBoundary({
  vendor,
  onOpenNetworkCheck,
  children,
}: ZellerTerminalBoundaryProps) {
  usePosThemeVersion();
  const [active, setActive] = useState(false);
  const [state, setState] = useState<ZellerTerminalBoundaryState>({ status: "idle" });
  const activate = useCallback(() => setActive(true), []);
  const value = useMemo<ZellerTerminalBoundaryValue>(
    () => ({ state, activate, ...(onOpenNetworkCheck ? { onOpenNetworkCheck } : {}) }),
    [activate, onOpenNetworkCheck, state],
  );

  return (
    <ZellerTerminalContext.Provider value={value}>
      <View style={styles.root}>
        {children}
        {active ? (
          <ZellerSdkHost vendor={vendor}>
            {(hostState) => <PublishHostState onChange={setState} state={hostState} />}
          </ZellerSdkHost>
        ) : null}
      </View>
    </ZellerTerminalContext.Provider>
  );
}

/**
 * 把 SDK 的状态上报给 boundary。
 *
 * 中文注释：SDK 的 useTerminal() 每次渲染都可能返回新对象；如果原样上报，
 * 「上报 → boundary 重新渲染 → 又拿到新对象 → 再上报」会死循环。
 * 所以对外只给一个身份不变的代理，方法调用时再转给当前最新的客户端。
 */
function PublishHostState({
  state,
  onChange,
}: Readonly<{
  state: ZellerSdkHostState;
  onChange: (state: ZellerTerminalBoundaryState) => void;
}>) {
  const latest = useRef(state);
  latest.current = state;

  const stableTerminal = useMemo<ZellerProbeTerminal>(() => {
    const current = (): ZellerProbeTerminal => {
      if (latest.current.status !== "ready") throw new Error("Zeller SDK is not ready");
      return latest.current.terminal;
    };
    return {
      initialise: () => current().initialise(),
      setup: () => current().setup(),
      purchase: (options) => current().purchase(options),
      refund: (options) => current().refund(options),
      getTransactions: (options) => current().getTransactions(options),
    };
  }, []);

  const status = state.status;
  const reason = state.status === "failed" ? state.reason : null;
  useEffect(() => {
    if (status === "ready") onChange({ status: "ready", terminal: stableTerminal });
    else if (status === "failed") onChange({ status: "failed", reason: reason ?? "" });
    else onChange({ status: "loading" });
  }, [onChange, reason, stableTerminal, status]);

  return null;
}

const styles = createPosStyles((colors) => ({
  root: { flex: 1 },
}));
