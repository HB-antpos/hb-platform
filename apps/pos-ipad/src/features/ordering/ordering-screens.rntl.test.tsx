import { describe, expect, it, jest } from "@jest/globals";
import { act, fireEvent, render, screen } from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import { OrderingAuthError, type OrderingUser } from "./ordering-auth";
import { OrderingLoginScreen } from "./ordering-login-screen";
import type { OrderingCrypto } from "./ordering-token-source";
import { OrderingWorkbenchScreen, type OrderingSignOutReason } from "./ordering-workbench-screen";
import { PosEntryScreen } from "./pos-entry-screen";

import type { FetchLike } from "@/features/supplier-assistant/supplier-assistant-session";

jest.mock("expo-status-bar", () => ({ StatusBar: () => null }));

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: jest.fn() }),
}));

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string) => key,
  }),
}));

// 网页窗口换成假组件：按 testID 记下各自的 props，并收集注入的脚本。
const mockWebViews: Record<string, Record<string, unknown>> = {};
const mockInjected: Record<string, string[]> = {};
jest.mock("react-native-webview", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { View } = jest.requireActual<typeof import("react-native")>("react-native");
  const FakeWebView = React.forwardRef((props: Record<string, unknown>, ref) => {
    const id = props.testID as string;
    mockWebViews[id] = props;
    React.useImperativeHandle(ref, () => ({
      injectJavaScript: (code: string) => {
        (mockInjected[id] ??= []).push(code);
      },
      reload: () => undefined,
    }));
    return React.createElement(View, { testID: id });
  });
  FakeWebView.displayName = "FakeWebView";
  return { WebView: FakeWebView };
});

const USER: OrderingUser = { userGuid: "G1", username: "sean", displayName: "Sean Pan" };

async function settle() {
  for (let index = 0; index < 6; index += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 5));
    });
  }
}

describe("PosEntryScreen", () => {
  it("未注册：收银卡片引导注册，订货卡片引导员工登录", async () => {
    const onCashier = jest.fn();
    const onOrdering = jest.fn();
    await render(
      <PosEntryScreen language="zh" onCashier={onCashier} onOrdering={onOrdering} registered={false} />,
    );
    expect(screen.getByText("这台 iPad 要用来做什么？")).toBeTruthy();
    expect(screen.getByText("用设备注册码注册")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("pos-entry-cashier"));
    await fireEvent.press(screen.getByTestId("pos-entry-ordering"));
    expect(onCashier).toHaveBeenCalledTimes(1);
    expect(onOrdering).toHaveBeenCalledTimes(1);
  });

  it("已注册：收银卡片是收银员登录", async () => {
    await render(<PosEntryScreen language="zh" onCashier={jest.fn()} onOrdering={jest.fn()} registered />);
    expect(screen.getByText("选择要做的事")).toBeTruthy();
    expect(screen.getByText("收银员登录")).toBeTruthy();
    expect(screen.getByText("员工账号登录")).toBeTruthy();
  });
});

describe("OrderingLoginScreen", () => {
  async function renderLogin(signIn: (credentials: { username: string; password: string }) => Promise<OrderingUser>) {
    const onSignedIn = jest.fn();
    await render(
      <OrderingLoginScreen language="zh" onBack={jest.fn()} onSignedIn={onSignedIn} signIn={signIn} />,
    );
    return onSignedIn;
  }

  it("没填账号密码时不发请求", async () => {
    const signIn = jest.fn(async () => USER);
    await renderLogin(signIn);
    await fireEvent.press(screen.getByTestId("ordering-login-submit"));
    expect(screen.getByText("请输入账号和密码。")).toBeTruthy();
    expect(signIn).not.toHaveBeenCalled();
  });

  it("登录成功把员工交给路由", async () => {
    const signIn = jest.fn(async (_credentials: { username: string; password: string }) => USER);
    const onSignedIn = await renderLogin(signIn);
    await fireEvent.changeText(screen.getByTestId("ordering-login-username"), " sean ");
    await fireEvent.changeText(screen.getByTestId("ordering-login-password"), "pw");
    await fireEvent.press(screen.getByTestId("ordering-login-submit"));
    await settle();
    expect(signIn).toHaveBeenCalledWith({ username: "sean", password: "pw" });
    expect(onSignedIn).toHaveBeenCalledWith(USER);
  });

  it("账号问题显示后端原话；没有订货权限给专门提示", async () => {
    const signIn = jest
      .fn<(credentials: { username: string; password: string }) => Promise<OrderingUser>>()
      .mockRejectedValueOnce(new OrderingAuthError("INVALID_CREDENTIALS", "账号已停用"))
      .mockRejectedValueOnce(new OrderingAuthError("NOT_ALLOWED"));
    await renderLogin(signIn);
    await fireEvent.changeText(screen.getByTestId("ordering-login-username"), "a");
    await fireEvent.changeText(screen.getByTestId("ordering-login-password"), "b");
    await fireEvent.press(screen.getByTestId("ordering-login-submit"));
    await settle();
    expect(screen.getByText("账号已停用")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("ordering-login-submit"));
    await settle();
    expect(screen.getByText("这个账号没有订货权限，请联系店长开通。")).toBeTruthy();
  });
});

describe("OrderingWorkbenchScreen", () => {
  const fakeCrypto: OrderingCrypto = {
    randomBytes: (length) => new Uint8Array(length).fill(7),
    sha256Base64: async () => "Y2hhbGxlbmdl",
  };

  async function renderWorkbench(
    options: Partial<{ fetchImpl: FetchLike; deviceStoreCode: string | null }> = {},
  ) {
    for (const key of Object.keys(mockWebViews)) delete mockWebViews[key];
    for (const key of Object.keys(mockInjected)) delete mockInjected[key];
    const signOut = jest.fn(async (_reason: OrderingSignOutReason, _bridge: unknown) => undefined);
    let scan: ((barcode: string) => void) | null = null;
    await render(
      <OrderingWorkbenchScreen
        crypto={fakeCrypto}
        deviceStoreCode={options.deviceStoreCode ?? null}
        fetchImpl={options.fetchImpl ?? (async () => ({ status: 500, ok: false, json: async () => ({}) }))}
        language="zh"
        registerScanHandler={(handler) => {
          scan = handler;
        }}
        signOut={signOut}
        user={USER}
      />,
    );
    return { signOut, scan: (barcode: string) => scan?.(barcode) };
  }

  const shop = () => mockWebViews["ordering-shop-webview"]!;

  it("默认打开仓库订货：嵌入标记、只认 hotbargain.vip/shop，外站链接不打开", async () => {
    await renderWorkbench();
    expect((shop().source as { uri: string }).uri).toBe("https://hotbargain.vip/shop");
    expect(shop().injectedJavaScriptBeforeContentLoaded).toContain('__HB_EMBED_MODE__="pos-ipad"');
    expect(shop().sharedCookiesEnabled).toBe(true);
    const shouldStart = shop().onShouldStartLoadWithRequest as (request: { url: string }) => boolean;
    expect(shouldStart({ url: "https://hotbargain.vip/shop/orders" })).toBe(true);
    expect(shouldStart({ url: "https://www.google.com/" })).toBe(false);
    expect(screen.getByText("Sean Pan")).toBeTruthy();
  });

  it("网页跳去后台页面时拦回 /shop；跳去登录页说明会话没了，退出", async () => {
    const { signOut } = await renderWorkbench();
    const onNavigation = shop().onNavigationStateChange as (navigation: { url: string }) => void;
    await act(async () => onNavigation({ url: "https://hotbargain.vip/dashboard" }));
    expect(mockInjected["ordering-shop-webview"]?.some((code) => code.includes('location.replace("/shop")'))).toBe(true);
    expect(screen.getByText("这里只能打开仓库订货的页面。")).toBeTruthy();
    await act(async () => onNavigation({ url: "https://hotbargain.vip/login" }));
    expect(signOut).toHaveBeenCalledWith("session-lost", expect.anything());
  });

  it("点退出只退一次；扫码在仓库订货页签交给网页搜索", async () => {
    const { scan, signOut } = await renderWorkbench();
    await act(async () => scan("9312345678907"));
    expect(
      mockInjected["ordering-shop-webview"]?.some(
        (code) => code.includes("hb-pos-app:scan") && code.includes("9312345678907"),
      ),
    ).toBe(true);
    await fireEvent.press(screen.getByTestId("ordering-sign-out"));
    await fireEvent.press(screen.getByTestId("ordering-sign-out"));
    expect(signOut).toHaveBeenCalledTimes(1);
    expect(signOut).toHaveBeenCalledWith("user", expect.anything());
  });

  it("切页签时隐藏的仓库订货网页仍留在窗口里（不用 display: none），只是透明且不接收触摸", async () => {
    await renderWorkbench();
    await fireEvent.press(screen.getByTestId("ordering-tab-assistant"));
    // 隐藏窗格对读屏不可见，RNTL 默认查不到，要显式包含。
    const pane = screen.getByTestId("ordering-pane-shop", { includeHiddenElements: true });
    // 中文注释：display: none 会把 WKWebView 移出原生视图层级，页面 JS 被挂起，
    // 订货助手经仓库订货网页申请授权码的脚本就收不到回信（模拟器上实测 20 秒超时）。
    const style = StyleSheet.flatten(pane.props.style) as Record<string, unknown>;
    expect(style.display).toBeUndefined();
    expect(style.opacity).toBe(0);
    expect(pane.props.pointerEvents).toBe("none");
    expect(pane.props.importantForAccessibility).toBe("no-hide-descendants");
    await fireEvent.press(screen.getByTestId("ordering-tab-shop"));
    const visible = StyleSheet.flatten(screen.getByTestId("ordering-pane-shop").props.style) as Record<string, unknown>;
    expect(visible.opacity).toBeUndefined();
    expect(screen.getByTestId("ordering-pane-shop").props.pointerEvents).toBe("auto");
  });

  it("订货助手：经仓库订货网页申请授权码、换令牌，默认用本机分店，打开供应商网站", async () => {
    const requests: { url: string; body?: string; auth?: string }[] = [];
    const fetchImpl: FetchLike = async (url, init) => {
      requests.push({ url, ...(init.body ? { body: init.body } : {}), ...(init.headers.Authorization ? { auth: init.headers.Authorization } : {}) });
      const data = url.endsWith("/api/Auth/extension/token")
        ? { accessToken: "ext-jwt", accessTokenExpiry: "2099-01-01T00:00:00Z" }
        : url.endsWith("/stores")
          ? { stores: [{ storeCode: "1042", storeName: "Orion" }, { storeCode: "1012", storeName: "Bankstown" }] }
          : url.endsWith("/supplier-profiles")
            ? { profiles: [{ supplierCode: "240", displayName: "DATS", enabled: true, origins: ["https://www.dats.com.au/*"] }] }
            : {};
      return { status: 200, ok: true, json: async () => ({ success: true, data }) };
    };
    await renderWorkbench({ fetchImpl, deviceStoreCode: "1012" });
    // 仓库订货网页加载完成：会话桥就绪。
    await act(async () => (shop().onLoadEnd as (event: unknown) => void)({ nativeEvent: { url: "https://hotbargain.vip/shop" } }));

    await fireEvent.press(screen.getByTestId("ordering-tab-assistant"));
    await settle();
    const script = mockInjected["ordering-shop-webview"]!.find((code) => code.includes("/api/Auth/extension/authorize"))!;
    expect(script).toBeTruthy();
    const id = /var id = "([^"]+)"/.exec(script)![1]!;
    const state = /\\"state\\":\\"([^\\"]+)\\"/.exec(script)![1]!;
    await act(async () =>
      (shop().onMessage as (event: unknown) => void)({
        nativeEvent: {
          url: "https://hotbargain.vip/shop",
          data: JSON.stringify({
            channel: "hb-ordering-bridge",
            id,
            result: { httpStatus: 200, body: { success: true, data: { code: "C".repeat(43), state } } },
          }),
        },
      }),
    );
    await settle();

    expect(requests[0]!.url).toBe("https://hotbargain.vip/api/Auth/extension/token");
    expect(requests[0]!.auth).toBeUndefined();
    const stores = requests.find((item) => item.url.endsWith("/browser-extension/stores"))!;
    expect(stores.auth).toBe("Bearer ext-jwt");
    expect(screen.getByText("1012 · Bankstown")).toBeTruthy();
    expect(mockWebViews["supplier-assistant-webview"]).toBeTruthy();
    expect(mockWebViews["supplier-assistant-webview"]!.injectedJavaScript).toContain('"selectedStoreCode":"1012"');
  });
});
