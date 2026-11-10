import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";

import type { ZellerProbeOperation, ZellerProbeTerminal } from "./zeller-probe-runner";
import { ZellerProbeScreen } from "./zeller-probe-screen";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    // 页面外壳的顶栏（状态条）用全局文案；这里原样返回键名即可。
    t: (key: string) => key,
  }),
}));

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: jest.fn() }),
}));

const REFERENCE = "HBZ-799287a33dc24df1bca3d33eea19b35a";
const UUID = "65c4b343-03e6-49ef-a54d-012e3f2c222c";

const ports = {
  newGuid: () => "799287a3-3dc2-4df1-bca3-d33eea19b35a",
  nowMs: () => Date.now(),
  nowIso: () => "2026-11-10T02:00:00.000Z",
};

function approved(over: Record<string, unknown> = {}) {
  return {
    type: "PURCHASE",
    amount: 100,
    status: "APPROVED",
    transactionUuid: UUID,
    externalReference: REFERENCE,
    ...over,
  };
}

/** 可以由测试决定何时结束的假调用，用来停在「进行中」的状态。 */
function deferred(): {
  operation: ZellerProbeOperation;
  resolve(value: unknown): void;
  cancel: ReturnType<typeof jest.fn>;
} {
  let resolve: (value: unknown) => void = () => undefined;
  const promise = new Promise<unknown>((done) => {
    resolve = done;
  });
  const cancel = jest.fn();
  return {
    operation: Object.assign(promise, { cancel, onEvent: () => undefined }),
    resolve,
    cancel,
  };
}

function immediate(value: unknown): ZellerProbeOperation {
  return Object.assign(Promise.resolve(value), {
    cancel: () => undefined,
    onEvent: () => undefined,
  });
}

function createTerminal(over: Partial<ZellerProbeTerminal> = {}) {
  const terminal = {
    initialise: jest.fn(() => immediate(true)),
    setup: jest.fn(() => immediate(true)),
    purchase: jest.fn(() => immediate(approved())),
    refund: jest.fn(() =>
      immediate(
        approved({
          type: "REFUND",
          externalReference: "HBZR-799287a33dc24df1bca3d33eea19b35a",
        }),
      ),
    ),
    getTransactions: jest.fn(() => immediate([approved()])),
    ...over,
  };
  return terminal as typeof terminal & ZellerProbeTerminal;
}

function disabled(element: { props: { accessibilityState?: { disabled?: boolean } } }) {
  return element.props.accessibilityState?.disabled === true;
}

describe("ZellerProbeScreen", () => {
  it("SDK 还没加载好或当前构建不提供时，所有操作都不可用", async () => {
    const screen = await render(
      <ZellerProbeScreen
        context={[]}
        onBack={jest.fn()}
        ports={ports}
        sdk={{ status: "unavailable" }}
      />,
    );

    expect(screen.getByTestId("zeller-probe-sdk-status").props.children).toBe(
      "正式版不提供这个验证页。",
    );
    for (const id of ["initialise", "setup", "purchase", "query", "refund", "cancel"]) {
      expect(disabled(screen.getByTestId(`zeller-probe-${id}`))).toBe(true);
    }

    await screen.rerender(
      <ZellerProbeScreen
        context={[]}
        onBack={jest.fn()}
        ports={ports}
        sdk={{ status: "failed", reason: "native module missing" }}
      />,
    );
    expect(screen.getByTestId("zeller-probe-sdk-status").props.children).toBe(
      "Zeller SDK 加载失败：native module missing",
    );
    expect(disabled(screen.getByTestId("zeller-probe-purchase"))).toBe(true);
  });

  it("扣款批准后才能退款；退款用原交易号，查询用上一笔的 reference", async () => {
    const terminal = createTerminal();
    const screen = await render(
      <ZellerProbeScreen
        context={["Zeller terminal check"]}
        onBack={jest.fn()}
        ports={ports}
        sdk={{ status: "ready", terminal }}
      />,
    );

    // 还没有扣款：查询和退款都不可用。
    expect(disabled(screen.getByTestId("zeller-probe-query"))).toBe(true);
    expect(disabled(screen.getByTestId("zeller-probe-refund"))).toBe(true);

    await fireEvent.press(screen.getByTestId("zeller-probe-amount-200"));
    await fireEvent.press(screen.getByTestId("zeller-probe-purchase"));
    await waitFor(() => expect(screen.getByTestId("zeller-probe-entry-0")).toBeTruthy());

    expect(terminal.purchase).toHaveBeenCalledWith({ amount: 200, reference: REFERENCE });
    // 假终端批准的是 100 分，而我们要的是 200 分：金额对不上，必须判结果未知，不能退款。
    expect(screen.getByTestId("zeller-probe-last").props.children).toBe(
      `Unknown · ${REFERENCE}`,
    );
    expect(disabled(screen.getByTestId("zeller-probe-refund"))).toBe(true);
    expect(disabled(screen.getByTestId("zeller-probe-query"))).toBe(false);

    await fireEvent.press(screen.getByTestId("zeller-probe-amount-100"));
    await fireEvent.press(screen.getByTestId("zeller-probe-purchase"));
    await waitFor(() => expect(screen.getByTestId("zeller-probe-entry-1")).toBeTruthy());
    expect(screen.getByTestId("zeller-probe-last").props.children).toBe(
      `Approved · ${REFERENCE}`,
    );
    expect(disabled(screen.getByTestId("zeller-probe-refund"))).toBe(false);

    await fireEvent.press(screen.getByTestId("zeller-probe-query"));
    await waitFor(() => expect(screen.getByTestId("zeller-probe-entry-2")).toBeTruthy());
    expect(terminal.getTransactions).toHaveBeenCalledWith({ reference: REFERENCE });

    await fireEvent.press(screen.getByTestId("zeller-probe-refund"));
    await waitFor(() => expect(screen.getByTestId("zeller-probe-entry-3")).toBeTruthy());
    expect(terminal.refund).toHaveBeenCalledWith({
      amount: 100,
      purchase: UUID,
      reference: "HBZR-799287a33dc24df1bca3d33eea19b35a",
    });
  });

  it("一笔调用进行中时只能取消，不能再发起别的调用", async () => {
    const pending = deferred();
    const terminal = createTerminal({ purchase: jest.fn(() => pending.operation) });
    const screen = await render(
      <ZellerProbeScreen
        context={[]}
        onBack={jest.fn()}
        ports={ports}
        sdk={{ status: "ready", terminal }}
      />,
    );

    await fireEvent.press(screen.getByTestId("zeller-probe-purchase"));
    await waitFor(() =>
      expect(screen.getByTestId("zeller-probe-state").props.children).toBe("等待刷卡机…"),
    );

    for (const id of ["initialise", "setup", "purchase", "query", "refund"]) {
      expect(disabled(screen.getByTestId(`zeller-probe-${id}`))).toBe(true);
    }
    // 连点第二下不会再发起一笔。
    await fireEvent.press(screen.getByTestId("zeller-probe-purchase"));
    expect(terminal.purchase).toHaveBeenCalledTimes(1);

    expect(disabled(screen.getByTestId("zeller-probe-cancel"))).toBe(false);
    await fireEvent.press(screen.getByTestId("zeller-probe-cancel"));
    expect(pending.cancel).toHaveBeenCalledTimes(1);

    pending.resolve(Object.assign(new Error(""), { type: "Cancelled" }));
    await waitFor(() =>
      expect(screen.getByTestId("zeller-probe-state").props.children).toBe("就绪"),
    );
    expect(screen.getByTestId("zeller-probe-last").props.children).toBe(
      `VerifyThenNotPaid · ${REFERENCE}`,
    );
    expect(disabled(screen.getByTestId("zeller-probe-cancel"))).toBe(true);
  });

  it("金额三选一：默认 $1.00，选中项跟着点击切换，扣款按钮显示所选金额", async () => {
    const screen = await render(
      <ZellerProbeScreen
        context={[]}
        onBack={jest.fn()}
        ports={ports}
        sdk={{ status: "ready", terminal: createTerminal() }}
      />,
    );
    const checked = (cents: number) =>
      screen.getByTestId(`zeller-probe-amount-${cents}`).props.accessibilityState
        ?.checked === true;

    expect([100, 200, 500].map(checked)).toEqual([true, false, false]);
    expect(screen.getByText("扣款 $1.00")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("zeller-probe-amount-500"));
    expect([100, 200, 500].map(checked)).toEqual([false, false, true]);
    expect(screen.getByText("扣款 $5.00")).toBeTruthy();
  });

  it("分享的是整份日志；分享失败时提示可以手动拷贝", async () => {
    const terminal = createTerminal();
    const shareReport = jest.fn(async (_report: string, _title: string) => undefined);
    const screen = await render(
      <ZellerProbeScreen
        context={["Zeller terminal check", "app: 0.2.0"]}
        onBack={jest.fn()}
        ports={ports}
        sdk={{ status: "ready", terminal }}
        shareReport={shareReport}
      />,
    );
    expect(disabled(screen.getByTestId("zeller-probe-share"))).toBe(true);

    await fireEvent.press(screen.getByTestId("zeller-probe-initialise"));
    await waitFor(() => expect(screen.getByTestId("zeller-probe-entry-0")).toBeTruthy());
    await fireEvent.press(screen.getByTestId("zeller-probe-share"));
    await waitFor(() => expect(shareReport).toHaveBeenCalledTimes(1));
    const report = shareReport.mock.calls[0]?.[0] ?? "";
    expect(report).toContain("app: 0.2.0");
    expect(report).toContain("#1 2026-11-10T02:00:00.000Z initialise → paired");

    shareReport.mockRejectedValueOnce(new Error("no share sheet"));
    await fireEvent.press(screen.getByTestId("zeller-probe-share"));
    await waitFor(() =>
      expect(screen.getByText("打不开分享。可以长按选中日志文字后拷贝。")).toBeTruthy(),
    );
  });
});
