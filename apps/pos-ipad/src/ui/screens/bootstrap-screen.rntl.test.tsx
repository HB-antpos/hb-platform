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
import { posColors } from "@/ui/theme";

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

  it("启动失败时由首页传入回调才显示网络检测入口", async () => {
    const onOpenNetworkCheck = jest.fn();
    const screen = await render(
      <BootstrapScreen onOpenNetworkCheck={onOpenNetworkCheck} />,
    );
    await fireEvent.press(screen.getByTestId("bootstrap-network-check"));
    expect(onOpenNetworkCheck).toHaveBeenCalledTimes(1);

    await screen.rerender(<BootstrapScreen />);
    expect(screen.queryByTestId("bootstrap-network-check")).toBeNull();
    await screen.unmount();
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

  it("启动失败时未就绪的核心状态卡整张标红，按钮集中在右栏且重试是 88 高主按钮", async () => {
    const screen = await render(
      <BootstrapScreen onOpenNetworkCheck={jest.fn()} />,
    );

    for (const testID of [
      "bootstrap-card-device",
      "bootstrap-card-backend",
      "bootstrap-card-database",
    ]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style),
      ).toMatchObject({
        backgroundColor: posColors.redSoft,
        borderColor: posColors.red,
      });
    }
    // 客显已就绪：保持普通卡片样式。
    expect(
      StyleSheet.flatten(
        screen.getByTestId("bootstrap-card-display").props.style,
      ),
    ).toMatchObject({
      backgroundColor: posColors.surface,
      borderColor: posColors.border,
    });

    expect(
      StyleSheet.flatten(screen.getByTestId("bootstrap-retry").props.style),
    ).toMatchObject({ backgroundColor: posColors.accent, minHeight: 88 });
    const abandon = await screen.findByTestId(
      "bootstrap-abandon-pending-activation",
    );
    expect(StyleSheet.flatten(abandon.props.style)).toMatchObject({
      backgroundColor: posColors.redSoft,
      borderColor: posColors.red,
    });
    expect(
      StyleSheet.flatten(abandon.props.style).minHeight,
    ).toBeGreaterThanOrEqual(64);
    expect(
      StyleSheet.flatten(
        screen.getByTestId("bootstrap-network-check").props.style,
      ).minHeight,
    ).toBeGreaterThanOrEqual(64);
    await screen.unmount();
  });

  it("正常启动中只显示四张状态卡和底部说明，不出现失败按钮与服务器面板", async () => {
    mockRuntimePhase = "starting";

    const screen = await render(
      <BootstrapScreen onOpenNetworkCheck={jest.fn()} />,
    );

    for (const testID of [
      "bootstrap-card-device",
      "bootstrap-card-backend",
      "bootstrap-card-database",
      "bootstrap-card-display",
    ]) {
      expect(screen.getByTestId(testID)).toBeTruthy();
    }
    // 启动中还没轮到的核心项不标红；只有明确失败的那张（这里是本机账本）标红。
    expect(
      StyleSheet.flatten(
        screen.getByTestId("bootstrap-card-backend").props.style,
      ),
    ).toMatchObject({ backgroundColor: posColors.surface });
    expect(
      StyleSheet.flatten(
        screen.getByTestId("bootstrap-card-database").props.style,
      ),
    ).toMatchObject({ backgroundColor: posColors.redSoft });
    expect(screen.getByText("bootstrap.footer")).toBeTruthy();
    expect(screen.queryByTestId("bootstrap-retry")).toBeNull();
    expect(screen.queryByTestId("bootstrap-network-check")).toBeNull();
    expect(
      screen.queryByTestId("bootstrap-abandon-pending-activation"),
    ).toBeNull();
    expect(screen.queryByTestId("server-connection-panel")).toBeNull();
    expect(mockLoadOtaRecovery).not.toHaveBeenCalled();
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
    // 禁用时外观同步变灰（统一按钮的禁用底色），避免店员误以为按钮无响应。
    expect(StyleSheet.flatten(retryButton.props.style)).toMatchObject({
      backgroundColor: posColors.disabledSurface,
      minHeight: 88,
    });
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
