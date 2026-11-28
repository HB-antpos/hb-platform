import { describe, expect, it, jest } from "@jest/globals";
import { act, fireEvent, render, screen } from "@testing-library/react-native";


import { SupplierAssistantWorkspace } from "./supplier-assistant-screen";
import {
  SupplierAssistantSession,
  SupplierAssistantTokenError,
  type FetchLike,
  type SupplierAssistantTokenSource,
} from "./supplier-assistant-session";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string) => key,
  }),
}));

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: jest.fn() }),
}));

// 网页窗口换成假组件：记下 props，并把 injectJavaScript 收集起来供断言。
const mockInjected: string[] = [];
const mockWebView: { props: Record<string, unknown> | null } = { props: null };
jest.mock("react-native-webview", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { View } = jest.requireActual<typeof import("react-native")>("react-native");
  const FakeWebView = React.forwardRef((props: Record<string, unknown>, ref) => {
    mockWebView.props = props;
    React.useImperativeHandle(ref, () => ({
      injectJavaScript: (code: string) => mockInjected.push(code),
      reload: () => undefined,
    }));
    return React.createElement(View, { testID: props.testID as string });
  });
  FakeWebView.displayName = "FakeWebView";
  return { WebView: FakeWebView };
});

const tokenSource: SupplierAssistantTokenSource = {
  issueToken: async () => ({
    accessToken: "jwt",
    expiresAtMs: Date.now() + 5 * 60_000,
    apiOrigin: "https://hotbargain.vip",
  }),
};

/** 订货工作台的用法：工作台建会话、选分店，页签只管供应商网页。 */
function workspace(
  fetchImpl: FetchLike,
  extra: Partial<{
    source: SupplierAssistantTokenSource;
    registerScanHandler: (handler: ((barcode: string) => void) | null) => void;
    onActivity: () => void;
  }> = {},
) {
  const session = new SupplierAssistantSession(extra.source ?? tokenSource, fetchImpl, "1.7.1");
  return (
    <SupplierAssistantWorkspace
      {...(extra.onActivity ? { onActivity: extra.onActivity } : {})}
      {...(extra.registerScanHandler ? { registerScanHandler: extra.registerScanHandler } : {})}
      session={session}
      storeCode="1042"
    />
  );
}

const profiles = {
  configVersion: "3",
  profiles: [
    { supplierCode: "240", displayName: "DATS", enabled: true, origins: ["https://www.dats.com.au/*"], search: { urlTemplate: "x" } },
  ],
};

function createFetch(requests: { url: string; body?: string }[]): FetchLike {
  return async (url, init) => {
    requests.push({ url, ...(init.body ? { body: init.body } : {}) });
    const data = url.endsWith("/supplier-profiles")
      ? profiles
      : url.endsWith("/product-purchase-cycles")
        ? {
            matchStatus: "matched",
            productName: "A4 复印纸",
            latestPurchaseDate: "2026-10-01",
            latestPurchaseQuantity: 20,
            salesSinceLatestPurchase: 12,
            cycles: [{ purchaseDate: "2026-10-01", purchaseQuantity: 20, salesEndDate: "2026-11-25", salesQuantity: 12 }],
          }
        : { summaries: [] };
    return { status: 200, ok: true, json: async () => ({ success: true, data }) };
  };
}

/** 让异步链（取令牌 → 拉供应商配置 → 代发请求 → 回写网页）在 act 里跑完，再做断言。 */
async function settle() {
  for (let index = 0; index < 5; index += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 5));
    });
  }
}

async function sendFromPage(message: unknown, url = "https://www.dats.com.au/office-stationery") {
  const onMessage = mockWebView.props?.onMessage as (event: unknown) => void;
  await act(async () => {
    onMessage({ nativeEvent: { data: JSON.stringify({ channel: "hb-supplier-assistant", kind: "request", id: "m1", message }), url } });
  });
}

describe("SupplierAssistantWorkspace", () => {
  it("用员工插件令牌加载供应商并打开网站，注入插件脚本与活动信号", async () => {
    const requests: { url: string; body?: string }[] = [];
    await render(workspace(createFetch(requests)));

    await settle();
    expect(screen.getByTestId("supplier-assistant-webview")).toBeTruthy();
    expect(screen.getByTestId("supplier-assistant-tab-240")).toBeTruthy();
    expect((mockWebView.props?.source as { uri: string }).uri).toBe("https://www.dats.com.au/");
    const script = mockWebView.props?.injectedJavaScript as string;
    expect(script).toContain('"selectedStoreCode":"1042"');
    expect(script).toContain("hb-ordering-activity");
    // 令牌绝不能进网页。
    expect(script).not.toContain("jwt");
    expect(requests[0]!.url).toBe("https://hotbargain.vip/api/react/v1/browser-extension/supplier-profiles");
  });

  it("本站 http 跳转改走 https 在 App 里打开，不交给 Safari；外站 http 拦下", async () => {
    await render(workspace(createFetch([])));
    await settle();
    // http 必须在白名单里，否则 react-native-webview 会把它交给系统（Safari）打开。
    expect(mockWebView.props?.originWhitelist).toEqual(["https://*", "http://*"]);
    const shouldStart = () => mockWebView.props?.onShouldStartLoadWithRequest as (request: { url: string }) => boolean;
    let result = true;
    await act(async () => {
      result = shouldStart()({ url: "http://www.dats.com.au/home.html" });
    });
    expect(result).toBe(false);
    expect((mockWebView.props?.source as { uri: string }).uri).toBe("https://www.dats.com.au/home.html");
    // 网站又把同一个 https 地址打回 http：不再升级，避免死循环。
    await act(async () => {
      result = shouldStart()({ url: "http://www.dats.com.au/home.html" });
    });
    expect(result).toBe(false);
    expect(shouldStart()({ url: "http://evil.example/" })).toBe(false);
    expect(shouldStart()({ url: "https://www.dats.com.au/office-stationery" })).toBe(true);
    // ATS 拦下明文（-1022）时给明确提示，而不是系统报错页。
    const renderError = mockWebView.props?.renderError as (domain: string | undefined, code: number) => React.ReactElement;
    await render(renderError("NSURLErrorDomain", -1022));
    expect(screen.getByText("这个供应商网站不是加密连接，App 里暂时打不开。")).toBeTruthy();
  });

  it("网页发来的摘要请求按工作台选定的分店代发，结果回写给网页", async () => {
    mockInjected.length = 0;
    const requests: { url: string; body?: string }[] = [];
    await render(workspace(createFetch(requests)));
    await settle();
    expect(screen.getByTestId("supplier-assistant-webview")).toBeTruthy();

    await sendFromPage({ type: "SUMMARY_BATCH", storeCode: "9999", supplierCode: "240", itemNumbers: ["VD028"] });

    await settle();
    expect(mockInjected.some((code) => code.includes('resolve("m1"'))).toBe(true);
    const summary = requests.find((item) => item.url.endsWith("/product-purchase-cycle-summary/batch"));
    expect(JSON.parse(summary!.body!).storeCode).toBe("1042");
  });

  it("别的网站发来的消息不代发", async () => {
    mockInjected.length = 0;
    const requests: { url: string; body?: string }[] = [];
    await render(workspace(createFetch(requests)));
    await settle();
    expect(screen.getByTestId("supplier-assistant-webview")).toBeTruthy();
    const before = requests.length;

    await sendFromPage({ type: "BARCODE_LOOKUP", supplierCode: "240", barcode: "9312345678907" }, "https://evil.example/");

    await settle();
    expect(mockInjected.some((code) => code.includes('"ok":false'))).toBe(true);
    expect(requests.length).toBe(before);
  });

  it("点商品按钮后右侧显示采购记录", async () => {
    await render(workspace(createFetch([])));
    await settle();
    expect(screen.getByTestId("supplier-assistant-webview")).toBeTruthy();

    await sendFromPage({ type: "LOCATE_ITEM", supplierCode: "240", itemNumber: "VD028" });

    await settle();
    expect(screen.getByText("A4 复印纸")).toBeTruthy();
    expect(screen.getByText("货号 VD028")).toBeTruthy();
    expect(screen.getAllByTestId("supplier-assistant-cycle")).toHaveLength(1);
    await fireEvent.press(screen.getByTestId("supplier-assistant-panel-close"));
    expect(screen.queryByTestId("supplier-assistant-panel")).toBeNull();
  });

  it("BLE 扫码转进网页里的插件脚本", async () => {
    mockInjected.length = 0;
    let handler: ((barcode: string) => void) | null = null;
    await render(
      workspace(createFetch([]), {
        registerScanHandler: (next) => {
          handler = next;
        },
      }),
    );
    await settle();
    expect(screen.getByTestId("supplier-assistant-webview")).toBeTruthy();

    await act(async () => handler?.("9312345678907"));

    expect(mockInjected.some((code) => code.includes('"type":"BARCODE_SCANNED"') && code.includes("9312345678907"))).toBe(true);
  });

  it("网页里有操作时通知工作台（闲置计时）", async () => {
    const onActivity = jest.fn();
    await render(workspace(createFetch([]), { onActivity }));
    await settle();
    const onMessage = mockWebView.props?.onMessage as (event: unknown) => void;
    await act(async () => {
      onMessage({ nativeEvent: { data: JSON.stringify({ channel: "hb-ordering-activity" }), url: "https://www.dats.com.au/" } });
    });
    expect(onActivity).toHaveBeenCalledTimes(1);
  });

  it("网页会话失效时提示重新登录，不显示重试", async () => {
    const expired: SupplierAssistantTokenSource = {
      issueToken: async () => {
        throw new SupplierAssistantTokenError("网页登录已失效", "SESSION_EXPIRED");
      },
    };
    await render(workspace(createFetch([]), { source: expired }));

    await settle();
    expect(screen.getByText("登录已失效，请退出后重新登录。")).toBeTruthy();
    expect(screen.queryByTestId("supplier-assistant-retry")).toBeNull();
  });

  it("账号没有订货助手权限（403）时明确提示", async () => {
    const forbidden: FetchLike = async () => ({
      status: 403,
      ok: false,
      json: async () => ({ success: false, errorCode: "FORBIDDEN" }),
    });
    await render(workspace(forbidden));

    await settle();
    expect(screen.getByText("这个账号没有订货助手权限，请联系店长开通订货权限。")).toBeTruthy();
    expect(screen.queryByTestId("supplier-assistant-retry")).toBeNull();
  });
});
