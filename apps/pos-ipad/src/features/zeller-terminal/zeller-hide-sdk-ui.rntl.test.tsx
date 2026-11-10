import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import { act, renderHook } from "@testing-library/react-native";
import Storage from "expo-sqlite/kv-store";

import { useZellerHideSdkUiControl } from "./zeller-hide-sdk-ui";

import { ZELLER_HIDE_SDK_UI_PREFERENCE_KEY } from "@/ui/preferences/terminal-ui-preferences";

jest.mock("expo-sqlite/kv-store", () => ({
  __esModule: true,
  default: {
    getItemSync: jest.fn(),
    setItem: jest.fn(),
  },
}));

const mockGetItemSync = jest.mocked(Storage.getItemSync);
const mockSetItem = jest.mocked(Storage.setItem);

beforeEach(() => {
  jest.clearAllMocks();
  mockGetItemSync.mockReturnValue(null);
  mockSetItem.mockResolvedValue(undefined);
});

describe("useZellerHideSdkUiControl", () => {
  it("本机没有保存过：默认关闭（保持弹窗），并把 false 交给运行时登记处", async () => {
    const registry = { setHideSdkUi: jest.fn() };
    const { result } = await renderHook(() => useZellerHideSdkUiControl(registry));

    expect(result.current.value).toBe(false);
    expect(registry.setHideSdkUi).toHaveBeenLastCalledWith(false);
    expect(mockGetItemSync).toHaveBeenCalledWith(ZELLER_HIDE_SDK_UI_PREFERENCE_KEY);
  });

  it("本机保存过打开：启动时读到 true，并立刻交给运行时登记处", async () => {
    mockGetItemSync.mockReturnValue("true");
    const registry = { setHideSdkUi: jest.fn() };
    const { result } = await renderHook(() => useZellerHideSdkUiControl(registry));

    expect(result.current.value).toBe(true);
    expect(registry.setHideSdkUi).toHaveBeenLastCalledWith(true);
  });

  it("切换：先写运行时登记处（下一笔扣款马上生效）再落盘，界面值同步更新", async () => {
    const registry = { setHideSdkUi: jest.fn() };
    const { result } = await renderHook(() => useZellerHideSdkUiControl(registry));

    await act(async () => {
      result.current.onChange(true);
    });
    expect(result.current.value).toBe(true);
    expect(registry.setHideSdkUi).toHaveBeenLastCalledWith(true);
    expect(mockSetItem).toHaveBeenLastCalledWith(ZELLER_HIDE_SDK_UI_PREFERENCE_KEY, "true");

    await act(async () => {
      result.current.onChange(false);
    });
    expect(result.current.value).toBe(false);
    expect(registry.setHideSdkUi).toHaveBeenLastCalledWith(false);
    expect(mockSetItem).toHaveBeenLastCalledWith(ZELLER_HIDE_SDK_UI_PREFERENCE_KEY, "false");
  });

  it("落盘失败不回滚当前选择，也不影响运行时", async () => {
    mockSetItem.mockRejectedValue(new Error("disk full"));
    const registry = { setHideSdkUi: jest.fn() };
    const { result } = await renderHook(() => useZellerHideSdkUiControl(registry));

    await act(async () => {
      result.current.onChange(true);
    });
    expect(result.current.value).toBe(true);
    expect(registry.setHideSdkUi).toHaveBeenLastCalledWith(true);
  });
});
