import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync } from "node:fs";

const appRoot = new URL("../", import.meta.url);
const workspaceRoot = new URL("../../", appRoot);
const packageJson = JSON.parse(
  readFileSync(new URL("package.json", appRoot), "utf8"),
);
const packageLock = JSON.parse(
  readFileSync(new URL("package-lock.json", workspaceRoot), "utf8"),
);
const appLockEntry = packageLock.packages["apps/pos-handheld"];
const easJson = JSON.parse(readFileSync(new URL("eas.json", appRoot), "utf8"));
const appConfigSource = readFileSync(
  new URL("app.config.ts", appRoot),
  "utf8",
);
const iosIdentity = JSON.parse(
  readFileSync(
    new URL(
      "src/core/contracts/pos-handheld-ios-identity.json",
      appRoot,
    ),
    "utf8",
  ),
);
const appUpdateContractSource = readFileSync(
  new URL("src/core/contracts/app-updates.ts", appRoot),
  "utf8",
);
const appEntrySource = readFileSync(new URL("index.js", appRoot), "utf8");

assert.equal(packageJson.name, "@hb/pos-handheld");
assert.equal(packageJson.version, "0.1.0");
assert.equal(appLockEntry.name, "@hb/pos-handheld");
assert.equal(appLockEntry.version, "0.1.0");
assert.equal(packageJson.main, "index.js");
assert.equal(packageJson.private, true);
assert.equal(
  appEntrySource.trim(),
  [
    'import "./src/core/performance/business-startup-origin";',
    'import "./src/core/performance/business-startup-clock";',
    'import "./src/core/observability/sentry";',
    'import "expo-router/entry";',
  ].join("\n"),
  "手持入口必须先注册性能时钟与 Sentry，再交给 Expo Router。",
);
assert.equal(
  packageJson.scripts.android,
  "npm run test:react-native-scheduler && expo run:android",
);
assert.equal(
  packageJson.scripts.ios,
  "npm run test:react-native-scheduler && expo run:ios",
);
assert.equal(packageJson.scripts["prebuild:android"], "expo prebuild --platform android");
assert.equal(packageJson.scripts["prebuild:ios"], "expo prebuild --platform ios");

assert.equal(easJson.build.production.distribution, "store");
assert.equal(easJson.build.production.channel, "pos-handheld-production");
assert.equal(easJson.build["android-internal"].distribution, "internal");
assert.equal(
  easJson.build["android-internal"].channel,
  "pos-handheld-production",
);
assert.equal(easJson.build["android-internal"].android.buildType, "apk");

assert.match(appConfigSource, /name:\s*"HB POS Mobile"/u);
assert.match(appConfigSource, /slug:\s*"hb-pos-handheld"/u);
assert.match(appConfigSource, /scheme:\s*"hbpos-handheld"/u);
assert.match(appConfigSource, /platforms:\s*\["ios",\s*"android"\]/u);
assert.match(appConfigSource, /orientation:\s*"portrait"/u);
assert.deepEqual(iosIdentity, {
  bundleIdentifier: "com.hbweb.poshandheld",
});
assert.match(
  appConfigSource,
  /import\s+posHandheldIosIdentity\s+from\s+"\.\/src\/core\/contracts\/pos-handheld-ios-identity\.json"/u,
);
assert.match(
  appConfigSource,
  /bundleIdentifier:\s*posHandheldIosIdentity\.bundleIdentifier/u,
);
assert.doesNotMatch(
  appConfigSource,
  /bundleIdentifier:\s*"com\.hbweb\.poshandheld"/u,
);
assert.match(
  appUpdateContractSource,
  /import\s+posHandheldIosIdentity\s+from\s+"\.\/pos-handheld-ios-identity\.json"/u,
);
assert.match(
  appUpdateContractSource,
  /POS_HANDHELD_IOS_BUNDLE_IDENTIFIER\s*=\s*\n\s*posHandheldIosIdentity\.bundleIdentifier/u,
);
assert.match(appConfigSource, /package:\s*"com\.hbweb\.poshandheld"/u);
assert.match(appConfigSource, /supportsTablet:\s*false/u);
assert.match(appConfigSource, /deploymentTarget:\s*"17\.0"/u);
assert.match(appConfigSource, /minSdkVersion:\s*29/u);
assert.match(appConfigSource, /useSQLCipher:\s*true/u);
assert.doesNotMatch(appConfigSource, /iPadOS|pos-ipad|posipad/iu);
assert.doesNotMatch(appConfigSource, /with-hb-external-display/iu);

for (const relativePath of [
  "modules/hb-external-display",
  "plugins/with-hb-external-display.js",
  "plugins/with-hb-external-display.test.mjs",
  "src/features/customer-display",
  "src/core/peripherals/customer-display",
]) {
  assert.equal(
    existsSync(new URL(relativePath, appRoot)),
    false,
    `手持 POS 不得包含客显实现：${relativePath}`,
  );
}

function collectProductionSources(relativeRoot) {
  const root = new URL(relativeRoot, appRoot);
  if (!existsSync(root)) {
    return [];
  }
  return readdirSync(root, { recursive: true, withFileTypes: true })
    .filter((entry) => {
      if (
        !entry.isFile() ||
        !/\.(?:[cm]?[jt]sx?|json|kt|swift)$/u.test(entry.name)
      ) {
        return false;
      }
      const fullPath = `${entry.parentPath}/${entry.name}`;
      return !(
        /(?:^|\/)src\/generated(?:\/|$)/u.test(fullPath) ||
        /(?:^|\/)tests?(?:\/|$)/u.test(fullPath) ||
        /(?:\.test|\.rntl|\.fixture)\.[cm]?[jt]sx?$/u.test(entry.name)
      );
    })
    .map((entry) => ({
      path: `${entry.parentPath}/${entry.name}`,
      source: readFileSync(`${entry.parentPath}/${entry.name}`, "utf8"),
    }));
}

const productionSources = [
  ...collectProductionSources("app/"),
  ...collectProductionSources("src/"),
  ...collectProductionSources("modules/"),
  ...collectProductionSources("plugins/"),
];
const forbiddenProductionReferences = productionSources
  .filter(({ source }) =>
    /customer[-_ ]?display|external[-_ ]?display|ipad/iu.test(source),
  )
  .map(({ path }) => path.replace(`${new URL(".", appRoot).pathname}`, ""));
assert.deepEqual(
  forbiddenProductionReferences,
  [],
  "手持 POS 生产源码不得保留客显或 iPad 专属实现/命名空间。",
);

const forbiddenVisibleChineseCopy = productionSources
  .filter(({ path }) => {
    const relativePath = path.replace(
      `${new URL(".", appRoot).pathname}`,
      "",
    );
    return (
      /^(?:app|src\/ui|src\/i18n\/locales)\//u.test(relativePath) ||
      /^src\/features\/.*\.(?:tsx|json)$/u.test(relativePath)
    );
  })
  .filter(({ source }) => /客显/u.test(source))
  .map(({ path }) => path.replace(`${new URL(".", appRoot).pathname}`, ""));
assert.deepEqual(
  forbiddenVisibleChineseCopy,
  [],
  "手持 POS 用户可见源码不得保留客显文案。",
);

assert.equal(
  existsSync(new URL("app/(tabs)", appRoot)),
  false,
  "手持 POS 使用任务流导航，不得引入底部 Tab。",
);

console.log("pos-handheld project contract: ok");
