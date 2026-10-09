import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import {
  parseReleaseCenterView,
  parseWpfChannel,
  worstLaneStatus,
} from "./release-center-nav";

// 深链 ?view= 与 Web 版本发布中心取值一致；未知值回到总览。
assert.equal(parseReleaseCenterView(undefined), "overview");
assert.equal(parseReleaseCenterView("wpf"), "wpf");
assert.equal(parseReleaseCenterView(["handheld"]), "handheld");
assert.equal(parseReleaseCenterView("tools"), "overview");
assert.equal(parseWpfChannel("preview"), "preview");
assert.equal(parseWpfChannel("anything"), "production");

// 终端状态取最需要关注的线路：读取失败 > 待处理 > 已激活 > 未启用。
assert.equal(worstLaneStatus([]), null);
assert.equal(worstLaneStatus(["inactive", "active"]), "active");
assert.equal(worstLaneStatus(["active", "pending", "inactive"]), "pending");
assert.equal(worstLaneStatus(["pending", "error"]), "error");

// 合并后的入口：工作台只保留版本发布中心，旧 wpf-versions 路由跳到它的 WPF 终端。
const root = resolve(__dirname, "../../..");
const workbenchSource = readFileSync(
  resolve(root, "src/modules/navigation/workbench.ts"),
  "utf8",
);
assert.ok(
  workbenchSource.includes('routeName: "app-downloads"') &&
    !workbenchSource.includes('routeName: "wpf-versions"'),
  "工作台只显示版本发布中心入口，不再单独显示 WPF 版本管理",
);
const wpfRouteSource = readFileSync(
  resolve(root, "app/(shell)/wpf-versions.tsx"),
  "utf8",
);
assert.ok(
  wpfRouteSource.includes("<Redirect") &&
    wpfRouteSource.includes('pathname: "/(shell)/app-downloads"') &&
    wpfRouteSource.includes('view: "wpf"') &&
    wpfRouteSource.includes("VersionManagementGuard"),
  "旧 WPF 入口应在管理员守卫内跳转到版本发布中心的 WPF 终端",
);

console.log("release center nav tests passed");
