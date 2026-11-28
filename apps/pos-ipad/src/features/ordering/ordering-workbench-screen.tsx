import { MaterialCommunityIcons } from "@expo/vector-icons";
import { StatusBar } from "expo-status-bar";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AppState, Modal, ScrollView, Text, View } from "react-native";

import type { OrderingUser } from "./ordering-auth";
import { ORDERING_IDLE_TIMEOUT_MS } from "./ordering-config";
import { orderingText, resolveOrderingLocale, type OrderingCopyKey } from "./ordering-copy";
import { OrderingShopView, type OrderingShopViewHandle } from "./ordering-shop-view";
import {
  formatIdleRemaining,
  resolveDefaultOrderingStore,
  toOrderingStores,
  type OrderingStore,
} from "./ordering-stores";
import {
  OrderingSupplierTokenSource,
  type OrderingCrypto,
} from "./ordering-token-source";
import { OrderingWebBridge } from "./ordering-web-bridge";

import { SUPPLIER_ASSISTANT_EXTENSION_VERSION } from "@/features/supplier-assistant/generated/supplier-assistant-content.generated";
import { SupplierAssistantWorkspace } from "@/features/supplier-assistant/supplier-assistant-screen";
import {
  SupplierAssistantSession,
  type FetchLike,
} from "@/features/supplier-assistant/supplier-assistant-session";
import { PosButton } from "@/ui/controls/pos-button";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

export type OrderingSignOutReason = "user" | "idle" | "session-lost";

export type OrderingWorkbenchScreenProps = Readonly<{
  user: OrderingUser;
  language: string;
  /** 已注册设备绑定的分店：订货助手默认用它（账号能访问时）。未注册设备为 null。 */
  deviceStoreCode: string | null;
  crypto: OrderingCrypto;
  /** 退出（网页会话已作废或尽力作废之后）回到首页。 */
  signOut(reason: OrderingSignOutReason, bridge: OrderingWebBridge): Promise<void>;
  /** 路由把 BLE 扫码枪扫到的码交给这里。 */
  registerScanHandler?: (handler: ((barcode: string) => void) | null) => void;
  onSwitchLanguage?: () => void;
  /** 测试注入；默认用全局 fetch。 */
  fetchImpl?: FetchLike;
  now?: () => number;
}>;

type Tab = "shop" | "assistant";

type StoresState =
  | Readonly<{ phase: "idle" | "loading" | "failed" }>
  | Readonly<{ phase: "ready"; stores: readonly OrderingStore[] }>;

const defaultFetch: FetchLike = (input, init) => fetch(input, init);

/**
 * 订货工作台：顶部两个页签「仓库订货 / 澳洲供应商订货」，右侧闲置倒计时、分店、员工、退出。
 *
 * 中文注释：
 * 1. 仓库订货网页窗口一直挂着（切到订货助手也不卸载），它是网页会话宿主，订货助手的令牌要靠它申请。
 * 2. 订货助手页签第一次打开时才加载（拿令牌、拉分店），之后两个页签都保留浏览位置。
 * 3. 闲置 15 分钟（两个网页里都没有点按 / 输入、App 自己的按钮也没点）自动退出；
 *    App 切到后台超过 15 分钟，回来时同样退出。
 */
export function OrderingWorkbenchScreen({
  crypto,
  deviceStoreCode,
  fetchImpl = defaultFetch,
  language,
  now = Date.now,
  onSwitchLanguage,
  registerScanHandler,
  signOut,
  user,
}: OrderingWorkbenchScreenProps) {
  usePosThemeVersion();
  const locale = resolveOrderingLocale(language);
  const t = useCallback(
    (key: OrderingCopyKey, values?: Readonly<Record<string, string | number>>) => orderingText(locale, key, values),
    [locale],
  );

  const bridge = useMemo(() => new OrderingWebBridge(), []);
  const session = useMemo(
    () =>
      new SupplierAssistantSession(
        // 换令牌与代发接口共用同一个 fetch（测试可注入）。
        new OrderingSupplierTokenSource(bridge, crypto, fetchImpl),
        fetchImpl,
        SUPPLIER_ASSISTANT_EXTENSION_VERSION,
      ),
    [bridge, crypto, fetchImpl],
  );

  const [tab, setTab] = useState<Tab>("shop");
  const [assistantOpened, setAssistantOpened] = useState(false);
  const [stores, setStores] = useState<StoresState>({ phase: "idle" });
  const [store, setStore] = useState<OrderingStore | null>(null);
  const [pickerVisible, setPickerVisible] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [signingOut, setSigningOut] = useState(false);

  // ---- 退出（只走一次）----
  const signingOutRef = useRef(false);
  const doSignOut = useCallback(
    (reason: OrderingSignOutReason) => {
      if (signingOutRef.current) return;
      signingOutRef.current = true;
      setSigningOut(true);
      void signOut(reason, bridge);
    },
    [bridge, signOut],
  );

  // ---- 闲置自动退出 ----
  const lastActivityRef = useRef(now());
  const touch = useCallback(() => {
    lastActivityRef.current = now();
  }, [now]);
  const signOutIdle = useCallback(() => doSignOut("idle"), [doSignOut]);
  const signOutSessionLost = useCallback(() => doSignOut("session-lost"), [doSignOut]);
  const showBlockedNotice = useCallback(() => setNotice(t("workbench.shopBlocked")), [t]);
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (state) => {
      // 回到前台时按真实时间判断；后台期间计时器可能被系统暂停。
      if (state === "active" && now() - lastActivityRef.current >= ORDERING_IDLE_TIMEOUT_MS) {
        doSignOut("idle");
      }
    });
    return () => subscription.remove();
  }, [doSignOut, now]);

  // ---- 订货助手分店 ----
  const loadStores = useCallback(async () => {
    setStores({ phase: "loading" });
    try {
      const result = await session.request("/api/react/v1/browser-extension/stores", { method: "GET" });
      if (!result.success) throw new Error(result.errorCode ?? "STORES_FAILED");
      const list = toOrderingStores(result.data);
      setStores({ phase: "ready", stores: list });
      const preferred = resolveDefaultOrderingStore(list, deviceStoreCode);
      setStore((current) => current ?? preferred);
      if (!preferred && list.length > 1) setPickerVisible(true);
    } catch {
      setStores({ phase: "failed" });
    }
  }, [deviceStoreCode, session]);

  const openTab = (next: Tab) => {
    touch();
    setTab(next);
    if (next === "assistant" && !assistantOpened) {
      setAssistantOpened(true);
      void loadStores();
    }
  };

  // ---- 扫码：当前页签是谁就交给谁 ----
  const shopRef = useRef<OrderingShopViewHandle>(null);
  const assistantScanRef = useRef<((barcode: string) => void) | null>(null);
  const tabRef = useRef(tab);
  tabRef.current = tab;
  useEffect(() => {
    if (!registerScanHandler) return undefined;
    registerScanHandler((barcode) => {
      lastActivityRef.current = now();
      if (tabRef.current === "assistant") assistantScanRef.current?.(barcode);
      else shopRef.current?.sendScan(barcode);
    });
    return () => registerScanHandler(null);
  }, [now, registerScanHandler]);
  const registerAssistantScan = useCallback((handler: ((barcode: string) => void) | null) => {
    assistantScanRef.current = handler;
  }, []);

  // 网页被拦回 /shop 时短暂提示一下。
  useEffect(() => {
    if (!notice) return undefined;
    const timer = setTimeout(() => setNotice(null), 3000);
    return () => clearTimeout(timer);
  }, [notice]);

  const storeLabel =
    store != null
      ? `${store.storeCode} · ${store.storeName}`
      : stores.phase === "loading"
        ? t("workbench.storeLoading")
        : t("workbench.storePick");

  const header = (
    <View style={styles.bar} testID="ordering-workbench-bar">
      <Text accessibilityRole="header" style={styles.title}>
        {t("workbench.title")}
      </Text>
      <View accessibilityRole="tablist" style={styles.segment}>
        <SegmentTab
          icon="warehouse"
          label={t("workbench.tab.shop")}
          onPress={() => openTab("shop")}
          selected={tab === "shop"}
          testID="ordering-tab-shop"
        />
        <SegmentTab
          icon="web"
          label={t("workbench.tab.assistant")}
          onPress={() => openTab("assistant")}
          selected={tab === "assistant"}
          testID="ordering-tab-assistant"
        />
      </View>
      <View style={styles.spacer} />
      <IdleCountdown lastActivityRef={lastActivityRef} now={now} onExpire={signOutIdle} t={t} />
      {tab === "assistant" ? (
        <PosButton
          disabled={stores.phase !== "ready" || stores.stores.length === 0}
          icon="storefront-outline"
          label={storeLabel}
          onPress={() => setPickerVisible(true)}
          size="small"
          testID="ordering-store"
          tone="secondary"
        />
      ) : null}
      <View style={styles.who} testID="ordering-user">
        <View style={styles.avatar}>
          <Text style={styles.avatarText}>{user.displayName.slice(0, 1).toUpperCase()}</Text>
        </View>
        <Text numberOfLines={1} style={styles.whoText}>
          {user.displayName}
        </Text>
      </View>
      <PosButton
        disabled={signingOut}
        icon="logout"
        label={t("workbench.signOut")}
        loading={signingOut}
        onPress={() => doSignOut("user")}
        size="small"
        testID="ordering-sign-out"
        tone="secondary"
      />
    </View>
  );

  return (
    <PosScreenFrame
      header={header}
      language={locale}
      {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
      rail="none"
      testID="ordering-workbench-screen"
    >
      <StatusBar style="light" />
      <View style={styles.body}>
        <View
          accessibilityElementsHidden={tab !== "shop"}
          importantForAccessibility={tab !== "shop" ? "no-hide-descendants" : "auto"}
          pointerEvents={tab !== "shop" ? "none" : "auto"}
          style={[styles.pane, tab !== "shop" && styles.hidden]}
          testID="ordering-pane-shop"
        >
          <OrderingShopView
            ref={shopRef}
            bridge={bridge}
            onActivity={touch}
            onBlockedNavigation={showBlockedNotice}
            onSessionLost={signOutSessionLost}
          />
        </View>
        {assistantOpened ? (
          <View
            accessibilityElementsHidden={tab !== "assistant"}
            importantForAccessibility={tab !== "assistant" ? "no-hide-descendants" : "auto"}
            pointerEvents={tab !== "assistant" ? "none" : "auto"}
            style={[styles.pane, tab !== "assistant" && styles.hidden]}
            testID="ordering-pane-assistant"
          >
            {store ? (
              <SupplierAssistantWorkspace
                onActivity={touch}
                registerScanHandler={registerAssistantScan}
                session={session}
                storeCode={store.storeCode}
              />
            ) : (
              <View style={styles.centered} testID="ordering-store-state">
                <Text style={styles.message}>
                  {stores.phase === "failed"
                    ? t("workbench.storesFailed")
                    : stores.phase === "ready" && stores.stores.length === 0
                      ? t("workbench.storesEmpty")
                      : stores.phase === "ready"
                        ? t("workbench.storePick")
                        : t("workbench.storeLoading")}
                </Text>
                {stores.phase === "failed" ? (
                  <PosButton
                    icon="refresh"
                    label={t("workbench.retry")}
                    onPress={() => void loadStores()}
                    size="medium"
                    testID="ordering-stores-retry"
                  />
                ) : stores.phase === "ready" && stores.stores.length > 0 ? (
                  <PosButton
                    icon="storefront-outline"
                    label={t("workbench.storePick")}
                    onPress={() => setPickerVisible(true)}
                    size="medium"
                    testID="ordering-store-pick"
                  />
                ) : null}
              </View>
            )}
          </View>
        ) : null}
        {notice ? (
          <View pointerEvents="none" style={styles.notice} testID="ordering-notice">
            <Text style={styles.noticeText}>{notice}</Text>
          </View>
        ) : null}
      </View>
      <StorePicker
        onClose={() => setPickerVisible(false)}
        onSelect={(next) => {
          touch();
          setStore(next);
          setPickerVisible(false);
        }}
        selectedStoreCode={store?.storeCode ?? null}
        stores={stores.phase === "ready" ? stores.stores : []}
        t={t}
        visible={pickerVisible && stores.phase === "ready"}
      />
    </PosScreenFrame>
  );
}

/**
 * 闲置倒计时：自己每秒刷新，不连带整个工作台（和两个网页窗口）重绘。
 */
function IdleCountdown({
  lastActivityRef,
  now,
  onExpire,
  t,
}: Readonly<{
  lastActivityRef: Readonly<{ current: number }>;
  now: () => number;
  onExpire(): void;
  t: (key: OrderingCopyKey, values?: Readonly<Record<string, string | number>>) => string;
}>) {
  const [remainingMs, setRemainingMs] = useState(ORDERING_IDLE_TIMEOUT_MS);
  useEffect(() => {
    const timer = setInterval(() => {
      const left = ORDERING_IDLE_TIMEOUT_MS - (now() - lastActivityRef.current);
      setRemainingMs(left);
      if (left <= 0) onExpire();
    }, 1000);
    return () => clearInterval(timer);
  }, [lastActivityRef, now, onExpire]);
  return (
    <View style={styles.idle} testID="ordering-idle">
      <MaterialCommunityIcons color={posColors.yellowInk} name="timer-outline" size={16} />
      <Text style={styles.idleText}>{t("workbench.idle", { time: formatIdleRemaining(remainingMs) })}</Text>
    </View>
  );
}

function SegmentTab({
  icon,
  label,
  onPress,
  selected,
  testID,
}: Readonly<{ icon: "warehouse" | "web"; label: string; selected: boolean; onPress(): void; testID: string }>) {
  return (
    <PosPressable
      accessibilityRole="tab"
      accessibilityState={{ selected }}
      onPress={onPress}
      sound="navigate"
      style={[styles.segmentTab, selected && styles.segmentTabSelected]}
      testID={testID}
    >
      <MaterialCommunityIcons color={selected ? posColors.ink : posColors.mutedInk} name={icon} size={20} />
      <Text style={[styles.segmentLabel, selected && styles.segmentLabelSelected]}>{label}</Text>
    </PosPressable>
  );
}

function StorePicker({
  onClose,
  onSelect,
  selectedStoreCode,
  stores,
  t,
  visible,
}: Readonly<{
  visible: boolean;
  stores: readonly OrderingStore[];
  selectedStoreCode: string | null;
  onSelect(store: OrderingStore): void;
  onClose(): void;
  t: (key: OrderingCopyKey) => string;
}>) {
  return (
    <Modal animationType="fade" onRequestClose={onClose} transparent visible={visible}>
      <View style={styles.modalBackdrop}>
        <View style={styles.modal} testID="ordering-store-picker">
          <View style={styles.modalHeader}>
            <Text style={styles.modalTitle}>{t("workbench.storePickerTitle")}</Text>
            <PosButton
              icon="close"
              label={t("workbench.storePickerClose")}
              onPress={onClose}
              size="small"
              testID="ordering-store-picker-close"
              tone="quiet"
            />
          </View>
          <ScrollView contentContainerStyle={styles.modalList}>
            {stores.map((store) => {
              const selected = store.storeCode === selectedStoreCode;
              return (
                <PosPressable
                  accessibilityRole="button"
                  accessibilityState={{ selected }}
                  key={store.storeCode}
                  onPress={() => onSelect(store)}
                  style={[styles.storeOption, selected && styles.storeOptionSelected]}
                  testID={`ordering-store-${store.storeCode}`}
                >
                  <Text style={styles.storeOptionCode}>{store.storeCode}</Text>
                  <Text numberOfLines={1} style={styles.storeOptionName}>
                    {store.storeName}
                  </Text>
                  {selected ? <MaterialCommunityIcons color={posColors.accent} name="check" size={22} /> : null}
                </PosPressable>
              );
            })}
          </ScrollView>
        </View>
      </View>
    </Modal>
  );
}

const styles = createPosStyles((colors) => ({
  bar: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  title: { color: colors.ink, fontSize: 20, fontWeight: "900", marginRight: 4 },
  segment: { backgroundColor: colors.surfaceSunken, borderRadius: 12, flexDirection: "row", gap: 4, padding: 4 },
  segmentTab: {
    alignItems: "center",
    borderRadius: 9,
    flexDirection: "row",
    gap: 6,
    minHeight: 44,
    paddingHorizontal: 16,
  },
  segmentTabSelected: { backgroundColor: colors.surface },
  segmentLabel: { color: colors.mutedInk, fontSize: 15, fontWeight: "800" },
  segmentLabelSelected: { color: colors.ink },
  spacer: { flex: 1 },
  idle: {
    alignItems: "center",
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
    borderRadius: 999,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 6,
    paddingHorizontal: 12,
    paddingVertical: 6,
  },
  idleText: { color: colors.yellowInk, fontSize: 13, fontWeight: "800" },
  who: {
    alignItems: "center",
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: 999,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 8,
    maxWidth: 200,
    paddingLeft: 5,
    paddingRight: 12,
    paddingVertical: 5,
  },
  avatar: {
    alignItems: "center",
    backgroundColor: colors.blue,
    borderRadius: 14,
    height: 28,
    justifyContent: "center",
    width: 28,
  },
  avatarText: { color: colors.onAccent, fontSize: 13, fontWeight: "900" },
  whoText: { color: colors.ink, flexShrink: 1, fontSize: 14, fontWeight: "800" },
  body: { flex: 1, minHeight: 0 },
  pane: { bottom: 0, left: 0, position: "absolute", right: 0, top: 0 },
  // 切页签只隐藏不卸载：两个网页都保留浏览位置，仓库订货网页（会话宿主）始终在。
  // 不能用 display: "none"：新架构会把这棵子树移出原生视图层级，WKWebView 离开窗口后页面 JS 被挂起，
  // 订货助手经仓库订货网页申请授权码的脚本就永远等不到回信。改为透明 + 压到底层 + 不接收触摸。
  hidden: { opacity: 0, zIndex: -1 },
  centered: { alignItems: "center", flex: 1, gap: 12, justifyContent: "center", padding: 24 },
  message: { color: colors.ink, fontSize: 16, fontWeight: "600", textAlign: "center" },
  notice: {
    alignSelf: "center",
    backgroundColor: colors.inverseSurface,
    borderRadius: 12,
    bottom: 24,
    paddingHorizontal: 18,
    paddingVertical: 12,
    position: "absolute",
  },
  noticeText: { color: colors.onInverse, fontSize: 14, fontWeight: "700" },
  modalBackdrop: { alignItems: "center", backgroundColor: colors.overlay, flex: 1, justifyContent: "center", padding: 24 },
  modal: {
    backgroundColor: colors.surface,
    borderRadius: 18,
    maxHeight: "80%",
    maxWidth: 520,
    padding: 20,
    width: "100%",
  },
  modalHeader: { alignItems: "center", flexDirection: "row", gap: 12, marginBottom: 12 },
  modalTitle: { color: colors.ink, flex: 1, fontSize: 20, fontWeight: "900" },
  modalList: { gap: 8 },
  storeOption: {
    alignItems: "center",
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 12,
    minHeight: 56,
    paddingHorizontal: 16,
  },
  storeOptionSelected: { backgroundColor: colors.selectedSurface, borderColor: colors.accent },
  storeOptionCode: { color: colors.ink, fontSize: 16, fontWeight: "900", width: 64 },
  storeOptionName: { color: colors.ink, flex: 1, fontSize: 16, fontWeight: "600" },
}));
