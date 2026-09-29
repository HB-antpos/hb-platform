import assert from "node:assert/strict";
import test from "node:test";

import {
  ExpoOtaUpdatePort,
  type ExpoOtaUpdateApplyResult,
} from "./expo-ota-update-port";

import type { PosHandheldOtaUpdatePolicy } from "@/core/contracts/ota-app-updates";

const policy: PosHandheldOtaUpdatePolicy = Object.freeze({
  state: "required",
  policyVersion: "policy-42",
  appKey: "pos-handheld",
  projectName: "hb-pos-handheld",
  platform: "iOS",
  required: true,
  channel: "store-s001",
  runtimeVersion: "1.2.3",
  updateId: "123e4567-e89b-42d3-a456-426614174000",
  updateGroupId: "223e4567-e89b-42d3-a456-426614174000",
  releaseMessage: "必须更新。",
});

test("Updates 启用时启动即 best-effort 清除持久化 channel，禁用或清理异常不崩启动", () => {
  const cleared: (Record<string, string> | null)[] = [];
  assert.doesNotThrow(
    () =>
      new ExpoOtaUpdatePort({
        enabled: true,
        runtimeVersion: "1.2.3",
        updates: {
          setUpdateRequestHeadersOverride(headers) {
            cleared.push(headers);
          },
          async checkForUpdateAsync() {
            return { isAvailable: false };
          },
          async fetchUpdateAsync() {
            return { isNew: false };
          },
          async reloadAsync() {},
        },
      }),
  );
  assert.deepEqual(cleared, [null]);

  for (const enabled of [false, true]) {
    assert.doesNotThrow(
      () =>
        new ExpoOtaUpdatePort({
          enabled,
          runtimeVersion: "1.2.3",
          updates: {
            setUpdateRequestHeadersOverride() {
              if (enabled) throw new Error("native override unavailable");
              throw new Error("disabled must not call override");
            },
            async checkForUpdateAsync() {
              return { isAvailable: false };
            },
            async fetchUpdateAsync() {
              return { isNew: false };
            },
            async reloadAsync() {},
          },
        }),
    );
  }
});

test("命中策略后才覆盖 channel，且 check/fetch 双重验证 runtime/updateId 后 reload", async () => {
  const trace: string[] = [];
  const manifest = {
    id: policy.updateId,
    runtimeVersion: policy.runtimeVersion,
    metadata: { updateGroupId: policy.updateGroupId },
  };
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: "1.2.3",
    updates: {
      setUpdateRequestHeadersOverride(headers) {
        trace.push(
          headers
            ? `override:${headers["expo-channel-name"]}`
            : "override:clear",
        );
      },
      async checkForUpdateAsync() {
        trace.push("check");
        return { isAvailable: true, manifest };
      },
      async fetchUpdateAsync() {
        trace.push("fetch");
        return { isNew: true, manifest };
      },
      async reloadAsync() {
        trace.push("reload");
      },
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "reloaded",
    reason: null,
  });
  assert.deepEqual(trace, [
    "override:clear",
    "override:store-s001",
    "check",
    "fetch",
    "override:clear",
    "override:store-s001",
    "reload",
  ]);
});

test("channel setter 部分持久化后抛错时仍清理并 fail-closed", async () => {
  const trace: string[] = [];
  let persistedChannel: string | null = null;
  let checks = 0;
  let reloads = 0;
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: {
      setUpdateRequestHeadersOverride(headers) {
        const channel = headers?.["expo-channel-name"] ?? null;
        persistedChannel = channel;
        trace.push(channel === null ? "clear" : `set:${channel}`);
        if (channel !== null) {
          throw new Error("native setter persisted before throwing");
        }
      },
      async checkForUpdateAsync() {
        checks += 1;
        return { isAvailable: false };
      },
      async fetchUpdateAsync() {
        throw new Error("must not fetch");
      },
      async reloadAsync() {
        reloads += 1;
      },
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "rejected",
    reason: "channel-override-failed",
  });
  assert.deepEqual(trace, [
    "clear",
    `set:${policy.channel}`,
    "clear",
  ]);
  assert.equal(persistedChannel, null);
  assert.equal(checks, 0);
  assert.equal(reloads, 0);
});

test("channel 清理抛错时返回稳定失败且绝不 reload", async () => {
  let overrideSet = false;
  let clearCalls = 0;
  let reloads = 0;
  const manifest = {
    id: policy.updateId,
    runtimeVersion: policy.runtimeVersion,
  };
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: {
      setUpdateRequestHeadersOverride(headers) {
        if (headers) {
          overrideSet = true;
          return;
        }
        clearCalls += 1;
        if (overrideSet) {
          throw new Error("native clear failed");
        }
      },
      async checkForUpdateAsync() {
        return { isAvailable: true, manifest };
      },
      async fetchUpdateAsync() {
        return { isNew: true, manifest };
      },
      async reloadAsync() {
        reloads += 1;
      },
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "rejected",
    reason: "channel-clear-failed",
  });
  assert.equal(clearCalls, 2);
  assert.equal(reloads, 0);
});

test("检查与 channel 清理同时抛错时仍以清理失败优先并绝不 reload", async () => {
  let overrideSet = false;
  let reloads = 0;
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: {
      setUpdateRequestHeadersOverride(headers) {
        if (headers) {
          overrideSet = true;
          return;
        }
        if (overrideSet) {
          throw new Error("native clear failed");
        }
      },
      async checkForUpdateAsync() {
        throw new Error("native check failed");
      },
      async fetchUpdateAsync() {
        throw new Error("must not fetch");
      },
      async reloadAsync() {
        reloads += 1;
      },
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "rejected",
    reason: "channel-clear-failed",
  });
  assert.equal(reloads, 0);
});

test("runtime 或 updateId 不匹配时 fail-closed，清理 override 且绝不 fetch/reload", async () => {
  for (const [runtimeVersion, manifest] of [
    [
      "9.9.9",
      {
        id: policy.updateId,
        runtimeVersion: "9.9.9",
        metadata: { updateGroupId: policy.updateGroupId },
      },
    ],
    [
      "1.2.3",
      {
        id: "323e4567-e89b-42d3-a456-426614174000",
        runtimeVersion: "1.2.3",
        metadata: { updateGroupId: policy.updateGroupId },
      },
    ],
  ] as const) {
    const trace: string[] = [];
    const port = new ExpoOtaUpdatePort({
      enabled: true,
      runtimeVersion,
      updates: {
        setUpdateRequestHeadersOverride(headers) {
          trace.push(headers ? "override:set" : "override:clear");
        },
        async checkForUpdateAsync() {
          trace.push("check");
          return { isAvailable: true, manifest };
        },
        async fetchUpdateAsync() {
          trace.push("fetch");
          return { isNew: true, manifest };
        },
        async reloadAsync() {
          trace.push("reload");
        },
      },
    });

    const result = await port.apply(policy);
    assert.equal(result.state, "rejected");
    assert.ok(
      result.reason === "runtime-mismatch" ||
        result.reason === "update-id-mismatch",
    );
    assert.equal(trace.includes("fetch"), false);
    assert.equal(trace.includes("reload"), false);
    if (trace.includes("override:set")) {
      assert.equal(trace.at(-1), "override:clear");
    }
  }
});

test("Expo manifest 未承诺的 group metadata 缺失或变化不影响 runtime/updateId 双校验", async () => {
  const groupCandidates = [
    undefined,
    "not-a-uuid",
    "323e4567-e89b-42d3-a456-426614174000",
  ] as const;

  for (const updateGroupId of groupCandidates) {
    let reloads = 0;
    const manifest = {
      id: policy.updateId,
      runtimeVersion: policy.runtimeVersion,
      ...(updateGroupId === undefined
        ? {}
        : { metadata: { updateGroupId } }),
    };
    const port: ExpoOtaUpdatePort = new ExpoOtaUpdatePort({
      enabled: true,
      runtimeVersion: "1.2.3",
      updates: {
        setUpdateRequestHeadersOverride() {},
        async checkForUpdateAsync(): Promise<{
          isAvailable: boolean;
          manifest: typeof manifest;
        }> {
          return {
            isAvailable: true,
            manifest,
          };
        },
        async fetchUpdateAsync(): Promise<{
          isNew: boolean;
          manifest: typeof manifest;
        }> {
          return { isNew: true, manifest };
        },
        async reloadAsync() {
          reloads += 1;
        },
      },
    });

    const result: ExpoOtaUpdateApplyResult = await port.apply(policy);
    assert.deepEqual(result, { state: "reloaded", reason: null });
    assert.equal(reloads, 1);
  }
});

test("expo-updates 未启用或服务器无匹配更新时不覆盖错误 channel、不 reload", async () => {
  let checks = 0;
  let reloads = 0;
  const disabled = new ExpoOtaUpdatePort({
    enabled: false,
    runtimeVersion: "1.2.3",
    updates: {
      setUpdateRequestHeadersOverride() {
        throw new Error("must not override");
      },
      async checkForUpdateAsync() {
        checks += 1;
        return { isAvailable: false };
      },
      async fetchUpdateAsync() {
        throw new Error("must not fetch");
      },
      async reloadAsync() {
        reloads += 1;
      },
    },
  });
  assert.deepEqual(await disabled.apply(policy), {
    state: "unavailable",
    reason: "updates-disabled",
  });

  const unavailable = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: "1.2.3",
    updates: {
      setUpdateRequestHeadersOverride() {},
      async checkForUpdateAsync() {
        checks += 1;
        return { isAvailable: false };
      },
      async fetchUpdateAsync() {
        throw new Error("must not fetch");
      },
      async reloadAsync() {
        reloads += 1;
      },
    },
  });
  assert.deepEqual(await unavailable.apply(policy), {
    state: "unavailable",
    reason: "not-available",
  });
  assert.equal(checks, 1);
  assert.equal(reloads, 0);
});

test("fetch 完成后 reload 前复核失败时清理 channel 且绝不调用 reload", async () => {
  const trace: string[] = [];
  const manifest = {
    id: policy.updateId,
    runtimeVersion: policy.runtimeVersion,
  };
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: {
      setUpdateRequestHeadersOverride(headers) {
        trace.push(headers ? "override:set" : "override:clear");
      },
      async checkForUpdateAsync() {
        trace.push("check");
        return { isAvailable: true, manifest };
      },
      async fetchUpdateAsync() {
        trace.push("fetch");
        return { isNew: true, manifest };
      },
      async reloadAsync() {
        trace.push("reload");
      },
    },
  });

  assert.deepEqual(
    await port.apply(policy, () => "restart-unsafe"),
    {
      state: "rejected",
      reason: "restart-unsafe",
    },
  );
  assert.deepEqual(trace, [
    "override:clear",
    "override:set",
    "check",
    "fetch",
    "override:clear",
  ]);
});

test("连续应用不同 release channel 时每次都独立覆盖并清理，不泄漏前一策略", async () => {
  const first = {
    ...policy,
    policyVersion: "policy-first",
    channel: "pos-handheld-release-20260730-a",
  };
  const second = {
    ...policy,
    policyVersion: "policy-second",
    channel: "pos-handheld-release-20260730-b",
    updateId: "323e4567-e89b-42d3-a456-426614174000",
  };
  let activeChannel: string | null = null;
  const trace: string[] = [];
  const updatesByChannel = new Map([
    [first.channel, first],
    [second.channel, second],
  ]);
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: "1.2.3",
    updates: {
      setUpdateRequestHeadersOverride(headers) {
        activeChannel = headers?.["expo-channel-name"] ?? null;
        trace.push(activeChannel ? `set:${activeChannel}` : "clear");
      },
      async checkForUpdateAsync() {
        const selected = activeChannel
          ? updatesByChannel.get(activeChannel)
          : undefined;
        return {
          isAvailable: selected !== undefined,
          manifest: selected
              ? {
                id: selected.updateId,
                runtimeVersion: selected.runtimeVersion,
                metadata: {
                  updateGroupId: selected.updateGroupId,
                },
              }
            : undefined,
        };
      },
      async fetchUpdateAsync() {
        const selected = activeChannel
          ? updatesByChannel.get(activeChannel)
          : undefined;
        return {
          isNew: selected !== undefined,
          manifest: selected
              ? {
                id: selected.updateId,
                runtimeVersion: selected.runtimeVersion,
                metadata: {
                  updateGroupId: selected.updateGroupId,
                },
              }
            : undefined,
        };
      },
      async reloadAsync() {
        trace.push("reload");
      },
    },
  });

  assert.equal((await port.apply(first)).state, "reloaded");
  assert.equal(activeChannel, first.channel);
  assert.equal((await port.apply(second)).state, "reloaded");
  assert.equal(activeChannel, second.channel);
  assert.deepEqual(trace, [
    "clear",
    `set:${first.channel}`,
    "clear",
    `set:${first.channel}`,
    "reload",
    `set:${second.channel}`,
    "clear",
    `set:${second.channel}`,
    "reload",
  ]);
});

test("prepare 只下载并校验，不 reload；apply 复用已准备目标", async () => {
  let fetches = 0;
  let reloads = 0;
  const manifest = { id: policy.updateId, runtimeVersion: policy.runtimeVersion };
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: {
      setUpdateRequestHeadersOverride() {},
      async checkForUpdateAsync() { return { isAvailable: true, manifest }; },
      async fetchUpdateAsync() {
        fetches += 1;
        return { isNew: true, manifest };
      },
      async reloadAsync() { reloads += 1; },
    },
  });

  assert.deepEqual(await port.prepare(policy), { state: "ready", reason: null });
  assert.equal(fetches, 1);
  assert.equal(reloads, 0);
  assert.deepEqual(await port.apply(policy), { state: "reloaded", reason: null });
  assert.equal(fetches, 1);
  assert.equal(reloads, 1);
});

test("策略改变会使旧 ready 失效，不能借用旧目标 reload", async () => {
  let fetches = 0;
  let reloads = 0;
  let activeChannel: string | null = null;
  const first = { ...policy, channel: "channel-a" };
  const second = { ...policy, channel: "channel-b", updateId: "323e4567-e89b-42d3-a456-426614174000" };
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: {
      setUpdateRequestHeadersOverride(headers) { activeChannel = headers?.["expo-channel-name"] ?? null; },
      async checkForUpdateAsync() { return { isAvailable: true, manifest: { id: activeChannel === second.channel ? second.updateId : first.updateId, runtimeVersion: policy.runtimeVersion } }; },
      async fetchUpdateAsync() { fetches += 1; return { isNew: true, manifest: { id: activeChannel === second.channel ? second.updateId : first.updateId, runtimeVersion: policy.runtimeVersion } }; },
      async reloadAsync() { reloads += 1; },
    },
  });

  await port.prepare(first);
  await port.prepare(second);
  assert.equal(fetches, 2);
  await port.apply(second);
  assert.equal(reloads, 1);
});

test("reload 失败会恢复 channel，且下一次 apply 重新下载并可重试", async () => {
  let fetches = 0;
  let reloads = 0;
  let failReload = true;
  let activeChannel: string | null = null;
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: {
      setUpdateRequestHeadersOverride(headers) { activeChannel = headers?.["expo-channel-name"] ?? null; },
      async checkForUpdateAsync() { return { isAvailable: true, manifest: { id: policy.updateId, runtimeVersion: policy.runtimeVersion } }; },
      async fetchUpdateAsync() { fetches += 1; return { isNew: true, manifest: { id: policy.updateId, runtimeVersion: policy.runtimeVersion } }; },
      async reloadAsync() { reloads += 1; if (failReload) throw new Error("reload failed"); },
    },
  });

  await port.prepare(policy);
  await assert.rejects(port.apply(policy), /reload failed/u);
  assert.equal(activeChannel, null);
  failReload = false;
  await port.apply(policy);
  assert.equal(fetches, 2);
  assert.equal(reloads, 2);
});
