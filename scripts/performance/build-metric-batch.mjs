import { createHash } from "node:crypto";
import {
  appendFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  renameSync,
  writeFileSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { validateWebBundleReport } from "./collect-web-bundle.mjs";
import { validateMetricBatchV1 } from "./lib/metric-batch.mjs";
import {
  ValidationError,
  assertCanonicalUtcTimestamp,
  assertCommitSha,
  assertEnum,
  assertExactKeys,
  assertFiniteNumber,
  assertSafeString,
} from "./lib/validation.mjs";

const QUALITY_LANES = ["backend", "web", "pos-ipad", "pos-handheld"];
const QUALITY_EVENTS = ["pull_request", "merge_group", "push", "schedule", "workflow_dispatch"];
const MAX_RESULT_BYTES = 64 * 1024;
const MAX_WEB_BUNDLE_REPORT_BYTES = 8 * 1024 * 1024;

function validateLaneResult(result, index) {
  const path = `laneResults[${index}]`;
  assertExactKeys(
    result,
    {
      required: [
        "schemaVersion",
        "lane",
        "startedAtUtc",
        "finishedAtUtc",
        "durationMs",
        "conclusion",
      ],
      optional: ["errorCode"],
    },
    path,
  );
  if (result.schemaVersion !== "QualityLaneResultV1") {
    throw new ValidationError(`${path}.schemaVersion 必须为 QualityLaneResultV1`);
  }
  assertEnum(result.lane, QUALITY_LANES, `${path}.lane`);
  const startedAt = assertCanonicalUtcTimestamp(result.startedAtUtc, `${path}.startedAtUtc`);
  const finishedAt = assertCanonicalUtcTimestamp(result.finishedAtUtc, `${path}.finishedAtUtc`);
  assertFiniteNumber(result.durationMs, `${path}.durationMs`, {
    min: 0,
    max: 24 * 60 * 60 * 1000,
    integer: true,
  });
  if (finishedAt.getTime() - startedAt.getTime() !== result.durationMs) {
    throw new ValidationError(`${path}.durationMs 与起止时间不一致`);
  }
  assertEnum(result.conclusion, ["accepted", "failed", "cancelled"], `${path}.conclusion`);
  if (result.conclusion === "accepted") {
    if (Object.hasOwn(result, "errorCode")) {
      throw new ValidationError(`${path}.errorCode 不得出现在 accepted lane`);
    }
  } else {
    if (!Object.hasOwn(result, "errorCode")) {
      throw new ValidationError(`${path}.errorCode 在失败或取消时为必填字段`);
    }
    assertSafeString(result.errorCode, `${path}.errorCode`, {
      maxLength: 80,
      pattern: /^[a-z][a-z0-9_]*$/u,
    });
  }
  return result;
}

function validateBuildContext(context) {
  assertExactKeys(
    context,
    {
      required: [
        "repository",
        "eventName",
        "ref",
        "commitSha",
        "workflow",
        "runId",
        "runAttempt",
      ],
    },
    "context",
  );
  assertSafeString(context.repository, "context.repository", {
    maxLength: 120,
    pattern: /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u,
  });
  assertEnum(context.eventName, QUALITY_EVENTS, "context.eventName");
  assertSafeString(context.ref, "context.ref", { maxLength: 255 });
  assertCommitSha(context.commitSha, "context.commitSha");
  assertSafeString(context.workflow, "context.workflow", { maxLength: 120 });
  assertSafeString(context.runId, "context.runId", {
    maxLength: 64,
    pattern: /^[A-Za-z0-9][A-Za-z0-9._:-]*$/u,
  });
  assertFiniteNumber(context.runAttempt, "context.runAttempt", {
    min: 1,
    max: 1_000_000,
    integer: true,
  });
}

export function resolveMetricEnvironment(context) {
  validateBuildContext(context);
  // 合并队列运行的是尚未进入 main 的组合提交，与 PR 同口径，不能记为正式基线。
  if (context.eventName === "pull_request" || context.eventName === "merge_group") return "PullRequest";
  if (context.eventName === "push" && context.ref !== "refs/heads/main") {
    throw new ValidationError("push 质量基线只允许 refs/heads/main 使用 Production 环境");
  }
  return "Production";
}

function validateExpectedLanes(expectedLanes) {
  if (!Array.isArray(expectedLanes) || expectedLanes.length < 1) {
    throw new ValidationError("expectedLanes 必须至少包含一个 lane");
  }
  const expectedSet = new Set();
  for (const lane of expectedLanes) {
    assertEnum(lane, QUALITY_LANES, "expected lane");
    if (expectedSet.has(lane)) throw new ValidationError(`expectedLanes 包含重复 lane ${lane}`);
    expectedSet.add(lane);
  }
  return expectedSet;
}

export function buildLaneReport({ laneResults, expectedLanes }) {
  if (!Array.isArray(laneResults)) throw new ValidationError("laneResults 必须是数组");
  const expectedSet = validateExpectedLanes(expectedLanes);
  const resultsByLane = new Map();
  laneResults.forEach((result, index) => {
    validateLaneResult(result, index);
    if (!expectedSet.has(result.lane)) {
      throw new ValidationError(`收到未选择 lane ${result.lane} 的结果`);
    }
    if (resultsByLane.has(result.lane)) {
      throw new ValidationError(`laneResults 包含重复 lane ${result.lane}`);
    }
    resultsByLane.set(result.lane, result);
  });

  return expectedLanes.map((lane) => {
    const result = resultsByLane.get(lane);
    if (result) return { ...result, timingAvailable: true };
    return {
      schemaVersion: "QualityLaneResultV1",
      lane,
      startedAtUtc: null,
      finishedAtUtc: null,
      durationMs: null,
      conclusion: "failed",
      timingAvailable: false,
      errorCode: "missing_lane_result",
    };
  });
}

function deterministicEventId(seed) {
  const bytes = createHash("sha256").update(seed, "utf8").digest().subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x50;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString("hex");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function buildMetricBatch({
  laneResults,
  expectedLanes,
  context,
  webBundleReport = null,
}) {
  validateBuildContext(context);
  const laneReport = buildLaneReport({ laneResults, expectedLanes });
  const measuredLanes = laneReport.filter((lane) => lane.timingAvailable);
  if (measuredLanes.length === 0) {
    throw new ValidationError("没有可上报的 lane 计时结果");
  }

  const environment = resolveMetricEnvironment(context);
  const dimensionsForLane = (lane) => ({
    environment,
    lane: lane.lane,
    component: lane.lane,
    outcome: lane.conclusion,
    source: "github-actions",
    project: context.repository,
    action: context.eventName,
  });
  const events = measuredLanes.map((lane) => ({
    eventId: deterministicEventId(
      `${context.repository}:${context.runId}:${context.runAttempt}:${context.commitSha}:${lane.lane}`,
    ),
    metric: "ci.run.duration",
    observedAt: lane.finishedAtUtc,
    value: lane.durationMs,
    unit: "ms",
    dimensions: dimensionsForLane(lane),
  }));

  const expectsWeb = expectedLanes.includes("web");
  if (expectsWeb) {
    const webLane = laneReport.find((lane) => lane.lane === "web");
    if (!webLane?.timingAvailable) {
      throw new ValidationError("Web lane 计时结果缺失，不能合并 Web bundle 指标");
    }
    // 构建失败也要留下真实 ci.run.duration；只有成功 Web lane 才要求和合并 bundle。
    if (webLane.conclusion !== "accepted") {
      if (webBundleReport !== null && webBundleReport !== undefined) {
        throw new ValidationError("失败或取消的 Web lane 不得合并 Web bundle 指标");
      }
      return validateMetricBatchV1({ schemaVersion: 1, events });
    }
    if (webBundleReport === null || webBundleReport === undefined) {
      throw new ValidationError("Web lane 缺失 Web bundle report，禁止以 0 bytes 上报");
    }
    validateWebBundleReport(webBundleReport);
    const generatedAt = new Date(webBundleReport.generatedAtUtc).getTime();
    const startedAt = new Date(webLane.startedAtUtc).getTime();
    const finishedAt = new Date(webLane.finishedAtUtc).getTime();
    if (generatedAt < startedAt || generatedAt > finishedAt) {
      throw new ValidationError("Web bundle report 生成时间不在 Web lane 起止范围内");
    }
    const webMetrics = [
      ["web.first_screen.bytes", webBundleReport.measurements.firstScreenGzipBytes],
      [
        "web.largest_initial_chunk.bytes",
        webBundleReport.measurements.largestInitialChunkGzipBytes,
      ],
    ];
    for (const [metric, value] of webMetrics) {
      events.push({
        eventId: deterministicEventId(
          `${context.repository}:${context.runId}:${context.runAttempt}:${context.commitSha}:web:${metric}:gzip`,
        ),
        metric,
        observedAt: webLane.finishedAtUtc,
        value,
        unit: "bytes",
        dimensions: dimensionsForLane(webLane),
      });
    }
  } else if (webBundleReport !== null && webBundleReport !== undefined) {
    throw new ValidationError("未选择 Web lane 时不得合并 Web bundle report");
  }
  return validateMetricBatchV1({ schemaVersion: 1, events });
}

function readLaneResults(resultsDirectory) {
  if (!existsSync(resultsDirectory)) return [];
  const results = [];
  for (const fileName of readdirSync(resultsDirectory).sort()) {
    if (!fileName.endsWith(".json")) continue;
    const filePath = join(resultsDirectory, fileName);
    const stat = lstatSync(filePath);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > MAX_RESULT_BYTES) {
      throw new ValidationError(`lane result 文件无效：${fileName}`);
    }
    try {
      results.push(JSON.parse(readFileSync(filePath, "utf8")));
    } catch {
      throw new ValidationError(`lane result 不是有效 JSON：${fileName}`);
    }
  }
  return results;
}

function readWebBundleReport(filePath) {
  let stat;
  try {
    stat = lstatSync(filePath);
  } catch (error) {
    if (error?.code === "ENOENT") {
      return null;
    }
    throw error;
  }
  if (
    !stat.isFile() ||
    stat.isSymbolicLink() ||
    stat.size > MAX_WEB_BUNDLE_REPORT_BYTES
  ) {
    throw new ValidationError("Web bundle report 必须是受限大小的普通文件，不能是符号链接");
  }
  let report;
  try {
    report = JSON.parse(readFileSync(filePath, "utf8"));
  } catch {
    throw new ValidationError("Web bundle report 不是有效 JSON");
  }
  return validateWebBundleReport(report);
}

function writeJsonAtomic(filePath, value) {
  mkdirSync(dirname(filePath), { recursive: true });
  const temporaryPath = `${filePath}.${process.pid}.tmp`;
  writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, {
    encoding: "utf8",
    mode: 0o600,
  });
  renameSync(temporaryPath, filePath);
}

function appendSummary(summaryPath, laneReport, context, webBundleReport) {
  if (!summaryPath) return;
  const rows = laneReport
    .map(
      (lane) =>
        `| ${lane.lane} | ${lane.startedAtUtc ?? "—"} | ${lane.finishedAtUtc ?? "—"} | ${lane.durationMs ?? "—"} | ${lane.conclusion} |`,
    )
    .join("\n");
  const conclusion = laneReport.every((lane) => lane.conclusion === "accepted")
    ? "accepted"
    : "failed";
  const environment = resolveMetricEnvironment(context);
  const webSummary = webBundleReport
    ? `\n- Web 首屏 JS+CSS：raw \`${webBundleReport.measurements.firstScreenRawBytes} bytes\` / gzip \`${webBundleReport.measurements.firstScreenGzipBytes} bytes\`\n- Web 最大初始 chunk gzip：\`${webBundleReport.measurements.largestInitialChunkGzipBytes} bytes\`（\`${webBundleReport.measurements.largestInitialChunkFile}\`）\n- Web 路由动态 chunk：\`${webBundleReport.routeDynamicChunks.length}\` 个（详见 artifact）`
    : "";
  appendFileSync(
    summaryPath,
    `\n## Quality baseline\n\n- Run：\`${context.runId}.${context.runAttempt}\`\n- 环境：\`${environment}\`\n- 总结论：\`${conclusion}\`${webSummary}\n\n| Lane | 开始（UTC） | 结束（UTC） | 用时（ms） | 结论 |\n| --- | --- | --- | ---: | --- |\n${rows}\n`,
    "utf8",
  );
}

function parseArgs(argv) {
  const options = {};
  const valueOptions = new Map([
    ["--results-dir", "resultsDirectory"],
    ["--web-bundle-file", "webBundleFile"],
    ["--output", "output"],
    ["--summary", "summary"],
  ]);
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === "--help") {
      options.help = true;
    } else if (valueOptions.has(argument)) {
      const value = argv[++index];
      if (!value || value.startsWith("--")) throw new ValidationError(`${argument} 缺少值`);
      options[valueOptions.get(argument)] = value;
    } else {
      throw new ValidationError(`未知参数 ${argument}`);
    }
  }
  if (!options.help && (!options.resultsDirectory || !options.output)) {
    throw new ValidationError("必须提供 --results-dir 和 --output");
  }
  return options;
}

function parseExpectedLanes(value) {
  try {
    const parsed = JSON.parse(value);
    if (!Array.isArray(parsed)) throw new Error("not array");
    return parsed;
  } catch {
    throw new ValidationError("QUALITY_EXPECTED_LANES 必须是 JSON 数组");
  }
}

function parseRunAttempt(value) {
  if (!/^\d+$/u.test(value ?? "")) {
    throw new ValidationError("GITHUB_RUN_ATTEMPT 必须是正整数");
  }
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < 1) {
    throw new ValidationError("GITHUB_RUN_ATTEMPT 必须是正整数");
  }
  return parsed;
}

function main(argv = process.argv.slice(2), env = process.env) {
  const options = parseArgs(argv);
  if (options.help) {
    console.log(
      "用法: node scripts/performance/build-metric-batch.mjs --results-dir <dir> --web-bundle-file <report.json> --output <batch.json> [--summary <markdown>]",
    );
    return;
  }
  const expectedLanes = parseExpectedLanes(env.QUALITY_EXPECTED_LANES);
  const context = {
    repository: env.GITHUB_REPOSITORY,
    eventName: env.GITHUB_EVENT_NAME,
    ref: env.GITHUB_REF,
    commitSha: env.GITHUB_SHA,
    workflow: env.GITHUB_WORKFLOW,
    runId: env.GITHUB_RUN_ID,
    runAttempt: parseRunAttempt(env.GITHUB_RUN_ATTEMPT),
  };
  const laneResults = readLaneResults(resolve(options.resultsDirectory));
  const laneReport = buildLaneReport({ laneResults, expectedLanes });
  if (expectedLanes.includes("web") && !options.webBundleFile) {
    throw new ValidationError("选择 Web lane 时必须提供 --web-bundle-file");
  }
  const webBundleReport = expectedLanes.includes("web")
    ? readWebBundleReport(resolve(options.webBundleFile))
    : null;
  const batch = buildMetricBatch({
    laneResults,
    expectedLanes,
    context,
    webBundleReport,
  });
  writeJsonAtomic(resolve(options.output), batch);
  appendSummary(
    options.summary ? resolve(options.summary) : null,
    laneReport,
    context,
    webBundleReport,
  );
  console.log(`MetricBatchV1 已生成：${batch.events.length} 个质量/性能事件`);
  // artifact 的通配下载可能只拿到部分 lane；保留诊断报告，同时明确阻止门禁假绿。
  const rejectedLanes = laneReport.filter((lane) => lane.conclusion !== "accepted");
  if (rejectedLanes.length > 0) {
    console.error(`质量结果不完整或失败：${rejectedLanes.map((lane) => `${lane.lane}=${lane.errorCode}`).join(", ")}`);
    process.exitCode = 1;
  }
}

const isMain =
  process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href;
if (isMain) {
  try {
    main();
  } catch (error) {
    console.error(`MetricBatchV1 聚合失败：${error.message}`);
    process.exitCode = 1;
  }
}
