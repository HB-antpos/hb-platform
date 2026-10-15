// 点击推送的去向映射：全部分支。
import assert from "node:assert/strict";
import { buildFaceHubRoute, parseFaceHubParams, resolveFaceHubActiveTab } from "../navigation/face-hub";
import { PUSH_TYPE_FACE_UNWELCOME_EXPIRING, PUSH_TYPE_FACE_UNWELCOME_HIT, resolvePushTarget } from "./push-routing";

const INBOX = { kind: "notifications" } as const;

// 疑似命中 → 「名单」页签 · 命中复核
assert.deepEqual(resolvePushTarget("face.unwelcome-hit", { notificationId: "n1", type: "face.unwelcome-hit" }), {
  kind: "face-hub",
  params: { tab: "list", sub: "review" },
});
assert.equal(PUSH_TYPE_FACE_UNWELCOME_HIT, "face.unwelcome-hit", "与后端 AppNotificationTypes.FaceUnwelcomeHit 一致");
assert.equal(PUSH_TYPE_FACE_UNWELCOME_EXPIRING, "face.unwelcome-expiring", "与后端 AppNotificationTypes.FaceUnwelcomeExpiring 一致");

// 名单条目将到期 → 「名单」页签 · 名单条目
assert.deepEqual(resolvePushTarget("face.unwelcome-expiring", {}), { kind: "face-hub", params: { tab: "list", sub: "entries" } });

// 其它 face.*（含将来新增）→ 合并入口，不指定页签（由合并页按各页签权限退回第一个可见页签）
assert.deepEqual(resolvePushTarget("face.something-new", {}), { kind: "face-hub", params: {} });
assert.deepEqual(resolvePushTarget("face.", {}), { kind: "face-hub", params: {} });

// 前后空白容忍
assert.deepEqual(resolvePushTarget("  face.unwelcome-hit  ", {}), { kind: "face-hub", params: { tab: "list", sub: "review" } });

// 其它类型 → 通知收件箱
for (const type of ["approval.submitted", "approval.decided", "store.price_updates", "container.new_products", "system.test", "unknown.type"]) {
  assert.deepEqual(resolvePushTarget(type, { notificationId: "n1", type }), INBOX, `${type} 落到收件箱`);
}
// 前缀只认 "face."：不是 face 开头 / 只是包含 face 的类型不进合并页
assert.deepEqual(resolvePushTarget("interface.changed", {}), INBOX);
assert.deepEqual(resolvePushTarget("faces.x", {}), INBOX);

// 未知 / 缺字段 → 通知收件箱
for (const type of [undefined, null, "", "   ", 42, {}, [], true]) {
  assert.deepEqual(resolvePushTarget(type, undefined), INBOX, `type=${JSON.stringify(type)} 落到收件箱`);
}
assert.deepEqual(resolvePushTarget("approval.decided"), INBOX, "data 省略也可以");
assert.deepEqual(resolvePushTarget("approval.decided", null), INBOX);

// 路由目标能被合并页接受：生成的路由经 parseFaceHubParams 往返不丢参数
{
  const target = resolvePushTarget("face.unwelcome-hit", {});
  assert.equal(target.kind, "face-hub");
  if (target.kind === "face-hub") {
    const route = buildFaceHubRoute(target.params);
    assert.equal(route.pathname, "/(shell)/face-hub");
    assert.deepEqual(route.params, { tab: "list", sub: "review" });
    assert.deepEqual(parseFaceHubParams(route.params), target.params);
    // 没有「名单」页签权限时，合并页的既有逻辑退回第一个可见页签（这里不重复做权限判断）
    assert.equal(resolveFaceHubActiveTab(["capture"], target.params.tab), "capture");
    assert.equal(resolveFaceHubActiveTab(["capture", "list"], target.params.tab), "list");
    assert.equal(resolveFaceHubActiveTab([], target.params.tab), null);
  }
}

console.log("push-routing.test.ts: ok");
