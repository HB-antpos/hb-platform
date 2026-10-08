import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(directory, "container-detail-screen.tsx"), "utf8");
const editStateSource = readFileSync(join(directory, "container-detail-edit-state.ts"), "utf8");
// 界面文案已迁入 containerDetail 命名空间：源码里断言「使用了某个文案键」，文案本身在 zh 语言包里断言。
const zh = JSON.parse(readFileSync(join(directory, "../../locales/zh/screens/containerDetail.json"), "utf8")) as Record<string, unknown>;

function zhText(key: string) {
  const value = key.split(".").reduce<unknown>((current, part) => (current as Record<string, unknown> | undefined)?.[part], zh);
  assert.equal(typeof value, "string", `zh 语言包缺少文案：${key}`);
  return value as string;
}

function assertScreenShows(key: string, expectedText: RegExp | string, message: string) {
  assert.ok(source.includes(`"${key}"`), `${message}（界面必须使用文案键 ${key}）`);
  const text = zhText(key);
  assert.ok(typeof expectedText === "string" ? text.includes(expectedText) : expectedText.test(text), `${message}（文案：${text}）`);
}

assert.match(source, /expectedServerFieldTokens/, "编辑保存必须携带字段级服务器基线令牌");
assert.match(editStateSource, /changedFields/, "编辑保存必须只提交实际修改字段，避免不同字段编辑互相冲突");
assert.match(editStateSource, /changedFields\.flatMap/, "基线令牌必须与实际修改字段一一对应");
assert.match(source, /overrideAcknowledgements/, "确认覆盖必须携带刚看到的服务器令牌");
assert.match(source, /result\.conflicts/, "部分成功时必须保留字段冲突供弹窗处理");
assertScreenShows("edit.useServer", "采用服务器值", "冲突面板必须允许采用服务器值");
assertScreenShows("edit.keepMine", "保留我的值", "冲突面板必须允许确认覆盖我的值");
assert.match(source, /CONCURRENCY_TOKEN_REQUIRED/, "旧版令牌拒绝必须提示升级，不能静默失败");
assert.match(source, /AppState\.addEventListener\(\s*"change"/, "App 切后台必须暂停货柜协作心跳");
assert.match(source, /heartbeatContainerDetailPresence/, "页面打开与编辑态必须更新协作心跳");
assert.match(source, /getContainerDetailPresence/, "回到前台必须刷新其他活动用户");
assert.match(source, /buildForegroundTokenConflicts/, "回到前台刷新令牌后必须比较仍打开弹窗的字段基线");
assertScreenShows("messages.foregroundConflicts", /服务器已更新 \{\{count\}\} 个正在编辑的字段/, "前台发现服务器字段变化必须保持弹窗并提示冲突");
assert.match(source, /tRef\.current\("messages\.foregroundConflicts", \{ count: conflicts\.length \}\)/, "前台冲突提示必须带上冲突字段个数");
assertScreenShows("edit.useAllServer", "全部采用服务器值", "多字段冲突必须支持批量采用服务器值");
assertScreenShows("edit.keepAllMine", "全部保留我的值", "多字段冲突必须支持批量保留我的值");
assertScreenShows("edit.overrideConfirmTitle", "确认覆盖服务器值", "批量保留我的值必须二次确认");
assert.match(source, /formatRecentActivity\(item\.lastActiveAt, /, "协作提示必须显示其他用户最近活动时间");
assert.match(source, /previewContainerDetailBatchAction/, "移动端批量操作必须先请求服务器预览");
assert.match(source, /previewToken/, "批量执行必须携带服务器签名预览令牌");
assert.match(source, /response\?\.status === 409/, "批量预览令牌失效必须识别 HTTP 409");
assert.match(source, /setBatchPreview\(null\);[\s\S]*previewBulkMutation\.mutate/, "预览失效后必须先清除旧令牌，再刷新预览");
assertScreenShows("messages.previewRefreshed", "请重新确认执行", "刷新预览后必须要求用户重新确认，不能自动重放批量写入");
assert.match(source, /isCurrentContainerDetailEditSession/, "迟到保存响应必须验证当前编辑会话");
assert.match(source, /disabled=\{updateMutation\.isPending\}/, "保存期间必须锁定编辑控件和重复保存");
assert.match(source, /onDismiss=\{updateMutation\.isPending \? \(\) => undefined : closeEditModal\}/, "保存期间必须禁止关闭编辑弹窗");
assert.match(source, /dismissable=\{!updateMutation\.isPending\}/, "保存期间底部弹窗的遮罩与关闭按钮也必须失效");

console.log("container-detail-concurrency-contract.test.ts: ok");
