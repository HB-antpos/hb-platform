import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// SearchField 的源码契约（它依赖 expo-router，Node 下没法直接渲染，所以读源码锁住关键写法）：
// 1. 输入框显示的文字来自自己持有的缓冲（useBufferedSearchText），而不是直接绑定父级的值——
//    否则每个按键都要等父级重绘一遍，拼音输入法组词时容易出现首字母重复（lulu → llulu）。
// 2. 清除图标的位置始终占着（只是空输入时看不见），输入框布局不随「有没有字」变化。
// 3. 选人弹层的搜索框都走停手通知（emitDelayMs），页面不再随每个按键重绘。
const read = (path: string) => readFileSync(resolve(__dirname, path), "utf8");

const ui = read("./ui.tsx");
const searchField = /export function SearchField\([\s\S]*?\n}\n/.exec(ui)?.[0] ?? "";
assert.ok(searchField, "找不到 SearchField");
assert.match(searchField, /useBufferedSearchText\(value, onChange, emitDelayMs\)/, "SearchField 必须用本地输入缓冲");
assert.match(searchField, /value=\{text\}/, "输入框显示缓冲里的文字");
assert.match(searchField, /onChangeText=\{handleChange\}/, "输入走缓冲的 handleChange");
assert.doesNotMatch(searchField, /value=\{value\}/, "不能再直接绑定父级的值");
assert.doesNotMatch(searchField, /right=\{value \?/, "清除图标不能再按「有没有字」条件渲染");
assert.match(searchField, /right=\{<TextInput\.Icon icon="close"/, "清除图标位置始终渲染");
assert.match(searchField, /emitDelayMs = 0/, "默认仍是每个按键立即通知，其他调用方行为不变");

const sheets = read("./permission-assign-sheets.tsx");
assert.match(sheets, /searchDelayMs=\{USER_PICKER_SEARCH_DELAY_MS\}/, "分配用户弹层的搜索框停手后才通知页面");
assert.match(sheets, /emitDelayMs=\{searchDelayMs\}/, "SheetToolbar 把延迟传给 SearchField");

const members = read("./role-detail-members.tsx");
assert.match(members, /emitDelayMs=\{USER_PICKER_SEARCH_DELAY_MS\}/, "添加成员弹层的搜索框停手后才通知页面");

// 两个选人弹层都固定高度（底部弹层靠底对齐，内容变矮时顶部的搜索框会往下掉，干扰输入法组词）
assert.equal((sheets.match(/fullHeight/g) ?? []).length >= 2, true, "分配用户弹层主页与二级页都固定高度");
assert.match(members, /fullHeight/, "添加成员弹层固定高度");

// 页面里不再对搜索词二次防抖（搜索框已经自带 300ms 停手通知）
const detail = read("./permission-detail-screen.tsx");
assert.doesNotMatch(detail, /useIdentitySearch/, "权限详情页不再二次防抖");
assert.match(detail, /const debouncedUserSearch = userSearch\.trim\(\);/);

console.log("search-field source contract tests passed");
