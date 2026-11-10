import { afterEach, expect, jest, test } from "@jest/globals";
import {
  act,
  cleanup,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";

import {
  AppUpdateRecoveryScreen,
  type AppUpdateRecoveryScreenState,
} from "./app-update-recovery-screen";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    // 页面外壳的顶栏（状态条）用全局文案；这里原样返回键名即可。
    t: (key: string) => key,
  }),
}));

afterEach(async () => {
  await cleanup();
  jest.clearAllMocks();
});

test("设置区只显示白名单诊断并保留设备注册入口", async () => {
  const props = createProps("settings", readyState());
  const screen = await render(<AppUpdateRecoveryScreen {...props} />);

  expect(screen.getByTestId("app-update-recovery-screen")).toBeTruthy();
  expect(screen.getByTestId("app-update-recovery-appVersion").props.children)
    .toBe("1.2.3");
  expect(screen.getByTestId("app-update-recovery-buildNumber").props.children)
    .toBe("101");
  expect(screen.queryByTestId("app-update-recovery-deviceLabel")).toBeNull();
  expect(screen.queryByTestId("app-update-recovery-storeCode")).toBeNull();
  expect(screen.queryByTestId("app-update-recovery-storeName")).toBeNull();
  expect(screen.queryByTestId("app-update-recovery-export")).toBeNull();

  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-recovery-registration"));
  });
  expect(props.onOpenRegistration).toHaveBeenCalledTimes(1);
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-recovery-nav-support"));
  });
  expect(props.onSelectSection).toHaveBeenCalledWith("support");
});

test("支持区可导出同一白名单，导出期间禁用重复操作", async () => {
  const props = createProps("support", readyState(), true);
  const screen = await render(<AppUpdateRecoveryScreen {...props} />);
  const exportButton = await waitFor(() =>
    screen.getByTestId("app-update-recovery-export"),
  );

  expect(exportButton.props.accessibilityState?.disabled).toBe(true);
  await act(async () => {
    fireEvent.press(exportButton);
  });
  expect(props.onExport).not.toHaveBeenCalled();
  expect(screen.getByText("正在导出…")).toBeTruthy();
});

test("支持区显示稳定的导出失败提示并允许再次操作", async () => {
  const props = createProps("support", readyState(), false, true);
  const screen = await render(<AppUpdateRecoveryScreen {...props} />);

  expect(
    screen.getByTestId("app-update-recovery-export-error").props
      .children,
  ).toBe("诊断导出失败，请重试。");
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-recovery-export"));
  });
  expect(props.onExport).toHaveBeenCalledTimes(1);
});

test("诊断失败只提供重试，不暴露完整设置或同步入口", async () => {
  const props = createProps("settings", {
    kind: "error",
    errorCode: "UPDATE_RECOVERY_SNAPSHOT_UNAVAILABLE",
  });
  const screen = await render(<AppUpdateRecoveryScreen {...props} />);

  expect(
    await waitFor(() => screen.getByText("诊断信息暂不可用。")),
  ).toBeTruthy();
  expect(screen.queryByTestId("app-update-recovery-export")).toBeNull();
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-recovery-retry"));
  });
  expect(props.onRetry).toHaveBeenCalledTimes(1);
});

test("当前分区按钮标为选中；两个分区都显示诊断信息，只有支持区多一张导出卡", async () => {
  const settings = await render(
    <AppUpdateRecoveryScreen {...createProps("settings", readyState())} />,
  );
  expect(
    settings.getByTestId("app-update-recovery-nav-settings").props
      .accessibilityState?.selected,
  ).toBe(true);
  expect(
    settings.getByTestId("app-update-recovery-nav-support").props
      .accessibilityState?.selected,
  ).toBe(false);
  expect(settings.getByText("诊断信息")).toBeTruthy();
  expect(settings.queryByText("更新支持")).toBeNull();
  await settings.unmount();

  const support = await render(
    <AppUpdateRecoveryScreen {...createProps("support", readyState())} />,
  );
  expect(
    support.getByTestId("app-update-recovery-nav-support").props
      .accessibilityState?.selected,
  ).toBe(true);
  expect(support.getByText("更新支持")).toBeTruthy();
  expect(support.getByText("诊断信息")).toBeTruthy();
  expect(
    support.getByTestId("app-update-recovery-deviceState").props.children,
  ).toBe("authorized-online");
});

test("加载中不显示诊断与导出，设备注册入口仍在页头", async () => {
  const props = createProps("support", { kind: "loading" });
  const screen = await render(<AppUpdateRecoveryScreen {...props} />);

  expect(screen.queryByTestId("app-update-recovery-appVersion")).toBeNull();
  expect(screen.queryByTestId("app-update-recovery-export")).toBeNull();
  await act(async () => {
    fireEvent.press(screen.getByTestId("app-update-recovery-registration"));
  });
  expect(props.onOpenRegistration).toHaveBeenCalledTimes(1);
});

function createProps(
  section: "settings" | "support",
  state: AppUpdateRecoveryScreenState,
  exporting = false,
  exportError = false,
) {
  return {
    section,
    state,
    exporting,
    exportError,
    onSelectSection: jest.fn(),
    onOpenRegistration: jest.fn(),
    onRetry: jest.fn(),
    onExport: jest.fn(),
  };
}

function readyState(): AppUpdateRecoveryScreenState {
  return {
    kind: "ready",
    snapshot: {
      appVersion: "1.2.3",
      buildNumber: "101",
      runtimeVersion: "1.2.3",
      channel: "pos-ipad-production",
      apiOrigin: "https://pos.example",
      backendState: "reachable",
      deviceState: "authorized-online",
    },
  };
}
