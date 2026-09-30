#!/usr/bin/env node

import { Buffer } from "node:buffer";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { chmod, mkdir, readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

import {
  buildReleaseEvent,
  reportReleaseEvent,
} from "../../../scripts/performance/report-release-event.mjs";
import {
  resolveReleaseCommit,
  selectReleaseEventCommit,
} from "../../../scripts/performance/release-commit.mjs";

const require = createRequire(import.meta.url);
const { getConfig } = require("expo/config");

export const POS_HANDHELD_PRODUCTION_CHANNEL = "pos-handheld-production";
export const EAS_CLI_VERSION = "21.3.0";
export const APP_OTA_PREFLIGHT_PATH = "/api/app-ota-releases/preflight";
export const APP_OTA_REGISTER_PATH = "/api/app-ota-releases/register";
export const LEGACY_OTA_REGISTER_PATH = "/api/mobile-app-builds/ota-updates";

const POS_HANDHELD_APP_KEY = "pos-handheld";
const POS_HANDHELD_ENVIRONMENT = "production";
const POS_HANDHELD_PROJECT_NAME = "hb-pos-handheld";
const DEFAULT_PLATFORM = "ios";
const VALID_PLATFORMS = new Set(["ios", "android"]);
const VALID_PLATFORM_SELECTIONS = new Set(["ios", "android", "all"]);
const READ_ONLY_SERVICE_TOKEN_PREFIX = "hbsvc_";
const RUNTIME_VERSION_MAX_LENGTH = 120;
const EAS_CHANNEL_PAGE_LIMIT = 25;
const MAX_EAS_CHANNEL_PAGES = 100;
const RECOVERY_SCHEMA_VERSION = 1;
const APP_ROOT = fileURLToPath(new URL("../", import.meta.url));
const EMPTY_EAS_RESULT = Object.freeze({
  updateGroupId: "",
  updateId: "",
  channel: "",
  branch: "",
  platform: "",
  runtimeVersion: "",
  message: "",
  gitCommitHash: "",
  dashboardUrl: "",
  publishedAt: "",
});

const HELP_TEXT = `
用法：
  node scripts/publish-ota-release.mjs --runtime-version <version> --message <message> [--platform ios|android|all]
  node scripts/publish-ota-release.mjs --register-only <recovery.json> [--platform ios|android|all]

参数：
  --runtime-version <version>       目标 runtimeVersion；必须等于当前 resolved appVersion。
  --message <message>               EAS Update 发布说明。
  --platform <ios|android|all>      默认 ios；all 为同一 ReleaseBatchId 下两次独立发布。
  --project-id <uuid>               专用 EAS projectId；默认读取 EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID。
  --center-base-url <url>           Center 根地址；默认读取 HBPOS_OTA_CENTER_BASE_URL。
  --access-token-stdin              从非交互标准输入读取管理员 JWT。
  --rollback-of-release-id <uuid>   将新发布事实标记为指定 release 的 rollback/republish。
  --recovery-manifest <path>        登记失败时写入不含凭据的恢复 manifest。
  --register-only <path>            不发布 EAS；重跑 channel:view 验证后幂等补登记。
  --bootstrap-legacy-fixed-channel  显式执行最后一次 fixed-channel bootstrap；只允许单平台。
  --dry-run                         不读凭据、不触网、不执行 EAS，仅展示命令与 channel。
  --mock-output-file <path>         dry-run 时解析保存的 EAS JSON。
  --help, -h                        显示帮助。

默认发布只创建 ${POS_HANDHELD_PRODUCTION_CHANNEL}-{ios|android}-release-* 唯一 channel，
登记发布事实绝不自动激活策略。preview 不接受后台 OTA 管理。
`;

export class OtaPublishBatchError extends Error {
  constructor(message, results, recoveryManifest = null) {
    super(message);
    this.name = "OtaPublishBatchError";
    this.exitCode = 1;
    this.results = Object.freeze([...results]);
    this.recoveryManifest = recoveryManifest;
  }
}

function requireReleaseReporterConfig(environment) {
  const baseUrl = environment.PERFORMANCE_SERVICE_URL?.trim();
  const token = environment.PERFORMANCE_SERVICE_TOKEN?.trim();
  if (!baseUrl || !token) {
    throw new Error(
      "发布 OTA 前必须配置 PERFORMANCE_SERVICE_URL 和 PERFORMANCE_SERVICE_TOKEN",
    );
  }
  return { baseUrl, token };
}

async function reportAcceptedOtaRelease(
  payload,
  {
    completedAtUtcFn = () => new Date().toISOString(),
    config,
    logger,
    reportReleaseEventFn,
    resolvedCommit,
  },
) {
  if (!reportReleaseEventFn) return;
  const sourceRunId = payload.updateGroupId;
  const event = buildReleaseEvent({
    action: payload.isRollback ? "rollback" : "deploy",
    conclusion: "accepted",
    component: POS_HANDHELD_APP_KEY,
    environment: "Production",
    releaseId: sourceRunId,
    commitSha: selectReleaseEventCommit({
      payloadCommit: payload.gitCommitHash,
      resolvedCommit,
    }),
    startedAtUtc: payload.publishedAtUtc ?? payload.publishedAt,
    completedAtUtc: completedAtUtcFn(),
    healthChecked: true,
    sourceProvider: "expo-ota",
    sourceRunId,
  });
  try {
    await reportReleaseEventFn({ event, config });
  } catch (error) {
    throw new Error(
      `OTA 已发布并登记，不得重新发布，只重试 release event 上报：${safeErrorMessage(error)}`,
      { cause: error },
    );
  }
  logger.log(`${payload.platform}: OTA release 验收已上报。`);
}

export function createReleaseChannel(platform, nowIso, entropy) {
  const selectedPlatform = requiredPlatform(platform);
  const instant = new Date(requiredText(nowIso, "release timestamp", 64));
  if (!Number.isFinite(instant.getTime())) {
    throw new Error("release timestamp 无效");
  }
  const timestamp = instant
    .toISOString()
    .replace(/[-:.]/gu, "")
    .toLowerCase();
  const suffix = requiredText(entropy, "release entropy", 128)
    .replace(/[^a-z0-9]/giu, "")
    .toLowerCase()
    .slice(0, 8);
  if (suffix.length !== 8) {
    throw new Error("release entropy 必须至少包含 8 个字母或数字");
  }
  return requiredReleaseChannel(
    `${POS_HANDHELD_PRODUCTION_CHANNEL}-${selectedPlatform}-release-${timestamp}-${suffix}`,
    selectedPlatform,
  );
}

export function parseEasUpdateOutput(output, platform = DEFAULT_PLATFORM) {
  const selectedPlatform = requiredPlatform(platform);
  const parsed = parseJsonSafely(output);
  if (!parsed) {
    return { ...EMPTY_EAS_RESULT, platform: selectedPlatform };
  }

  const objects = collectObjects(parsed);
  const selectedUpdate = objects.find(
    (candidate) =>
      stringField(candidate, ["platform"]).toLowerCase() === selectedPlatform,
  );
  const groupObject = objects.find((candidate) => asObject(candidate.group));
  const updateGroupId =
    stringField(selectedUpdate, ["updateGroupId", "groupId", "group"]) ||
    stringField(groupObject?.group, ["id"]);

  return {
    updateGroupId,
    updateId:
      stringField(selectedUpdate, ["id", "updateId"]) ||
      firstStringFromObjects(objects, [
        selectedPlatform === "android" ? "androidUpdateId" : "iosUpdateId",
      ]),
    channel:
      stringField(selectedUpdate, ["channel", "channelName"]) ||
      firstStringFromObjects(objects, ["channel", "channelName"]),
    branch:
      stringField(selectedUpdate, ["branch", "branchName"]) ||
      firstStringFromObjects(objects, ["branch", "branchName"]),
    platform: selectedPlatform,
    runtimeVersion:
      rawStringField(selectedUpdate, ["runtimeVersion"]) ||
      firstRawStringFromObjects(objects, ["runtimeVersion"]),
    message:
      rawStringField(selectedUpdate, ["message", "commitMessage"]) ||
      firstRawStringFromObjects(objects, ["message", "commitMessage"]),
    gitCommitHash:
      stringField(selectedUpdate, ["gitCommitHash", "gitCommit", "commit"]) ||
      firstStringFromObjects(objects, ["gitCommitHash", "gitCommit", "commit"]),
    dashboardUrl:
      rawStringField(selectedUpdate, [
        "dashboardUrl",
        "dashboardURL",
        "manifestPermalink",
        "url",
      ]) ||
      firstRawStringFromObjects(objects, [
        "dashboardUrl",
        "dashboardURL",
        "manifestPermalink",
        "url",
      ]),
    publishedAt:
      stringField(selectedUpdate, ["publishedAt", "createdAt"]) ||
      firstStringFromObjects(objects, ["publishedAt", "createdAt"]),
  };
}

function parseEasChannelMappingProof(output, expectedChannel, platform) {
  const root = asObject(parseJsonSafely(output));
  const channel = asObject(root?.currentPage);
  const trustedChannel = requiredTrustedPublishChannel(
    expectedChannel,
    platform,
  );
  if (!channel || rawStringField(channel, ["name"]) !== trustedChannel) {
    throw new Error("EAS channel readback channel 不匹配");
  }
  if (channel.isPaused !== false) {
    throw new Error("EAS channel readback channel 不是 active 状态");
  }

  const mapping = asObject(
    parseJsonSafely(rawStringField(channel, ["branchMapping"])),
  );
  const mappingRows = Array.isArray(mapping?.data) ? mapping.data : [];
  const mappingRow = mappingRows.length === 1 ? asObject(mappingRows[0]) : null;
  const mappedBranchId = rawStringField(mappingRow, ["branchId"]);
  if (
    mapping?.version !== 0 ||
    !mappedBranchId ||
    mappingRow?.branchMappingLogic !== "true"
  ) {
    throw new Error("EAS channel readback branchMapping 不是单一固定映射");
  }

  const branches = Array.isArray(channel.updateBranches)
    ? channel.updateBranches
    : [];
  const branch = branches.length === 1 ? asObject(branches[0]) : null;
  if (
    !branch ||
    rawStringField(branch, ["id"]) !== mappedBranchId ||
    rawStringField(branch, ["name"]) !== trustedChannel
  ) {
    throw new Error("EAS channel readback branch 不匹配");
  }

  return Object.freeze({
    branch,
    identity: Object.freeze({
      channel: trustedChannel,
      branch: trustedChannel,
      branchId: mappedBranchId,
    }),
  });
}

export function parseAndValidateEasChannelMapping(
  output,
  expectedChannel,
  platform,
) {
  return parseEasChannelMappingProof(output, expectedChannel, platform).identity;
}

export function parseAndValidateEasChannelReadback(output, lane, published) {
  const mapping = parseEasChannelMappingProof(
    output,
    lane.releaseChannel,
    lane.platform,
  );
  if (published.branch !== mapping.identity.branch) {
    throw new Error("EAS channel readback branch 不匹配");
  }
  const branch = mapping.branch;

  // eas-cli@21.3.0 的 channel:view 将最新 group 表示为二维数组；
  // 任何展平、历史多组或组内多更新都不能证明唯一的当前目标。
  const updateGroups = Array.isArray(branch.updateGroups)
    ? branch.updateGroups
    : [];
  const latestGroup =
    updateGroups.length === 1 && Array.isArray(updateGroups[0])
      ? updateGroups[0]
      : [];
  const update = latestGroup.length === 1 ? asObject(latestGroup[0]) : null;
  if (
    !update ||
    !stringField(update, ["id", "updateId"]) ||
    !stringField(update, ["group", "updateGroupId", "groupId"]) ||
    !stringField(update, ["platform"])
  ) {
    throw new Error("EAS channel readback latest update 缺失或不唯一");
  }
  const identity = Object.freeze({
    channel: mapping.identity.channel,
    branch: mapping.identity.branch,
    updateGroupId: stringField(update, ["group", "updateGroupId", "groupId"]),
    updateId: stringField(update, ["id", "updateId"]),
    platform: stringField(update, ["platform"]).toLowerCase(),
    runtimeVersion: rawStringField(update, ["runtimeVersion"]),
    message: rawStringField(update, ["message", "commitMessage"]),
    gitCommitHash: stringField(update, ["gitCommitHash", "gitCommit", "commit"]),
    dashboardUrl: rawStringField(update, [
      "dashboardUrl",
      "manifestPermalink",
      "url",
    ]),
    publishedAtUtc: stringField(update, ["publishedAt", "createdAt"]),
  });
  validateEasChannelReadbackIdentity(identity, lane, published);
  return identity;
}

export function parseEasChannelListOutput(output) {
  const root = asObject(parseJsonSafely(output));
  const page = root?.currentPage;
  if (!Array.isArray(page)) {
    throw new Error("EAS channel:list JSON currentPage 无效");
  }
  const names = page.map((candidate) => {
    const channel = asObject(candidate);
    const rawName = channel?.name;
    if (
      typeof rawName !== "string" ||
      !rawName.trim() ||
      rawName !== rawName.trim()
    ) {
      throw new Error("EAS channel:list JSON 包含无效 channel");
    }
    return rawName;
  });
  if (new Set(names).size !== names.length) {
    throw new Error("EAS channel:list JSON 包含重复 channel");
  }
  return Object.freeze(names);
}

export async function assertReleaseChannelsUnused(
  plans,
  environment = process.env,
  runCommandFn = runCommand,
) {
  if (!Array.isArray(plans) || plans.length === 0) {
    throw new Error("EAS channel:list 没有待验证的 release lane");
  }
  const normalizedPlans = plans.map((plan) => {
    const object = asObject(plan);
    const platform = requiredPlatform(object?.platform);
    return Object.freeze({
      ...object,
      platform,
      releaseChannel: requiredReleaseChannel(
        object?.releaseChannel,
        platform,
      ),
    });
  });

  const knownChannels = new Set();
  let reachedLastPage = false;
  for (let pageIndex = 0; pageIndex < MAX_EAS_CHANNEL_PAGES; pageIndex += 1) {
    const offset = pageIndex * EAS_CHANNEL_PAGE_LIMIT;
    const command = buildEasChannelListCommand(
      normalizedPlans[0],
      offset,
      environment,
    );
    const execution = await runCommandFn(command);
    const names = parseEasChannelListOutput(execution.stdout);
    for (const name of names) {
      if (knownChannels.has(name)) {
        throw new Error(
          "EAS channel:list 分页不稳定，无法证明 release channel 未使用",
        );
      }
      knownChannels.add(name);
    }
    if (names.length < EAS_CHANNEL_PAGE_LIMIT) {
      reachedLastPage = true;
      break;
    }
  }
  if (!reachedLastPage) {
    throw new Error("EAS channel:list 超过 fail-closed 分页上限");
  }
  for (const plan of normalizedPlans) {
    if (knownChannels.has(plan.releaseChannel)) {
      throw new Error(`EAS release channel 已存在：${plan.releaseChannel}`);
    }
  }
}

export function buildOtaReleasePayload(
  parsed,
  context,
  fallbackPublishedAt = new Date().toISOString(),
) {
  const platform = requiredPlatform(context.platform);
  const rollbackOfReleaseId = context.rollbackOfReleaseId
    ? requiredUuid(context.rollbackOfReleaseId, "rollbackOfReleaseId")
    : null;
  return Object.freeze({
    releaseBatchId: requiredUuid(context.releaseBatchId, "releaseBatchId"),
    appKey: POS_HANDHELD_APP_KEY,
    environment: POS_HANDHELD_ENVIRONMENT,
    clientChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
    releaseChannel: requiredReleaseChannel(context.releaseChannel, platform),
    easBranch: parsed.branch || null,
    projectName: POS_HANDHELD_PROJECT_NAME,
    easProjectId: requiredUuid(context.projectId, "EAS projectId"),
    platform,
    runtimeVersion: requiredTrimmedToken(
      parsed.runtimeVersion || context.runtimeVersion,
      "runtimeVersion",
      RUNTIME_VERSION_MAX_LENGTH,
    ),
    updateGroupId: parsed.updateGroupId || null,
    updateId: parsed.updateId || null,
    message: requiredTrimmedText(
      parsed.message || context.message,
      "message",
      1_000,
    ),
    gitCommitHash: parsed.gitCommitHash || null,
    dashboardUrl: nullableCanonicalHttpsUrl(
      parsed.dashboardUrl || null,
      "dashboardUrl",
    ),
    publishedAtUtc: parsed.publishedAt || fallbackPublishedAt,
    isRollback: rollbackOfReleaseId !== null,
    rollbackOfReleaseId,
  });
}

function buildLegacyOtaPayload(parsed, context, fallbackPublishedAt) {
  const platform = requiredPlatform(context.platform);
  const updateId = parsed.updateId || null;
  return Object.freeze({
    projectName: POS_HANDHELD_PROJECT_NAME,
    updateGroupId: parsed.updateGroupId || null,
    updateId,
    androidUpdateId: platform === "android" ? updateId : null,
    channel: POS_HANDHELD_PRODUCTION_CHANNEL,
    branch: parsed.branch || POS_HANDHELD_PRODUCTION_CHANNEL,
    platform,
    runtimeVersion: parsed.runtimeVersion || context.runtimeVersion,
    message:
      parsed.message || requiredText(context.message, "--message", 1_000),
    gitCommitHash: parsed.gitCommitHash || null,
    dashboardUrl: parsed.dashboardUrl || null,
    publishedAt: parsed.publishedAt || fallbackPublishedAt,
    isRollback: false,
    rollbackOfGroupId: null,
    bootstrapLegacyFixedChannel: true,
  });
}

function buildCenterUrl(baseUrl, targetPath) {
  const url = new URL(requiredText(baseUrl, "Center base URL", 2_048));
  const loopbackHttp =
    url.protocol === "http:" &&
    ["localhost", "127.0.0.1", "[::1]", "::1"].includes(url.hostname);
  if (
    (url.protocol !== "https:" && !loopbackHttp) ||
    url.username ||
    url.password
  ) {
    throw new Error(
      "Center base URL 必须使用 HTTPS；HTTP 仅允许无凭据的 loopback 地址",
    );
  }
  const normalizedBasePath = url.pathname.replace(/\/+$/u, "");
  const requestPath = normalizedBasePath.endsWith("/api")
    ? targetPath.replace(/^\/api/u, "")
    : targetPath;
  url.pathname = `${normalizedBasePath}${requestPath}`;
  url.search = "";
  url.hash = "";
  return url.toString();
}

export function buildPreflightUrl(baseUrl) {
  return buildCenterUrl(baseUrl, APP_OTA_PREFLIGHT_PATH);
}

export function buildRegistrationUrl(baseUrl) {
  return buildCenterUrl(baseUrl, APP_OTA_REGISTER_PATH);
}

function buildLegacyRegistrationUrl(baseUrl) {
  return buildCenterUrl(baseUrl, LEGACY_OTA_REGISTER_PATH);
}

function buildEasEnvironment(options, environment) {
  const childEnvironment = buildEasReadbackEnvironment(options, environment);
  const runtimeVersion = childEnvironment.EXPO_PUBLIC_HBPOS_RUNTIME_VERSION;
  const currentRuntimeVersion = resolveCurrentRuntimeVersion();
  if (runtimeVersion !== currentRuntimeVersion) {
    throw new Error(
      `--runtime-version ${runtimeVersion} 与当前 resolved appVersion ` +
        `${currentRuntimeVersion} 不一致`,
    );
  }

  return childEnvironment;
}

function buildEasReadbackEnvironment(options, environment) {
  const projectId = requiredUuid(
    options.projectId ?? environment.EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID,
    "EAS projectId",
  );
  const runtimeVersion = requiredTrimmedToken(
    options.runtimeVersion,
    "--runtime-version",
    RUNTIME_VERSION_MAX_LENGTH,
  );
  // EAS 子进程只对白名单中的 EXPO_TOKEN 保留凭据；其余常见密钥名
  // 一律剔除，避免无关的中心、客户端或第三方凭据进入 EAS 环境。
  const childEnvironment = { ...environment };
  for (const key of Object.keys(childEnvironment)) {
    const normalizedKey = key.toUpperCase();
    if (
      key !== "EXPO_TOKEN" &&
      (normalizedKey.startsWith("PERFORMANCE_") ||
        /(?:TOKEN|JWT|SECRET|PASSWORD|CREDENTIAL|ACCESS_KEY|PRIVATE_KEY)/u.test(
          normalizedKey,
        ))
    ) {
      delete childEnvironment[key];
    }
  }
  return {
    ...childEnvironment,
    EXPO_PUBLIC_HBPOS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: runtimeVersion,
  };
}

export function resolveCurrentRuntimeVersion() {
  let resolvedConfig;
  try {
    resolvedConfig = getConfig(APP_ROOT, {
      skipSDKVersionRequirement: true,
    }).exp;
  } catch (error) {
    throw new Error(
      `无法解析当前 Expo config：${
        error instanceof Error ? error.message : String(error)
      }`,
      { cause: error },
    );
  }
  const appVersion = requiredToken(
    resolvedConfig.version,
    "当前 Expo appVersion",
    RUNTIME_VERSION_MAX_LENGTH,
  );
  const runtimeVersion = resolvedConfig.runtimeVersion;
  if (
    runtimeVersion &&
    typeof runtimeVersion === "object" &&
    runtimeVersion.policy === "appVersion"
  ) {
    return appVersion;
  }
  if (typeof runtimeVersion === "string" && runtimeVersion === appVersion) {
    return appVersion;
  }
  throw new Error(
    `当前 resolved runtimeVersion 必须使用 appVersion ${appVersion}`,
  );
}

export function buildEasUpdateCommand(options, environment = process.env) {
  const message = requiredTrimmedText(options.message, "--message", 1_000);
  const platform = requiredPlatform(options.platform);
  const releaseChannel = options.bootstrapLegacyFixedChannel === true
    ? requiredLegacyBootstrapChannel(options.releaseChannel)
    : requiredReleaseChannel(options.releaseChannel, platform);
  return {
    command: "npx",
    args: [
      `eas-cli@${EAS_CLI_VERSION}`,
      "update",
      "--channel",
      releaseChannel,
      "--platform",
      platform,
      "--message",
      message,
      "--json",
      "--non-interactive",
    ],
    env: buildEasEnvironment(options, environment),
  };
}

export function buildEasChannelReadbackCommand(
  options,
  environment = process.env,
) {
  const platform = requiredPlatform(options.platform);
  const releaseChannel = options.bootstrapLegacyFixedChannel === true
    ? requiredLegacyBootstrapChannel(options.releaseChannel)
    : requiredReleaseChannel(options.releaseChannel, platform);
  return {
    command: "npx",
    args: [
      `eas-cli@${EAS_CLI_VERSION}`,
      "channel:view",
      releaseChannel,
      "--json",
      "--non-interactive",
    ],
    env: buildEasReadbackEnvironment(options, environment),
  };
}

export function buildEasChannelListCommand(
  options,
  offset = 0,
  environment = process.env,
) {
  const platform = requiredPlatform(options.platform);
  requiredReleaseChannel(options.releaseChannel, platform);
  if (!Number.isSafeInteger(offset) || offset < 0) {
    throw new Error("EAS channel:list offset 无效");
  }
  return {
    command: "npx",
    args: [
      `eas-cli@${EAS_CLI_VERSION}`,
      "channel:list",
      "--json",
      "--non-interactive",
      "--limit",
      String(EAS_CHANNEL_PAGE_LIMIT),
      "--offset",
      String(offset),
    ],
    env: buildEasReadbackEnvironment(options, environment),
  };
}

async function postJson(url, payload, config, fetchFn, label) {
  const accessToken = requiredRegistrationCredential(config.accessToken);
  const response = await fetchFn(url, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${accessToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(payload),
  });
  const responseText = await response.text();
  if (!response.ok) {
    throw new Error(
      `${label}失败：HTTP ${response.status} ${response.statusText}${
        responseText ? ` - ${responseText}` : ""
      }`,
    );
  }
  const responsePayload = parseJsonSafely(responseText);
  if (responseField(responsePayload, "success") !== true) {
    throw new Error(`${label}失败：响应不是成功 ApiResponse`);
  }
  return responseField(responsePayload, "data");
}

export async function preflightOtaRelease(
  payload,
  config,
  fetchFn = globalThis.fetch,
) {
  const data = await postJson(
    buildPreflightUrl(config.baseUrl),
    payload,
    config,
    fetchFn,
    "OTA release preflight ",
  );
  if (responseField(data, "valid") !== true) {
    throw new Error("OTA release preflight 失败：data.valid 不是 true");
  }
  return Object.freeze({ valid: true });
}

export async function registerOtaRelease(
  payload,
  config,
  fetchFn = globalThis.fetch,
) {
  const data = await postJson(
    buildRegistrationUrl(config.baseUrl),
    payload,
    config,
    fetchFn,
    "OTA release 登记",
  );
  const release = responseField(data, "release");
  const idempotent = responseField(data, "idempotent");
  if (!asObject(release) || typeof idempotent !== "boolean") {
    throw new Error(
      "OTA release 登记失败：响应缺少 data.release 或 data.idempotent",
    );
  }
  validateRegisteredReleaseResponse(release, payload);
  return Object.freeze({ release, idempotent });
}

function validateRegisteredReleaseResponse(release, payload) {
  try {
    requiredUuid(responseField(release, "id"), "release.id");
    assertUuidResponseField(release, payload, "releaseBatchId");
    for (const field of [
      "appKey",
      "environment",
      "clientChannel",
      "releaseChannel",
      "easBranch",
      "projectName",
      "runtimeVersion",
      "message",
      "isRollback",
    ]) {
      if (responseField(release, field) !== payload[field]) {
        throw new Error(`${field} 不匹配`);
      }
    }
    // 可空文本字段：后端 WhenWritingNull 会省略 null，缺失视同 null 后再严格比较。
    for (const field of ["gitCommitHash", "dashboardUrl"]) {
      if (nullableResponseField(release, field) !== payload[field]) {
        throw new Error(`${field} 不匹配`);
      }
    }
    if (
      String(responseField(release, "platform")).toLowerCase() !==
      payload.platform
    ) {
      throw new Error("platform 不匹配");
    }
    assertUuidResponseField(release, payload, "updateGroupId");
    assertUuidResponseField(release, payload, "updateId");
    assertNullableUuidResponseField(release, payload, "rollbackOfReleaseId");

    const responsePublishedAt = requiredUtcInstant(
      responseField(release, "publishedAtUtc"),
      "release.publishedAtUtc",
    );
    const requestPublishedAt = requiredUtcInstant(
      payload.publishedAtUtc,
      "payload.publishedAtUtc",
    );
    if (responsePublishedAt !== requestPublishedAt) {
      throw new Error("publishedAtUtc 不匹配");
    }
    if (responseField(release, "legacy") !== false) {
      throw new Error("legacy 不匹配");
    }
    if (responseField(release, "registrationSource") !== "app-ota-release-api") {
      throw new Error("registrationSource 不匹配");
    }
    const fingerprint = responseField(release, "factFingerprint");
    if (
      fingerprint !== undefined &&
      (typeof fingerprint !== "string" || !/^[0-9a-f]{64}$/u.test(fingerprint))
    ) {
      throw new Error("factFingerprint 不匹配");
    }
  } catch (error) {
    throw new Error(
      `OTA release 登记失败：响应 identity 不匹配（${safeErrorMessage(error)}）`,
      { cause: error },
    );
  }
}

function assertUuidResponseField(release, payload, field) {
  if (
    requiredUuid(responseField(release, field), `release.${field}`) !==
    requiredUuid(payload[field], `payload.${field}`)
  ) {
    throw new Error(`${field} 不匹配`);
  }
}

// 后端 JSON 序列化配置 JsonIgnoreCondition.WhenWritingNull，值为 null 的可空字段
// 会从响应中整体省略；因此“字段缺失”与“显式 null”等价，统一归一为 null。
// 字段存在时原样返回，由调用方继续严格比较，不放宽任何非空值的校验。
function nullableResponseField(release, field) {
  const releaseObject = asObject(release);
  if (!releaseObject) return null;
  const pascalField = `${field[0].toUpperCase()}${field.slice(1)}`;
  const value = Object.hasOwn(releaseObject, field)
    ? releaseObject[field]
    : releaseObject[pascalField];
  return value === undefined ? null : value;
}

function assertNullableUuidResponseField(release, payload, field) {
  const responseValue = nullableResponseField(release, field);
  const requestValue = payload[field];
  if (responseValue === null && requestValue === null) return;
  if (responseValue === null || requestValue === null) {
    throw new Error(`${field} 不匹配`);
  }
  if (
    requiredUuid(responseValue, `release.${field}`) !==
    requiredUuid(requestValue, `payload.${field}`)
  ) {
    throw new Error(`${field} 不匹配`);
  }
}

async function registerLegacyOtaRelease(
  payload,
  config,
  fetchFn = globalThis.fetch,
) {
  const data = await postJson(
    buildLegacyRegistrationUrl(config.baseUrl),
    payload,
    config,
    fetchFn,
    "legacy bootstrap OTA 登记",
  );
  const savedGroupId = responseField(data, "updateGroupId");
  if (
    typeof savedGroupId !== "string" ||
    savedGroupId.toLowerCase() !== String(payload.updateGroupId).toLowerCase()
  ) {
    throw new Error("legacy bootstrap OTA 登记失败：updateGroupId 不匹配");
  }
  return Object.freeze({ updateGroupId: savedGroupId });
}

export async function runPublishPosHandheldOtaRelease(
  options,
  {
    environment = process.env,
    logger = console,
    createReleaseBatchIdFn = randomUUID,
    createReleaseChannelFn = (platform, nowIso) =>
      createReleaseChannel(platform, nowIso, randomUUID()),
    nowIsoFn = () => new Date().toISOString(),
    assertReleaseChannelsUnusedFn,
    preflightOtaReleaseFn = preflightOtaRelease,
    readAccessTokenStdinFn = readAccessTokenFromStdin,
    readbackEasReleaseFn,
    readMockOutputFn = readMockOutput,
    readRecoveryManifestFn = readRecoveryManifest,
    registerLegacyOtaReleaseFn = registerLegacyOtaRelease,
    registerOtaReleaseFn = registerOtaRelease,
    reportReleaseEventFn,
    resolveReleaseCommitFn = resolveReleaseCommit,
    runCommandFn = runCommand,
    completedAtUtcFn,
    writeRecoveryManifestFn = writeRecoveryManifest,
  } = {},
) {
  validateOptions(options);
  const releaseReporterConfig =
    reportReleaseEventFn && options.dryRun !== true
      ? requireReleaseReporterConfig(environment)
      : null;
  // 在任何 EAS 写入前解析 commit，避免发布成功后才因本地 SHA 缺失而失败。
  const resolvedCommit =
    reportReleaseEventFn && options.dryRun !== true
      ? resolveReleaseCommitFn({ environment })
      : null;
  const requireCenter = options.dryRun !== true;
  const configuration = await resolveConfiguration(
    options,
    environment,
    requireCenter,
    true,
    readAccessTokenStdinFn,
  );
  const centerAccess = {
    baseUrl: configuration.centerBaseUrl,
    accessToken: configuration.accessToken,
  };

  if (options.registerOnlyFile) {
    return runRegisterOnly(options, {
      configuration,
      centerAccess,
      environment,
      logger,
      readbackEasReleaseFn,
      readRecoveryManifestFn,
      registerLegacyOtaReleaseFn,
      registerOtaReleaseFn,
      reportReleaseEventFn,
      releaseReporterConfig,
      resolvedCommit,
      runCommandFn,
      completedAtUtcFn,
    });
  }

  const selection = requiredPlatformSelection(options.platform);
  const platforms = selection === "all" ? ["ios", "android"] : [selection];
  const releaseBatchId = requiredUuid(
    createReleaseBatchIdFn(),
    "ReleaseBatchId",
  );
  const createdAtUtc = nowIsoFn();
  const lanes = platforms.map((platform) => {
    const releaseChannel = options.bootstrapLegacyFixedChannel === true
      ? POS_HANDHELD_PRODUCTION_CHANNEL
      : createReleaseChannelFn(platform, createdAtUtc);
    const laneOptions = {
      ...options,
      platform,
      releaseChannel,
      releaseBatchId,
      projectId: configuration.projectId,
    };
    const command = buildEasUpdateCommand(laneOptions, environment);
    logger.log(
      `预期命令：${[command.command, ...command.args]
        .map(shellQuote)
        .join(" ")}`,
    );
    logger.log(`平台：${platform}；release channel：${releaseChannel}`);
    return Object.freeze({ platform, releaseChannel, laneOptions, command });
  });

  if (options.dryRun === true) {
    const mockOutput = options.mockOutputFile
      ? await readMockOutputFn(options.mockOutputFile)
      : "";
    const results = lanes.map((lane) => {
      const parsed = parseEasUpdateOutput(mockOutput, lane.platform);
      return Object.freeze({
        platform: lane.platform,
        releaseChannel: lane.releaseChannel,
        command: lane.command,
        previewPayload: parsed.updateId
          ? buildOtaReleasePayload(parsed, lane.laneOptions, createdAtUtc)
          : null,
      });
    });
    logger.log("dry-run：未读取管理员 JWT，未执行 EAS，未发送 Center 写入。");
    return Object.freeze({
      dryRun: true,
      status: "dry-run",
      releaseBatchId,
      results: Object.freeze(results),
    });
  }

  // `all` 必须先完成两条 lane 的 preflight；任何一条失败时都不能产生 EAS 写入。
  const preflightResults = [];
  for (const lane of lanes) {
    try {
      await preflightOtaReleaseFn(
        buildPreflightPayload(lane, configuration.projectId, options),
        centerAccess,
      );
      preflightResults.push(
        Object.freeze({
          platform: lane.platform,
          releaseChannel: lane.releaseChannel,
          status: "preflight-ok",
        }),
      );
    } catch (error) {
      preflightResults.push(
        Object.freeze({
          platform: lane.platform,
          releaseChannel: lane.releaseChannel,
          status: "preflight-failed",
          error: safeErrorMessage(error),
        }),
      );
    }
  }
  if (preflightResults.some((result) => result.status === "preflight-failed")) {
    throw new OtaPublishBatchError(
      "OTA release preflight 未全部通过；未执行任何 EAS 写入。",
      preflightResults,
    );
  }

  if (options.bootstrapLegacyFixedChannel === true) {
    await verifyLegacyBootstrapChannelMapping(
      lanes[0],
      environment,
      runCommandFn,
    );
  } else {
    // 后端只能证明数据库中未登记；首次 EAS 写入前还必须穷尽 Expo
    // channel:list，且所有新 release lane 都取得权威 unused 证明。
    const releasePlans = lanes.map((lane) => lane.laneOptions);
    await (assertReleaseChannelsUnusedFn ?? (
      (plans) => assertReleaseChannelsUnused(
        plans,
        environment,
        runCommandFn,
      )
    ))(releasePlans);
  }

  const results = [];
  const recoveryReleases = [];
  for (const lane of lanes) {
    let easCompleted = false;
    let verifiedPayload = null;
    try {
      const commandResult = await runCommandFn(lane.command);
      easCompleted = true;
      let parsed = parseEasUpdateOutput(commandResult.stdout, lane.platform);
      if (!parsed.updateId && commandResult.stderr) {
        parsed = parseEasUpdateOutput(commandResult.stderr, lane.platform);
      }
      validateParsedEasRelease(parsed, lane, options);
      const channelReadback = readbackEasReleaseFn
        ? await readbackEasReleaseFn(lane, parsed)
        : await readbackPublishedEasRelease(
            lane,
            parsed,
            environment,
            runCommandFn,
          );
      validateEasChannelReadbackIdentity(channelReadback, lane, parsed);
      parsed = Object.freeze({ ...parsed, channel: channelReadback.channel });

      if (options.bootstrapLegacyFixedChannel === true) {
        const legacyPayload = buildLegacyOtaPayload(
          parsed,
          lane.laneOptions,
          createdAtUtc,
        );
        validateLegacyPayload(legacyPayload);
        verifiedPayload = legacyPayload;
        const registration = await registerLegacyOtaReleaseFn(
          legacyPayload,
          centerAccess,
        );
        const registeredResult = {
          platform: lane.platform,
          releaseChannel: lane.releaseChannel,
          mode: "bootstrap-legacy",
          status: "registered",
          payload: legacyPayload,
          registration,
        };
        try {
          await reportAcceptedOtaRelease(legacyPayload, {
            completedAtUtcFn,
            config: releaseReporterConfig,
            logger,
            reportReleaseEventFn,
            resolvedCommit,
          });
          results.push(Object.freeze(registeredResult));
        } catch (error) {
          recoveryReleases.push(legacyPayload);
          results.push(
            Object.freeze({
              ...registeredResult,
              releaseEventStatus: "failed",
              error: safeErrorMessage(error),
            }),
          );
        }
        continue;
      }

      const releasePayload = buildOtaReleasePayload(
        parsed,
        lane.laneOptions,
        createdAtUtc,
      );
      validateReleasePayload(releasePayload);
      verifiedPayload = releasePayload;
      const registration = await registerOtaReleaseFn(
        releasePayload,
        centerAccess,
      );
      const registeredResult = {
        platform: lane.platform,
        releaseChannel: lane.releaseChannel,
        mode: "immutable-release",
        status: "registered",
        payload: releasePayload,
        registration,
      };
      try {
        await reportAcceptedOtaRelease(releasePayload, {
          completedAtUtcFn,
          config: releaseReporterConfig,
          logger,
          reportReleaseEventFn,
          resolvedCommit,
        });
        results.push(Object.freeze(registeredResult));
      } catch (error) {
        recoveryReleases.push(releasePayload);
        results.push(
          Object.freeze({
            ...registeredResult,
            releaseEventStatus: "failed",
            error: safeErrorMessage(error),
          }),
        );
      }
    } catch (error) {
      if (
        !results.some(
          (result) =>
            result.platform === lane.platform &&
            result.releaseChannel === lane.releaseChannel,
        )
      ) {
        results.push(
          Object.freeze({
            platform: lane.platform,
            releaseChannel: lane.releaseChannel,
            mode: options.bootstrapLegacyFixedChannel === true
              ? "bootstrap-legacy"
              : "immutable-release",
            status: !easCompleted
              ? "publish-failed"
              : verifiedPayload
                ? "registration-failed"
                : "published-unverified",
            easCompleted,
            ...(verifiedPayload ? { payload: verifiedPayload } : {}),
            error: safeErrorMessage(error),
          }),
        );
        if (easCompleted && verifiedPayload) {
          recoveryReleases.push(verifiedPayload);
        }
      }
    }
  }

  let recoveryManifest = null;
  if (recoveryReleases.length > 0) {
    recoveryManifest = options.bootstrapLegacyFixedChannel === true
      ? createBootstrapRecoveryManifest(
          releaseBatchId,
          createdAtUtc,
          configuration.projectId,
          recoveryReleases[0],
        )
      : createRecoveryManifest(
          releaseBatchId,
          createdAtUtc,
          recoveryReleases,
        );
    const recoveryFile = options.recoveryManifestFile || defaultRecoveryManifestPath(
      releaseBatchId,
    );
    try {
      await writeRecoveryManifestFn(recoveryFile, recoveryManifest);
    } catch (error) {
      logger.log(
        `恢复 manifest 写入失败（${safeErrorMessage(error)}）；以下内容不含凭据，请安全保存：`,
      );
      logger.log(JSON.stringify(recoveryManifest));
      throw new OtaPublishBatchError(
        "EAS 已发布且登记失败，恢复 manifest 写入失败；不得重新发布。请安全保存日志中的无凭据 manifest 后使用 --register-only。",
        results,
        recoveryManifest,
      );
    }
    const hasReleaseReportFailure = results.some(
      (result) => result.releaseEventStatus === "failed",
    );
    logger.log(
      hasReleaseReportFailure
        ? `EAS 已发布并登记但验收上报失败；不得重新发布。请用 --register-only ${shellQuote(recoveryFile)} 幂等重试上报。`
        : `EAS 已发布但登记失败；不得重新发布。请用 --register-only ${shellQuote(recoveryFile)} 补登记。`,
    );
  }

  const failures = results.filter(
    (result) =>
      result.status !== "registered" || result.releaseEventStatus === "failed",
  );
  if (failures.length > 0) {
    const hasUnverifiedPublish = failures.some(
      (result) => result.status === "published-unverified",
    );
    const hasBootstrapRegistrationFailure = failures.some(
      (result) =>
        result.mode === "bootstrap-legacy" &&
        result.status === "registration-failed",
    );
    const hasReleaseReportFailure = failures.some(
      (result) => result.releaseEventStatus === "failed",
    );
    throw new OtaPublishBatchError(
      hasUnverifiedPublish
        ? "EAS 已返回成功，但发布事实未通过严格回读验证；不得重新发布。请核对 EAS Dashboard 和原始输出后恢复登记。"
        : hasReleaseReportFailure
          ? "OTA 已发布并登记，但 release event 上报失败；禁止重发，只能用 recovery manifest 幂等重试验收上报。"
          : hasBootstrapRegistrationFailure
          ? "legacy bootstrap EAS 已发布但登记失败；不得重新发布。请使用 recovery manifest 执行 --register-only 受限补登记。"
          : results.some((result) => result.status === "registered")
            ? "OTA 发布部分完成；命令以 non-zero 结束。"
            : "OTA 发布未完成；命令以 non-zero 结束。",
      results,
      recoveryManifest,
    );
  }

  return Object.freeze({
    dryRun: false,
    status: "complete",
    releaseBatchId,
    results: Object.freeze(results),
  });
}

async function runRegisterOnly(
  options,
  {
    configuration,
    centerAccess,
    completedAtUtcFn,
    environment,
    logger,
    readbackEasReleaseFn,
    readRecoveryManifestFn,
    registerLegacyOtaReleaseFn,
    registerOtaReleaseFn,
    releaseReporterConfig,
    reportReleaseEventFn,
    resolvedCommit,
    runCommandFn,
  },
) {
  const rawManifest = await readRecoveryManifestFn(options.registerOnlyFile);
  const manifest = normalizeRecoveryManifest(rawManifest);
  const selection = requiredPlatformSelection(options.platform ?? "all");
  if (manifest.mode === "bootstrap-legacy") {
    return runBootstrapRegisterOnly(manifest, selection, {
      configuration,
      centerAccess,
      completedAtUtcFn,
      environment,
      logger,
      releaseReporterConfig,
      reportReleaseEventFn,
      resolvedCommit,
      registerLegacyOtaReleaseFn,
      runCommandFn,
    });
  }
  const releases = selection === "all"
    ? manifest.releases
    : manifest.releases.filter((release) => release.platform === selection);
  if (releases.length === 0) {
    throw new Error("recovery manifest 没有匹配 --platform 的 release");
  }
  const results = [];
  for (const release of releases) {
    const published = Object.freeze({
      branch: release.easBranch,
      updateGroupId: release.updateGroupId,
      updateId: release.updateId,
      platform: release.platform,
      runtimeVersion: release.runtimeVersion,
      message: release.message,
      gitCommitHash: release.gitCommitHash ?? "",
      dashboardUrl: release.dashboardUrl ?? "",
      publishedAtUtc: release.publishedAtUtc,
    });
    const lane = Object.freeze({
      platform: release.platform,
      releaseChannel: release.releaseChannel,
      laneOptions: Object.freeze({
        platform: release.platform,
        releaseChannel: release.releaseChannel,
        runtimeVersion: release.runtimeVersion,
        message: release.message,
        projectId: configuration.projectId,
      }),
    });
    try {
      if (release.easProjectId !== configuration.projectId) {
        throw new Error(
          "recovery release 的 EAS project identity 与当前配置不匹配",
        );
      }
      const channelReadback = readbackEasReleaseFn
        ? await readbackEasReleaseFn(lane, published)
        : await readbackPublishedEasRelease(
            lane,
            published,
            environment,
            runCommandFn,
          );
      validateEasChannelReadbackIdentity(channelReadback, lane, published);
    } catch (error) {
      results.push(
        Object.freeze({
          platform: release.platform,
          releaseChannel: release.releaseChannel,
          status: "verification-failed",
          payload: release,
          error: safeErrorMessage(error),
        }),
      );
      continue;
    }
    try {
      const registration = await registerOtaReleaseFn(release, centerAccess);
      const registeredResult = {
        platform: release.platform,
        releaseChannel: release.releaseChannel,
        status: "registered",
        payload: release,
        registration,
      };
      try {
        await reportAcceptedOtaRelease(release, {
          completedAtUtcFn,
          config: releaseReporterConfig,
          logger,
          reportReleaseEventFn,
          resolvedCommit,
        });
        results.push(Object.freeze(registeredResult));
      } catch (error) {
        results.push(
          Object.freeze({
            ...registeredResult,
            releaseEventStatus: "failed",
            error: safeErrorMessage(error),
          }),
        );
      }
    } catch (error) {
      results.push(
        Object.freeze({
          platform: release.platform,
          releaseChannel: release.releaseChannel,
          status: "registration-failed",
          payload: release,
          error: safeErrorMessage(error),
        }),
      );
    }
  }
  if (
    results.some(
      (result) =>
        result.status !== "registered" || result.releaseEventStatus === "failed",
    )
  ) {
    const hasReleaseReportFailure = results.some(
      (result) => result.releaseEventStatus === "failed",
    );
    throw new OtaPublishBatchError(
      hasReleaseReportFailure
        ? "register-only 已完成幂等登记，但 release event 上报失败；未执行 EAS 发布，可安全重试同一 recovery manifest。"
        : "register-only 未全部成功；未执行 EAS 发布，也未自动重放写入。",
      results,
      manifest,
    );
  }
  logger.log("register-only 完成；仅执行 EAS channel:view，未发布 EAS，发布策略未改变。");
  return Object.freeze({
    dryRun: false,
    status: "complete",
    releaseBatchId: manifest.releaseBatchId,
    results: Object.freeze(results),
  });
}

async function runBootstrapRegisterOnly(
  manifest,
  selection,
  {
    configuration,
    centerAccess,
    completedAtUtcFn,
    environment,
    logger,
    releaseReporterConfig,
    reportReleaseEventFn,
    resolvedCommit,
    registerLegacyOtaReleaseFn,
    runCommandFn,
  },
) {
  const release = manifest.release;
  if (selection !== "all" && selection !== release.platform) {
    throw new Error("recovery manifest 没有匹配 --platform 的 bootstrap release");
  }
  if (manifest.easProjectId !== configuration.projectId) {
    throw new Error(
      "bootstrap recovery 的 EAS project identity 与当前配置不匹配",
    );
  }

  const published = Object.freeze({
    branch: release.branch,
    updateGroupId: release.updateGroupId,
    updateId: release.updateId,
    platform: release.platform,
    runtimeVersion: release.runtimeVersion,
    message: release.message,
    gitCommitHash: release.gitCommitHash ?? "",
    dashboardUrl: release.dashboardUrl ?? "",
    publishedAtUtc: release.publishedAt,
  });
  const lane = Object.freeze({
    platform: release.platform,
    releaseChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
    laneOptions: Object.freeze({
      platform: release.platform,
      releaseChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
      runtimeVersion: release.runtimeVersion,
      message: release.message,
      projectId: configuration.projectId,
      bootstrapLegacyFixedChannel: true,
    }),
  });
  const results = [];
  try {
    // recovery 只能信任当前固定 CLI 的 channel:view；不允许测试或
    // 调用方注入替代回读，避免绕过 Expo 权威事实。
    const channelReadback = await readbackPublishedEasRelease(
      lane,
      published,
      environment,
      runCommandFn,
    );
    validateEasChannelReadbackIdentity(channelReadback, lane, published);
  } catch (error) {
    results.push(
      Object.freeze({
        platform: release.platform,
        releaseChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
        mode: "bootstrap-legacy",
        status: "verification-failed",
        payload: release,
        error: safeErrorMessage(error),
      }),
    );
  }

  if (results.length === 0) {
    try {
      const registration = await registerLegacyOtaReleaseFn(
        release,
        centerAccess,
      );
      const registeredResult = {
        platform: release.platform,
        releaseChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
        mode: "bootstrap-legacy",
        status: "registered",
        payload: release,
        registration,
      };
      try {
        await reportAcceptedOtaRelease(release, {
          completedAtUtcFn,
          config: releaseReporterConfig,
          logger,
          reportReleaseEventFn,
          resolvedCommit,
        });
        results.push(Object.freeze(registeredResult));
      } catch (error) {
        results.push(
          Object.freeze({
            ...registeredResult,
            releaseEventStatus: "failed",
            error: safeErrorMessage(error),
          }),
        );
      }
    } catch (error) {
      results.push(
        Object.freeze({
          platform: release.platform,
          releaseChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
          mode: "bootstrap-legacy",
          status: "registration-failed",
          payload: release,
          error: safeErrorMessage(error),
        }),
      );
    }
  }

  if (
    results[0].status !== "registered" ||
    results[0].releaseEventStatus === "failed"
  ) {
    const releaseReportFailed = results[0].releaseEventStatus === "failed";
    throw new OtaPublishBatchError(
      releaseReportFailed
        ? "bootstrap register-only 已完成幂等登记，但 release event 上报失败；未执行 EAS 发布，可安全重试。"
        : "bootstrap register-only 未成功；未执行 EAS 发布，也未写入 AppOtaRelease。",
      results,
      manifest,
    );
  }
  logger.log(
    "bootstrap register-only 完成；仅执行 EAS channel:view 和受限旧登记，未发布 EAS。",
  );
  return Object.freeze({
    dryRun: false,
    status: "complete",
    releaseBatchId: manifest.releaseBatchId,
    results: Object.freeze(results),
  });
}

function buildPreflightPayload(lane, projectId, options) {
  return Object.freeze({
    appKey: POS_HANDHELD_APP_KEY,
    environment: POS_HANDHELD_ENVIRONMENT,
    clientChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
    releaseChannel: lane.releaseChannel,
    easBranch: lane.releaseChannel,
    projectName: POS_HANDHELD_PROJECT_NAME,
    easProjectId: projectId,
    platform: lane.platform,
    runtimeVersion: requiredTrimmedToken(
      options.runtimeVersion,
      "--runtime-version",
      RUNTIME_VERSION_MAX_LENGTH,
    ),
    ...(options.rollbackOfReleaseId
      ? {
          rollbackOfReleaseId: requiredUuid(
            options.rollbackOfReleaseId,
            "--rollback-of-release-id",
          ),
        }
      : {}),
    bootstrapLegacyFixedChannel:
      options.bootstrapLegacyFixedChannel === true,
  });
}

function createRecoveryManifest(releaseBatchId, createdAtUtc, releases) {
  return Object.freeze({
    schemaVersion: RECOVERY_SCHEMA_VERSION,
    appKey: POS_HANDHELD_APP_KEY,
    environment: POS_HANDHELD_ENVIRONMENT,
    releaseBatchId: requiredUuid(releaseBatchId, "releaseBatchId"),
    createdAtUtc: requiredUtcInstant(createdAtUtc, "createdAtUtc"),
    releases: Object.freeze([...releases]),
  });
}

function createBootstrapRecoveryManifest(
  releaseBatchId,
  createdAtUtc,
  easProjectId,
  release,
) {
  validateLegacyPayload(release);
  return Object.freeze({
    schemaVersion: RECOVERY_SCHEMA_VERSION,
    mode: "bootstrap-legacy",
    appKey: POS_HANDHELD_APP_KEY,
    environment: POS_HANDHELD_ENVIRONMENT,
    releaseBatchId: requiredUuid(releaseBatchId, "releaseBatchId"),
    createdAtUtc: requiredUtcInstant(createdAtUtc, "createdAtUtc"),
    easProjectId: requiredUuid(easProjectId, "EAS projectId"),
    release: Object.freeze({ ...release }),
  });
}

function normalizeRecoveryManifest(input) {
  const object = asObject(input);
  if (object?.mode === "bootstrap-legacy") {
    return normalizeBootstrapRecoveryManifest(object);
  }
  const fields = [
    "schemaVersion",
    "appKey",
    "environment",
    "releaseBatchId",
    "createdAtUtc",
    "releases",
  ];
  if (
    !object ||
    Object.keys(object).length !== fields.length ||
    fields.some((field) => !Object.hasOwn(object, field)) ||
    object.schemaVersion !== RECOVERY_SCHEMA_VERSION ||
    object.appKey !== POS_HANDHELD_APP_KEY ||
    object.environment !== POS_HANDHELD_ENVIRONMENT ||
    !Array.isArray(object.releases) ||
    object.releases.length === 0
  ) {
    throw new Error("recovery manifest 结构无效");
  }
  const releaseBatchId = requiredUuid(object.releaseBatchId, "releaseBatchId");
  const createdAtUtc = requiredUtcInstant(object.createdAtUtc, "createdAtUtc");
  const releases = object.releases.map((release) => {
    validateReleasePayload(release);
    if (
      requiredUuid(release.releaseBatchId, "release.releaseBatchId") !==
      releaseBatchId
    ) {
      throw new Error("recovery manifest ReleaseBatchId 不一致");
    }
    return Object.freeze({ ...release });
  });
  return createRecoveryManifest(releaseBatchId, createdAtUtc, releases);
}

function normalizeBootstrapRecoveryManifest(object) {
  const fields = [
    "schemaVersion",
    "mode",
    "appKey",
    "environment",
    "releaseBatchId",
    "createdAtUtc",
    "easProjectId",
    "release",
  ];
  if (
    !hasExactFields(object, fields) ||
    object.schemaVersion !== RECOVERY_SCHEMA_VERSION ||
    object.mode !== "bootstrap-legacy" ||
    object.appKey !== POS_HANDHELD_APP_KEY ||
    object.environment !== POS_HANDHELD_ENVIRONMENT
  ) {
    throw new Error("bootstrap recovery manifest 结构无效");
  }
  return createBootstrapRecoveryManifest(
    object.releaseBatchId,
    object.createdAtUtc,
    object.easProjectId,
    object.release,
  );
}

export function parsePublishOtaArgs(argv) {
  const options = {
    dryRun: false,
    help: false,
    platform: DEFAULT_PLATFORM,
  };
  let platformExplicit = false;
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === "--help" || argument === "-h") {
      options.help = true;
      continue;
    }
    if (argument === "--dry-run") {
      options.dryRun = true;
      continue;
    }
    if (argument === "--access-token-stdin") {
      if (options.accessTokenStdin === true) {
        throw new Error("参数 --access-token-stdin 不能重复");
      }
      options.accessTokenStdin = true;
      continue;
    }
    if (argument === "--bootstrap-legacy-fixed-channel") {
      options.bootstrapLegacyFixedChannel = true;
      continue;
    }
    if (argument === "--environment" || argument.startsWith("--environment=")) {
      throw new Error(
        "手持 POS OTA 固定为 production，不接受 --environment 或 preview",
      );
    }
    if (
      argument === "--access-token" ||
      argument.startsWith("--access-token=")
    ) {
      throw new Error(
        "禁止使用 --access-token 传递凭据；请改用 --access-token-stdin 或 HBPOS_OTA_CENTER_ACCESS_TOKEN",
      );
    }
    if (!argument.startsWith("--")) {
      throw new Error(`未知参数：${argument}`);
    }
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`参数 ${argument} 缺少取值`);
    }
    switch (argument) {
      case "--runtime-version":
        options.runtimeVersion = value;
        break;
      case "--platform":
        options.platform = value;
        platformExplicit = true;
        break;
      case "--message":
        options.message = value;
        break;
      case "--project-id":
        options.projectId = value;
        break;
      case "--center-base-url":
        options.centerBaseUrl = value;
        break;
      case "--mock-output-file":
        options.mockOutputFile = value;
        break;
      case "--rollback-of-release-id":
        options.rollbackOfReleaseId = value;
        break;
      case "--recovery-manifest":
        options.recoveryManifestFile = value;
        break;
      case "--register-only":
        options.registerOnlyFile = value;
        break;
      default:
        throw new Error(`未知参数：${argument}`);
    }
    index += 1;
  }
  if (options.registerOnlyFile && !platformExplicit) options.platform = "all";
  return options;
}

function validateOptions(options) {
  if (!options || typeof options !== "object") {
    throw new Error("发布参数无效");
  }
  if (options.accessToken !== undefined) {
    throw new Error(
      "禁止通过 options.accessToken 传递凭据；请使用标准输入或环境变量",
    );
  }
  requiredPlatformSelection(options.platform ?? DEFAULT_PLATFORM);
  if (options.registerOnlyFile) {
    requiredText(options.registerOnlyFile, "--register-only", 2_048);
    if (
      options.dryRun === true ||
      options.bootstrapLegacyFixedChannel === true ||
      options.mockOutputFile ||
      options.runtimeVersion ||
      options.message ||
      options.rollbackOfReleaseId
    ) {
      throw new Error("--register-only 不能与发布、bootstrap、dry-run 参数混用");
    }
    return;
  }
  requiredTrimmedToken(
    options.runtimeVersion,
    "--runtime-version",
    RUNTIME_VERSION_MAX_LENGTH,
  );
  requiredTrimmedText(options.message, "--message", 1_000);
  if (options.rollbackOfReleaseId) {
    requiredUuid(options.rollbackOfReleaseId, "--rollback-of-release-id");
  }
  if (options.mockOutputFile && options.dryRun !== true) {
    throw new Error("--mock-output-file 只能与 --dry-run 一起使用");
  }
  if (
    options.bootstrapLegacyFixedChannel === true &&
    options.rollbackOfReleaseId
  ) {
    throw new Error("legacy bootstrap 不支持 rollback 标记");
  }
  if (
    options.bootstrapLegacyFixedChannel === true &&
    options.platform === "all"
  ) {
    throw new Error("bootstrap fixed-channel 只允许单平台，不能使用 --platform all");
  }
}

async function resolveConfiguration(
  options,
  environment,
  requireCenter,
  requireProject,
  readAccessTokenStdinFn,
) {
  const projectId = requireProject
    ? requiredUuid(
        options.projectId ?? environment.EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID,
        "EAS projectId",
      )
    : "";
  if (!requireCenter) {
    return { projectId, centerBaseUrl: "", accessToken: "" };
  }
  const centerBaseUrl = requiredText(
    options.centerBaseUrl ?? environment.HBPOS_OTA_CENTER_BASE_URL,
    "Center base URL",
    2_048,
  );
  buildRegistrationUrl(centerBaseUrl);
  const legacyEnvironmentAccessToken =
    environment.HBPOS_OTA_CENTER_ACCESS_TOKEN ??
    environment.HBPOS_OTA_ADMIN_JWT;
  const releaseServiceToken =
    environment.HBPOS_OTA_RELEASE_SERVICE_TOKEN;
  if (options.accessTokenStdin === true) {
    if (
      legacyEnvironmentAccessToken !== undefined ||
      releaseServiceToken !== undefined
    ) {
      throw new Error(
        "--access-token-stdin 不能与任何 Center 凭据环境变量同时使用",
      );
    }
  } else if (legacyEnvironmentAccessToken !== undefined) {
    throw new Error(
      "管理员 JWT 禁止从环境变量读取；请使用 --access-token-stdin",
    );
  }
  const accessToken = options.accessTokenStdin === true
    ? requiredAdministratorJwt(await readAccessTokenStdinFn())
    : requiredReleaseServiceToken(releaseServiceToken);
  return { projectId, centerBaseUrl, accessToken };
}

export async function readAccessTokenFromStdin(input = process.stdin) {
  if (input.isTTY === true) {
    throw new Error("--access-token-stdin 需要非交互标准输入，不能等待 TTY");
  }
  let byteLength = 0;
  let content = "";
  for await (const chunk of input) {
    const buffer = Buffer.isBuffer(chunk)
      ? chunk
      : Buffer.from(String(chunk));
    byteLength += buffer.byteLength;
    if (byteLength > 4_096) {
      throw new Error("管理员 access token 超过 4096 bytes");
    }
    content += buffer.toString("utf8");
  }
  return content.replace(/\r?\n$/u, "");
}

function validateParsedEasRelease(parsed, lane, options) {
  const allowLegacy = options.bootstrapLegacyFixedChannel === true;
  const expectedChannel = allowLegacy
    ? requiredLegacyBootstrapChannel(lane.releaseChannel)
    : requiredReleaseChannel(lane.releaseChannel, lane.platform);
  if (parsed.branch !== expectedChannel) {
    throw new Error(`EAS JSON branch 不匹配：${parsed.branch || "--"}`);
  }
  if (parsed.runtimeVersion !== options.runtimeVersion) {
    throw new Error(
      `EAS JSON runtimeVersion 不匹配：${parsed.runtimeVersion || "--"}`,
    );
  }
  if (parsed.platform !== lane.platform) {
    throw new Error(`EAS JSON platform 不匹配：${parsed.platform || "--"}`);
  }
  requiredUuid(parsed.updateGroupId, "updateGroupId");
  requiredUuid(parsed.updateId, "updateId");
  if (
    parsed.message !== requiredTrimmedText(options.message, "--message", 1_000)
  ) {
    throw new Error(`EAS JSON message 不匹配：${parsed.message || "--"}`);
  }
  requiredUtcInstant(parsed.publishedAt, "publishedAtUtc");
}

function requiredTrustedPublishChannel(value, platform) {
  const normalized = requiredText(value, "release channel", 200);
  return normalized === POS_HANDHELD_PRODUCTION_CHANNEL
    ? requiredLegacyBootstrapChannel(normalized)
    : requiredReleaseChannel(normalized, platform);
}

function validateEasChannelReadbackIdentity(readback, lane, published) {
  const object = asObject(readback);
  const expectedChannel = requiredTrustedPublishChannel(
    lane.releaseChannel,
    lane.platform,
  );
  const matches =
    object?.channel === expectedChannel &&
    object.branch === published.branch &&
    String(object.updateGroupId).toLowerCase() ===
      String(published.updateGroupId).toLowerCase() &&
    String(object.updateId).toLowerCase() ===
      String(published.updateId).toLowerCase() &&
    object.platform === lane.platform &&
    object.runtimeVersion === published.runtimeVersion &&
    object.message === published.message &&
    object.gitCommitHash === (published.gitCommitHash ?? "") &&
    object.dashboardUrl === (published.dashboardUrl ?? "") &&
    object.publishedAtUtc ===
      (published.publishedAtUtc ?? published.publishedAt);
  if (!matches) {
    throw new Error(
      "EAS channel readback 的 channel/branch/update/runtime/platform/fact 身份不匹配",
    );
  }
}

async function verifyLegacyBootstrapChannelMapping(
  lane,
  environment,
  runCommandFn,
) {
  const command = buildEasChannelReadbackCommand(
    lane.laneOptions,
    environment,
  );
  const result = await runCommandFn(command);
  try {
    return parseAndValidateEasChannelMapping(
      result.stdout,
      POS_HANDHELD_PRODUCTION_CHANNEL,
      lane.platform,
    );
  } catch (stdoutError) {
    if (!result.stderr) throw stdoutError;
    return parseAndValidateEasChannelMapping(
      result.stderr,
      POS_HANDHELD_PRODUCTION_CHANNEL,
      lane.platform,
    );
  }
}

async function readbackPublishedEasRelease(
  lane,
  published,
  environment,
  runCommandFn,
) {
  const command = buildEasChannelReadbackCommand(
    lane.laneOptions,
    environment,
  );
  const result = await runCommandFn(command);
  try {
    return parseAndValidateEasChannelReadback(
      result.stdout,
      lane,
      published,
    );
  } catch (stdoutError) {
    if (!result.stderr) throw stdoutError;
    return parseAndValidateEasChannelReadback(
      result.stderr,
      lane,
      published,
    );
  }
}

function validateReleasePayload(payload) {
  const fields = [
    "releaseBatchId",
    "appKey",
    "environment",
    "clientChannel",
    "releaseChannel",
    "easBranch",
    "projectName",
    "easProjectId",
    "platform",
    "runtimeVersion",
    "updateGroupId",
    "updateId",
    "message",
    "gitCommitHash",
    "dashboardUrl",
    "publishedAtUtc",
    "isRollback",
    "rollbackOfReleaseId",
  ];
  const object = asObject(payload);
  if (
    !object ||
    Object.keys(object).length !== fields.length ||
    fields.some((field) => !Object.hasOwn(object, field))
  ) {
    throw new Error("immutable OTA release payload 结构无效");
  }
  if (
    object.appKey !== POS_HANDHELD_APP_KEY ||
    object.environment !== POS_HANDHELD_ENVIRONMENT ||
    object.clientChannel !== POS_HANDHELD_PRODUCTION_CHANNEL ||
    object.projectName !== POS_HANDHELD_PROJECT_NAME
  ) {
    throw new Error("immutable OTA release identity 不匹配");
  }
  requiredUuid(object.releaseBatchId, "releaseBatchId");
  const platform = requiredPlatform(object.platform);
  const releaseChannel = requiredReleaseChannel(object.releaseChannel, platform);
  if (object.easBranch !== releaseChannel) {
    throw new Error("immutable OTA easBranch 不匹配 releaseChannel");
  }
  requiredUuid(object.easProjectId, "EAS projectId");
  requiredTrimmedToken(
    object.runtimeVersion,
    "runtimeVersion",
    RUNTIME_VERSION_MAX_LENGTH,
  );
  requiredUuid(object.updateGroupId, "updateGroupId");
  requiredUuid(object.updateId, "updateId");
  requiredTrimmedText(object.message, "message", 1_000);
  requiredUtcInstant(object.publishedAtUtc, "publishedAtUtc");
  if (object.gitCommitHash !== null) {
    requiredToken(object.gitCommitHash, "gitCommitHash", 128);
  }
  if (object.dashboardUrl !== null) {
    const dashboardUrl = nullableCanonicalHttpsUrl(
      object.dashboardUrl,
      "dashboardUrl",
    );
    if (dashboardUrl !== object.dashboardUrl) {
      throw new Error("dashboardUrl 无效");
    }
  }
  if (typeof object.isRollback !== "boolean") {
    throw new Error("isRollback 无效");
  }
  const rollbackOfReleaseId = object.rollbackOfReleaseId === null
    ? null
    : requiredUuid(object.rollbackOfReleaseId, "rollbackOfReleaseId");
  if (object.isRollback !== (rollbackOfReleaseId !== null)) {
    throw new Error("rollback identity 不一致");
  }
  return payload;
}

function validateLegacyPayload(payload) {
  const fields = [
    "projectName",
    "updateGroupId",
    "updateId",
    "androidUpdateId",
    "channel",
    "branch",
    "platform",
    "runtimeVersion",
    "message",
    "gitCommitHash",
    "dashboardUrl",
    "publishedAt",
    "isRollback",
    "rollbackOfGroupId",
    "bootstrapLegacyFixedChannel",
  ];
  const object = asObject(payload);
  if (
    !hasExactFields(object, fields) ||
    object.projectName !== POS_HANDHELD_PROJECT_NAME ||
    object.channel !== POS_HANDHELD_PRODUCTION_CHANNEL ||
    object.branch !== POS_HANDHELD_PRODUCTION_CHANNEL ||
    object.bootstrapLegacyFixedChannel !== true
  ) {
    throw new Error("legacy bootstrap identity 不匹配");
  }
  const platform = requiredPlatform(object.platform);
  requiredUuid(object.updateGroupId, "updateGroupId");
  const updateId = requiredUuid(object.updateId, "updateId");
  if (platform === "android") {
    if (requiredUuid(object.androidUpdateId, "androidUpdateId") !== updateId) {
      throw new Error("Android legacy bootstrap updateId 不一致");
    }
  } else if (object.androidUpdateId !== null) {
    throw new Error("iOS legacy bootstrap 不得携带 androidUpdateId");
  }
  requiredTrimmedToken(
    object.runtimeVersion,
    "runtimeVersion",
    RUNTIME_VERSION_MAX_LENGTH,
  );
  requiredTrimmedText(object.message, "message", 1_000);
  if (object.gitCommitHash !== null) {
    requiredToken(object.gitCommitHash, "gitCommitHash", 128);
  }
  if (
    nullableCanonicalHttpsUrl(object.dashboardUrl, "dashboardUrl") !==
    object.dashboardUrl
  ) {
    throw new Error("dashboardUrl 无效");
  }
  requiredUtcInstant(object.publishedAt, "publishedAt");
  if (object.isRollback !== false || object.rollbackOfGroupId !== null) {
    throw new Error("legacy bootstrap rollback identity 无效");
  }
  return payload;
}

function requiredPlatform(value) {
  const platform = requiredText(value ?? DEFAULT_PLATFORM, "--platform", 20)
    .toLowerCase();
  if (!VALID_PLATFORMS.has(platform)) {
    throw new Error("--platform 必须是 ios 或 android");
  }
  return platform;
}

function requiredPlatformSelection(value) {
  const platform = requiredText(value ?? DEFAULT_PLATFORM, "--platform", 20)
    .toLowerCase();
  if (!VALID_PLATFORM_SELECTIONS.has(platform)) {
    throw new Error("--platform 必须是 ios、android 或 all");
  }
  return platform;
}

function requiredReleaseChannel(value, platform) {
  const channel = requiredToken(value, "release channel", 128);
  const prefix = `${POS_HANDHELD_PRODUCTION_CHANNEL}-${requiredPlatform(platform)}-release-`;
  const suffix = channel.startsWith(prefix) ? channel.slice(prefix.length) : "";
  if (!/^[a-z0-9][a-z0-9-]{7,79}$/u.test(suffix)) {
    throw new Error(
      `release channel 必须匹配 ${prefix}*，且包含不可复用后缀`,
    );
  }
  return channel;
}

function requiredLegacyBootstrapChannel(value) {
  if (value !== POS_HANDHELD_PRODUCTION_CHANNEL) {
    throw new Error("legacy bootstrap channel 必须精确等于 production fixed channel");
  }
  return value;
}

function requiredRegistrationCredential(value) {
  if (
    typeof value === "string" &&
    value.startsWith(READ_ONLY_SERVICE_TOKEN_PREFIX)
  ) {
    return requiredReleaseServiceToken(value);
  }
  return requiredAdministratorJwt(value);
}

function requiredAdministratorJwt(value) {
  const token = requiredText(value, "管理员 access token", 4_096);
  if (token.toLowerCase().startsWith(READ_ONLY_SERVICE_TOKEN_PREFIX)) {
    throw new Error(
      "只读 hbsvc_ service token 不能登记 OTA release；请使用管理员 JWT",
    );
  }
  if (value !== token || /\s/u.test(token)) {
    throw new Error("管理员 access token 无效");
  }
  if (!/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/u.test(token)) {
    throw new Error("管理员 access token 必须是三段 base64url JWT");
  }
  return token;
}

function requiredReleaseServiceToken(value) {
  const token = requiredText(value, "专用发布 service token", 4_096);
  if (
    value !== token ||
    /\s/u.test(token) ||
    !/^hbsvc_[A-Za-z0-9_-]{8,}$/u.test(token)
  ) {
    throw new Error("专用发布 service token 无效");
  }
  return token;
}

function requiredUuid(value, field) {
  const normalized = requiredText(value, field, 36).toLowerCase();
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(
      normalized,
    )
  ) {
    throw new Error(`${field} 必须是 UUID`);
  }
  return normalized;
}

function requiredToken(value, field, maximum) {
  const normalized = requiredText(value, field, maximum);
  if (!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/u.test(normalized)) {
    throw new Error(`${field} 无效`);
  }
  return normalized;
}

function requiredTrimmedToken(value, field, maximum) {
  const normalized = requiredToken(value, field, maximum);
  if (value !== normalized) {
    throw new Error(`${field} 必须已 trim`);
  }
  return normalized;
}

function requiredTrimmedText(value, field, maximum) {
  const normalized = requiredText(value, field, maximum);
  if (value !== normalized) {
    throw new Error(`${field} 必须已 trim`);
  }
  return normalized;
}

function nullableCanonicalHttpsUrl(value, field) {
  if (value === null || value === undefined || value === "") return null;
  const normalized = requiredTrimmedText(value, field, 2_048);
  let url;
  try {
    url = new URL(normalized);
  } catch {
    throw new Error(`${field} 无效`);
  }
  if (
    url.protocol !== "https:" ||
    url.username ||
    url.password ||
    url.toString() !== normalized
  ) {
    throw new Error(`${field} 必须是规范 HTTPS URL`);
  }
  return normalized;
}

function requiredText(value, field, maximum) {
  if (typeof value !== "string") {
    throw new Error(`${field} 不能为空`);
  }
  const normalized = value.trim();
  if (
    !normalized ||
    normalized.length > maximum ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new Error(`${field} 无效`);
  }
  return normalized;
}

function requiredIsoInstant(value, field) {
  const normalized = requiredText(value, field, 64);
  if (!Number.isFinite(Date.parse(normalized))) {
    throw new Error(`${field} 无效`);
  }
  return normalized;
}

function requiredUtcInstant(value, field) {
  const normalized = requiredIsoInstant(value, field);
  if (value !== normalized || !/(?:Z|\+00:00)$/iu.test(normalized)) {
    throw new Error(`${field} 必须是已 trim 的 UTC 时间`);
  }
  return new Date(normalized).toISOString();
}

function parseJsonSafely(output) {
  const clean = stripAnsi(String(output ?? "")).trim();
  if (!clean) return null;
  try {
    return JSON.parse(clean);
  } catch {
    const starts = [clean.indexOf("{"), clean.indexOf("[")].filter(
      (index) => index >= 0,
    );
    if (!starts.length) return null;
    const start = Math.min(...starts);
    const end = Math.max(clean.lastIndexOf("}"), clean.lastIndexOf("]"));
    if (end <= start) return null;
    try {
      return JSON.parse(clean.slice(start, end + 1));
    } catch {
      return null;
    }
  }
}

function stripAnsi(input) {
  return input.replace(/\x1B\[[0-?]*[ -/]*[@-~]/gu, "");
}

function collectObjects(value, output = []) {
  const object = asObject(value);
  if (object) {
    output.push(object);
    for (const nested of Object.values(object)) collectObjects(nested, output);
  } else if (Array.isArray(value)) {
    for (const nested of value) collectObjects(nested, output);
  }
  return output;
}

function asObject(value) {
  return value && typeof value === "object" && !Array.isArray(value)
    ? value
    : null;
}

function hasExactFields(object, fields) {
  return Boolean(
    object &&
    Object.keys(object).length === fields.length &&
    fields.every((field) => Object.hasOwn(object, field)),
  );
}

function stringField(source, keys) {
  const object = asObject(source);
  if (!object) return "";
  for (const key of keys) {
    const value = object[key];
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return "";
}

function rawStringField(source, keys) {
  const object = asObject(source);
  if (!object) return "";
  for (const key of keys) {
    const value = object[key];
    if (typeof value === "string" && value.length > 0) return value;
  }
  return "";
}

function firstStringFromObjects(objects, keys) {
  for (const object of objects) {
    const value = stringField(object, keys);
    if (value) return value;
  }
  return "";
}

function firstRawStringFromObjects(objects, keys) {
  for (const object of objects) {
    const value = rawStringField(object, keys);
    if (value) return value;
  }
  return "";
}

function responseField(source, camelKey) {
  const object = asObject(source);
  if (!object) return undefined;
  const pascalKey = `${camelKey[0].toUpperCase()}${camelKey.slice(1)}`;
  return object[camelKey] ?? object[pascalKey];
}

function safeErrorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

function shellQuote(value) {
  return /^[A-Za-z0-9_@%+=:,./-]+$/u.test(value)
    ? value
    : `'${value.replace(/'/gu, "'\\''")}'`;
}

function runCommand({ command, args, env }) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: process.cwd(),
      env,
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => {
      const output = chunk.toString();
      stdout += output;
      process.stdout.write(output);
    });
    child.stderr.on("data", (chunk) => {
      const output = chunk.toString();
      stderr += output;
      process.stderr.write(output);
    });
    child.on("error", reject);
    child.on("close", (code) => {
      if (code === 0) {
        resolve({ stdout, stderr });
        return;
      }
      reject(new Error(`EAS OTA 发布失败，退出码 ${code}`));
    });
  });
}

async function readMockOutput(file) {
  return readFile(path.resolve(process.cwd(), file), "utf8");
}

async function readRecoveryManifest(file) {
  return JSON.parse(
    await readFile(path.resolve(process.cwd(), file), "utf8"),
  );
}

export async function writeRecoveryManifest(file, manifest) {
  const outputPath = path.resolve(process.cwd(), file);
  await mkdir(path.dirname(outputPath), { recursive: true });
  await writeFile(outputPath, `${JSON.stringify(manifest, null, 2)}\n`, {
    encoding: "utf8",
    mode: 0o600,
  });
  // mode 只在新建时生效；覆盖旧文件后仍必须显式收紧权限。
  await chmod(outputPath, 0o600);
}

function defaultRecoveryManifestPath(releaseBatchId) {
  return path.join(
    ".artifacts",
    "ota-recovery",
    `pos-handheld-${releaseBatchId}.json`,
  );
}

async function main() {
  const options = parsePublishOtaArgs(process.argv.slice(2));
  if (options.help) {
    console.log(HELP_TEXT.trim());
    return;
  }
  await runPublishPosHandheldOtaRelease(options, {
    reportReleaseEventFn: ({ event, config }) =>
      reportReleaseEvent({
        event,
        baseUrl: config.baseUrl,
        token: config.token,
      }),
  });
}

if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))
) {
  main().catch((error) => {
    console.error(error instanceof Error ? error.message : String(error));
    if (error instanceof OtaPublishBatchError) {
      for (const result of error.results) {
        console.error(
          `${result.platform}: ${result.status}${result.error ? ` - ${result.error}` : ""}`,
        );
      }
    }
    process.exitCode = 1;
  });
}
