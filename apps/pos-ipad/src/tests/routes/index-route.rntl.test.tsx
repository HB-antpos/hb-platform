import { beforeEach, expect, jest, test } from "@jest/globals";
import { fireEvent, render } from "@testing-library/react-native";

import IndexScreen from "../../../app/index";

const mockResolvePosEntryRoute = jest.fn();
const mockRouterPush = jest.fn();
let mockRuntime: unknown;
let mockActiveCashier: unknown;
let mockOrderingUser: unknown;

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Redirect: ({ href }: { href: string }) =>
      React.createElement(Text, { testID: "redirect" }, href),
    router: { push: (href: string) => mockRouterPush(href) },
  };
});

jest.mock("react-i18next", () => ({
  useTranslation: () => ({ i18n: { language: "zh", resolvedLanguage: "zh" } }),
}));

jest.mock("@/i18n", () => ({ toggleAppLanguage: jest.fn() }));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

jest.mock("@/features/cashier-login", () => ({
  resolvePosEntryRoute: (...args: unknown[]) => mockResolvePosEntryRoute(...args),
  useCashierLoginStore: (selector: (state: { activeCashier: unknown }) => unknown) =>
    selector({ activeCashier: mockActiveCashier }),
}));

jest.mock("@/features/ordering", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Pressable, Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    useOrderingSessionStore: (selector: (state: { user: unknown }) => unknown) =>
      selector({ user: mockOrderingUser }),
    PosEntryScreen: (props: { registered: boolean; onCashier(): void; onOrdering(): void }) =>
      React.createElement(
        React.Fragment,
        null,
        React.createElement(Text, { testID: "entry-mode" }, props.registered ? "registered" : "unregistered"),
        React.createElement(Pressable, { testID: "entry-cashier", onPress: props.onCashier }),
        React.createElement(Pressable, { testID: "entry-ordering", onPress: props.onOrdering }),
      ),
  };
});

jest.mock("@/ui/screens/bootstrap-screen", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    BootstrapScreen: () =>
      React.createElement(Text, { testID: "bootstrap" }, "bootstrap"),
  };
});

beforeEach(() => {
  mockRuntime = { state: { phase: "bootstrapping" } };
  mockActiveCashier = null;
  mockOrderingUser = null;
  mockResolvePosEntryRoute.mockReset();
  mockRouterPush.mockReset();
});

test("入口尚未解析路由时展示 BootstrapScreen", async () => {
  mockResolvePosEntryRoute.mockReturnValue(null);

  const screen = await render(<IndexScreen />);

  expect(screen.getByTestId("bootstrap")).toBeTruthy();
  expect(mockResolvePosEntryRoute).toHaveBeenCalledWith(
    (mockRuntime as { state: unknown }).state,
    null,
  );
  await screen.unmount();
});

test("收银员已登录时照旧直接进收银页", async () => {
  mockResolvePosEntryRoute.mockReturnValue("/sales");

  const screen = await render(<IndexScreen />);

  expect(screen.getByTestId("redirect").props.children).toBe("/sales");
  await screen.unmount();
});

test("未注册设备：两个入口，收银去注册，订货去员工登录", async () => {
  mockResolvePosEntryRoute.mockReturnValue("/registration");

  const screen = await render(<IndexScreen />);

  expect(screen.getByTestId("entry-mode").props.children).toBe("unregistered");
  await fireEvent.press(screen.getByTestId("entry-cashier"));
  await fireEvent.press(screen.getByTestId("entry-ordering"));
  expect(mockRouterPush.mock.calls).toEqual([["/registration"], ["/ordering-login"]]);
  await screen.unmount();
});

test("已注册设备：收银去收银员登录；订货已登录时直接回订货工作台", async () => {
  mockResolvePosEntryRoute.mockReturnValue("/login");
  mockOrderingUser = { userGuid: "G", username: "sean", displayName: "Sean" };

  const screen = await render(<IndexScreen />);

  expect(screen.getByTestId("entry-mode").props.children).toBe("registered");
  await fireEvent.press(screen.getByTestId("entry-cashier"));
  await fireEvent.press(screen.getByTestId("entry-ordering"));
  expect(mockRouterPush.mock.calls).toEqual([["/login"], ["/ordering"]]);
  await screen.unmount();
});
