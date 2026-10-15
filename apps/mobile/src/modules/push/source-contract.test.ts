// 推送模块的源码契约：
// 1. expo-notifications / expo-device 只允许以动态 import() 出现在 push-native.ts，并包在 try/catch 里（旧安装包没有原生模块，静态引用会在启动时崩溃）；
// 2. push 模块不写日志、不落盘，令牌与通知内容不进日志函数 / 缓存 / URL；
// 3. 通知收件箱模块（N1「只拉取」边界）的既有契约没有被放宽，也没有引入推送；
// 4. 接线：shell 布局在默认入口纠偏之后挂钩子，退出登录在清会话之前注销推送设备，且不产生循环依赖；
// 5. 文案：Android 渠道名称走 i18n，中英文齐全。
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const read = (path: string) => readFileSync(path, "utf8");
const listFiles = (directory: string): string[] =>
  readdirSync(directory).flatMap((name) => {
    if (name === "node_modules") return [];
    const full = join(directory, name);
    return statSync(full).isDirectory() ? listFiles(full) : [full];
  });
const isTest = (file: string) => /\.test\.tsx?$/.test(file) || file.endsWith("test-support.ts");
const stripComments = (source: string) => source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
const pushFiles = listFiles(moduleDir).filter((file) => /\.tsx?$/.test(file) && !isTest(file));
const pushSource = (name: string) => stripComments(read(join(moduleDir, name)));

assert.ok(pushFiles.length >= 10, "应扫描到 push 模块的全部源码");

// ───────── 1. 原生模块只在 push-native.ts 里以动态 import() 出现 ─────────
{
  const appSources = [...listFiles(join(mobileRoot, "src")), ...listFiles(join(mobileRoot, "app"))].filter(
    (file) => /\.tsx?$/.test(file) && !isTest(file),
  );
  const NATIVE_MODULES = ["expo-notifications", "expo-device"];
  const nativeFile = join(moduleDir, "push-native.ts");
  for (const file of appSources) {
    const code = stripComments(read(file));
    for (const name of NATIVE_MODULES) {
      const occurrences = [...code.matchAll(new RegExp(`["'\`]${name}["'\`]`, "g"))];
      if (occurrences.length === 0) continue;
      assert.equal(file, nativeFile, `${relative(mobileRoot, file)} 引用了 ${name}：只有 push-native.ts 可以引用`);
      for (const occurrence of occurrences) {
        const before = code.slice(Math.max(0, (occurrence.index ?? 0) - 8), occurrence.index);
        assert.match(before, /import\(\s*$/, `${name} 只能以 import("${name}") 出现（静态 import 会让旧安装包在启动时崩溃）`);
      }
    }
    assert.equal(/\bfrom\s+["'](expo-notifications|expo-device)["']/.test(code), false, `${relative(mobileRoot, file)} 不得静态 import 推送原生模块`);
    assert.equal(/require\(\s*["'](expo-notifications|expo-device)["']\s*\)/.test(code), false, `${relative(mobileRoot, file)} 不得 require 推送原生模块`);
  }

  // 动态 import 包在 try/catch 里：加载失败只得到 null
  const native = pushSource("push-native.ts");
  const tryStart = native.indexOf("try {", native.indexOf("loading ??="));
  const catchStart = native.indexOf("} catch", tryStart);
  assert.ok(tryStart > 0 && catchStart > tryStart, "loadPushNative 必须有 try/catch");
  const guarded = native.slice(tryStart, catchStart);
  assert.ok(guarded.includes('import("expo-notifications")') && guarded.includes('import("expo-device")'), "两个动态 import 都要在 try 里");
  assert.match(native.slice(catchStart, catchStart + 80), /return null/, "加载失败返回 null，而不是抛出");

  // 类型层面的 import("expo-notifications") 只用于类型，不产生运行时 import
  assert.equal(/^\s*import\s+(?!type\b).*["']expo-notifications["']/m.test(native), false);

  // package.json 依赖与 app.json 插件已登记
  const pkg = JSON.parse(read(join(mobileRoot, "package.json"))) as { dependencies: Record<string, string>; scripts: Record<string, string> };
  assert.ok(pkg.dependencies["expo-notifications"] && pkg.dependencies["expo-device"], "package.json 缺少推送依赖");
  assert.match(pkg.scripts["test:push"] ?? "", /source-contract\.test\.ts/, "test:push 必须包含本契约测试");
}

// ───────── 2. 不写日志、不落盘；令牌与内容不进日志 / 缓存 / URL ─────────
{
  const FORBIDDEN = [
    "console.",
    "reportApplicationLog",
    "log-center",
    "logger",
    "AsyncStorage",
    "SecureStorage",
    "SecureStore",
    "localStorage",
    "sessionStorage",
    "expo-sqlite",
    "expo-file-system",
    "FileSystem",
  ];
  for (const file of pushFiles) {
    const code = stripComments(read(file));
    for (const term of FORBIDDEN) {
      assert.equal(code.toLowerCase().includes(term.toLowerCase()), false, `${relative(mobileRoot, file)} 不应出现 ${term}（令牌与通知内容不写日志、不落盘）`);
    }
    // 进 react-query 缓存的调用不得与令牌 / 标题 / 正文挂钩
    for (const line of code.split("\n").filter((l) => /setQueryData|setQueriesData|invalidateQueries|prefetchQuery/.test(l))) {
      assert.equal(/token|title|body|content/i.test(line), false, `${relative(mobileRoot, file)} 缓存调用里出现敏感字段：${line.trim()}`);
    }
  }

  // 令牌只放请求体，绝不进 URL / 查询串
  const api = pushSource("push-api-core.ts");
  assert.equal(/params\s*:/.test(api), false, "不使用 params 传令牌");
  assert.equal(api.includes("${PUSH_DEVICES_PATH") || /PUSH_DEVICES_PATH\s*\+/.test(api) || /PUSH_DEVICES_PATH\s*\)?\s*\+/.test(api), false, "URL 不拼接任何变量");
  assert.match(api, /http\.delete\(PUSH_DEVICES_PATH,[\s\S]*data:\s*\{\s*expoPushToken\s*\}/, "DELETE 的令牌在请求体里");
  assert.match(api, /http\.post\(PUSH_DEVICES_PATH,\s*body/, "POST 的令牌在请求体里");
  const types = pushSource("push-types.ts");
  assert.match(types, /PUSH_DEVICES_PATH\s*=\s*"\/react\/v1\/notifications\/push-devices"/);
  assert.match(types, /PUSH_ANDROID_CHANNEL_ID\s*=\s*"default"/);
  assert.match(types, /"X-Skip-Center-Log":\s*"1"/, "推送设备请求不进中心日志");

  // 推送内容（标题 / 正文）只在 push-native.ts 的类型签名里出现，且不会被读取
  for (const file of pushFiles) {
    const code = stripComments(read(file));
    assert.equal(/content\??\.(title|body|subtitle)\b/.test(code), false, `${relative(mobileRoot, file)} 不得读取推送标题 / 正文`);
  }

  // 核心逻辑不依赖 React Native / 原生桥，保证能在 Node 下测试
  for (const name of ["push-registration-core.ts", "push-unregister-core.ts", "push-events-core.ts", "push-api-core.ts", "push-routing.ts", "push-types.ts", "push-async.ts", "push-logout-hook.ts"]) {
    assert.equal(/from\s+["'](react-native|expo-[a-z-]+|@\/store|@\/shared\/api\/client)/.test(pushSource(name)), false, `${name} 不得依赖 React Native / 原生模块 / 全局 store`);
  }
}

// ───────── 3. 通知收件箱模块（N1 只拉取）的既有契约没有被放宽 ─────────
{
  const notificationsContract = stripComments(read(join(mobileRoot, "src/modules/notifications/source-contract.test.ts")));
  for (const term of ["expo-notifications", "@microsoft/signalr", "registerForPushNotifications", "getExpoPushTokenAsync", "reportApplicationLog", "AsyncStorage", "SecureStore"]) {
    assert.ok(notificationsContract.includes(`"${term}"`), `notifications 的契约测试仍须禁止 ${term}`);
  }
  assert.ok(notificationsContract.includes("本期只做拉取"), "notifications 的契约范围说明未被改动");
  for (const file of listFiles(join(mobileRoot, "src/modules/notifications")).filter((f) => /\.tsx?$/.test(f) && !isTest(f))) {
    const code = stripComments(read(file));
    assert.equal(/modules\/push|\.\.\/push/.test(code), false, `${relative(mobileRoot, file)} 不应依赖 push 模块（推送放在独立模块）`);
  }
}

// ───────── 4. 接线 ─────────
{
  const layout = read(join(mobileRoot, "app/(shell)/_layout.tsx"));
  assert.ok(layout.includes('from "@/modules/push/use-push-registration"'));
  const hookCall = layout.indexOf("usePushRegistration({");
  const correction = layout.indexOf("router.replace(nextPath");
  const earlyReturn = layout.indexOf("boundAccountRestorePending ||\n    shouldWaitForNavigation ||");
  assert.ok(hookCall > 0 && correction > 0 && earlyReturn > 0);
  assert.ok(correction < hookCall, "推送钩子必须在默认入口纠偏 effect 之后（冷启动点击的导航不能被纠偏抢回去）");
  assert.ok(hookCall < earlyReturn, "钩子必须在提前 return 之前调用（hooks 顺序）");
  const hookArgs = layout.slice(hookCall, layout.indexOf("});", hookCall));
  assert.match(hookArgs, /sessionKind === "account"/, "只有个人账号会话才处理推送");
  assert.match(hookArgs, /!isIosReviewSession/, "iOS 审核会话不处理推送");
  assert.match(hookArgs, /!iosReviewOfflineGuardActive/, "iOS 审核构建的离线守卫期不处理推送");
  assert.match(hookArgs, /navigationReady/);
  assert.match(hookArgs, /!shouldWaitForNavigation/);
  assert.match(hookArgs, /!mustChangePassword/);

  const authStore = read(join(mobileRoot, "src/store/auth-store.ts"));
  assert.ok(authStore.includes('from "@/modules/push/push-logout-hook"'), "auth-store 只依赖极小的钩子注册表");
  assert.equal(/modules\/push\/(push-runtime|use-push-registration|push-native)/.test(authStore), false, "auth-store 不得依赖推送运行时（避免循环依赖）");
  const logout = authStore.slice(authStore.indexOf("async logout()"), authStore.indexOf("async hydrateIosReviewSession()"));
  const unregister = logout.indexOf("runPushLogoutHook()");
  const serverLogout = logout.indexOf("await logoutApi(rt)");
  const awaited = logout.indexOf("await pushUnregistered");
  const clear = logout.lastIndexOf("await get().clearLocalSession()");
  assert.ok(unregister > 0 && serverLogout > unregister, "注销推送设备与服务端登出并行：先启动注销");
  assert.ok(awaited > serverLogout && clear > awaited, "必须等注销结束（最长 3 秒）再清会话，之后就没有访问令牌了");
  assert.match(logout.slice(logout.indexOf("await pushUnregistered") - 20, clear), /finally|await pushUnregistered/);
  // 审核 / 设备账号会话在注销推送之前就返回：它们从不注册推送
  assert.ok(logout.indexOf('sessionKind === "iosReview"') < unregister);
  assert.ok(logout.indexOf('sessionKind === "deviceAccount"') < unregister);

  const runtime = pushSource("push-runtime.ts");
  assert.match(runtime, /registerPushLogoutHook\(unregisterOnLogout\)/);
  assert.match(runtime, /sessionKind === "account"/);
  assert.match(runtime, /isIosReviewSessionActive\(\)/);

  // 注销时限：总时限 3 秒，且不弹授权框
  const unregisterCore = pushSource("push-unregister-core.ts");
  assert.match(unregisterCore, /PUSH_UNREGISTER_TIMEOUT_MS\s*=\s*3000/);
  assert.equal(unregisterCore.includes("requestPermission"), false, "注销路径永不弹授权框");
}

// ───────── 5. 文案 ─────────
{
  const zh = JSON.parse(read(join(mobileRoot, "src/locales/zh/screens/push.json"))) as Record<string, string>;
  const en = JSON.parse(read(join(mobileRoot, "src/locales/en/screens/push.json"))) as Record<string, string>;
  assert.deepEqual(Object.keys(zh).sort(), Object.keys(en).sort(), "中英文键一致");
  assert.deepEqual(Object.keys(zh), ["channelName"], "push 命名空间只有渠道名一个文案，没有死键");
  assert.ok(/[㐀-鿿]/.test(zh.channelName) && !/[㐀-鿿]/.test(en.channelName));
  assert.ok(pushSource("push-runtime.ts").includes('i18n.t("push:channelName")'));
  const i18n = read(join(mobileRoot, "src/shared/i18n/i18n.ts"));
  assert.ok(i18n.includes("push: pushZh") && i18n.includes("push: pushEn") && i18n.includes('"push"'), "push 命名空间已注册到 i18n");
  assert.equal(existsSync(join(mobileRoot, "src/modules/push/PushPrompt.tsx")), false, "不加自定义授权弹窗");
}

console.log("source-contract.test.ts: ok");
