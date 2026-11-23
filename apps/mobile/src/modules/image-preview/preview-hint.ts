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

const TOOLTIP_KEY = "hb.imagePreview.tooltipSeen";
// 同一次启动里只让一张图弹出气泡：页面上可能同时有好几张可放大的图（如候选列表）。
let tooltipClaimed = false;

/**
 * 「点图片可全屏查看」气泡每台设备只出现一次：本次启动内先到先得，
 * 再看设备上有没有记过「已看过」。存储读取失败时不弹，宁可少提示也不反复打扰。
 */
export async function claimImageTooltip(): Promise<boolean> {
  if (tooltipClaimed) return false;
  tooltipClaimed = true;
  try {
    return (await AppAsyncStorage.getString(TOOLTIP_KEY)) !== "1";
  } catch {
    return false;
  }
}

export async function markImageTooltipSeen(): Promise<void> {
  try {
    await AppAsyncStorage.setString(TOOLTIP_KEY, "1");
  } catch {
    // 记不下来最多下次启动再提示一次，不影响使用。
  }
}
