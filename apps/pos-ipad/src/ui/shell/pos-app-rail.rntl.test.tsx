import { afterEach, expect, jest, test } from "@jest/globals";
import { act, fireEvent, render } from "@testing-library/react-native";
import { SafeAreaInsetsContext } from "react-native-safe-area-context";

import { PosAppRail, type PosNavEntry, type PosNavModel } from "./pos-app-rail";
import {
  DEFAULT_PINNED_DESTINATIONS,
  type PosDestinationId,
} from "./pos-navigation";

import { applyPosTheme } from "@/ui/theme";
import { PosThemeControllerContext } from "@/ui/theme-controller";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string, values?: Record<string, unknown>) =>
      values ? `${key}:${JSON.stringify(values)}` : key,
  }),
}));

// 窗口尺寸可按用例改；null 表示沿用 RN 测试环境的默认尺寸。
const mockWindow: { size: { height: number; width: number } | null } = {
  size: null,
};
jest.mock("react-native/Libraries/Utilities/useWindowDimensions", () => {
  const actual = jest.requireActual<{
    default: () => {
      fontScale: number;
      height: number;
      scale: number;
      width: number;
    };
  }>("react-native/Libraries/Utilities/useWindowDimensions");
  return {
    __esModule: true,
    default: () =>
      mockWindow.size
        ? { fontScale: 1, scale: 2, ...mockWindow.size }
        : actual.default(),
  };
});

afterEach(async () => {
  mockWindow.size = null;
  await act(async () => {
    applyPosTheme("classic");
  });
});

const ALL_IDS: readonly PosDestinationId[] = [
  "held-orders",
  "returns",
  "installments",
  "payment-recovery",
  "local-history",
  "remote-history",
  "sync-history",
  "daily-close",
  "attendance-audit",
  "catalog-maintenance",
  "settings",
];

function createModel(overrides: Partial<PosNavModel> = {}) {
  const presses = new Map<PosDestinationId, jest.Mock>();
  const entries: PosNavEntry[] = ALL_IDS.map((id) => {
    const onPress = jest.fn();
    presses.set(id, onPress);
    return { description: `desc ${id}`, id, label: `label ${id}`, onPress };
  });
  const model: PosNavModel = {
    entries,
    onOpenSales: jest.fn(),
    onPinnedChange: jest.fn(),
    pinned: [...DEFAULT_PINNED_DESTINATIONS],
    ...overrides,
  };
  return { model, presses };
}

test("侧栏只显示收银、固定项、更多；当前页高亮且再点不跳转", async () => {
  const { model, presses } = createModel();
  const screen = await render(<PosAppRail active="returns" model={model} />);

  for (const id of DEFAULT_PINNED_DESTINATIONS) {
    expect(screen.getByTestId(`pos-rail-${id}`)).toBeTruthy();
  }
  expect(screen.queryByTestId("pos-rail-settings")).toBeNull();
  expect(screen.queryByTestId("pos-rail-lock")).toBeNull();
  expect(
    screen.getByTestId("pos-rail-returns").props.accessibilityState.selected,
  ).toBe(true);

  await fireEvent.press(screen.getByTestId("pos-rail-returns"));
  expect(presses.get("returns")).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("pos-rail-held-orders"));
  expect(presses.get("held-orders")).toHaveBeenCalledTimes(1);

  await fireEvent.press(screen.getByTestId("pos-rail-sales"));
  expect(model.onOpenSales).toHaveBeenCalledTimes(1);
});

test("当前页不在侧栏里时高亮「更多」；没权限的入口不出现", async () => {
  const { model } = createModel();
  const limited: PosNavModel = {
    ...model,
    entries: model.entries.filter((entry) => entry.id !== "daily-close"),
  };
  const screen = await render(<PosAppRail active="settings" model={limited} />);

  expect(
    screen.getByTestId("pos-rail-more").props.accessibilityState.selected,
  ).toBe(true);
  expect(screen.queryByTestId("pos-rail-daily-close")).toBeNull();
});

test("「全部功能」面板列出所有入口，点入口后关闭面板并跳转", async () => {
  const { model, presses } = createModel();
  const screen = await render(<PosAppRail active="sales" model={model} />);

  expect(screen.queryByTestId("pos-more")).toBeNull();
  await fireEvent.press(screen.getByTestId("pos-rail-more"));

  for (const id of ALL_IDS) {
    expect(screen.getByTestId(`pos-more-${id}`)).toBeTruthy();
  }
  await fireEvent.press(screen.getByTestId("pos-more-settings"));

  expect(presses.get("settings")).toHaveBeenCalledTimes(1);
  expect(screen.queryByTestId("pos-more")).toBeNull();
});

test("固定入口到侧栏；已到上限时提示而不是替换已有项", async () => {
  const { model } = createModel({ pinned: ["returns"] });
  const screen = await render(<PosAppRail active="sales" model={model} />);
  await fireEvent.press(screen.getByTestId("pos-rail-more"));

  await fireEvent.press(screen.getByTestId("pos-more-pin-settings"));
  expect(model.onPinnedChange).toHaveBeenLastCalledWith(["returns", "settings"]);

  await fireEvent.press(screen.getByTestId("pos-more-pin-returns"));
  expect(model.onPinnedChange).toHaveBeenLastCalledWith([]);

  const full = createModel();
  const fullScreen = await render(
    <PosAppRail active="sales" model={full.model} pinnedCapacity={5} />,
  );
  await fireEvent.press(fullScreen.getByTestId("pos-rail-more"));
  await fireEvent.press(fullScreen.getByTestId("pos-more-pin-settings"));

  expect(full.model.onPinnedChange).not.toHaveBeenCalled();
  expect(fullScreen.getByTestId("pos-more-pin-hint").props.children).toBe(
    `nav.pinLimit:${JSON.stringify({ max: 5 })}`,
  );
});

test("13 寸能固定 6 个：默认 5 个之外还能再固定 1 个，侧栏只显示前 capacity 个", async () => {
  const six = createModel();
  const sixScreen = await render(
    <PosAppRail active="sales" model={six.model} pinnedCapacity={6} />,
  );
  await fireEvent.press(sixScreen.getByTestId("pos-rail-more"));
  expect(sixScreen.getByTestId("pos-more-pin-hint").props.children).toBe(
    `nav.pinHint:${JSON.stringify({ max: 6 })}`,
  );
  await fireEvent.press(sixScreen.getByTestId("pos-more-pin-settings"));
  expect(six.model.onPinnedChange).toHaveBeenLastCalledWith([
    ...DEFAULT_PINNED_DESTINATIONS,
    "settings",
  ]);

  // 本机存了 6 个，但这台屏幕只放得下 5 个：侧栏显示前 5 个，第 6 个留在存储里不丢。
  const overflow = createModel({
    pinned: [...DEFAULT_PINNED_DESTINATIONS, "settings"],
  });
  const overflowScreen = await render(
    <PosAppRail active="sales" model={overflow.model} pinnedCapacity={5} />,
  );
  for (const id of DEFAULT_PINNED_DESTINATIONS) {
    expect(overflowScreen.getByTestId(`pos-rail-${id}`)).toBeTruthy();
  }
  expect(overflowScreen.queryByTestId("pos-rail-settings")).toBeNull();
});

test("没给 pinnedCapacity 时按窗口高度算：iPad Pro 13 寸横屏能固定 6 个", async () => {
  mockWindow.size = { height: 1032, width: 1376 };
  const { model } = createModel({
    pinned: [...DEFAULT_PINNED_DESTINATIONS, "settings", "sync-history"],
  });
  const screen = await render(
    <SafeAreaInsetsContext.Provider
      value={{ bottom: 20, left: 0, right: 0, top: 24 }}
    >
      <PosAppRail active="sales" model={model} />
    </SafeAreaInsetsContext.Provider>,
  );
  expect(screen.getByTestId("pos-rail-settings")).toBeTruthy();
  expect(screen.queryByTestId("pos-rail-sync-history")).toBeNull();
});

test("面板里可以切换主题、语言和锁屏", async () => {
  const selectTheme = jest.fn();
  const onSwitchLanguage = jest.fn();
  const onLock = jest.fn();
  const { model } = createModel({ onLock, onSwitchLanguage });
  const screen = await render(
    <PosThemeControllerContext.Provider value={{ selectTheme }}>
      <PosAppRail active="sales" model={model} />
    </PosThemeControllerContext.Provider>,
  );

  expect(screen.getByTestId("pos-rail-lock")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("pos-rail-more"));

  await fireEvent.press(screen.getByTestId("pos-more-theme-dark"));
  expect(selectTheme).toHaveBeenCalledWith("dark");

  await fireEvent.press(screen.getByTestId("pos-more-language"));
  expect(onSwitchLanguage).toHaveBeenCalledTimes(1);

  await fireEvent.press(screen.getByTestId("pos-more-lock"));
  expect(onLock).toHaveBeenCalledTimes(1);
  expect(screen.queryByTestId("pos-more")).toBeNull();
});

test("页面锁住导航时，侧栏所有跳转都不可点", async () => {
  const { model, presses } = createModel();
  const screen = await render(
    <PosAppRail active="returns" locked model={model} />,
  );

  for (const testID of [
    "pos-rail-sales",
    "pos-rail-held-orders",
    "pos-rail-more",
  ]) {
    expect(screen.getByTestId(testID).props.accessibilityState.disabled).toBe(
      true,
    );
  }
  await fireEvent.press(screen.getByTestId("pos-rail-held-orders"));
  expect(presses.get("held-orders")).not.toHaveBeenCalled();
  expect(screen.queryByTestId("pos-more")).toBeNull();
});
