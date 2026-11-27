import { afterEach, expect, jest, test } from "@jest/globals";
import { act, render } from "@testing-library/react-native";

import {
  POS_ROUTE_LOADING_INDICATOR_DELAY_MS,
  PosRouteLoadingScreen,
} from "./pos-route-loading-screen";

import type { PosNavModel } from "@/ui/shell/pos-app-rail";
import {
  PosNavigationContext,
  type PosNavigationContextValue,
} from "@/ui/shell/pos-navigation-context";
import { PosShellChromeStore, PosShellHost } from "@/ui/shell/pos-shell-host";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string) => key,
  }),
}));

// 侧栏 / 顶栏换成假组件：这里只验证占位页向外壳登记了什么。
jest.mock("@/ui/shell/pos-app-rail", () => {
  const { Text: MockText } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    PosAppRail: (props: { active: string }) => (
      <MockText testID="mock-rail">{`rail:${props.active}`}</MockText>
    ),
  };
});
jest.mock("@/ui/shell/status-strip", () => {
  const { Text: MockText } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return { PosStatusStrip: () => <MockText testID="mock-status-strip" /> };
});

const model: PosNavModel = { entries: [], pinned: [] };
const navigation: PosNavigationContextValue = {
  appVersionLabel: "0.2.0 (22)",
  language: "zh",
  model,
  openSales: () => undefined,
  switchLanguage: () => undefined,
};

afterEach(() => {
  jest.useRealTimers();
});

test("在常驻外壳里加载时保留左侧栏并提前高亮目标页，转圈延迟出现", async () => {
  jest.useFakeTimers();
  const store = new PosShellChromeStore();
  const screen = await render(
    <PosNavigationContext.Provider value={navigation}>
      <PosShellHost store={store}>
        <PosRouteLoadingScreen active="returns" />
      </PosShellHost>
    </PosNavigationContext.Provider>,
  );

  // 以前的 BootstrapScreen 登记 model: null，左侧栏会在切页时消失；现在保留同一份侧栏模型。
  expect(store.getSnapshot()).toMatchObject({ active: "returns", model });
  expect(screen.getByTestId("mock-rail")).toHaveTextContent("rail:returns");
  expect(screen.getByTestId("pos-route-loading")).toBeTruthy();
  expect(screen.queryByTestId("pos-route-loading-indicator")).toBeNull();

  await act(async () => {
    jest.advanceTimersByTime(POS_ROUTE_LOADING_INDICATOR_DELAY_MS);
  });
  expect(screen.getByTestId("pos-route-loading-indicator")).toBeTruthy();
});
