import Storage from "expo-sqlite/kv-store";

import type { CameraScanMode } from "@/core/contracts/scanner";
import { isPosThemeId, type PosThemeId } from "@/ui/theme-palettes";

export type AppLanguage = "zh" | "en";

export const LANGUAGE_PREFERENCE_KEY = "hb.pos.language.v1";
export const CAMERA_SCAN_MODE_PREFERENCE_KEY = "hb.pos.camera-scan-mode.v1";
export const BUTTON_SOUND_PREFERENCE_KEY = "hb.pos.button-sound.v1";
export const SPECIAL_NODE_SOUND_PREFERENCE_KEY =
  "hb.pos.special-node-sound.v1";
/** 旧版本总开关仅用于双开关首次读取时的兼容回退。 */
export const TOUCH_SOUND_PREFERENCE_KEY = "hb.pos.touch-sound.v1";
export const THEME_PREFERENCE_KEY = "hb.pos.theme.v1";
export const NAV_PINNED_PREFERENCE_KEY = "hb.pos.nav-pinned.v1";

function isAppLanguage(value: unknown): value is AppLanguage {
  return value === "zh" || value === "en";
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}

function isCameraScanMode(value: unknown): value is CameraScanMode {
  return value === "single" || value === "continuous";
}

/** 同步读取仅含语言代码的本地偏好，存储故障时继续使用设备语言。 */
export function readStoredLanguage(): AppLanguage | null {
  try {
    const value = Storage.getItemSync(LANGUAGE_PREFERENCE_KEY);
    return isAppLanguage(value) ? value : null;
  } catch {
    return null;
  }
}

/** 异步保存语言；设备存储不可用不得影响当前会话。 */
export async function saveStoredLanguage(language: AppLanguage): Promise<void> {
  try {
    await Storage.setItem(LANGUAGE_PREFERENCE_KEY, language);
  } catch {
    // 偏好持久化失败不应阻断 POS 界面。
  }
}

/** 同步读取相机扫码模式，缺失、损坏或存储故障时默认单次扫码。 */
export function readCameraScanMode(): CameraScanMode {
  try {
    const value = Storage.getItemSync(CAMERA_SCAN_MODE_PREFERENCE_KEY);
    return isCameraScanMode(value) ? value : "single";
  } catch {
    return "single";
  }
}

/** 仅保存合法扫码模式；持久化失败不得影响当前会话。 */
export async function saveCameraScanMode(mode: CameraScanMode): Promise<void> {
  if (!isCameraScanMode(mode)) {
    return;
  }

  try {
    await Storage.setItem(CAMERA_SCAN_MODE_PREFERENCE_KEY, mode);
  } catch {
    // 偏好持久化失败不应阻断 POS 界面。
  }
}

function parseStoredBoolean(value: string | null): boolean | null {
  if (value === "true") return true;
  if (value === "false") return false;
  return null;
}

function readSoundEnabled(preferenceKey: string): boolean {
  try {
    const currentValue = parseStoredBoolean(Storage.getItemSync(preferenceKey));
    if (currentValue !== null) return currentValue;

    const legacyValue = parseStoredBoolean(
      Storage.getItemSync(TOUCH_SOUND_PREFERENCE_KEY),
    );
    return legacyValue ?? true;
  } catch {
    return true;
  }
}

/** 新键缺失或损坏时只读回退旧总开关；新安装和读取异常默认开启。 */
export function readButtonSoundEnabled(): boolean {
  return readSoundEnabled(BUTTON_SOUND_PREFERENCE_KEY);
}

/** 新键缺失或损坏时只读回退旧总开关；新安装和读取异常默认开启。 */
export function readSpecialNodeSoundEnabled(): boolean {
  return readSoundEnabled(SPECIAL_NODE_SOUND_PREFERENCE_KEY);
}

async function saveSoundEnabled(
  preferenceKey: string,
  enabled: boolean,
): Promise<void> {
  try {
    await Storage.setItem(preferenceKey, enabled ? "true" : "false");
  } catch {
    // 偏好持久化失败不应阻断 POS 界面。
  }
}

/** 写入失败不回滚内存会话状态，也不阻断终端当前操作。 */
export async function saveButtonSoundEnabled(enabled: boolean): Promise<void> {
  await saveSoundEnabled(BUTTON_SOUND_PREFERENCE_KEY, enabled);
}

/** 写入失败不回滚内存会话状态，也不阻断终端当前操作。 */
export async function saveSpecialNodeSoundEnabled(
  enabled: boolean,
): Promise<void> {
  await saveSoundEnabled(SPECIAL_NODE_SOUND_PREFERENCE_KEY, enabled);
}

/** 同步读取界面主题；缺失、损坏或存储故障时返回 null，由调用方使用默认主题。 */
export function readStoredTheme(): PosThemeId | null {
  try {
    const value = Storage.getItemSync(THEME_PREFERENCE_KEY);
    return isPosThemeId(value) ? value : null;
  } catch {
    return null;
  }
}

/** 仅保存合法主题；持久化失败不得影响当前会话已生效的主题。 */
export async function saveStoredTheme(themeId: PosThemeId): Promise<void> {
  if (!isPosThemeId(themeId)) {
    return;
  }

  try {
    await Storage.setItem(THEME_PREFERENCE_KEY, themeId);
  } catch {
    // 偏好持久化失败不应阻断 POS 界面。
  }
}

/** 同步读取固定到左侧栏的功能 ID 列表，损坏数据一律忽略。 */
export function readPinnedDestinations(): string[] | null {
  try {
    const serialized = Storage.getItemSync(NAV_PINNED_PREFERENCE_KEY);
    if (serialized == null) {
      return null;
    }

    const pinned: unknown = JSON.parse(serialized);
    return isStringArray(pinned) ? pinned : null;
  } catch {
    return null;
  }
}

/** 异步保存固定到左侧栏的功能 ID 列表，非法数据或存储故障均安全忽略。 */
export async function savePinnedDestinations(
  destinationIds: readonly string[],
): Promise<void> {
  if (!isStringArray(destinationIds)) {
    return;
  }

  try {
    await Storage.setItem(
      NAV_PINNED_PREFERENCE_KEY,
      JSON.stringify(destinationIds),
    );
  } catch {
    // 偏好持久化失败不应阻断 POS 界面。
  }
}
