// 员工移动端「订货助手」页面。
//
// 中文注释：
// 1. 本文件顶层 import 了 react-native-webview，它在加载时就强制要求原生模块；
//    只能由路由页在 isSupplierAssistantNativeAvailable() 为真后延迟 require，别处不要直接 import。
// 2. 网页窗口里注入的是浏览器订货助手插件原样的供应商页脚本（与大屏收银 App 同一份生成文件），
//    网页发来的消息由 supplier-assistant-background.ts 逐条核对来源网址 / 供应商 / 分店后再代发。
// 3. 分店由员工自己选（后端只给员工可访问的分店），选过的记在本机、按账号分开。
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { BackHandler, Platform, ScrollView, StyleSheet, View } from "react-native";
import { useFocusEffect, useRouter } from "expo-router";
import { ActivityIndicator, Button, Chip, FAB, IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { CameraView } from "expo-camera";
import { WebView, type WebViewMessageEvent, type WebViewNavigation } from "react-native-webview";

import {
  SUPPLIER_ASSISTANT_CONTENT_SCRIPT,
  SUPPLIER_ASSISTANT_EXTENSION_VERSION,
} from "./generated/supplier-assistant-content.generated";
import {
  createSupplierAssistantBackground,
  type LocateRequest,
  resolveProfileHomeUrl,
  decideSupplierNavigation,
  type SupplierProfileLite,
  toProfileLites,
} from "./supplier-assistant-background";
import {
  resolveSupplierAssistantLocale,
  supplierAssistantText,
  type SupplierAssistantCopyKey,
} from "./supplier-assistant-copy";
import {
  createSupplierAssistantRequest,
  type SupplierAssistantHttp,
} from "./supplier-assistant-session";
import {
  resolveDefaultSupplierAssistantStore,
  resolvePrimaryStoreCode,
  supplierAssistantStoreStorageKey,
  toSupplierAssistantStores,
  type SupplierAssistantStore,
} from "./store-selection";

import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { CameraScanSheet } from "@/components/ui/CameraScanSheet";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { acquireBleScanLease } from "@/modules/scanner/ble/ble-scan-hub";
import { useCameraScan, type CameraScanMode } from "@/modules/scanner/use-camera-scan";
import { apiClient } from "@/shared/api/client";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

type LoadState =
  | Readonly<{ phase: "loading" }>
  | Readonly<{ phase: "error"; reason: "failed" | "notAllowed" }>
  | Readonly<{
      phase: "ready";
      profiles: readonly SupplierProfileLite[];
      rawProfiles: unknown;
      stores: readonly SupplierAssistantStore[];
    }>;

type CycleDto = Readonly<{
  purchaseDate?: string;
  purchaseQuantity?: number;
  salesEndDate?: string;
  salesQuantity?: number;
}>;

type CyclesDto = Readonly<{
  matchStatus?: string;
  productName?: string | null;
  latestPurchaseDate?: string | null;
  latestPurchaseQuantity?: number | null;
  salesSinceLatestPurchase?: number;
  lastYearSalesQuantity30Days?: number | null;
  lastYearSalesQuantity60Days?: number | null;
  cycles?: readonly CycleDto[];
}>;

type PanelState =
  | null
  | Readonly<{ request: LocateRequest; storeCode: string; phase: "loading" }>
  | Readonly<{ request: LocateRequest; storeCode: string; phase: "failed" }>
  | Readonly<{ request: LocateRequest; storeCode: string; phase: "ready"; data: CyclesDto }>;

// 经 apiClient 代发：自动带员工登录令牌、401 自动刷新、线路故障自动换入口。
const apiClientHttp: SupplierAssistantHttp = async (request) => {
  const response = await apiClient.request({
    method: request.method,
    url: request.url,
    headers: request.headers,
    ...(request.data !== undefined ? { data: request.data } : {}),
    ...(request.signal ? { signal: request.signal } : {}),
  });
  return { status: response.status, data: response.data };
};

// 供应商网站多是按电脑版网页写的，插件的商品卡片选择器也是；手机上默认请求电脑版网页，按钮才挂得上。
// iOS 用 WKWebView 的 contentMode；安卓换成电脑版 Chrome 的 UA。
const ANDROID_DESKTOP_USER_AGENT =
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

export function SupplierAssistantScreen() {
  const router = useRouter();
  const { language } = useAppTranslation("common");
  const locale = resolveSupplierAssistantLocale(language);
  const t = useCallback(
    (key: SupplierAssistantCopyKey, values?: Readonly<Record<string, string | number>>) =>
      supplierAssistantText(locale, key, values),
    [locale],
  );
  const user = useAuthStore((state) => state.user);
  const userGuid = user?.userGUID || user?.userGuid || null;
  const primaryStoreCode = resolvePrimaryStoreCode(user?.stores);
  const storageKey = supplierAssistantStoreStorageKey(userGuid);

  const request = useMemo(
    () => createSupplierAssistantRequest(apiClientHttp, SUPPLIER_ASSISTANT_EXTENSION_VERSION),
    [],
  );
  const [load, setLoad] = useState<LoadState>({ phase: "loading" });
  const [store, setStore] = useState<SupplierAssistantStore | null>(null);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [activeCode, setActiveCode] = useState<string | null>(null);
  const [panel, setPanel] = useState<PanelState>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [desktopSite, setDesktopSite] = useState(true);
  const [webCanGoBack, setWebCanGoBack] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [cameraMode, setCameraMode] = useState<CameraScanMode>("single");
  // 网页里改的设置（排名周期、去年同期窗口等）记在这里，重建网页时带回去。
  const pageSettingsRef = useRef<Record<string, unknown>>({});
  const webViewRef = useRef<WebView>(null);
  // 明文 http 跳转被改成 https 后要加载的地址；只对生成它的那个网页窗口（webKey）有效。
  const [upgradedSource, setUpgradedSource] = useState<Readonly<{ webKey: string; url: string }> | null>(null);
  const lastUpgradeRef = useRef<Readonly<{ url: string; at: number }> | null>(null);
  const panelSeqRef = useRef(0);

  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/workbench");
  }, [router]);

  const loadAll = useCallback(async () => {
    setLoad({ phase: "loading" });
    try {
      const [profilesResult, storesResult, remembered] = await Promise.all([
        request("/api/react/v1/browser-extension/supplier-profiles", { method: "GET" }),
        request("/api/react/v1/browser-extension/stores", { method: "GET" }),
        storageKey ? AppAsyncStorage.getString(storageKey).catch(() => null) : Promise.resolve(null),
      ]);
      if (profilesResult.httpStatus === 403 || storesResult.httpStatus === 403) {
        setLoad({ phase: "error", reason: "notAllowed" });
        return;
      }
      if (!profilesResult.success || !storesResult.success) {
        setLoad({ phase: "error", reason: "failed" });
        return;
      }
      const profiles = toProfileLites(profilesResult.data);
      const stores = toSupplierAssistantStores(storesResult.data);
      setLoad({ phase: "ready", profiles, rawProfiles: profilesResult.data, stores });
      setStore((current) =>
        resolveDefaultSupplierAssistantStore(stores, {
          rememberedStoreCode: current?.storeCode ?? remembered,
          primaryStoreCode,
        }),
      );
      setActiveCode((current) =>
        current && profiles.some((item) => item.supplierCode === current)
          ? current
          : profiles[0]?.supplierCode ?? null,
      );
    } catch {
      setLoad({ phase: "error", reason: "failed" });
    }
  }, [primaryStoreCode, request, storageKey]);

  useEffect(() => {
    void loadAll();
  }, [loadAll]);

  const selectStore = useCallback(
    (next: SupplierAssistantStore) => {
      setPanel(null);
      setStore(next);
      if (storageKey) void AppAsyncStorage.setString(storageKey, next.storeCode).catch(() => undefined);
    },
    [storageKey],
  );

  const activeProfile =
    load.phase === "ready"
      ? load.profiles.find((item) => item.supplierCode === activeCode) ?? null
      : null;
  const activeProfileRef = useRef<SupplierProfileLite | null>(null);
  activeProfileRef.current = activeProfile;
  const storeCodeRef = useRef<string | null>(null);
  storeCodeRef.current = store?.storeCode ?? null;

  const openPanel = useCallback(
    (locate: LocateRequest) => {
      const seq = ++panelSeqRef.current;
      const storeCode = storeCodeRef.current;
      if (!storeCode) return;
      setPanel({ request: locate, storeCode, phase: "loading" });
      void request("/api/react/v1/browser-extension/product-purchase-cycles", {
        method: "POST",
        body: { storeCode, supplierCode: locate.supplierCode, itemNumber: locate.itemNumber },
      })
        .then((result) => {
          // 连续点不同商品时只认最后一次，迟到的结果丢弃。
          if (seq !== panelSeqRef.current) return;
          setPanel(
            result.success
              ? { request: locate, storeCode, phase: "ready", data: (result.data ?? {}) as CyclesDto }
              : { request: locate, storeCode, phase: "failed" },
          );
        })
        .catch(() => {
          if (seq === panelSeqRef.current) setPanel({ request: locate, storeCode, phase: "failed" });
        });
    },
    [request],
  );

  const background = useMemo(
    () =>
      createSupplierAssistantBackground({
        request: (path, init) => request(path, init),
        getActiveProfile: () => activeProfileRef.current,
        getStoreCode: () => storeCodeRef.current,
        onLocate: openPanel,
      }),
    [openPanel, request],
  );

  const injectToPage = useCallback((code: string) => {
    webViewRef.current?.injectJavaScript(`${code};true;`);
  }, []);

  const onMessage = useCallback(
    (event: WebViewMessageEvent) => {
      let envelope: Record<string, unknown>;
      try {
        envelope = JSON.parse(event.nativeEvent.data) as Record<string, unknown>;
      } catch {
        return;
      }
      if (envelope.channel !== "hb-supplier-assistant") return;
      const senderUrl = event.nativeEvent.url ?? null;
      if (envelope.kind === "storage-set" && envelope.values && typeof envelope.values === "object") {
        // 只记插件自己的显示设置；分店、供应商配置由 App 决定，网页改不了。
        const { selectedStoreCode: _store, supplierProfiles: _profiles, ...rest } =
          envelope.values as Record<string, unknown>;
        pageSettingsRef.current = { ...pageSettingsRef.current, ...rest };
        return;
      }
      if (envelope.kind !== "request" || typeof envelope.id !== "string") return;
      const id = envelope.id;
      void background
        .handle(envelope.message, senderUrl)
        .catch((error: unknown) => ({ ok: false, error: String((error as Error)?.message ?? error) }))
        .then((response) => {
          injectToPage(
            `window.__hbSupplierAssistant&&window.__hbSupplierAssistant.resolve(${JSON.stringify(id)},${JSON.stringify(response)})`,
          );
        });
    },
    [background, injectToPage],
  );

  // 扫到的码（BLE 扫码枪 / 相机）交给网页里的插件脚本，走和插件一样的「条码 → 货号 → 高亮 / 搜索」流程。
  // 键盘模式扫码枪的按键本来就打进网页，插件脚本自己会识别，这里不再挂隐藏输入框抢焦点。
  const dispatchBarcode = useCallback(
    (barcode: string) => {
      injectToPage(
        `window.__hbSupplierAssistant&&window.__hbSupplierAssistant.dispatch(${JSON.stringify({ type: "BARCODE_SCANNED", barcode })})`,
      );
    },
    [injectToPage],
  );

  const homeUrl = activeProfile ? resolveProfileHomeUrl(activeProfile) : null;
  const webKey = `${activeCode}:${store?.storeCode ?? ""}:${reloadKey}:${desktopSite ? "d" : "m"}`;
  const sourceUrl = upgradedSource?.webKey === webKey ? upgradedSource.url : homeUrl;

  const onShouldStartLoadWithRequest = useCallback(
    (request: Readonly<{ url: string }>) => {
      const profile = activeProfileRef.current;
      if (!profile) return false;
      const decision = decideSupplierNavigation(profile, request.url);
      if (decision.action === "allow") return true;
      if (decision.action === "upgrade") {
        // 同一个 https 地址 5 秒内又被打回 http，说明网站自己在 http / https 之间来回跳，停下别死循环。
        const now = Date.now();
        const last = lastUpgradeRef.current;
        if (last && last.url === decision.url && now - last.at < 5_000) return false;
        lastUpgradeRef.current = { url: decision.url, at: now };
        setUpgradedSource({ webKey, url: decision.url });
      }
      return false;
    },
    [webKey],
  );

  const renderLoadError = useCallback(
    (_domain: string | undefined, code: number) => (
      <View style={styles.webError} testID="supplier-assistant-web-error">
        <Text style={styles.message}>
          {/* -1022：iOS ATS 拦下了明文 http（TXK 等整站 http 的供应商，需原生包开例外才能打开）。 */}
          {t(code === -1022 ? "state.insecureSite" : "state.pageFailed")}
        </Text>
      </View>
    ),
    [t],
  );
  const webReady = load.phase === "ready" && !!store && !!homeUrl;
  const webReadyRef = useRef(false);
  webReadyRef.current = webReady;

  // 页面在前台时持有 BLE 扫码租约：码只投给最上层的接收者，离开页面自动交还。
  useFocusEffect(
    useCallback(() => {
      const lease = acquireBleScanLease((value) => {
        if (!webReadyRef.current) return false;
        dispatchBarcode(value);
        return true;
      });
      return () => lease.release();
    }, [dispatchBarcode]),
  );

  // 安卓返回键：先关采购记录，再让网页后退，最后才离开页面。
  useFocusEffect(
    useCallback(() => {
      const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
        if (panel) {
          setPanel(null);
          return true;
        }
        if (webCanGoBack) {
          webViewRef.current?.goBack();
          return true;
        }
        return false;
      });
      return () => subscription.remove();
    }, [panel, webCanGoBack]),
  );

  const camera = useCameraScan({
    disabled: !cameraVisible,
    cooldownMs: 1200,
    suppressRepeatsUntilChange: cameraMode === "continuous",
    onBarcode: (barcode) => {
      if (cameraMode === "single") setCameraVisible(false);
      dispatchBarcode(barcode);
    },
  });

  const injectedJavaScript = useMemo(() => {
    if (load.phase !== "ready" || !store) return "";
    const initialStorage = {
      ...pageSettingsRef.current,
      supplierProfiles: load.rawProfiles,
      selectedStoreCode: store.storeCode,
      locale,
    };
    return `window.__hbSupplierAssistantInitialStorage=${JSON.stringify(initialStorage)};${SUPPLIER_ASSISTANT_CONTENT_SCRIPT};true;`;
    // reloadKey：回首页重建网页时，把网页里改过的显示设置（存在 ref 里）带进新页面。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [load, locale, reloadKey, store]);

  const onNavigationStateChange = useCallback((navigation: WebViewNavigation) => {
    setWebCanGoBack(navigation.canGoBack);
  }, []);

  const resetWeb = useCallback(() => {
    setPanel(null);
    setWebCanGoBack(false);
    setReloadKey((value) => value + 1);
  }, []);

  let body: ReactNode;
  if (load.phase === "loading") {
    body = (
      <View style={styles.centered} testID="supplier-assistant-loading">
        <ActivityIndicator />
        <Text style={styles.muted}>{t("state.loading")}</Text>
      </View>
    );
  } else if (load.phase === "error") {
    body = (
      <View style={styles.centered} testID="supplier-assistant-error">
        <Text style={styles.message}>{t(load.reason === "notAllowed" ? "state.notAllowed" : "state.failed")}</Text>
        {load.reason === "failed" ? (
          <Button mode="contained" icon="refresh" onPress={() => void loadAll()} testID="supplier-assistant-retry">
            {t("action.retry")}
          </Button>
        ) : null}
      </View>
    );
  } else if (load.profiles.length === 0) {
    body = (
      <View style={styles.centered} testID="supplier-assistant-empty">
        <Text style={styles.message}>{t("state.empty")}</Text>
      </View>
    );
  } else if (load.stores.length === 0) {
    body = (
      <View style={styles.centered} testID="supplier-assistant-no-stores">
        <Text style={styles.message}>{t("state.noStores")}</Text>
      </View>
    );
  } else if (!store) {
    body = (
      <View style={styles.centered} testID="supplier-assistant-pick-store">
        <Text style={styles.message}>{t("state.pickStore")}</Text>
        <Button mode="contained" icon="storefront-outline" onPress={() => setStorePickerVisible(true)}>
          {t("action.pickStore")}
        </Button>
      </View>
    );
  } else {
    body = (
      <View style={styles.workspace}>
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          contentContainerStyle={styles.tabs}
          style={styles.tabsScroll}
          testID="supplier-assistant-tabs"
        >
          {load.profiles.map((profile) => {
            const selected = profile.supplierCode === activeCode;
            return (
              <Chip
                key={profile.supplierCode}
                selected={selected}
                showSelectedCheck={false}
                mode={selected ? "flat" : "outlined"}
                onPress={() => {
                  setPanel(null);
                  setWebCanGoBack(false);
                  setActiveCode(profile.supplierCode);
                }}
                style={selected ? styles.tabSelected : styles.tab}
                testID={`supplier-assistant-tab-${profile.supplierCode}`}
              >
                {profile.displayName}
              </Chip>
            );
          })}
        </ScrollView>
        <View style={styles.toolbar}>
          <IconButton
            icon="arrow-left"
            size={20}
            disabled={!webCanGoBack}
            onPress={() => webViewRef.current?.goBack()}
            accessibilityLabel={t("action.back")}
            testID="supplier-assistant-web-back"
          />
          <IconButton
            icon="home-outline"
            size={20}
            disabled={!homeUrl}
            onPress={resetWeb}
            accessibilityLabel={t("action.home")}
            testID="supplier-assistant-home"
          />
          <IconButton
            icon="refresh"
            size={20}
            onPress={() => webViewRef.current?.reload()}
            accessibilityLabel={t("action.reload")}
            testID="supplier-assistant-reload"
          />
          <View style={styles.toolbarSpacer} />
          <Chip
            compact
            icon={desktopSite ? "monitor" : "cellphone"}
            selected={desktopSite}
            showSelectedCheck={false}
            onPress={() => {
              setDesktopSite((value) => !value);
              resetWeb();
            }}
            testID="supplier-assistant-desktop-toggle"
          >
            {t("action.desktopSite")}
          </Chip>
        </View>
        <View style={styles.webFrame}>
          {homeUrl ? (
            <WebView
              // 换供应商 / 换分店 / 回首页 / 切电脑版时整个重建，旧页面的脚本状态不会带过去。
              key={webKey}
              ref={webViewRef}
              source={{ uri: sourceUrl ?? homeUrl }}
              injectedJavaScript={injectedJavaScript}
              onMessage={onMessage}
              onNavigationStateChange={onNavigationStateChange}
              // http 也要进白名单：不在白名单里的地址会被系统交给 Safari；放行、改走 https 还是拦下见 decideSupplierNavigation。
              onShouldStartLoadWithRequest={onShouldStartLoadWithRequest}
              originWhitelist={["https://*", "http://*"]}
              renderError={renderLoadError}
              sharedCookiesEnabled
              // 供应商网站的 target=_blank 链接在同一个网页窗口里打开（手机上没有多标签）。
              setSupportMultipleWindows={false}
              {...(Platform.OS === "ios"
                ? { contentMode: desktopSite ? ("desktop" as const) : ("mobile" as const) }
                : desktopSite
                  ? { userAgent: ANDROID_DESKTOP_USER_AGENT }
                  : {})}
              testID="supplier-assistant-webview"
            />
          ) : (
            <View style={styles.centered}>
              <Text style={styles.message}>{t("state.unsupported")}</Text>
            </View>
          )}
        </View>
        {activeProfile?.hasSearch ? (
          <FAB
            icon="barcode-scan"
            label={t("action.scan")}
            style={styles.fab}
            onPress={() => setCameraVisible(true)}
            testID="supplier-assistant-scan"
          />
        ) : null}
      </View>
    );
  }

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]} testID="supplier-assistant-screen">
      <View style={styles.header}>
        <Button compact onPress={goBack} icon="chevron-left" testID="supplier-assistant-back">
          {t("action.back")}
        </Button>
        <Text variant="titleMedium" style={styles.headerTitle} numberOfLines={1}>
          {t("title")}
        </Text>
        {load.phase === "ready" && load.stores.length > 0 ? (
          <Button
            compact
            mode="outlined"
            icon="storefront-outline"
            onPress={() => setStorePickerVisible(true)}
            style={styles.storeButton}
            labelStyle={styles.storeButtonLabel}
            testID="supplier-assistant-store"
          >
            {store?.storeName ?? t("action.pickStore")}
          </Button>
        ) : null}
      </View>
      {body}
      {load.phase === "ready" ? (
        <StorePickerModal
          visible={storePickerVisible}
          presentation="sheet"
          stores={load.stores.map((item) => ({ storeCode: item.storeCode, storeName: item.storeName }))}
          selectedStoreCode={store?.storeCode ?? null}
          title={t("store.pickerTitle")}
          cancelLabel={t("store.cancel")}
          onDismiss={() => setStorePickerVisible(false)}
          onSelectStore={(picked) => {
            setStorePickerVisible(false);
            const next = picked && load.stores.find((item) => item.storeCode === picked.storeCode);
            if (next) selectStore(next);
          }}
        />
      ) : null}
      <CameraScanSheet
        visible={cameraVisible}
        title={t("scan.title")}
        mode={cameraMode}
        onModeChange={setCameraMode}
        onDismiss={() => setCameraVisible(false)}
        libraryPicker={camera.libraryPicker}
      >
        {camera.permission?.granted ? (
          <CameraView style={styles.camera} {...camera.cameraProps} />
        ) : (
          <View style={styles.permission}>
            <Button mode="contained" onPress={() => void camera.requestPermission()}>
              {t("action.enableCamera")}
            </Button>
          </View>
        )}
      </CameraScanSheet>
      <BusinessSheet
        visible={panel !== null}
        title={t("panel.title")}
        subtitle={panel ? t("panel.item", { itemNumber: panel.request.itemNumber, storeCode: panel.storeCode }) : undefined}
        onDismiss={() => setPanel(null)}
      >
        {panel ? <PurchasePanelContent panel={panel} t={t} /> : null}
      </BusinessSheet>
    </SafeAreaView>
  );
}

function PurchasePanelContent({
  panel,
  t,
}: Readonly<{
  panel: NonNullable<PanelState>;
  t: (key: SupplierAssistantCopyKey, values?: Readonly<Record<string, string | number>>) => string;
}>) {
  const none = t("panel.none");
  if (panel.phase === "loading") return <Text style={styles.muted}>{t("panel.loading")}</Text>;
  if (panel.phase === "failed") return <Text style={styles.message}>{t("panel.failed")}</Text>;
  const data = panel.data;
  if (data.matchStatus && data.matchStatus !== "matched") {
    return <Text style={styles.message}>{t("panel.noMatch")}</Text>;
  }
  const cycles = data.cycles ?? [];
  return (
    <View style={styles.panelContent} testID="supplier-assistant-panel">
      {data.productName ? <Text style={styles.productName}>{data.productName}</Text> : null}
      <Row
        label={t("panel.latestPurchase")}
        value={data.latestPurchaseDate ? `${data.latestPurchaseDate} × ${data.latestPurchaseQuantity ?? 0}` : none}
      />
      <Row label={t("panel.salesSince")} value={String(data.salesSinceLatestPurchase ?? 0)} />
      <Row
        label={t("panel.lastYear30")}
        value={data.lastYearSalesQuantity30Days == null ? none : String(data.lastYearSalesQuantity30Days)}
      />
      <Row
        label={t("panel.lastYear60")}
        value={data.lastYearSalesQuantity60Days == null ? none : String(data.lastYearSalesQuantity60Days)}
      />
      <Text style={styles.sectionTitle}>{t("panel.cycles")}</Text>
      {cycles.length === 0 ? (
        <Text style={styles.muted}>{t("panel.noCycles")}</Text>
      ) : (
        cycles.map((cycle, index) => (
          <View key={`${cycle.purchaseDate}-${index}`} style={styles.cycle}>
            <Text style={styles.cycleOrdered}>
              {t("panel.cycleOrdered", { date: cycle.purchaseDate ?? none, quantity: cycle.purchaseQuantity ?? 0 })}
            </Text>
            <Text style={styles.muted}>
              {t("panel.cycleSold", { date: cycle.salesEndDate ?? none, quantity: cycle.salesQuantity ?? 0 })}
            </Text>
          </View>
        ))
      )}
    </View>
  );
}

function Row({ label, value }: Readonly<{ label: string; value: string }>) {
  return (
    <View style={styles.row}>
      <Text style={styles.muted}>{label}</Text>
      <Text style={styles.rowValue}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.xxs,
    minHeight: 52,
    paddingHorizontal: HB_SPACING.xs,
  },
  headerTitle: { flex: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  storeButton: { maxWidth: "46%" },
  storeButtonLabel: { fontSize: 13, marginHorizontal: 10 },
  workspace: { flex: 1, minHeight: 0 },
  tabsScroll: { flexGrow: 0 },
  tabs: { gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.sm, paddingVertical: HB_SPACING.xxs },
  tab: { backgroundColor: HB_COLORS.surface },
  tabSelected: { backgroundColor: HB_COLORS.surfaceMuted },
  toolbar: { alignItems: "center", flexDirection: "row", paddingHorizontal: HB_SPACING.xxs, paddingRight: HB_SPACING.sm },
  toolbarSpacer: { flex: 1 },
  webFrame: {
    backgroundColor: HB_COLORS.white,
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flex: 1,
    overflow: "hidden",
  },
  fab: { bottom: HB_SPACING.md, position: "absolute", right: HB_SPACING.md },
  centered: { alignItems: "center", flex: 1, gap: HB_SPACING.sm, justifyContent: "center", padding: HB_SPACING.lg },
  message: { color: HB_COLORS.textPrimary, fontSize: 16, fontWeight: "600", textAlign: "center" },
  webError: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "center",
    backgroundColor: HB_COLORS.surface,
    justifyContent: "center",
    padding: HB_SPACING.lg,
  },
  muted: { color: HB_COLORS.textSecondary, fontSize: 13 },
  panelContent: { gap: HB_SPACING.xs },
  productName: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700" },
  row: { flexDirection: "row", gap: HB_SPACING.xs, justifyContent: "space-between" },
  rowValue: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700" },
  sectionTitle: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "800", marginTop: HB_SPACING.xs },
  cycle: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: 2,
    paddingTop: HB_SPACING.xs,
  },
  cycleOrdered: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "700" },
  camera: { borderRadius: HB_RADIUS.surface, height: 280, overflow: "hidden" },
  permission: { alignItems: "center", height: 160, justifyContent: "center" },
});
