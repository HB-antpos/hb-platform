import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { buildLaneReport, buildMetricBatch } from "./build-metric-batch.mjs";
import {
  buildLaneProcessEnvironment,
  finishLane,
  getLaneCommands,
  runLaneCommands,
  startLane,
} from "./quality-lane.mjs";
import {
  QUALITY_LANES,
  resolveLanesForEvent,
  selectLanesForPaths,
} from "./select-quality-lanes.mjs";

const COMMIT_SHA = "b".repeat(40);

test("路径选择只启用受影响 lane，共享基线文件启用全部 lane", () => {
  assert.deepEqual(selectLanesForPaths(["services/backend/BlazorApp.Api/Program.cs"]), [
    "backend",
  ]);
  assert.deepEqual(selectLanesForPaths(["apps/web/src/main.tsx"]), ["web"]);
  assert.deepEqual(selectLanesForPaths(["web-bundle-budget.json"]), ["web"]);
  assert.deepEqual(selectLanesForPaths(["apps/pos-ipad/src/app.ts"]), ["pos-ipad"]);
  assert.deepEqual(selectLanesForPaths(["apps/pos-handheld/src/app.ts"]), [
    "pos-handheld",
  ]);
  for (const path of [
    "packages/pos-domain/src/core/contracts/cart.ts",
    "scripts/pos-shared/check-pos-package-boundaries.test.mjs",
    "patches/react-native+0.81.5.patch",
    "package.json",
    "package-lock.json",
    "eslint.config.mjs",
    "tsconfig.pos-packages.json",
  ]) {
    assert.deepEqual(selectLanesForPaths([path]), ["pos-ipad", "pos-handheld"], path);
  }
  assert.deepEqual(
    selectLanesForPaths(["scripts/performance/report-metric-batch.mjs"]),
    QUALITY_LANES,
  );
});

test("nightly/手动运行全部 lane，PR 使用显式 base/head diff", () => {
  assert.deepEqual(resolveLanesForEvent({ eventName: "schedule" }), QUALITY_LANES);
  assert.deepEqual(resolveLanesForEvent({ eventName: "workflow_dispatch" }), QUALITY_LANES);

  const calls = [];
  const selected = resolveLanesForEvent({
    eventName: "pull_request",
    baseSha: "1".repeat(40),
    headSha: "2".repeat(40),
    diffProvider: (baseSha, headSha, options) => {
      calls.push([baseSha, headSha, options]);
      return ["apps/web/src/main.tsx", "apps/pos-handheld/src/app.ts"];
    },
  });
  assert.deepEqual(calls, [
    ["1".repeat(40), "2".repeat(40), { mergeBase: true, includeDeleted: true }],
  ]);
  assert.deepEqual(selected, ["web", "pos-handheld"]);
});

test("lane 命令使用现有 build/typecheck/test 且不通过 shell 拼接", () => {
  const backend = getLaneCommands("backend");
  assert.deepEqual(
    backend.map((item) => item.command),
    ["dotnet", "dotnet", "dotnet"],
  );
  const backendTestProject =
    "services/backend/BlazorApp.Api.Tests/BlazorApp.Api.Tests.csproj";
  assert.deepEqual(
    backend.map((item) => item.args[1]),
    [backendTestProject, backendTestProject, backendTestProject],
  );
  assert.ok(backend[2].args.includes("--no-restore"));
  assert.ok(backend[2].args.includes("--no-build"));
  assert.ok(
    !backend[2].args.includes("-p:IsTestProject=true"),
    "测试项目已由 Microsoft.NET.Test.Sdk 标识，不应覆写 IsTestProject",
  );
  assert.deepEqual(backend[2].args.slice(-2), [
    "--filter",
    "Category!=SQL&Category!=Performance&Category!=LiveE2e",
  ]);

  const web = getLaneCommands("web");
  assert.deepEqual(
    web.map((item) => [item.command, ...item.args]),
    [
      ["npm", "ci"],
      ["npm", "run", "build", "--", "--manifest"],
      ["npm", "test"],
    ],
  );
  assert.deepEqual(web[1].environment, {
    VITE_CENTER_LOG_KEY: "quality-baseline-ci-placeholder",
    VITE_CENTER_LOG_PROJECT: "hbweb_rv",
    VITE_CENTER_LOG_ENVIRONMENT: "Production",
    VITE_CENTER_LOG_SERVICE_NAME: "hbweb_rv-web",
  });
  assert.deepEqual(buildLaneProcessEnvironment(web[1], { PATH: "/usr/bin" }), {
    PATH: "/usr/bin",
    CI: "true",
    ...web[1].environment,
  });

  for (const lane of ["pos-ipad", "pos-handheld"]) {
    const laneCommands = getLaneCommands(lane);
    const commands = laneCommands.map((item) => [item.command, ...item.args]);
    assert.deepEqual(commands, [
      ["npm", "ci"],
      ["npm", "run", "typecheck", `--workspace=@hb/${lane}`],
      ["npm", "run", "verify:metro-bundle", `--workspace=@hb/${lane}`],
      ["npm", "run", "test:ci", `--workspace=@hb/${lane}`],
    ]);
    assert.deepEqual(laneCommands.map((item) => item.cwd), [".", ".", ".", "."]);
    assert.deepEqual(laneCommands[3].environment, {
      TZ: "Australia/Brisbane",
    });
    assert.deepEqual(
      buildLaneProcessEnvironment(laneCommands[3], { PATH: "/usr/bin" }),
      {
        PATH: "/usr/bin",
        TZ: "Australia/Brisbane",
        CI: "true",
      },
    );
  }
});

test("lane 执行首个失败后停止并返回失败命令", async () => {
  const calls = [];
  await assert.rejects(
    runLaneCommands("web", {
      execute: async (spec) => {
        calls.push(spec.args.join(" "));
        return calls.length === 2 ? 7 : 0;
      },
    }),
    /build|退出码 7/i,
  );
  assert.deepEqual(calls, ["ci", "run build -- --manifest"]);
});

test("lane 起止、用时和结论写入独立 JSON", () => {
  const directory = mkdtempSync(join(tmpdir(), "quality-lane-test-"));
  const statePath = join(directory, "backend.state.json");
  const resultPath = join(directory, "backend.json");
  try {
    startLane({
      lane: "backend",
      statePath,
      now: new Date("2026-08-25T04:00:00.000Z"),
    });
    const result = finishLane({
      lane: "backend",
      statePath,
      resultPath,
      jobStatus: "success",
      verificationOutcome: "success",
      now: new Date("2026-08-25T04:00:02.500Z"),
    });

    assert.deepEqual(JSON.parse(readFileSync(resultPath, "utf8")), result);
    assert.equal(result.durationMs, 2500);
    assert.equal(result.conclusion, "accepted");
    assert.equal(result.startedAtUtc, "2026-08-25T04:00:00.000Z");
    assert.equal(result.finishedAtUtc, "2026-08-25T04:00:02.500Z");
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("聚合器保留 lane 计时并将缺失结果标为失败", () => {
  const batch = buildMetricBatch({
    laneResults: [
      {
        schemaVersion: "QualityLaneResultV1",
        lane: "backend",
        startedAtUtc: "2026-08-25T05:00:00.000Z",
        finishedAtUtc: "2026-08-25T05:00:03.000Z",
        durationMs: 3000,
        conclusion: "accepted",
      },
    ],
    expectedLanes: ["backend"],
    context: {
      repository: "hotbargain/hb-platform",
      eventName: "push",
      ref: "refs/heads/main",
      commitSha: COMMIT_SHA,
      workflow: "quality-baseline",
      runId: "9988",
      runAttempt: 1,
    },
    now: new Date("2026-08-25T05:00:04.000Z"),
  });

  assert.deepEqual(Object.keys(batch).sort(), ["events", "schemaVersion"]);
  assert.equal(batch.schemaVersion, 1);
  assert.equal(batch.events.length, 1);
  assert.equal(batch.events[0].metric, "ci.run.duration");
  assert.equal(batch.events[0].value, 3000);
  assert.equal(batch.events[0].dimensions.lane, "backend");
  assert.equal(batch.events[0].dimensions.outcome, "accepted");
  assert.equal(batch.events[0].dimensions.environment, "Production");

  const laneReport = buildLaneReport({
    laneResults: [
      {
        schemaVersion: "QualityLaneResultV1",
        lane: "backend",
        startedAtUtc: "2026-08-25T05:00:00.000Z",
        finishedAtUtc: "2026-08-25T05:00:03.000Z",
        durationMs: 3000,
        conclusion: "accepted",
      },
    ],
    expectedLanes: ["backend", "web"],
  });
  assert.equal(laneReport[1].lane, "web");
  assert.equal(laneReport[1].timingAvailable, false);
  assert.equal(laneReport[1].errorCode, "missing_lane_result");
});

function webBundleReport() {
  return {
    schemaVersion: "WebBundleReportV1",
    generatedAtUtc: "2026-08-25T05:00:02.500Z",
    manifestPath: ".vite/manifest.json",
    indexPath: "index.html",
    measurements: {
      firstScreenRawBytes: 350,
      firstScreenGzipBytes: 170,
      largestInitialChunkFile: "assets/vendor.js",
      largestInitialChunkRawBytes: 200,
      largestInitialChunkGzipBytes: 80,
    },
    initialAssets: [
      { file: "assets/main.css", type: "css", rawBytes: 50, gzipBytes: 30 },
      { file: "assets/main.js", type: "js", rawBytes: 100, gzipBytes: 60 },
      { file: "assets/vendor.js", type: "js", rawBytes: 200, gzipBytes: 80 },
    ],
    routeDynamicChunks: [
      {
        manifestKey: "src/routes/orders.tsx",
        file: "assets/orders.js",
        rawBytes: 90,
        gzipBytes: 55,
        cssAssets: [
          { file: "assets/orders.css", type: "css", rawBytes: 20, gzipBytes: 15 },
        ],
      },
    ],
  };
}

test("Web bundle 两项 gzip 指标与 lane timing 合并，并保留动态 chunk report", () => {
  const bundleReport = webBundleReport();
  const batch = buildMetricBatch({
    laneResults: [
      {
        schemaVersion: "QualityLaneResultV1",
        lane: "web",
        startedAtUtc: "2026-08-25T05:00:00.000Z",
        finishedAtUtc: "2026-08-25T05:00:03.000Z",
        durationMs: 3000,
        conclusion: "accepted",
      },
    ],
    expectedLanes: ["web"],
    webBundleReport: bundleReport,
    context: {
      repository: "hotbargain/hb-platform",
      eventName: "push",
      ref: "refs/heads/main",
      commitSha: COMMIT_SHA,
      workflow: "quality-baseline",
      runId: "9989",
      runAttempt: 1,
    },
  });

  assert.deepEqual(
    batch.events.map((event) => [event.metric, event.value, event.unit]),
    [
      ["ci.run.duration", 3000, "ms"],
      ["web.first_screen.bytes", bundleReport.measurements.firstScreenGzipBytes, "bytes"],
      [
        "web.largest_initial_chunk.bytes",
        bundleReport.measurements.largestInitialChunkGzipBytes,
        "bytes",
      ],
    ],
  );
  for (const event of batch.events) {
    assert.equal(event.dimensions.environment, "Production");
    assert.equal(event.dimensions.lane, "web");
    assert.equal(event.dimensions.component, "web");
    assert.equal(event.dimensions.source, "github-actions");
    assert.equal(event.dimensions.outcome, "accepted");
  }
  assert.equal(bundleReport.routeDynamicChunks[0].file, "assets/orders.js");
});

test("Web lane 缺 bundle report 时明确失败，不以 0 bytes 上报", () => {
  assert.throws(
    () =>
      buildMetricBatch({
        laneResults: [
          {
            schemaVersion: "QualityLaneResultV1",
            lane: "web",
            startedAtUtc: "2026-08-25T05:00:00.000Z",
            finishedAtUtc: "2026-08-25T05:00:03.000Z",
            durationMs: 3000,
            conclusion: "accepted",
          },
        ],
        expectedLanes: ["web"],
        context: {
          repository: "hotbargain/hb-platform",
          eventName: "push",
          ref: "refs/heads/main",
          commitSha: COMMIT_SHA,
          workflow: "quality-baseline",
          runId: "9990",
          runAttempt: 1,
        },
      }),
    /Web bundle|缺失|report/i,
  );
});

test("Web 构建失败且没有 bundle 时仍上报失败用时，绝不伪造 bundle 指标", () => {
  const batch = buildMetricBatch({
    laneResults: [
      {
        schemaVersion: "QualityLaneResultV1",
        lane: "web",
        startedAtUtc: "2026-08-25T05:00:00.000Z",
        finishedAtUtc: "2026-08-25T05:00:03.000Z",
        durationMs: 3000,
        conclusion: "failed",
        errorCode: "lane_verification_failed",
      },
    ],
    expectedLanes: ["web"],
    context: {
      repository: "hotbargain/hb-platform",
      eventName: "push",
      ref: "refs/heads/main",
      commitSha: COMMIT_SHA,
      workflow: "quality-baseline",
      runId: "web-failed-without-bundle",
      runAttempt: 1,
    },
  });

  assert.deepEqual(
    batch.events.map((event) => [event.metric, event.value, event.dimensions.outcome]),
    [["ci.run.duration", 3000, "failed"]],
  );
  assert.equal(batch.events.some((event) => event.value === 0), false);
});

test("事件环境按正式与 PR 口径映射，禁止统一写成 CI", () => {
  const cases = [
    ["push", "refs/heads/main", "Production"],
    ["schedule", "refs/heads/main", "Production"],
    ["workflow_dispatch", "refs/heads/main", "Production"],
    ["pull_request", "refs/pull/42/merge", "PullRequest"],
    ["merge_group", "refs/heads/gh-readonly-queue/main/pr-42-0123456789abcdef0123456789abcdef01234567", "PullRequest"],
  ];
  for (const [eventName, ref, expectedEnvironment] of cases) {
    const batch = buildMetricBatch({
      laneResults: [
        {
          schemaVersion: "QualityLaneResultV1",
          lane: "web",
          startedAtUtc: "2026-08-25T05:00:00.000Z",
          finishedAtUtc: "2026-08-25T05:00:03.000Z",
          durationMs: 3000,
          conclusion: "accepted",
        },
      ],
      expectedLanes: ["web"],
      webBundleReport: webBundleReport(),
      context: {
        repository: "hotbargain/hb-platform",
        eventName,
        ref,
        commitSha: COMMIT_SHA,
        workflow: "quality-baseline",
        runId: `environment-${eventName}`,
        runAttempt: 1,
      },
    });
    assert.equal(batch.events.length, 3);
    for (const event of batch.events) {
      assert.equal(event.dimensions.environment, expectedEnvironment);
      assert.notEqual(event.dimensions.environment, "CI");
    }
  }
});
