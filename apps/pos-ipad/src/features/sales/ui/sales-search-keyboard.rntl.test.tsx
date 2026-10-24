import { describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render } from "@testing-library/react-native";

import {
  applySalesSearchKey,
  SALES_SEARCH_QUERY_MAX_LENGTH,
  SalesSearchKeyboard,
  type SalesSearchKey,
} from "./sales-search-keyboard";

import { PosSoundContext } from "@/ui/feedback/pos-sound-context";

const labels = {
  title: "Search keyboard",
  backspace: "Delete",
  space: "Space",
  clear: "Clear",
  search: "Search",
  close: "Hide",
  system: "System keyboard",
};

describe("applySalesSearchKey", () => {
  it("字符追加、退格和清空按搜索词逐步编辑", () => {
    let value = "";
    value = applySalesSearchKey(value, { kind: "char", char: "A" });
    value = applySalesSearchKey(value, { kind: "char", char: "4" });
    expect(value).toBe("A4");
    expect(applySalesSearchKey(value, { kind: "backspace" })).toBe("A");
    expect(applySalesSearchKey("", { kind: "backspace" })).toBe("");
    expect(applySalesSearchKey(value, { kind: "clear" })).toBe("");
  });

  it("空格不出现在开头且不连续", () => {
    expect(applySalesSearchKey("", { kind: "space" })).toBe("");
    expect(applySalesSearchKey("MILK", { kind: "space" })).toBe("MILK ");
    expect(applySalesSearchKey("MILK ", { kind: "space" })).toBe("MILK ");
  });

  it("达到长度上限后不再追加字符或空格", () => {
    const full = "X".repeat(SALES_SEARCH_QUERY_MAX_LENGTH);
    expect(applySalesSearchKey(full, { kind: "char", char: "Y" })).toBe(full);
    expect(applySalesSearchKey(full, { kind: "space" })).toBe(full);
  });

  it("搜索、收起和系统键盘不改变搜索词", () => {
    for (const kind of ["search", "close", "system"] as const) {
      expect(applySalesSearchKey("ABC", { kind })).toBe("ABC");
    }
  });
});

describe("SalesSearchKeyboard", () => {
  it("分发各类按键并使用对应的按键音", async () => {
    const onKeyPress = jest.fn<(key: SalesSearchKey) => void>();
    const play = jest.fn();
    const screen = await render(
      <PosSoundContext.Provider
        value={{
          buttonSoundEnabled: true,
          play,
          setButtonSoundEnabled: jest.fn(),
          setSpecialNodeSoundEnabled: jest.fn(),
          specialNodeSoundEnabled: true,
        }}
      >
        <SalesSearchKeyboard labels={labels} onKeyPress={onKeyPress} />
      </PosSoundContext.Provider>,
    );

    for (const testID of [
      "sales-search-keyboard-key-Q",
      "sales-search-keyboard-key-7",
      "sales-search-keyboard-key--",
      "sales-search-keyboard-key-space",
      "sales-search-keyboard-key-backspace",
      "sales-search-keyboard-key-clear",
      "sales-search-keyboard-key-search",
      "sales-search-keyboard-key-close",
      "sales-search-keyboard-key-system",
    ]) {
      await fireEvent.press(screen.getByTestId(testID));
    }

    expect(onKeyPress.mock.calls.map(([key]) => key)).toEqual([
      { kind: "char", char: "Q" },
      { kind: "char", char: "7" },
      { kind: "char", char: "-" },
      { kind: "space" },
      { kind: "backspace" },
      { kind: "clear" },
      { kind: "search" },
      { kind: "close" },
      { kind: "system" },
    ]);
    expect(play.mock.calls.map(([sound]) => sound)).toEqual([
      "key",
      "key",
      "key",
      "key",
      "key",
      "danger",
      "tap",
      "navigate",
      "navigate",
    ]);
    expect(
      screen.getByTestId("sales-search-keyboard-key-backspace").props
        .accessibilityLabel,
    ).toBe("Delete");
    await screen.unmount();
  });

  it("禁用时所有按键都不可点", async () => {
    const onKeyPress = jest.fn<(key: SalesSearchKey) => void>();
    const screen = await render(
      <SalesSearchKeyboard disabled labels={labels} onKeyPress={onKeyPress} />,
    );

    await fireEvent.press(screen.getByTestId("sales-search-keyboard-key-A"));
    await fireEvent.press(screen.getByTestId("sales-search-keyboard-key-search"));

    expect(onKeyPress).not.toHaveBeenCalled();
    expect(
      screen.getByTestId("sales-search-keyboard-key-A").props.accessibilityState,
    ).toMatchObject({ disabled: true });
    await screen.unmount();
  });
});
