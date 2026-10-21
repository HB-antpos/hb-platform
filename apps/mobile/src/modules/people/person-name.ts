/**
 * 人员姓名：姓 / 名 / 中间名 / 昵称。
 *
 * 规则与后端 `PersonNameFormatter` 保持一致：
 * - 任一段含 CJK 字符 → 「姓 + 中间名 + 名」无空格拼接（王芳）；
 * - 否则按「名 中间名 姓」用单个空格拼接（Emily Rose Chen）。
 * 后端只把「合成后的全名」写进 FullName，旧客户端、WPF、报表继续读 FullName，不受影响。
 */

export interface PersonNameParts {
  givenName?: string | null;
  middleName?: string | null;
  familyName?: string | null;
  nickname?: string | null;
}

export interface PersonNameSource extends PersonNameParts {
  fullName?: string | null;
  username?: string | null;
}

// CJK 统一表意文字 + 扩展 A + 兼容表意文字 + 扩展 B～F。
const CJK_PATTERN = /[㐀-䶿一-鿿豈-﫿\u{20000}-\u{2FA1F}]/u;

/** 去首尾空白并折叠内部连续空白；空串统一成 null。 */
export function cleanNamePart(value?: string | null): string | null {
  const text = (value ?? "").replace(/\s+/g, " ").trim();
  return text ? text : null;
}

export function containsCjk(value?: string | null): boolean {
  return CJK_PATTERN.test(value ?? "");
}

/** 把姓 / 名 / 中间名合成全名；三段都为空返回 null。 */
export function composeFullName(parts: PersonNameParts): string | null {
  const given = cleanNamePart(parts.givenName);
  const middle = cleanNamePart(parts.middleName);
  const family = cleanNamePart(parts.familyName);
  const present = [given, middle, family].filter((part): part is string => part !== null);
  if (!present.length) {
    return null;
  }
  if (present.some((part) => containsCjk(part))) {
    return [family, middle, given].filter((part): part is string => part !== null).join("");
  }
  return [given, middle, family].filter((part): part is string => part !== null).join(" ");
}

/** 是否已经有结构化的姓名（至少姓或名其中之一）。 */
export function hasStructuredName(parts: PersonNameParts): boolean {
  return cleanNamePart(parts.givenName) !== null || cleanNamePart(parts.familyName) !== null;
}

/**
 * 把只有「全名」的存量账号拆成表单预填值（仅用于编辑页预填，拆不准的由店长/本人改）。
 * - 含 CJK 且无空格：首字当姓，其余当名（复姓会拆错，表单里可改）；
 * - 西文：单词 → 名；两个词 → 名 姓；三个及以上 → 第一个当名、最后一个当姓、中间归中间名。
 */
export function splitLegacyFullName(fullName?: string | null): Required<Pick<PersonNameParts, "givenName" | "middleName" | "familyName">> {
  const text = cleanNamePart(fullName);
  if (!text) {
    return { givenName: "", middleName: "", familyName: "" };
  }
  if (containsCjk(text) && !text.includes(" ")) {
    const chars = Array.from(text);
    if (chars.length === 1) {
      return { givenName: chars[0], middleName: "", familyName: "" };
    }
    return { givenName: chars.slice(1).join(""), middleName: "", familyName: chars[0] };
  }
  const words = text.split(" ");
  if (words.length === 1) {
    return { givenName: words[0], middleName: "", familyName: "" };
  }
  if (words.length === 2) {
    return { givenName: words[0], middleName: "", familyName: words[1] };
  }
  return {
    givenName: words[0],
    middleName: words.slice(1, -1).join(" "),
    familyName: words[words.length - 1],
  };
}

export interface ResolvedPersonName {
  /** 列表 / 详情主标题：有结构化姓名用合成值，否则全名，再否则账号。 */
  primary: string;
  /** 昵称；与主标题相同或为空时为 null，界面据此决定是否显示「Em」小字。 */
  nickname: string | null;
  /** 收银、排班、小票等窄位置使用：昵称 → 名 → 全名 → 账号。 */
  short: string;
  /** 头像字母：中文取首字，西文取「名 + 姓」首字母，缺姓取前两个字母。 */
  initials: string;
}

export function resolvePersonName(source: PersonNameSource): ResolvedPersonName {
  const username = cleanNamePart(source.username) ?? "";
  const nickname = cleanNamePart(source.nickname);
  const given = cleanNamePart(source.givenName);
  const family = cleanNamePart(source.familyName);
  const fullName = cleanNamePart(source.fullName);
  const composed = composeFullName(source);

  const primary = composed ?? fullName ?? username ?? "";
  const short = nickname ?? given ?? fullName ?? username;

  let initials: string;
  if (containsCjk(primary)) {
    initials = Array.from(primary)[0] ?? "?";
  } else if (given && family) {
    initials = `${Array.from(given)[0]}${Array.from(family)[0]}`.toUpperCase();
  } else {
    const words = primary.split(" ").filter(Boolean);
    if (words.length >= 2) {
      initials = `${Array.from(words[0])[0]}${Array.from(words[1])[0]}`.toUpperCase();
    } else if (words.length === 1) {
      initials = Array.from(words[0]).slice(0, 2).join("").toUpperCase();
    } else {
      initials = "?";
    }
  }

  return {
    primary,
    nickname: nickname && nickname !== primary ? nickname : null,
    short,
    initials: initials || "?",
  };
}

/**
 * 个人码标签上打印的称呼（标签字号大、位置窄，不印中间名）：
 * 1. 有昵称：昵称 + 姓（Em Chen）；没有姓只印昵称；
 * 2. 没昵称但有名：名 + 姓首字母（Emily C.）；没有姓只印名；
 * 3. 都没有：账号兜底。
 * 拆分字段上线前创建、只有「全名」的存量账号，先按 splitLegacyFullName 拆出名 / 姓再套上面的规则。
 * 含中文时不缩写首字母，按「姓 + 昵称 / 名」无空格拼接（王小芳 / 王芳），与 composeFullName 的中文顺序一致。
 */
export function resolvePersonalCodeLabelName(source: PersonNameSource): string {
  const legacy = hasStructuredName(source) ? null : splitLegacyFullName(source.fullName);
  const nickname = cleanNamePart(source.nickname);
  const given = cleanNamePart(legacy ? legacy.givenName : source.givenName);
  const family = cleanNamePart(legacy ? legacy.familyName : source.familyName);
  const lead = nickname ?? given;
  if (lead) {
    if (!family) return lead;
    if (containsCjk(lead) || containsCjk(family)) return `${family}${lead}`;
    // 有昵称印完整的姓；只有名时姓缩成首字母，避免标签上与全名重复。
    return nickname ? `${nickname} ${family}` : `${given} ${Array.from(family)[0].toUpperCase()}.`;
  }
  return cleanNamePart(source.username) ?? "";
}

/** 搜索用：把姓名各段、昵称、账号拼成一串小写文本，客户端本地过滤时命中任意一段。 */
export function buildPersonSearchText(source: PersonNameSource): string {
  return [
    source.fullName,
    source.givenName,
    source.middleName,
    source.familyName,
    source.nickname,
    source.username,
  ]
    .map((part) => cleanNamePart(part))
    .filter((part): part is string => part !== null)
    .join(" ")
    .toLowerCase();
}
