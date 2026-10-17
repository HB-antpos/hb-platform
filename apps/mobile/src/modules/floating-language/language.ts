import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { i18n, setAppLanguage } from "@/shared/i18n/i18n";
import type { AppLanguage } from "@/shared/i18n/types";

/** 点一下要切到的语言：只有中 / 英两种，互相切换。 */
export function resolveNextLanguage(current: string | undefined): AppLanguage {
  return current === "en" ? "zh" : "en";
}

/**
 * 与设置页语言切换同一口径：iOS 审核会话只改当前内存语言，不覆盖普通用户存下的偏好；
 * 其余情况落盘并切换。
 */
export async function changeLanguageFromFloatingButton(next: AppLanguage) {
  if (isIosReviewSessionActive()) {
    await i18n.changeLanguage(next);
    return;
  }
  await setAppLanguage(next);
}
