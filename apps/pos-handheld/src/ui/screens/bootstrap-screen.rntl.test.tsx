import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  jest,
} from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";

import { BootstrapScreen } from "./bootstrap-screen";

import { PosSoundContext } from "@/ui/feedback/pos-sound-context";

const mockRetry = jest.fn<() => Promise<void>>();
const mockLoadOtaRecovery = jest.fn<
  (input: { canReload(): boolean }) => Promise<unknown>
>();
const mockServerTest = jest.fn<
  (address: string, signal: AbortSignal) => Promise<boolean>
>();
let mockRuntimeError = "bootstrap.error";
let mockTranslations: Readonly<Record<string, string>> = {};
let mockRuntimeState = {
  backend: "unreachable",
  database: "failed",
  device: "unauthorized",
  phase: "failed",
};

jest.mock("@expo/vector-icons", () => ({
  MaterialCommunityIcons: () => null,
}));

jest.mock("expo-status-bar", () => ({ StatusBar: () => null }));

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    t: (key: string) => mockTranslations[key] ?? key,
  }),
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => ({
    retry: mockRetry,
    state: {
      ...mockRuntimeState,
      error: mockRuntimeError,
    },
  }),
}));

jest.mock("@/core/runtime/expo-bootstrap-server-diagnostics", () => ({
  loadExpoBootstrapServerDiagnostics: () =>
    Promise.resolve({
      currentApiBaseUrl: "https://hotbargain.vip/pos-api",
      test: mockServerTest,
    }),
}));

jest.mock("@/core/runtime/expo-startup-ota-recovery", () => ({
  loadExpoStartupOtaRecovery: (input: { canReload(): boolean }) =>
    mockLoadOtaRecovery(input),
}));

jest.mock("@/ui/shell/status-strip", () => ({
  PosStatusStrip: () => null,
}));

describe("BootstrapScreen", () => {
  beforeEach(() => {
    mockRetry.mockReset();
    mockRetry.mockResolvedValue(undefined);
    mockServerTest.mockReset();
    mockServerTest.mockResolvedValue(true);
    mockLoadOtaRecovery.mockReset();
    mockLoadOtaRecovery.mockRejectedValue(new Error("not under test"));
    mockRuntimeError = "bootstrap.error";
    mockTranslations = {};
    mockRuntimeState = {
      backend: "unreachable",
      database: "failed",
      device: "unauthorized",
      phase: "failed",
    };
    jest.spyOn(console, "error").mockImplementation(() => undefined);
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
        "[HBPOS][Handheld][Bootstrap] Runtime initialization failed.",
        mockRuntimeError,
      ),
    );
  });

  it("启动页显示手机/PDA 身份且不暴露 iPad 或客显文案", async () => {
    mockTranslations = {
      "bootstrap.eyebrow": "MOBILE / PDA CHECKOUT TERMINAL",
    };

    const screen = await render(<BootstrapScreen />);

    expect(
      screen.getByText("MOBILE / PDA CHECKOUT TERMINAL"),
    ).toBeTruthy();
    expect(
      screen.queryByText(/iPad|customer display|external display|客显/iu),
    ).toBeNull();
  });

  it("启动状态由真实 runtime 逐项推进而非伪造百分比", async () => {
    mockRuntimeError = "";
    mockRuntimeState = {
      backend: "unverified",
      database: "opening",
      device: "pending-approval",
      phase: "bootstrapping",
    };
    const screen = await render(<BootstrapScreen />);

    expect(
      screen.getByText("bootstrap.deviceState.pending-approval"),
    ).toBeTruthy();
    expect(
      screen.getByText("bootstrap.backendState.unverified"),
    ).toBeTruthy();
    expect(
      screen.getByText("bootstrap.databaseState.opening"),
    ).toBeTruthy();

    mockRuntimeState = {
      backend: "reachable",
      database: "ready",
      device: "authorized-online",
      phase: "ready",
    };
    await screen.rerender(<BootstrapScreen />);

    expect(
      screen.getByText("bootstrap.deviceState.authorized-online"),
    ).toBeTruthy();
    expect(
      screen.getByText("bootstrap.backendState.reachable"),
    ).toBeTruthy();
    expect(
      screen.getByText("bootstrap.databaseState.ready"),
    ).toBeTruthy();
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

    expect(screen.getByTestId("handheld-state-startup")).toBeTruthy();
    expect(screen.queryByText("HB")).toBeNull();

    await fireEvent.press(screen.getByText("bootstrap.retry"));

    expect(mockRetry).toHaveBeenCalledTimes(1);
    expect(play).toHaveBeenCalledWith("tap");
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
    await fireEvent.press(retryButton);
    expect(mockRetry).not.toHaveBeenCalled();

    const [{ canReload }] = mockLoadOtaRecovery.mock.calls[0] as [
      { canReload(): boolean },
    ];
    expect(canReload()).toBe(true);
    mockRuntimeState = { ...mockRuntimeState, phase: "starting" };
    await screen.rerender(<BootstrapScreen />);
    expect(canReload()).toBe(false);
  });
});
