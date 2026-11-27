import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useState,
  useSyncExternalStore,
  type ReactElement,
  type ReactNode,
} from "react";
import { View } from "react-native";
import {
  SafeAreaInsetsContext,
  SafeAreaView,
} from "react-native-safe-area-context";

import { PosAppRail, type PosNavModel } from "./pos-app-rail";
import type { PosActiveDestination } from "./pos-navigation";
import { PosStatusStrip } from "./status-strip";

import { createPosStyles, usePosThemeVersion } from "@/ui/theme";

/**
 * 常驻外壳（顶栏 + 左侧栏）：放在 app/(pos)/_layout.tsx 里，整组功能页共用一份，
 * 切换页面时外壳不卸载，只有内容区（路由栈）淡入淡出。
 *
 * 中文注释：
 * 1. 各页面仍然用 <PosScreenFrame>：在外壳里时它只渲染页头 + 内容，并把自己要的外壳配置
 *    （侧栏高亮、侧栏模型、是否锁住、是否隐藏顶栏）登记到这里；外壳之外（登录、付款等）照旧整页渲染。
 * 2. 配置放在外部 store，只有顶栏 / 侧栏两个出口组件订阅；外壳布局本身不订阅，
 *    所以页面每次渲染都登记也不会让页面再被重新渲染（不会形成渲染循环）。
 * 3. 路由栈里可能同时挂着收银页和另一页：只有获得焦点的页面能登记，后台页面的渲染不会改外壳。
 */
export type PosShellChrome = Readonly<{
  active: PosActiveDestination;
  /** null = 这一页不显示左侧栏（收银成功、锁屏等）。 */
  model: PosNavModel | null;
  navigationLocked: boolean;
  /** false = 隐藏顶栏；undefined = 默认状态条；传入节点 = 自定义顶栏。 */
  topBar: ReactNode | false | undefined;
  language: string | undefined;
  onSwitchLanguage: (() => void) | undefined;
  appVersionLabel: string | null;
}>;

export class PosShellChromeStore {
  private owner: symbol | null = null;
  private chrome: PosShellChrome | null = null;
  private readonly listeners = new Set<() => void>();

  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  public readonly getSnapshot = (): PosShellChrome | null => this.chrome;

  public publish(owner: symbol, chrome: PosShellChrome): void {
    this.owner = owner;
    this.chrome = chrome;
    this.emit();
  }

  /** 页面卸载时只撤回自己登记的配置；已被新焦点页覆盖就不动。 */
  public release(owner: symbol): void {
    if (this.owner !== owner) return;
    this.owner = null;
    this.chrome = null;
    this.emit();
  }

  private emit(): void {
    for (const listener of [...this.listeners]) listener();
  }
}

export const PosShellHostContext = createContext<PosShellChromeStore | null>(
  null,
);

/** 页面在常驻外壳里时返回外壳 store；外壳之外（或单独渲染、测试）为 null。 */
export function usePosShellHost(): PosShellChromeStore | null {
  return useContext(PosShellHostContext);
}

/**
 * 本页是否是外壳路由栈当前获得焦点的页面；不在外壳路由栈里时为 null。
 * 由 posShellScreenLayout 按页提供，PosScreenFrame 只读它——不直接依赖 react-navigation
 * （它只有 ESM 版本，页面单测里没法加载）。
 */
const PosScreenFocusContext = createContext<boolean | null>(null);

export function usePosScreenFocused(): boolean {
  return useContext(PosScreenFocusContext) ?? true;
}

/** 路由栈给每一页的导航对象里，跟踪焦点只用到这两个方法。 */
export type PosShellScreenNavigation = Readonly<{
  isFocused(): boolean;
  addListener(event: "focus" | "blur", listener: () => void): () => void;
}>;

export function PosShellScreen({
  children,
  navigation,
}: Readonly<{ children: ReactNode; navigation: PosShellScreenNavigation }>) {
  const [focused, setFocused] = useState(() => navigation.isFocused());
  useEffect(() => {
    setFocused(navigation.isFocused());
    const unsubscribeFocus = navigation.addListener("focus", () =>
      setFocused(true),
    );
    const unsubscribeBlur = navigation.addListener("blur", () =>
      setFocused(false),
    );
    return () => {
      unsubscribeFocus();
      unsubscribeBlur();
    };
  }, [navigation]);
  return (
    <PosScreenFocusContext.Provider value={focused}>
      {children}
    </PosScreenFocusContext.Provider>
  );
}

/** 给外壳路由栈的 screenLayout：每一页外面包一层焦点跟踪。 */
export function posShellScreenLayout({
  children,
  navigation,
}: Readonly<{
  children: ReactNode;
  navigation: PosShellScreenNavigation;
}>): ReactElement {
  return <PosShellScreen navigation={navigation}>{children}</PosShellScreen>;
}

export function PosShellHost({
  children,
  store: providedStore,
}: Readonly<{ children: ReactNode; store?: PosShellChromeStore }>) {
  usePosThemeVersion();
  const store = useMemo(
    () => providedStore ?? new PosShellChromeStore(),
    [providedStore],
  );

  return (
    <PosShellHostContext.Provider value={store}>
      <View style={styles.root} testID="pos-shell-host">
        <PosShellTopOutlet store={store} />
        <SafeAreaView edges={EDGES_WITHOUT_TOP} style={styles.frame}>
          <PosShellStatusOutlet store={store} />
          <View style={styles.body}>
            <PosShellRailOutlet store={store} />
            <View style={styles.content} testID="pos-shell-content">
              {children}
            </View>
          </View>
        </SafeAreaView>
      </View>
    </PosShellHostContext.Provider>
  );
}

const EDGES_WITHOUT_TOP = ["left", "right", "bottom"] as const;

function useChrome(store: PosShellChromeStore): PosShellChrome | null {
  return useSyncExternalStore(
    store.subscribe,
    store.getSnapshot,
    store.getSnapshot,
  );
}

/** 顶部安全区：有顶栏时铺成顶栏深色（系统时间电量要看得清），隐藏顶栏时用页面底色。 */
function PosShellTopOutlet({ store }: Readonly<{ store: PosShellChromeStore }>) {
  usePosThemeVersion();
  const chrome = useChrome(store);
  const topInset = useContext(SafeAreaInsetsContext)?.top ?? 0;
  if (topInset <= 0) return null;
  return (
    <View
      pointerEvents="none"
      style={[
        chrome?.topBar === false ? styles.topInsetPlain : styles.topInsetFill,
        { height: topInset },
      ]}
      testID="pos-top-inset-fill"
    />
  );
}

function PosShellStatusOutlet({
  store,
}: Readonly<{ store: PosShellChromeStore }>) {
  usePosThemeVersion();
  const chrome = useChrome(store);
  if (chrome?.topBar === false) return null;
  if (chrome?.topBar) return <>{chrome.topBar}</>;
  return (
    <PosStatusStrip
      appVersionLabel={chrome?.appVersionLabel ?? null}
      showBrand
      showTerminalIdentity
      {...(chrome?.language ? { language: chrome.language } : {})}
      {...(chrome?.onSwitchLanguage
        ? { onSwitchLanguage: chrome.onSwitchLanguage }
        : {})}
    />
  );
}

function PosShellRailOutlet({ store }: Readonly<{ store: PosShellChromeStore }>) {
  usePosThemeVersion();
  const chrome = useChrome(store);
  if (!chrome?.model) return null;
  return (
    <PosAppRail
      active={chrome.active}
      language={chrome.language}
      locked={chrome.navigationLocked}
      model={chrome.model}
    />
  );
}

const styles = createPosStyles((colors) => ({
  body: {
    flex: 1,
    flexDirection: "row",
    minHeight: 0,
  },
  content: {
    flex: 1,
    minHeight: 0,
    minWidth: 0,
  },
  frame: {
    backgroundColor: colors.canvas,
    flex: 1,
  },
  root: {
    backgroundColor: colors.canvas,
    flex: 1,
  },
  topInsetFill: {
    backgroundColor: colors.chrome,
  },
  topInsetPlain: {
    backgroundColor: colors.canvas,
  },
}));
