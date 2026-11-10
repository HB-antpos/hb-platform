import { beforeEach, expect, jest, test } from "@jest/globals";
import Storage from "expo-sqlite/kv-store";

import {
  BUTTON_SOUND_PREFERENCE_KEY,
  CAMERA_SCAN_MODE_PREFERENCE_KEY,
  LANGUAGE_PREFERENCE_KEY,
  NAV_PINNED_PREFERENCE_KEY,
  readButtonSoundEnabled,
  readCameraScanMode,
  readPinnedDestinations,
  readSpecialNodeSoundEnabled,
  readStoredLanguage,
  readStoredTheme,
  saveButtonSoundEnabled,
  saveCameraScanMode,
  savePinnedDestinations,
  saveSpecialNodeSoundEnabled,
  saveStoredLanguage,
  saveStoredTheme,
  SPECIAL_NODE_SOUND_PREFERENCE_KEY,
  THEME_PREFERENCE_KEY,
  TOUCH_SOUND_PREFERENCE_KEY,
  readZellerHideSdkUi,
  saveZellerHideSdkUi,
  ZELLER_HIDE_SDK_UI_PREFERENCE_KEY,
} from "./terminal-ui-preferences";

import type { CameraScanMode } from "@/core/contracts/scanner";

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

test("语言偏好只接受 zh/en，读取异常时安全回退", () => {
  mockGetItemSync.mockReturnValue("zh");
  expect(readStoredLanguage()).toBe("zh");
  expect(mockGetItemSync).toHaveBeenCalledWith(LANGUAGE_PREFERENCE_KEY);

  mockGetItemSync.mockReturnValue("fr");
  expect(readStoredLanguage()).toBeNull();

  mockGetItemSync.mockImplementation(() => {
    throw new Error("SQLite unavailable");
  });
  expect(readStoredLanguage()).toBeNull();
});

test("异步保存使用固定键且吞掉存储异常", async () => {
  await saveStoredLanguage("en");
  await savePinnedDestinations(["returns", "settings"]);

  expect(mockSetItem).toHaveBeenNthCalledWith(
    1,
    LANGUAGE_PREFERENCE_KEY,
    "en",
  );
  expect(mockSetItem).toHaveBeenNthCalledWith(
    2,
    NAV_PINNED_PREFERENCE_KEY,
    '["returns","settings"]',
  );

  mockSetItem.mockRejectedValue(new Error("disk full"));
  await expect(saveStoredLanguage("zh")).resolves.toBeUndefined();
  await expect(savePinnedDestinations(["returns"])).resolves.toBeUndefined();
});

test("相机扫码模式只接受 single/continuous，缺失、损坏或读取异常时回退单次", () => {
  mockGetItemSync.mockReturnValue("continuous");
  expect(readCameraScanMode()).toBe("continuous");
  expect(mockGetItemSync).toHaveBeenCalledWith(
    CAMERA_SCAN_MODE_PREFERENCE_KEY,
  );

  mockGetItemSync.mockReturnValue("single");
  expect(readCameraScanMode()).toBe("single");

  mockGetItemSync.mockReturnValue(null);
  expect(readCameraScanMode()).toBe("single");

  mockGetItemSync.mockReturnValue("invalid");
  expect(readCameraScanMode()).toBe("single");

  mockGetItemSync.mockImplementation(() => {
    throw new Error("SQLite unavailable");
  });
  expect(readCameraScanMode()).toBe("single");
});

test("相机扫码模式仅保存合法值，非法运行时值和写入异常均安全忽略", async () => {
  await saveCameraScanMode("continuous");
  await saveCameraScanMode("single");

  expect(mockSetItem).toHaveBeenNthCalledWith(
    1,
    CAMERA_SCAN_MODE_PREFERENCE_KEY,
    "continuous",
  );
  expect(mockSetItem).toHaveBeenNthCalledWith(
    2,
    CAMERA_SCAN_MODE_PREFERENCE_KEY,
    "single",
  );

  await saveCameraScanMode("invalid" as CameraScanMode);
  expect(mockSetItem).toHaveBeenCalledTimes(2);

  mockSetItem.mockRejectedValue(new Error("disk full"));
  await expect(saveCameraScanMode("continuous")).resolves.toBeUndefined();
});

test("双音效新键严格读取 true/false，且有效新值优先于旧总开关", () => {
  mockGetItemSync.mockImplementation((key) => {
    if (key === BUTTON_SOUND_PREFERENCE_KEY) return "false";
    if (key === SPECIAL_NODE_SOUND_PREFERENCE_KEY) return "true";
    if (key === TOUCH_SOUND_PREFERENCE_KEY) return "false";
    return null;
  });

  expect(readButtonSoundEnabled()).toBe(false);
  expect(readSpecialNodeSoundEnabled()).toBe(true);
  expect(mockGetItemSync).not.toHaveBeenCalledWith(TOUCH_SOUND_PREFERENCE_KEY);
});

test("每个新音效键缺失或损坏时独立只读回退旧总开关", () => {
  mockGetItemSync.mockImplementation((key) => {
    if (key === BUTTON_SOUND_PREFERENCE_KEY) return null;
    if (key === SPECIAL_NODE_SOUND_PREFERENCE_KEY) return "TRUE";
    if (key === TOUCH_SOUND_PREFERENCE_KEY) return "false";
    return null;
  });

  expect(readButtonSoundEnabled()).toBe(false);
  expect(readSpecialNodeSoundEnabled()).toBe(false);
  expect(mockGetItemSync).toHaveBeenCalledWith(BUTTON_SOUND_PREFERENCE_KEY);
  expect(mockGetItemSync).toHaveBeenCalledWith(
    SPECIAL_NODE_SOUND_PREFERENCE_KEY,
  );
  expect(mockGetItemSync).toHaveBeenCalledTimes(4);
});

test("新安装、旧值损坏或读取异常时双音效均默认开启", () => {
  mockGetItemSync.mockReturnValue(null);
  expect(readButtonSoundEnabled()).toBe(true);
  expect(readSpecialNodeSoundEnabled()).toBe(true);

  mockGetItemSync.mockReturnValue("TRUE");
  expect(readButtonSoundEnabled()).toBe(true);

  mockGetItemSync.mockImplementation(() => {
    throw new Error("SQLite unavailable");
  });
  expect(readButtonSoundEnabled()).toBe(true);
  expect(readSpecialNodeSoundEnabled()).toBe(true);
});

test("双音效只写各自新键，写入异常不抛错且不触碰旧键", async () => {
  await saveButtonSoundEnabled(false);
  await saveSpecialNodeSoundEnabled(true);

  expect(mockSetItem).toHaveBeenNthCalledWith(
    1,
    BUTTON_SOUND_PREFERENCE_KEY,
    "false",
  );
  expect(mockSetItem).toHaveBeenNthCalledWith(
    2,
    SPECIAL_NODE_SOUND_PREFERENCE_KEY,
    "true",
  );
  expect(mockSetItem).not.toHaveBeenCalledWith(
    TOUCH_SOUND_PREFERENCE_KEY,
    expect.any(String),
  );

  mockSetItem.mockRejectedValue(new Error("disk full"));
  await expect(saveButtonSoundEnabled(true)).resolves.toBeUndefined();
  await expect(saveSpecialNodeSoundEnabled(false)).resolves.toBeUndefined();
});

test("界面主题只接受已知主题 ID，读取异常时回退为 null", async () => {
  mockGetItemSync.mockReturnValue("dark");
  expect(readStoredTheme()).toBe("dark");
  expect(mockGetItemSync).toHaveBeenCalledWith(THEME_PREFERENCE_KEY);

  mockGetItemSync.mockReturnValue("neon");
  expect(readStoredTheme()).toBeNull();

  mockGetItemSync.mockImplementation(() => {
    throw new Error("SQLite unavailable");
  });
  expect(readStoredTheme()).toBeNull();

  await saveStoredTheme("ocean");
  expect(mockSetItem).toHaveBeenCalledWith(THEME_PREFERENCE_KEY, "ocean");

  mockSetItem.mockClear();
  await saveStoredTheme("neon" as never);
  expect(mockSetItem).not.toHaveBeenCalled();

  mockSetItem.mockRejectedValue(new Error("disk full"));
  await expect(saveStoredTheme("dark")).resolves.toBeUndefined();
});

test("侧栏固定项只接受字符串数组，损坏数据与存储故障一律忽略", async () => {
  expect(readPinnedDestinations()).toBeNull();

  mockGetItemSync.mockReturnValue(JSON.stringify(["returns", "settings"]));
  expect(readPinnedDestinations()).toEqual(["returns", "settings"]);
  expect(mockGetItemSync).toHaveBeenCalledWith(NAV_PINNED_PREFERENCE_KEY);

  mockGetItemSync.mockReturnValue("{not json");
  expect(readPinnedDestinations()).toBeNull();

  mockGetItemSync.mockReturnValue(JSON.stringify([1, 2]));
  expect(readPinnedDestinations()).toBeNull();

  await savePinnedDestinations(["returns"]);
  expect(mockSetItem).toHaveBeenCalledWith(
    NAV_PINNED_PREFERENCE_KEY,
    JSON.stringify(["returns"]),
  );

  mockSetItem.mockRejectedValue(new Error("disk full"));
  await expect(savePinnedDestinations(["returns"])).resolves.toBeUndefined();
});

test("Zeller 隐藏 SDK 窗口偏好：只有明确保存过 true 才为 true，缺失 / 损坏 / 存储故障一律保持弹窗", async () => {
  // 默认（缺失）：false。
  mockGetItemSync.mockReturnValue(null);
  expect(readZellerHideSdkUi()).toBe(false);
  expect(mockGetItemSync).toHaveBeenCalledWith(ZELLER_HIDE_SDK_UI_PREFERENCE_KEY);

  mockGetItemSync.mockReturnValue("true");
  expect(readZellerHideSdkUi()).toBe(true);
  mockGetItemSync.mockReturnValue("false");
  expect(readZellerHideSdkUi()).toBe(false);

  // 损坏的值不能被当成 true。
  for (const corrupted of ["1", "yes", "TRUE", ""]) {
    mockGetItemSync.mockReturnValue(corrupted);
    expect(readZellerHideSdkUi()).toBe(false);
  }
  mockGetItemSync.mockImplementation(() => {
    throw new Error("storage unavailable");
  });
  expect(readZellerHideSdkUi()).toBe(false);

  await saveZellerHideSdkUi(true);
  expect(mockSetItem).toHaveBeenLastCalledWith(ZELLER_HIDE_SDK_UI_PREFERENCE_KEY, "true");
  await saveZellerHideSdkUi(false);
  expect(mockSetItem).toHaveBeenLastCalledWith(ZELLER_HIDE_SDK_UI_PREFERENCE_KEY, "false");

  // 写入失败不抛出，也不影响当前选择。
  mockSetItem.mockRejectedValue(new Error("disk full"));
  await expect(saveZellerHideSdkUi(true)).resolves.toBeUndefined();
});
