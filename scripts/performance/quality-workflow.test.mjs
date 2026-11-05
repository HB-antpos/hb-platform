import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const workflowPath = resolve(repositoryRoot, ".github/workflows/quality-baseline.yml");
const budgetPath = resolve(repositoryRoot, "quality-baseline-budget.json");
const bundleBudgetPath = resolve(repositoryRoot, "web-bundle-budget.json");

test("quality-baseline workflow 覆盖 main/nightly，PR 由现有组件任务采集质量结果", () => {
  assert.ok(existsSync(workflowPath), "必须新增 quality-baseline workflow");
  const workflow = readFileSync(workflowPath, "utf8");
  assert.deepEqual(
    [...workflow.matchAll(/node-version:\s*(\d+)/g)].map((match) => match[1]),
    ["24", "24"],
    "性能脚本与客户端 lane 必须统一使用支持 node:sqlite 的 Node 24",
  );

  assert.doesNotMatch(workflow, /^  pull_request:/m);
  assert.match(workflow, /push:[\s\S]*branches:\s*\[main\]/);
  assert.match(workflow, /schedule:[\s\S]*cron:/);
  for (const path of [
    "services/backend/**",
    "apps/web/**",
    "apps/pos-ipad/**",
    "apps/pos-handheld/**",
    "packages/pos-*/**",
    "scripts/pos-shared/**",
    "patches/**",
    "package.json",
    "package-lock.json",
    "eslint.config.mjs",
    "tsconfig.pos-packages.json",
  ]) {
    assert.ok(workflow.includes(path), `workflow 缺少路径 ${path}`);
  }
  for (const lane of ["backend", "web", "pos-ipad", "pos-handheld"]) {
    assert.ok(workflow.includes(lane), `workflow 缺少 lane ${lane}`);
  }
  assert.doesNotMatch(workflow, /apps\/pos-(?:ipad|handheld)\/package-lock\.json/);
  assert.match(
    workflow,
    /startsWith\(matrix\.lane, 'pos-'\)[^\n]*'package-lock\.json'/,
    "POS 质量 lane 必须以根 workspace lock 作为 npm cache 真源",
  );
  assert.match(
    workflow,
    /node --test scripts\/performance\/\*\.test\.mjs/,
    "workflow 必须先运行性能基线脚本自身的测试",
  );
  assert.match(workflow, /if:\s*\$?\{?\{?\s*always\(\)/);
  assert.match(workflow, /report-metric-batch\.mjs[\s\S]*--optional/);
  assert.match(workflow, /QUALITY_BASELINE_SERVICE_TOKEN:\s*\$\{\{\s*secrets\./);
  assert.match(workflow, /QUALITY_BASELINE_SERVICE_URL:\s*\$\{\{\s*secrets\./);
  const reporterStep = workflow.slice(
    workflow.indexOf("- name: 可选上报 automation batch"),
    workflow.indexOf("- name: 保存质量基线 artifact"),
  );
  assert.match(
    reporterStep,
    /if:\s*\$\{\{\s*always\(\)\s*&&\s*github\.event_name\s*!=\s*'pull_request'\s*\}\}/,
    "PR 运行不得向可修改的工作流步骤注入长期 service token",
  );
  assert.doesNotMatch(workflow, /report-metric-batch\.mjs[^\n]*--token/);
  assert.match(workflow, /workflow_call:/);
  assert.match(workflow, /report-release-event\.mjs/);
  const concurrency = workflow.slice(
    workflow.indexOf("concurrency:"),
    workflow.indexOf("jobs:"),
  );
  assert.match(
    concurrency,
    /github\.event_name\s*==\s*'workflow_call'[\s\S]*github\.run_id[\s\S]*github\.run_attempt/,
    "workflow_call 必须使用每次运行唯一的并发组，不能只按 github.ref 分组",
  );
  assert.match(
    concurrency,
    /cancel-in-progress:\s*\$\{\{\s*github\.event_name\s*!=\s*'workflow_call'\s*\}\}/,
    "workflow_call 不得取消并发或相邻的发布验收，PR/main 保留原有取消策略",
  );
  const planJob = workflow.slice(workflow.indexOf("  plan:"), workflow.indexOf("  quality:"));
  const qualityJob = workflow.slice(workflow.indexOf("  quality:"), workflow.indexOf("  report:"));
  const reportJob = workflow.slice(
    workflow.indexOf("  report:"),
    workflow.indexOf("  release_acceptance:"),
  );
  for (const [name, job] of [["plan", planJob], ["quality", qualityJob], ["report", reportJob]]) {
    assert.match(
      job,
      /github\.event_name\s*!=\s*'workflow_call'/,
      `${name} job 必须在 workflow_call 时完全跳过`,
    );
  }
  const releaseStep = workflow.slice(
    workflow.indexOf("      - name: 记录部署或回滚验收"),
  );
  const releaseRun = releaseStep.slice(releaseStep.indexOf("        run:"));
  assert.doesNotMatch(releaseRun, /\$\{\{\s*inputs\./);
  assert.match(releaseStep, /RELEASE_ACTION:\s*\$\{\{\s*inputs\.action\s*\}\}/);
  assert.match(releaseStep, /--action\s+"\$RELEASE_ACTION"/);
  assert.match(releaseStep, /--conclusion\s+"\$RELEASE_CONCLUSION"/);
  assert.match(
    workflow,
    /actions\/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9/,
  );
  assert.match(workflow, /quality-web-bundle-/);
  assert.match(workflow, /collect-web-bundle\.mjs/);
  assert.match(workflow, /verify:bundle/);
  assert.match(workflow, /web-bundle-budget\.json/);
  assert.match(workflow, /web-bundle\.json/);
  assert.match(workflow, /--web-bundle-file/);
  assert.match(
    workflow,
    /steps\.verify\.outcome == 'success'/,
    "只有 Web 验证成功才允许采集或要求 bundle",
  );
  assert.match(workflow, /--selected-lanes/);
  assert.ok(
    workflow.indexOf("quality-lane.mjs run") < workflow.indexOf("collect-web-bundle.mjs") &&
      workflow.indexOf("collect-web-bundle.mjs") < workflow.indexOf("verify:bundle") &&
      workflow.indexOf("verify:bundle") < workflow.indexOf("quality-lane.mjs finish"),
    "Web bundle 必须在现有 build/test 后采集并执行硬门禁，再记录 lane 结束",
  );
});

test("初始预算只处于 observing，且不固化本机测量值", () => {
  assert.ok(existsSync(budgetPath), "必须新增根 quality-baseline-budget.json");
  const budget = JSON.parse(readFileSync(budgetPath, "utf8"));
  assert.equal(budget.schemaVersion, "QualityBaselineBudgetV1");
  assert.equal(budget.mode, "observing");
  assert.deepEqual(budget.metrics, {});
});

test("确定性 Web bundle 预算独立版本化且不修改 observing P95 预算", () => {
  assert.ok(existsSync(bundleBudgetPath), "必须新增根 web-bundle-budget.json");
  const budget = JSON.parse(readFileSync(bundleBudgetPath, "utf8"));
  assert.equal(budget.schemaVersion, "WebBundleBudgetV1");
  assert.deepEqual(budget.mainEntry, {
    manifestKey: "index.html",
    maxRawBytes: 700 * 1024,
    maxGzipBytes: 250 * 1024,
  });
  assert.equal(budget.firstScreen.maxGzipBytes, 450 * 1024);
  assert.equal(budget.largestInitialJs.maxGzipBytes, 250 * 1024);
  assert.equal(budget.anyJsChunk.maxRawBytes, 1000 * 1024);
  assert.deepEqual(
    Object.fromEntries(
      budget.asyncChunks.map((chunk) => [chunk.id, [chunk.maxRawBytes, chunk.maxGzipBytes]]),
    ),
    {
      excel: [1000 * 1024, 300 * 1024],
      pdf: [650 * 1024, 200 * 1024],
    },
  );
  assert.deepEqual(
    budget.requiredDynamicEntries.map((entry) => entry.manifestKey),
    [
      "src/pages/Dashboard/index.tsx",
      "src/pages/ShopHome/index.tsx",
      "src/pages/Warehouse/Products/index.tsx",
      "src/pages/Warehouse/StoreOrders/Invoice.tsx",
    ],
  );
});
