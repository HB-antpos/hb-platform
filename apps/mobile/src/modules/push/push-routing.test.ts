// 点击推送的去向映射：全部分支。
import assert from "node:assert/strict";
import { buildFaceHubRoute, parseFaceHubParams, resolveFaceHubActiveTab, type FaceHubTabKey } from "../navigation/face-hub";
import { resolveNotificationTarget } from "../notifications/logic";
import {
  FACE_HIT_PUSH_PARAMS,
  PUSH_TYPE_FACE_UNWELCOME_EXPIRING,
  PUSH_TYPE_FACE_UNWELCOME_HIT,
  resolvePushTarget,
} from "./push-routing";

const INBOX = { kind: "notifications" } as const;
const HIT_PARAMS = { tab: "capture", filter: "blacklist", hitStatus: "unread", sub: "review" } as const;

// 疑似命中 → 「抓拍」页签 · 黑名单 · 未读命中（没有抓拍权限时退回「名单」页签 · 命中复核）
assert.deepEqual(resolvePushTarget("face.unwelcome-hit", { notificationId: "n1", type: "face.unwelcome-hit" }), {
  kind: "face-hub",
  params: HIT_PARAMS,
});
assert.deepEqual(FACE_HIT_PUSH_PARAMS, HIT_PARAMS);
assert.notEqual(FACE_HIT_PUSH_PARAMS.tab, "list", "不能再直接落到「命中复核 · 待复核」：高相似度命中被自动确认后那里是空的");
assert.equal(PUSH_TYPE_FACE_UNWELCOME_HIT, "face.unwelcome-hit", "与后端 AppNotificationTypes.FaceUnwelcomeHit 一致");
assert.equal(PUSH_TYPE_FACE_UNWELCOME_EXPIRING, "face.unwelcome-expiring", "与后端 AppNotificationTypes.FaceUnwelcomeExpiring 一致");

// 名单条目将到期 → 「名单」页签 · 名单条目
assert.deepEqual(resolvePushTarget("face.unwelcome-expiring", {}), { kind: "face-hub", params: { tab: "list", sub: "entries" } });

// 其它 face.*（含将来新增）→ 合并入口，不指定页签（由合并页按各页签权限退回第一个可见页签）
assert.deepEqual(resolvePushTarget("face.something-new", {}), { kind: "face-hub", params: {} });
assert.deepEqual(resolvePushTarget("face.", {}), { kind: "face-hub", params: {} });

// 前后空白容忍
assert.deepEqual(resolvePushTarget("  face.unwelcome-hit  ", {}), { kind: "face-hub", params: HIT_PARAMS });

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
    assert.deepEqual(route.params, HIT_PARAMS);
    assert.deepEqual(parseFaceHubParams(route.params), target.params);
    // 没有「抓拍」页签权限时，合并页的既有逻辑退回第一个可见页签（这里不重复做权限判断）
    assert.equal(resolveFaceHubActiveTab(["capture"], target.params.tab), "capture");
    assert.equal(resolveFaceHubActiveTab(["capture", "list"], target.params.tab), "capture");
    assert.equal(resolveFaceHubActiveTab(["list"], target.params.tab), "list");
    assert.equal(resolveFaceHubActiveTab([], target.params.tab), null);
  }
}

// 推送与通知收件箱点同一条疑似命中通知：各权限组合下最终落点必须一致（push 不看权限，靠合并页的页签回退达成）
{
  const pushTarget = resolvePushTarget("face.unwelcome-hit", {});
  assert.equal(pushTarget.kind, "face-hub");
  if (pushTarget.kind === "face-hub") {
    const combos = [
      { canViewCapture: true, canViewList: true },
      { canViewCapture: true, canViewList: false },
      { canViewCapture: false, canViewList: true },
    ];
    for (const access of combos) {
      const visibleTabs: FaceHubTabKey[] = [];
      if (access.canViewCapture) visibleTabs.push("capture");
      if (access.canViewList) visibleTabs.push("list");
      const inboxTarget = resolveNotificationTarget({ type: "face.unwelcome-hit" }, access);
      assert.equal(inboxTarget.kind, "route");
      if (inboxTarget.kind !== "route") continue;
      const label = JSON.stringify(access);
      const pushTab = resolveFaceHubActiveTab(visibleTabs, pushTarget.params.tab);
      assert.equal(pushTab, inboxTarget.params.tab, `${label}：推送与收件箱落在同一个一级页签`);
      if (pushTab === "capture") {
        assert.equal(pushTarget.params.filter, inboxTarget.params.filter, `${label}：同一个筛选`);
        assert.equal(pushTarget.params.hitStatus, inboxTarget.params.hitStatus, `${label}：同一个命中状态`);
      } else {
        assert.equal(pushTarget.params.sub, inboxTarget.params.sub, `${label}：名单页签里同一个二级分段`);
      }
    }
  }
}

console.log("push-routing.test.ts: ok");
