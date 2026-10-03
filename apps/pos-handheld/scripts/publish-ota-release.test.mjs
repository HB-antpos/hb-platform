import assert from "node:assert/strict";
import { chmod, mkdtemp, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { Readable } from "node:stream";
import test from "node:test";

import {
  APP_OTA_PREFLIGHT_PATH,
  APP_OTA_REGISTER_PATH,
  EAS_CLI_VERSION,
  LEGACY_OTA_REGISTER_PATH,
  OtaPublishBatchError,
  POS_HANDHELD_PRODUCTION_CHANNEL,
  assertReleaseChannelsUnused,
  buildEasChannelListCommand,
  buildEasChannelReadbackCommand,
  buildEasUpdateCommand,
  buildOtaReleasePayload,
  buildPreflightUrl,
  buildRegistrationUrl,
  createReleaseChannel,
  parseAndValidateEasChannelMapping,
  parseAndValidateEasChannelReadback,
  parseEasChannelListOutput,
  parseEasUpdateOutput,
  parsePublishOtaArgs,
  preflightOtaRelease,
  readAccessTokenFromStdin,
  registerOtaRelease,
  runPublishPosHandheldOtaRelease,
  writeRecoveryManifest,
} from "./publish-ota-release.mjs";

const projectId = "123e4567-e89b-42d3-a456-426614174000";
const batchId = "153e4567-e89b-42d3-a456-426614174000";
const iosGroupId = "223e4567-e89b-42d3-a456-426614174000";
const iosUpdateId = "323e4567-e89b-42d3-a456-426614174000";
const androidGroupId = "423e4567-e89b-42d3-a456-426614174000";
const androidUpdateId = "523e4567-e89b-42d3-a456-426614174000";
const rollbackOfReleaseId = "623e4567-e89b-42d3-a456-426614174000";
const otherProjectId = "723e4567-e89b-42d3-a456-426614174000";
const registeredReleaseId = "823e4567-e89b-42d3-a456-426614174000";
const currentRuntimeVersion = "0.1.1";
const administratorAccessToken =
  "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.signature";
const iosReleaseChannel =
  "pos-handheld-production-ios-release-20260827t101500000z-a1b2c3d4";
const androidReleaseChannel =
  "pos-handheld-production-android-release-20260827t101500000z-d4e5f6a7";
const liveEnvironment = Object.freeze({
  EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
  HBPOS_OTA_CENTER_BASE_URL: "https://center.example",
  PERFORMANCE_SERVICE_URL: "https://metrics.example",
  PERFORMANCE_SERVICE_TOKEN: "hbsvc_release_events",
  EXPO_TOKEN: "expo-auth-must-remain",
});
const easEnvironment = Object.freeze({
  ...liveEnvironment,
  HBPOS_OTA_CENTER_ACCESS_TOKEN: administratorAccessToken,
  HBPOS_OTA_ADMIN_JWT: administratorAccessToken,
  HBPOS_OTA_SERVICE_TOKEN: "hbsvc_legacy",
  HBPOS_OTA_RELEASE_SERVICE_TOKEN: "hbsvc_release_writer",
  POS_CENTER_ADMIN_JWT: "must-not-enter-eas",
  POS_CENTER_SERVICE_TOKEN: "must-not-enter-eas",
  APP_OTA_RELEASE_ACCESS_TOKEN: "must-not-enter-eas",
  HBPOS_APP_UPDATE_DECISION_READ_TOKEN: "must-not-enter-eas",
  HBPOS_MANAGEMENT_TOKEN: "must-not-enter-eas",
  HBWEB_API_TOKEN: "must-not-enter-eas",
  expo_token: "must-not-enter-eas",
});

function releaseChannelFor(platform) {
  return platform === "android" ? androidReleaseChannel : iosReleaseChannel;
}

function createJsonOutput(platform = "ios", channel = releaseChannelFor(platform), overrides = {}) {
  const updateId = platform === "android" ? androidUpdateId : iosUpdateId;
  const groupId = platform === "android" ? androidGroupId : iosGroupId;
  return JSON.stringify([
    {
      id: updateId,
      createdAt: "2026-08-27T10:16:00.000Z",
      group: groupId,
      branch: channel,
      runtimeVersion: currentRuntimeVersion,
      platform,
      message: "门店修复",
      manifestPermalink:
        `https://expo.dev/projects/${projectId}/updates/${groupId}`,
      gitCommitHash: "abcdef1234567890",
      ...overrides,
    },
  ]);
}

function createChannelReadbackOutput(
  platform = "ios",
  channel = releaseChannelFor(platform),
  overrides = {},
) {
  const updateId = platform === "android" ? androidUpdateId : iosUpdateId;
  const groupId = platform === "android" ? androidGroupId : iosGroupId;
  return JSON.stringify({
    currentPage: {
      id: "channel-id",
      name: channel,
      isPaused: false,
      branchMapping: JSON.stringify({
        version: 0,
        data: [{ branchId: "branch-id", branchMappingLogic: "true" }],
      }),
      updateBranches: [
        {
          id: "branch-id",
          name: channel,
          updateGroups: [
            [
              {
                id: updateId,
                group: groupId,
                platform,
                runtimeVersion: currentRuntimeVersion,
                message: "门店修复",
                gitCommitHash: "abcdef1234567890",
                manifestPermalink:
                  `https://expo.dev/projects/${projectId}/updates/${groupId}`,
                createdAt: "2026-08-27T10:16:00.000Z",
                ...overrides.update,
              },
            ],
          ],
          ...overrides.branch,
        },
      ],
      ...overrides.channel,
    },
  });
}

function createChannelMappingOutput(
  channel = POS_HANDHELD_PRODUCTION_CHANNEL,
  overrides = {},
) {
  const branch = {
    id: "fixed-branch-id",
    name: channel,
    updateGroups: [],
    ...overrides.branch,
  };
  return JSON.stringify({
    currentPage: {
      id: "fixed-channel-id",
      name: channel,
      isPaused: false,
      branchMapping: JSON.stringify({
        version: 0,
        data: [
          {
            branchId: "fixed-branch-id",
            branchMappingLogic: "true",
          },
        ],
        ...overrides.mapping,
      }),
      updateBranches: overrides.updateBranches ?? [branch],
      ...overrides.channel,
    },
  });
}

function createChannelListOutput(channels) {
  return JSON.stringify({
    currentPage: channels.map((name) => ({ name })),
  });
}

function createOptions(overrides = {}) {
  return {
    runtimeVersion: currentRuntimeVersion,
    message: "门店修复",
    platform: "ios",
    projectId,
    accessTokenStdin: true,
    ...overrides,
  };
}

function createDependencies(overrides = {}) {
  return {
    environment: liveEnvironment,
    logger: { log() {} },
    createReleaseBatchIdFn: () => batchId,
    createReleaseChannelFn: (platform) => releaseChannelFor(platform),
    nowIsoFn: () => "2026-08-27T10:15:00.000Z",
    readAccessTokenStdinFn: async () => administratorAccessToken,
    preflightOtaReleaseFn: async () => ({ valid: true }),
    assertReleaseChannelsUnusedFn: async () => undefined,
    readbackEasReleaseFn: async (lane, parsed) => ({
      channel: lane.releaseChannel,
      branch: parsed.branch,
      updateGroupId: parsed.updateGroupId,
      updateId: parsed.updateId,
      platform: parsed.platform,
      runtimeVersion: parsed.runtimeVersion,
      message: parsed.message,
      gitCommitHash: parsed.gitCommitHash,
      dashboardUrl: parsed.dashboardUrl,
      publishedAtUtc: parsed.publishedAt,
    }),
    registerOtaReleaseFn: async (payload) => ({
      release: { id: `${payload.platform}-release-id`, ...payload },
      idempotent: false,
    }),
    runCommandFn: async (command) => {
      const platform = command.args[command.args.indexOf("--platform") + 1];
      const channel = command.args[command.args.indexOf("--channel") + 1];
      return { stdout: createJsonOutput(platform, channel), stderr: "" };
    },
    writeRecoveryManifestFn: async () => {},
    ...overrides,
  };
}

function createStoredReleaseDto(payload, overrides = {}) {
  const release = {
    id: registeredReleaseId,
    ...payload,
    factFingerprint: "a".repeat(64),
    legacy: false,
    registrationSource: "app-ota-release-api",
    createdAt: "2026-08-27T10:17:00.000Z",
    createdBy: "admin",
    ...overrides,
  };
  delete release.easProjectId;
  return release;
}

function createBootstrapLegacyPayload(overrides = {}) {
  return {
    projectName: "hb-pos-handheld",
    updateGroupId: iosGroupId,
    updateId: iosUpdateId,
    androidUpdateId: null,
    channel: POS_HANDHELD_PRODUCTION_CHANNEL,
    branch: POS_HANDHELD_PRODUCTION_CHANNEL,
    platform: "ios",
    runtimeVersion: currentRuntimeVersion,
    message: "门店修复",
    gitCommitHash: "abcdef1234567890",
    dashboardUrl:
      `https://expo.dev/projects/${projectId}/updates/${iosGroupId}`,
    publishedAt: "2026-08-27T10:16:00.000Z",
    isRollback: false,
    rollbackOfGroupId: null,
    bootstrapLegacyFixedChannel: true,
    ...overrides,
  };
}

function createBootstrapRecoveryManifest(overrides = {}) {
  return {
    schemaVersion: 1,
    mode: "bootstrap-legacy",
    appKey: "pos-handheld",
    environment: "production",
    releaseBatchId: batchId,
    createdAtUtc: "2026-08-27T10:17:00.000Z",
    easProjectId: projectId,
    release: createBootstrapLegacyPayload(),
    ...overrides,
  };
}

test("release channel 由 production、真机平台、时间和熵派生且永不使用 preview", () => {
  assert.equal(
    createReleaseChannel(
      "ios",
      "2026-08-27T10:15:00.000Z",
      "A1B2C3D4-ffff",
    ),
    iosReleaseChannel,
  );
  assert.equal(
    createReleaseChannel(
      "android",
      "2026-08-27T10:15:00.000Z",
      "D4E5F6A7-ffff",
    ),
    androidReleaseChannel,
  );
  assert.notEqual(iosReleaseChannel, androidReleaseChannel);
  assert.equal(iosReleaseChannel.includes("preview"), false);
});

test("EAS 命令固定 CLI、runtime 和唯一平台 channel，并剔除 Center 凭据", () => {
  for (const platform of ["ios", "android"]) {
    const channel = releaseChannelFor(platform);
    const command = buildEasUpdateCommand(
      createOptions({ platform, releaseChannel: channel }),
      easEnvironment,
    );
    assert.deepEqual(command.args, [
      `eas-cli@${EAS_CLI_VERSION}`,
      "update",
      "--channel",
      channel,
      "--platform",
      platform,
      "--message",
      "门店修复",
      "--json",
      "--non-interactive",
    ]);
    assert.equal(command.env.HBPOS_OTA_CENTER_ACCESS_TOKEN, undefined);
    assert.equal(command.env.HBPOS_OTA_ADMIN_JWT, undefined);
    assert.equal(command.env.HBPOS_OTA_SERVICE_TOKEN, undefined);
    assert.equal(command.env.HBPOS_OTA_RELEASE_SERVICE_TOKEN, undefined);
    assert.equal(command.env.PERFORMANCE_SERVICE_URL, undefined);
    assert.equal(command.env.PERFORMANCE_SERVICE_TOKEN, undefined);
    assert.equal(command.env.POS_CENTER_ADMIN_JWT, undefined);
    assert.equal(command.env.POS_CENTER_SERVICE_TOKEN, undefined);
    assert.equal(command.env.APP_OTA_RELEASE_ACCESS_TOKEN, undefined);
    assert.equal(command.env.HBPOS_APP_UPDATE_DECISION_READ_TOKEN, undefined);
    assert.equal(command.env.HBPOS_MANAGEMENT_TOKEN, undefined);
    assert.equal(command.env.HBWEB_API_TOKEN, undefined);
    assert.equal(command.env.expo_token, undefined);
    assert.equal(command.env.EXPO_TOKEN, "expo-auth-must-remain");
    assert.equal(command.env.EXPO_PUBLIC_HBPOS_RUNTIME_VERSION, currentRuntimeVersion);
  }

  assert.throws(
    () =>
      buildEasUpdateCommand(
        createOptions({ releaseChannel: "pos-handheld-preview" }),
        easEnvironment,
      ),
    /release channel/i,
  );
});

test("EAS channel:list 使用固定 CLI、limit/offset 分页并严格解析权威列表", () => {
  const command = buildEasChannelListCommand(
    createOptions({ releaseChannel: iosReleaseChannel }),
    25,
    easEnvironment,
  );
  assert.deepEqual(command.args, [
    `eas-cli@${EAS_CLI_VERSION}`,
    "channel:list",
    "--json",
    "--non-interactive",
    "--limit",
    "25",
    "--offset",
    "25",
  ]);
  assert.equal(command.env.HBPOS_OTA_RELEASE_SERVICE_TOKEN, undefined);
  assert.equal(command.env.HBPOS_APP_UPDATE_DECISION_READ_TOKEN, undefined);
  assert.equal(command.env.HBPOS_MANAGEMENT_TOKEN, undefined);
  assert.equal(command.env.HBWEB_API_TOKEN, undefined);
  assert.equal(command.env.PERFORMANCE_SERVICE_URL, undefined);
  assert.equal(command.env.PERFORMANCE_SERVICE_TOKEN, undefined);
  assert.equal(command.env.expo_token, undefined);
  assert.equal(command.env.EXPO_TOKEN, "expo-auth-must-remain");
  assert.deepEqual(
    parseEasChannelListOutput(
      createChannelListOutput([POS_HANDHELD_PRODUCTION_CHANNEL, "historical"]),
    ),
    [POS_HANDHELD_PRODUCTION_CHANNEL, "historical"],
  );
  assert.throws(
    () => parseEasChannelListOutput("network gateway error"),
    /channel:list/i,
  );
  assert.throws(
    () => parseEasChannelListOutput(JSON.stringify({ currentPage: {} })),
    /channel:list/i,
  );
  assert.throws(
    () => parseEasChannelListOutput(createChannelListOutput(["same", "same"])),
    /duplicate|重复/i,
  );
});

test("EAS publish JSON 按平台解析 branch、runtime、group 与 update，不假设 CLI 回显 channel", () => {
  assert.deepEqual(parseEasUpdateOutput(createJsonOutput("ios"), "ios"), {
    updateGroupId: iosGroupId,
    updateId: iosUpdateId,
    channel: "",
    branch: iosReleaseChannel,
    platform: "ios",
    runtimeVersion: currentRuntimeVersion,
    message: "门店修复",
    gitCommitHash: "abcdef1234567890",
    dashboardUrl: `https://expo.dev/projects/${projectId}/updates/${iosGroupId}`,
    publishedAt: "2026-08-27T10:16:00.000Z",
  });
  assert.equal(parseEasUpdateOutput(createJsonOutput("android"), "ios").updateId, "");
  assert.equal(parseEasUpdateOutput(`Update group ${iosGroupId}`, "ios").updateId, "");
});

test("EAS channel:view 使用同一固定 CLI 并严格回读 channel→branch→update 身份", async (t) => {
  const options = createOptions({ releaseChannel: iosReleaseChannel });
  const command = buildEasChannelReadbackCommand(options, easEnvironment);
  assert.deepEqual(command.args, [
    `eas-cli@${EAS_CLI_VERSION}`,
    "channel:view",
    iosReleaseChannel,
    "--json",
    "--non-interactive",
  ]);
  assert.equal(command.env.HBPOS_OTA_RELEASE_SERVICE_TOKEN, undefined);
  assert.equal(command.env.HBPOS_APP_UPDATE_DECISION_READ_TOKEN, undefined);
  assert.equal(command.env.HBPOS_MANAGEMENT_TOKEN, undefined);
  assert.equal(command.env.HBWEB_API_TOKEN, undefined);
  assert.equal(command.env.expo_token, undefined);

  const lane = {
    platform: "ios",
    releaseChannel: iosReleaseChannel,
  };
  const parsed = parseEasUpdateOutput(createJsonOutput("ios"), "ios");
  assert.deepEqual(
    parseAndValidateEasChannelReadback(
      createChannelReadbackOutput("ios"),
      lane,
      parsed,
    ),
    {
      channel: iosReleaseChannel,
      branch: iosReleaseChannel,
      updateGroupId: iosGroupId,
      updateId: iosUpdateId,
      platform: "ios",
      runtimeVersion: currentRuntimeVersion,
      message: "门店修复",
      gitCommitHash: "abcdef1234567890",
      dashboardUrl: `https://expo.dev/projects/${projectId}/updates/${iosGroupId}`,
      publishedAtUtc: "2026-08-27T10:16:00.000Z",
    },
  );

  const cases = [
    ["channel", { channel: { name: "pos-handheld-preview" } }],
    ["paused", { channel: { isPaused: true } }],
    [
      "mapping-version",
      {
        channel: {
          branchMapping: JSON.stringify({
            version: 1,
            data: [{ branchId: "branch-id", branchMappingLogic: "true" }],
          }),
        },
      },
    ],
    [
      "mapping-logic",
      {
        channel: {
          branchMapping: JSON.stringify({
            version: 0,
            data: [{ branchId: "branch-id", branchMappingLogic: "rollout" }],
          }),
        },
      },
    ],
    ["branch", { branch: { name: "wrong-branch" } }],
    ["update", { update: { id: androidUpdateId } }],
    ["group", { update: { group: androidGroupId } }],
    ["platform", { update: { platform: "android" } }],
    ["runtime", { update: { runtimeVersion: "9.9.9" } }],
    ["runtime-whitespace", { update: { runtimeVersion: ` ${currentRuntimeVersion} ` } }],
    ["message", { update: { message: "tampered" } }],
    ["message-whitespace", { update: { message: " 门店修复 " } }],
    ["commit", { update: { gitCommitHash: "tampered" } }],
    ["dashboard", { update: { manifestPermalink: "https://expo.dev/tampered" } }],
    [
      "dashboard-whitespace",
      {
        update: {
          manifestPermalink:
            ` https://expo.dev/projects/${projectId}/updates/${iosGroupId}`,
        },
      },
    ],
    ["published", { update: { createdAt: "2026-08-27T10:17:00.000Z" } }],
  ];
  for (const [name, overrides] of cases) {
    await t.test(name, () => {
      assert.throws(
        () =>
          parseAndValidateEasChannelReadback(
            createChannelReadbackOutput("ios", iosReleaseChannel, overrides),
            lane,
            parsed,
          ),
        /channel readback/i,
      );
    });
  }

  await t.test("flattened-update-groups", () => {
    const flattened = JSON.parse(createChannelReadbackOutput("ios"));
    flattened.currentPage.updateBranches[0].updateGroups =
      flattened.currentPage.updateBranches[0].updateGroups[0];
    assert.throws(
      () =>
        parseAndValidateEasChannelReadback(
          JSON.stringify(flattened),
          lane,
          parsed,
        ),
      /channel readback/i,
    );
  });
});

test("legacy bootstrap 写入前只证明 fixed channel 到 fixed branch 的唯一 active 映射", async (t) => {
  assert.deepEqual(
    parseAndValidateEasChannelMapping(
      createChannelMappingOutput(),
      POS_HANDHELD_PRODUCTION_CHANNEL,
    ),
    {
      channel: POS_HANDHELD_PRODUCTION_CHANNEL,
      branch: POS_HANDHELD_PRODUCTION_CHANNEL,
      branchId: "fixed-branch-id",
    },
  );

  const cases = [
    ["channel", { channel: { name: "pos-handheld-preview" } }],
    ["paused", { channel: { isPaused: true } }],
    ["mapping-version", { mapping: { version: 1 } }],
    ["mapping-missing", { mapping: { data: [] } }],
    [
      "mapping-ambiguous",
      {
        mapping: {
          data: [
            { branchId: "fixed-branch-id", branchMappingLogic: "true" },
            { branchId: "other-branch-id", branchMappingLogic: "true" },
          ],
        },
      },
    ],
    [
      "mapping-logic",
      {
        mapping: {
          data: [
            {
              branchId: "fixed-branch-id",
              branchMappingLogic: "rollout",
            },
          ],
        },
      },
    ],
    ["branch-id", { branch: { id: "wrong-branch-id" } }],
    ["branch-name", { branch: { name: "pos-handheld-preview" } }],
    [
      "branch-ambiguous",
      {
        updateBranches: [
          {
            id: "fixed-branch-id",
            name: POS_HANDHELD_PRODUCTION_CHANNEL,
            updateGroups: [],
          },
          {
            id: "other-branch-id",
            name: POS_HANDHELD_PRODUCTION_CHANNEL,
            updateGroups: [],
          },
        ],
      },
    ],
  ];
  for (const [name, overrides] of cases) {
    await t.test(name, () => {
      assert.throws(
        () =>
          parseAndValidateEasChannelMapping(
            createChannelMappingOutput(
              POS_HANDHELD_PRODUCTION_CHANNEL,
              overrides,
            ),
            POS_HANDHELD_PRODUCTION_CHANNEL,
          ),
        /channel|mapping|branch|映射/i,
      );
    });
  }
});

test("immutable release payload 精确区分 clientChannel 与 releaseChannel", () => {
  const payload = buildOtaReleasePayload(
    parseEasUpdateOutput(createJsonOutput("ios"), "ios"),
    {
      releaseBatchId: batchId,
      releaseChannel: iosReleaseChannel,
      projectId,
      runtimeVersion: currentRuntimeVersion,
      message: "参数说明",
      platform: "ios",
      rollbackOfReleaseId,
    },
  );

  assert.deepEqual(payload, {
    releaseBatchId: batchId,
    appKey: "pos-handheld",
    environment: "production",
    clientChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
    releaseChannel: iosReleaseChannel,
    easBranch: iosReleaseChannel,
    projectName: "hb-pos-handheld",
    easProjectId: projectId,
    platform: "ios",
    runtimeVersion: currentRuntimeVersion,
    updateGroupId: iosGroupId,
    updateId: iosUpdateId,
    message: "门店修复",
    gitCommitHash: "abcdef1234567890",
    dashboardUrl: `https://expo.dev/projects/${projectId}/updates/${iosGroupId}`,
    publishedAtUtc: "2026-08-27T10:16:00.000Z",
    isRollback: true,
    rollbackOfReleaseId,
  });
  assert.equal("state" in payload, false);
  assert.equal("required" in payload, false);
  assert.equal("channel" in payload, false);
  assert.throws(
    () =>
      buildOtaReleasePayload(
        parseEasUpdateOutput(createJsonOutput("ios"), "ios"),
        {
          releaseBatchId: batchId,
          releaseChannel: iosReleaseChannel,
          runtimeVersion: currentRuntimeVersion,
          message: "参数说明",
          platform: "ios",
        },
      ),
    /EAS projectId/i,
  );
});

test("immutable/recovery dashboardUrl 只接受 null 或 <=2048 的规范 HTTPS URL", () => {
  const parsed = parseEasUpdateOutput(createJsonOutput("ios"), "ios");
  const context = {
    ...createOptions(),
    releaseBatchId: batchId,
    releaseChannel: iosReleaseChannel,
  };
  assert.equal(
    buildOtaReleasePayload(
      { ...parsed, dashboardUrl: null },
      context,
    ).dashboardUrl,
    null,
  );
  assert.equal(
    buildOtaReleasePayload(parsed, context).dashboardUrl,
    `https://expo.dev/projects/${projectId}/updates/${iosGroupId}`,
  );
  for (const dashboardUrl of [
    ` ${parsed.dashboardUrl}`,
    "http://expo.dev/projects/example/updates/example",
    "https://user:password@expo.dev/projects/example/updates/example",
    "https://expo.dev/projects/a b/updates/example",
    `https://expo.dev/${"x".repeat(2_040)}`,
  ]) {
    assert.throws(
      () => buildOtaReleasePayload({ ...parsed, dashboardUrl }, context),
      /dashboardUrl/i,
    );
  }
});

test("CLI 支持 ios|android|all、register-only 与显式 bootstrap，拒绝 preview/environment", () => {
  assert.equal(
    parsePublishOtaArgs([
      "--runtime-version",
      currentRuntimeVersion,
      "--message",
      "门店修复",
    ]).platform,
    "ios",
  );
  assert.equal(
    parsePublishOtaArgs([
      "--runtime-version",
      currentRuntimeVersion,
      "--platform",
      "all",
      "--message",
      "门店修复",
    ]).platform,
    "all",
  );
  assert.equal(
    parsePublishOtaArgs(["--register-only", "recovery.json", "--access-token-stdin"])
      .registerOnlyFile,
    "recovery.json",
  );
  assert.equal(
    parsePublishOtaArgs([
      "--runtime-version",
      currentRuntimeVersion,
      "--message",
      "门店修复",
      "--bootstrap-legacy-fixed-channel",
    ]).bootstrapLegacyFixedChannel,
    true,
  );
  assert.throws(
    () => parsePublishOtaArgs(["--environment", "preview"]),
    /production.*preview|不接受.*environment/i,
  );
  assert.throws(
    () =>
      parsePublishOtaArgs([
        "--runtime-version",
        currentRuntimeVersion,
        "--message",
        "门店修复",
        "--access-token",
        administratorAccessToken,
      ]),
    /禁止使用 --access-token/,
  );
});

test("runtime/message 必须预先 trim 且在 120/1000 长度内，并在凭据或 EAS 前拒绝", async (t) => {
  const cases = [
    ["runtime-leading-space", { runtimeVersion: ` ${currentRuntimeVersion}` }],
    ["runtime-too-long", { runtimeVersion: "r".repeat(121) }],
    ["message-trailing-space", { message: "门店修复 " }],
    ["message-too-long", { message: "m".repeat(1_001) }],
  ];
  for (const [name, overrides] of cases) {
    await t.test(name, async () => {
      let credentialReads = 0;
      let easCalls = 0;
      await assert.rejects(
        () =>
          runPublishPosHandheldOtaRelease(
            createOptions(overrides),
            createDependencies({
              readAccessTokenStdinFn: async () => {
                credentialReads += 1;
                return administratorAccessToken;
              },
              runCommandFn: async () => {
                easCalls += 1;
                throw new Error("不得执行 EAS");
              },
            }),
          ),
        /trim|无效/i,
      );
      assert.equal(credentialReads, 0);
      assert.equal(easCalls, 0);
    });
  }

  await assert.doesNotReject(() =>
    runPublishPosHandheldOtaRelease(
      createOptions({ dryRun: true, message: "m".repeat(1_000) }),
      createDependencies({
        readAccessTokenStdinFn: async () => {
          throw new Error("dry-run 不得读取凭据");
        },
      }),
    ));
});

test("preflight/register 只调用不可变事实接口并严格解包 ApiResponse.data", async () => {
  const calls = [];
  const payload = buildOtaReleasePayload(
    parseEasUpdateOutput(createJsonOutput("ios"), "ios"),
    { ...createOptions(), releaseBatchId: batchId, releaseChannel: iosReleaseChannel },
  );
  const fetchFn = async (url, init) => {
    calls.push({ url, init });
    const isPreflight = url.endsWith(APP_OTA_PREFLIGHT_PATH);
    return {
      ok: true,
      status: 200,
      statusText: "OK",
      text: async () => JSON.stringify({
        success: true,
        data: isPreflight
          ? { valid: true }
          : {
              release: createStoredReleaseDto(payload, {
                releaseBatchId: payload.releaseBatchId.toUpperCase(),
                updateGroupId: payload.updateGroupId.toUpperCase(),
                updateId: payload.updateId.toUpperCase(),
                publishedAtUtc: "2026-08-27T10:16:00+00:00",
              }),
              idempotent: false,
            },
      }),
    };
  };
  const config = {
    baseUrl: "https://center.example/api",
    accessToken: administratorAccessToken,
  };
  const preflight = {
    appKey: "pos-handheld",
    environment: "production",
    clientChannel: POS_HANDHELD_PRODUCTION_CHANNEL,
    releaseChannel: iosReleaseChannel,
    easBranch: iosReleaseChannel,
    projectName: "hb-pos-handheld",
    easProjectId: projectId,
    platform: "ios",
    runtimeVersion: currentRuntimeVersion,
  };
  assert.deepEqual(await preflightOtaRelease(preflight, config, fetchFn), { valid: true });
  assert.deepEqual(await registerOtaRelease(payload, config, fetchFn), {
    release: createStoredReleaseDto(payload, {
      releaseBatchId: payload.releaseBatchId.toUpperCase(),
      updateGroupId: payload.updateGroupId.toUpperCase(),
      updateId: payload.updateId.toUpperCase(),
      publishedAtUtc: "2026-08-27T10:16:00+00:00",
    }),
    idempotent: false,
  });
  assert.deepEqual(calls.map((call) => call.url), [
    "https://center.example/api/app-ota-releases/preflight",
    "https://center.example/api/app-ota-releases/register",
  ]);
  assert.equal(calls.every((call) =>
    call.init.headers.Authorization === `Bearer ${administratorAccessToken}`), true);
  assert.equal(calls.some((call) => call.url.includes(LEGACY_OTA_REGISTER_PATH)), false);
});

test("immutable register 回包的全部不可变事实、UTC 与 source 任一漂移都失败关闭", async (t) => {
  const payload = buildOtaReleasePayload(
    parseEasUpdateOutput(createJsonOutput("ios"), "ios"),
    { ...createOptions(), releaseBatchId: batchId, releaseChannel: iosReleaseChannel },
  );
  const config = {
    baseUrl: "https://center.example",
    accessToken: administratorAccessToken,
  };
  const cases = [
    ["id", "not-a-uuid"],
    ["releaseBatchId", otherProjectId],
    ["appKey", "mobile"],
    ["environment", "preview"],
    ["clientChannel", "preview"],
    ["platform", "android"],
    ["releaseChannel", androidReleaseChannel],
    ["easBranch", androidReleaseChannel],
    ["projectName", "other-project"],
    ["runtimeVersion", "9.9.9"],
    ["updateGroupId", androidGroupId],
    ["updateId", androidUpdateId],
    ["message", "tampered"],
    ["gitCommitHash", "tampered"],
    ["dashboardUrl", null],
    ["publishedAtUtc", "2026-08-27T20:16:00+10:00"],
    ["isRollback", true],
    ["rollbackOfReleaseId", rollbackOfReleaseId],
    ["legacy", true],
    ["registrationSource", "legacy"],
    ["factFingerprint", "not-a-fingerprint"],
  ];
  for (const [field, value] of cases) {
    await t.test(field, async () => {
      const fetchFn = async () => ({
        ok: true,
        status: 200,
        statusText: "OK",
        text: async () => JSON.stringify({
          success: true,
          data: {
            release: createStoredReleaseDto(payload, { [field]: value }),
            idempotent: false,
          },
        }),
      });
      await assert.rejects(
        () => registerOtaRelease(payload, config, fetchFn),
        /identity|\u4e0d\u5339\u914d/i,
      );
    });
  }
});

test("register 回包省略 null 可空字段（后端 WhenWritingNull）视同 null，存在且不同仍失败", async (t) => {
  const basePayload = buildOtaReleasePayload(
    parseEasUpdateOutput(createJsonOutput("android"), "android"),
    { ...createOptions(), platform: "android", releaseBatchId: batchId, releaseChannel: androidReleaseChannel },
  );
  const nullFactsPayload = { ...basePayload, gitCommitHash: null, dashboardUrl: null };
  const rollbackPayload = buildOtaReleasePayload(
    parseEasUpdateOutput(createJsonOutput("android"), "android"),
    {
      ...createOptions(),
      platform: "android",
      releaseBatchId: batchId,
      releaseChannel: androidReleaseChannel,
      rollbackOfReleaseId,
    },
  );
  const config = {
    baseUrl: "https://center.example",
    accessToken: administratorAccessToken,
  };
  // 模拟后端真实序列化：值为 null 的字段整体不出现在 JSON 中。
  const respondWith = (payload, omittedFields, overrides = {}) => {
    const release = createStoredReleaseDto(payload, overrides);
    for (const field of omittedFields) delete release[field];
    return async () => ({
      ok: true,
      status: 200,
      statusText: "OK",
      text: async () => JSON.stringify({
        success: true,
        data: { release, idempotent: false },
      }),
    });
  };

  await t.test("非回滚发布省略 rollbackOfReleaseId 通过", async () => {
    assert.equal(basePayload.rollbackOfReleaseId, null);
    const result = await registerOtaRelease(
      basePayload,
      config,
      respondWith(basePayload, ["rollbackOfReleaseId"]),
    );
    assert.equal(Object.hasOwn(result.release, "rollbackOfReleaseId"), false);
  });

  await t.test("gitCommitHash/dashboardUrl/rollbackOfReleaseId 全部省略通过", async () => {
    await registerOtaRelease(
      nullFactsPayload,
      config,
      respondWith(nullFactsPayload, ["rollbackOfReleaseId", "gitCommitHash", "dashboardUrl"]),
    );
  });

  const mismatches = [
    ["请求为 null、响应存在 rollbackOfReleaseId", basePayload, [], { rollbackOfReleaseId }],
    ["回滚请求但响应省略 rollbackOfReleaseId", rollbackPayload, ["rollbackOfReleaseId"], {}],
    ["回滚请求但响应 rollbackOfReleaseId 不同", rollbackPayload, [], { rollbackOfReleaseId: otherProjectId }],
    ["请求为 null、响应存在 gitCommitHash", nullFactsPayload, ["rollbackOfReleaseId", "dashboardUrl"], { gitCommitHash: "abc123" }],
    ["请求有值但响应省略 gitCommitHash", basePayload, ["rollbackOfReleaseId", "gitCommitHash"], {}],
    ["请求为 null、响应存在 dashboardUrl", nullFactsPayload, ["rollbackOfReleaseId", "gitCommitHash"], { dashboardUrl: "https://expo.dev/x" }],
    ["请求有值但响应省略 dashboardUrl", basePayload, ["rollbackOfReleaseId", "dashboardUrl"], {}],
  ];
  for (const [name, payload, omittedFields, overrides] of mismatches) {
    await t.test(name, async () => {
      await assert.rejects(
        () => registerOtaRelease(payload, config, respondWith(payload, omittedFields, overrides)),
        /identity|不匹配/i,
      );
    });
  }
});

test("live 发布默认在 register 前执行同版本 channel:view 二次回读", async () => {
  const events = [];
  await runPublishPosHandheldOtaRelease(
    createOptions(),
    createDependencies({
      readbackEasReleaseFn: undefined,
      runCommandFn: async (command) => {
        if (command.args[1] === "update") {
          events.push("publish");
          return { stdout: createJsonOutput("ios", iosReleaseChannel), stderr: "" };
        }
        assert.equal(command.args[1], "channel:view");
        events.push("channel-readback");
        return { stdout: createChannelReadbackOutput("ios"), stderr: "" };
      },
      registerOtaReleaseFn: async (payload) => {
        events.push("register");
        return { release: { id: "release-id", ...payload }, idempotent: false };
      },
    }),
  );
  assert.deepEqual(events, ["publish", "channel-readback", "register"]);
});

test("live all 在任何 EAS 前逐 lane preflight，两平台共享 batch 但独立发布登记", async () => {
  const events = [];
  const releaseEvents = [];
  const preflights = [];
  const payloads = [];
  const result = await runPublishPosHandheldOtaRelease(
    createOptions({ platform: "all" }),
    createDependencies({
      preflightOtaReleaseFn: async (payload) => {
        events.push(`preflight:${payload.platform}`);
        preflights.push(payload);
        return { valid: true };
      },
      assertReleaseChannelsUnusedFn: async (lanes) => {
        events.push(`unused:${lanes.map((lane) => lane.platform).join(",")}`);
      },
      runCommandFn: async (command) => {
        const platform = command.args[command.args.indexOf("--platform") + 1];
        const channel = command.args[command.args.indexOf("--channel") + 1];
        events.push(`eas:${platform}`);
        return { stdout: createJsonOutput(platform, channel), stderr: "" };
      },
      readbackEasReleaseFn: async (lane, parsed) => {
        events.push(`readback:${lane.platform}`);
        return {
          channel: lane.releaseChannel,
          branch: parsed.branch,
          updateGroupId: parsed.updateGroupId,
          updateId: parsed.updateId,
          platform: parsed.platform,
          runtimeVersion: parsed.runtimeVersion,
          message: parsed.message,
          gitCommitHash: parsed.gitCommitHash,
          dashboardUrl: parsed.dashboardUrl,
          publishedAtUtc: parsed.publishedAt,
        };
      },
      registerOtaReleaseFn: async (payload) => {
        events.push(`register:${payload.platform}`);
        payloads.push(payload);
        return { release: { id: `${payload.platform}-id` }, idempotent: false };
      },
      completedAtUtcFn: () => "2026-08-27T10:17:00.000Z",
      reportReleaseEventFn: async ({ event }) => {
        events.push(`report:${event.version}`);
        releaseEvents.push(event);
      },
      resolveReleaseCommitFn: () => "a".repeat(40),
    }),
  );

  assert.deepEqual(events, [
    "preflight:ios",
    "preflight:android",
    "unused:ios,android",
    "eas:ios",
    "readback:ios",
    "register:ios",
    `report:${iosGroupId}`,
    "eas:android",
    "readback:android",
    "register:android",
    `report:${androidGroupId}`,
  ]);
  assert.equal(new Set(payloads.map((payload) => payload.releaseBatchId)).size, 1);
  assert.equal(payloads[0].releaseBatchId, batchId);
  assert.deepEqual(payloads.map((payload) => payload.releaseChannel), [
    iosReleaseChannel,
    androidReleaseChannel,
  ]);
  assert.deepEqual(preflights.map((payload) => payload.easBranch), [
    iosReleaseChannel,
    androidReleaseChannel,
  ]);
  assert.equal(payloads.every((payload) => payload.easProjectId === projectId), true);
  assert.equal(
    preflights.some((payload) => Object.hasOwn(payload, "rollbackOfReleaseId")),
    false,
  );
  assert.equal(result.status, "complete");
  assert.equal(result.results.length, 2);
  assert.deepEqual(releaseEvents.map((event) => event.version), [iosGroupId, androidGroupId]);
  assert.notEqual(releaseEvents[0].eventId, releaseEvents[1].eventId);
});

test("所有新 release lane 必须穷尽 channel:list 分页并证明从未使用", async (t) => {
  const lanes = [
    createOptions({ platform: "ios", releaseChannel: iosReleaseChannel }),
    createOptions({ platform: "android", releaseChannel: androidReleaseChannel }),
  ];

  await t.test("完整分页后发现任一目标已存在即拒绝", async () => {
    const pages = [
      Array.from({ length: 25 }, (_, index) => `historical-${index}`),
      [androidReleaseChannel],
    ];
    let calls = 0;
    await assert.rejects(
      () =>
        assertReleaseChannelsUnused(lanes, liveEnvironment, async (command) => {
          assert.equal(command.args[1], "channel:list");
          assert.equal(
            command.args[command.args.indexOf("--offset") + 1],
            String(calls * 25),
          );
          const stdout = createChannelListOutput(pages[calls] ?? []);
          calls += 1;
          return { stdout, stderr: "" };
        }),
      /already exists|已存在/i,
    );
    assert.equal(calls, 2);
  });

  await t.test("跨页重叠视为分页不稳定", async () => {
    const firstPage = [
      "overlap",
      ...Array.from({ length: 24 }, (_, index) => `stable-${index}`),
    ];
    let calls = 0;
    await assert.rejects(
      () =>
        assertReleaseChannelsUnused(lanes, liveEnvironment, async () => {
          const stdout = createChannelListOutput(
            calls === 0 ? firstPage : ["overlap"],
          );
          calls += 1;
          return { stdout, stderr: "" };
        }),
      /pagination.*unstable|分页.*不稳定/i,
    );
    assert.equal(calls, 2);
  });

  await t.test("网络或失形输出不能被解释为 channel 不存在", async () => {
    await assert.rejects(
      () =>
        assertReleaseChannelsUnused(lanes, liveEnvironment, async () => {
          throw new Error("EAS network unavailable");
        }),
      /network unavailable/i,
    );
    await assert.rejects(
      () =>
        assertReleaseChannelsUnused(lanes, liveEnvironment, async () => ({
          stdout: "not-json",
          stderr: "channel not found",
        })),
      /channel:list/i,
    );
  });

  await t.test("连续满页达到安全上限仍失败关闭", async () => {
    let calls = 0;
    await assert.rejects(
      () =>
        assertReleaseChannelsUnused(lanes, liveEnvironment, async () => {
          const page = Array.from(
            { length: 25 },
            (_, index) => `page-${calls}-${index}`,
          );
          calls += 1;
          return { stdout: createChannelListOutput(page), stderr: "" };
        }),
      /pagination limit|分页上限/i,
    );
    assert.equal(calls, 100);
  });

  await t.test("完整短页且两条目标均未出现时证明成功", async () => {
    let calls = 0;
    await assert.doesNotReject(() =>
      assertReleaseChannelsUnused(lanes, liveEnvironment, async () => {
        calls += 1;
        return {
          stdout: createChannelListOutput(["historical-fixed-channel"]),
          stderr: "",
        };
      }));
    assert.equal(calls, 1);
  });
});

test("unused 权威证明失败时两平台均不得执行 EAS update", async () => {
  let updateCalls = 0;
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions({ platform: "all" }),
        createDependencies({
          assertReleaseChannelsUnusedFn: async () => {
            throw new Error("EAS channel:list could not prove unused");
          },
          runCommandFn: async () => {
            updateCalls += 1;
            throw new Error("不得执行 EAS update");
          },
        }),
      ),
    /prove unused/i,
  );
  assert.equal(updateCalls, 0);
});

test("rollback 来源 release ID 在 EAS 写入前进入 preflight 同 lane 验证", async () => {
  let preflightPayload;
  await runPublishPosHandheldOtaRelease(
    createOptions({ rollbackOfReleaseId }),
    createDependencies({
      preflightOtaReleaseFn: async (payload) => {
        preflightPayload = payload;
        return { valid: true };
      },
    }),
  );
  assert.equal(preflightPayload.rollbackOfReleaseId, rollbackOfReleaseId);
});

test("all 一端失败仍完成另一端，整体 non-zero 且登记失败生成无凭据 recovery manifest", async () => {
  const writes = [];
  const registrations = [];
  const reported = [];
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions({ platform: "all", recoveryManifestFile: "recovery.json" }),
        createDependencies({
          registerOtaReleaseFn: async (payload) => {
            registrations.push(payload.platform);
            if (payload.platform === "ios") throw new Error("Center 暂不可用");
            return { release: { id: "android-id" }, idempotent: false };
          },
          reportReleaseEventFn: async ({ event }) => {
            reported.push(event.version);
          },
          resolveReleaseCommitFn: () => "a".repeat(40),
          writeRecoveryManifestFn: async (file, manifest) => {
            writes.push({ file, manifest });
          },
        }),
      ),
    (error) => {
      assert.ok(error instanceof OtaPublishBatchError);
      assert.equal(error.exitCode, 1);
      assert.equal(error.results.filter((item) => item.status === "registered").length, 1);
      assert.equal(error.results.filter((item) => item.status === "registration-failed").length, 1);
      return true;
    },
  );
  assert.deepEqual(registrations, ["ios", "android"]);
  assert.deepEqual(reported, [androidGroupId]);
  assert.equal(writes.length, 1);
  assert.equal(writes[0].file, "recovery.json");
  assert.equal(writes[0].manifest.releases.length, 1);
  assert.equal(writes[0].manifest.releases[0].platform, "ios");
  assert.equal(JSON.stringify(writes[0].manifest).includes(administratorAccessToken), false);
  assert.equal("accessToken" in writes[0].manifest, false);
});

test("all 一端 EAS 失败仍继续另一端发布登记，并整体返回 partial non-zero", async () => {
  const registrations = [];
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions({ platform: "all" }),
        createDependencies({
          runCommandFn: async (command) => {
            const platform = command.args[command.args.indexOf("--platform") + 1];
            const channel = command.args[command.args.indexOf("--channel") + 1];
            if (platform === "ios") throw new Error("iOS EAS failed");
            return { stdout: createJsonOutput(platform, channel), stderr: "" };
          },
          registerOtaReleaseFn: async (payload) => {
            registrations.push(payload.platform);
            return { release: { id: "android-id" }, idempotent: false };
          },
        }),
      ),
    (error) => {
      assert.ok(error instanceof OtaPublishBatchError);
      assert.deepEqual(error.results.map((item) => item.status), [
        "publish-failed",
        "registered",
      ]);
      return true;
    },
  );
  assert.deepEqual(registrations, ["android"]);
});

test("EAS 成功但输出无法验证时标记 published-unverified 并明确禁止重发", async () => {
  let registrations = 0;
  let recoveryWrites = 0;
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions(),
        createDependencies({
          runCommandFn: async () => ({
            stdout: "EAS command returned success without machine-readable facts",
            stderr: "",
          }),
          registerOtaReleaseFn: async () => {
            registrations += 1;
          },
          writeRecoveryManifestFn: async () => {
            recoveryWrites += 1;
          },
        }),
      ),
    (error) => {
      assert.ok(error instanceof OtaPublishBatchError);
      assert.match(error.message, /不得重新发布/);
      assert.equal(error.results[0].status, "published-unverified");
      assert.equal(error.results[0].easCompleted, true);
      assert.equal(error.recoveryManifest, null);
      return true;
    },
  );
  assert.equal(registrations, 0);
  assert.equal(recoveryWrites, 0);
});

test("register-only 不做 backend preflight 或 EAS 发布，但必须重跑 channel:view 才幂等补登记", async () => {
  const manifest = {
    schemaVersion: 1,
    appKey: "pos-handheld",
    environment: "production",
    releaseBatchId: batchId,
    createdAtUtc: "2026-08-27T10:17:00.000Z",
    releases: [
      buildOtaReleasePayload(
        parseEasUpdateOutput(createJsonOutput("ios"), "ios"),
        { ...createOptions(), releaseBatchId: batchId, releaseChannel: iosReleaseChannel },
      ),
    ],
  };
  const events = [];
  const releaseEvents = [];
  const result = await runPublishPosHandheldOtaRelease(
    {
      registerOnlyFile: "recovery.json",
      platform: "all",
      accessTokenStdin: true,
    },
    createDependencies({
      environment: liveEnvironment,
      readbackEasReleaseFn: undefined,
      readRecoveryManifestFn: async (file) => {
        assert.equal(file, "recovery.json");
        return manifest;
      },
      preflightOtaReleaseFn: async () => {
        events.push("preflight");
        throw new Error("register-only 不得执行 preflight");
      },
      assertReleaseChannelsUnusedFn: async () => {
        events.push("unused");
        throw new Error("register-only 不得证明 channel unused");
      },
      registerOtaReleaseFn: async () => {
        events.push("register");
        return { release: { id: "ios-id" }, idempotent: true };
      },
      reportReleaseEventFn: async ({ event }) => {
        events.push("report");
        releaseEvents.push(event);
      },
      resolveReleaseCommitFn: () => "a".repeat(40),
      runCommandFn: async (command) => {
        assert.equal(command.args[1], "channel:view");
        assert.equal(command.args.includes("update"), false);
        events.push("channel-readback");
        return { stdout: createChannelReadbackOutput("ios"), stderr: "" };
      },
    }),
  );
  assert.deepEqual(events, ["channel-readback", "register", "report"]);
  assert.equal(result.results[0].registration.idempotent, true);
  assert.equal(releaseEvents[0].version, iosGroupId);
});

test("bootstrap register-only 只做 fixed channel 权威回读与受限旧登记", async () => {
  const events = [];
  const manifest = createBootstrapRecoveryManifest();
  const result = await runPublishPosHandheldOtaRelease(
    {
      registerOnlyFile: "bootstrap-recovery.json",
      platform: "all",
      accessTokenStdin: true,
    },
    createDependencies({
      readRecoveryManifestFn: async (file) => {
        assert.equal(file, "bootstrap-recovery.json");
        return manifest;
      },
      preflightOtaReleaseFn: async () => {
        events.push("preflight");
        throw new Error("bootstrap register-only 不得 preflight");
      },
      assertReleaseChannelsUnusedFn: async () => {
        events.push("unused");
        throw new Error("bootstrap register-only 不得检查 unused");
      },
      readbackEasReleaseFn: async () => {
        events.push("injected-readback");
        throw new Error("bootstrap register-only 必须使用固定 CLI 回读");
      },
      registerOtaReleaseFn: async () => {
        events.push("immutable-register");
        throw new Error("bootstrap register-only 不得写 AppOtaRelease");
      },
      registerLegacyOtaReleaseFn: async (payload) => {
        events.push("legacy-register");
        assert.deepEqual(payload, createBootstrapLegacyPayload());
        return { updateGroupId: payload.updateGroupId };
      },
      runCommandFn: async (command) => {
        assert.equal(command.args[1], "channel:view");
        assert.equal(command.args[2], POS_HANDHELD_PRODUCTION_CHANNEL);
        assert.equal(command.args.includes("update"), false);
        events.push("channel-view");
        return {
          stdout: createChannelReadbackOutput(
            "ios",
            POS_HANDHELD_PRODUCTION_CHANNEL,
          ),
          stderr: "",
        };
      },
    }),
  );

  assert.deepEqual(events, ["channel-view", "legacy-register"]);
  assert.equal(result.releaseBatchId, batchId);
  assert.equal(result.results[0].mode, "bootstrap-legacy");
  assert.equal(result.results[0].status, "registered");
});

test("bootstrap register-only 严格拒绝凭据、project 漂移和 release 额外字段", async (t) => {
  const cases = [
    [
      "credential",
      { ...createBootstrapRecoveryManifest(), accessToken: administratorAccessToken },
    ],
    [
      "project-drift",
      createBootstrapRecoveryManifest({ easProjectId: otherProjectId }),
    ],
    [
      "release-extra",
      createBootstrapRecoveryManifest({
        release: {
          ...createBootstrapLegacyPayload(),
          unexpected: true,
        },
      }),
    ],
    [
      "branch-drift",
      createBootstrapRecoveryManifest({
        release: createBootstrapLegacyPayload({ branch: "pos-handheld-preview" }),
      }),
    ],
  ];

  for (const [name, manifest] of cases) {
    await t.test(name, async () => {
      const events = [];
      await assert.rejects(
        () =>
          runPublishPosHandheldOtaRelease(
            {
              registerOnlyFile: `${name}.json`,
              platform: "all",
              accessTokenStdin: true,
            },
            createDependencies({
              readRecoveryManifestFn: async () => manifest,
              runCommandFn: async () => {
                events.push("eas");
                throw new Error("不得执行 EAS");
              },
              registerLegacyOtaReleaseFn: async () => {
                events.push("legacy-register");
              },
              registerOtaReleaseFn: async () => {
                events.push("immutable-register");
              },
            }),
          ),
        /recovery|project|identity|legacy|branch|无效|不匹配/i,
      );
      assert.deepEqual(events, []);
    });
  }
});

test("register-only manifest 身份与 channel:view 权威事实不一致时禁止登记", async () => {
  const release = buildOtaReleasePayload(
    parseEasUpdateOutput(createJsonOutput("ios"), "ios"),
    { ...createOptions(), releaseBatchId: batchId, releaseChannel: iosReleaseChannel },
  );
  const manifest = {
    schemaVersion: 1,
    appKey: "pos-handheld",
    environment: "production",
    releaseBatchId: batchId,
    createdAtUtc: "2026-08-27T10:17:00.000Z",
    releases: [{ ...release, updateId: androidUpdateId }],
  };
  let registrations = 0;
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        {
          registerOnlyFile: "tampered-recovery.json",
          platform: "all",
          accessTokenStdin: true,
        },
        createDependencies({
          readbackEasReleaseFn: undefined,
          readRecoveryManifestFn: async () => manifest,
          runCommandFn: async (command) => {
            assert.equal(command.args[1], "channel:view");
            return { stdout: createChannelReadbackOutput("ios"), stderr: "" };
          },
          registerOtaReleaseFn: async () => {
            registrations += 1;
          },
        }),
      ),
    (error) => {
      assert.ok(error instanceof OtaPublishBatchError);
      assert.equal(error.results[0].status, "verification-failed");
      return true;
    },
  );
  assert.equal(registrations, 0);
});

test("register-only recovery 必须精确携带并匹配当前 EAS project identity", async (t) => {
  const release = buildOtaReleasePayload(
    parseEasUpdateOutput(createJsonOutput("ios"), "ios"),
    { ...createOptions(), releaseBatchId: batchId, releaseChannel: iosReleaseChannel },
  );
  const createManifest = (recoveryRelease) => ({
    schemaVersion: 1,
    appKey: "pos-handheld",
    environment: "production",
    releaseBatchId: batchId,
    createdAtUtc: "2026-08-27T10:17:00.000Z",
    releases: [recoveryRelease],
  });

  await t.test("不同 projectId 在权威回读和登记前失败关闭", async () => {
    let readbacks = 0;
    let registrations = 0;
    await assert.rejects(
      () =>
        runPublishPosHandheldOtaRelease(
          {
            registerOnlyFile: "wrong-project.json",
            platform: "all",
            accessTokenStdin: true,
          },
          createDependencies({
            readRecoveryManifestFn: async () =>
              createManifest({ ...release, easProjectId: otherProjectId }),
            readbackEasReleaseFn: async () => {
              readbacks += 1;
            },
            registerOtaReleaseFn: async () => {
              registrations += 1;
            },
          }),
        ),
      (error) => {
        assert.ok(error instanceof OtaPublishBatchError);
        assert.equal(error.results[0].status, "verification-failed");
        assert.match(error.results[0].error, /EAS project/i);
        return true;
      },
    );
    assert.equal(readbacks, 0);
    assert.equal(registrations, 0);
  });

  await t.test("缺少 easProjectId 不符合 recovery exact schema", async () => {
    const { easProjectId: _omitted, ...missingProjectIdentity } = release;
    await assert.rejects(
      () =>
        runPublishPosHandheldOtaRelease(
          {
            registerOnlyFile: "missing-project.json",
            platform: "all",
            accessTokenStdin: true,
          },
          createDependencies({
            readRecoveryManifestFn: async () =>
              createManifest(missingProjectIdentity),
          }),
        ),
      /immutable OTA release payload.*无效/i,
    );
  });

  for (const [name, field, value] of [
    ["runtime-not-trimmed", "runtimeVersion", ` ${currentRuntimeVersion}`],
    ["message-not-trimmed", "message", "门店修复 "],
    ["dashboard-not-canonical", "dashboardUrl", ` ${release.dashboardUrl}`],
  ]) {
    await t.test(name, async () => {
      let readbacks = 0;
      await assert.rejects(
        () =>
          runPublishPosHandheldOtaRelease(
            {
              registerOnlyFile: `${name}.json`,
              platform: "all",
              accessTokenStdin: true,
            },
            createDependencies({
              readRecoveryManifestFn: async () =>
                createManifest({ ...release, [field]: value }),
              readbackEasReleaseFn: async () => {
                readbacks += 1;
              },
            }),
          ),
        /trim|dashboardUrl|runtimeVersion|message/i,
      );
      assert.equal(readbacks, 0);
    });
  }
});

test("register-only 对含凭据或额外字段的 recovery manifest 失败关闭", async () => {
  let writes = 0;
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        {
          registerOnlyFile: "unsafe.json",
          platform: "all",
          accessTokenStdin: true,
        },
        createDependencies({
          readRecoveryManifestFn: async () => ({
            schemaVersion: 1,
            appKey: "pos-handheld",
            environment: "production",
            releaseBatchId: batchId,
            createdAtUtc: "2026-08-27T10:17:00.000Z",
            releases: [],
            accessToken: administratorAccessToken,
          }),
          preflightOtaReleaseFn: async () => {
            writes += 1;
          },
          registerOtaReleaseFn: async () => {
            writes += 1;
          },
        }),
      ),
    /recovery manifest.*无效/i,
  );
  assert.equal(writes, 0);
});

test("bootstrap fixed-channel 必须显式启用、单平台、preflight 后才走 legacy 登记", async () => {
  const events = [];
  const result = await runPublishPosHandheldOtaRelease(
    createOptions({ bootstrapLegacyFixedChannel: true }),
    createDependencies({
      createReleaseChannelFn: () => {
        throw new Error("bootstrap 不应生成 release channel");
      },
      preflightOtaReleaseFn: async (payload) => {
        events.push(`preflight:${payload.releaseChannel}`);
        assert.equal(payload.bootstrapLegacyFixedChannel, true);
        assert.equal(payload.easBranch, POS_HANDHELD_PRODUCTION_CHANNEL);
        return { valid: true };
      },
      assertReleaseChannelsUnusedFn: async () => {
        events.push("unused");
        throw new Error("legacy fixed channel 已使用，不得要求 unused");
      },
      runCommandFn: async (command) => {
        if (command.args[1] === "channel:view") {
          events.push("mapping:fixed");
          assert.equal(command.args[2], POS_HANDHELD_PRODUCTION_CHANNEL);
          return { stdout: createChannelMappingOutput(), stderr: "" };
        }
        const channel = command.args[command.args.indexOf("--channel") + 1];
        events.push(`eas:${channel}`);
        return { stdout: createJsonOutput("ios", channel), stderr: "" };
      },
      registerLegacyOtaReleaseFn: async (payload) => {
        events.push(`legacy-register:${payload.channel}`);
        assert.equal(payload.bootstrapLegacyFixedChannel, true);
        return { updateGroupId: payload.updateGroupId };
      },
    }),
  );
  assert.deepEqual(events, [
    `preflight:${POS_HANDHELD_PRODUCTION_CHANNEL}`,
    "mapping:fixed",
    `eas:${POS_HANDHELD_PRODUCTION_CHANNEL}`,
    `legacy-register:${POS_HANDHELD_PRODUCTION_CHANNEL}`,
  ]);
  assert.equal(result.results[0].mode, "bootstrap-legacy");

  let credentialReads = 0;
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions({ platform: "all", bootstrapLegacyFixedChannel: true }),
        createDependencies({
          environment: {},
          readAccessTokenStdinFn: async () => {
            credentialReads += 1;
            return administratorAccessToken;
          },
        }),
      ),
    /bootstrap.*all|单平台/i,
  );
  assert.equal(credentialReads, 0);
});

test("legacy bootstrap mapping 权威证明失败时零 EAS 写入", async (t) => {
  const cases = [
    [
      "branch-drift",
      async () => ({
        stdout: createChannelMappingOutput(
          POS_HANDHELD_PRODUCTION_CHANNEL,
          { branch: { name: "pos-handheld-preview" } },
        ),
        stderr: "",
      }),
    ],
    [
      "paused",
      async () => ({
        stdout: createChannelMappingOutput(
          POS_HANDHELD_PRODUCTION_CHANNEL,
          { channel: { isPaused: true } },
        ),
        stderr: "",
      }),
    ],
    [
      "network",
      async () => {
        throw new Error("EAS channel:view unavailable");
      },
    ],
  ];
  for (const [name, channelViewResult] of cases) {
    await t.test(name, async () => {
      const events = [];
      let updateCalls = 0;
      await assert.rejects(
        () =>
          runPublishPosHandheldOtaRelease(
            createOptions({ bootstrapLegacyFixedChannel: true }),
            createDependencies({
              preflightOtaReleaseFn: async () => {
                events.push("preflight");
                return { valid: true };
              },
              runCommandFn: async (command) => {
                if (command.args[1] === "channel:view") {
                  events.push("mapping");
                  return channelViewResult();
                }
                updateCalls += 1;
                throw new Error("不得执行 EAS update");
              },
            }),
          ),
        /channel|mapping|branch|unavailable|映射/i,
      );
      assert.deepEqual(events, ["preflight", "mapping"]);
      assert.equal(updateCalls, 0);
    });
  }
});

test("bootstrap EAS 成功但 legacy 登记失败时写无凭据 recovery manifest", async () => {
  const writes = [];
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions({
          bootstrapLegacyFixedChannel: true,
          recoveryManifestFile: "bootstrap-recovery.json",
        }),
        createDependencies({
          runCommandFn: async (command) =>
            command.args[1] === "channel:view"
              ? { stdout: createChannelMappingOutput(), stderr: "" }
              : {
                  stdout: createJsonOutput(
                    "ios",
                    POS_HANDHELD_PRODUCTION_CHANNEL,
                  ),
                  stderr: "",
                },
          registerLegacyOtaReleaseFn: async () => {
            throw new Error("legacy center unavailable");
          },
          writeRecoveryManifestFn: async (file, manifest) => {
            writes.push({ file, manifest });
          },
        }),
      ),
    (error) => {
      assert.ok(error instanceof OtaPublishBatchError);
      assert.match(error.message, /不得重新发布/);
      assert.match(error.message, /register-only/i);
      assert.equal(error.results[0].status, "registration-failed");
      assert.equal(error.results[0].mode, "bootstrap-legacy");
      assert.equal(error.results[0].payload.channel, POS_HANDHELD_PRODUCTION_CHANNEL);
      assert.equal(error.recoveryManifest.mode, "bootstrap-legacy");
      return true;
    },
  );
  assert.equal(writes.length, 1);
  assert.equal(writes[0].file, "bootstrap-recovery.json");
  assert.deepEqual(Object.keys(writes[0].manifest).sort(), [
    "appKey",
    "createdAtUtc",
    "easProjectId",
    "environment",
    "mode",
    "release",
    "releaseBatchId",
    "schemaVersion",
  ]);
  assert.equal(writes[0].manifest.mode, "bootstrap-legacy");
  assert.equal(writes[0].manifest.easProjectId, projectId);
  assert.deepEqual(writes[0].manifest.release, createBootstrapLegacyPayload());
  assert.equal(
    JSON.stringify(writes[0].manifest).includes(administratorAccessToken),
    false,
  );
});

test("恢复 manifest 写入失败时保留可恢复事实并禁止盲目重发", async () => {
  const logs = [];
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions({ recoveryManifestFile: "read-only/recovery.json" }),
        createDependencies({
          logger: { log(message) { logs.push(message); } },
          registerOtaReleaseFn: async () => {
            throw new Error("Center unavailable");
          },
          writeRecoveryManifestFn: async () => {
            throw new Error("read-only filesystem");
          },
        }),
      ),
    (error) => {
      assert.ok(error instanceof OtaPublishBatchError);
      assert.match(error.message, /manifest.*写入失败.*不得重新发布/i);
      assert.equal(error.results[0].status, "registration-failed");
      assert.equal(error.recoveryManifest.releases[0].updateId, iosUpdateId);
      return true;
    },
  );
  assert.equal(logs.some((line) => String(line).includes(iosUpdateId)), true);
  assert.equal(logs.some((line) => String(line).includes(administratorAccessToken)), false);
});

test("recovery manifest 即使覆盖既有文件也强制为 0600", async (t) => {
  const directory = await mkdtemp(path.join(tmpdir(), "hbpos-ota-recovery-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const file = path.join(directory, "bootstrap.json");
  await writeFile(file, "stale\n", { encoding: "utf8", mode: 0o644 });
  await chmod(file, 0o644);

  await writeRecoveryManifest(file, createBootstrapRecoveryManifest());

  const metadata = await stat(file);
  assert.equal(metadata.mode & 0o777, 0o600);
});

test("EAS publish JSON platform/runtime/branch 或关键身份不匹配时禁止登记", async (t) => {
  const cases = [
    ["branch", { branch: "wrong-branch" }],
    ["runtimeVersion", { runtimeVersion: "9.9.9" }],
    ["platform", { platform: "android" }],
    ["message", { message: "tampered" }],
    ["updateId", { id: "" }],
  ];
  for (const [name, overrides] of cases) {
    await t.test(name, async () => {
      let registrations = 0;
      await assert.rejects(
        () =>
          runPublishPosHandheldOtaRelease(
            createOptions(),
            createDependencies({
              runCommandFn: async () => ({
                stdout: createJsonOutput("ios", iosReleaseChannel, overrides),
                stderr: "",
              }),
              registerOtaReleaseFn: async () => {
                registrations += 1;
              },
            }),
          ),
        OtaPublishBatchError,
      );
      assert.equal(registrations, 0);
    });
  }
});

test("dry-run 不读 token、不触网、不执行 EAS，并展示各平台唯一 channel", async () => {
  let commands = 0;
  let writes = 0;
  const logs = [];
  const result = await runPublishPosHandheldOtaRelease(
    createOptions({ platform: "all", dryRun: true, accessTokenStdin: true }),
    createDependencies({
      environment: { EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId },
      logger: { log: (line) => logs.push(String(line)) },
      readAccessTokenStdinFn: async () => {
        throw new Error("dry-run 不得读取 stdin");
      },
      preflightOtaReleaseFn: async () => {
        writes += 1;
      },
      registerOtaReleaseFn: async () => {
        writes += 1;
      },
      runCommandFn: async () => {
        commands += 1;
      },
    }),
  );
  assert.equal(result.dryRun, true);
  assert.equal(commands, 0);
  assert.equal(writes, 0);
  assert.equal(logs.some((line) => line.includes(iosReleaseChannel)), true);
  assert.equal(logs.some((line) => line.includes(androidReleaseChannel)), true);
  assert.equal(logs.join("\n").includes(administratorAccessToken), false);
});

test("管理员 JWT 只允许 stdin；专用发布 service token 可从独立环境变量读取", async () => {
  const tty = Readable.from([]);
  tty.isTTY = true;
  await assert.rejects(() => readAccessTokenFromStdin(tty), /TTY/);
  assert.equal(
    await readAccessTokenFromStdin(Readable.from([`${administratorAccessToken}\n`])),
    administratorAccessToken,
  );
  await assert.rejects(
    () => readAccessTokenFromStdin(Readable.from(["x".repeat(4_097)])),
    /4096/,
  );

  for (const environment of [
    {
      EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
      HBPOS_OTA_CENTER_BASE_URL: "https://center.example",
      HBPOS_OTA_CENTER_ACCESS_TOKEN: administratorAccessToken,
    },
    {
      EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
      HBPOS_OTA_CENTER_BASE_URL: "http://center.example",
    },
  ]) {
    let commands = 0;
    await assert.rejects(
      () =>
        runPublishPosHandheldOtaRelease(createOptions(), createDependencies({
          environment,
          runCommandFn: async () => {
            commands += 1;
          },
        })),
      /stdin|环境变量|HTTPS|loopback/i,
    );
    assert.equal(commands, 0);
  }

  let serviceTokenCommands = 0;
  await runPublishPosHandheldOtaRelease(
    createOptions({ accessTokenStdin: false }),
    createDependencies({
      environment: {
        EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
        HBPOS_OTA_CENTER_BASE_URL: "https://center.example",
        HBPOS_OTA_RELEASE_SERVICE_TOKEN: "hbsvc_release_writer",
      },
      runCommandFn: async (command) => {
        serviceTokenCommands += 1;
        assert.equal(command.env.HBPOS_OTA_RELEASE_SERVICE_TOKEN, undefined);
        return { stdout: createJsonOutput("ios", iosReleaseChannel), stderr: "" };
      },
    }),
  );
  assert.equal(serviceTokenCommands, 1);
});

test("URL builder 对 /api 基址去重，且默认登记路由不再指向旧通用 upsert", () => {
  assert.equal(
    buildPreflightUrl("https://center.example/api"),
    "https://center.example/api/app-ota-releases/preflight",
  );
  assert.equal(
    buildRegistrationUrl("https://center.example"),
    "https://center.example/api/app-ota-releases/register",
  );
  assert.equal(buildRegistrationUrl("https://center.example").includes(LEGACY_OTA_REGISTER_PATH), false);
  assert.throws(() => buildRegistrationUrl("http://center.example"), /HTTPS|loopback/);
  assert.equal(APP_OTA_REGISTER_PATH, "/api/app-ota-releases/register");
});

test("手持 OTA 只在登记成功后上报 deploy 或 rollback 验收", async () => {
  for (const scenario of [
    { action: "deploy", rollbackOfReleaseId: undefined },
    { action: "rollback", rollbackOfReleaseId },
  ]) {
    const trace = [];
    await runPublishPosHandheldOtaRelease(
      createOptions({ rollbackOfReleaseId: scenario.rollbackOfReleaseId }),
      createDependencies({
        completedAtUtcFn: () => "2026-08-27T10:17:00.000Z",
        registerOtaReleaseFn: async (payload) => {
          trace.push("register");
          return {
            release: { id: registeredReleaseId },
            idempotent: false,
            payload,
          };
        },
        reportReleaseEventFn: async ({ event, config }) => {
          trace.push("report");
          assert.equal(event.action, scenario.action);
          assert.equal(event.status, "accepted");
          assert.equal(event.component, "pos-handheld");
          assert.equal(event.environment, "Production");
          assert.equal(event.version, iosGroupId);
          assert.equal(event.commit, "a".repeat(40));
          assert.equal(config.baseUrl, "https://metrics.example");
          assert.equal(config.token, "hbsvc_release_events");
        },
        resolveReleaseCommitFn: () => "a".repeat(40),
      }),
    );
    assert.deepEqual(trace, ["register", "report"]);
  }
});

test("手持 OTA 验收上报失败写 recovery 且明确禁止重发", async () => {
  let recoveryManifest;
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions({ recoveryManifestFile: "release-report-recovery.json" }),
        createDependencies({
          completedAtUtcFn: () => "2026-08-27T10:17:00.000Z",
          reportReleaseEventFn: async () => {
            throw new Error("release reporter unavailable");
          },
          resolveReleaseCommitFn: () => "b".repeat(40),
          writeRecoveryManifestFn: async (file, manifest) => {
            assert.equal(file, "release-report-recovery.json");
            recoveryManifest = manifest;
          },
        }),
      ),
    (error) => {
      assert.ok(error instanceof OtaPublishBatchError);
      assert.match(error.message, /release event|禁止重发/i);
      assert.equal(error.results[0].status, "registered");
      assert.equal(error.results[0].releaseEventStatus, "failed");
      return true;
    },
  );
  assert.equal(recoveryManifest.releases[0].updateGroupId, iosGroupId);
});

test("手持 OTA 缺少性能上报配置时在任何凭据读取或 EAS 写入前失败", async () => {
  const {
    PERFORMANCE_SERVICE_URL: _url,
    PERFORMANCE_SERVICE_TOKEN: _token,
    ...environmentWithoutReporter
  } = liveEnvironment;
  let credentialReads = 0;
  let commandCalls = 0;
  await assert.rejects(
    () =>
      runPublishPosHandheldOtaRelease(
        createOptions(),
        createDependencies({
          environment: environmentWithoutReporter,
          readAccessTokenStdinFn: async () => {
            credentialReads += 1;
            return administratorAccessToken;
          },
          reportReleaseEventFn: async () => undefined,
          runCommandFn: async () => {
            commandCalls += 1;
            throw new Error("must not execute EAS");
          },
        }),
      ),
    /PERFORMANCE_SERVICE_URL.*PERFORMANCE_SERVICE_TOKEN/i,
  );
  assert.equal(credentialReads, 0);
  assert.equal(commandCalls, 0);
});
