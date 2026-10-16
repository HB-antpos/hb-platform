import assert from "node:assert/strict";
import test from "node:test";
import {
  buildIdentityMeta,
  countAttentionItems,
  resolveAvatarInitial,
  resolveIdentityKind,
  resolveRoleTag,
  resolveRuntimeVersionLabel,
} from "./settings-summary";

const ok = { canViewDeviceCard: true, deviceReady: true, labelPrinterError: false, receiptPrinterError: false };

test("需处理项数：设备未就绪、标签机出错、小票机出错各算一项", () => {
  assert.equal(countAttentionItems(ok), 0);
  assert.equal(countAttentionItems({ ...ok, deviceReady: false }), 1);
  assert.equal(countAttentionItems({ ...ok, labelPrinterError: true }), 1);
  assert.equal(countAttentionItems({ ...ok, deviceReady: false, labelPrinterError: true, receiptPrinterError: true }), 3);
});

test("没有设备会话 / 绑定 / 用户时，设备未就绪不算需处理", () => {
  assert.equal(countAttentionItems({ ...ok, canViewDeviceCard: false, deviceReady: false }), 0);
  // 但打印机出错照样要提示。
  assert.equal(countAttentionItems({ ...ok, canViewDeviceCard: false, deviceReady: false, receiptPrinterError: true }), 1);
});

test("名片类型：有用户是账号名片，只有设备授权是设备名片，都没有就是未登录", () => {
  assert.equal(resolveIdentityKind({ hasUser: true, hasDeviceSession: true, hasAccountBinding: false }), "account");
  assert.equal(resolveIdentityKind({ hasUser: false, hasDeviceSession: true, hasAccountBinding: false }), "device");
  assert.equal(resolveIdentityKind({ hasUser: false, hasDeviceSession: false, hasAccountBinding: true }), "device");
  assert.equal(resolveIdentityKind({ hasUser: false, hasDeviceSession: false, hasAccountBinding: false }), "none");
});

test("角色标签：取第一个并本地化，其余折成 +N；没有角色不显示", () => {
  assert.equal(resolveRoleTag(undefined, "zh"), null);
  assert.equal(resolveRoleTag([], "zh"), null);
  assert.equal(resolveRoleTag(["  "], "zh"), null);
  const single = resolveRoleTag(["Admin"], "en");
  assert.ok(single);
  assert.equal(single.extra, 0);
  assert.ok(single.label.length > 0);
  const many = resolveRoleTag(["StoreManager", "Order", "Admin"], "zh");
  assert.ok(many);
  assert.equal(many.extra, 2);
  // 本地化角色名时中英文不同（至少未知角色原样返回，已知角色按语言翻译）。
  const zh = resolveRoleTag(["StoreManager"], "zh");
  const en = resolveRoleTag(["StoreManager"], "en");
  assert.ok(zh && en);
  assert.notEqual(zh.label, en.label);
});

test("名片第二行：缺哪个省略哪个，不留悬空分隔符", () => {
  assert.equal(buildIdentityMeta(["xiaoming.li", "Bankstown 1008"]), "xiaoming.li · Bankstown 1008");
  assert.equal(buildIdentityMeta(["xiaoming.li", undefined]), "xiaoming.li");
  assert.equal(buildIdentityMeta([null, "  Bankstown  "]), "Bankstown");
  assert.equal(buildIdentityMeta([undefined, null, ""]), "");
});

test("头像首字：姓名首字（英文大写），没有名字用 ?；表情符号等多码点字符不会被劈开", () => {
  assert.equal(resolveAvatarInitial("李小明"), "李");
  assert.equal(resolveAvatarInitial("grace.liu"), "G");
  assert.equal(resolveAvatarInitial("  "), "?");
  assert.equal(resolveAvatarInitial(undefined), "?");
  assert.equal(resolveAvatarInitial("😀abc"), "😀");
});

test("运行时版本一行：没有就不显示", () => {
  assert.equal(resolveRuntimeVersionLabel("1.0.9"), "1.0.9");
  assert.equal(resolveRuntimeVersionLabel("  "), null);
  assert.equal(resolveRuntimeVersionLabel(null), null);
  assert.equal(resolveRuntimeVersionLabel(undefined), null);
});
