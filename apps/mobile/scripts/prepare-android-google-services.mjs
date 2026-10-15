// EAS 构建钩子（package.json 的 eas-build-post-install）：把文件型环境变量 GOOGLE_SERVICES_JSON 指向的
// Firebase 配置复制到 android/app/google-services.json，供 Android 原生工程的 Google Services 插件使用（FCM 推送）。
//
// 为什么需要它：本仓库的 android/ 目录是提交在仓库里的原生工程（bare），EAS 不会对它重新 prebuild，
// app.config.ts 里的 android.googleServicesFile 只对 prebuild 流程生效，所以要在构建前手工放好文件。
//
// 约定：
// - 没有设置 GOOGLE_SERVICES_JSON（本地开发、OTA、没配置凭据的构建）→ 什么都不做，退出码 0；
// - iOS 构建 → 跳过；
// - 设置了但文件不存在 / 不是合法 JSON / 不含本应用的包名 → 直接让构建失败（拿一个坏凭据去构建只会得到一个推送必坏的包）；
// - 任何输出都不得包含文件内容或其中的字段值；目标文件已在 .gitignore 里，绝不提交。
import { chmodSync, copyFileSync, existsSync, mkdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));

function parseProjectRoot(argv) {
  const index = argv.indexOf("--project-root");
  return index >= 0 && argv[index + 1] ? path.resolve(argv[index + 1]) : path.resolve(scriptsDirectory, "..");
}

function main() {
  const projectRoot = parseProjectRoot(process.argv.slice(2));
  const source = process.env.GOOGLE_SERVICES_JSON?.trim();

  if (process.env.EAS_BUILD_PLATFORM === "ios") {
    console.log("iOS 构建，跳过 Android 推送凭据准备。");
    return 0;
  }
  if (!source) {
    console.log("未设置 GOOGLE_SERVICES_JSON，跳过 Android 推送凭据准备（该构建不带 Android 推送）。");
    return 0;
  }

  const failures = [];
  if (!existsSync(source) || !statSync(source).isFile()) {
    failures.push("GOOGLE_SERVICES_JSON 指向的文件不存在");
  } else {
    let parsed = null;
    try {
      parsed = JSON.parse(readFileSync(source, "utf8"));
    } catch {
      failures.push("GOOGLE_SERVICES_JSON 指向的文件不是合法 JSON");
    }
    if (parsed) {
      const expectedPackage = JSON.parse(readFileSync(path.join(projectRoot, "app.json"), "utf8")).expo?.android?.package;
      const packages = Array.isArray(parsed.client)
        ? parsed.client.map((client) => client?.client_info?.android_client_info?.package_name)
        : [];
      if (!expectedPackage || !packages.includes(expectedPackage)) {
        failures.push("google-services.json 中没有本应用的 Android 包名（须与 app.json 的 expo.android.package 一致）");
      }
    }
  }

  if (failures.length > 0) {
    console.error("Android 推送凭据准备失败：");
    failures.forEach((failure) => console.error(`- ${failure}`));
    return 1;
  }

  const destination = path.join(projectRoot, "android", "app", "google-services.json");
  mkdirSync(path.dirname(destination), { recursive: true });
  copyFileSync(source, destination);
  chmodSync(destination, 0o600);
  console.log("已准备 android/app/google-services.json（内容不输出，文件已被 .gitignore 忽略）。");
  return 0;
}

process.exitCode = main();
