import { afterEach, expect, jest, test } from "@jest/globals";
import {
  act,
  cleanup,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import { AppUpdateGateBridge } from "./app-update-gate-bridge";

let mockRuntime: any;
let mockPathname = "/sales";
const mockPush = jest.fn();

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("expo-router", () => ({
  usePathname: () => mockPathname,
  useRouter: () => ({ push: mockPush }),
}));

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
  }),
}));

afterEach(async () => {
  await cleanup();
  jest.useRealTimers();
  jest.restoreAllMocks();
  mockRuntime = null;
  mockPathname = "/sales";
  mockPush.mockReset();
});

test("required 安全后显示不可关闭的全屏门，UI 只触发持租约的 orchestrator", async () => {
  const updates = updateService({
    key: "native:required:2.0.0",
    kind: "native",
    requirement: "required",
    phase: "blocking",
    blocking: true,
    releaseMessage: "必须升级。",
    appStoreUrl: "https://apps.apple.com/au/app/hb-pos/id123456789",
  });
  updates.performSelectedUpdate.mockResolvedValue({
    action: "open-app-store",
    url: "https://apps.apple.com/au/app/hb-pos/id123456789",
  });
  mockRuntime = { services: { appUpdates: updates } };

  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.getByTestId("app-update-blocking-gate")).toBeTruthy();
  expect(screen.queryByTestId("app-update-dismiss")).toBeNull();
  expect(screen.getByTestId("app-update-settings-entry")).toBeTruthy();
  expect(screen.getByTestId("app-update-support-entry")).toBeTruthy();
  expect(screen.getByTestId("app-update-registration-entry")).toBeTruthy();

  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-action"));
    await new Promise((resolve) => setImmediate(resolve));
  });
  await waitFor(() =>
    expect(updates.performSelectedUpdate).toHaveBeenCalledTimes(1),
  );
});

test("required 业务门提供只读设置、支持和注册入口，且升级动作始终保留", async () => {
  const updates = updateService({
    key: "native:required:2.0.0",
    kind: "native",
    requirement: "required",
    phase: "blocking",
    blocking: true,
    releaseMessage: "必须升级。",
    appStoreUrl: "https://apps.apple.com/au/app/hb-pos/id123456789",
  });
  mockRuntime = { services: { appUpdates: updates } };

  const screen = await render(<AppUpdateGateBridge />);
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-settings-entry"));
  });
  expect(mockPush).toHaveBeenLastCalledWith(
    "/update-recovery?section=settings",
  );
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-support-entry"));
  });
  expect(mockPush).toHaveBeenLastCalledWith(
    "/update-recovery?section=support",
  );
  await act(async () => {
    fireEvent.press(
      screen.getByTestId("app-update-registration-entry"),
    );
  });
  expect(mockPush).toHaveBeenLastCalledWith("/registration");
  expect(screen.getByTestId("app-update-action")).toBeTruthy();
});

test.each(["/registration", "/update-recovery"])(
  "required 在恢复与支持路由 %s 不覆盖页面交互，同时保留升级入口",
  async (pathname) => {
    mockPathname = pathname;
    const updates = updateService({
      key: "ota:required:policy-42:update-id",
      kind: "ota",
      requirement: "required",
      phase: "blocking",
      blocking: true,
      releaseMessage: null,
      appStoreUrl: null,
    });
    mockRuntime = { services: { appUpdates: updates } };

    const screen = await render(<AppUpdateGateBridge />);
    expect(screen.queryByTestId("app-update-blocking-gate")).toBeNull();
    expect(
      screen.getByTestId("app-update-recovery-access").props
        .pointerEvents,
    ).toBe("box-none");
    expect(screen.getByTestId("app-update-action")).toBeTruthy();
  },
);

test("恢复页上更新入口停靠在右下角，不盖住页头的设备注册；注册页仍在右上角", async () => {
  const updates = updateService({
    key: "native:required:2.0.0",
    kind: "native",
    requirement: "required",
    phase: "blocking",
    blocking: true,
    releaseMessage: null,
    appStoreUrl: "https://apps.apple.com/app/id123",
  });
  mockRuntime = { services: { appUpdates: updates } };

  mockPathname = "/update-recovery";
  const recovery = await render(<AppUpdateGateBridge />);
  expect(
    StyleSheet.flatten(
      recovery.getByTestId("app-update-recovery-access").props.style,
    ),
  ).toEqual(
    expect.objectContaining({
      alignItems: "flex-end",
      justifyContent: "flex-end",
    }),
  );
  // 停靠条只保留升级主按钮，不重复提供设置 / 支持 / 注册入口。
  expect(recovery.getByText("打开 App Store")).toBeTruthy();
  expect(recovery.queryByTestId("app-update-settings-entry")).toBeNull();
  expect(recovery.queryByTestId("app-update-dismiss")).toBeNull();
  await recovery.unmount();

  mockPathname = "/registration";
  const registration = await render(<AppUpdateGateBridge />);
  expect(
    StyleSheet.flatten(
      registration.getByTestId("app-update-recovery-access").props.style,
    ),
  ).toEqual(
    expect.objectContaining({
      alignItems: "flex-end",
      justifyContent: "flex-start",
    }),
  );
  expect(registration.getByTestId("app-update-action")).toBeTruthy();
});

test("升级执行中主按钮禁用并显示正在更新，失败后给出稳定提示", async () => {
  const updates = updateService({
    key: "ota:required:policy-42:update-id",
    kind: "ota",
    requirement: "required",
    phase: "blocking",
    blocking: true,
    releaseMessage: null,
    appStoreUrl: null,
  });
  let finish: (value: unknown) => void = () => undefined;
  updates.performSelectedUpdate.mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  mockRuntime = { services: { appUpdates: updates } };

  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.getByText("安装更新")).toBeTruthy();
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-action"));
  });
  expect(screen.getByText("正在更新…")).toBeTruthy();
  expect(
    screen.getByTestId("app-update-action").props.accessibilityState
      ?.disabled,
  ).toBe(true);

  await act(async () => {
    finish({ action: "blocked" });
  });
  await waitFor(() =>
    expect(
      screen.getByText("请先完成当前交易或支付恢复，再执行更新。"),
    ).toBeTruthy(),
  );
  expect(
    screen.getByTestId("app-update-action").props.accessibilityState
      ?.disabled,
  ).toBe(false);
});

test.each(["/login", "/settings", "/sync-history"])(
  "required 不放开完整业务路由 %s",
  async (pathname) => {
    mockPathname = pathname;
    const updates = updateService({
      key: "native:required:2.0.0",
      kind: "native",
      requirement: "required",
      phase: "blocking",
      blocking: true,
      releaseMessage: null,
      appStoreUrl: "https://apps.apple.com/app/id123",
    });
    mockRuntime = { services: { appUpdates: updates } };

    const screen = await render(<AppUpdateGateBridge />);
    expect(screen.getByTestId("app-update-blocking-gate")).toBeTruthy();
    expect(
      screen.queryByTestId("app-update-recovery-access"),
    ).toBeNull();
  },
);

test("optional 主动提示发现新版，但允许稍后处理且不形成全屏阻断", async () => {
  const updates = updateService({
    key: "ota:optional:policy-42:update-id",
    kind: "ota",
    requirement: "optional",
    phase: "prompt",
    blocking: false,
    releaseMessage: null,
    appStoreUrl: null,
  });
  mockRuntime = { services: { appUpdates: updates } };

  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.getByTestId("app-update-optional-prompt")).toBeTruthy();
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-dismiss"));
  });
  await waitFor(() => {
    expect(screen.queryByTestId("app-update-optional-prompt")).toBeNull();
  });
});

test("required 交易未安全时不盖住恢复页面，并持续复查直到安全", async () => {
  jest.useFakeTimers();
  const updates = updateService({
    key: "ota:required:policy-42:update-id",
    kind: "ota",
    requirement: "required",
    phase: "waiting-for-safe",
    blocking: false,
    releaseMessage: null,
    appStoreUrl: null,
  });
  mockRuntime = { services: { appUpdates: updates } };

  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.queryByTestId("app-update-blocking-gate")).toBeNull();
  await act(async () => {
    jest.advanceTimersByTime(1_000);
  });
  expect(updates.refreshSafety).toHaveBeenCalled();
});

function updateService(presentation: any): any {
  return {
    getPresentation: jest.fn(() => presentation),
    subscribePresentation: jest.fn(() => () => undefined),
    refreshSafety: jest.fn(async () => presentation),
    performSelectedUpdate: jest.fn(async () => ({
      action: "none",
      reason: "no-update",
    })),
  };
}
