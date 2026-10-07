import { afterEach, expect, jest, test } from "@jest/globals";
import { act, cleanup, render, waitFor } from "@testing-library/react-native";
import { AppState } from "react-native";

import { RuntimeWorkBridge } from "./runtime-work-bridge";

let mockRuntime: any;

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

// 变量名必须以 mock 开头，jest 才允许 mock 工厂引用；各用例按需切换联网状态。
let mockConnectivity = "online";

jest.mock("./pos-shell-store", () => ({
  usePosShellStore: (selector: (state: { connectivity: string }) => unknown) =>
    selector({ connectivity: mockConnectivity }),
}));

afterEach(async () => {
  await cleanup();
  jest.restoreAllMocks();
  jest.useRealTimers();
  mockRuntime = null;
  mockConnectivity = "online";
});

test("常驻前台每 30 分钟检查更新，后台和卸载后不检查", async () => {
  jest.useFakeTimers();
  const refresh = jest.fn(async () => undefined);
  const previous = AppState.currentState;
  mockRuntime = { services: {
    sync: { onApplicationStarted: async () => {}, onForeground: async () => {}, onNetworkChanged: async () => {} },
    fulfilment: { drainAutomaticQueue: async () => {} },
    appUpdates: { refreshOnStartup: async () => {}, refreshOnNetworkAvailable: async () => {}, refreshOnForeground: refresh },
  } };
  try {
    AppState.currentState = "active";
    const screen = await render(<RuntimeWorkBridge />);
    await act(async () => { jest.advanceTimersByTime(30 * 60 * 1_000); });
    expect(refresh).toHaveBeenCalledTimes(1);
    AppState.currentState = "background";
    await act(async () => { jest.advanceTimersByTime(30 * 60 * 1_000); });
    expect(refresh).toHaveBeenCalledTimes(1);
    await screen.unmount();
    AppState.currentState = "active";
    await act(async () => { jest.advanceTimersByTime(30 * 60 * 1_000); });
    expect(refresh).toHaveBeenCalledTimes(1);
  } finally { AppState.currentState = previous; }
});

test("程序日志缺失或 SQLite 日志失败时，启动与联网同步仍照常运行", async () => {
  const started = jest.fn(async () => undefined);
  const network = jest.fn(async (_isOnline: boolean) => undefined);
  mockRuntime = {
    services: {
      sync: {
        onApplicationStarted: started,
        onForeground: async () => undefined,
        onNetworkChanged: network,
      },
      fulfilment: { drainAutomaticQueue: async () => undefined },
    },
  };

  await act(async () => {
    await render(<RuntimeWorkBridge />);
  });
  await waitFor(() => {
    expect(started).toHaveBeenCalledTimes(1);
    expect(network).toHaveBeenCalledWith(true);
  });
});

test("后台同步失败时只 best-effort 记录程序日志，不让 bridge 抛出", async () => {
  const record = jest.fn();
  mockRuntime = {
    services: {
      sync: {
        onApplicationStarted: async () => { throw new Error("sync failure"); },
        onForeground: async () => undefined,
        onNetworkChanged: async () => undefined,
      },
      fulfilment: { drainAutomaticQueue: async () => undefined },
      applicationLog: {
        onApplicationStarted: jest.fn(),
        onForeground: jest.fn(),
        onNetworkChanged: jest.fn(),
        record,
      },
    },
  };

  await act(async () => {
    await render(<RuntimeWorkBridge />);
    await new Promise((resolve) => setImmediate(resolve));
  });
  await waitFor(() => {
    expect(record).toHaveBeenCalledWith(expect.objectContaining({
      level: "Error",
      category: "runtime.background-work",
    }));
  });
});

function receiptProfileServices(
  overrides: Readonly<Record<string, unknown>> = {},
) {
  const requestSync = jest.fn(async (_trigger: string) => undefined);
  return {
    requestSync,
    services: {
      device: "authorized-online",
      sync: {
        onApplicationStarted: async () => undefined,
        onForeground: async () => undefined,
        onNetworkChanged: async () => undefined,
      },
      fulfilment: { drainAutomaticQueue: async () => undefined },
      receiptProfileSync: { requestSync },
      ...overrides,
    },
  };
}

test("设备认证就绪后立即同步一次下发资料，之后前台在线每 60 秒一次，后台与卸载后不再轮询", async () => {
  jest.useFakeTimers();
  const { requestSync, services } = receiptProfileServices();
  mockRuntime = { services };
  const previous = AppState.currentState;
  try {
    AppState.currentState = "active";
    const screen = await render(<RuntimeWorkBridge />);
    await act(async () => { await Promise.resolve(); });
    // 启动 + 在线（网络状态变化）各触发一次，控制器内部单飞合并
    expect(requestSync).toHaveBeenCalledWith("startup");
    expect(requestSync).toHaveBeenCalledWith("network");
    requestSync.mockClear();

    await act(async () => { jest.advanceTimersByTime(59_000); });
    expect(requestSync).not.toHaveBeenCalled();
    await act(async () => { jest.advanceTimersByTime(1_000); });
    expect(requestSync).toHaveBeenCalledTimes(1);
    expect(requestSync).toHaveBeenLastCalledWith("timer");
    await act(async () => { jest.advanceTimersByTime(60_000); });
    expect(requestSync).toHaveBeenCalledTimes(2);

    // 后台不轮询
    AppState.currentState = "background";
    await act(async () => { jest.advanceTimersByTime(180_000); });
    expect(requestSync).toHaveBeenCalledTimes(2);

    // 卸载后停止
    AppState.currentState = "active";
    await screen.unmount();
    await act(async () => { jest.advanceTimersByTime(180_000); });
    expect(requestSync).toHaveBeenCalledTimes(2);
  } finally {
    AppState.currentState = previous;
  }
});

test("设备尚未认证（待注册、待审批、已锁定）时不暴露下发资料同步：启动与定时都不请求", async () => {
  jest.useFakeTimers();
  for (const device of ["registration-required", "pending-approval", "locked"]) {
    const { requestSync, services } = receiptProfileServices({ device });
    mockRuntime = { services };
    const previous = AppState.currentState;
    try {
      AppState.currentState = "active";
      const screen = await render(<RuntimeWorkBridge />);
      await act(async () => { await Promise.resolve(); });
      await act(async () => { jest.advanceTimersByTime(180_000); });
      expect(requestSync).not.toHaveBeenCalled();
      await screen.unmount();
    } finally {
      AppState.currentState = previous;
    }
  }
});

test("离线时不轮询下发资料（继续使用本机资料），联网状态恢复后立即补一次并恢复定时", async () => {
  jest.useFakeTimers();
  const { requestSync, services } = receiptProfileServices();
  mockRuntime = { services };
  mockConnectivity = "offline";
  const previous = AppState.currentState;
  try {
    AppState.currentState = "active";
    const screen = await render(<RuntimeWorkBridge />);
    await act(async () => { await Promise.resolve(); });
    requestSync.mockClear();
    await act(async () => { jest.advanceTimersByTime(180_000); });
    expect(requestSync).not.toHaveBeenCalledWith("timer");
    expect(requestSync).not.toHaveBeenCalledWith("network");

    mockConnectivity = "online";
    await screen.rerender(<RuntimeWorkBridge />);
    await act(async () => { await Promise.resolve(); });
    expect(requestSync).toHaveBeenCalledWith("network");
    requestSync.mockClear();
    await act(async () => { jest.advanceTimersByTime(60_000); });
    expect(requestSync).toHaveBeenCalledWith("timer");
  } finally {
    AppState.currentState = previous;
  }
});

test("回到前台立即触发一次下发资料同步", async () => {
  const { requestSync, services } = receiptProfileServices();
  mockRuntime = { services };
  const previous = AppState.currentState;
  const listeners: ((state: string) => void)[] = [];
  jest.spyOn(AppState, "addEventListener").mockImplementation(((
    _event: string,
    listener: (state: string) => void,
  ) => {
    listeners.push(listener);
    return { remove: jest.fn() };
  }) as never);
  try {
    AppState.currentState = "background";
    await render(<RuntimeWorkBridge />);
    await act(async () => { await Promise.resolve(); });
    requestSync.mockClear();

    await act(async () => { listeners.forEach((listener) => listener("active")); });
    expect(requestSync).toHaveBeenCalledWith("foreground");
  } finally {
    AppState.currentState = previous;
  }
});
