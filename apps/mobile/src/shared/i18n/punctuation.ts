import i18next from "i18next";

/**
 * 按界面语言拼接列表与名称的标点：中文用全角「、」「，」「「」」，英文用半角逗号与弯引号。
 * 不传 language 时读 i18next 当前语言（未初始化时按中文），纯 UI 组件和逻辑层无需接 hook。
 */
function isChinese(language?: string) {
  const resolved = language ?? i18next.resolvedLanguage ?? i18next.language ?? "zh";
  return resolved.toLowerCase().startsWith("zh");
}

/** 并列项（人名、分店、字段名）之间的分隔：中文「、」，英文「, 」。 */
export function listSeparator(language?: string) {
  return isChinese(language) ? "、" : ", ";
}

/** 短句之间的分隔：中文「，」，英文「, 」。 */
export function clauseSeparator(language?: string) {
  return isChinese(language) ? "，" : ", ";
}

/** 标签与值之间的冒号：中文「：」，英文「: 」。 */
export function labelColon(language?: string) {
  return isChinese(language) ? "：" : ": ";
}

/** 名称加引号（如昵称）：中文「「名」」，英文 “名”。 */
export function quoteName(name: string, language?: string) {
  return isChinese(language) ? `「${name}」` : `“${name}”`;
}

/** 名称后附编号（如「分店名（编号）」）：中文全角括号，英文半角括号加空格。 */
export function withCode(name: string, code: string, language?: string) {
  return isChinese(language) ? `${name}（${code}）` : `${name} (${code})`;
}
