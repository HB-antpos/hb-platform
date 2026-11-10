import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";

import { resolveZellerPairingOutcome, ZellerSettingsPanel } from "./zeller-settings-panel";
import type { ZellerTerminalBoundaryState } from "./zeller-terminal-boundary";

import type { ZellerProbeTerminal } from "@/features/zeller-probe/zeller-probe-runner";

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: jest.fn() }),
}));

function sdkError(type: string): Error {
  return Object.assign(new Error(""), { type });
}

/** 可以由测试决定何时结束的假调用，用来停在「进行中」。 */
function deferred() {
  let resolve: (value: unknown) => void = () => undefined;
  const promise = new Promise<unknown>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function createTerminal(over: Partial<Record<"initialise" | "setup", () => PromiseLike<unknown>>> = {}) {
  const terminal = {
    initialise: jest.fn(over.initialise ?? (() => Promise.resolve(sdkError("Setup Required")))),
    setup: jest.fn(over.setup ?? (() => Promise.resolve(true))),
    purchase: jest.fn(),
    refund: jest.fn(),
    getTransactions: jest.fn(),
  };
  return terminal as typeof terminal & ZellerProbeTerminal;
}

function ready(terminal: ZellerProbeTerminal): ZellerTerminalBoundaryState {
  return { status: "ready", terminal };
}

const at = (hours: number, minutes: number) => () => new Date(2026, 10, 10, hours, minutes);

function text(screen: Awaited<ReturnType<typeof render>>, id: string): string {
  return String(screen.getByTestId(id).props.children);
}

function isDisabled(screen: Awaited<ReturnType<typeof render>>, id: string): boolean {
  return screen.getByTestId(id).props.accessibilityState?.disabled === true;
}

describe("resolveZellerPairingOutcome", () => {
  it("只有 SDK 明确说需要设置才算未配对；其余错误都算没查到", () => {
    expect(resolveZellerPairingOutcome(true)).toEqual({ kind: "paired" });
    expect(resolveZellerPairingOutcome(sdkError("Setup Required"))).toEqual({ kind: "unpaired" });
    expect(resolveZellerPairingOutcome(sdkError("Cancelled"))).toEqual({ kind: "cancelled" });
    expect(resolveZellerPairingOutcome(sdkError("Network Failure"))).toEqual({
      kind: "offline",
      reason: "NetworkFailure",
    });
    // 读不懂的返回不能当成未配对。
    for (const unexpected of [false, null, undefined, "true", sdkError("Something New")]) {
      expect(resolveZellerPairingOutcome(unexpected)).toEqual({
        kind: "offline",
        reason: "Unrecognised",
      });
    }
  });
});

describe("ZellerSettingsPanel", () => {
  it("SDK 还在加载或加载失败时，不能检查也不能配对", async () => {
    const screen = await render(
      <ZellerSettingsPanel activeProviderLabel="Linkly" enable={null} locale="zh" sdk={{ status: "loading" }} />,
    );
    expect(text(screen, "settings-zeller-status")).toBe("正在加载 Zeller…");
    expect(isDisabled(screen, "settings-zeller-check")).toBe(true);
    expect(isDisabled(screen, "settings-zeller-pair")).toBe(true);

    await screen.rerender(
      <ZellerSettingsPanel
        activeProviderLabel="Linkly"
        enable={null}
        locale="zh"
        sdk={{ status: "failed", reason: "native module missing" }}
      />,
    );
    expect(text(screen, "settings-zeller-status")).toBe("Zeller 加载失败");
    expect(text(screen, "settings-zeller-copy")).toBe("Zeller 组件加载失败：native module missing");
    expect(isDisabled(screen, "settings-zeller-pair")).toBe(true);
  });

  it("打开后自动查一次配对；未配对时主按钮是配对刷卡机，启用按钮锁住", async () => {
    const terminal = createTerminal();
    const screen = await render(
      <ZellerSettingsPanel
        activeProviderLabel="Linkly"
        enable={{ active: false, onEnable: jest.fn() }}
        locale="zh"
        now={at(10, 42)}
        sdk={ready(terminal)}
      />,
    );

    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("还没有配对"));
    expect(terminal.initialise).toHaveBeenCalledTimes(1);
    expect(terminal.setup).not.toHaveBeenCalled();
    expect(text(screen, "settings-zeller-pairing")).toBe("未配对");
    expect(text(screen, "settings-zeller-last-check")).toBe("今天 10:42");
    expect(text(screen, "settings-zeller-active-provider")).toBe("Linkly");
    expect(screen.getByText("配对刷卡机")).toBeTruthy();
    expect(text(screen, "settings-zeller-banner-hint")).toBe("配对成功后才能启用。");
    expect(isDisabled(screen, "settings-zeller-enable")).toBe(true);
  });

  it("配对中其余按钮停用；配对成功后才出现可点的启用，启用是单独一步", async () => {
    const pairing = deferred();
    const terminal = createTerminal({ setup: () => pairing.promise });
    const onEnable = jest.fn();
    const screen = await render(
      <ZellerSettingsPanel
        activeProviderLabel="Linkly"
        enable={{ active: false, onEnable }}
        locale="zh"
        now={at(10, 44)}
        sdk={ready(terminal)}
      />,
    );
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("还没有配对"));

    await fireEvent.press(screen.getByTestId("settings-zeller-pair"));
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("正在配对"));
    expect(text(screen, "settings-zeller-pairing")).toBe("配对中…");
    expect(isDisabled(screen, "settings-zeller-check")).toBe(true);
    expect(isDisabled(screen, "settings-zeller-pair")).toBe(true);
    // 连点第二下不会再开一个窗口。
    await fireEvent.press(screen.getByTestId("settings-zeller-pair"));
    expect(terminal.setup).toHaveBeenCalledTimes(1);

    pairing.resolve(true);
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("已配对"));
    expect(text(screen, "settings-zeller-copy")).toBe(
      "已和 Zeller 刷卡机配对。还没有启用，刷卡仍走 Linkly。",
    );
    expect(screen.getByText("重新配对")).toBeTruthy();
    expect(text(screen, "settings-zeller-banner-title")).toBe("把这台 iPad 的刷卡切到 Zeller");
    // 配对成功本身不会启用。
    expect(onEnable).not.toHaveBeenCalled();
    expect(isDisabled(screen, "settings-zeller-enable")).toBe(false);
    await fireEvent.press(screen.getByTestId("settings-zeller-enable"));
    expect(onEnable).toHaveBeenCalledTimes(1);
  });

  it("关掉 Zeller 窗口等于取消：状态和上次检查都不变", async () => {
    const terminal = createTerminal({ setup: () => Promise.resolve(sdkError("Cancelled")) });
    const screen = await render(
      <ZellerSettingsPanel activeProviderLabel={null} enable={null} locale="zh" now={at(9, 5)} sdk={ready(terminal)} />,
    );
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("还没有配对"));

    await fireEvent.press(screen.getByTestId("settings-zeller-pair"));
    await waitFor(() => expect(terminal.setup).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(isDisabled(screen, "settings-zeller-pair")).toBe(false));
    expect(text(screen, "settings-zeller-status")).toBe("还没有配对");
    expect(text(screen, "settings-zeller-last-check")).toBe("今天 09:05");
    expect(text(screen, "settings-zeller-active-provider")).toBe("未选择");
  });

  it("收款还没接入时：已配对也不能启用，并说明原因", async () => {
    const terminal = createTerminal({ initialise: () => Promise.resolve(true) });
    const screen = await render(
      <ZellerSettingsPanel activeProviderLabel="Square" enable={null} locale="zh" sdk={ready(terminal)} />,
    );
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("已配对"));
    expect(isDisabled(screen, "settings-zeller-enable")).toBe(true);
    expect(text(screen, "settings-zeller-banner-hint")).toBe(
      "Zeller 收款还没有接进这台 iPad 的收银流程。现在可以先完成配对，启用要等后续版本。",
    );
  });

  it("已启用：显示已启用，不再有启用按钮", async () => {
    const terminal = createTerminal({ initialise: () => Promise.resolve(true) });
    const screen = await render(
      <ZellerSettingsPanel
        activeProviderLabel="Zeller"
        enable={{ active: true, onEnable: jest.fn() }}
        locale="zh"
        sdk={ready(terminal)}
      />,
    );
    await waitFor(() => expect(text(screen, "settings-zeller-banner-title")).toBe("已启用 Zeller"));
    expect(text(screen, "settings-zeller-copy")).toBe("这台 iPad 的刷卡走 Zeller 刷卡机。");
    expect(screen.queryByTestId("settings-zeller-enable")).toBeNull();
  });

  it("连不上 Zeller：不当成未配对，保留上次已配对的事实，给出重试和网络检测", async () => {
    const results: unknown[] = [true, sdkError("Network Failure")];
    const terminal = createTerminal({ initialise: () => Promise.resolve(results.shift()) });
    const onOpenNetworkCheck = jest.fn();
    const screen = await render(
      <ZellerSettingsPanel
        activeProviderLabel="Zeller"
        enable={{ active: true, onEnable: jest.fn() }}
        locale="zh"
        now={at(14, 3)}
        onOpenNetworkCheck={onOpenNetworkCheck}
        sdk={ready(terminal)}
      />,
    );
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("已配对"));

    await fireEvent.press(screen.getByTestId("settings-zeller-check"));
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("连不上 Zeller"));
    expect(text(screen, "settings-zeller-pairing")).toBe("未知（上次：已配对）");
    expect(text(screen, "settings-zeller-last-check")).toBe("今天 14:03 失败（NetworkFailure）");
    expect(text(screen, "settings-zeller-banner-title")).toBe("刷卡可能暂时用不了");
    // 没查到时不引导重新配对。
    expect(screen.queryByTestId("settings-zeller-pair")).toBeNull();
    expect(screen.getByText("重试")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("settings-zeller-network-check"));
    expect(onOpenNetworkCheck).toHaveBeenCalledTimes(1);
  });

  it("设置页忙或没有权限时，不能检查、配对和启用", async () => {
    const terminal = createTerminal({ initialise: () => Promise.resolve(true) });
    const screen = await render(
      <ZellerSettingsPanel
        activeProviderLabel="Linkly"
        disabled
        enable={{ active: false, onEnable: jest.fn() }}
        locale="zh"
        sdk={ready(terminal)}
      />,
    );
    await waitFor(() => expect(text(screen, "settings-zeller-status")).toBe("已配对"));
    for (const id of ["settings-zeller-check", "settings-zeller-pair", "settings-zeller-enable"]) {
      expect(isDisabled(screen, id)).toBe(true);
    }
  });
});

describe("ZellerSettingsPanel：收款时不弹出 Zeller 窗口开关", () => {
  const baseProps = {
    activeProviderLabel: "Linkly",
    enable: null,
    sdk: { status: "loading" } as const,
  };

  it("不提供开关时不显示这一行", async () => {
    const screen = await render(<ZellerSettingsPanel {...baseProps} locale="zh" />);
    expect(screen.queryByTestId("settings-zeller-hide-ui")).toBeNull();
    expect(screen.queryByText("收款时不弹出 Zeller 窗口")).toBeNull();
  });

  it("显示标题和说明（含「退款和配对仍会弹窗」），开关反映当前值，切换时通知调用方", async () => {
    const onChange = jest.fn();
    const screen = await render(
      <ZellerSettingsPanel {...baseProps} hideSdkUi={{ value: false, onChange }} locale="zh" />,
    );
    expect(screen.getByText("收款时不弹出 Zeller 窗口")).toBeTruthy();
    expect(screen.getByText(/退款和配对仍会弹出 Zeller 窗口/)).toBeTruthy();
    const control = screen.getByTestId("settings-zeller-hide-ui");
    expect(control.props.value).toBe(false);

    await fireEvent(control, "valueChange", true);
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith(true);

    await screen.rerender(
      <ZellerSettingsPanel {...baseProps} hideSdkUi={{ value: true, onChange }} locale="zh" />,
    );
    expect(screen.getByTestId("settings-zeller-hide-ui").props.value).toBe(true);
  });

  it("设置页忙或没有权限时，开关不能改", async () => {
    const onChange = jest.fn();
    const screen = await render(
      <ZellerSettingsPanel
        {...baseProps}
        disabled
        hideSdkUi={{ value: false, onChange }}
        locale="zh"
      />,
    );
    await fireEvent(screen.getByTestId("settings-zeller-hide-ui"), "valueChange", true);
    expect(onChange).not.toHaveBeenCalled();
  });

  it("英文界面显示英文标题", async () => {
    const screen = await render(
      <ZellerSettingsPanel {...baseProps} hideSdkUi={{ value: false, onChange: jest.fn() }} locale="en" />,
    );
    expect(screen.getByText("Hide the Zeller window when taking payment")).toBeTruthy();
    expect(screen.getByText(/Refunds and pairing still open the Zeller window/)).toBeTruthy();
  });
});
