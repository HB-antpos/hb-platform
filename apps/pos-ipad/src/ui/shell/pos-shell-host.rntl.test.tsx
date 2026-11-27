import { expect, jest, test } from "@jest/globals";
import { act, render } from "@testing-library/react-native";
import type { ReactNode } from "react";
import { Text } from "react-native";

import type { PosNavModel } from "./pos-app-rail";
import { PosScreenFrame } from "./pos-screen-frame";
import {
  PosShellChromeStore,
  PosShellHost,
  PosShellScreen,
  type PosShellScreenNavigation,
} from "./pos-shell-host";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string) => key,
  }),
}));

// 侧栏 / 顶栏换成只记录参数的假组件：这里只验证外壳把谁画了几次、用的哪一页的配置。
const mockRailRenders: { active: string; locked: boolean; model: unknown }[] = [];
jest.mock("./pos-app-rail", () => {
  const { Text: MockText } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    PosAppRail: (props: { active: string; locked: boolean; model: unknown }) => {
      mockRailRenders.push(props);
      return <MockText testID="mock-rail">{`rail:${props.active}`}</MockText>;
    },
  };
});
jest.mock("./status-strip", () => {
  const { Text: MockText } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    PosStatusStrip: (props: { appVersionLabel: string | null }) => (
      <MockText testID="mock-status-strip">{`status:${props.appVersionLabel}`}</MockText>
    ),
  };
});

const model: PosNavModel = {
  entries: [],
  pinned: [],
};

type FakeNavigation = Readonly<{
  setFocused(focused: boolean): void;
  value: PosShellScreenNavigation;
}>;

/** 最小的导航对象：只实现 PosScreenFrame 用到的 isFocused / addListener。 */
function fakeNavigation(initiallyFocused: boolean): FakeNavigation {
  let focused = initiallyFocused;
  const listeners = { blur: new Set<() => void>(), focus: new Set<() => void>() };
  return {
    setFocused(next) {
      focused = next;
      for (const listener of listeners[next ? "focus" : "blur"]) listener();
    },
    value: {
      addListener(event: "focus" | "blur", listener: () => void) {
        listeners[event].add(listener);
        return () => listeners[event].delete(listener);
      },
      isFocused: () => focused,
    },
  };
}

function Screen({
  children,
  navigation,
}: Readonly<{ children: ReactNode; navigation: FakeNavigation }>) {
  return (
    <PosShellScreen navigation={navigation.value}>{children}</PosShellScreen>
  );
}

test("在常驻外壳里：页面只渲染页头与内容，顶栏与左侧栏由外壳画一份，用获得焦点那一页的配置", async () => {
  const store = new PosShellChromeStore();
  const sales = fakeNavigation(false);
  const settings = fakeNavigation(true);
  mockRailRenders.length = 0;
  const screen = await render(
    <PosShellHost store={store}>
      <Screen navigation={sales}>
        <PosScreenFrame active="sales" appVersionLabel="0.2.0 (22)" rail={model} testID="sales-frame">
          <Text>sales-content</Text>
        </PosScreenFrame>
      </Screen>
      <Screen navigation={settings}>
        <PosScreenFrame active="settings" rail={model} testID="settings-frame">
          <Text>settings-content</Text>
        </PosScreenFrame>
      </Screen>
    </PosShellHost>,
  );

  // 只有一份侧栏和顶栏，且都在外壳里，不在页面里。
  expect(screen.getAllByTestId("mock-rail")).toHaveLength(1);
  expect(screen.getAllByTestId("mock-status-strip")).toHaveLength(1);
  expect(screen.getByText("rail:settings")).toBeTruthy();
  expect(screen.getByText("sales-content")).toBeTruthy();
  expect(screen.getByText("settings-content")).toBeTruthy();

  // 切回收银：收银页获得焦点后由它的配置接管外壳（高亮、版本号）。
  await act(async () => {
    settings.setFocused(false);
    sales.setFocused(true);
  });
  expect(screen.getByText("rail:sales")).toBeTruthy();
  expect(screen.getByText("status:0.2.0 (22)")).toBeTruthy();
  expect(screen.getAllByTestId("mock-rail")).toHaveLength(1);
});

test("获得焦点的页面要求隐藏侧栏 / 顶栏、或锁住侧栏时外壳照办；页面卸载后撤回配置", async () => {
  const store = new PosShellChromeStore();
  const navigation = fakeNavigation(true);
  mockRailRenders.length = 0;
  const screen = await render(
    <PosShellHost store={store}>
      <Screen navigation={navigation}>
        <PosScreenFrame active="returns" navigationLocked rail={model}>
          <Text>returns</Text>
        </PosScreenFrame>
      </Screen>
    </PosShellHost>,
  );
  expect(mockRailRenders.at(-1)).toMatchObject({ active: "returns", locked: true });

  await screen.rerender(
    <PosShellHost store={store}>
      <Screen navigation={navigation}>
        <PosScreenFrame rail="none" topBar={false}>
          <Text>sales-success</Text>
        </PosScreenFrame>
      </Screen>
    </PosShellHost>,
  );
  expect(screen.queryByTestId("mock-rail")).toBeNull();
  expect(screen.queryByTestId("mock-status-strip")).toBeNull();

  await screen.rerender(<PosShellHost store={store}>{null}</PosShellHost>);
  expect(store.getSnapshot()).toBeNull();
});

test("不在常驻外壳里（登录、付款、单独渲染）时照旧整页渲染自己的顶栏与侧栏", async () => {
  mockRailRenders.length = 0;
  const screen = await render(
    <PosScreenFrame active="sales" rail={model}>
      <Text>standalone</Text>
    </PosScreenFrame>,
  );
  expect(screen.getByTestId("mock-rail")).toBeTruthy();
  expect(screen.getByTestId("mock-status-strip")).toBeTruthy();
  expect(screen.queryByTestId("pos-shell-host")).toBeNull();
});
