import { expect, jest, test } from "@jest/globals";
import { render } from "@testing-library/react-native";

import RootLayout from "../../../app/_layout";

jest.mock("expo-keep-awake", () => ({
  useKeepAwake: jest.fn(),
}));

jest.mock("expo-splash-screen", () => ({
  hideAsync: jest.fn(() => Promise.reject(new Error("native-hide-failed"))),
}));

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Stack: () => React.createElement(Text, { testID: "root-stack" }, "stack"),
  };
});

jest.mock("@/app-providers", () => ({
  AppProviders: ({ children }: { children: React.ReactNode }) => children,
}));

jest.mock("@/core/peripherals/customer-display/native", () => ({}));

// Zeller 的根挂载点只负责按需加载 SDK（读构建环境变量、原生库），与根布局本身的行为无关，这里原样透传。
jest.mock("@/features/zeller-terminal/zeller-app-boundary", () => ({
  ZellerAppBoundary: ({ children }: { children: React.ReactNode }) => children,
}));

jest.mock("@/ui/scanner/scanner-route-bridge", () => ({
  ScannerRouteProvider: ({ children }: { children: React.ReactNode }) =>
    children,
}));

// 导航桥依赖路由、收银员会话和各页面权限；这里只验证根布局的装配顺序。
jest.mock("@/features/navigation/pos-navigation-bridge", () => ({
  PosNavigationBridge: ({ children }: { children: React.ReactNode }) =>
    children,
}));

const mockedKeepAwake = jest.requireMock<{ useKeepAwake: jest.Mock }>(
  "expo-keep-awake",
);
const mockedSplashScreen = jest.requireMock<{ hideAsync: jest.Mock }>(
  "expo-splash-screen",
);

test("启动遮罩关闭失败也不阻断根路由与常亮能力", async () => {
  expect(mockedSplashScreen.hideAsync).toHaveBeenCalledTimes(1);

  const screen = await render(<RootLayout />);

  expect(mockedKeepAwake.useKeepAwake).toHaveBeenCalledTimes(1);
  expect(screen.getByTestId("root-stack")).toBeTruthy();
});
