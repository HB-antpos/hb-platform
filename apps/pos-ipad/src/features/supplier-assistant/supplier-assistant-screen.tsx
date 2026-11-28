import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, Text, View } from "react-native";
import { WebView, type WebViewMessageEvent } from "react-native-webview";

import { SUPPLIER_ASSISTANT_CONTENT_SCRIPT } from "./generated/supplier-assistant-content.generated";
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
  SupplierAssistantTokenError,
  type SupplierAssistantSession,
} from "./supplier-assistant-session";

import {
  ORDERING_ACTIVITY_CHANNEL,
  ORDERING_ACTIVITY_SCRIPT,
} from "@/features/ordering/ordering-activity";
import { PosButton } from "@/ui/controls/pos-button";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { createPosStyles, posRadius, usePosThemeVersion } from "@/ui/theme";

export type SupplierAssistantWorkspaceProps = Readonly<{
  /** 订货工作台建好的会话（工作台也用它查分店列表），令牌只在这里面。 */
  session: SupplierAssistantSession;
  /** 订货工作台选定的分店；摘要、采购记录都按这家分店查。 */
  storeCode: string;
  /** 页面订阅扫码后把扫到的码交给这里（BLE 扫码枪）。 */
  registerScanHandler?: (handler: ((barcode: string) => void) | null) => void;
  /** 员工在供应商网页里有操作（点按、输入）时通知订货工作台，用于闲置自动退出计时。 */
  onActivity?: () => void;
}>;

type LoadState =
  | Readonly<{ phase: "loading" }>
  | Readonly<{ phase: "error"; reason: "tokenFailed" | "notAllowed" | "sessionExpired" }>
  | Readonly<{
      phase: "ready";
      profiles: readonly SupplierProfileLite[];
      rawProfiles: unknown;
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
  | Readonly<{ request: LocateRequest; phase: "loading" }>
  | Readonly<{ request: LocateRequest; phase: "failed" }>
  | Readonly<{ request: LocateRequest; phase: "ready"; data: CyclesDto }>;

/**
 * 订货工作台里的「供应商订货助手」页签：在网页窗口里打开供应商网站，注入浏览器订货助手插件的页面脚本，
 * 商品卡片上直接显示所选分店上次订货、至今销量；点按钮或扫码定位商品时在右侧显示采购记录。
 *
 * 中文注释：员工插件令牌只在这一层（App 原生层）持有并代发请求，供应商网页里拿不到。
 */
export function SupplierAssistantWorkspace({
  onActivity,
  registerScanHandler,
  session,
  storeCode,
}: SupplierAssistantWorkspaceProps) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveSupplierAssistantLocale(i18n.resolvedLanguage);
  const t = useCallback(
    (key: SupplierAssistantCopyKey, values?: Readonly<Record<string, string | number>>) =>
      supplierAssistantText(locale, key, values),
    [locale],
  );

  const [load, setLoad] = useState<LoadState>({ phase: "loading" });
  const [activeCode, setActiveCode] = useState<string | null>(null);
  const [panel, setPanel] = useState<PanelState>(null);
  const [reloadKey, setReloadKey] = useState(0);
  // 网页里改的设置（排名周期、去年同期窗口等）记在这里，下次打开页面带回去。
  const pageSettingsRef = useRef<Record<string, unknown>>({});
  const webViewRef = useRef<WebView>(null);
  // 明文 http 跳转被改成 https 后要加载的地址；只对生成它的那个网页窗口（webKey）有效。
  const [upgradedSource, setUpgradedSource] = useState<Readonly<{ webKey: string; url: string }> | null>(null);
  const lastUpgradeRef = useRef<Readonly<{ url: string; at: number }> | null>(null);
  const panelSeqRef = useRef(0);

  const loadAll = useCallback(async () => {
    setLoad({ phase: "loading" });
    try {
      const result = await session.request("/api/react/v1/browser-extension/supplier-profiles", {
        method: "GET",
      });
      if (result.httpStatus === 403) {
        setLoad({ phase: "error", reason: "notAllowed" });
        return;
      }
      if (!result.success) throw new Error(result.errorCode ?? "PROFILES_FAILED");
      const profiles = toProfileLites(result.data);
      setLoad({ phase: "ready", profiles, rawProfiles: result.data });
      setActiveCode((current) =>
        current && profiles.some((item) => item.supplierCode === current)
          ? current
          : profiles[0]?.supplierCode ?? null,
      );
    } catch (error) {
      // 网页会话已失效（被踢下线 / 过期）时，重试也没用，提示员工退出重登。
      const expired = error instanceof SupplierAssistantTokenError && error.code === "SESSION_EXPIRED";
      setLoad({ phase: "error", reason: expired ? "sessionExpired" : "tokenFailed" });
    }
  }, [session]);

  useEffect(() => {
    void loadAll();
  }, [loadAll]);

  const activeProfile =
    load.phase === "ready"
      ? load.profiles.find((item) => item.supplierCode === activeCode) ?? null
      : null;
  const activeProfileRef = useRef<SupplierProfileLite | null>(null);
  activeProfileRef.current = activeProfile;
  const storeCodeRef = useRef<string | null>(null);
  storeCodeRef.current = storeCode;

  const openPanel = useCallback(
    (request: LocateRequest) => {
      const seq = ++panelSeqRef.current;
      setPanel({ request, phase: "loading" });
      const storeCode = storeCodeRef.current;
      if (!storeCode) {
        setPanel({ request, phase: "failed" });
        return;
      }
      void session
        .request("/api/react/v1/browser-extension/product-purchase-cycles", {
          method: "POST",
          body: { storeCode, supplierCode: request.supplierCode, itemNumber: request.itemNumber },
        })
        .then((result) => {
          // 连续点不同商品时只认最后一次，迟到的结果丢弃。
          if (seq !== panelSeqRef.current) return;
          setPanel(
            result.success
              ? { request, phase: "ready", data: (result.data ?? {}) as CyclesDto }
              : { request, phase: "failed" },
          );
        })
        .catch(() => {
          if (seq === panelSeqRef.current) setPanel({ request, phase: "failed" });
        });
    },
    [session],
  );

  const background = useMemo(
    () =>
      createSupplierAssistantBackground({
        request: (path, init) => session.request(path, init),
        getActiveProfile: () => activeProfileRef.current,
        getStoreCode: () => storeCodeRef.current,
        onLocate: openPanel,
      }),
    [openPanel, session],
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
      if (envelope.channel === ORDERING_ACTIVITY_CHANNEL) {
        onActivity?.();
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
    [background, injectToPage, onActivity],
  );

  // BLE 扫码枪：扫到的码直接交给网页里的插件脚本，走和插件一样的「条码 → 货号 → 高亮 / 搜索」流程。
  // 键盘模式扫码枪本来就会把按键打进网页，插件脚本自己会识别。
  useEffect(() => {
    if (!registerScanHandler) return undefined;
    registerScanHandler((barcode) => {
      injectToPage(
        `window.__hbSupplierAssistant&&window.__hbSupplierAssistant.dispatch(${JSON.stringify({ type: "BARCODE_SCANNED", barcode })})`,
      );
    });
    return () => registerScanHandler(null);
  }, [injectToPage, registerScanHandler]);

  const homeUrl = activeProfile ? resolveProfileHomeUrl(activeProfile) : null;
  const webKey = `${activeCode}:${storeCode}:${reloadKey}`;
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
  const injectedJavaScript = useMemo(() => {
    if (load.phase !== "ready") return "";
    const initialStorage = {
      ...pageSettingsRef.current,
      supplierProfiles: load.rawProfiles,
      selectedStoreCode: storeCode,
      locale,
    };
    return `${ORDERING_ACTIVITY_SCRIPT};window.__hbSupplierAssistantInitialStorage=${JSON.stringify(initialStorage)};${SUPPLIER_ASSISTANT_CONTENT_SCRIPT};true;`;
    // reloadKey：回首页重建网页时，把网页里改过的显示设置（存在 ref 里）带进新页面。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [load, locale, reloadKey, storeCode]);

  return (
    <View style={styles.root} testID="supplier-assistant-screen">
      {load.phase === "loading" ? (
        <View style={styles.centered} testID="supplier-assistant-loading">
          <ActivityIndicator />
          <Text style={styles.muted}>{t("state.loading")}</Text>
        </View>
      ) : load.phase === "error" ? (
        <View style={styles.centered} testID="supplier-assistant-error">
          <Text style={styles.message}>{t(`state.${load.reason}`)}</Text>
          {load.reason === "tokenFailed" ? (
            <PosButton
              icon="refresh"
              label={t("action.retry")}
              onPress={() => void loadAll()}
              size="medium"
              testID="supplier-assistant-retry"
            />
          ) : null}
        </View>
      ) : load.profiles.length === 0 ? (
        <View style={styles.centered} testID="supplier-assistant-empty">
          <Text style={styles.message}>{t("state.empty")}</Text>
        </View>
      ) : (
        <View style={styles.workspace}>
          <View accessibilityRole="tablist" style={styles.tabs} testID="supplier-assistant-tabs">
            {load.profiles.map((profile) => {
              const selected = profile.supplierCode === activeCode;
              return (
                <PosPressable
                  accessibilityRole="tab"
                  accessibilityState={{ selected }}
                  key={profile.supplierCode}
                  onPress={() => {
                    setPanel(null);
                    setActiveCode(profile.supplierCode);
                  }}
                  sound="navigate"
                  style={[styles.tab, selected && styles.tabSelected]}
                  testID={`supplier-assistant-tab-${profile.supplierCode}`}
                >
                  <Text style={[styles.tabLabel, selected && styles.tabLabelSelected]}>{profile.displayName}</Text>
                </PosPressable>
              );
            })}
            <Text style={styles.scanHint}>{activeProfile?.hasSearch ? t("scan.hint") : ""}</Text>
            <PosButton
              disabled={!homeUrl}
              icon="home-outline"
              label={t("action.home")}
              onPress={() => {
                setPanel(null);
                setReloadKey((value) => value + 1);
              }}
              size="medium"
              testID="supplier-assistant-home"
              tone="quiet"
            />
            <PosButton
              disabled={!homeUrl}
              icon="refresh"
              label={t("action.reload")}
              onPress={() => webViewRef.current?.reload()}
              size="medium"
              testID="supplier-assistant-reload"
              tone="quiet"
            />
          </View>
          <View style={styles.body}>
            <View style={styles.webFrame}>
              {homeUrl ? (
                <WebView
                  // 换供应商、换分店或回首页时整个重建，旧页面的脚本状态不会带过去。
                  key={webKey}
                  injectedJavaScript={injectedJavaScript}
                  onMessage={onMessage}
                  // http 也要进白名单：不在白名单里的地址会被系统交给 Safari；放行、改走 https 还是拦下见 decideSupplierNavigation。
                  onShouldStartLoadWithRequest={onShouldStartLoadWithRequest}
                  originWhitelist={["https://*", "http://*"]}
                  ref={webViewRef}
                  renderError={renderLoadError}
                  sharedCookiesEnabled
                  source={{ uri: sourceUrl ?? homeUrl }}
                  testID="supplier-assistant-webview"
                />
              ) : (
                <View style={styles.centered}>
                  <Text style={styles.message}>{t("state.unsupported")}</Text>
                </View>
              )}
            </View>
            {panel ? <PurchasePanel onClose={() => setPanel(null)} panel={panel} t={t} /> : null}
          </View>
        </View>
      )}
    </View>
  );
}

function PurchasePanel({
  onClose,
  panel,
  t,
}: Readonly<{
  panel: NonNullable<PanelState>;
  onClose(): void;
  t: (key: SupplierAssistantCopyKey, values?: Readonly<Record<string, string | number>>) => string;
}>) {
  const none = t("panel.none");
  const data = panel.phase === "ready" ? panel.data : null;
  const unmatched = data && data.matchStatus && data.matchStatus !== "matched";
  return (
    <View style={styles.panel} testID="supplier-assistant-panel">
      <View style={styles.panelHeader}>
        <View style={styles.panelHeading}>
          <Text style={styles.panelTitle}>{t("panel.title")}</Text>
          <Text style={styles.muted}>{t("panel.item", { itemNumber: panel.request.itemNumber })}</Text>
        </View>
        <PosButton
          icon="close"
          label={t("action.closePanel")}
          onPress={onClose}
          size="small"
          testID="supplier-assistant-panel-close"
          tone="quiet"
        />
      </View>
      {panel.phase === "loading" ? (
        <Text style={styles.muted}>{t("panel.loading")}</Text>
      ) : panel.phase === "failed" ? (
        <Text style={styles.message}>{t("panel.failed")}</Text>
      ) : unmatched ? (
        <Text style={styles.message}>{t("panel.noMatch")}</Text>
      ) : (
        <ScrollView contentContainerStyle={styles.panelContent}>
          {data?.productName ? <Text style={styles.productName}>{data.productName}</Text> : null}
          <Row label={t("panel.latestPurchase")} value={
            data?.latestPurchaseDate ? `${data.latestPurchaseDate} × ${data.latestPurchaseQuantity ?? 0}` : none
          } />
          <Row label={t("panel.salesSince")} value={String(data?.salesSinceLatestPurchase ?? 0)} />
          <Row label={t("panel.lastYear30")} value={data?.lastYearSalesQuantity30Days == null ? none : String(data.lastYearSalesQuantity30Days)} />
          <Row label={t("panel.lastYear60")} value={data?.lastYearSalesQuantity60Days == null ? none : String(data.lastYearSalesQuantity60Days)} />
          <Text style={styles.sectionTitle}>{t("panel.cycles")}</Text>
          {(data?.cycles ?? []).length === 0 ? (
            <Text style={styles.muted}>{t("panel.noCycles")}</Text>
          ) : (
            (data?.cycles ?? []).map((cycle, index) => (
              <View key={`${cycle.purchaseDate}-${index}`} style={styles.cycle} testID="supplier-assistant-cycle">
                <Text style={styles.cycleOrdered}>
                  {t("panel.cycleOrdered", { date: cycle.purchaseDate ?? none, quantity: cycle.purchaseQuantity ?? 0 })}
                </Text>
                <Text style={styles.muted}>
                  {t("panel.cycleSold", { date: cycle.salesEndDate ?? none, quantity: cycle.salesQuantity ?? 0 })}
                </Text>
              </View>
            ))
          )}
        </ScrollView>
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

const styles = createPosStyles((colors) => ({
  root: { flex: 1, minHeight: 0 },
  workspace: { flex: 1, gap: 8, minHeight: 0, padding: 12 },
  tabs: { alignItems: "center", flexDirection: "row", flexWrap: "wrap", gap: 8 },
  tab: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.large,
    borderWidth: 1,
    minHeight: 48,
    justifyContent: "center",
    paddingHorizontal: 18,
  },
  tabSelected: { backgroundColor: colors.accentSoft, borderColor: colors.accent },
  tabLabel: { color: colors.ink, fontSize: 16, fontWeight: "700" },
  tabLabelSelected: { color: colors.accent },
  scanHint: { color: colors.mutedInk, flex: 1, fontSize: 13, textAlign: "right" },
  body: { flex: 1, flexDirection: "row", gap: 12, minHeight: 0 },
  webFrame: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flex: 1,
    minWidth: 0,
    overflow: "hidden",
  },
  panel: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 10,
    padding: 16,
    width: 360,
  },
  panelHeader: { alignItems: "flex-start", flexDirection: "row", gap: 8 },
  panelHeading: { flex: 1, gap: 2 },
  panelTitle: { color: colors.ink, fontSize: 18, fontWeight: "800" },
  panelContent: { gap: 8, paddingBottom: 12 },
  productName: { color: colors.ink, fontSize: 15, fontWeight: "700" },
  row: { flexDirection: "row", justifyContent: "space-between", gap: 8 },
  rowValue: { color: colors.ink, fontSize: 15, fontWeight: "700" },
  sectionTitle: { color: colors.ink, fontSize: 14, fontWeight: "800", marginTop: 8 },
  cycle: { borderTopColor: colors.border, borderTopWidth: 1, gap: 2, paddingTop: 8 },
  cycleOrdered: { color: colors.ink, fontSize: 14, fontWeight: "700" },
  centered: { alignItems: "center", flex: 1, gap: 12, justifyContent: "center", padding: 24 },
  message: { color: colors.ink, fontSize: 16, fontWeight: "600", textAlign: "center" },
  webError: {
    alignItems: "center",
    backgroundColor: colors.surface,
    bottom: 0,
    justifyContent: "center",
    left: 0,
    padding: 24,
    position: "absolute",
    right: 0,
    top: 0,
  },
  muted: { color: colors.mutedInk, fontSize: 13 },
}));
