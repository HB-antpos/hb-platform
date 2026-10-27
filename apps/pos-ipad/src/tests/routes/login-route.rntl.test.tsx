import { expect, jest, test } from "@jest/globals";
import { act, render } from "@testing-library/react-native";

import LoginRoute from "../../../app/login";

let mockRuntime: any;
let mockRouteCaptureProps: any;
let mockScreenProps: any;
const mockRouterReplace = jest.fn();
const mockToggleAppLanguage = jest.fn<() => Promise<"en" | "zh">>();

jest.mock("expo-router", () => ({
  router: { replace: (...args: unknown[]) => mockRouterReplace(...args) },
}));

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
