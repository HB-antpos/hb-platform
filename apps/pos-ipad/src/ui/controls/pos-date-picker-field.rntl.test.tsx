/* eslint-disable @typescript-eslint/no-require-imports -- 原生日期组件必须在加载被测组件前替换。 */
import { afterEach, expect, jest, test } from "@jest/globals";
import { fireEvent, render } from "@testing-library/react-native";
import { Keyboard, StyleSheet } from "react-native";

jest.doMock("@react-native-community/datetimepicker", () => {
  const React = require("react");
  const { View } = require("react-native");

  return {
    __esModule: true,
    default: (props: Record<string, unknown>) =>
      React.createElement(View, props),
  };
});

const playTouchSound = jest.fn();

jest.doMock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({
    buttonSoundEnabled: true,
    play: playTouchSound,
    setButtonSoundEnabled: jest.fn(),
    setSpecialNodeSoundEnabled: jest.fn(),
    specialNodeSoundEnabled: true,
  }),
}));

const { PosDatePickerField } =
  require("./pos-date-picker-field") as typeof import("./pos-date-picker-field");

afterEach(() => {
  jest.restoreAllMocks();
  playTouchSound.mockReset();
});

function renderedTypes(node: unknown): string[] {
  if (Array.isArray(node)) return node.flatMap(renderedTypes);
  if (!node || typeof node !== "object") return [];
  const rendered = node as { children?: unknown; type?: unknown };
  return [
    ...(typeof rendered.type === "string" ? [rendered.type] : []),
    ...renderedTypes(rendered.children),
  ];
}

test("触发器不是输入框，打开时关闭键盘并使用横屏 inline 日期弹层", async () => {
  const dismiss = jest.spyOn(Keyboard, "dismiss").mockImplementation(() => undefined);
  const screen = await render(
    <PosDatePickerField
      accessibilityLabel="开始日期"
      locale="zh"
      onChange={jest.fn()}
      testID="date-from"
      value="2026-07-29"
    />,
  );

  const trigger = screen.getByTestId("date-from");
  expect(trigger.props.accessibilityLabel).toBe("开始日期");
  expect(trigger.props.accessibilityRole).toBe("button");
  expect(renderedTypes(screen.toJSON())).not.toContain("TextInput");
  // 触发器与输入框同高（56）。
  expect(StyleSheet.flatten(trigger.props.style).minHeight).toBeGreaterThanOrEqual(56);

  await fireEvent.press(trigger);

  expect(dismiss).toHaveBeenCalledTimes(1);
  expect(screen.getByTestId("date-from-modal").props).toMatchObject({
    presentationStyle: "overFullScreen",
    supportedOrientations: ["landscape-left", "landscape-right"],
    transparent: true,
    visible: true,
  });
  expect(screen.getByTestId("date-from-picker").props).toMatchObject({
    display: "inline",
    mode: "date",
  });
  // 弹层里的取消 / 确定是 56 高的大按钮。
  for (const testID of ["date-from-cancel", "date-from-confirm"]) {
    expect(
      StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
    ).toBeGreaterThanOrEqual(56);
  }
});

test("日期触发器使用 navigate 音，禁用时保持静默", async () => {
  const screen = await render(
    <>
      <PosDatePickerField
        accessibilityLabel="Start date"
        locale="en"
        onChange={jest.fn()}
        testID="date-from"
        value="2026-07-29"
      />
      <PosDatePickerField
        accessibilityLabel="End date"
        disabled
        locale="en"
        onChange={jest.fn()}
        testID="date-to"
        value="2026-07-29"
      />
    </>,
  );

  await fireEvent.press(screen.getByTestId("date-from"));
  await fireEvent.press(screen.getByTestId("date-to"));

  expect(playTouchSound).toHaveBeenCalledTimes(1);
  expect(playTouchSound).toHaveBeenCalledWith("navigate");
});

test("日期弹层的清除、取消与确认分别使用 danger、tap 与 navigate 音", async () => {
  const screen = await render(
    <PosDatePickerField
      accessibilityLabel="End date"
      allowClear
      locale="en"
      onChange={jest.fn()}
      testID="date-to"
      value="2026-07-29"
    />,
  );

  await fireEvent.press(screen.getByTestId("date-to"));
  playTouchSound.mockClear();
  await fireEvent.press(screen.getByTestId("date-to-clear"));
  expect(playTouchSound).toHaveBeenCalledWith("danger");

  await fireEvent.press(screen.getByTestId("date-to"));
  playTouchSound.mockClear();
  await fireEvent.press(screen.getByTestId("date-to-cancel"));
  expect(playTouchSound).toHaveBeenCalledWith("tap");

  await fireEvent.press(screen.getByTestId("date-to"));
  playTouchSound.mockClear();
  await fireEvent.press(screen.getByTestId("date-to-confirm"));
  expect(playTouchSound).toHaveBeenCalledWith("navigate");
});

test("取消只关闭弹层，不提交草稿日期", async () => {
  const onChange = jest.fn();
  const screen = await render(
    <PosDatePickerField
      accessibilityLabel="Start date"
      locale="en"
      onChange={onChange}
      testID="date-from"
      value="2026-07-29"
    />,
  );

  await fireEvent.press(screen.getByTestId("date-from"));
  await fireEvent(
    screen.getByTestId("date-from-picker"),
    "onChange",
    { type: "set" },
    new Date(2026, 7, 3, 21, 45),
  );
  await fireEvent.press(screen.getByTestId("date-from-cancel"));

  expect(onChange).not.toHaveBeenCalled();
  expect(screen.queryByTestId("date-from-modal")).toBeNull();
});

test("确定使用 Date 的本地年月日生成日历键", async () => {
  const onChange = jest.fn();
  const screen = await render(
    <PosDatePickerField
      accessibilityLabel="Start date"
      locale="en"
      onChange={onChange}
      testID="date-from"
      value="2026-07-29"
    />,
  );

  await fireEvent.press(screen.getByTestId("date-from"));
  await fireEvent(
    screen.getByTestId("date-from-picker"),
    "onChange",
    { type: "set" },
    new Date(2026, 7, 3, 23, 59),
  );
  await fireEvent.press(screen.getByTestId("date-from-confirm"));

  expect(onChange).toHaveBeenCalledTimes(1);
  expect(onChange).toHaveBeenCalledWith("2026-08-03");
  expect(screen.queryByTestId("date-from-modal")).toBeNull();
});

test("可选日期显示不限日期并可在弹层中清除", async () => {
  const onChange = jest.fn();
  const screen = await render(
    <PosDatePickerField
      accessibilityLabel="结束日期"
      allowClear
      locale="zh"
      onChange={onChange}
      testID="date-to"
      value={null}
    />,
  );

  expect(screen.getByText("不限日期")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("date-to"));
  expect(screen.getByText("清除")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("date-to-clear"));

  expect(onChange).toHaveBeenCalledTimes(1);
  expect(onChange).toHaveBeenCalledWith(null);
  expect(screen.queryByTestId("date-to-modal")).toBeNull();
});

test("必填空值显示选择提示，禁用状态不可打开弹层", async () => {
  const dismiss = jest.spyOn(Keyboard, "dismiss").mockImplementation(() => undefined);
  const onChange = jest.fn();
  const screen = await render(
    <PosDatePickerField
      accessibilityLabel="Business date"
      disabled
      locale="en"
      onChange={onChange}
      testID="business-date"
      value={null}
    />,
  );

  expect(screen.getByText("Select date")).toBeTruthy();
  const trigger = screen.getByTestId("business-date");
  expect(trigger.props.accessibilityState).toEqual({ disabled: true });
  await fireEvent.press(trigger);

  expect(dismiss).not.toHaveBeenCalled();
  expect(onChange).not.toHaveBeenCalled();
  expect(screen.queryByTestId("business-date-modal")).toBeNull();
});

test("日期选择弹层点击面板外遮罩取消且不回调", async () => {
  const onChange = jest.fn();
  const screen = await render(
    <PosDatePickerField
      accessibilityLabel="开始日期"
      locale="zh"
      onChange={onChange}
      testID="date-from"
      value="2026-07-29"
    />,
  );

  await fireEvent.press(screen.getByTestId("date-from"));
  expect(screen.getByTestId("date-from-modal")).toBeTruthy();
  await fireEvent.press(screen.getByTestId("date-from-backdrop"));
  expect(screen.queryByTestId("date-from-modal")).toBeNull();
  expect(onChange).not.toHaveBeenCalled();
});
