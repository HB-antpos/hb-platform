import { expect, jest, test } from "@jest/globals";
import { act, render } from "@testing-library/react-native";

import LoginRoute from "../../../app/login";

let mockRuntime: any;
let mockRouteCaptureProps: any;
let mockScreenProps: any;
const mockRouterReplace = jest.fn();
const mockToggleAppLanguage = jest.fn<() => Promise<"en" | "zh">>();

// 聚焦效果由测试手动触发：模拟「登录页聚焦 / 失焦」。
const mockFocusEffects = new Set<() => unknown>();

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  return {
    router: { replace: (...args: unknown[]) => mockRouterReplace(...args) },
    useFocusEffect: (effect: () => unknown) => {
      React.useEffect(() => {
        mockFocusEffects.add(effect);
        return () => {
          mockFocusEffects.delete(effect);
        };
      }, [effect]);
    },
  };
});

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
  }),
}));

jest.mock("@/i18n", () => ({
  toggleAppLanguage: () => mockToggleAppLanguage(),
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("@/features/cashier-login", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    CashierLoginScreen: (props: unknown) => {
      mockScreenProps = props;
      return React.createElement(Text, { testID: "cashier-login" }, "login");
    },
    // 与真实实现一致：就绪（含离线就绪）且设备未锁定。
    isCashierLoginRuntimeReady: (runtime: {
      state: { phase: string; device: string };
      services: unknown;
    }) =>
      (runtime.state.phase === "ready" ||
        runtime.state.phase === "ready-offline") &&
      runtime.state.device !== "locked" &&
      runtime.services !== null,
  };
});

jest.mock("@/ui/scanner/scanner-route-bridge", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    RouteHidScannerCapture: (props: unknown) => {
      mockRouteCaptureProps = props;
      return React.createElement(
        Text,
        { testID: "login-hid-capture" },
        String((props as { enabled?: boolean }).enabled ?? true),
      );
    },
  };
});

test("登录路由只传受限 runtime facade，成功后替换为销售页", async () => {
  mockRuntime = {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      cashierSession: { signIn: jest.fn() },
      updateIdentity: {
        runtimeVersion: "0.2.0",
        updateId: "A1B2C3D4-E5F6-7890",
        isEmbeddedLaunch: false,
      },
      nativeAppVersion: { appVersion: "0.2.0", buildNumber: "12" },
    },
  };
  mockRouteCaptureProps = null;
  mockScreenProps = null;
  mockToggleAppLanguage.mockResolvedValue("zh");
  const screen = await render(<LoginRoute />);

  expect(screen.getByTestId("cashier-login")).toBeTruthy();
  expect(screen.getByTestId("login-hid-capture").props.children).toBe("false");
  expect(mockRouteCaptureProps.enabled).toBe(false);
  expect(mockScreenProps).toMatchObject({
    language: "en",
    runtime: mockRuntime,
    versionInfo: {
      appVersion: "0.2.0",
      buildNumber: "12",
      runtimeVersion: "0.2.0",
      otaVersion: "a1b2c3d4",
    },
  });
  expect(mockScreenProps).not.toHaveProperty("storeCode");
  expect(mockScreenProps).not.toHaveProperty("deviceCode");
  expect(mockScreenProps).not.toHaveProperty("token");

  await act(async () => {
    mockScreenProps.onSwitchLanguage();
    await Promise.resolve();
  });
  expect(mockToggleAppLanguage).toHaveBeenCalledTimes(1);

  await act(async () => {
    mockScreenProps.onManualInputFocusChange(true);
  });
  expect(mockRouteCaptureProps.enabled).toBe(false);

  mockScreenProps.onSuccess();
  expect(mockRouterReplace).toHaveBeenCalledWith("/sales");
});

test.each([
  {
    name: "embedded launch",
    identity: {
      runtimeVersion: "0.2.0",
      updateId: "A1B2C3D4-E5F6-7890",
      isEmbeddedLaunch: true,
    },
  },
  {
    name: "缺少 update id",
    identity: {
      runtimeVersion: "0.2.0",
      updateId: null,
      isEmbeddedLaunch: false,
    },
  },
])("$name 时 OTA 显示 embedded", async ({ identity }) => {
  mockRuntime = {
    state: { phase: "ready", device: "authorized-online" },
    services: { updateIdentity: identity },
  };
  mockScreenProps = null;
  const screen = await render(<LoginRoute />);

  // 旧运行时 mock 没有 nativeAppVersion 时，版本与 build 回退 unknown 而不是崩溃。
  expect(mockScreenProps.versionInfo).toEqual({
    appVersion: "unknown",
    buildNumber: "unknown",
    runtimeVersion: "0.2.0",
    otaVersion: "embedded",
  });
  await screen.unmount();
});

test("更新身份缺失时 Runtime 回退 unknown 且 OTA 显示 embedded", async () => {
  mockRuntime = {
    state: { phase: "starting", device: "unknown" },
    services: null,
  };
  mockScreenProps = null;
  const screen = await render(<LoginRoute />);

  expect(mockScreenProps.versionInfo).toEqual({
    appVersion: "unknown",
    buildNumber: "unknown",
    runtimeVersion: "unknown",
    otaVersion: "embedded",
  });
  await screen.unmount();
});

test("可见输入失焦后延迟恢复 HID，快速重新聚焦会取消恢复", async () => {
  mockRouteCaptureProps = null;
  mockScreenProps = null;
  const screen = await render(<LoginRoute />);

  jest.useFakeTimers();
  try {
    expect(mockRouteCaptureProps.enabled).toBe(false);

    await act(async () => {
      mockScreenProps.onManualInputFocusChange(false);
    });
    expect(mockRouteCaptureProps.enabled).toBe(false);

    await act(async () => {
      mockScreenProps.onManualInputFocusChange(true);
      jest.runOnlyPendingTimers();
    });
    expect(mockRouteCaptureProps.enabled).toBe(false);

    await act(async () => {
      mockScreenProps.onManualInputFocusChange(false);
      jest.runOnlyPendingTimers();
    });
    expect(mockRouteCaptureProps.enabled).toBe(true);
    expect(screen.getByTestId("login-hid-capture").props.children).toBe("true");
  } finally {
    jest.useRealTimers();
  }
});

test("登录路由卸载时清理待恢复的 HID 定时器", async () => {
  mockRouteCaptureProps = null;
  mockScreenProps = null;
  const screen = await render(<LoginRoute />);

  jest.useFakeTimers();
  const clearTimeoutSpy = jest.spyOn(global, "clearTimeout");
  try {
    await act(async () => {
      mockScreenProps.onManualInputFocusChange(false);
    });

    await screen.unmount();
    expect(clearTimeoutSpy).toHaveBeenCalledTimes(1);

    await act(async () => {
      jest.runOnlyPendingTimers();
    });
  } finally {
    clearTimeoutSpy.mockRestore();
    jest.useRealTimers();
  }
});

function attendanceSource() {
  return {
    destroy: jest.fn(),
    getState: jest.fn(),
    start: jest.fn(),
    subscribe: jest.fn(() => () => undefined),
  };
}

async function focusLoginRoute(): Promise<unknown[]> {
  const cleanups: unknown[] = [];
  await act(async () => {
    for (const effect of mockFocusEffects) cleanups.push(effect());
    await Promise.resolve();
  });
  return cleanups;
}

test("登录页聚焦时创建设备级考勤码并启动，失焦时销毁并撤下", async () => {
  const source = attendanceSource();
  const createDeviceQrController = jest.fn(() => source);
  mockRuntime = {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      attendanceAudit: { createPresenter: jest.fn(), createDeviceQrController },
    },
  };
  mockScreenProps = null;
  mockFocusEffects.clear();
  const screen = await render(<LoginRoute />);
  // 还没聚焦：不创建。
  expect(createDeviceQrController).not.toHaveBeenCalled();
  expect(mockScreenProps.attendanceQr).toBeNull();

  const cleanups = await focusLoginRoute();
  expect(createDeviceQrController).toHaveBeenCalledTimes(1);
  expect(createDeviceQrController).toHaveBeenCalledWith();
  expect(source.start).toHaveBeenCalledTimes(1);
  expect(mockScreenProps.attendanceQr).toBe(source);

  // 登录进入收银页 / 去网络检测页（失焦）：销毁，由收银页的考勤码接力。
  await act(async () => {
    for (const cleanup of cleanups) {
      if (typeof cleanup === "function") cleanup();
    }
    await Promise.resolve();
  });
  expect(source.destroy).toHaveBeenCalledTimes(1);
  expect(mockScreenProps.attendanceQr).toBeNull();
  await screen.unmount();
});

test.each([
  { name: "设备被服务端锁定", state: { phase: "ready", device: "locked" } },
  { name: "终端还没就绪", state: { phase: "starting", device: "unknown" } },
])("$name 时登录页不创建考勤码", async ({ state }) => {
  const createDeviceQrController = jest.fn(() => attendanceSource());
  mockRuntime = {
    state,
    services: {
      attendanceAudit: { createPresenter: jest.fn(), createDeviceQrController },
    },
  };
  mockScreenProps = null;
  mockFocusEffects.clear();
  const screen = await render(<LoginRoute />);

  await focusLoginRoute();
  expect(createDeviceQrController).not.toHaveBeenCalled();
  expect(mockScreenProps.attendanceQr).toBeNull();
  await screen.unmount();
});

test("没有考勤服务或创建失败时登录页照常显示、不带考勤码", async () => {
  mockRuntime = {
    state: { phase: "ready", device: "authorized-online" },
    services: {
      attendanceAudit: {
        createPresenter: jest.fn(),
        createDeviceQrController: jest.fn(() => {
          throw new Error("boom");
        }),
      },
    },
  };
  mockScreenProps = null;
  mockFocusEffects.clear();
  const screen = await render(<LoginRoute />);
  await focusLoginRoute();
  expect(mockScreenProps.attendanceQr).toBeNull();
  expect(screen.getByTestId("cashier-login")).toBeTruthy();
  await screen.unmount();

  // 组合根没提供考勤服务（status: unavailable）时同样不创建。
  mockRuntime = {
    state: { phase: "ready", device: "authorized-online" },
    services: { attendanceAudit: { status: "unavailable" } },
  };
  mockScreenProps = null;
  const second = await render(<LoginRoute />);
  await focusLoginRoute();
  expect(mockScreenProps.attendanceQr).toBeNull();
  await second.unmount();
});
