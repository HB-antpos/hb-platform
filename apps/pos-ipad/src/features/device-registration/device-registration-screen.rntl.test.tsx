import { beforeEach, expect, jest, test } from "@jest/globals";
import {
  act,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import Storage from "expo-sqlite/kv-store";
import { StyleSheet } from "react-native";

import { DeviceRegistrationScreen } from "./device-registration-screen";

import type {
  DeviceActivationPreviewResponse,
  DeviceRegistrationStore,
} from "@/core/api/hbpos-api";
import type { DeviceSessionState } from "@/core/security/device-session";
import i18n from "@/i18n";
import { posColors } from "@/ui/theme";

jest.mock("expo-sqlite/kv-store", () => ({
  __esModule: true,
  default: {
    getItemSync: jest.fn(),
    setItem: jest.fn(),
  },
}));

const mockGetItemSync = jest.mocked(Storage.getItemSync);
const mockSetItem = jest.mocked(Storage.setItem);

const activationCode =
  "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ";
const mockPreviewActivationCode = jest.fn<
  (value: string) => Promise<DeviceActivationPreviewResponse>
>();
const mockRedeemActivationCode = jest.fn<
  (input: Readonly<{ activationCode: string }>) => Promise<DeviceSessionState>
>();
const mockRestorePendingActivationCode = jest.fn<() => Promise<string | null>>();
const mockClearPendingActivationCode = jest.fn<() => Promise<void>>();
const mockRegisterAppReview = jest.fn<
  (input: Readonly<{ storeCode: string; provisioningCode: string }>) => Promise<DeviceSessionState>
>();
const mockListRegistrationStores = jest.fn<
  () => Promise<readonly DeviceRegistrationStore[]>
>();
const mockUpdateOperationalState = jest.fn();
const mockRetry = jest.fn<() => Promise<void>>();
const mockServerTest = jest.fn<
  (address: string, signal: AbortSignal) => Promise<boolean>
>();
const mockServerChange = jest.fn<
  (
    address: string,
    signal: AbortSignal,
  ) => Promise<
    | Readonly<{ status: "completed"; apiBaseUrl: string }>
    | Readonly<{
        status: "blocked";
        reason: "pending-local-data" | "candidate-unreachable";
      }>
  >
>();
let mockRuntimeValue: unknown;
let mockStatusStripProps: Readonly<{
  language?: string;
  onSwitchLanguage?: () => void;
}> | null = null;
let mockLatestCameraProps: Readonly<{
  context: string;
  onScan(value: string): void;
  visible: boolean;
}> | null = null;

jest.mock("@expo/vector-icons", () => ({
  MaterialCommunityIcons: () => null,
}));

jest.mock("expo-router", () => ({
  Redirect: () => null,
}));

jest.mock("expo-localization", () => ({
  getLocales: () => [{ languageCode: "zh" }],
}));

jest.mock("expo-status-bar", () => ({
  StatusBar: () => null,
}));

jest.mock("@/ui/shell/status-strip", () => ({
  PosStatusStrip: (props: typeof mockStatusStripProps) => {
    mockStatusStripProps = props;
    return null;
  },
}));

jest.mock("@/features/scanner-camera/camera-scanner-modal", () => ({
  CameraScannerModal: (props: typeof mockLatestCameraProps) => {
    mockLatestCameraProps = props;
    return null;
  },
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntimeValue,
}));

beforeEach(async () => {
  jest.clearAllMocks();
  mockGetItemSync.mockReturnValue(null);
  mockSetItem.mockResolvedValue(undefined);
  mockListRegistrationStores.mockResolvedValue([
    { storeCode: "1002", storeName: "Aspley" },
    { storeCode: "1003", storeName: "Chermside" },
  ]);
  mockPreviewActivationCode.mockResolvedValue({
    isAllowed: true,
    storeCode: "1042",
    storeName: "Sunnybank",
    deviceSystem: "iPadOS",
    expiresAtUtc: "2026-08-27T12:00:00.000Z",
  });
  mockRedeemActivationCode.mockResolvedValue({
    status: "authorized",
    deviceCode: "IPAD-1042-01",
    storeCode: "1042",
  });
  mockRestorePendingActivationCode.mockResolvedValue(null);
  mockClearPendingActivationCode.mockResolvedValue(undefined);
  mockRegisterAppReview.mockResolvedValue({
    status: "authorized",
    deviceCode: "IPAD-REVIEW-01",
    storeCode: "1003",
  });
  mockRetry.mockResolvedValue(undefined);
  mockServerTest.mockResolvedValue(true);
  mockServerChange.mockResolvedValue({
    status: "completed",
    apiBaseUrl: "https://hotbargain.vip/pos-api",
  });
  mockRuntimeValue = {
    retry: mockRetry,
    services: {
      deviceSession: {
        clearPendingActivationCode: mockClearPendingActivationCode,
        getDeviceIdentity: jest.fn(),
        listRegistrationStores: mockListRegistrationStores,
        poll: jest.fn(),
        previewActivationCode: mockPreviewActivationCode,
        redeemActivationCode: mockRedeemActivationCode,
        register: jest.fn(),
        registerAppReview: mockRegisterAppReview,
        restorePendingActivationCode: mockRestorePendingActivationCode,
        reregister: jest.fn(),
      },
      scanner: {
        router: {
          acceptCameraText: jest.fn(() => true),
          acquireContext: jest.fn(() => jest.fn()),
          startCamera: jest.fn(async () => undefined),
          stopCamera: jest.fn(async () => undefined),
        },
      },
      serverConnection: {
        change: mockServerChange,
        getCurrentApiBaseUrl: () => "https://hotbargain.vip/pos-api",
        test: mockServerTest,
      },
    },
    state: {
      backend: "reachable",
      database: "ready",
      device: "registration-required",
      phase: "registration-required",
    },
    updateOperationalState: mockUpdateOperationalState,
  };
  await i18n.changeLanguage("zh");
  mockLatestCameraProps = null;
  mockStatusStripProps = null;
});

test("注册页提供网络检测入口", async () => {
  const onOpenNetworkCheck = jest.fn();
  const screen = await render(
    <DeviceRegistrationScreen onOpenNetworkCheck={onOpenNetworkCheck} />,
  );
  await fireEvent.press(await screen.findByTestId("registration-network-check"));
  expect(onOpenNetworkCheck).toHaveBeenCalledTimes(1);
});

test("待开通码读取损坏时显示恢复错误且不把它当作空记录", async () => {
  mockRestorePendingActivationCode.mockRejectedValue(
    new Error("Device activation recovery data is unavailable."),
  );

  const screen = await render(<DeviceRegistrationScreen />);

  expect(
    await screen.findByText("Device activation recovery data is unavailable."),
  ).toBeTruthy();
});

test("编辑输入框不得清除上一次结果不确定的待恢复开通码", async () => {
  mockRestorePendingActivationCode.mockResolvedValue(activationCode);
  const screen = await render(<DeviceRegistrationScreen />);
  const input = screen.getByTestId("registration-activation-code");
  await waitFor(() => expect(input.props.value).toBe(activationCode));

  await fireEvent.changeText(input, `${activationCode}X`);

  expect(mockClearPendingActivationCode).not.toHaveBeenCalled();
});

test("手动输入先预览服务端权威分店，确认后才兑换", async () => {
  const screen = await render(<DeviceRegistrationScreen />);
  const input = screen.getByTestId("registration-activation-code");

  expect(input.props.returnKeyType).toBe("go");

  await fireEvent.changeText(
    input,
    `  ${activationCode.toLowerCase()}\n`,
  );
  await fireEvent.press(screen.getByTestId("registration-preview"));

  await waitFor(() =>
    expect(mockPreviewActivationCode).toHaveBeenCalledWith(activationCode),
  );
  expect(mockListRegistrationStores).not.toHaveBeenCalled();
  expect(screen.getByText("Sunnybank · 1042")).toBeTruthy();
  expect(screen.getByText("设备平台：iPadOS")).toBeTruthy();
  expect(screen.getByText(/2026/)).toBeTruthy();
  expect(mockRedeemActivationCode).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("registration-redeem"));

  await waitFor(() =>
    expect(mockRedeemActivationCode).toHaveBeenCalledWith({ activationCode }),
  );
});

test("相机只使用私有 device-activation 上下文并在扫码后预览", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.press(screen.getByTestId("registration-scan"));
  expect(mockLatestCameraProps?.context).toBe("device-activation");
  expect(mockLatestCameraProps?.visible).toBe(true);
  await act(async () => {
    mockLatestCameraProps?.onScan(` ${activationCode.toLowerCase()} `);
  });

  await waitFor(() =>
    expect(mockPreviewActivationCode).toHaveBeenCalledWith(activationCode),
  );
  expect(mockListRegistrationStores).not.toHaveBeenCalled();
});

test("中英文切换保留已经输入的设备开通码", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    activationCode,
  );
  // 新布局：语言切换并入外壳顶栏，页面不再有自己的语言按钮。
  expect(screen.queryByTestId("registration-language-switch")).toBeNull();
  expect(mockStatusStripProps?.onSwitchLanguage).toEqual(expect.any(Function));
  await act(async () => {
    mockStatusStripProps?.onSwitchLanguage?.();
  });

  await waitFor(() =>
    expect(screen.getByText("Register device")).toBeTruthy(),
  );
  await waitFor(() =>
    expect(mockSetItem).toHaveBeenCalledWith("hb.pos.language.v1", "en"),
  );
  expect(screen.getByTestId("registration-activation-code").props.value).toBe(
    activationCode,
  );
});

test("未注册设备测试通过后可安全切换服务器并重建 runtime", async () => {
  const screen = await render(<DeviceRegistrationScreen />);
  await fireEvent.press(screen.getByTestId("server-connection-edit"));
  await fireEvent.changeText(
    screen.getByTestId("server-connection-input"),
    "https://hotbargain.top/pos-api",
  );
  await fireEvent.press(screen.getByTestId("server-connection-test"));

  await waitFor(() =>
    expect(mockServerTest).toHaveBeenCalledWith(
      "https://hotbargain.top/pos-api",
      expect.any(AbortSignal),
    ),
  );
  await fireEvent.press(screen.getByTestId("server-connection-save"));
  await fireEvent.press(screen.getByTestId("server-connection-confirm"));

  await waitFor(() =>
    expect(mockServerChange).toHaveBeenCalledWith(
      "https://hotbargain.top/pos-api",
      expect.any(AbortSignal),
    ),
  );
  await waitFor(() => expect(mockRetry).toHaveBeenCalledTimes(1));
});

test("设备重置状态不确定时只显示只读恢复页并禁止重复注册", async () => {
  mockRuntimeValue = {
    ...(mockRuntimeValue as Record<string, unknown>),
    state: {
      backend: "offline",
      database: "ready",
      device: "locked",
      phase: "locked",
    },
  };

  const screen = await render(<DeviceRegistrationScreen />);

  expect(screen.getByTestId("registration-recovery-readonly")).toBeTruthy();
  expect(screen.queryByTestId("registration-store-picker")).toBeNull();
  expect(screen.queryByTestId("registration-activation-code")).toBeNull();
  expect(screen.queryByTestId("registration-preview")).toBeNull();
  expect(screen.queryByTestId("server-connection-edit")).toBeNull();
  expect(mockListRegistrationStores).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("registration-recovery-retry"));
  expect(mockRetry).toHaveBeenCalledTimes(1);
});

test("HBDEV1 保留前缀格式错误时拒绝且不得 fallback 到 App Review", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    "  hbdev1-incomplete  ",
  );
  await fireEvent.press(screen.getByTestId("registration-preview"));

  expect(
    await screen.findByText("请扫描或输入完整的 HBDEV1 设备开通码。"),
  ).toBeTruthy();
  expect(mockPreviewActivationCode).not.toHaveBeenCalled();
  expect(mockListRegistrationStores).not.toHaveBeenCalled();
  expect(mockRegisterAppReview).not.toHaveBeenCalled();
});

test("非 ASCII 空白包围的 HBDEV1 候选仍严格拒绝且不得 fallback", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    "\u00a0hbdev1-incomplete\u00a0",
  );
  await fireEvent.press(screen.getByTestId("registration-preview"));

  expect(
    await screen.findByText("请扫描或输入完整的 HBDEV1 设备开通码。"),
  ).toBeTruthy();
  expect(mockListRegistrationStores).not.toHaveBeenCalled();
  expect(mockRegisterAppReview).not.toHaveBeenCalled();
});

test("统一入口识别 App Review 代码后才加载分店并保留代码大小写", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  expect(mockListRegistrationStores).not.toHaveBeenCalled();
  expect(screen.queryByTestId("registration-app-review-toggle")).toBeNull();
  expect(screen.queryByTestId("registration-app-review-code")).toBeNull();
  expect(screen.queryByText(/App Review/u)).toBeNull();

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    "  Open-Review-Device-xYz  ",
  );
  await fireEvent.press(screen.getByTestId("registration-preview"));

  await waitFor(() =>
    expect(mockListRegistrationStores).toHaveBeenCalledTimes(1),
  );
  expect(mockPreviewActivationCode).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("registration-store-picker"));
  await fireEvent.press(await screen.findByTestId("registration-store-1003"));
  await fireEvent.press(screen.getByTestId("registration-app-review-submit"));

  await waitFor(() =>
    expect(mockRegisterAppReview).toHaveBeenCalledWith({
      storeCode: "1003",
      provisioningCode: "Open-Review-Device-xYz",
    }),
  );
});

test("扫码的 App Review 代码使用同一私有上下文并进入选店阶段", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.press(screen.getByTestId("registration-scan"));
  expect(mockLatestCameraProps?.context).toBe("device-activation");
  await act(async () => {
    mockLatestCameraProps?.onScan(" Scan-Review-Code-AbC ");
  });

  await waitFor(() =>
    expect(mockListRegistrationStores).toHaveBeenCalledTimes(1),
  );
  expect(screen.getByTestId("registration-store-picker")).toBeTruthy();
  expect(screen.getByTestId("registration-activation-code").props.value).toBe(
    " Scan-Review-Code-AbC ",
  );
  expect(mockPreviewActivationCode).not.toHaveBeenCalled();
});

test("编辑已识别代码会回到未分类状态且不清除待恢复开通码", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    "OPEN-REVIEW-DEVICE",
  );
  await fireEvent.press(screen.getByTestId("registration-preview"));
  await waitFor(() =>
    expect(screen.getByTestId("registration-store-picker")).toBeTruthy(),
  );

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    activationCode,
  );

  expect(screen.queryByTestId("registration-store-picker")).toBeNull();
  expect(screen.queryByTestId("registration-app-review-submit")).toBeNull();
  expect(mockClearPendingActivationCode).not.toHaveBeenCalled();
});

test("App Review 分店加载失败后可在同一表单重试", async () => {
  mockListRegistrationStores
    .mockRejectedValueOnce(new Error("temporary store lookup failure"))
    .mockResolvedValueOnce([
      { storeCode: "1003", storeName: "Chermside" },
    ]);
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    "REVIEW-RETRY-CODE",
  );
  await fireEvent.press(screen.getByTestId("registration-preview"));

  expect(
    await screen.findByText("temporary store lookup failure"),
  ).toBeTruthy();
  await fireEvent.press(screen.getByTestId("registration-store-retry"));

  await waitFor(() =>
    expect(mockListRegistrationStores).toHaveBeenCalledTimes(2),
  );
  await waitFor(() =>
    expect(
      screen.getByTestId("registration-store-picker").props.accessibilityState
        .disabled,
    ).toBe(false),
  );
});

test("旧 pending 页面不显示统一注册码入口", async () => {
  const pendingState: DeviceSessionState = {
    status: "pending-approval",
    deviceCode: "IPAD-PENDING-01",
    storeCode: "1002",
  };
  const currentRuntime = mockRuntimeValue as {
    services: { deviceSession: Record<string, unknown> };
  } & Record<string, unknown>;
  mockRuntimeValue = {
    ...currentRuntime,
    services: {
      ...(currentRuntime.services as Record<string, unknown>),
      deviceSession: {
        ...currentRuntime.services.deviceSession,
        poll: jest.fn(async () => pendingState),
      },
    },
    state: {
      backend: "reachable",
      database: "ready",
      device: "pending-approval",
      phase: "pending-approval",
    },
  };

  const screen = await render(<DeviceRegistrationScreen />);

  expect(screen.queryByTestId("registration-scan")).toBeNull();
  expect(screen.queryByTestId("registration-activation-code")).toBeNull();
  expect(screen.queryByTestId("registration-preview")).toBeNull();
  expect(mockListRegistrationStores).not.toHaveBeenCalled();
});

test("注册页按新布局放大：扫码主按钮 88、注册码框与继续 76、服务器连接收成一条", async () => {
  const screen = await render(
    <DeviceRegistrationScreen onOpenNetworkCheck={jest.fn()} />,
  );

  const scan = screen.getByTestId("registration-scan");
  expect(StyleSheet.flatten(scan.props.style)).toMatchObject({
    backgroundColor: posColors.accent,
    minHeight: 88,
  });
  expect(screen.getByText("或手动输入")).toBeTruthy();
  const input = screen.getByTestId("registration-activation-code");
  expect(input.props.secureTextEntry).toBe(true);
  expect(StyleSheet.flatten(input.props.style)).toMatchObject({ height: 76 });
  expect(
    StyleSheet.flatten(screen.getByTestId("registration-preview").props.style),
  ).toMatchObject({ minHeight: 76 });

  // 收起时只有一条：当前地址常显，地址输入要点「修改 / 检查」才展开。
  expect(
    screen.getByTestId("server-connection-current").props.children,
  ).toBe("https://hotbargain.vip/pos-api");
  expect(screen.queryByTestId("server-connection-input")).toBeNull();
  for (const testID of [
    "server-connection-edit",
    "registration-network-check",
  ]) {
    expect(
      StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
    ).toBeGreaterThanOrEqual(56);
  }
  await screen.unmount();
});

test("服务端确认分店后主按钮让给「确认并开通设备」，扫描降为普通按钮", async () => {
  const screen = await render(<DeviceRegistrationScreen />);

  await fireEvent.changeText(
    screen.getByTestId("registration-activation-code"),
    activationCode,
  );
  await fireEvent.press(screen.getByTestId("registration-preview"));

  const redeem = await screen.findByTestId("registration-redeem");
  expect(StyleSheet.flatten(redeem.props.style)).toMatchObject({
    backgroundColor: posColors.accent,
    minHeight: 88,
  });
  expect(
    StyleSheet.flatten(screen.getByTestId("registration-scan").props.style)
      .backgroundColor,
  ).not.toBe(posColors.accent);
  expect(screen.queryByTestId("registration-preview")).toBeNull();
  // 注册码输入框仍在，编辑即可回到未识别状态重新来过。
  expect(screen.getByTestId("registration-activation-code")).toBeTruthy();
  await screen.unmount();
});

test("需要恢复时网络检测入口单独保留，重试按钮与其他状态同尺寸", async () => {
  mockRuntimeValue = {
    ...(mockRuntimeValue as Record<string, unknown>),
    state: {
      backend: "offline",
      database: "ready",
      device: "locked",
      phase: "locked",
    },
  };
  const onOpenNetworkCheck = jest.fn();
  const screen = await render(
    <DeviceRegistrationScreen onOpenNetworkCheck={onOpenNetworkCheck} />,
  );

  expect(screen.queryByTestId("server-connection-panel")).toBeNull();
  await fireEvent.press(screen.getByTestId("registration-network-check"));
  expect(onOpenNetworkCheck).toHaveBeenCalledTimes(1);
  expect(
    StyleSheet.flatten(
      screen.getByTestId("registration-recovery-retry").props.style,
    ),
  ).toMatchObject({ minHeight: 88 });
  await screen.unmount();
});
