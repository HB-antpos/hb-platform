import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  jest,
} from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";
import { Alert, StyleSheet } from "react-native";

import { BootstrapScreen } from "./bootstrap-screen";

import { PosSoundContext } from "@/ui/feedback/pos-sound-context";

const mockRetry = jest.fn<() => Promise<void>>();
const mockLoadOtaRecovery = jest.fn<
  (input: { canReload(): boolean }) => Promise<unknown>
>();
let mockRuntimePhase = "failed";
let mockRuntimeError = "bootstrap.error";
const mockAbandonPendingDeviceActivation = jest.fn<() => Promise<void>>();
const mockServerTest =
  jest.fn<(address: string, signal: AbortSignal) => Promise<boolean>>();

jest.mock("@expo/vector-icons", () => ({
  MaterialCommunityIcons: () => null,
}));

jest.mock("expo-status-bar", () => ({ StatusBar: () => null }));

jest.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => ({
    retry: mockRetry,
    state: {
      backend: "unreachable",
      database: "failed",
      device: "unauthorized",
      error: mockRuntimeError,
      phase: mockRuntimePhase,
    },
  }),
}));

jest.mock("@/core/runtime/expo-bootstrap-server-diagnostics", () => ({
  loadExpoBootstrapServerDiagnostics: () =>
    Promise.resolve({
      abandonPendingDeviceActivation: mockAbandonPendingDeviceActivation,
      canAbandonPendingDeviceActivation: true,
      currentApiBaseUrl: "https://hotbargain.vip/pos-api",
      test: mockServerTest,
    }),
}));

jest.mock("@/core/runtime/expo-startup-ota-recovery", () => ({
  loadExpoStartupOtaRecovery: (input: { canReload(): boolean }) =>
    mockLoadOtaRecovery(input),
}));

jest.mock("@/ui/shell/pos-shell-store", () => ({
  usePosShellStore: (selector: (state: { display: string }) => unknown) =>
    selector({ display: "ready" }),
}));

jest.mock("@/ui/shell/status-strip", () => ({
  PosStatusStrip: () => null,
}));

describe("BootstrapScreen", () => {
  beforeEach(() => {
    mockRetry.mockReset();
    mockRetry.mockResolvedValue(undefined);
    mockRuntimePhase = "failed";
    mockRuntimeError = "bootstrap.error";
    jest.spyOn(console, "error").mockImplementation(() => undefined);
    mockLoadOtaRecovery.mockReset();
    mockLoadOtaRecovery.mockRejectedValue(new Error("not under test"));
    mockAbandonPendingDeviceActivation.mockReset();
    mockAbandonPendingDeviceActivation.mockResolvedValue(undefined);
    mockServerTest.mockReset();
    mockServerTest.mockResolvedValue(true);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it("SecureStore 原始英文错误只写诊断日志，操作员只看到本地化安全摘要", async () => {
    mockRuntimeError =
      "SecureStore: missing keychain-access-groups entitlement for getItemAsync";

    const screen = await render(<BootstrapScreen />);

    expect(screen.getByText("bootstrap.error.secureStorage")).toBeTruthy();
    expect(screen.queryByText(mockRuntimeError)).toBeNull();
    await waitFor(() =>
      expect(console.error).toHaveBeenCalledWith(
        "[HBPOS][iPad][Bootstrap] Runtime initialization failed.",
        mockRuntimeError,
      ),
    );
  });

  it("其他初始化异常显示通用本地化摘要，不暴露原始英文报错", async () => {
    mockRuntimeError =
      "Recovered pricing state does not reproduce the persisted cart.";

    const screen = await render(<BootstrapScreen />);

    expect(screen.getByText("bootstrap.error.generic")).toBeTruthy();
    expect(screen.queryByText(mockRuntimeError)).toBeNull();
  });

  it("失败时重试保留原有调用，并发出 tap 触控音", async () => {
    const play = jest.fn();
    const screen = await render(
      <PosSoundContext.Provider
        value={{
          buttonSoundEnabled: true,
          play,
          setButtonSoundEnabled: jest.fn(),
          setSpecialNodeSoundEnabled: jest.fn(),
          specialNodeSoundEnabled: true,
        }}
      >
        <BootstrapScreen />
      </PosSoundContext.Provider>,
    );

    await fireEvent.press(screen.getByText("bootstrap.retry"));

    expect(mockRetry).toHaveBeenCalledTimes(1);
    expect(play).toHaveBeenCalledWith("tap");
    await screen.unmount();
  });

  it("重试启动进行中禁止重复重试或放弃 pending", async () => {
    let finishRetry: (() => void) | undefined;
    mockRetry.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finishRetry = resolve;
        }),
    );
    const alert = jest.spyOn(Alert, "alert");
    const screen = await render(<BootstrapScreen />);

    const retry = screen.getByRole("button", { name: "bootstrap.retry" });
    await fireEvent.press(retry);
    await fireEvent.press(retry);

    expect(mockRetry).toHaveBeenCalledTimes(1);
    const abandon = screen.getByTestId("bootstrap-abandon-pending-activation");
    expect(abandon.props.accessibilityState.disabled).toBe(true);
    await fireEvent.press(abandon);
    expect(alert).not.toHaveBeenCalled();

    finishRetry?.();
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "bootstrap.retry" }).props
          .accessibilityState.disabled,
      ).toBe(false),
    );

    alert.mockRestore();
    await screen.unmount();
  });

  it("确认放弃旧开通后先清理单一 pending，再重试启动", async () => {
    let finishAbandon: (() => void) | undefined;
    mockAbandonPendingDeviceActivation.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finishAbandon = resolve;
        }),
    );
    const alert = jest
      .spyOn(Alert, "alert")
      .mockImplementation((_title, _message, buttons) => {
        buttons?.find((button) => button.style === "destructive")?.onPress?.();
      });
    const screen = await render(<BootstrapScreen />);

    await fireEvent.press(
      screen.getByTestId("bootstrap-abandon-pending-activation"),
    );

    expect(alert).toHaveBeenCalledWith(
      "bootstrap.abandonPendingTitle",
      "bootstrap.abandonPendingMessage",
      expect.any(Array),
    );
    await waitFor(() =>
      expect(mockAbandonPendingDeviceActivation).toHaveBeenCalledTimes(1),
    );
    expect(mockRetry).not.toHaveBeenCalled();
    expect(
      screen.getByRole("button", { name: "bootstrap.retry" }).props
        .accessibilityState.disabled,
    ).toBe(true);

    finishAbandon?.();
    await waitFor(() => expect(mockRetry).toHaveBeenCalledTimes(1));

    alert.mockRestore();
    await screen.unmount();
  });

  it("失败准备页可修改候选地址并测试，但不允许绕过账本门禁保存", async () => {
    const screen = await render(<BootstrapScreen />);
    await waitFor(() =>
      expect(screen.getByTestId("server-connection-panel")).toBeTruthy(),
    );

    await fireEvent.press(screen.getByTestId("server-connection-edit"));
    await fireEvent.press(screen.getByTestId("server-connection-test"));

    await waitFor(() =>
      expect(mockServerTest).toHaveBeenCalledWith(
        "https://hotbargain.vip/pos-api",
        expect.any(AbortSignal),
      ),
    );
    expect(
      screen.getByTestId("server-connection-save").props.accessibilityState
        .disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("server-connection-save-disabled-reason"),
    ).toBeTruthy();
  });

  it("失败页加载修复更新通道；安装进行中禁用重试，reload 只在 runtime 仍失败时放行", async () => {
    let listener: ((state: unknown) => void) | null = null;
    const recovery = {
      check: jest.fn(async () => {
        listener?.({ phase: "applying" });
        return { phase: "applying" };
      }),
      apply: jest.fn(),
      getState: () => ({ phase: "applying" }),
      subscribe: (next: (state: unknown) => void) => {
        listener = next;
        return () => {
          listener = null;
        };
      },
    };
    mockLoadOtaRecovery.mockResolvedValue(recovery);

    const screen = await render(<BootstrapScreen />);

    await waitFor(() =>
      expect(screen.getByText("bootstrap.otaRecovery.applying")).toBeTruthy(),
    );
    const retryButton = screen.getByRole("button", { name: "bootstrap.retry" });
    expect(retryButton.props.accessibilityState.disabled).toBe(true);
    // 禁用时外观同步变灰，避免店员误以为按钮无响应。
    expect(StyleSheet.flatten(retryButton.props.style).opacity).toBe(0.4);
    await fireEvent.press(retryButton);
    expect(mockRetry).not.toHaveBeenCalled();

    const [{ canReload }] = mockLoadOtaRecovery.mock.calls[0] as [
      { canReload(): boolean },
    ];
    expect(canReload()).toBe(true);
    mockRuntimePhase = "starting";
    await screen.rerender(<BootstrapScreen />);
    expect(canReload()).toBe(false);
  });
});
