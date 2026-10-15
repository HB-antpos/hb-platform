// prepare-android-google-services.mjs 的行为测试：在临时工程目录里运行，不触碰真实的 android/ 目录。
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.join(scriptsDirectory, "prepare-android-google-services.mjs");
const SECRET = "SECRET-API-KEY-sentinel-123";

function createProject() {
  const root = mkdtempSync(path.join(tmpdir(), "hb-gs-test-"));
  mkdirSync(path.join(root, "android", "app"), { recursive: true });
  writeFileSync(path.join(root, "app.json"), JSON.stringify({ expo: { android: { package: "com.hbweb.expo" } } }));
  return root;
}

function writeCredential(root, contents) {
  const file = path.join(root, "incoming-google-services.json");
  writeFileSync(file, typeof contents === "string" ? contents : JSON.stringify(contents));
  return file;
}

function credential(packageName) {
  return {
    project_info: { project_number: "123", project_id: "demo" },
    client: [{ client_info: { mobilesdk_app_id: "1:123:android:abc", android_client_info: { package_name: packageName } }, api_key: [{ current_key: SECRET }] }],
  };
}

function run(root, env) {
  const cleanEnv = { ...process.env };
  delete cleanEnv.GOOGLE_SERVICES_JSON;
  delete cleanEnv.EAS_BUILD_PLATFORM;
  return spawnSync(process.execPath, [scriptPath, "--project-root", root], { env: { ...cleanEnv, ...env }, encoding: "utf8" });
}

const destinationOf = (root) => path.join(root, "android", "app", "google-services.json");
const created = [];
const project = () => {
  const root = createProject();
  created.push(root);
  return root;
};

try {
  // 没设置 / 只有空白：什么都不做，退出码 0（本地开发、OTA、没配置凭据的构建不受影响）
  for (const value of [undefined, "", "   "]) {
    const root = project();
    const result = run(root, value === undefined ? {} : { GOOGLE_SERVICES_JSON: value });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(existsSync(destinationOf(root)), false, "没有凭据时不生成文件");
  }

  // iOS 构建：即使设置了也跳过
  {
    const root = project();
    const file = writeCredential(root, credential("com.hbweb.expo"));
    const result = run(root, { GOOGLE_SERVICES_JSON: file, EAS_BUILD_PLATFORM: "ios" });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(existsSync(destinationOf(root)), false);
  }

  // 合法凭据：原样复制、权限收紧、输出里不含任何内容
  for (const platform of ["android", undefined]) {
    const root = project();
    const file = writeCredential(root, credential("com.hbweb.expo"));
    const result = run(root, { GOOGLE_SERVICES_JSON: file, ...(platform ? { EAS_BUILD_PLATFORM: platform } : {}) });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(destinationOf(root), "utf8"), readFileSync(file, "utf8"));
    assert.equal(statSync(destinationOf(root)).mode & 0o077, 0, "目标文件只有属主可读写");
    assert.equal((result.stdout + result.stderr).includes(SECRET), false, "输出不得包含凭据内容");
    assert.equal((result.stdout + result.stderr).includes(file), false, "输出不泄露源文件路径");
  }

  // 多个 client 里有本应用即可
  {
    const root = project();
    const value = credential("com.other.app");
    value.client.push(credential("com.hbweb.expo").client[0]);
    const result = run(root, { GOOGLE_SERVICES_JSON: writeCredential(root, value) });
    assert.equal(result.status, 0, result.stderr);
  }

  // 失败：文件不存在 / 不是 JSON / 包名不符 —— 构建应当失败，且不留下目标文件、不泄露内容
  {
    const root = project();
    const result = run(root, { GOOGLE_SERVICES_JSON: path.join(root, "missing.json") });
    assert.equal(result.status, 1);
    assert.equal(existsSync(destinationOf(root)), false);
  }
  {
    const root = project();
    const result = run(root, { GOOGLE_SERVICES_JSON: writeCredential(root, `not json ${SECRET}`) });
    assert.equal(result.status, 1);
    assert.equal(existsSync(destinationOf(root)), false);
    assert.equal((result.stdout + result.stderr).includes(SECRET), false);
  }
  {
    const root = project();
    const result = run(root, { GOOGLE_SERVICES_JSON: writeCredential(root, credential("com.other.app")) });
    assert.equal(result.status, 1);
    assert.equal(existsSync(destinationOf(root)), false);
    assert.equal((result.stdout + result.stderr).includes(SECRET), false);
  }
  {
    const root = project();
    const result = run(root, { GOOGLE_SERVICES_JSON: writeCredential(root, { client: "oops" }) });
    assert.equal(result.status, 1);
  }
} finally {
  for (const root of created) rmSync(root, { recursive: true, force: true });
}

console.log("prepare-android-google-services.test.mjs: ok");
