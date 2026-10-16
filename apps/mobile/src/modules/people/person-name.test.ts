import {
  buildPersonSearchText,
  cleanNamePart,
  composeFullName,
  containsCjk,
  hasStructuredName,
  resolvePersonName,
  splitLegacyFullName,
} from "./person-name";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

// ── 清理与 CJK 判断 ──
assertEqual(cleanNamePart("  Emily   Rose "), "Emily Rose", "折叠内部空白");
assertEqual(cleanNamePart("   "), null, "空白变 null");
assertEqual(cleanNamePart(undefined), null, "undefined 变 null");
assertEqual(containsCjk("王芳"), true, "中文");
assertEqual(containsCjk("Emily"), false, "英文");
assertEqual(containsCjk("Sean 王"), true, "混合");
assertEqual(containsCjk("𠮷野"), true, "扩展 B 区汉字");

// ── 合成全名：西文「名 中间名 姓」，中文「姓+名」 ──
assertEqual(composeFullName({ givenName: "Emily", familyName: "Chen" }), "Emily Chen", "西文名 姓");
assertEqual(composeFullName({ givenName: "Emily", middleName: "Rose", familyName: "Chen" }), "Emily Rose Chen", "西文含中间名");
assertEqual(composeFullName({ givenName: "Emily" }), "Emily", "只有名");
assertEqual(composeFullName({ familyName: "Chen" }), "Chen", "只有姓");
assertEqual(composeFullName({ givenName: "芳", familyName: "王" }), "王芳", "中文姓名无空格");
assertEqual(composeFullName({ givenName: "小明", familyName: "李" }), "李小明", "中文三字");
assertEqual(composeFullName({ givenName: "Sean", familyName: "王" }), "王Sean", "混合按中文规则拼接");
assertEqual(composeFullName({ givenName: " Emily ", familyName: " Chen " }), "Emily Chen", "各段先清理");
assertEqual(composeFullName({}), null, "全空返回 null");
assertEqual(composeFullName({ nickname: "Em" }), null, "昵称不参与全名");

assertEqual(hasStructuredName({ givenName: "Emily" }), true, "有名即结构化");
assertEqual(hasStructuredName({ middleName: "Rose" }), false, "只有中间名不算");
assertEqual(hasStructuredName({}), false, "空不算");

// ── 存量全名拆分（编辑页预填） ──
assertEqual(splitLegacyFullName("Emily Chen"), { givenName: "Emily", middleName: "", familyName: "Chen" }, "两个词");
assertEqual(splitLegacyFullName("Emily Rose Chen"), { givenName: "Emily", middleName: "Rose", familyName: "Chen" }, "三个词");
assertEqual(splitLegacyFullName("Mary Jane Anne Smith"), { givenName: "Mary", middleName: "Jane Anne", familyName: "Smith" }, "四个词中间并入中间名");
assertEqual(splitLegacyFullName("lily"), { givenName: "lily", middleName: "", familyName: "" }, "单个词只给名");
assertEqual(splitLegacyFullName("王芳"), { givenName: "芳", middleName: "", familyName: "王" }, "中文首字当姓");
assertEqual(splitLegacyFullName("李"), { givenName: "李", middleName: "", familyName: "" }, "单字当名");
assertEqual(splitLegacyFullName("店长 Sean"), { givenName: "店长", middleName: "", familyName: "Sean" }, "混合带空格按空格拆（预填后可改）");
assertEqual(splitLegacyFullName(null), { givenName: "", middleName: "", familyName: "" }, "空值");

// ── 展示名 ──
const emily = resolvePersonName({ givenName: "Emily", familyName: "Chen", nickname: "Em", username: "emily.chen" });
assertEqual(emily.primary, "Emily Chen", "主标题");
assertEqual(emily.nickname, "Em", "昵称");
assertEqual(emily.short, "Em", "窄位置优先昵称");
assertEqual(emily.initials, "EC", "西文首字母");

const noNick = resolvePersonName({ givenName: "Jack", familyName: "Nguyen", username: "jack.nguyen" });
assertEqual(noNick.short, "Jack", "没有昵称用名");
assertEqual(noNick.nickname, null, "没有昵称");

const legacy = resolvePersonName({ fullName: "Sophie Tran", username: "sophie.tran" });
assertEqual(legacy.primary, "Sophie Tran", "只有全名时沿用全名");
assertEqual(legacy.short, "Sophie Tran", "没有结构化名时窄位置用全名");
assertEqual(legacy.initials, "ST", "全名拆首字母");

const chinese = resolvePersonName({ givenName: "芳", familyName: "王", username: "fang.wang" });
assertEqual(chinese.primary, "王芳", "中文主标题");
assertEqual(chinese.initials, "王", "中文头像取首字");

const accountOnly = resolvePersonName({ username: "order1003" });
assertEqual(accountOnly.primary, "order1003", "只有账号");
assertEqual(accountOnly.short, "order1003", "只有账号");
assertEqual(accountOnly.initials, "OR", "账号前两个字母");

const sameNick = resolvePersonName({ fullName: "Em", nickname: "Em", username: "em" });
assertEqual(sameNick.nickname, null, "昵称与主标题相同不重复显示");

assertEqual(resolvePersonName({}).initials, "?", "全空头像");

// ── 搜索文本 ──
const search = buildPersonSearchText({ fullName: "Emily Chen", givenName: "Emily", familyName: "Chen", nickname: "Em", username: "Emily.Chen" });
assertEqual(search.includes("em "), true, "包含昵称");
assertEqual(search.includes("emily.chen"), true, "包含账号并小写");
assertEqual(buildPersonSearchText({}), "", "空");
