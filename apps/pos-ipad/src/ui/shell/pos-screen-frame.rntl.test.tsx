import { afterEach, expect, jest, test } from "@jest/globals";
import { act, render } from "@testing-library/react-native";
import { StyleSheet, Text } from "react-native";
import { SafeAreaInsetsContext } from "react-native-safe-area-context";

import { PosPageHeader, PosScreenFrame } from "./pos-screen-frame";

import { applyPosTheme, POS_THEMES } from "@/ui/theme";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string) => key,
  }),
}));

afterEach(async () => {
  await act(async () => {
    applyPosTheme("classic");
  });
});

const INSETS = { bottom: 20, left: 0, right: 0, top: 24 };

test("顶部安全区铺成顶栏颜色，随主题变化；没有安全区或隐藏顶栏时不渲染", async () => {
  const withInsets = await render(
    <SafeAreaInsetsContext.Provider value={INSETS}>
      <PosScreenFrame rail="none">
        <Text>content</Text>
      </PosScreenFrame>
    </SafeAreaInsetsContext.Provider>,
  );
  const fill = withInsets.getByTestId("pos-top-inset-fill");
  expect(StyleSheet.flatten(fill.props.style)).toMatchObject({
    backgroundColor: POS_THEMES.classic.palette.chrome,
    height: 24,
  });

  await act(async () => {
    applyPosTheme("dark");
  });
  expect(
    StyleSheet.flatten(withInsets.getByTestId("pos-top-inset-fill").props.style)
      .backgroundColor,
  ).toBe(POS_THEMES.dark.palette.chrome);
  await withInsets.unmount();

  const withoutProvider = await render(
    <PosScreenFrame rail="none">
      <Text>content</Text>
    </PosScreenFrame>,
  );
  expect(withoutProvider.queryByTestId("pos-top-inset-fill")).toBeNull();
  await withoutProvider.unmount();

  const hiddenTopBar = await render(
    <SafeAreaInsetsContext.Provider value={INSETS}>
      <PosScreenFrame rail="none" topBar={false}>
        <Text>content</Text>
      </PosScreenFrame>
    </SafeAreaInsetsContext.Provider>,
  );
  expect(hiddenTopBar.queryByTestId("pos-top-inset-fill")).toBeNull();
});

test("页头：有返回回调与文案才显示返回键，标题、副标题与右侧操作都在", async () => {
  const onBack = jest.fn();
  const screen = await render(
    <PosPageHeader
      actions={<Text>actions</Text>}
      backLabel="返回收银"
      backTestID="header-back"
      onBack={onBack}
      subtitle="副标题"
      title="标题"
    />,
  );
  expect(screen.getByText("标题")).toBeTruthy();
  expect(screen.getByText("副标题")).toBeTruthy();
  expect(screen.getByText("actions")).toBeTruthy();
  expect(screen.getByTestId("header-back")).toBeTruthy();
  await screen.unmount();

  const noBack = await render(<PosPageHeader title="标题" />);
  expect(noBack.queryByTestId("header-back")).toBeNull();
});
