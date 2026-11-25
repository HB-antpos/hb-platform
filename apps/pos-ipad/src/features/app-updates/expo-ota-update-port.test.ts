import assert from "node:assert/strict";
import test from "node:test";

import {
  ExpoOtaUpdatePort,
  type ExpoOtaUpdateApplyResult,
} from "./expo-ota-update-port";

import type { PosIpadOtaUpdatePolicy } from "@/core/contracts/ota-app-updates";

const policy: PosIpadOtaUpdatePolicy = Object.freeze({
  state: "required",
  policyVersion: "policy-42",
  channel: "store-s001",
  runtimeVersion: "1.2.3",
  iosUpdateId: "123e4567-e89b-42d3-a456-426614174000",
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
          },
        }),
    );
  }
});

test("命中策略后才覆盖 channel，check/fetch 双重验证 runtime/updateId 后只下载、等冷启动生效", async () => {
  const trace: string[] = [];
  const manifest = {
    id: policy.iosUpdateId,
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
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "downloaded",
    reason: null,
  });
  assert.deepEqual(trace, [
    "override:clear",
    "override:store-s001",
    "check",
    "fetch",
    "override:clear",
  ]);
});

test("channel setter 部分持久化后抛错时仍清理并 fail-closed", async () => {
  const trace: string[] = [];
  let persistedChannel: string | null = null;
  let checks = 0;
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
});

test("channel 清理抛错时返回稳定失败，不报下载成功", async () => {
  let overrideSet = false;
  let clearCalls = 0;
  const manifest = {
    id: policy.iosUpdateId,
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
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "rejected",
    reason: "channel-clear-failed",
  });
  assert.equal(clearCalls, 2);
});

test("检查与 channel 清理同时抛错时仍以清理失败优先", async () => {
  let overrideSet = false;
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
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "rejected",
    reason: "channel-clear-failed",
  });
});

test("runtime 或 updateId 不匹配时 fail-closed，清理 override 且绝不 fetch", async () => {
  for (const [runtimeVersion, manifest] of [
    [
      "9.9.9",
      {
        id: policy.iosUpdateId,
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
      },
    });

    const result = await port.apply(policy);
    assert.equal(result.state, "rejected");
    assert.ok(
      result.reason === "runtime-mismatch" ||
        result.reason === "update-id-mismatch",
    );
    assert.equal(trace.includes("fetch"), false);
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
    const manifest = {
      id: policy.iosUpdateId,
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
      },
    });

    const result: ExpoOtaUpdateApplyResult = await port.apply(policy);
    assert.deepEqual(result, { state: "downloaded", reason: null });
  }
});

test("expo-updates 未启用或服务器无匹配更新时不覆盖错误 channel", async () => {
  let checks = 0;
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
    },
  });
  assert.deepEqual(await unavailable.apply(policy), {
    state: "unavailable",
    reason: "not-available",
  });
  assert.equal(checks, 1);
});

test("下载前复核失败时不 fetch、清理 channel（下载即等于下次冷启动生效）", async () => {
  const trace: string[] = [];
  const manifest = {
    id: policy.iosUpdateId,
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
    "override:clear",
  ]);
});

test("连续应用不同 release channel 时每次都独立覆盖并清理，不泄漏前一策略", async () => {
  const first = {
    ...policy,
    policyVersion: "policy-first",
    channel: "pos-ipad-release-20260730-a",
  };
  const second = {
    ...policy,
    policyVersion: "policy-second",
    channel: "pos-ipad-release-20260730-b",
    iosUpdateId: "323e4567-e89b-42d3-a456-426614174000",
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
                id: selected.iosUpdateId,
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
                id: selected.iosUpdateId,
                runtimeVersion: selected.runtimeVersion,
                metadata: {
                  updateGroupId: selected.updateGroupId,
                },
              }
            : undefined,
        };
      },
    },
  });

  assert.equal((await port.apply(first)).state, "downloaded");
  assert.equal(activeChannel, null);
  assert.equal((await port.apply(second)).state, "downloaded");
  assert.equal(activeChannel, null);
  assert.deepEqual(trace, [
    "clear",
    `set:${first.channel}`,
    "clear",
    `set:${second.channel}`,
    "clear",
  ]);
});

test("回归：即使原生对象带 reloadAsync，下载成功后也绝不在进程内 reload", async () => {
  // 2026-11-25 build 18 真机：Updates.reloadAsync() 拆旧 Hermes 运行时时
  // expo-modules-core 3.0.30 的 SharedObjectRegistry 后台析构 jsi::WeakObject 闪退。
  const manifest = {
    id: policy.iosUpdateId,
    runtimeVersion: policy.runtimeVersion,
  };
  let reloads = 0;
  // 用变量而不是对象字面量传入，模拟直接把 expo-updates 模块整个交给端口。
  const nativeUpdates = {
    setUpdateRequestHeadersOverride() {},
    async checkForUpdateAsync() {
      return { isAvailable: true, manifest };
    },
    async fetchUpdateAsync() {
      return { isNew: true, manifest };
    },
    async reloadAsync() {
      reloads += 1;
    },
  };
  const port = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates: nativeUpdates,
  });

  assert.deepEqual(await port.apply(policy), {
    state: "downloaded",
    reason: null,
  });
  assert.equal(reloads, 0);
});
