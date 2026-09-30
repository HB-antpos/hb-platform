import { afterEach, beforeEach, describe, expect, it, jest } from "@jest/globals";
import { act, fireEvent, render, waitFor, within } from "@testing-library/react-native";
import { Linking, Platform, StyleSheet } from "react-native";

import { derivePendingWorkBlockers } from "@hb/pos-domain";
import type { SettingsSquareLocation } from "@hb/pos-domain/features/settings/settings-square-setup";

import {
  SETTINGS_APP_UPDATE_PERMISSION,
  SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
  SETTINGS_CATALOG_RESET_PERMISSION,
  SETTINGS_DEVICE_REGISTRATION_PERMISSION,
  SETTINGS_MIN_TOUCH_TARGET,
  SETTINGS_PAYMENT_TERMINAL_PERMISSION,
  SETTINGS_RECEIPT_PRINTER_PERMISSION,
  SETTINGS_VIEW_PERMISSION,
  SettingsPresenter,
  SettingsScreen,
  SettingsUnavailableScreen,
  type SettingsControlPort,
  type SettingsDangerousConfirmation,
  type SettingsDangerousActionResult,
  type SettingsDeviceReregistrationPreflightResult,
  type SettingsDeviceActivationPreviewResponse,
  type SettingsPaymentSettingsInput,
  type SettingsPrinterDevice,
  type SettingsReceiptProfileDraft,
  type SettingsLinklyHealthSnapshot,
  type SettingsLinklyPairingPort,
  type SettingsLinklyPairResult,
  type SettingsLinklySetupControlPort,
  type SettingsLinklyTerminalSelectionSnapshot,
  type SettingsSnapshot,
  type SettingsSquareSetupControlPort,
} from "./index";

import {
  DEFAULT_RECEIPT_PRINTER_SETTINGS,
  type ReceiptPrinterSettings,
} from "@/core/db/pos-settings-repository";
import { usePosSound } from "@/ui/feedback/pos-sound-context";
import { posColors } from "@/ui/theme";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
  }),
}));

jest.mock("@/features/scanner-camera/camera-scanner-modal", () => ({
  CameraScannerModal: () => null,
}));

jest.mock("@/ui/feedback/pos-sound-context", () => ({ usePosSound: jest.fn() }));

const mockUsePosSound = jest.mocked(usePosSound);
const playSound = jest.fn();
const setButtonSoundEnabled = jest.fn();
const setSpecialNodeSoundEnabled = jest.fn();
const mockToggleAppLanguage = jest.fn<() => Promise<"en" | "zh">>();

jest.mock("@/i18n", () => ({
  toggleAppLanguage: () => mockToggleAppLanguage(),
}));

const presenters: SettingsPresenter[] = [];
const DEVICE_ACTIVATION_CODE = `HBDEV1-${"A".repeat(26)}-${"B".repeat(26)}`;

beforeEach(() => {
  jest.clearAllMocks();
  mockToggleAppLanguage.mockResolvedValue("zh");
  mockUsePosSound.mockReturnValue({
    buttonSoundEnabled: true,
    play: playSound,
    setButtonSoundEnabled,
    setSpecialNodeSoundEnabled,
    specialNodeSoundEnabled: false,
  });
});

afterEach(() => {
  for (const presenter of presenters.splice(0)) presenter.destroy();
  jest.restoreAllMocks();
});

describe("SettingsScreen", () => {
  it("设置内容滚动区采用系统键盘避让合同", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );

    expect(screen.getByTestId("settings-content-scroll").props).toMatchObject({
      automaticallyAdjustKeyboardInsets: true,
      keyboardDismissMode: "interactive",
      keyboardShouldPersistTaps: "handled",
    });
  });

  it("整页滚动合同：标题、导航与内容同处页面滚动容器，确认弹窗位于容器外", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    const pageScroll = screen.getByTestId("settings-content-scroll");
    // 导航、设备徽章与内容区都必须在页面滚动容器内，保证小屏手持端整页可滑动。
    // getByTestId 找不到即抛错，无需 toBeTruthy。
    within(pageScroll).getByTestId("settings-nav-general");
    within(pageScroll).getByTestId("settings-device-badge");
    within(pageScroll).getByTestId("settings-workspace");
    within(pageScroll).getByTestId("settings-pane-content-general");

    // 触发危险操作确认，让 Modal 真正进入 visible 状态后再验证其位于滚动容器外。
    await fireEvent.changeText(
      screen.getByTestId("settings-api-address"),
      "http://localhost:5159",
    );
    await fireEvent.press(screen.getByTestId("settings-api-request-change"));
    await screen.findByTestId("settings-confirmation");
    expect(screen.getByTestId("settings-confirmation-modal")).toBeTruthy();
    // 确认弹窗是原生 Modal（portal），不参与页面滚动布局。
    expect(
      within(pageScroll).queryByTestId("settings-confirmation-modal"),
    ).toBeNull();
  });

  it("常规分区提供真实语言/安全设置与符合 48pt 合同的两组音效开关", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    expect(screen.getByTestId("handheld-state-settings-index")).toBeTruthy();
    expect(
      screen.getByTestId("handheld-state-transaction-settings"),
    ).toBeTruthy();
    expect(SETTINGS_MIN_TOUCH_TARGET).toBe(48);
    expect(
      screen.getByText(
        "Single-screen handheld terminal; secondary-screen controls are not available.",
      ),
    ).toBeTruthy();
    expect(
      screen.queryByText(/iPad|customer display|external display|客显/iu),
    ).toBeNull();
    await fireEvent.press(screen.getByTestId("settings-language-switch"));
    expect(mockToggleAppLanguage).toHaveBeenCalledTimes(1);
    playSound.mockClear();

    const buttonToggle = screen.getByTestId("settings-button-sound");
    const specialNodeToggle = screen.getByTestId(
      "settings-special-node-sound",
    );
    expect(screen.getByText("Sound feedback")).toBeTruthy();
    expect(screen.getByText("Button sounds")).toBeTruthy();
    expect(screen.getByText("Special event sounds")).toBeTruthy();
    expect(buttonToggle.props.accessibilityLabel).toBe("Button sounds");
    expect(buttonToggle.props.accessibilityRole).toBe("switch");
    expect(buttonToggle.props.accessibilityState).toEqual(
      expect.objectContaining({ checked: true }),
    );
    expect(specialNodeToggle.props.accessibilityLabel).toBe(
      "Special event sounds",
    );
    expect(specialNodeToggle.props.accessibilityRole).toBe("switch");
    expect(specialNodeToggle.props.accessibilityState).toEqual(
      expect.objectContaining({ checked: false }),
    );
    for (const toggle of [buttonToggle, specialNodeToggle]) {
      expect(
        StyleSheet.flatten(toggle.props.style).minHeight,
      ).toBeGreaterThanOrEqual(SETTINGS_MIN_TOUCH_TARGET);
    }

    await fireEvent(buttonToggle, "valueChange", false);
    expect(setButtonSoundEnabled).toHaveBeenCalledWith(false);
    expect(setSpecialNodeSoundEnabled).not.toHaveBeenCalled();

    await fireEvent(specialNodeToggle, "valueChange", true);
    expect(setSpecialNodeSoundEnabled).toHaveBeenCalledWith(true);
    expect(setButtonSoundEnabled).toHaveBeenCalledTimes(1);
    expect(playSound).not.toHaveBeenCalled();

    await screen.rerender(<SettingsScreen locale="zh" presenter={presenter} />);
    expect(screen.getByText("音效反馈")).toBeTruthy();
    expect(screen.getByText("普通按钮音效")).toBeTruthy();
    expect(screen.getByText("特殊节点音效")).toBeTruthy();
    expect(
      screen.getByText(
        "覆盖按钮、按键、页面导航和危险操作；开启时播放一次示例，关闭后保持静音。",
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "覆盖商品查询、加购/累加、未找到和受阻结果；开启时播放一次示例，关闭后保持静音。",
      ),
    ).toBeTruthy();
  });

  it("设置导航与危险操作分别使用 navigate/danger 音", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    expect(playSound).toHaveBeenCalledWith("navigate");

    await fireEvent.press(screen.getByTestId("settings-nav-general"));
    await fireEvent.press(screen.getByTestId("settings-catalog-reset"));
    expect(playSound).toHaveBeenLastCalledWith("danger");
  });

  it("运行时不可用页按当前语言显示", async () => {
    const onBack = jest.fn();
    const screen = await render(
      <SettingsUnavailableScreen locale="zh" onBack={onBack} />,
    );
    expect(screen.getByText("设置服务暂不可用")).toBeTruthy();
    expect(screen.queryByText("Settings unavailable")).toBeNull();
  });

  it("英文界面只呈现英文五分区、单列竖屏，并保持导航与主要操作至少 48pt", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const onBack = jest.fn();
    const screen = await render(
      <SettingsScreen locale="en" onBack={onBack} presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    expect(screen.getByText("Settings")).toBeTruthy();
    expect(screen.queryByText("设置")).toBeNull();
    expect(
      StyleSheet.flatten(screen.getByTestId("settings-workspace").props.style)
        .flexDirection,
    ).toBe("column");
    expect(screen.getAllByTestId(/^settings-nav-/)).toHaveLength(5);

    for (const testID of [
      "settings-back",
      "settings-nav-general",
      "settings-nav-payments",
      "settings-nav-peripherals",
      "settings-nav-device",
      "settings-nav-hardware",
      "settings-catalog-download",
      "settings-api-request-change",
    ]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
      ).toBeGreaterThanOrEqual(SETTINGS_MIN_TOUCH_TARGET);
    }

    await fireEvent.press(screen.getByTestId("settings-back"));
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it("当前终端卡片按分店名称、设备代码、分店代码展示完整身份", async () => {
    const storeName = "Brisbane Central Shopping Centre Flagship Store";
    const port = new ScreenSettingsPort();
    port.snapshotValue = {
      ...snapshot(),
      device: {
        ...snapshot().device,
        storeName,
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    const card = screen.getByTestId("settings-device-badge");
    expect(
      card.children
        .slice(1)
        .map((child) => typeof child === "string" ? child : child.props.testID),
    ).toEqual([
      "settings-device-store-name",
      "settings-device-code",
      "settings-device-store-code",
    ]);
    expect(
      screen.getByTestId("settings-device-store-name").props,
    ).toEqual(
      expect.objectContaining({
        accessibilityLabel: storeName,
        ellipsizeMode: "tail",
        numberOfLines: 2,
      }),
    );
    expect(screen.getByTestId("settings-device-code").props.children).toBe(
      "POS-01",
    );
    expect(screen.getByTestId("settings-device-store-code").props.children).toBe(
      "BNE-01",
    );
  });

  it("当前终端缺少分店名称时显示占位符且不拿分店代码回退", async () => {
    const port = new ScreenSettingsPort();
    port.snapshotValue = {
      ...snapshot(),
      device: {
        ...snapshot().device,
        storeName: "",
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    expect(
      screen.getByTestId("settings-device-store-name").props.children,
    ).toBe("—");
    expect(
      screen.getByTestId("settings-device-store-name").props.children,
    ).not.toBe("BNE-01");
    expect(screen.getByTestId("settings-device-store-code").props.children).toBe(
      "BNE-01",
    );
  });

  it("本地后端快捷按钮只填入开发地址，仍需显式申请切换", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-api-use-local"));

    await waitFor(() =>
      expect(screen.getByTestId("settings-api-address").props.value).toBe(
        "http://192.168.31.246:5003",
      ),
    );
    expect(
      StyleSheet.flatten(
        screen.getByTestId("settings-api-use-local").props.style,
      ).minHeight,
    ).toBeGreaterThanOrEqual(SETTINGS_MIN_TOUCH_TARGET);
    expect(
      StyleSheet.flatten(screen.getByTestId("settings-api-actions").props.style)
        .flexWrap,
    ).toBe("wrap");
    expect(port.apiAddresses).toEqual([]);
    expect(screen.queryByTestId("settings-confirmation")).toBeNull();

    await fireEvent.press(screen.getByTestId("settings-api-request-change"));
    await screen.findByTestId("settings-confirmation");
    expect(screen.getByText(/http:\/\/192\.168\.31\.246:5003/)).toBeTruthy();
    expect(port.apiAddresses).toEqual([]);
  });

  it("远程后端快捷按钮只填入生产地址，仍需显式申请切换", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-api-use-remote"));

    await waitFor(() =>
      expect(screen.getByTestId("settings-api-address").props.value).toBe(
        "https://hotbargain.vip/pos-api",
      ),
    );
    expect(
      StyleSheet.flatten(
        screen.getByTestId("settings-api-use-remote").props.style,
      ).minHeight,
    ).toBeGreaterThanOrEqual(SETTINGS_MIN_TOUCH_TARGET);
    expect(port.apiAddresses).toEqual([]);
    expect(screen.queryByTestId("settings-confirmation")).toBeNull();

    await fireEvent.press(screen.getByTestId("settings-api-request-change"));
    await screen.findByTestId("settings-confirmation");
    expect(screen.getByText(/https:\/\/hotbargain\.vip\/pos-api/)).toBeTruthy();
    expect(port.apiAddresses).toEqual([]);
  });

  it("测试连接按钮检查候选地址并保留当前后端", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-api-use-local"));
    await fireEvent.press(screen.getByTestId("settings-api-test"));

    await waitFor(() =>
      expect(port.apiAddressTests).toEqual(["http://192.168.31.246:5003"]),
    );
    expect(screen.getByText(/连接成功/)).toBeTruthy();
    expect(port.apiAddresses).toEqual([]);
    expect(presenter.getState().apiBaseUrl).toBe(
      "https://hotbargain.vip/pos-api",
    );
    expect(screen.queryByTestId("settings-confirmation")).toBeNull();
  });

  it("危险操作确认弹窗只声明应用支持的竖屏方向", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.changeText(
      screen.getByTestId("settings-api-address"),
      "http://localhost:5159",
    );
    await fireEvent.press(screen.getByTestId("settings-api-request-change"));

    await screen.findByTestId("settings-confirmation");
    expect(
      screen.getByTestId("settings-confirmation-modal").props
        .supportedOrientations,
    ).toEqual(["portrait", "portrait-upside-down"]);
  });

  it("设置目录卡片恢复共享真实进度，刷新中可返回但阻断下载、重置与 API 切换", async () => {
    const port = new ScreenSettingsPort();
    port.publishCatalogRefresh({
      kind: "running",
      storeCode: "BNE-01",
      progress: {
        currentStep: "products",
        overallPercent: 35,
        elapsedMilliseconds: 116_000,
        steps: [
          { step: "prepare", percent: 100 },
          {
            step: "products",
            percent: 25,
            completedItemCount: 500,
            totalItemCount: 2_000,
            completedPageCount: 1,
            totalPageCount: 4,
          },
          { step: "promotions", percent: 0 },
          { step: "activate", percent: 0 },
        ],
      },
    });
    port.snapshotValue = {
      ...snapshot(),
      appUpdate: {
        ...snapshot().appUpdate,
        restartAvailable: true,
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const onBack = jest.fn();
    const screen = await render(
      <SettingsScreen locale="zh" onBack={onBack} presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    expect(screen.getByTestId("settings-catalog-refresh-state")).toBeTruthy();
    expect(
      screen.getByTestId("settings-catalog-refresh-progress").props
        .accessibilityValue,
    ).toEqual({ min: 0, max: 100, now: 35 });
    expect(screen.getByText("当前步骤：下载并校验商品")).toBeTruthy();
    expect(screen.getByText("已用时：01:56")).toBeTruthy();
    expect(screen.getByText(/500 \/ 2000/)).toBeTruthy();
    expect(screen.getByText(/1 \/ 4 页/)).toBeTruthy();
    expect(screen.getByText(/可离开本页.*应用内继续刷新/)).toBeTruthy();
    expect(
      screen.getByTestId("settings-catalog-download").props.accessibilityState
        .disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-catalog-reset").props.accessibilityState
        .disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-api-request-change").props
        .accessibilityState.disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-update-restart").props.accessibilityState
        .disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-back").props.accessibilityState.disabled,
    ).toBe(false);

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    await screen.findByTestId("settings-pane-content-payments");
    expect(
      screen.getByTestId("settings-payment-save").props.accessibilityState
        .disabled,
    ).toBe(true);

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await screen.findByTestId("settings-pane-content-device");
    expect(
      screen.getByTestId("settings-reregister-preview").props
        .accessibilityState.disabled,
    ).toBe(true);
    expect(screen.queryByTestId("settings-reregister-request")).toBeNull();

    await fireEvent.press(screen.getByTestId("settings-back"));
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it("重新进入设置后显示共享目录完成摘要与安全告警码", async () => {
    const port = new ScreenSettingsPort();
    port.publishCatalogRefresh({
      kind: "warning",
      storeCode: "BNE-01",
      summary: {
        snapshotId: "catalog-background",
        catalogVersion: "v-background",
        itemCount: 63,
        activatedAt: "2026-07-29T02:00:00.000Z",
      },
      warningCode: "catalog-runtime-reload-failed",
      progress: {
        currentStep: "activate",
        overallPercent: 100,
        elapsedMilliseconds: 121_000,
        steps: [
          { step: "prepare", percent: 100 },
          { step: "products", percent: 100 },
          { step: "promotions", percent: 100 },
          { step: "activate", percent: 100 },
        ],
      },
    });
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    expect(screen.getByText("catalog-background")).toBeTruthy();
    expect(screen.getByText("63")).toBeTruthy();
    expect(screen.getByText(/目录已启用，但有安全警告/)).toBeTruthy();
    expect(
      screen.getByText(/catalog-runtime-reload-failed/),
    ).toBeTruthy();
  });

  it("重新进入设置后显示共享目录失败摘要，保留旧目录且不泄露异常详情", async () => {
    const port = new ScreenSettingsPort();
    port.publishCatalogRefresh({
      kind: "failed",
      storeCode: "BNE-01",
      errorCode: "catalog-refresh-network-failed",
      progress: {
        currentStep: "prepare",
        overallPercent: 0,
        elapsedMilliseconds: 14_000,
        steps: [
          { step: "prepare", percent: 0 },
          { step: "products", percent: 0 },
          { step: "promotions", percent: 0 },
          { step: "activate", percent: 0 },
        ],
      },
    });
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    expect(screen.getByText("catalog-current")).toBeTruthy();
    expect(screen.getByText(/目录刷新未完成，旧目录仍可用/)).toBeTruthy();
    expect(
      screen.getByText(/catalog-refresh-network-failed/),
    ).toBeTruthy();
    expect(screen.queryByText(/Bearer|https?:\/\//)).toBeNull();
  });

  it("Production Square 使用只读加载选择链，Sandbox 明确禁用 Device Code", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    await screen.findByTestId("settings-pane-content-payments");

    expect(screen.getByTestId("settings-square-token-status")).toBeTruthy();
    expect(
      screen.getByTestId("settings-square-location-load").props
        .accessibilityRole,
    ).toBe("button");
    expect(
      screen.getByTestId("settings-square-location-select").props
        .accessibilityRole,
    ).toBe("button");
    expect(
      screen.getByTestId("settings-square-device-load").props
        .accessibilityRole,
    ).toBe("button");
    expect(
      screen.getByTestId("settings-square-device-select").props
        .accessibilityRole,
    ).toBe("button");
    expect(
      StyleSheet.flatten(
        screen.getByTestId("settings-square-location-row").props.style,
      ).flexWrap,
    ).toBe("wrap");
    expect(
      StyleSheet.flatten(
        screen.getByTestId("settings-square-location-select").props.style,
      ).minWidth,
    ).toBeLessThanOrEqual(140);
    expect(
      StyleSheet.flatten(
        screen.getByTestId("settings-square-location-select").props.style,
      ).minHeight,
    ).toBeGreaterThanOrEqual(SETTINGS_MIN_TOUCH_TARGET);
    expect(screen.queryByLabelText("Square 门店位置 ID")).toBeNull();
    expect(screen.queryByLabelText("Square 终端设备 ID")).toBeNull();
    expect(
      screen.getByTestId("settings-square-device-code-create").props
        .accessibilityState.disabled,
    ).toBe(false);
    expect(
      screen.getByTestId("settings-square-device-code-refresh").props
        .accessibilityState.disabled,
    ).toBe(true);

    await fireEvent.press(
      screen.getByTestId("settings-square-location-load"),
    );
    await screen.findByTestId(
      "settings-square-location-picker-option-location-1",
    );
    expect(screen.getByText("已配置且已启用")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId(
        "settings-square-location-picker-option-location-1",
      ),
    );
    expect(screen.getByText("location-1 · ACTIVE")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("settings-square-device-load"));
    await screen.findByTestId(
      "settings-square-device-picker-option-device-1",
    );
    await fireEvent.press(
      screen.getByTestId("settings-square-device-picker-option-device-1"),
    );
    expect(screen.getByText("device-1 · SQ-01 · ENABLED")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("settings-square-device-code-load"),
    );
    const currentCode = await screen.findByTestId(
      "settings-square-device-code-device-code-1",
    );
    expect(screen.getByText("PAIR-01 · PAIRED")).toBeTruthy();
    await fireEvent.press(currentCode);
    expect(
      screen.getByTestId("settings-square-device-code-device-code-1").props
        .accessibilityState.selected,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-square-device-code-refresh").props
        .accessibilityState.disabled,
    ).toBe(false);

    await fireEvent.press(screen.getByTestId("settings-square-sandbox"));

    expect(
      screen.getByText(
        "Sandbox 使用 Square 官方测试终端，无需创建或配对 Device Code。",
      ),
    ).toBeTruthy();
    expect(
      screen.getByTestId("settings-square-device-code-create").props
        .accessibilityState.disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-square-device-code-refresh").props
        .accessibilityState.disabled,
    ).toBe(true);
  });

  it("Square runtime 未就绪时仍允许首次配置，但阻止测试", async () => {
    const port = new ScreenSettingsPort();
    const current = snapshot();
    port.snapshotValue = {
      ...current,
      paymentProvider: null,
      square: {
        ...current.square,
        available: false,
        blockerCode: "SQUARE_CONFIGURATION_MISSING",
        deviceId: "",
        locationId: "",
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    expect(
      screen.getByTestId("settings-payment-provider-square").props
        .accessibilityState.disabled,
    ).toBe(false);
    await fireEvent.press(
      screen.getByTestId("settings-payment-provider-square"),
    );
    expect(screen.queryByText(/运行时未配置/)).toBeNull();
    expect(
      screen.getByTestId("settings-square-location-load").props
        .accessibilityState.disabled,
    ).toBe(false);
    expect(
      screen.getByTestId("settings-square-test").props.accessibilityState
        .disabled,
    ).toBe(true);
  });

  it.each([
    "SQUARE_CONFIGURATION_INVALID",
    "SQUARE_CONFIGURATION_LOAD_FAILED",
  ])("Square setup 可用时仍显示真实 runtime 故障 %s", async (blockerCode) => {
    const port = new ScreenSettingsPort();
    const current = snapshot();
    port.snapshotValue = {
      ...current,
      square: {
        ...current.square,
        available: false,
        blockerCode,
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    expect(screen.getByText(new RegExp(blockerCode))).toBeTruthy();
  });

  it("Square 加载位置为空或失败时在选择框内显示明确状态", async () => {
    const emptyPort = new ScreenSettingsPort();
    emptyPort.squareLocations = Object.freeze([]);
    const emptyPresenter = createPresenter(emptyPort);
    await emptyPresenter.load();
    const emptyScreen = await render(
      <SettingsScreen locale="zh" presenter={emptyPresenter} />,
    );
    await fireEvent.press(emptyScreen.getByTestId("settings-nav-payments"));
    await fireEvent.press(
      emptyScreen.getByTestId("settings-square-location-load"),
    );
    expect(
      await emptyScreen.findAllByText("服务端未返回 Square 门店位置。"),
    ).not.toHaveLength(0);
    expect(
      emptyScreen.getByTestId("settings-square-location-select").props
        .accessibilityState.disabled,
    ).toBe(true);

    emptyScreen.unmount();
    const failedPort = new ScreenSettingsPort();
    failedPort.squareLocationLoadFailure = true;
    const failedPresenter = createPresenter(failedPort);
    await failedPresenter.load();
    const failedScreen = await render(
      <SettingsScreen locale="zh" presenter={failedPresenter} />,
    );
    await fireEvent.press(failedScreen.getByTestId("settings-nav-payments"));
    await fireEvent.press(
      failedScreen.getByTestId("settings-square-location-load"),
    );
    expect(
      await failedScreen.findAllByText("无法加载 Square 设置，请重试。"),
    ).not.toHaveLength(0);
    expect(
      failedScreen.getByTestId("settings-square-location-select").props
        .accessibilityState.disabled,
    ).toBe(true);
  });

  it("支付页只编辑公开 Square/Linkly 选择，不出现 token、secret 或密码输入", async () => {
    const port = new ScreenSettingsPort();
    port.snapshotValue = { ...snapshot(), paymentProvider: null };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    await waitFor(() =>
      expect(screen.getByTestId("settings-pane-content-payments")).toBeTruthy(),
    );
    expect(screen.getAllByText("Square")).toHaveLength(2);
    expect(screen.getAllByText("Linkly")).toHaveLength(2);
    expect(screen.queryByText(/token/i)).toBeNull();
    expect(screen.queryByText(/secret/i)).toBeNull();
    expect(screen.queryByText(/密码|password/i)).toBeNull();
    expect(
      screen.getByText(
        "Select exactly one terminal provider; card payments remain disabled when no available provider is selected.",
      ),
    ).toBeTruthy();
    expect(screen.queryByText(/必须明确选择一个终端提供方/)).toBeNull();
    expect(
      screen.getByTestId("settings-payment-provider-square").props
        .accessibilityState.selected,
    ).toBe(false);
    expect(
      screen.getByTestId("settings-payment-provider-linkly").props
        .accessibilityState.selected,
    ).toBe(false);

    await fireEvent.press(screen.getByTestId("settings-payment-save"));
    expect(screen.queryByTestId("settings-confirmation")).toBeNull();
    await screen.findByText(/\[payment-settings-invalid\]/);

    await fireEvent.press(
      screen.getByTestId("settings-payment-provider-square"),
    );
    await fireEvent.press(screen.getByTestId("settings-square-sandbox"));
    await fireEvent.press(
      screen.getByTestId("settings-square-location-load"),
    );
    await screen.findByTestId(
      "settings-square-location-picker-option-location-2",
    );
    await fireEvent.press(
      screen.getByTestId(
        "settings-square-location-picker-option-location-2",
      ),
    );
    await fireEvent.press(screen.getByTestId("settings-square-device-load"));
    await screen.findByTestId(
      "settings-square-device-picker-option-device-2",
    );
    await fireEvent.press(
      screen.getByTestId("settings-square-device-picker-option-device-2"),
    );
    await fireEvent.press(screen.getByTestId("settings-linkly-sandbox"));
    await fireEvent.press(screen.getByTestId("settings-payment-save"));

    await screen.findByTestId("settings-confirmation");
    expect(port.savedPayments).toEqual([]);
    await fireEvent.press(screen.getByTestId("settings-confirm"));

    expect(port.savedPayments).toEqual([
      {
        provider: "square",
        square: {
          environment: "Sandbox",
          locationId: "location-2",
          deviceId: "device-2",
        },
        linkly: null,
      },
    ]);
    await screen.findByText(/\[payment-settings-saved\]/);
    expect(screen.getByText("Payment settings saved")).toBeTruthy();
    expect(screen.queryByText("支付终端设置已保存")).toBeNull();
    expect(screen.getByText("Gold Coast")).toBeTruthy();
    expect(screen.getByText("Backup Terminal")).toBeTruthy();
    expect(screen.queryByLabelText("Square location ID")).toBeNull();
    expect(screen.queryByLabelText("Square device ID")).toBeNull();
  });

  it("Linkly 缺少终端密钥时仍可先配对，刷新 ready、测试后才保存", async () => {
    const port = new ScreenSettingsPort();
    const current = snapshot();
    port.snapshotValue = {
      ...current,
      linkly: {
        available: false,
        blockerCode: "LINKLY_CONFIGURATION_MISSING",
        environment: "Production",
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    await waitFor(() =>
      expect(screen.getByTestId("settings-pane-content-payments")).toBeTruthy(),
    );
    expect(screen.queryByText(/运行时未配置/)).toBeNull();
    expect(
      screen.getByTestId("settings-payment-provider-linkly").props
        .accessibilityState.disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-linkly-sandbox").props.accessibilityState
        .disabled,
    ).toBe(false);
    expect(
      screen.getByTestId("settings-linkly-test").props.accessibilityState
        .disabled,
    ).toBe(true);
    await fireEvent.press(screen.getByTestId("settings-linkly-sandbox"));
    await fireEvent.changeText(
      screen.getByTestId("settings-linkly-pair-code"),
      "123456",
    );
    expect(
      screen.getByTestId("settings-linkly-pair").props.accessibilityState
        .disabled,
    ).toBe(false);
    await fireEvent.press(screen.getByTestId("settings-linkly-pair"));
    await screen.findByTestId("settings-confirmation");
    await fireEvent.press(screen.getByTestId("settings-confirm"));
    await screen.findByText("Linkly 终端已配对；状态已刷新");

    await fireEvent.press(screen.getByTestId("settings-linkly-test"));
    await screen.findByText("支付通道可用");
    await fireEvent.press(
      screen.getByTestId("settings-payment-provider-linkly"),
    );
    await fireEvent.press(screen.getByTestId("settings-payment-save"));
    await screen.findByTestId("settings-confirmation");
    await fireEvent.press(screen.getByTestId("settings-confirm"));

    expect(port.savedPayments).toEqual([
      {
        provider: "linkly",
        square: null,
        linkly: { environment: "Sandbox" },
      },
    ]);
  });

  it("Linkly 多终端列表显示 Lane 状态并可持久切换后配对指定终端", async () => {
    const port = new ScreenSettingsPort();
    port.linklySetup.multiTerminal = true;
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    await screen.findByTestId("settings-linkly-terminal-terminal-1");
    expect(screen.getByText("前台 · Lane 1")).toBeTruthy();
    expect(screen.getByText("退货台 · Lane 2")).toBeTruthy();
    expect(
      screen.getByTestId("settings-linkly-terminal-terminal-2").props
        .accessibilityState.disabled,
    ).toBe(false);

    await fireEvent.press(
      screen.getByTestId("settings-linkly-terminal-terminal-2"),
    );
    await waitFor(() =>
      expect(port.linklySetup.selectedTerminalId).toBe("terminal-2"),
    );
    await fireEvent.changeText(
      screen.getByTestId("settings-linkly-pair-code"),
      "654321",
    );
    await fireEvent.press(screen.getByTestId("settings-linkly-pair"));
    await fireEvent.press(await screen.findByTestId("settings-confirm"));

    expect(port.linklyPairing.pairCalls).toEqual([
      {
        environment: "Production",
        terminalId: "terminal-2",
        pairCode: "654321",
      },
    ]);
  });

  it("Linkly 忙碌终端仍可持久预选", async () => {
    const port = new ScreenSettingsPort();
    port.linklySetup.multiTerminal = true;
    port.linklySetup.busyTerminalId = "terminal-2";
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    const busyTerminal = await screen.findByTestId(
      "settings-linkly-terminal-terminal-2",
    );
    expect(busyTerminal.props.accessibilityState.disabled).toBe(false);
    await fireEvent.press(busyTerminal);

    await waitFor(() =>
      expect(port.linklySetup.selectedTerminalId).toBe("terminal-2"),
    );
    expect(screen.getByText("正在处理其他交易")).toBeTruthy();
  });

  it("Linkly 配置无效或读取失败时设置页仍保持禁用", async () => {
    for (const blockerCode of [
      "LINKLY_CONFIGURATION_INVALID",
      "LINKLY_CONFIGURATION_LOAD_FAILED",
    ]) {
      const port = new ScreenSettingsPort();
      const current = snapshot();
      port.snapshotValue = {
        ...current,
        paymentProvider: null,
        linkly: {
          available: false,
          blockerCode,
          environment: "Production",
        },
      };
      const presenter = createPresenter(port);
      await presenter.load();
      const screen = await render(
        <SettingsScreen locale="zh" presenter={presenter} />,
      );
      await screen.findByTestId("settings-pane-content-general");

      await fireEvent.press(screen.getByTestId("settings-nav-payments"));
      await waitFor(() =>
        expect(
          screen.getByTestId("settings-pane-content-payments"),
        ).toBeTruthy(),
      );
      expect(
        screen.getByTestId("settings-payment-provider-linkly").props
          .accessibilityState.disabled,
      ).toBe(true);
      expect(
        screen.getByTestId("settings-linkly-test").props.accessibilityState
          .disabled,
      ).toBe(true);

      await fireEvent.press(
        screen.getByTestId("settings-payment-provider-linkly"),
      );
      expect(presenter.getState().paymentProviderDraft).toBeNull();
      screen.unmount();
    }
  });

  it("Linkly Backend Async 显示 health/配对状态，并把配对纳入确认后危险操作", async () => {
    const port = new ScreenSettingsPort();
    port.snapshotValue = {
      ...snapshot(),
      paymentProvider: null,
      linkly: {
        available: false,
        blockerCode: "LINKLY_CONFIGURATION_MISSING",
        environment: "Production",
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await fireEvent.press(screen.getByTestId("settings-nav-payments"));

    expect(screen.queryByText(/Runtime unavailable/)).toBeNull();
    expect(screen.getByTestId("settings-linkly-store-credentials")).toBeTruthy();
    expect(screen.getByText(/Ready.*STORE-01/)).toBeTruthy();
    expect(screen.getByTestId("settings-linkly-current-pairing")).toBeTruthy();
    expect(screen.getAllByText("Not paired").length).toBeGreaterThan(0);
    expect(screen.getByTestId("settings-linkly-backend-ready")).toBeTruthy();
    expect(screen.getByText("Not ready")).toBeTruthy();
    expect(screen.getByTestId("settings-linkly-refresh")).toBeTruthy();
    expect(screen.getByTestId("settings-linkly-pair-code")).toBeTruthy();
    expect(screen.getByText(/FUNC.*8880/)).toBeTruthy();

    await fireEvent.changeText(
      screen.getByTestId("settings-linkly-pair-code"),
      "123456",
    );
    expect(
      screen.getByTestId("settings-linkly-pair").props.accessibilityState
        .disabled,
    ).toBe(false);

    await fireEvent.press(screen.getByTestId("settings-linkly-pair"));
    expect(screen.getByTestId("settings-confirmation")).toBeTruthy();
    expect(port.linklyPairing.pairCalls).toEqual([]);
    await fireEvent.press(screen.getByTestId("settings-confirm"));

    await waitFor(() =>
      expect(port.linklyPairing.pairCalls).toEqual([
        {
          environment: "Production",
          terminalId: "terminal-1",
          pairCode: "123456",
        },
      ]),
    );
    expect(screen.getByTestId("settings-linkly-pair-code").props.value).toBe(
      "",
    );
    expect(screen.getByText(/Paired.*IPAD-01/)).toBeTruthy();
    expect(screen.getByText("Linkly terminal paired; status refreshed")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("settings-linkly-test"));
    await waitFor(() =>
      expect(screen.getByText("Payment provider available")).toBeTruthy(),
    );
    await fireEvent.press(
      screen.getByTestId("settings-payment-provider-linkly"),
    );
    await fireEvent.press(screen.getByTestId("settings-payment-save"));
    await screen.findByTestId("settings-confirmation");
    await fireEvent.press(screen.getByTestId("settings-confirm"));
    await screen.findByText("Payment settings saved");
  });

  it("中文危险操作显示明确确认，确认后才调用端口", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.changeText(
      screen.getByTestId("settings-api-address"),
      "https://staging.example.com/pos-api/",
    );
    await fireEvent.press(screen.getByTestId("settings-api-request-change"));

    await waitFor(() =>
      expect(screen.getByTestId("settings-confirmation")).toBeTruthy(),
    );
    expect(
      screen.getByTestId("settings-confirmation-modal").props.visible,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-nav-payments").props.accessibilityState
        .disabled,
    ).toBe(true);
    expect(screen.getByText(/待同步数据.*不会被清除/)).toBeTruthy();
    expect(port.apiAddresses).toEqual([]);

    await fireEvent.press(screen.getByTestId("settings-confirm"));
    await waitFor(() =>
      expect(port.apiAddresses).toEqual([
        "https://staging.example.com/pos-api",
      ]),
    );
    await screen.findByText(/\[api-address-saved\]/);
    expect(
      screen.getByText("API 地址已保存；运行时必须按适配器指引重新建立。"),
    ).toBeTruthy();
    expect(
      screen.queryByText(
        "API address saved. The runtime must reconnect as directed by its adapter.",
      ),
    ).toBeNull();
  });

  it("外设与硬件测试页可扫描/连接打印机并测试打印和扫码", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(<SettingsScreen presenter={presenter} />);
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await screen.findByTestId("settings-pane-content-peripherals");
    expect(
      screen.getByTestId("handheld-state-peripheral-settings"),
    ).toBeTruthy();
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));
    await screen.findByText(/\[printer-scan-finished\]/);
    expect(
      screen.getByTestId("handheld-state-pda-printer-connect"),
    ).toBeTruthy();
    expect(
      screen.getByTestId("settings-printer-picker-modal").props.visible,
    ).toBe(true);
    await screen.findByTestId("settings-printer-device-printer-2");
    await fireEvent.press(
      screen.getByTestId("settings-printer-connect-printer-2"),
    );
    await screen.findByText(/\[printer-connected\]/);
    expect(port.connectedPrinters).toEqual(["printer-2"]);
    expect(screen.getByText("Printer connected and saved")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("settings-nav-hardware"));
    await screen.findByTestId("settings-pane-content-hardware");
    await fireEvent.press(screen.getByTestId("settings-hardware-printer"));
    await screen.findByText(/\[printer-test-passed\]/);
    await fireEvent.press(screen.getByTestId("settings-hardware-scanner"));
    await screen.findByText(/\[scanner-test-passed\]/);

    expect(port.printerTests).toBe(1);
    expect(port.scannerTests).toBe(1);
    expect(screen.getByText("••••0001 · 12 chars")).toBeTruthy();
    expect(screen.queryByText("930000000001")).toBeNull();
  });

  it("已保存打印机可独立测试钱箱并清除，按钮按设备与钱箱设置启用", async () => {
    const port = new ScreenSettingsPort();
    const current = snapshot();
    port.snapshotValue = {
      ...current,
      printer: {
        ...current.printer,
        drawerEnabled: true,
        peripheralId: "printer-1",
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");
    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));

    const drawerTest = screen.getByTestId("settings-drawer-test");
    const clearSavedPrinter = screen.getByTestId(
      "settings-printer-clear-saved",
    );
    expect(screen.getByText("测试钱箱")).toBeTruthy();
    expect(screen.getByText("清除已保存打印机")).toBeTruthy();
    expect(drawerTest.props.accessibilityState.disabled).toBe(false);
    expect(clearSavedPrinter.props.accessibilityState.disabled).toBe(false);
    expect(StyleSheet.flatten(drawerTest.props.style).minHeight).toBeGreaterThanOrEqual(
      SETTINGS_MIN_TOUCH_TARGET,
    );

    await fireEvent.press(drawerTest);
    await waitFor(() => expect(port.cashDrawerTests).toBe(1));
    await screen.findByText(/\[cash-drawer-test-passed\]/);
    expect(screen.getByText("钱箱测试指令已发送")).toBeTruthy();
    await fireEvent.press(clearSavedPrinter);
    await waitFor(() => expect(port.clearedPrinterSettings).toBe(1));
    await screen.findByText(/\[printer-cleared\]/);
    expect(screen.getByText("已清除保存的打印机")).toBeTruthy();
    expect(presenter.getState().printer.peripheralId).toBeNull();
    expect(presenter.getState().hardware.printerStatus).toBe("connected");
  });

  it("钱箱未启用时阻止测试，但仍允许清除已有打印机", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");
    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));

    expect(
      screen.getByTestId("settings-drawer-test").props.accessibilityState
        .disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-printer-clear-saved").props
        .accessibilityState.disabled,
    ).toBe(false);
  });

  it("没有已保存打印机时同时阻止钱箱测试与清除", async () => {
    const port = new ScreenSettingsPort();
    const current = snapshot();
    port.snapshotValue = {
      ...current,
      printer: {
        ...current.printer,
        drawerEnabled: false,
        peripheralId: null,
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");
    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));

    expect(
      screen.getByTestId("settings-drawer-test").props.accessibilityState
        .disabled,
    ).toBe(true);
    expect(
      screen.getByTestId("settings-printer-clear-saved").props
        .accessibilityState.disabled,
    ).toBe(true);
  });

  it("PDA 扫描框显示真实 BLE 设备、固定操作区并保留 N160 双语推荐目标", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));
    await screen.findByTestId("settings-printer-device-printer001");

    expect(screen.getByText("Choose a nearby Bluetooth device")).toBeTruthy();
    expect(
      screen.getByText(
        /shows every nearby Bluetooth Low Energy device.*printer001 is the recommended target/,
      ),
    ).toBeTruthy();
    expect(screen.getAllByText("Name")).toHaveLength(3);
    expect(screen.getAllByText("Device address")).toHaveLength(3);
    expect(
      screen.getByTestId("settings-printer-device-name-printer001").props
        .children,
    ).toBe("Xprinter N160");
    expect(
      screen.getByTestId("settings-printer-device-address-printer001").props
        .children,
    ).toBe("printer001");
    expect(screen.getByText("Recommended target · printer001")).toBeTruthy();
    expect(screen.getByText("Backup Xprinter")).toBeTruthy();
    expect(screen.getByText("Stockroom temperature sensor")).toBeTruthy();

    const deviceList = screen.getByTestId("settings-printer-device-list");
    expect(deviceList.props.scrollEnabled).not.toBe(false);
    expect(StyleSheet.flatten(deviceList.props.style).flexShrink).toBe(1);
    expect(screen.getByTestId("settings-printer-picker-header")).toBeTruthy();
    expect(screen.getByTestId("settings-printer-picker-actions")).toBeTruthy();

    const preferredTag = screen.getByTestId(
      "settings-printer-preferred-printer001",
    );
    expect(
      StyleSheet.flatten(preferredTag.props.style).fontSize,
    ).toBeLessThanOrEqual(12);

    const otherPrinterAction = screen.getByTestId(
      "settings-printer-connect-printer-2",
    );
    expect(otherPrinterAction.props.accessibilityState.disabled).toBe(false);
    expect(
      StyleSheet.flatten(otherPrinterAction.props.style).minHeight,
    ).toBeGreaterThanOrEqual(SETTINGS_MIN_TOUCH_TARGET);
    expect(screen.getAllByText("Connect & Save")).toHaveLength(3);

    screen.unmount();
    const chinesePresenter = createPresenter(new ScreenSettingsPort());
    await chinesePresenter.load();
    const chineseScreen = await render(
      <SettingsScreen locale="zh" presenter={chinesePresenter} />,
    );
    await chineseScreen.findByTestId("settings-pane-content-general");
    await fireEvent.press(
      chineseScreen.getByTestId("settings-nav-peripherals"),
    );
    await fireEvent.press(chineseScreen.getByTestId("settings-printer-scan"));
    await chineseScreen.findByText("推荐目标");
    expect(chineseScreen.getByText("选择附近的蓝牙设备")).toBeTruthy();
    expect(chineseScreen.getAllByText("名称")).toHaveLength(3);
    expect(chineseScreen.getAllByText("设备地址")).toHaveLength(3);
    expect(
      chineseScreen.getByText(
        /显示附近所有低功耗蓝牙设备.*printer001 是推荐目标/,
      ),
    ).toBeTruthy();
    expect(chineseScreen.getAllByText("连接并保存")).toHaveLength(3);
  });

  it("蓝牙扫描没有发现附近设备时仍弹出结果框并提供重扫提示", async () => {
    const port = new ScreenSettingsPort();
    port.printerDevices = [];
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));

    expect(
      screen.getByTestId("settings-printer-picker-modal").props.visible,
    ).toBe(true);
    expect(
      await screen.findByText(/未发现附近的蓝牙设备.*蓝牙已开启/),
    ).toBeTruthy();
    expect(screen.getByTestId("settings-printer-picker-rescan")).toBeTruthy();
    expect(screen.queryByText("连接并保存")).toBeNull();
  });

  it("通用扫描失败只显示错误，不同时显示成功空结果", async () => {
    const port = new ScreenSettingsPort();
    port.printerScanError = new Error("generic scan failure");
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));

    await screen.findByText(/\[printer-scan-failed\]/);
    expect(screen.getAllByText("Printer scan failed")).toHaveLength(2);
    expect(
      screen.queryByText(
        /No (?:compatible printer|nearby Bluetooth devices) found/,
      ),
    ).toBeNull();
    expect(screen.queryByTestId("settings-printer-device-list")).toBeNull();
  });

  it("蓝牙权限缺失时显示明确引导并可直接前往系统设置", async () => {
    const openSettings = jest
      .spyOn(Linking, "openSettings")
      .mockResolvedValue(undefined);
    const port = new ScreenSettingsPort();
    port.printerScanError = Object.assign(
      new Error("Bluetooth permission required"),
      { code: "PRINTER_BLUETOOTH_PERMISSION_REQUIRED" },
    );
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));

    await screen.findByText(/\[printer-bluetooth-permission-required\]/);
    expect(
      screen.getByText(
        "请在设备系统设置中允许 HB POS 使用蓝牙，然后返回应用重新扫描。",
      ),
    ).toBeTruthy();
    expect(
      screen.queryByText(/未发现(?:兼容打印机|附近的蓝牙设备)/),
    ).toBeNull();

    await fireEvent.press(
      screen.getByTestId("settings-printer-open-system-settings"),
    );
    expect(openSettings).toHaveBeenCalledTimes(1);
  });

  describe("Android 蓝牙扫描引导按系统版本区分", () => {
    const originalOs = Platform.OS;
    const originalVersion = Platform.Version;
    const setAndroidVersion = (version: number) => {
      Object.defineProperty(Platform, "OS", { configurable: true, value: "android" });
      Object.defineProperty(Platform, "Version", { configurable: true, value: version });
    };

    afterEach(() => {
      Object.defineProperty(Platform, "OS", { configurable: true, value: originalOs });
      Object.defineProperty(Platform, "Version", {
        configurable: true,
        value: originalVersion,
      });
    });

    const renderScanFailure = async (code: string) => {
      const port = new ScreenSettingsPort();
      port.printerScanError = Object.assign(new Error(code), { code });
      const presenter = createPresenter(port);
      await presenter.load();
      const screen = await render(
        <SettingsScreen locale="zh" presenter={presenter} />,
      );
      await screen.findByTestId("settings-pane-content-general");
      await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
      await fireEvent.press(screen.getByTestId("settings-printer-scan"));
      return screen;
    };

    it("Android 10 缺权限时指向“位置信息”，不再提示不存在的蓝牙权限项", async () => {
      setAndroidVersion(29);
      const screen = await renderScanFailure("PRINTER_BLUETOOTH_PERMISSION_REQUIRED");

      await screen.findByText(/\[printer-bluetooth-permission-required\]/);
      expect(screen.getByText(/权限 → 位置信息/)).toBeTruthy();
      expect(
        screen.queryByText("请在设备系统设置中允许 HB POS 使用蓝牙，然后返回应用重新扫描。"),
      ).toBeNull();
      expect(screen.getByTestId("settings-printer-open-system-settings")).toBeTruthy();
    });

    it("Android 12+ 缺权限时指向“附近的设备”", async () => {
      setAndroidVersion(33);
      const screen = await renderScanFailure("PRINTER_BLUETOOTH_PERMISSION_REQUIRED");

      await screen.findByText(/\[printer-bluetooth-permission-required\]/);
      expect(screen.getByText(/允许“附近的设备”/)).toBeTruthy();
      expect(screen.queryByText(/位置信息/)).toBeNull();
    });

    it("定位服务关闭时提示打开定位，并可直达定位设置页", async () => {
      setAndroidVersion(29);
      const sendIntent = jest
        .spyOn(Linking, "sendIntent")
        .mockResolvedValue(undefined);
      const screen = await renderScanFailure("PRINTER_BLUETOOTH_LOCATION_OFF");

      await screen.findByText(/\[printer-bluetooth-location-off\]/);
      expect(screen.getByText(/打开“位置信息”开关/)).toBeTruthy();
      expect(screen.queryByTestId("settings-printer-open-system-settings")).toBeNull();
      expect(screen.queryByText(/未发现附近的蓝牙设备/)).toBeNull();

      await fireEvent.press(
        screen.getByTestId("settings-printer-open-location-settings"),
      );
      expect(sendIntent).toHaveBeenCalledWith(
        "android.settings.LOCATION_SOURCE_SETTINGS",
      );
    });
  });

  it("蓝牙关闭时提示开启蓝牙且不会误导用户修改应用权限", async () => {
    const port = new ScreenSettingsPort();
    port.printerScanError = Object.assign(new Error("Bluetooth powered off"), {
      code: "PRINTER_BLUETOOTH_POWERED_OFF",
    });
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));

    await screen.findByText(/\[printer-bluetooth-powered-off\]/);
    expect(screen.getAllByText("蓝牙已关闭，请开启后重新扫描")).toHaveLength(2);
    expect(
      screen.queryByTestId("settings-printer-open-system-settings"),
    ).toBeNull();
  });

  it("连接失败与已连接但保存失败显示不同的本地化状态", async () => {
    const connectionFailurePort = new ScreenSettingsPort();
    connectionFailurePort.printerConnectionFailure = true;
    const connectionFailurePresenter = createPresenter(connectionFailurePort);
    await connectionFailurePresenter.load();
    const chineseScreen = await render(
      <SettingsScreen
        locale="zh"
        presenter={connectionFailurePresenter}
      />,
    );
    await chineseScreen.findByTestId("settings-pane-content-general");
    await fireEvent.press(
      chineseScreen.getByTestId("settings-nav-peripherals"),
    );
    await fireEvent.press(chineseScreen.getByTestId("settings-printer-scan"));
    await chineseScreen.findByTestId("settings-printer-device-printer001");
    await fireEvent.press(
      chineseScreen.getByTestId("settings-printer-connect-printer001"),
    );
    await chineseScreen.findByText(/\[printer-connect-failed\]/);
    expect(
      chineseScreen.getAllByText("打印机连接失败，设置未保存"),
    ).toHaveLength(2);

    chineseScreen.unmount();
    const saveFailurePort = new ScreenSettingsPort();
    saveFailurePort.printerSettingsSaveFailure = true;
    const saveFailurePresenter = createPresenter(saveFailurePort);
    await saveFailurePresenter.load();
    const englishScreen = await render(
      <SettingsScreen locale="en" presenter={saveFailurePresenter} />,
    );
    await englishScreen.findByTestId("settings-pane-content-general");
    await fireEvent.press(englishScreen.getByTestId("settings-nav-peripherals"));
    await fireEvent.press(englishScreen.getByTestId("settings-printer-scan"));
    await englishScreen.findByTestId("settings-printer-device-printer001");
    await fireEvent.press(
      englishScreen.getByTestId("settings-printer-connect-printer001"),
    );
    await englishScreen.findByText(/\[printer-connected-save-failed\]/);
    expect(
      englishScreen.getAllByText(
        "Printer connected, but settings could not be saved",
      ),
    ).toHaveLength(2);
    expect(saveFailurePort.connectedPrinters).toEqual(["printer001"]);
  });

  it("打印测试结果 unknown 使用稳定状态文案且不自动重试", async () => {
    const port = new ScreenSettingsPort();
    port.printerTestError = Object.assign(
      new Error("ambiguous native print result"),
      { code: "SETTINGS_PRINTER_TEST_OUTCOME_UNKNOWN" },
    );
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");
    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await fireEvent.press(screen.getByTestId("settings-printer-test"));

    await screen.findByText(/\[printer-test-unknown\]/);
    expect(
      screen.getByText(
        "测试命令已发送，但打印机未确认结果；请人工检查是否出纸，应用不会自动重试。",
      ),
    ).toBeTruthy();
    expect(port.printerTests).toBe(1);
  });

  it("打印机与扫描器之间以竖屏单列展示六字段门店资料卡，控件至少 48pt 且载入不保存", async () => {
    const port = new ScreenSettingsPort();
    port.receiptProfileValue = {
      storeCode: "BNE-01",
      brandName: "Hot Bargain",
      storeName: "Brisbane Central",
      address: "1 Queen St\nBrisbane QLD 4000",
      phone: "07 3000 0000",
      abn: "12 345 678 901",
      returnPolicy: "Refunds within 14 days.",
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="en" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await screen.findByTestId("settings-pane-content-peripherals");

    expect(screen.getByText("Receipt store profile")).toBeTruthy();
    const layout = JSON.stringify(screen.toJSON());
    expect(layout.indexOf("settings-receipt-profile-card")).toBeGreaterThan(
      layout.indexOf("settings-printer-id"),
    );
    expect(layout.indexOf("settings-receipt-profile-card")).toBeLessThan(
      layout.indexOf("settings-scanner-test"),
    );

    const fieldMaxLengths = {
      "settings-receipt-brand-name": 120,
      "settings-receipt-store-name": 120,
      "settings-receipt-address": 240,
      "settings-receipt-phone": 60,
      "settings-receipt-abn": 32,
      "settings-receipt-return-policy": 500,
    } as const;
    for (const [testID, maxLength] of Object.entries(fieldMaxLengths)) {
      const field = screen.getByTestId(testID);
      expect(field.props.maxLength).toBe(maxLength);
      expect(StyleSheet.flatten(field.props.style).minHeight).toBeGreaterThanOrEqual(
        SETTINGS_MIN_TOUCH_TARGET,
      );
    }
    expect(screen.getByTestId("settings-receipt-address").props.multiline).toBe(
      true,
    );
    expect(
      screen.getByTestId("settings-receipt-return-policy").props.multiline,
    ).toBe(true);

    const loadButton = screen.getByTestId("settings-receipt-profile-load");
    expect(StyleSheet.flatten(loadButton.props.style).minHeight).toBeGreaterThanOrEqual(
      SETTINGS_MIN_TOUCH_TARGET,
    );
    expect(port.savedPrinterSettings).toHaveLength(0);

    await fireEvent.press(loadButton);

    await waitFor(() =>
      expect(screen.getByTestId("settings-receipt-brand-name").props.value).toBe(
        "Hot Bargain",
      ),
    );
    expect(screen.getByTestId("settings-receipt-store-name").props.value).toBe(
      "Brisbane Central",
    );
    expect(screen.getByTestId("settings-receipt-address").props.value).toBe(
      "1 Queen St\nBrisbane QLD 4000",
    );
    expect(screen.getByTestId("settings-receipt-phone").props.value).toBe(
      "07 3000 0000",
    );
    expect(screen.getByTestId("settings-receipt-abn").props.value).toBe(
      "12 345 678 901",
    );
    expect(screen.getByTestId("settings-receipt-return-policy").props.value).toBe(
      "Refunds within 14 days.",
    );
    expect(screen.getByText("Loaded. Save to apply.")).toBeTruthy();
    expect(port.savedPrinterSettings).toHaveLength(0);
  });

  it("不可用硬件不显示绿色健康状态，关键配置输入使用中文标签", async () => {
    const port = new ScreenSettingsPort();
    const current = snapshot();
    port.snapshotValue = {
      ...current,
      hardware: {
        ...current.hardware,
        scannerStatus: "unavailable",
      },
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await screen.findByTestId("settings-pane-content-peripherals");
    expect(screen.getByLabelText("打印机设备 ID")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await screen.findByTestId("settings-pane-content-device");
    expect(screen.getByLabelText("一次性设备开通码")).toBeTruthy();
    expect(screen.getByLabelText("终端名称")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("settings-nav-hardware"));
    const scannerStatus = screen.getByTestId(
      "settings-hardware-scanner-status",
    );
    expect(StyleSheet.flatten(scannerStatus.props.style).color).toBe(
      posColors.mutedInk,
    );
    expect(StyleSheet.flatten(scannerStatus.props.style).color).not.toBe(
      posColors.green,
    );
  });

  it("换店必须先预览当前到目标、平台和到期，再开放安全确认", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await fireEvent.changeText(
      screen.getByTestId("settings-reregister-store"),
      DEVICE_ACTIVATION_CODE,
    );
    expect(screen.queryByTestId("settings-reregister-request")).toBeNull();

    await fireEvent.press(screen.getByTestId("settings-reregister-preview"));
    await screen.findByTestId("settings-reregister-preview-card");
    expect(screen.getByText("BNE-01 → Sunnybank · BNE-02")).toBeTruthy();
    expect(screen.getByText("Android")).toBeTruthy();
    expect(screen.getByText("到期时间")).toBeTruthy();
    expect(screen.getByText(/2026/)).toBeTruthy();

    await fireEvent.press(screen.getByTestId("settings-reregister-request"));
    await screen.findByTestId("settings-confirmation");
    expect(screen.getByText(/BNE-01 → Sunnybank（BNE-02）/)).toBeTruthy();
    expect(screen.getByText(/Android.*2026/s)).toBeTruthy();
  });

  it("换店阻断面板按稳定顺序展示八类脱敏详情，并提供匹配入口和只读重新检查", async () => {
    const port = new ScreenSettingsPort();
    port.preflightResult = {
      status: "blocked",
      reason: "pending-local-data",
      blockers: derivePendingWorkBlockers({
        hasActiveCart: true,
        hasFulfilmentInFlight: true,
        hasSyncOrAuditInFlight: true,
        paymentConfigurationSensitiveOrderCount: 2,
        pendingDurableWriteCount: 3,
        pendingReturnCount: 4,
        pendingSaleCount: 5,
        unresolvedPaymentCount: 6,
      }),
    };
    const backToSales = jest.fn();
    const openSyncHistory = jest.fn();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen
        locale="zh"
        onBack={backToSales}
        onOpenSyncHistory={openSyncHistory}
        presenter={presenter}
      />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await fireEvent.changeText(
      screen.getByTestId("settings-reregister-store"),
      DEVICE_ACTIVATION_CODE,
    );
    await fireEvent.press(screen.getByTestId("settings-reregister-preview"));
    await screen.findByTestId("settings-reregister-preview-card");
    await fireEvent.press(screen.getByTestId("settings-reregister-request"));

    const panel = await screen.findByTestId("settings-reregister-blockers");
    expect(panel.props.accessibilityRole).toBe("alert");
    expect(within(panel).getByText("暂时无法更换分店")).toBeTruthy();
    expect(within(panel).getByText(/当前购物车/)).toBeTruthy();
    expect(within(panel).getByText(/履约或硬件操作｜进行中/)).toBeTruthy();
    expect(within(panel).getByText(/同步或审计上传｜进行中/)).toBeTruthy();
    expect(within(panel).getByText("依赖当前支付配置的订单：2 笔")).toBeTruthy();
    expect(within(panel).getByText(/未完成耐久写入：3 项/)).toBeTruthy();
    expect(within(panel).getByText("待同步退货：4 笔")).toBeTruthy();
    expect(within(panel).getByText("待同步销售：5 笔")).toBeTruthy();
    expect(within(panel).getByText(/未决支付：6 笔/)).toBeTruthy();
    expect(screen.queryByTestId("settings-confirmation")).toBeNull();
    for (const testID of [
      "settings-reregister-back-to-sales",
      "settings-reregister-open-sync-history",
      "settings-reregister-recheck",
    ]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
      ).toBeGreaterThanOrEqual(SETTINGS_MIN_TOUCH_TARGET);
    }

    await fireEvent.press(screen.getByTestId("settings-reregister-back-to-sales"));
    expect(backToSales).toHaveBeenCalledTimes(1);
    await fireEvent.press(
      screen.getByTestId("settings-reregister-open-sync-history"),
    );
    expect(openSyncHistory).toHaveBeenCalledTimes(1);

    port.preflightResult = { status: "ready" };
    await fireEvent.press(screen.getByTestId("settings-reregister-recheck"));
    await screen.findByTestId("settings-confirmation");
    expect(port.preflightCalls).toBe(2);
  });

  it("英文换店阻断显示准确笔数与英文安全入口", async () => {
    const port = new ScreenSettingsPort();
    port.preflightResult = {
      status: "blocked",
      reason: "pending-local-data",
      blockers: derivePendingWorkBlockers({
        hasActiveCart: false,
        hasFulfilmentInFlight: false,
        hasSyncOrAuditInFlight: false,
        paymentConfigurationSensitiveOrderCount: 0,
        pendingDurableWriteCount: 0,
        pendingReturnCount: 0,
        pendingSaleCount: 2,
        unresolvedPaymentCount: 0,
      }),
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen
        locale="en"
        onOpenSyncHistory={jest.fn()}
        presenter={presenter}
      />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await fireEvent.changeText(
      screen.getByTestId("settings-reregister-store"),
      DEVICE_ACTIVATION_CODE,
    );
    await fireEvent.press(screen.getByTestId("settings-reregister-preview"));
    await screen.findByTestId("settings-reregister-preview-card");
    await fireEvent.press(screen.getByTestId("settings-reregister-request"));

    const panel = await screen.findByTestId("settings-reregister-blockers");
    expect(
      within(panel).getByText("Store change is temporarily blocked"),
    ).toBeTruthy();
    expect(within(panel).getByText("Sales waiting to sync: 2")).toBeTruthy();
    expect(within(panel).getByText("View sync history")).toBeTruthy();
    expect(within(panel).getByText("Recheck")).toBeTruthy();
  });

  it("没有同步记录权限时不显示误导入口并提示联系主管", async () => {
    const port = new ScreenSettingsPort();
    port.preflightResult = {
      status: "blocked",
      reason: "pending-local-data",
      blockers: derivePendingWorkBlockers({
        hasActiveCart: false,
        hasFulfilmentInFlight: false,
        hasSyncOrAuditInFlight: false,
        paymentConfigurationSensitiveOrderCount: 0,
        pendingDurableWriteCount: 0,
        pendingReturnCount: 0,
        pendingSaleCount: 1,
        unresolvedPaymentCount: 0,
      }),
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await fireEvent.changeText(
      screen.getByTestId("settings-reregister-store"),
      DEVICE_ACTIVATION_CODE,
    );
    await fireEvent.press(screen.getByTestId("settings-reregister-preview"));
    await screen.findByTestId("settings-reregister-preview-card");
    await fireEvent.press(screen.getByTestId("settings-reregister-request"));

    expect(
      await screen.findByTestId("settings-reregister-sync-history-permission"),
    ).toHaveTextContent("请联系具备同步记录权限的主管检查待处理订单。");
    expect(
      screen.queryByTestId("settings-reregister-open-sync-history"),
    ).toBeNull();
  });

  it("本地安全快照读取失败只显示 safety-check-failed 面板与重新检查", async () => {
    const port = new ScreenSettingsPort();
    port.preflightResult = {
      status: "blocked",
      reason: "safety-check-failed",
    };
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await fireEvent.changeText(
      screen.getByTestId("settings-reregister-store"),
      DEVICE_ACTIVATION_CODE,
    );
    await fireEvent.press(screen.getByTestId("settings-reregister-preview"));
    await screen.findByTestId("settings-reregister-preview-card");
    await fireEvent.press(screen.getByTestId("settings-reregister-request"));

    const panel = await screen.findByTestId("settings-reregister-blockers");
    expect(within(panel).getByText("无法读取本地安全状态")).toBeTruthy();
    expect(
      within(panel).getByText(/无法确认本地业务是否已安全完成/),
    ).toBeTruthy();
    expect(
      within(panel).getByTestId("settings-reregister-recheck"),
    ).toBeTruthy();
    expect(
      within(panel).queryAllByTestId(/settings-reregister-blocker-/),
    ).toHaveLength(0);
    expect(screen.queryByTestId("settings-confirmation")).toBeNull();
  });

  it("清除设备注册显示精确设备影响说明，员工条码不预填且只在最终确认时提交", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("settings-nav-device"));
    await fireEvent.press(
      screen.getByTestId("settings-device-registration-reset-request"),
    );

    expect(screen.getByText(/清除 BNE-01 \/ POS-01/)).toBeTruthy();
    const barcode = screen.getByTestId(
      "settings-device-registration-reset-barcode",
    );
    expect(barcode.props.value).toBe("");
    expect(barcode.props.secureTextEntry).toBe(true);
    expect(
      screen.getByTestId("settings-confirm").props.accessibilityState.disabled,
    ).toBe(true);

    fireEvent.changeText(barcode, "9900000000001");
    await waitFor(() => {
      expect(
        screen.getByTestId("settings-confirm").props.accessibilityState
          .disabled,
      ).toBe(false);
    });
    await fireEvent.press(screen.getByTestId("settings-confirm"));

    await waitFor(() => {
      expect(port.deviceResetBarcodes).toEqual(["9900000000001"]);
    });
  });

  it("危险确认弹窗点击面板外遮罩取消且不执行确认", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.changeText(
      screen.getByTestId("settings-api-address"),
      "http://localhost:5159",
    );
    await fireEvent.press(screen.getByTestId("settings-api-request-change"));
    await screen.findByTestId("settings-confirmation");

    await fireEvent.press(
      screen.getByTestId("settings-confirmation-backdrop"),
    );
    expect(screen.queryByTestId("settings-confirmation")).toBeNull();
    expect(port.apiAddresses).toEqual([]);
  });

  it("危险确认弹窗 busy 时遮罩不可关闭", async () => {
    const port = new ScreenSettingsPort();
    let resolveAction:
      | ((value: SettingsDangerousActionResult) => void)
      | undefined;
    port.executeDangerousAction = jest.fn(
      () =>
        new Promise<SettingsDangerousActionResult>((resolve) => {
          resolveAction = resolve;
        }),
    );
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.changeText(
      screen.getByTestId("settings-api-address"),
      "http://localhost:5159",
    );
    await fireEvent.press(screen.getByTestId("settings-api-request-change"));
    await screen.findByTestId("settings-confirmation");
    await fireEvent.press(screen.getByTestId("settings-confirm"));
    await waitFor(() =>
      expect(
        screen.getByTestId("settings-confirm").props.accessibilityState,
      ).toMatchObject({ disabled: true }),
    );

    await fireEvent.press(
      screen.getByTestId("settings-confirmation-backdrop"),
    );
    expect(screen.getByTestId("settings-confirmation")).toBeTruthy();

    await act(async () => {
      resolveAction?.({ status: "completed", kind: "change-api-address" });
    });
    await waitFor(() =>
      expect(screen.queryByTestId("settings-confirmation")).toBeNull(),
    );
  });

  it("Square 选择弹窗点击面板外遮罩关闭", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-payments"));
    await screen.findByTestId("settings-pane-content-payments");
    await fireEvent.press(screen.getByTestId("settings-square-location-load"));
    await screen.findByTestId(
      "settings-square-location-picker-option-location-1",
    );

    await fireEvent.press(
      screen.getByTestId("settings-square-location-picker-backdrop"),
    );
    expect(
      screen.queryByTestId("settings-square-location-picker-modal"),
    ).toBeNull();
  });

  it("打印机选择弹窗点击面板外遮罩关闭", async () => {
    const port = new ScreenSettingsPort();
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await screen.findByTestId("settings-pane-content-peripherals");
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));
    await screen.findByText(/\[printer-scan-finished\]/);
    expect(
      screen.getByTestId("settings-printer-picker-modal").props.visible,
    ).toBe(true);

    await fireEvent.press(
      screen.getByTestId("settings-printer-picker-backdrop"),
    );
    expect(
      screen.queryByTestId("settings-printer-picker-modal"),
    ).toBeNull();
  });

  it("打印机选择弹窗 busy 时遮罩不可关闭", async () => {
    const port = new ScreenSettingsPort();
    let resolveScan:
      | ((value: readonly SettingsPrinterDevice[]) => void)
      | undefined;
    port.scanPrinters = jest.fn(
      () =>
        new Promise<readonly SettingsPrinterDevice[]>((resolve) => {
          resolveScan = resolve;
        }),
    );
    const presenter = createPresenter(port);
    await presenter.load();
    const screen = await render(
      <SettingsScreen locale="zh" presenter={presenter} />,
    );
    await screen.findByTestId("settings-pane-content-general");

    await fireEvent.press(screen.getByTestId("settings-nav-peripherals"));
    await screen.findByTestId("settings-pane-content-peripherals");
    await fireEvent.press(screen.getByTestId("settings-printer-scan"));
    await waitFor(() =>
      expect(
        screen.getByTestId("settings-printer-picker-modal").props.visible,
      ).toBe(true),
    );

    await fireEvent.press(
      screen.getByTestId("settings-printer-picker-backdrop"),
    );
    expect(
      screen.getByTestId("settings-printer-picker-modal").props.visible,
    ).toBe(true);

    await act(async () => {
      resolveScan?.(port.printerDevices);
    });
    await screen.findByText(/\[printer-scan-finished\]/);
    await fireEvent.press(
      screen.getByTestId("settings-printer-picker-backdrop"),
    );
    expect(
      screen.queryByTestId("settings-printer-picker-modal"),
    ).toBeNull();
  });
});

function createPresenter(port: ScreenSettingsPort): SettingsPresenter {
  const presenter = new SettingsPresenter({
    permissions: [
      SETTINGS_VIEW_PERMISSION,
      SETTINGS_PAYMENT_TERMINAL_PERMISSION,
      SETTINGS_RECEIPT_PRINTER_PERMISSION,
      SETTINGS_CATALOG_DOWNLOAD_PERMISSION,
      SETTINGS_CATALOG_RESET_PERMISSION,
      SETTINGS_DEVICE_REGISTRATION_PERMISSION,
      SETTINGS_APP_UPDATE_PERMISSION,
    ],
    port,
  });
  presenters.push(presenter);
  return presenter;
}

class ScreenSettingsPort implements SettingsControlPort {
  public readonly apiAddresses: string[] = [];
  public readonly apiAddressTests: string[] = [];
  public readonly connectedPrinters: string[] = [];
  public readonly savedPayments: SettingsPaymentSettingsInput[] = [];
  public readonly savedPrinterSettings: ReceiptPrinterSettings[] = [];
  public readonly deviceResetBarcodes: string[] = [];
  public readonly squareSetup: SettingsSquareSetupControlPort = {
    createSquareDeviceCode: async (environment, locationId, name) => ({
      code: "PAIR-02",
      deviceId: null,
      id: "device-code-2",
      locationId,
      name,
      status: environment === "Production" ? "UNPAIRED" : "SANDBOX_TEST",
    }),
    getSquareDeviceCode: async (_environment, deviceCodeId) =>
      this.squareDeviceCodes.find((item) => item.id === deviceCodeId) ??
      this.squareDeviceCodes[0]!,
    getSquareTokenStatus: async (environment) => ({
      configured: true,
      enabled: true,
      environment,
      updatedAt: "2026-08-01T00:00:00.000Z",
    }),
    listSquareDeviceCodes: async () => this.squareDeviceCodes,
    listSquareDevices: async (_environment, locationId) =>
      this.squareDevices.map((device) => ({ ...device, locationId })),
    listSquareLocations: async () => {
      if (this.squareLocationLoadFailure) {
        throw new Error("square locations unavailable");
      }
      return this.squareLocations;
    },
  };
  public readonly linklySetup = new ScreenLinklySetupControlPort();
  public readonly linklyPairing = new ScreenLinklyPairingPort(
    this.linklySetup,
  );
  public readonly squareDeviceCodes = [
    {
      code: "PAIR-01",
      deviceId: "device-1",
      id: "device-code-1",
      locationId: "location-1",
      name: "Front Terminal",
      status: "PAIRED",
    },
  ] as const;
  public readonly squareDevices = [
    {
      code: "SQ-01",
      id: "device-1",
      locationId: "location-1",
      name: "Front Terminal",
      sandboxTest: false,
      status: "ENABLED",
    },
    {
      code: "SQ-02",
      id: "device-2",
      locationId: "location-1",
      name: "Backup Terminal",
      sandboxTest: false,
      status: "ENABLED",
    },
  ] as const;
  public squareLocations: readonly SettingsSquareLocation[] = [
    {
      country: "AU",
      currency: "AUD",
      id: "location-1",
      name: "Brisbane",
      status: "ACTIVE",
    },
    {
      country: "AU",
      currency: "AUD",
      id: "location-2",
      name: "Gold Coast",
      status: "ACTIVE",
    },
  ] as const;
  public squareLocationLoadFailure = false;
  public cashDrawerTests = 0;
  public clearedPrinterSettings = 0;
  public printerConnectionFailure = false;
  public printerDevices: readonly SettingsPrinterDevice[] = [
    {
      id: "printer001",
      name: "Xprinter N160",
      preferred: true,
      transport: "bluetooth",
    },
    {
      id: "printer-2",
      name: "Backup Xprinter",
      preferred: false,
      transport: "bluetooth",
    },
    {
      id: "sensor-1",
      name: "Stockroom temperature sensor",
      preferred: false,
      transport: "bluetooth-le",
    },
  ];
  public printerScanError: Error | null = null;
  public printerSettingsSaveFailure = false;
  public printerTestError: Error | null = null;
  public printerTests = 0;
  public receiptProfileValue: SettingsReceiptProfileDraft | null = null;
  public scannerTests = 0;
  public activationPreviewResponse: SettingsDeviceActivationPreviewResponse | null =
    null;
  public preflightCalls = 0;
  public preflightResult: SettingsDeviceReregistrationPreflightResult = {
    status: "ready",
  };
  public snapshotValue: SettingsSnapshot | null = null;
  private catalogRefreshState: ReturnType<
    SettingsControlPort["getCatalogRefreshState"]
  > = { kind: "idle" };
  private readonly catalogRefreshListeners = new Set<() => void>();

  public async loadSnapshot(): Promise<SettingsSnapshot> {
    return this.snapshotValue ?? snapshot();
  }

  public getCatalogRefreshState() {
    return this.catalogRefreshState;
  }

  public subscribeCatalogRefresh(listener: () => void): () => void {
    this.catalogRefreshListeners.add(listener);
    return () => this.catalogRefreshListeners.delete(listener);
  }

  public subscribeDeviceReregistrationCommitted(): () => void {
    return () => undefined;
  }

  public publishCatalogRefresh(
    state: ReturnType<SettingsControlPort["getCatalogRefreshState"]>,
  ): void {
    this.catalogRefreshState = state;
    for (const listener of this.catalogRefreshListeners) listener();
  }

  public async downloadCatalog() {
    return {
      snapshotId: "catalog-new",
      itemCount: 20,
      activatedAt: "2026-07-28T03:00:00.000Z",
    };
  }

  public async testApiAddress(apiBaseUrl: string): Promise<boolean> {
    this.apiAddressTests.push(apiBaseUrl);
    return true;
  }

  public async previewDeviceActivationCode() {
    return this.activationPreviewResponse ?? {
      deviceSystem: "Android",
      expiresAtUtc: "2026-08-28T00:00:00.000Z",
      isAllowed: true,
      storeCode: "BNE-02",
      storeName: "Sunnybank",
    };
  }

  public async preflightDeviceReregistration() {
    this.preflightCalls += 1;
    return this.preflightResult;
  }

  public async executeDangerousAction(
    action: SettingsDangerousConfirmation,
    _signal?: AbortSignal,
    employeeBarcode?: string,
  ): Promise<SettingsDangerousActionResult> {
    if (action.kind === "change-api-address") {
      this.apiAddresses.push(action.apiBaseUrl);
      return { status: "completed", kind: action.kind };
    }
    if (action.kind === "change-payment-settings") {
      this.savedPayments.push(action.input);
      return { status: "completed", kind: action.kind };
    }
    if (action.kind === "pair-linkly") {
      const result = await this.linklyPairing.pair(
        action.environment,
        action.terminalId,
        action.pairCode,
      );
      return result.status === "unknown"
        ? { status: "unknown", kind: action.kind }
        : { status: "completed", kind: action.kind };
    }
    if (action.kind === "reset-catalog") {
      return {
        status: "completed",
        kind: action.kind,
        catalog: { snapshotId: null, itemCount: 0, activatedAt: null },
      };
    }
    if (action.kind === "reset-device-registration") {
      this.deviceResetBarcodes.push(employeeBarcode?.trim() ?? "");
    }
    return { status: "completed", kind: action.kind };
  }

  public async testPaymentProvider(): Promise<void> {}

  public async savePrinterSettings(
    settings: ReceiptPrinterSettings,
  ): Promise<void> {
    if (this.printerSettingsSaveFailure) {
      throw new Error("printer settings save failed");
    }
    this.savedPrinterSettings.push(settings);
  }

  public async scanPrinters() {
    if (this.printerScanError) throw this.printerScanError;
    return this.printerDevices;
  }

  public async connectPrinter(peripheralId: string): Promise<void> {
    if (this.printerConnectionFailure) {
      throw new Error("printer connection failed");
    }
    this.connectedPrinters.push(peripheralId);
  }

  public async testPrinter(): Promise<void> {
    this.printerTests += 1;
    if (this.printerTestError) throw this.printerTestError;
  }

  public async loadReceiptProfile(): Promise<SettingsReceiptProfileDraft | null> {
    return this.receiptProfileValue;
  }

  public async testCashDrawer() {
    this.cashDrawerTests += 1;
    return { status: "completed" as const, errorCode: null };
  }

  public async clearSavedPrinter() {
    this.clearedPrinterSettings += 1;
    return { status: "completed" as const, errorCode: null };
  }

  public async testScanner() {
    this.scannerTests += 1;
    return { source: "hid" as const, value: "930000000001" };
  }

  public async checkForAppUpdate() {
    return snapshot().appUpdate;
  }
}

class ScreenLinklySetupControlPort implements SettingsLinklySetupControlPort {
  public ready = false;
  public multiTerminal = false;
  public busyTerminalId: string | null = null;
  public selectedTerminalId = "terminal-1";
  public selectionRevision = 1;

  public async readState(
    environment: "Sandbox" | "Production",
  ): Promise<SettingsLinklyHealthSnapshot> {
    return {
      environment,
      storeCode: "STORE-01",
      deviceCode: "IPAD-01",
      isReady: this.ready,
      checks: [
        {
          code: "STORE_CREDENTIAL",
          isReady: true,
          message: "ready",
        },
        {
          code: "TERMINAL_SECRET",
          isReady: this.ready,
          message: this.ready ? "ready" : "missing",
        },
        {
          code: "TERMINAL_POS_ID",
          isReady: this.ready,
          message: this.ready ? "ready" : "missing",
        },
      ],
    };
  }

  public async readTerminals(
    environment: "Sandbox" | "Production",
  ): Promise<SettingsLinklyTerminalSelectionSnapshot> {
    return {
      environment,
      mode: "Active",
      selectedTerminalId: this.selectedTerminalId,
      selectionRevision: this.selectionRevision,
      terminals: (this.multiTerminal
        ? [
            terminal("terminal-1", 1, "前台", this.ready),
            terminal("terminal-2", 2, "退货台", true),
          ]
        : [terminal("terminal-1", 1, "前台", this.ready)]).map((item) => ({
          ...item,
          isBusy: item.terminalId === this.busyTerminalId,
        })),
    };
  }

  public async selectTerminal(
    environment: "Sandbox" | "Production",
    terminalId: string,
    expectedRevision: number,
  ): Promise<SettingsLinklyTerminalSelectionSnapshot> {
    this.selectedTerminalId = terminalId;
    this.selectionRevision = expectedRevision + 1;
    return this.readTerminals(environment);
  }
}

class ScreenLinklyPairingPort implements SettingsLinklyPairingPort {
  public readonly pairCalls: {
    environment: "Sandbox" | "Production";
    terminalId: string;
    pairCode: string;
  }[] = [];

  public constructor(private readonly setup: ScreenLinklySetupControlPort) {}

  public async pair(
    environment: "Sandbox" | "Production",
    terminalId: string,
    pairCode: string,
  ): Promise<SettingsLinklyPairResult> {
    this.pairCalls.push({ environment, terminalId, pairCode });
    this.setup.ready = true;
    return { status: "completed" };
  }
}

function terminal(
  terminalId: string,
  laneNo: number,
  displayName: string,
  ready: boolean,
) {
  return {
    terminalId,
    laneNo,
    displayName,
    pairingState: ready ? ("Ready" as const) : ("Unpaired" as const),
    isBusy: false,
    isReady: ready,
    lastHealthStatus: ready ? "ready" : null,
    lastHealthAt: null,
  };
}

function snapshot(): SettingsSnapshot {
  return {
    apiBaseUrl: "https://hotbargain.vip/pos-api",
    appUpdate: {
      channel: "production",
      currentVersion: "1.0.0",
      availableVersion: null,
      updateRequired: false,
      restartAvailable: false,
    },
    catalog: {
      snapshotId: "catalog-current",
      itemCount: 10,
      activatedAt: "2026-07-27T03:00:00.000Z",
    },
    device: {
      deviceCode: "POS-01",
      storeCode: "BNE-01",
      storeName: "Brisbane",
      terminalName: "Front",
    },
    hardware: {
      printerStatus: "connected",
      scannerStatus: "ready",
      lastScannerValue: null,
    },
    paymentProvider: "square",
    linkly: {
      available: true,
      blockerCode: null,
      environment: "Production",
    },
    printer: {
      ...DEFAULT_RECEIPT_PRINTER_SETTINGS,
      printEnabled: true,
      peripheralId: "printer-1",
    },
    square: {
      available: true,
      blockerCode: null,
      environment: "Production",
      deviceId: "device-1",
      locationId: "location-1",
    },
  };
}
