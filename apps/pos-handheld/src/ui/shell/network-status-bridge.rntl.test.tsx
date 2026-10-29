import { afterEach, beforeEach, describe, expect, jest, test } from "@jest/globals";
import { act, cleanup, render, waitFor } from "@testing-library/react-native";
import * as Network from "expo-network";
import {
  AppState,
  type AppStateEvent,
  type AppStateStatus,
} from "react-native";

import { NetworkStatusBridge } from "./network-status-bridge";
import { usePosShellStore } from "./pos-shell-store";

// 设备网络层：固定返回“设备在线”（模拟 Wi-Fi 正常，后端却已停止的场景）。
jest.mock("expo-network", () => ({
  getNetworkStateAsync: jest.fn(async () => ({
    isConnected: true,
    isInternetReachable: true,
  })),
  addNetworkStateListener: jest.fn(() => ({ remove: jest.fn() })),
}));

// app.config extra：本地 API 地址（与用户环境 192.168.31.246:5003 一致）。
jest.mock("expo-constants", () => ({
  __esModule: true,
  default: {
    expoConfig: {
      extra: {
        hbpos: {
          apiBaseUrl: "http://192.168.31.246:5003/pos-api",
          trustedApiOrigins: [],
        },
      },
    },
  },
}));

// 无持久化 API 配置：走 app.config extra 兜底。
jest.mock("expo-secure-store", () => ({
  getItemAsync: async () => null,
  setItemAsync: async () => undefined,
  deleteItemAsync: async () => undefined,
  WHEN_UNLOCKED: "WHEN_UNLOCKED",
  WHEN_UNLOCKED_THIS_DEVICE_ONLY: "WHEN_UNLOCKED_THIS_DEVICE_ONLY",
}));

let appStateListener: ((state: AppStateStatus) => void) | null = null;

beforeEach(() => {
  usePosShellStore.getState().reset();
  appStateListener = null;
  jest.mocked(Network.getNetworkStateAsync).mockResolvedValue({
    isConnected: true,
    isInternetReachable: true,
  });
  jest.spyOn(AppState, "addEventListener").mockImplementation(
    (eventName: AppStateEvent, handler: (state: AppStateStatus) => void) => {
      if (eventName === "change") {
        appStateListener = handler;
      }
      return { remove: jest.fn() } as never;
    },
  );
});

afterEach(async () => {
  await cleanup();
  jest.restoreAllMocks();
});

test("设备在线但后端已停止（health 非 2xx）时，收银页 connectivity 翻转为 offline", async () => {
  // 后端停止：health 请求失败（非 2xx）。
  const fetchMock = jest.fn(async () => ({ ok: false })) as unknown as typeof fetch;
  (globalThis as { fetch: typeof fetch }).fetch = fetchMock;

  await act(async () => {
    await render(<NetworkStatusBridge />);
  });

  await waitFor(() => {
    expect(usePosShellStore.getState().connectivity).toBe("offline");
  });
  expect(fetchMock).toHaveBeenCalledWith(
    "http://192.168.31.246:5003/pos-api/api/v1/health",
    expect.objectContaining({ method: "GET" }),
  );
});

test("后端恢复（health 2xx）后，收银页 connectivity 翻转为 online", async () => {
  const fetchMock = jest.fn(async () => ({ ok: true })) as unknown as typeof fetch;
  (globalThis as { fetch: typeof fetch }).fetch = fetchMock;

  await act(async () => {
    await render(<NetworkStatusBridge />);
  });

  await waitFor(() => {
    expect(usePosShellStore.getState().connectivity).toBe("online");
  });
});

test("App 回到前台时立即重新探测后端", async () => {
  // 第一次探测失败（后端停止），前台恢复后探测成功（后端已恢复）。
  let reachable = false;
  const fetchMock = jest.fn(
    async () => ({ ok: reachable }),
  ) as unknown as typeof fetch;
  (globalThis as { fetch: typeof fetch }).fetch = fetchMock;

  await act(async () => {
    await render(<NetworkStatusBridge />);
  });
  await waitFor(() => {
    expect(usePosShellStore.getState().connectivity).toBe("offline");
  });

  // 后端恢复，App 回到前台触发重新探测。
  reachable = true;
  await act(async () => {
    appStateListener?.("active");
  });
  await waitFor(() => {
    expect(usePosShellStore.getState().connectivity).toBe("online");
  });
});

describe("周期探测节奏（对齐 WPF #926 夜间降频）", () => {
  async function renderAt(hour: number, reachable: boolean) {
    jest.useFakeTimers({ now: new Date(2026, 9, 27, hour, 0, 0) });
    const fetchMock = jest.fn(
      async () => ({ ok: reachable }),
    ) as unknown as typeof fetch;
    (globalThis as { fetch: typeof fetch }).fetch = fetchMock;
    await act(async () => {
      await render(<NetworkStatusBridge />);
    });
    await act(async () => {
      await jest.advanceTimersByTimeAsync(0);
    });
    return fetchMock;
  }

  afterEach(() => {
    jest.useRealTimers();
  });

  test("白天每 30 秒周期探测一次", async () => {
    const fetchMock = await renderAt(12, true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(30_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  test("夜间且后端可达时放慢到 5 分钟", async () => {
    const fetchMock = await renderAt(23, true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(4 * 60_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(70_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  test("夜间但后端不可达时仍按 30 秒重试，恢复后尽快翻回在线", async () => {
    const fetchMock = await renderAt(23, false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(30_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

test("新 probe 先完成时，旧 probe 结果不得覆盖最新 connectivity", async () => {
  const stale = deferred<{ ok: boolean }>();
  const latest = deferred<{ ok: boolean }>();
  const fetchMock = jest.fn()
    .mockImplementationOnce(() => stale.promise)
    .mockImplementationOnce(() => latest.promise) as unknown as typeof fetch;
  (globalThis as { fetch: typeof fetch }).fetch = fetchMock;

  await act(async () => {
    await render(<NetworkStatusBridge />);
  });
  await waitFor(() => {
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  await act(async () => {
    appStateListener?.("active");
  });
  await waitFor(() => {
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  latest.resolve({ ok: true });
  await waitFor(() => {
    expect(usePosShellStore.getState().connectivity).toBe("online");
  });
  stale.resolve({ ok: false });
  await act(async () => {
    await Promise.resolve();
  });
  expect(usePosShellStore.getState().connectivity).toBe("online");
});

test("卸载后延迟 probe 不得再发布状态", async () => {
  const pending = deferred<{ ok: boolean }>();
  const fetchMock = jest.fn(() => pending.promise) as unknown as typeof fetch;
  (globalThis as { fetch: typeof fetch }).fetch = fetchMock;

  const screen = await render(<NetworkStatusBridge />);
  await waitFor(() => {
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
  const connectivityBeforeUnmount = usePosShellStore.getState().connectivity;
  await screen.unmount();
  pending.resolve({ ok: false });
  await act(async () => {
    await Promise.resolve();
  });

  expect(usePosShellStore.getState().connectivity).toBe(
    connectivityBeforeUnmount,
  );
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
