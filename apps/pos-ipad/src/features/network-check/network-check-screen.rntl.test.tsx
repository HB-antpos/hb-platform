import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";

import type { NetworkCheckPorts } from "./network-check-runner";
import { NetworkCheckScreen } from "./network-check-screen";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
  }),
}));

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: jest.fn() }),
}));

const healthOk = JSON.stringify({
  success: true,
  data: { isOnline: true, serverTime: new Date().toISOString(), status: "ok" },
});

/** EdgeOne 入口不通、其余都正常的假网络。 */
function createPorts(): NetworkCheckPorts & { requests: string[] } {
  const requests: string[] = [];
  return {
    requests,
    fetch: async (url) => {
      requests.push(url);
      const parsed = new URL(url);
      if (parsed.hostname === "pos.hotbargain.vip") {
        throw new TypeError("Network request failed");
      }
      const body =
        parsed.pathname === "/dns-query"
          ? JSON.stringify({ Status: 0, Answer: [{ type: 1, data: "43.160.208.238" }] })
          : parsed.pathname === "/api/health/diagnostics"
            ? JSON.stringify({ clientIp: "124.187.52.242", requestId: "req-1" })
            : parsed.pathname === "/cdn-cgi/trace"
              ? "ip=1.2.3.4"
              : healthOk;
      return { status: 200, headers: { get: () => null }, text: async () => body };
    },
    now: () => Date.now(),
    wallNow: () => Date.now(),
    sleep: async () => undefined,
    readLocalNetwork: async () => ({
      known: true,
      connected: true,
      type: "WIFI",
      ipAddress: "192.168.1.20",
    }),
    readEntryState: () => ({ preferred: "aws", active: "aws", usingFallback: false }),
  };
}

const context = {
  appLabel: "0.2.0 (11) · runtime 0.2.0 · OTA embedded",
  deviceLabel: "iPad · iPadOS 26.6",
  serverLabel: "https://hotbargain.vip/pos-api",
};

describe("NetworkCheckScreen", () => {
  it("拿到服务器地址后自动检测，并排显示三条入口、首选 / 实际入口并给出结论", async () => {
    const ports = createPorts();
    const screen = await render(
      <NetworkCheckScreen
        apiBaseUrl="https://hotbargain.vip/pos-api"
        context={context}
        onBack={jest.fn()}
        ports={ports}
      />,
    );

    await waitFor(() =>
      expect(screen.getByTestId("network-check-verdict-title").props.children).toBe(
        "备用入口（EdgeOne）不通，首选入口正常",
      ),
    );
    // 后台首选 AWS 悉尼：顶部显示首选、当前实际入口与是否回退中。
    expect(screen.getByTestId("network-check-summary-preferred").props.children).toBe("AWS 悉尼");
    expect(screen.getByTestId("network-check-summary-active").props.children).toBe("AWS 悉尼入口");
    expect(screen.getByTestId("network-check-summary-fallback").props.children).toBe("否，正在用首选入口");
    expect(screen.getByTestId("network-check-aws-first-status").props.children).toBe("正常");
    expect(screen.getByTestId("network-check-edge-first-status").props.children).toBe("失败");
    expect(screen.getByTestId("network-check-direct-first-status").props.children).toBe("正常");
    expect(screen.getByTestId("network-check-edge-burst-status").props.children).toBe("跳过");
    expect(screen.getByTestId("network-check-publicIp-status").props.children).toBe("正常");
    expect(screen.getByTestId("network-check-report").props.children).toContain(
      "【直连 · hotbargain.vip】",
    );
  });

  it("未拿到地址时不发请求；分享报告调用注入的分享函数", async () => {
    const ports = createPorts();
    const shareReport = jest.fn(async (_report: string, _title: string) => undefined);
    const onBack = jest.fn();
    const screen = await render(
      <NetworkCheckScreen
        apiBaseUrl={null}
        context={context}
        onBack={onBack}
        ports={ports}
        shareReport={shareReport}
      />,
    );
    expect(ports.requests).toHaveLength(0);
    expect(screen.getByText("正在读取服务器地址…")).toBeTruthy();

    await screen.rerender(
      <NetworkCheckScreen
        apiBaseUrl="https://hotbargain.vip/pos-api"
        context={context}
        onBack={onBack}
        ports={ports}
        shareReport={shareReport}
      />,
    );
    await waitFor(() => expect(screen.getByTestId("network-check-report")).toBeTruthy());
    await fireEvent.press(screen.getByTestId("network-check-share"));
    await waitFor(() => expect(shareReport).toHaveBeenCalledTimes(1));
    expect(shareReport.mock.calls[0]?.[0]).toContain("HB POS 网络检测报告");
    expect(shareReport.mock.calls[0]?.[1]).toBe("HB POS 网络检测报告");

    await fireEvent.press(screen.getByTestId("network-check-back"));
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it("检测中可以停止，停止后显示已停止并可重新检测", async () => {
    const ports = createPorts();
    // 让第一次请求一直挂起，模拟慢网络。
    ports.fetch = (url, init) =>
      new Promise((_, reject) => {
        ports.requests.push(url);
        init.signal.addEventListener("abort", () => reject(new Error("aborted")));
      });
    const screen = await render(
      <NetworkCheckScreen
        apiBaseUrl="https://hotbargain.vip/pos-api"
        context={context}
        onBack={jest.fn()}
        ports={ports}
      />,
    );
    await waitFor(() => expect(screen.getByTestId("network-check-stop")).toBeTruthy());
    await fireEvent.press(screen.getByTestId("network-check-stop"));
    await waitFor(() =>
      expect(screen.getByTestId("network-check-phase").props.children).toBe("已停止"),
    );
    expect(screen.getByTestId("network-check-start")).toBeTruthy();
    expect(screen.getByTestId("network-check-report").props.children).toContain(
      "（检测中途停止）",
    );
  });
});
