import { expect, jest, test } from "@jest/globals";
import { render, within } from "@testing-library/react-native";

import PosShellLayout, {
  POS_SHELL_CONTENT_FADE_MS,
} from "../../../app/(pos)/_layout";

import { posShellScreenLayout } from "@/ui/shell/pos-shell-host";

let mockStackProps: Record<string, unknown> | null = null;
jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const { Text } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    Stack: (props: Record<string, unknown>) => {
      mockStackProps = props;
      return React.createElement(Text, { testID: "pos-group-stack" }, "stack");
    },
  };
});

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string) => key,
  }),
}));

test("带左侧栏的功能页共用常驻外壳：路由栈只在内容区里，切页只淡入淡出内容区", async () => {
  const screen = await render(<PosShellLayout />);

  // 路由栈挂在外壳的内容区里，顶栏 / 侧栏不随切页卸载。
  const content = screen.getByTestId("pos-shell-content");
  expect(within(content).getByTestId("pos-group-stack")).toBeTruthy();
  expect(mockStackProps?.screenOptions).toMatchObject({
    animation: "fade",
    animationDuration: POS_SHELL_CONTENT_FADE_MS,
    headerShown: false,
  });
  // 每一页都包一层焦点跟踪，只有当前页能把侧栏高亮等配置登记给外壳。
  expect(mockStackProps?.screenLayout).toBe(posShellScreenLayout);
  await screen.unmount();
});
