import {
  STAFF_STATUS_FILTER_ORDER,
  buildStaffListRows,
  compareStaffByName,
  countStaffByStore,
  countStaffStatuses,
  filterStoreStaff,
  matchesStaffKeyword,
  matchesStaffStatusFilter,
  resolveRowPosition,
  resolveStaffStoreSelection,
  resolveStoreListSubtitleKind,
} from "./store-staff-list-logic";
import type { StoreUserListItem } from "./types";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

function staff(overrides: Partial<StoreUserListItem> & { userGUID: string; username: string }): StoreUserListItem {
  return {
    status: 1,
    roleNames: ["StoreStaff"],
    email: `${overrides.username}@example.com`,
    ...overrides,
  };
}

const emily = staff({ userGUID: "U-EMILY", username: "emily.chen", fullName: "Emily Chen", givenName: "Emily", familyName: "Chen", nickname: "Em", phone: "0412 345 678", storeCode: "1008" });
const jack = staff({ userGUID: "U-JACK", username: "jack.nguyen", fullName: "Jack Nguyen", mustChangePassword: true, phone: "0433120556", storeCode: "1008" });
const fang = staff({ userGUID: "U-FANG", username: "fang.wang", fullName: "王芳", loginEnabled: false, storeCode: "1012" });
const sophie = staff({ userGUID: "U-SOPHIE", username: "sophie.tran", fullName: "Sophie Tran", email: "sophie@hb1008.store.local", storeCode: "1008" });
const wei = staff({ userGUID: "U-WEI", username: "wei.chen", fullName: "陈伟", status: 0, storeCode: "1003" });
const me = staff({ userGUID: "U-ME", username: "xiaoming.li", fullName: "李小明", roleNames: ["StoreManager"], storeCode: "1008" });
const all = [emily, jack, fang, sophie, wei, me];

// ── 状态筛选：每个筛选的判断 ──
assertEqual(matchesStaffStatusFilter(emily, "all"), true, "全部");
assertEqual(matchesStaffStatusFilter(wei, "active"), false, "停用不算启用");
assertEqual(matchesStaffStatusFilter(wei, "disabled"), true, "停用");
assertEqual(matchesStaffStatusFilter(jack, "pendingFirstLogin"), true, "待首次登录");
assertEqual(matchesStaffStatusFilter(emily, "pendingFirstLogin"), false, "正常不是待首次登录");
assertEqual(matchesStaffStatusFilter(fang, "cannotLogin"), true, "不能登录 = loginEnabled false");
assertEqual(matchesStaffStatusFilter(emily, "cannotLogin"), false, "缺省视为允许登录");
assertEqual(matchesStaffStatusFilter({ ...emily, loginEnabled: true }, "cannotLogin"), false, "loginEnabled true 允许登录");
assertEqual(matchesStaffStatusFilter(sophie, "noEmail"), true, "占位邮箱 = 无邮箱");
assertEqual(matchesStaffStatusFilter(emily, "noEmail"), false, "有邮箱");

// ── 计数 ──
const counts = countStaffStatuses(all);
assertEqual(counts, { all: 6, active: 5, disabled: 1, pendingFirstLogin: 1, cannotLogin: 1, noEmail: 1 }, "各状态计数");
for (const filter of STAFF_STATUS_FILTER_ORDER) {
  assertEqual(filterStoreStaff(all, { statusFilter: filter }).length, counts[filter], `芯片数字 = 点开后人数：${filter}`);
}
assertEqual(STAFF_STATUS_FILTER_ORDER, ["all", "active", "disabled", "pendingFirstLogin", "cannotLogin", "noEmail"], "芯片顺序");

// ── 搜索：姓名 / 昵称 / 账号 / 邮箱 / 电话 ──
assertEqual(matchesStaffKeyword(emily, ""), true, "空关键词全部命中");
assertEqual(matchesStaffKeyword(emily, "emily"), true, "名");
assertEqual(matchesStaffKeyword(emily, "CHEN"), true, "姓（不分大小写）");
assertEqual(matchesStaffKeyword(emily, "em"), true, "昵称 / 名前缀");
assertEqual(matchesStaffKeyword(staff({ userGUID: "x", username: "zz", fullName: "Zed", nickname: "Blaze" }), "blaze"), true, "只有昵称能命中的也要找得到");
assertEqual(matchesStaffKeyword(emily, "chen emily"), true, "多个词乱序也命中");
assertEqual(matchesStaffKeyword(emily, "emily.chen"), true, "账号");
assertEqual(matchesStaffKeyword(emily, "example.com"), true, "邮箱");
assertEqual(matchesStaffKeyword(emily, "0412"), true, "电话前缀");
assertEqual(matchesStaffKeyword(emily, "0412345"), true, "忽略电话里的空格");
assertEqual(matchesStaffKeyword(jack, "0433 120"), true, "关键词带空格时忽略号码连续性");
assertEqual(matchesStaffKeyword(emily, "jack"), false, "不相关不命中");
assertEqual(matchesStaffKeyword(fang, "王"), true, "中文姓");
assertEqual(matchesStaffKeyword(fang, "王芳"), true, "中文全名");

// ── 过滤 + 排序 ──
assertEqual(filterStoreStaff(all, { keyword: "chen" }).map((user) => user.userGUID), ["U-EMILY", "U-WEI"].sort((a, b) => compareStaffByName(all.find((u) => u.userGUID === a)!, all.find((u) => u.userGUID === b)!)), "关键词 + 排序");
assertEqual(filterStoreStaff(all, { statusFilter: "cannotLogin" }).map((user) => user.userGUID), ["U-FANG"], "不能登录筛选");
assertEqual(filterStoreStaff(all, { statusFilter: "active", keyword: "chen" }).map((user) => user.userGUID), ["U-EMILY"], "状态 + 关键词叠加");
assertEqual(all.map((user) => user.userGUID).join(","), "U-EMILY,U-JACK,U-FANG,U-SOPHIE,U-WEI,U-ME", "过滤不得改动原数组顺序");
assertEqual(filterStoreStaff([emily, staff({ userGUID: "U-A", username: "a.user", fullName: "Emily Chen" })], {}).map((user) => user.username), ["a.user", "emily.chen"], "同名按账号稳定排序");

// ── 分组与卡片位置 ──
assertEqual(resolveRowPosition(0, 1), "only", "单行");
assertEqual(resolveRowPosition(0, 3), "first", "首行");
assertEqual(resolveRowPosition(1, 3), "middle", "中间行");
assertEqual(resolveRowPosition(2, 3), "last", "末行");

const labels = { me: "我", staffSection: (count: number) => `店员 ${count}`, sortedByName: "按姓名" };
const rows = buildStaffListRows(filterStoreStaff(all, {}), "u-me", labels);
assertEqual(
  rows.map((row) => (row.kind === "header" ? `H:${row.label}${row.hint ? `|${row.hint}` : ""}` : `${row.isSelf ? "me" : "u"}:${row.user.userGUID}:${row.position}`)),
  [
    "H:我",
    "me:U-ME:only",
    "H:店员 5|按姓名",
    ...filterStoreStaff(all, {}).filter((user) => user.userGUID !== "U-ME").map((user, index, list) => `u:${user.userGUID}:${resolveRowPosition(index, list.length)}`),
  ],
  "「我」置顶（GUID 大小写不敏感）+ 店员分组",
);
assertEqual(buildStaffListRows([emily], "", labels).map((row) => row.kind), ["header", "user"], "没有 actorGuid 时全部算店员，不出现「我」");
assertEqual(buildStaffListRows([], "u-me", labels), [], "空列表没有分组标题");
assertEqual(buildStaffListRows([me], "U-ME", { ...labels, sortedByName: undefined }).length, 2, "只有本人时没有店员分组");

// ── 各分店人数 ──
assertEqual(countStaffByStore(all), { total: 6, byStore: { "1008": 4, "1012": 1, "1003": 1 } }, "按分店统计");
assertEqual(countStaffByStore([emily, emily]).total, 1, "重复行只算一人");
assertEqual(countStaffByStore([{ ...emily, storeCode: undefined }]), { total: 1, byStore: {} }, "没有分店编码的不进分店统计，但计入总数");

// ── 副标题语义 ──
assertEqual(resolveStoreListSubtitleKind({ managedStoreCode: null, hasSelectedStore: false, canManageSelectedStore: false }), "allStores", "全部已分配分店");
assertEqual(resolveStoreListSubtitleKind({ managedStoreCode: "1008", hasSelectedStore: false, canManageSelectedStore: false }), "noStore", "分店列表未就绪");
assertEqual(resolveStoreListSubtitleKind({ managedStoreCode: "1008", hasSelectedStore: true, canManageSelectedStore: true }), "manageable", "可管理");
assertEqual(resolveStoreListSubtitleKind({ managedStoreCode: "1003", hasSelectedStore: true, canManageSelectedStore: false }), "viewOnly", "仅查看");

// ── 默认分店：自己管理的第一个分店兜底 ──
const managerPicker = [
  { storeCode: "1012", isPrimary: true },
  { storeCode: "1008", isPrimary: true },
];
const baseSelection = {
  deviceBoundStoreCode: null,
  currentStoreCode: null,
  userPicked: false,
  rememberedStoreCode: null,
  rememberedIsAuto: false,
  pickerStores: managerPicker,
};
assertEqual(resolveStaffStoreSelection(baseSelection), "1012", "店长进入页面：默认自己管理的第一个分店");
assertEqual(resolveStaffStoreSelection({ ...baseSelection, pickerStores: [] }), null, "候选为空 / 还没加载完：退回全部分店");
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, rememberedStoreCode: "1008", rememberedIsAuto: true }),
  "1012",
  "全局自动默认的分店不算用户选择，仍按第一个管理分店",
);
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, rememberedStoreCode: "1008" }),
  "1008",
  "用户在别处明确选过的分店（且在候选里）优先于默认",
);
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, rememberedStoreCode: "9999" }),
  "1012",
  "记住的分店不在候选里（非管理分店）时不能选中它",
);
assertEqual(resolveStaffStoreSelection({ ...baseSelection, currentStoreCode: "1008" }), "1008", "已有选择且仍在候选里：保持不变");
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, currentStoreCode: "1008", pickerStores: [managerPicker[0]] }),
  "1012",
  "当前选择已不在候选里：回到第一个管理分店",
);
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, userPicked: true, rememberedStoreCode: "1008", rememberedIsAuto: true }),
  null,
  "用户手动选了全部分店：后续重算（分店刷新、全局自动默认变化）不覆盖",
);
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, userPicked: true, currentStoreCode: "1008" }),
  "1008",
  "用户手动选了某个分店：保持",
);
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, deviceBoundStoreCode: "2001", userPicked: true }),
  "2001",
  "设备绑定模式永远固定本机分店",
);
// 管理员：候选是全部 POS 分店，只有自己管理的（isPrimary）才作为默认；一家都没有就保持全部分店。
assertEqual(
  resolveStaffStoreSelection({
    ...baseSelection,
    pickerStores: [{ storeCode: "1001" }, { storeCode: "1003", isPrimary: false }, { storeCode: "1005", isPrimary: true }],
  }),
  "1005",
  "管理员：跳过非自己管理的分店，选第一个自己管理的",
);
assertEqual(
  resolveStaffStoreSelection({ ...baseSelection, pickerStores: [{ storeCode: "1001" }, { storeCode: "1003", isPrimary: false }] }),
  null,
  "管理员没有自己管理的分店：保持全部分店",
);

console.log("store-staff-list-logic.test.ts: ok");
