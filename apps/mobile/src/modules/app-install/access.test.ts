import assert from "node:assert/strict";
import { APP_INSTALL_PERMISSION, canViewAppInstall } from "./access";

assert.equal(APP_INSTALL_PERMISSION, "System.ViewMobileAppInstallLinks");
assert.equal(canViewAppInstall(true, (code) => code === APP_INSTALL_PERMISSION, false), true);
assert.equal(canViewAppInstall(false, () => true, false), false);
assert.equal(canViewAppInstall(true, () => true, true), false);
// 版本管理查看权限不等于安装页权限。
assert.equal(canViewAppInstall(true, (code) => code === "System.ViewAppDownloads", false), false);

console.log("app-install access tests passed");
