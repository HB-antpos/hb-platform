import assert from "node:assert/strict";
import test from "node:test";
import { describeRow, describeWhen, resolveRoleTone } from "./presentation";
import { VAULT_TOKEN_TTL_MS, type VaultEntry } from "./vault";

const HOST = "https://api.example.com";
const NOW = new Date(2026, 9, 17, 15, 30).getTime();

function entry(overrides: Partial<VaultEntry> = {}): VaultEntry {
  return {
    userGuid: "g",
    username: "u",
    roleNames: [],
    apiHost: HOST,
    refreshToken: "rt",
    tokenIssuedAt: NOW,
    savedAt: NOW,
    lastUsedAt: NOW,
    ...overrides,
  };
}

test("角色颜色：管理员紫、店长蓝、仓库管理员 / 订货员中性", () => {
  assert.equal(resolveRoleTone(["Admin"]), "admin");
  assert.equal(resolveRoleTone(["Order", "Administrator"]), "admin");
  assert.equal(resolveRoleTone(["Manager"]), "manager");
  assert.equal(resolveRoleTone(["Warehouse Manager"]), "neutral");
  assert.equal(resolveRoleTone(["Order"]), "neutral");
  assert.equal(resolveRoleTone(undefined), "neutral");
});

test("上次使用按本地自然日算：今天 / 昨天 / N 天前（跨午夜不按 24 小时算）", () => {
  const lateLastNight = new Date(2026, 9, 16, 23, 50).getTime();
  assert.deepEqual(describeWhen(NOW, NOW), { key: "today" });
  assert.deepEqual(describeWhen(lateLastNight, NOW), { key: "yesterday" });
  assert.deepEqual(describeWhen(new Date(2026, 9, 14, 8, 0).getTime(), NOW), { key: "daysAgo", days: 3 });
  assert.deepEqual(describeWhen(NOW + 1000, NOW), { key: "today" });
});

test("行描述：可用 / 过期 / 被拒 / 其他服务器", () => {
  assert.equal(describeRow(entry(), NOW, HOST).status, "ok");
  const expired = describeRow(entry({ tokenIssuedAt: NOW - VAULT_TOKEN_TTL_MS - 60_000 }), NOW, HOST);
  assert.equal(expired.status, "needsRelogin");
  assert.equal(expired.reloginReason, "expired");
  assert.equal(expired.meta.key, "expiredMeta");
  const rejected = describeRow(entry({ needsRelogin: true }), NOW, HOST);
  assert.equal(rejected.meta.key, "rejectedMeta");
  assert.equal(rejected.reloginReason, "rejected");
  assert.equal(describeRow(entry(), NOW, "https://other.example.com").meta.key, "otherHostMeta");
});
