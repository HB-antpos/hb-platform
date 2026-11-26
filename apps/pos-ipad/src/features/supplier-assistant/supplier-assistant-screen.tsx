import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, Text, View } from "react-native";
import { WebView, type WebViewMessageEvent } from "react-native-webview";

import {
  SUPPLIER_ASSISTANT_CONTENT_SCRIPT,
  SUPPLIER_ASSISTANT_EXTENSION_VERSION,
} from "./generated/supplier-assistant-content.generated";
import {
  createSupplierAssistantBackground,
  resolveProfileHomeUrl,
  toProfileLites,
  type LocateRequest,
  type SupplierProfileLite,
} from "./supplier-assistant-background";
import {
  resolveSupplierAssistantLocale,
  supplierAssistantText,
  type SupplierAssistantCopyKey,
} from "./supplier-assistant-copy";
import {
  SupplierAssistantSession,
  type FetchLike,
  type SupplierAssistantTokenSource,
} from "./supplier-assistant-session";

import { HbposApiError } from "@/core/api/hbpos-api";
import { PosButton } from "@/ui/controls/pos-button";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { createPosStyles, posRadius, usePosThemeVersion } from "@/ui/theme";

export type SupplierAssistantScreenProps = Readonly<{
  tokenSource: SupplierAssistantTokenSource;
  onBack(): void;
  /** 测试注入；默认用全局 fetch（App 原生请求，不受浏览器跨域限制）。 */
  fetchImpl?: FetchLike;
  /** 页面订阅扫码后把扫到的码交给这里（BLE 扫码枪）。 */
  registerScanHandler?: (handler: ((barcode: string) => void) | null) => void;
}>;

type LoadState =
  | Readonly<{ phase: "loading" }>
  | Readonly<{ phase: "error"; reason: "tokenFailed" | "notEligible" }>
  | Readonly<{
      phase: "ready";
      storeCode: string;
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

const defaultFetch: FetchLike = (input, init) => fetch(input, init);

/**
 * 大屏收银 App 内「订货助手」：在网页窗口里打开供应商网站，注入浏览器订货助手插件的页面脚本，
 * 商品卡片上直接显示本店上次订货、至今销量；点按钮或扫码定位商品时在右侧显示采购记录。
 *
 * 中文注释：设备令牌只在这一层（App 原生层）持有并代发请求，网页里拿不到。
 */
export function SupplierAssistantScreen({
  fetchImpl = defaultFetch,
  onBack,
  registerScanHandler,
  tokenSource,
}: SupplierAssistantScreenProps) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveSupplierAssistantLocale(i18n.resolvedLanguage);
  const t = useCallback(
    (key: SupplierAssistantCopyKey, values?: Readonly<Record<string, string | number>>) =>
      supplierAssistantText(locale, key, values),
    [locale],
  );

  const session = useMemo(
    () => new SupplierAssistantSession(tokenSource, fetchImpl, SUPPLIER_ASSISTANT_EXTENSION_VERSION),
    [fetchImpl, tokenSource],
  );
  const [load, setLoad] = useState<LoadState>({ phase: "loading" });
  const [activeCode, setActiveCode] = useState<string | null>(null);
  const [panel, setPanel] = useState<PanelState>(null);
  const [reloadKey, setReloadKey] = useState(0);
  // 网页里改的设置（排名周期、去年同期窗口等）记在这里，下次打开页面带回去。
  const pageSettingsRef = useRef<Record<string, unknown>>({});
  const webViewRef = useRef<WebView>(null);
  const panelSeqRef = useRef(0);

  const loadAll = useCallback(async () => {
    setLoad({ phase: "loading" });
    try {
      const token = await session.getToken();
      const result = await session.request("/api/react/v1/browser-extension/supplier-profiles", {
        method: "GET",
      });
      if (!result.success) throw new Error(result.errorCode ?? "PROFILES_FAILED");
      const profiles = toProfileLites(result.data);
      setLoad({ phase: "ready", storeCode: token.storeCode, profiles, rawProfiles: result.data });
      setActiveCode((current) =>
        current && profiles.some((item) => item.supplierCode === current)
          ? current
          : profiles[0]?.supplierCode ?? null,
      );
    } catch (error) {
      const notEligible =
        error instanceof HbposApiError
        && (error.code === "SUPPLIER_ASSISTANT_DEVICE_NOT_ELIGIBLE"
          || error.code === "SUPPLIER_ASSISTANT_DEVICE_SYSTEM_NOT_SUPPORTED");
      setLoad({ phase: "error", reason: notEligible ? "notEligible" : "tokenFailed" });
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
  storeCodeRef.current = load.phase === "ready" ? load.storeCode : null;

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
  const injectedJavaScript = useMemo(() => {
    if (load.phase !== "ready") return "";
    const initialStorage = {
      ...pageSettingsRef.current,
      supplierProfiles: load.rawProfiles,
      selectedStoreCode: load.storeCode,
      locale,
    };
    return `window.__hbSupplierAssistantInitialStorage=${JSON.stringify(initialStorage)};${SUPPLIER_ASSISTANT_CONTENT_SCRIPT};true;`;
    // reloadKey：回首页重建网页时，把网页里改过的显示设置（存在 ref 里）带进新页面。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [load, locale, reloadKey]);

  const header = (
    <PosPageHeader
      actions={
        <>
          <View style={styles.storePill} testID="supplier-assistant-store">
            <Text style={styles.storePillText}>
              {load.phase === "ready" ? t("store.label", { storeCode: load.storeCode }) : t("store.loading")}
            </Text>
          </View>
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
            disabled={load.phase !== "ready"}
            icon="refresh"
            label={t("action.reload")}
            onPress={() => webViewRef.current?.reload()}
            size="medium"
            testID="supplier-assistant-reload"
            tone="quiet"
          />
        </>
      }
      backLabel={t("action.back")}
      backTestID="supplier-assistant-back"
      eyebrow={t("eyebrow")}
      onBack={onBack}
      subtitle={t("subtitle")}
      title={t("title")}
    />
  );

  return (
    <PosScreenFrame active="supplier-assistant" header={header} testID="supplier-assistant-screen">
      {load.phase === "loading" ? (
        <View style={styles.centered} testID="supplier-assistant-loading">
          <ActivityIndicator />
          <Text style={styles.muted}>{t("state.loading")}</Text>
        </View>
      ) : load.phase === "error" ? (
        <View style={styles.centered} testID="supplier-assistant-error">
          <Text style={styles.message}>{t(load.reason === "notEligible" ? "state.notEligible" : "state.tokenFailed")}</Text>
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
          </View>
          <View style={styles.body}>
            <View style={styles.webFrame}>
              {homeUrl ? (
                <WebView
                  // 换供应商或回首页时整个重建，旧页面的脚本状态不会带过去。
                  key={`${activeCode}:${reloadKey}`}
                  injectedJavaScript={injectedJavaScript}
                  onMessage={onMessage}
                  originWhitelist={["https://*"]}
                  ref={webViewRef}
                  sharedCookiesEnabled
                  source={{ uri: homeUrl }}
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
    </PosScreenFrame>
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
  muted: { color: colors.mutedInk, fontSize: 13 },
  storePill: {
    backgroundColor: colors.surfaceAlt,
    borderRadius: posRadius.large,
    justifyContent: "center",
    minHeight: 40,
    paddingHorizontal: 14,
  },
  storePillText: { color: colors.ink, fontSize: 14, fontWeight: "700" },
}));
