import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";
import { existsSync, readFileSync, readdirSync, realpathSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const appRoots = [
  join(repositoryRoot, "apps", "pos-ipad"),
  join(repositoryRoot, "apps", "pos-handheld"),
];
const sharedPackages = [
  ["pos-domain", "@hb/pos-domain"],
  ["pos-api-client", "@hb/pos-api-client"],
  ["pos-db", "@hb/pos-db"],
  ["pos-sync", "@hb/pos-sync"],
  ["pos-payments-core", "@hb/pos-payments-core"],
  ["pos-receipt-core", "@hb/pos-receipt-core"],
  ["pos-testing", "@hb/pos-testing"],
];

function readJson(path) {
  return JSON.parse(readFileSync(path, "utf8"));
}

function resolveFrom(specifier, appRoot) {
  return realpathSync(require.resolve(specifier, { paths: [appRoot] }));
}

test("根 npm workspace 只包含两个 POS App 与七个共享包", () => {
  const rootPackage = readJson(join(repositoryRoot, "package.json"));
  assert.deepEqual(rootPackage.workspaces, [
    "apps/pos-ipad",
    "apps/pos-handheld",
    "packages/pos-*",
  ]);
  assert.ok(
    rootPackage.workspaces.every((entry) => !entry.includes("/modules")),
    "App 内本地 Expo 模块不得成为根 workspace",
  );

  for (const [directory, expectedName] of sharedPackages) {
    const packageRoot = join(repositoryRoot, "packages", directory);
    const packageJson = readJson(join(packageRoot, "package.json"));
    assert.equal(packageJson.name, expectedName);
    assert.equal(packageJson.version, "0.1.0");
    assert.equal(packageJson.private, true);
    assert.equal(packageJson.types, "./src/index.ts");
    assert.equal(packageJson.exports?.["."]?.default, "./src/index.ts");
  }
});

test("根门禁和两个 App 默认测试都执行七个共享包测试", () => {
  const rootPackage = readJson(join(repositoryRoot, "package.json"));
  const aggregateScript = rootPackage.scripts?.["test:pos-packages"] ?? "";
  const sharedCiScript = rootPackage.scripts?.["test:pos-shared-ci"] ?? "";
  const appAggregateScript = rootPackage.scripts?.["test:pos-apps"] ?? "";
  const appRunnerSource = readFileSync(
    join(repositoryRoot, "scripts", "pos-shared", "run-pos-app-tests.mjs"),
    "utf8",
  );
  assert.match(rootPackage.scripts?.test ?? "", /npm run test:pos-shared-ci/u);
  assert.match(rootPackage.scripts?.test ?? "", /npm run test:pos-apps/u);
  assert.equal(rootPackage.scripts?.["generate:pos-workspace-lock"], undefined);
  assert.equal(
    existsSync(join(repositoryRoot, "scripts", "pos-shared", "compose-pos-workspace-lock.mjs")),
    false,
  );
  assert.match(sharedCiScript, /npm run test:pos-packages/u);
  assert.match(sharedCiScript, /npm run lint:pos-packages/u);
  assert.match(appAggregateScript, /run-pos-app-tests\.mjs/u);
  assert.match(appRunnerSource, /--no-install/u);
  assert.match(appRunnerSource, /HB_POS_IPAD_GENERATED_IOS_ROOT/u);

  for (const [, packageName] of sharedPackages) {
    assert.ok(
      aggregateScript.includes(`--workspace=${packageName}`),
      `共享测试门禁缺少 ${packageName}`,
    );
  }

  for (const appRoot of appRoots) {
    const appPackage = readJson(join(appRoot, "package.json"));
    assert.ok(
      appRunnerSource.includes(`--workspace=${appPackage.name}`),
      `根最终门禁缺少 ${appPackage.name} 完整测试`,
    );
    assert.equal(
      appPackage.scripts?.["test:shared-core"],
      "npm --prefix ../.. run test:pos-packages",
    );
    assert.match(appPackage.scripts?.test ?? "", /^npm run test:shared-core &&/u);
  }
});

test("两个 POS App 显式声明所有直接使用的共享生产包", () => {
  const productionPackageNames = sharedPackages
    .filter(([directory]) => directory !== "pos-testing")
    .map(([, packageName]) => packageName);

  for (const appRoot of appRoots) {
    const appPackage = readJson(join(appRoot, "package.json"));
    for (const packageName of productionPackageNames) {
      // npm 11 仍拒绝 workspace:*；精确匹配本地版本可保持 workspace link，且避免回退到 registry。
      assert.equal(
        appPackage.dependencies?.[packageName],
        "0.1.0",
        `${appPackage.name} 必须声明直接共享依赖 ${packageName}`,
      );
    }

    const importedPackages = new Set();
    for (const sourceRoot of [join(appRoot, "app"), join(appRoot, "src")]) {
      for (const path of walkFiles(sourceRoot)) {
        if (
          !/\.[cm]?[jt]sx?$/u.test(path) ||
          /(?:\.test|\.spec|\.rntl)\.[cm]?[jt]sx?$/u.test(path)
        ) {
          continue;
        }
        for (const match of readFileSync(path, "utf8").matchAll(/@hb\/pos-[a-z-]+/gu)) {
          importedPackages.add(match[0]);
        }
      }
    }

    for (const packageName of importedPackages) {
      assert.ok(
        Object.hasOwn(appPackage.dependencies ?? {}, packageName),
        `${appPackage.name} 的生产源码直接导入 ${packageName}，但 dependencies 未声明`,
      );
    }
  }
});

test("根 patch-package 补丁没有会破坏解析的多余尾部空行", () => {
  for (const patchName of [
    "react-native+0.81.5.patch",
    "expo-audio+1.1.1.patch",
  ]) {
    const source = readFileSync(
      join(repositoryRoot, "patches", patchName),
      "utf8",
    );
    assert.equal(
      source.endsWith("\n\n"),
      false,
      patchName + " 不得以空白 diff 行结束",
    );
  }
});

test("workspace 只保留根 lockfile 和根补丁真源", () => {
  assert.equal(existsSync(join(repositoryRoot, "package-lock.json")), true);
  for (const appRoot of appRoots) {
    assert.equal(
      existsSync(join(appRoot, "package-lock.json")),
      false,
      appRoot + " 不得保留独立 lockfile",
    );
    assert.equal(
      existsSync(join(appRoot, "patches")),
      false,
      appRoot + " 不得保留独立 patch 真源",
    );
  }
});

test("从任一 POS App 运行 npm 都以根 workspace 为安装锚点", () => {
  for (const appRoot of appRoots) {
    const npmPrefix = execFileSync("npm", ["prefix"], {
      cwd: appRoot,
      encoding: "utf8",
    }).trim();
    assert.equal(
      realpathSync(npmPrefix),
      realpathSync(repositoryRoot),
      `${appRoot} 必须解析到根 workspace，确保根 postinstall 应用共享补丁`,
    );
  }
});

test("根 lockfile 的第三方版本全部来自当前整合基线的双端 lock 或已审查的新增依赖", () => {
  const { baseline, reviewedDependencyAdditions = [] } = readJson(
    join(repositoryRoot, "scripts", "pos-shared", "pos-shared-migration-state.json"),
  );
  const allowedVersions = new Map();
  // 基线之后新增的第三方依赖必须逐条登记名称、精确版本、使用方与理由，不能借此放开整个 lockfile。
  for (const addition of reviewedDependencyAdditions) {
    assert.ok(addition.name && addition.version && addition.reason, "新增依赖登记缺少名称、版本或理由");
    const versions = allowedVersions.get(addition.name) ?? new Set();
    versions.add(addition.version);
    allowedVersions.set(addition.name, versions);
  }
  for (const app of ["pos-ipad", "pos-handheld"]) {
    const source = execFileSync(
      "git",
      ["show", `${baseline}:apps/${app}/package-lock.json`],
      { cwd: repositoryRoot, encoding: "utf8" },
    );
    const lock = JSON.parse(source);
    for (const [lockPath, packageEntry] of Object.entries(lock.packages ?? {})) {
      const packageName = packageNameFromLockPath(lockPath);
      if (!packageName || !packageEntry.version) continue;
      const versions = allowedVersions.get(packageName) ?? new Set();
      versions.add(packageEntry.version);
      allowedVersions.set(packageName, versions);
    }
  }
  const lock = readJson(join(repositoryRoot, "package-lock.json"));
  const unexpectedVersions = [];
  for (const [lockPath, packageEntry] of Object.entries(lock.packages ?? {})) {
    const packageName = packageNameFromLockPath(lockPath);
    if (!packageName || !packageEntry.version) continue;
    if (!allowedVersions.get(packageName)?.has(packageEntry.version)) {
      unexpectedVersions.push(`${packageName}@${packageEntry.version} (${lockPath})`);
    }
  }

  assert.deepEqual(
    unexpectedVersions.sort(),
    [],
    `根 lockfile 出现基线外版本：\n${unexpectedVersions.sort().join("\n")}`,
  );

  // 反向核对：登记项必须真实存在于根 lockfile，且由所列 workspace 直接声明，避免留下过期放行。
  for (const addition of reviewedDependencyAdditions) {
    assert.equal(
      lock.packages?.[`node_modules/${addition.name}`]?.version,
      addition.version,
      `${addition.name}@${addition.version} 已登记但不在根 lockfile 中`,
    );
    for (const workspace of addition.workspaces ?? []) {
      const manifest = readJson(join(repositoryRoot, workspace, "package.json"));
      assert.ok(
        manifest.dependencies?.[addition.name] ?? manifest.devDependencies?.[addition.name],
        `${workspace} 未声明已登记的新增依赖 ${addition.name}`,
      );
    }
  }
});

test("两个 POS App 从同一 hoisted 拓扑解析关键原生依赖", () => {
  for (const specifier of [
    "react/package.json",
    "react-native/package.json",
    "expo/package.json",
    "expo-audio/package.json",
    "expo-sqlite/package.json",
    "openapi-typescript/package.json",
  ]) {
    const resolved = appRoots.map((appRoot) => resolveFrom(specifier, appRoot));
    assert.equal(
      resolved[0],
      resolved[1],
      specifier + " 必须由两个 POS App 解析到同一真实路径",
    );
  }
});

test("两个 POS App 可直接解析 source-first domain contract", () => {
  const resolved = appRoots.map((appRoot) =>
    resolveFrom("@hb/pos-domain/core/contracts/printer", appRoot),
  );
  assert.equal(resolved[0], resolved[1]);
  assert.equal(
    resolved[0],
    realpathSync(
      join(
        repositoryRoot,
        "packages",
        "pos-domain",
        "src",
        "core",
        "contracts",
        "printer.ts",
      ),
    ),
  );
});

test("Metro 保持 Sentry 的 Expo workspace 配置，不注入手写 resolver", () => {
  for (const appRoot of appRoots) {
    const metro = readFileSync(join(appRoot, "metro.config.js"), "utf8");
    assert.match(metro, /getSentryExpoConfig\(__dirname\)/);
    assert.doesNotMatch(metro, /watchFolders|nodeModulesPaths|disableHierarchicalLookup/);
  }
});

test("Hbpos OpenAPI 快照和生成类型只保留共享包真源", () => {
  assert.equal(
    existsSync(join(repositoryRoot, "packages", "pos-api-client", "openapi", "hbpos.openapi.json")),
    true,
  );
  assert.equal(
    existsSync(
      join(
        repositoryRoot,
        "packages",
        "pos-api-client",
        "src",
        "generated",
        "hbpos",
        "schema.d.ts",
      ),
    ),
    true,
  );
  for (const appRoot of appRoots) {
    assert.equal(existsSync(join(appRoot, "openapi", "hbpos.openapi.json")), false);
    assert.equal(existsSync(join(appRoot, "src", "generated", "hbpos", "schema.d.ts")), false);
    for (const path of walkFiles(join(appRoot, "src"))) {
      if (!/\.[cm]?[jt]sx?$/.test(path)) continue;
      assert.doesNotMatch(
        readFileSync(path, "utf8"),
        /(?:@\/|\.\.\/)generated\/hbpos\/schema/,
        `${path} 仍引用 App-local OpenAPI 类型`,
      );
    }
  }
});

function walkFiles(root, result = []) {
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) walkFiles(path, result);
    else result.push(path);
  }
  return result;
}

function packageNameFromLockPath(lockPath) {
  const marker = "node_modules/";
  const index = lockPath.lastIndexOf(marker);
  return index < 0 ? null : lockPath.slice(index + marker.length);
}
