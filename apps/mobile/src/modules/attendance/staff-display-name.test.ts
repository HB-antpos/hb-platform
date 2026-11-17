import assert from "node:assert/strict";
import { test } from "node:test";
import {
  DEFAULT_STAFF_NAME_MODE,
  STAFF_NAME_MODE_STORAGE_KEY,
  parseStaffNameMode,
  readStaffNameMode,
  resolveStaffName,
  resolveStaffNames,
  writeStaffNameMode,
  type StaffNameMode,
  type StaffNameSource,
} from "./staff-display-name";

// 与 Web 的 staffNameLogic.test.ts 逐条对应：两端行为必须一致。
const person = (patch: Partial<StaffNameSource> & { userGuid: string }): StaffNameSource => ({
  username: `${patch.userGuid}-login`,
  ...patch,
});
const names = (sources: StaffNameSource[], mode: StaffNameMode = "nickname") =>
  Object.fromEntries(resolveStaffNames(sources, mode));

test("昵称模式取名顺序：昵称 → 名 → 全名 → 登录名 → userGuid", () => {
  assert.equal(DEFAULT_STAFF_NAME_MODE, "nickname", "默认显示昵称");
  assert.equal(resolveStaffName(person({ userGuid: "a", nickname: "Immy", givenName: "Imogen", fullName: "Imogen Hitchcock" }), "nickname"), "Immy");
  assert.equal(resolveStaffName(person({ userGuid: "a", givenName: "Taylor", fullName: "Taylor Keating" }), "nickname"), "Taylor", "没填昵称 → 名");
  assert.equal(resolveStaffName(person({ userGuid: "a", fullName: "Zhan Chen" }), "nickname"), "Zhan Chen", "名也没有 → 全名");
  assert.equal(resolveStaffName({ userGuid: "a", username: "hbstaff07" }, "nickname"), "hbstaff07", "全名也没有 → 登录名");
  assert.equal(resolveStaffName({ userGuid: "guid-1" }, "nickname"), "guid-1", "什么都没有 → userGuid");
  // 空白串当作没填，并去掉首尾空白
  assert.equal(resolveStaffName(person({ userGuid: "a", nickname: "   ", givenName: " Sam ", fullName: "Sam Lee" }), "nickname"), "Sam");
  // null（后端可空字段）与 undefined 等价
  assert.equal(resolveStaffName(person({ userGuid: "a", nickname: null, givenName: null, fullName: "Sam Lee" }), "nickname"), "Sam Lee");
});

test("全名模式与改动前一致：fullName → username → userGuid，不看昵称 / 名", () => {
  assert.equal(resolveStaffName(person({ userGuid: "a", nickname: "Immy", givenName: "Imogen", fullName: "Imogen Hitchcock" }), "full"), "Imogen Hitchcock");
  assert.equal(resolveStaffName({ userGuid: "a", username: "hbstaff07", nickname: "Hb" }, "full"), "hbstaff07");
  assert.equal(resolveStaffName({ userGuid: "guid-1" }, "full"), "guid-1");
});

test("昵称模式一批员工：不重名原样返回，与设计图样例一致", () => {
  assert.deepEqual(
    names([person({ userGuid: "a", nickname: "Cody" }), person({ userGuid: "b", nickname: "Immy" })]),
    { a: "Cody", b: "Immy" },
  );
  // Taylor 回落到名，Zhan Chen 回落到全名
  assert.deepEqual(
    names([
      person({ userGuid: "cody", nickname: "Cody", givenName: "Cody", fullName: "Cody Johnson" }),
      person({ userGuid: "taylor", givenName: "Taylor", fullName: "Taylor Keating" }),
      person({ userGuid: "zhan", fullName: "Zhan Chen" }),
    ]),
    { cody: "Cody", taylor: "Taylor", zhan: "Zhan Chen" },
  );
});

test("昵称模式重名：先补姓的首字母（大小写不敏感地识别重名）", () => {
  assert.deepEqual(
    names([
      person({ userGuid: "a", nickname: "Sam", familyName: "Chen", fullName: "Sam Chen" }),
      person({ userGuid: "b", nickname: "sam", familyName: "lee", fullName: "Sam Lee" }),
    ]),
    { a: "Sam C.", b: "sam L." },
  );
  // 中文姓不是拉丁字母：直接用完整的姓，不加点
  assert.deepEqual(
    names([
      person({ userGuid: "a", nickname: "Amy", familyName: "陈" }),
      person({ userGuid: "b", nickname: "Amy", familyName: "李" }),
    ]),
    { a: "Amy 陈", b: "Amy 李" },
  );
  // 只有一人有姓：补完已经能区分（Sam / Sam L.），没姓的那位不用再加后缀
  assert.deepEqual(
    names([
      person({ userGuid: "a", username: "sam1", nickname: "Sam" }),
      person({ userGuid: "b", username: "sam2", nickname: "Sam", familyName: "Lee" }),
    ]),
    { a: "Sam", b: "Sam L." },
  );
});

test("昵称模式重名：补完仍重复的补登录名后缀", () => {
  // 同姓首字母（Chen / Cho）
  assert.deepEqual(
    names([
      person({ userGuid: "a", username: "sam.chen", nickname: "Sam", familyName: "Chen" }),
      person({ userGuid: "b", username: "sam.cho", nickname: "Sam", familyName: "Cho" }),
    ]),
    { a: "Sam (sam.chen)", b: "Sam (sam.cho)" },
  );
  // 都没有姓可补
  assert.deepEqual(
    names([
      person({ userGuid: "a", username: "sam1", nickname: "Sam" }),
      person({ userGuid: "b", username: "sam2", nickname: "Sam" }),
    ]),
    { a: "Sam (sam1)", b: "Sam (sam2)" },
  );
  // 已经是全名兜底的两个同名人：名字本身带姓，不补首字母，直接补登录名后缀
  assert.deepEqual(
    names([
      person({ userGuid: "a", username: "zc1", fullName: "Zhan Chen", familyName: "Chen" }),
      person({ userGuid: "b", username: "zc2", fullName: "Zhan Chen", familyName: "Chen" }),
    ]),
    { a: "Zhan Chen (zc1)", b: "Zhan Chen (zc2)" },
  );
  // 补完首字母后撞上了别人原本的显示名（有人昵称就叫 "Sam C."）→ 撞上的两人补登录名后缀，另一位不受影响
  assert.deepEqual(
    names([
      person({ userGuid: "a", username: "u1", nickname: "Sam", familyName: "Chen" }),
      person({ userGuid: "b", username: "u2", nickname: "Sam", familyName: "Lee" }),
      person({ userGuid: "c", username: "u3", nickname: "Sam C." }),
    ]),
    { a: "Sam (u1)", b: "Sam L.", c: "Sam C. (u3)" },
  );
  // 没有登录名时用 userGuid 前 4 位兜底，不会产生 "undefined"
  assert.deepEqual(
    names([
      { userGuid: "abcd-1", nickname: "Sam" },
      { userGuid: "efgh-2", nickname: "Sam" },
    ]),
    { "abcd-1": "Sam (abcd)", "efgh-2": "Sam (efgh)" },
  );
});

test("全名模式保持原样：同名也不处理", () => {
  assert.deepEqual(
    names([
      person({ userGuid: "a", fullName: "Sam Chen" }),
      person({ userGuid: "b", fullName: "Sam Chen" }),
    ], "full"),
    { a: "Sam Chen", b: "Sam Chen" },
  );
});

test("偏好解析：只认 full，其余回默认昵称", () => {
  assert.equal(parseStaffNameMode("full"), "full");
  assert.equal(parseStaffNameMode("nickname"), "nickname");
  assert.equal(parseStaffNameMode(null), "nickname", "没存过 → 默认昵称");
  assert.equal(parseStaffNameMode(undefined), "nickname");
  assert.equal(parseStaffNameMode("乱七八糟"), "nickname", "被改坏 → 默认昵称");
});

test("偏好读写：存取一致，存储抛错时静默", async () => {
  const data = new Map<string, string>();
  const memory = {
    getString: async (key: string) => data.get(key) ?? null,
    setString: async (key: string, value: string) => { data.set(key, value); },
  };
  assert.equal(await readStaffNameMode(memory), "nickname");
  await writeStaffNameMode(memory, "full");
  assert.equal(data.get(STAFF_NAME_MODE_STORAGE_KEY), "full", "写入的 key 固定");
  assert.equal(await readStaffNameMode(memory), "full");
  await writeStaffNameMode(memory, "nickname");
  assert.equal(await readStaffNameMode(memory), "nickname");

  // 存储抛错：读回默认、写入不抛
  const broken = {
    getString: async (): Promise<string | null> => { throw new Error("denied"); },
    setString: async (): Promise<void> => { throw new Error("quota"); },
  };
  assert.equal(await readStaffNameMode(broken), "nickname");
  await assert.doesNotReject(() => writeStaffNameMode(broken, "full"));
});
