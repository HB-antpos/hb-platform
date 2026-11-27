import { afterEach, beforeEach, expect, jest, test } from "@jest/globals";
import {
  act,
  cleanup,
  fireEvent,
  render,
} from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import { usePosShellStore } from "./pos-shell-store";
import {
  POS_TOP_BAR_STORE_MIN_WIDTH,
  POS_TOP_BAR_VERSION_MAX_WIDTH,
  PosStatusStrip,
} from "./status-strip";

import en from "@/i18n/locales/en.json";
import { usePosSound } from "@/ui/feedback/pos-sound-context";

jest.mock("@/ui/feedback/pos-sound-context", () => ({ usePosSound: jest.fn() }));

jest.mock("@expo/vector-icons", () => ({
  MaterialCommunityIcons: ({ name }: { name: string }) => {
    const { Text } = jest.requireActual<typeof import("react-native")>(
      "react-native",
    );

    return <Text>{name}</Text>;
  },
}));

const mockUsePosSound = jest.mocked(usePosSound);
const play = jest.fn();

let mockLanguage = "zh";

const mockTranslations: Readonly<Record<string, Readonly<Record<string, string>>>> =
  {
    en: {
      "status.languageSwitchHint": "Show all interface text in Chinese.",
      "status.languageSwitchLabel": "Switch interface language to Chinese",
      "status.device": "Device",
      "status.device.authorized": "Authorized",
      "status.deviceCode": "Device code",
      "status.display": "Display",
      "status.network": "Network",
      "status.network.online": "Online",
      "status.peripheral.disconnected": "Offline",
      "status.printer": "Printer",
      "status.bleScanner": "BLE scanner",
      "status.bleScanner.ready": "Connected",
      "status.bleScanner.readyWithBattery": "{{level}}%",
      "status.bleScanner.bluetooth-off": "Bluetooth off",
      "status.scanner": "Scanner",
      "status.scanner.inactive": "Unfocused",
      "status.storeName": "Branch name",
      "status.sync": "Sync",
      "status.sync.checking": "Checking",
      "status.sync.pending": "0 pending",
      "status.sync.unavailable": "Unavailable",
    },
    zh: {
      "status.languageSwitchHint": "将所有界面文字显示为英文。",
      "status.languageSwitchLabel": "将界面语言切换为英文",
      "status.device": "设备",
      "status.device.authorized": "已授权",
      "status.deviceCode": "设备代码",
      "status.display": "客显",
      "status.network": "网络",
      "status.network.online": "在线",
      "status.peripheral.disconnected": "未连接",
      "status.printer": "打印机",
      "status.bleScanner": "蓝牙枪",
      "status.bleScanner.ready": "已连接",
      "status.bleScanner.readyWithBattery": "已连接 {{level}}%",
      "status.bleScanner.bluetooth-off": "蓝牙已关",
      "status.scanner": "扫码",
      "status.scanner.inactive": "未聚焦",
      "status.storeName": "分店名称",
      "status.sync": "补传",
      "status.sync.checking": "检查中",
      "status.sync.pending": "0 笔待处理",
      "status.sync.unavailable": "不可用",
    },
  };

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
    t: (key: string, values?: Readonly<Record<string, number | string>>) =>
      key === "status.sync.pending"
        ? mockLanguage === "zh"
          ? `${values?.count ?? 0} 笔待处理`
          : `${values?.count ?? 0} pending`
        : (mockTranslations[mockLanguage]?.[key] ?? key).replace(
            /\{\{(\w+)\}\}/gu,
            (_match, name: string) => String(values?.[name] ?? ""),
          ),
  }),
}));

afterEach(() => {
  cleanup();
  mockLanguage = "zh";
  usePosShellStore.getState().reset();
});

beforeEach(() => {
  mockUsePosSound.mockReturnValue({
    buttonSoundEnabled: true,
    play,
    setButtonSoundEnabled: jest.fn(),
    setSpecialNodeSoundEnabled: jest.fn(),
    specialNodeSoundEnabled: true,
  });
});

test("没有切换回调时不显示语言入口", async () => {
  const screen = await render(<PosStatusStrip language="zh" />);

  expect(screen.queryByTestId("status-strip-language-switch")).toBeNull();
});

test("终端身份默认关闭，即使 shell 已有展示身份也不影响其他调用方", async () => {
  usePosShellStore.getState().setTerminalPresentation({
    storeName: "Brisbane CBD",
    deviceCode: "IPAD-07",
  });

  const screen = await render(<PosStatusStrip language="zh" />);

  expect(screen.queryByTestId("status-strip-terminal-identity")).toBeNull();
});

test("同步状态初始显示检查中，成功读取显示真实数量，失败显示不可用", async () => {
  const screen = await render(<PosStatusStrip language="zh" />);

  expect(screen.getByLabelText("补传: 检查中")).toBeTruthy();
  await act(async () => {
    usePosShellStore.getState().setPendingSync({ kind: "ready", count: 3 });
  });
  expect(screen.getByLabelText("补传: 3 笔待处理")).toBeTruthy();
  await act(async () => {
    usePosShellStore.getState().setPendingSync({ kind: "unavailable" });
  });
  expect(screen.getByLabelText("补传: 不可用")).toBeTruthy();
});

test("开启后按分店名称、设备代码展示静态身份，并保留设备授权状态", async () => {
  usePosShellStore.getState().setDeviceGate("authorized");
  usePosShellStore.getState().setTerminalPresentation({
    storeName: "Brisbane Central Superstore With A Long Name",
    deviceCode: "IPAD-07",
  });
  const screen = await render(
    <PosStatusStrip
      language="zh"
      onSwitchLanguage={jest.fn()}
      showTerminalIdentity
    />,
  );

  const group = screen.getByTestId("status-strip-terminal-identity");
  expect(group.children).toHaveLength(2);
  expect(
    screen.getByTestId("status-strip-store-identity").props.accessibilityLabel,
  ).toBe(
    "分店名称: Brisbane Central Superstore With A Long Name",
  );
  expect(
    screen.getByTestId("status-strip-device-code-identity").props
      .accessibilityLabel,
  ).toBe("设备代码: IPAD-07");
  // 分店名只拿剩余宽度、超出裁切省略；设备代码保持完整。
  const storeStyle = StyleSheet.flatten(
    screen.getByTestId("status-strip-store-identity").props.style,
  );
  expect(storeStyle.flexGrow).toBe(1);
  expect(storeStyle.flexBasis).toBe(0);
  expect(storeStyle.overflow).toBe("hidden");
  expect(
    StyleSheet.flatten(
      screen.getByTestId("status-strip-device-code-identity").props.style,
    ).flexShrink,
  ).toBe(0);
  expect(
    StyleSheet.flatten(
      screen.getByTestId("status-strip-language-switch").props.style,
    ).flexShrink,
  ).toBe(0);
  expect(screen.getByLabelText("设备: 已授权")).toBeTruthy();
});

test("终端身份空值显示破折号，且绝不以 storeCode 冒充分店名称", async () => {
  usePosShellStore.getState().setTerminalPresentation({
    storeName: null,
    deviceCode: "",
    storeCode: "S001",
  } as Parameters<
    ReturnType<typeof usePosShellStore.getState>["setTerminalPresentation"]
  >[0]);

  const screen = await render(
    <PosStatusStrip language="zh" showTerminalIdentity />,
  );

  expect(screen.getAllByText("—")).toHaveLength(2);
  expect(screen.queryByText("S001")).toBeNull();
  expect(screen.getByLabelText("分店名称: —")).toBeTruthy();
  expect(screen.getByLabelText("设备代码: —")).toBeTruthy();
});

test("英文状态栏用语义图标替代长分类标签，同时保留完整无障碍文案", async () => {
  mockLanguage = "en";
  const shell = usePosShellStore.getState();
  shell.setConnectivity("online");
  shell.setDeviceGate("authorized");
  shell.setDisplay("disconnected");
  shell.setPrinter("disconnected");
  shell.setScanner("inactive");
  shell.setTerminalPresentation({
    storeName: "Brisbane Central Superstore",
    deviceCode: "POS_1042_0547",
  });

  const screen = await render(
    <PosStatusStrip language="en" showTerminalIdentity />,
  );

  for (const icon of [
    "tablet",
    "store-outline",
    "identifier",
    "wifi",
    "sync",
    "printer-outline",
    "barcode-scan",
    "monitor",
  ]) {
    expect(screen.getByText(icon)).toBeTruthy();
  }
  for (const label of [
    "Device",
    "Branch name",
    "Device code",
    "Network",
    "Sync",
    "Printer",
    "Scanner",
    "Display",
  ]) {
    expect(screen.queryByText(label)).toBeNull();
  }
  expect(
    screen.getByLabelText("Branch name: Brisbane Central Superstore"),
  ).toBeTruthy();
  expect(screen.getByLabelText("Network: Online")).toBeTruthy();
});

test("中文界面显示目标 EN 图标，并保留完整无障碍文案和 44pt 触控目标", async () => {
  const onSwitchLanguage = jest.fn();
  const screen = await render(
    <PosStatusStrip
      language="zh-CN"
      onSwitchLanguage={onSwitchLanguage}
    />,
  );

  const button = screen.getByTestId("status-strip-language-switch");
  expect(button.props.accessibilityLabel).toBe("将界面语言切换为英文");
  expect(button.props.accessibilityHint).toBe("将所有界面文字显示为英文。");
  expect(
    screen.getByTestId("status-strip-language-icon").props.children,
  ).toBe("EN");
  expect(StyleSheet.flatten(button.props.style).minHeight).toBeGreaterThanOrEqual(
    44,
  );
  expect(StyleSheet.flatten(button.props.style).minWidth).toBeGreaterThanOrEqual(
    44,
  );
  expect(screen.queryByText("English")).toBeNull();

  fireEvent.press(button);
  expect(play).toHaveBeenCalledWith("navigate");
  expect(onSwitchLanguage).toHaveBeenCalledTimes(1);
});

test("英文界面显示目标中文字形图标，并使用英文无障碍文案", async () => {
  mockLanguage = "en";
  const screen = await render(
    <PosStatusStrip language="en-AU" onSwitchLanguage={jest.fn()} />,
  );

  const button = screen.getByTestId("status-strip-language-switch");
  expect(button.props.accessibilityLabel).toBe(
    "Switch interface language to Chinese",
  );
  expect(button.props.accessibilityHint).toBe(
    "Show all interface text in Chinese.",
  );
  expect(
    screen.getByTestId("status-strip-language-icon").props.children,
  ).toBe("中");
  expect(screen.queryByText("中文")).toBeNull();
});

test("顶栏可显示品牌名与版本号，未传时两者都不占位", async () => {
  const plain = await render(<PosStatusStrip language="zh" />);
  expect(plain.queryByTestId("status-strip-brand")).toBeNull();
  expect(plain.queryByTestId("status-strip-app-version")).toBeNull();
  await plain.unmount();

  const screen = await render(
    <PosStatusStrip
      appVersionLabel="Version 1.0.12 · Build 66"
      language="zh"
      showBrand
    />,
  );
  expect(screen.getByTestId("status-strip-brand").props.children).toBe(
    "HB POS",
  );
  const version = screen.getByTestId("status-strip-app-version");
  expect(version.props.children).toBe("Version 1.0.12 · Build 66");
  // 版本号限宽、最多折两行：长版本串（带 OTA ID）不能再独占一大段宽度挤掉状态胶囊。
  expect(version.props.numberOfLines).toBe(2);
  const versionStyle = StyleSheet.flatten(version.props.style);
  expect(versionStyle.flexShrink).toBe(0);
  expect(versionStyle.maxWidth).toBe(POS_TOP_BAR_VERSION_MAX_WIDTH);
  expect(versionStyle.textAlign).toBe("right");
});

test("BLE 扫码枪图标：没有 BLE 服务时不显示，随连接状态切换颜色、图标与电量", async () => {
  const screen = await render(<PosStatusStrip language="zh" />);
  expect(screen.queryByLabelText(/^蓝牙枪:/u)).toBeNull();

  await act(async () => {
    usePosShellStore
      .getState()
      .setBleScanner({ state: "ready", batteryLevel: 85, attention: false });
  });
  expect(screen.getByLabelText("蓝牙枪: 已连接 85%")).toBeTruthy();
  expect(screen.getByText("bluetooth-connect")).toBeTruthy();

  await act(async () => {
    usePosShellStore
      .getState()
      .setBleScanner({ state: "bluetooth-off", batteryLevel: null, attention: false });
  });
  expect(screen.getByLabelText("蓝牙枪: 蓝牙已关")).toBeTruthy();
  expect(screen.getByText("bluetooth-off")).toBeTruthy();

  // 本机不支持蓝牙：整个图标消失，而不是显示一个永远灰着的状态。
  await act(async () => {
    usePosShellStore
      .getState()
      .setBleScanner({ state: "unsupported", batteryLevel: null, attention: false });
  });
  expect(screen.queryByLabelText(/^蓝牙枪:/u)).toBeNull();
});

test("蓝牙枪休眠时顶栏胶囊可点，点一下即重连；已连接时只读", async () => {
  const reconnect = jest.fn();
  const shell = usePosShellStore.getState();
  shell.setBleScanner({ state: "sleeping", batteryLevel: null, attention: false });
  shell.setBleScannerReconnect(reconnect);

  const screen = await render(<PosStatusStrip language="zh" />);
  const button = screen.getByTestId("status-strip-ble-reconnect");
  expect(screen.getByText("refresh")).toBeTruthy();
  await act(async () => {
    fireEvent.press(button);
  });
  expect(reconnect).toHaveBeenCalledTimes(1);

  await act(async () => {
    usePosShellStore
      .getState()
      .setBleScanner({ state: "ready", batteryLevel: 80, attention: false });
  });
  expect(screen.queryByTestId("status-strip-ble-reconnect")).toBeNull();
  expect(screen.getByText("已连接 80%")).toBeTruthy();
});

test("没有注册重连入口时，休眠状态也只显示、不可点", async () => {
  usePosShellStore
    .getState()
    .setBleScanner({ state: "sleeping", batteryLevel: null, attention: false });

  const screen = await render(<PosStatusStrip language="zh" />);
  expect(screen.queryByTestId("status-strip-ble-reconnect")).toBeNull();
  expect(screen.getByText("bluetooth")).toBeTruthy();
});

test("未配对时只有页面登记了配对入口（设备注册页）才可点，点一下打开配对面板", async () => {
  const openPairing = jest.fn();
  usePosShellStore
    .getState()
    .setBleScanner({ state: "not-paired", batteryLevel: null, attention: false });

  const screen = await render(<PosStatusStrip language="zh" />);
  // 没有登记入口（登录页、收银页等）：保持原样只读。
  expect(screen.queryByTestId("status-strip-ble-pair")).toBeNull();

  await act(async () => {
    usePosShellStore.getState().setBleScannerOpenPairing(openPairing);
  });
  const button = screen.getByTestId("status-strip-ble-pair");
  expect(screen.getByText("chevron-right")).toBeTruthy();
  await act(async () => {
    fireEvent.press(button);
  });
  expect(openPairing).toHaveBeenCalledTimes(1);

  // 配好后回到只读的已连接胶囊。
  await act(async () => {
    usePosShellStore
      .getState()
      .setBleScanner({ state: "ready", batteryLevel: 90, attention: false });
  });
  expect(screen.queryByTestId("status-strip-ble-pair")).toBeNull();
  expect(screen.getByText("已连接 90%")).toBeTruthy();
});

test("英文顶栏：状态胶囊文字只在胶囊内省略，分店名吸收剩余宽度，不再溢出压到相邻项", async () => {
  mockLanguage = "en";
  const shell = usePosShellStore.getState();
  shell.setConnectivity("online");
  shell.setDeviceGate("authorized");
  shell.setPendingSync({ kind: "ready", count: 12 });
  shell.setPrinter("disconnected");
  shell.setScanner("inactive");
  shell.setBleScanner({ state: "ready", batteryLevel: 85, attention: false });
  shell.setDisplay("disconnected");
  shell.setTerminalPresentation({
    storeName: "Charlestown Square",
    deviceCode: "POS_1042_2307",
  });

  const screen = await render(
    <PosStatusStrip
      appVersionLabel="v0.2.0 (23) · OTA 01a21a64"
      language="en"
      onSwitchLanguage={jest.fn()}
      showBrand
      showTerminalIdentity
    />,
  );

  // 英文已连接只显示电量，中文仍是「已连接 85%」（见上面的中文用例）。
  expect(screen.getByLabelText("BLE scanner: 85%")).toBeTruthy();
  expect(screen.getByText("85%")).toBeTruthy();

  for (const value of [
    "Authorized",
    "Online",
    "12 pending",
    "Offline",
    "Unfocused",
    "85%",
  ]) {
    for (const node of screen.getAllByText(value)) {
      // 胶囊被压窄时文字必须跟着收缩并单行省略，不能保持原宽溢出胶囊。
      expect(node.props.numberOfLines).toBe(1);
      const style = StyleSheet.flatten(node.props.style);
      expect(style.flexShrink).toBe(1);
      expect(style.minWidth).toBe(0);
    }
  }

  // 身份外框不参与布局（display: contents），分店名 / 设备代码直接参与整行分配，
  // 避免「外框被压窄、里面的文字不跟着缩」导致设备代码盖住右侧胶囊。
  expect(
    StyleSheet.flatten(
      screen.getByTestId("status-strip-terminal-identity").props.style,
    ).display,
  ).toBe("contents");
  expect(
    StyleSheet.flatten(
      screen.getByTestId("status-strip-store-identity").props.style,
    ).minWidth,
  ).toBe(POS_TOP_BAR_STORE_MIN_WIDTH);
});

test("英文状态文案保持简短，避免 iPad 横屏顶栏放不下", () => {
  // 顶栏一行要放下品牌、身份、7 个状态胶囊、语言键和版本号；iPad 10 代横屏只有 1180pt。
  // 实测英文状态值超过 13 个字符就会把其他胶囊挤出省略号，新增 / 修改文案时守住这个上限。
  const STATUS_VALUE_PREFIXES = [
    "status.device.",
    "status.network.",
    "status.sync.",
    "status.peripheral.",
    "status.scanner.",
    "status.bleScanner.",
  ];
  const NON_STRIP_KEYS = new Set([
    // 下面这些是提示条 / 无障碍文案，不显示在顶栏胶囊里。
    "status.scanner.bleNotAccepted",
    "status.bleScanner.reconnectLabel",
    "status.bleScanner.reconnectHint",
    "status.bleScanner.pairLabel",
    "status.bleScanner.pairHint",
  ]);
  const tooLong = Object.entries(en as Record<string, string>)
    .filter(
      ([key]) =>
        STATUS_VALUE_PREFIXES.some((prefix) => key.startsWith(prefix)) &&
        !NON_STRIP_KEYS.has(key),
    )
    .map(([key, value]) => [key, value.replace("{{count}}", "99")] as const)
    .filter(([, value]) => value.length > 13);

  expect(tooLong).toEqual([]);
  expect((en as Record<string, string>)["status.peripheral.disconnected"]).toBe(
    "Offline",
  );
  expect(
    (en as Record<string, string>)["status.bleScanner.readyWithBattery"],
  ).toBe("{{level}}%");
});
