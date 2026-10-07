import { afterEach, expect, jest, test } from "@jest/globals";
import {
  act,
  cleanup,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";

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
  expect(screen.getByTestId("handheld-state-required-update")).toBeTruthy();
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

test("Android verified installer 启动成功后不显示 unavailable 错误", async () => {
  const updates = updateService({
    key: "native:Android:required:200",
    kind: "native",
    requirement: "required",
    phase: "blocking",
    blocking: true,
    releaseMessage: "必须升级。",
    platform: "Android",
    appStoreUrl: null,
    downloadUrl: "https://updates.example.test/handheld.apk",
  });
  updates.performSelectedUpdate.mockResolvedValue({
    action: "install-android-apk",
  });
  mockRuntime = { services: { appUpdates: updates } };

  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.getByText("安装更新")).toBeTruthy();
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-action"));
    await new Promise((resolve) => setImmediate(resolve));
  });
  expect(
    screen.queryByText("更新暂不可用，请重试或打开更新支持。"),
  ).toBeNull();
});

test.each(["required", "optional"] as const)(
  "Android %s 更新缺少未知来源安装授权时显示打开设置，且不触发下载安装",
  async (requirement) => {
    const updates = updateService({
      key: `native:Android:${requirement}:200`,
      kind: "native",
      requirement,
      phase: requirement === "required" ? "blocking" : "prompt",
      blocking: requirement === "required",
      releaseMessage: "必须升级。",
      platform: "Android",
      appStoreUrl: null,
      downloadUrl: "https://updates.example.test/handheld.apk",
    });
    updates.getAndroidInstallPermissionStatus.mockResolvedValue("denied");
    mockRuntime = { services: { appUpdates: updates } };

    const screen = await render(<AppUpdateGateBridge />);
    await waitFor(() =>
      expect(screen.getByText("去授权")).toBeTruthy(),
    );
    expect(screen.getByText("请在系统设置中允许 HB POS 安装未知应用后，再次点击安装更新。")).toBeTruthy();

    await act(async () => {
      fireEvent.press(screen.getByTestId("app-update-action"));
      await new Promise((resolve) => setImmediate(resolve));
    });
    expect(updates.openAndroidInstallPermissionSettings).toHaveBeenCalledTimes(1);
    expect(updates.performSelectedUpdate).not.toHaveBeenCalled();
  },
);

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

function updateService(presentation: any, downloadProgress: any = null): any {
  return {
    getPresentation: jest.fn(() => presentation),
    subscribePresentation: jest.fn(() => () => undefined),
    getDownloadProgress: jest.fn(() => downloadProgress),
    subscribeDownloadProgress: jest.fn(() => () => undefined),
    refreshSafety: jest.fn(async () => presentation),
    prepareSelectedUpdate: jest.fn(async () => presentation),
    getAndroidInstallPermissionStatus: jest.fn(async () => "granted"),
    openAndroidInstallPermissionSettings: jest.fn(async () => undefined),
    performSelectedUpdate: jest.fn(async () => ({
      action: "none",
      reason: "no-update",
    })),
  };
}

test("Android 登录页仅在下载完成后提示安装，不显示 App Store 文案", async () => {
  mockPathname = "/login";
  const presentation = {
    key: "native:Android:optional:200",
    kind: "native", platform: "Android", requirement: "optional",
    phase: "prompt", blocking: false, releaseMessage: null,
    appStoreUrl: null, downloadUrl: "https://updates.example.test/handheld.apk",
    downloadState: "downloading",
  };
  const updates = updateService(presentation);
  mockRuntime = { services: { appUpdates: updates } };
  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.queryByTestId("app-update-action")).toBeNull();
  expect(screen.queryByText(/App Store/)).toBeNull();
  expect(screen.getByText("正在后台下载更新，可继续使用")).toBeTruthy();

  updates.getPresentation.mockReturnValue({ ...presentation, downloadState: "ready" });
  await screen.rerender(<AppUpdateGateBridge />);
  expect(screen.getByText("更新已下载，可以安装")).toBeTruthy();
  expect(screen.getByText("安装更新")).toBeTruthy();
  expect(screen.queryByText(/App Store/)).toBeNull();
});

test.each(["downloading", "failed"] as const)(
  "optional %s 状态可以关闭，随后同一目标 ready 仍会提醒",
  async (downloadState) => {
    const presentation = {
      key: "native:Android:optional:200",
      downloadTargetKey: "apk:android:200:hash-a",
      kind: "native", platform: "Android", requirement: "optional",
      phase: "prompt", blocking: false, releaseMessage: null,
      appStoreUrl: null, downloadUrl: "https://updates.example.test/handheld.apk",
      downloadState,
    };
    const updates = updateService(presentation);
    mockRuntime = { services: { appUpdates: updates } };
    const screen = await render(<AppUpdateGateBridge />);

    expect(screen.getByTestId("app-update-download-status")).toBeTruthy();
    await act(async () => {
      fireEvent.press(screen.getByTestId("app-update-dismiss"));
    });
    expect(screen.queryByTestId("app-update-download-status")).toBeNull();

    if (downloadState === "failed") {
      updates.getPresentation.mockReturnValue({ ...presentation, downloadState: "downloading" });
      await screen.rerender(<AppUpdateGateBridge />);
      expect(screen.queryByTestId("app-update-download-status")).toBeNull();
    }

    updates.getPresentation.mockReturnValue({ ...presentation, downloadState: "ready" });
    await screen.rerender(<AppUpdateGateBridge />);
    expect(screen.getByTestId("app-update-optional-prompt")).toBeTruthy();
    expect(screen.getByTestId("app-update-action")).toBeTruthy();
  },
);

test("下载中只展示当前目标的确定进度，旧原生包无进度时保持原文案", async () => {
  const presentation = {
    key: "native:Android:optional:200",
    downloadTargetKey: "apk:android:200:hash-a",
    kind: "native", platform: "Android", requirement: "optional",
    phase: "prompt", blocking: false, releaseMessage: null,
    appStoreUrl: null, downloadUrl: "https://updates.example.test/handheld.apk",
    downloadState: "downloading",
  };
  const updates = updateService(presentation, {
    targetKey: "apk:android:200:hash-a",
    bytesWritten: 12 * 1024 * 1024,
    totalBytes: 48 * 1024 * 1024,
  });
  mockRuntime = { services: { appUpdates: updates } };
  const screen = await render(<AppUpdateGateBridge />);

  expect(screen.getByText("正在后台下载更新，可继续使用")).toBeTruthy();
  expect(screen.getByTestId("app-update-download-progress")).toBeTruthy();
  expect(screen.getByText("25% · 12.0 / 48.0 MB")).toBeTruthy();
  expect(screen.getByRole("progressbar").props.accessibilityValue)
    .toEqual({ min: 0, max: 100, now: 25 });

  // 进度属于别的下载目标（策略已切换）时不能串显示。
  updates.getDownloadProgress.mockReturnValue({
    targetKey: "apk:android:199:hash-old",
    bytesWritten: 1,
    totalBytes: 2,
  });
  await screen.rerender(<AppUpdateGateBridge />);
  expect(screen.queryByTestId("app-update-download-progress")).toBeNull();

  updates.getDownloadProgress.mockReturnValue(null);
  await screen.rerender(<AppUpdateGateBridge />);
  expect(screen.queryByTestId("app-update-download-progress")).toBeNull();
  expect(screen.getByText("正在后台下载更新，可继续使用")).toBeTruthy();
});

test("required 下载中在全屏门内展示确定进度", async () => {
  mockRuntime = { services: { appUpdates: updateService({
    key: "native:Android:required:200", kind: "native", platform: "Android",
    requirement: "required", phase: "blocking", blocking: true,
    downloadState: "downloading", downloadTargetKey: "apk:android:200:hash-a",
    releaseMessage: null, appStoreUrl: null,
  }, {
    targetKey: "apk:android:200:hash-a",
    bytesWritten: 30,
    totalBytes: 40,
  }) } };
  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.getByTestId("app-update-blocking-gate")).toBeTruthy();
  expect(screen.getByText(/^75% · /u)).toBeTruthy();
  expect(screen.queryByTestId("app-update-action")).toBeNull();
});

test("optional pending 关闭后进入 ready 再失败，会重新显示新的失败状态", async () => {
  const presentation = {
    key: "native:Android:optional:200",
    downloadTargetKey: "apk:android:200:hash-a",
    kind: "native", platform: "Android", requirement: "optional",
    phase: "prompt", blocking: false, releaseMessage: null,
    appStoreUrl: null, downloadUrl: "https://updates.example.test/handheld.apk",
    downloadState: "downloading",
  };
  const updates = updateService(presentation);
  mockRuntime = { services: { appUpdates: updates } };
  const screen = await render(<AppUpdateGateBridge />);
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-dismiss"));
  });
  expect(screen.queryByTestId("app-update-download-status")).toBeNull();

  updates.getPresentation.mockReturnValue({ ...presentation, downloadState: "ready" });
  await screen.rerender(<AppUpdateGateBridge />);
  expect(screen.getByTestId("app-update-action")).toBeTruthy();

  updates.getPresentation.mockReturnValue({ ...presentation, downloadState: "failed" });
  await screen.rerender(<AppUpdateGateBridge />);
  expect(screen.getByTestId("app-update-download-status")).toBeTruthy();
});

test("同一 presentation key 换 downloadTargetKey 后重新显示未完成状态", async () => {
  const presentation = {
    key: "native:Android:optional",
    downloadTargetKey: "apk:android:200:hash-a",
    kind: "native", platform: "Android", requirement: "optional",
    phase: "prompt", blocking: false, releaseMessage: null,
    appStoreUrl: null, downloadUrl: "https://updates.example.test/handheld.apk",
    downloadState: "downloading",
  };
  const updates = updateService(presentation);
  mockRuntime = { services: { appUpdates: updates } };
  const screen = await render(<AppUpdateGateBridge />);
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-dismiss"));
  });
  expect(screen.queryByTestId("app-update-download-status")).toBeNull();

  updates.getPresentation.mockReturnValue({
    ...presentation,
    downloadTargetKey: "apk:android:201:hash-b",
  });
  await screen.rerender(<AppUpdateGateBridge />);
  expect(screen.getByTestId("app-update-download-status")).toBeTruthy();
});

test.each(["downloading", "failed"])("required %s 仍保留恢复入口且不提供安装按钮", async (downloadState) => {
  mockRuntime = { services: { appUpdates: updateService({
    key: "native:Android:required:200", kind: "native", platform: "Android",
    requirement: "required", phase: "blocking", blocking: true, downloadState,
    releaseMessage: null, appStoreUrl: null,
  }) } };
  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.getByTestId("app-update-blocking-gate")).toBeTruthy();
  expect(screen.getByTestId("app-update-settings-entry")).toBeTruthy();
  expect(screen.getByTestId("app-update-support-entry")).toBeTruthy();
  expect(screen.getByTestId("app-update-registration-entry")).toBeTruthy();
  expect(screen.queryByTestId("app-update-action")).toBeNull();
  expect(screen.queryByTestId("app-update-dismiss")).toBeNull();
  if (downloadState === "failed") {
    expect(screen.getByTestId("app-update-download-retry")).toBeTruthy();
  }
});

test.each(["downloading", "failed"])("required waiting-for-safe %s 不显示 optional 下载浮层", async (downloadState) => {
  mockRuntime = { services: { appUpdates: updateService({
    key: "native:Android:required:200", kind: "native", platform: "Android",
    requirement: "required", phase: "waiting-for-safe", blocking: false, downloadState,
    releaseMessage: null, appStoreUrl: null,
  }) } };
  const screen = await render(<AppUpdateGateBridge />);
  expect(screen.queryByTestId("app-update-download-status")).toBeNull();
  expect(screen.queryByTestId("app-update-dismiss")).toBeNull();
});
