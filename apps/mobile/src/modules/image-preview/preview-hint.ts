import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { shouldShowPreviewHint } from "./zoom-logic";

const HINT_KEY = "hb.imagePreview.hintShown";

/**
 * 手势提示条只在设备上的前 3 次打开时出现：读取已展示次数，未满则计一次并返回 true。
 * 存储读写失败（隐私模式、存储被清理）时不显示提示，也不影响预览本身。
 */
export async function consumePreviewHint(): Promise<boolean> {
  try {
    const shown = Number(await AppAsyncStorage.getString(HINT_KEY)) || 0;
    if (!shouldShowPreviewHint(shown)) return false;
    await AppAsyncStorage.setString(HINT_KEY, String(shown + 1));
    return true;
  } catch {
    return false;
  }
}
