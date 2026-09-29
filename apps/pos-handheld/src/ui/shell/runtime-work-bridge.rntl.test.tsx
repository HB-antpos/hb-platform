import { afterEach, expect, jest, test } from "@jest/globals";
import { act, cleanup, render, waitFor } from "@testing-library/react-native";
import { AppState } from "react-native";

import { RuntimeWorkBridge } from "./runtime-work-bridge";

let mockRuntime: any;

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("./pos-shell-store", () => ({
  usePosShellStore: (selector: (state: { connectivity: string }) => unknown) =>
    selector({ connectivity: "online" }),
}));

afterEach(async () => {
  await cleanup();
  jest.restoreAllMocks();
  jest.useRealTimers();
  mockRuntime = null;
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
