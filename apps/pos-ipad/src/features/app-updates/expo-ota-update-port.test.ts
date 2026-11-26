import assert from "node:assert/strict";
import test from "node:test";

import {
  createExpoOtaChannelBindingStore,
  EXPO_OTA_CHANNEL_BINDING_KEY,
} from "./expo-ota-channel-binding";
import {
  ExpoOtaUpdatePort,
  type ExpoOtaChannelBinding,
  type ExpoOtaChannelBindingStore,
  type ExpoOtaUpdateApplyResult,
  type ExpoOtaUpdatePortOptions,
  type ExpoOtaUpdatesRuntimePort,
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

/** 原生包内嵌的 channel（没有覆盖时 Updates.channel 就是它）。 */
const EMBEDDED_CHANNEL = "pos-ipad-production";
const NATIVE_BUILD = "21";

function memoryBinding(initial: ExpoOtaChannelBinding | null = null) {
  let value = initial;
  const writes: (ExpoOtaChannelBinding | null)[] = [];
  const store: ExpoOtaChannelBindingStore = {
    read: () => value,
    write(binding) {
      writes.push(binding);
      value = binding;
    },
  };
  return { store, writes, current: () => value };
}

function createPort(
  updates: ExpoOtaUpdatesRuntimePort,
  overrides: Partial<ExpoOtaUpdatePortOptions> = {},
): ExpoOtaUpdatePort {
  return new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: policy.runtimeVersion,
    updates,
    launchChannel: EMBEDDED_CHANNEL,
    nativeBuild: NATIVE_BUILD,
    channelBinding: memoryBinding().store,
    ...overrides,
  });
}

const matchingManifest = Object.freeze({
  id: policy.iosUpdateId,
  runtimeVersion: policy.runtimeVersion,
  metadata: { updateGroupId: policy.updateGroupId },
});

function tracingUpdates(trace: string[]): ExpoOtaUpdatesRuntimePort {
  return {
    setUpdateRequestHeadersOverride(headers) {
      trace.push(
        headers ? `override:${headers["expo-channel-name"]}` : "override:clear",
      );
    },
    async checkForUpdateAsync() {
      trace.push("check");
      return { isAvailable: true, manifest: matchingManifest };
    },
    async fetchUpdateAsync() {
      trace.push("fetch");
      return { isNew: true, manifest: matchingManifest };
    },
  };
}

test("启动：没有有效记录时清掉遗留 channel 覆盖；禁用或原生异常不崩启动", () => {
  const trace: string[] = [];
  const binding = memoryBinding();
  createPort(tracingUpdates(trace), { channelBinding: binding.store });
  assert.deepEqual(trace, ["override:clear"]);
  assert.deepEqual(binding.writes, [], "本来就没有记录时不必再写");

  for (const enabled of [false, true]) {
    assert.doesNotThrow(() =>
      createPort(
        {
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
        { enabled },
      ),
    );
  }
});

test("启动：同一原生包、本次就按记录的 channel 启动时保留覆盖，已装的 OTA 继续生效", () => {
  const trace: string[] = [];
  const binding = memoryBinding({ channel: policy.channel, nativeBuild: NATIVE_BUILD });
  createPort(tracingUpdates(trace), {
    channelBinding: binding.store,
    launchChannel: policy.channel,
  });
  assert.deepEqual(trace, []);
  assert.deepEqual(binding.current(), { channel: policy.channel, nativeBuild: NATIVE_BUILD });
});

test("启动：换了新原生包（覆盖会跨 App 升级保留）时清掉覆盖与记录，回到新原生包自带版本", () => {
  const trace: string[] = [];
  const binding = memoryBinding({ channel: policy.channel, nativeBuild: "20" });
  createPort(tracingUpdates(trace), {
    channelBinding: binding.store,
    launchChannel: policy.channel,
    nativeBuild: "22",
  });
  assert.deepEqual(trace, ["override:clear"]);
  assert.equal(binding.current(), null);
});

test("启动：记录读不出来时情况不明，不动覆盖，按本次启动的 channel 视为已安装", async () => {
  const trace: string[] = [];
  const port = createPort(
    {
      ...tracingUpdates(trace),
      async checkForUpdateAsync() {
        trace.push("check");
        return { isAvailable: false };
      },
    },
    {
      launchChannel: policy.channel,
      channelBinding: {
        read() {
          throw new Error("kv unavailable");
        },
        write() {},
      },
    },
  );
  assert.deepEqual(trace, []);
  // 之后一次没结果的检查要恢复成本次启动的 channel，而不是清空。
  await port.apply({ ...policy, channel: "other-release" });
  assert.equal(trace.at(-1), `override:${policy.channel}`);
});

test("下载成功后保留该 channel 覆盖并记下原生包 build 号（冷启动才能选中这个更新）", async () => {
  const trace: string[] = [];
  const binding = memoryBinding();
  const port = createPort(tracingUpdates(trace), { channelBinding: binding.store });

  assert.deepEqual(await port.apply(policy), { state: "downloaded", reason: null });
  assert.deepEqual(trace, [
    "override:clear",
    `override:${policy.channel}`,
    "check",
    "fetch",
  ]);
  assert.deepEqual(binding.current(), { channel: policy.channel, nativeBuild: NATIVE_BUILD });
});

test("冷启动后：记录有效则保留；之后没结果的检查恢复成已安装 OTA 的 channel 而不是清空", async () => {
  const trace: string[] = [];
  const binding = memoryBinding({ channel: policy.channel, nativeBuild: NATIVE_BUILD });
  const port = createPort(
    {
      ...tracingUpdates(trace),
      async checkForUpdateAsync() {
        trace.push("check");
        return { isAvailable: false };
      },
    },
    { channelBinding: binding.store, launchChannel: policy.channel },
  );

  assert.deepEqual(await port.apply({ ...policy, channel: "pos-ipad-release-next" }), {
    state: "unavailable",
    reason: "not-available",
  });
  assert.deepEqual(trace, [
    "override:pos-ipad-release-next",
    "check",
    `override:${policy.channel}`,
  ]);
  assert.deepEqual(binding.current(), { channel: policy.channel, nativeBuild: NATIVE_BUILD });
});

test("channel setter 部分持久化后抛错时仍恢复并 fail-closed", async () => {
  const trace: string[] = [];
  let persistedChannel: string | null = null;
  let checks = 0;
  const port = createPort({
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
  });

  assert.deepEqual(await port.apply(policy), {
    state: "rejected",
    reason: "channel-override-failed",
  });
  assert.deepEqual(trace, ["clear", `set:${policy.channel}`, "clear"]);
  assert.equal(persistedChannel, null);
  assert.equal(checks, 0);
});

test("下载成功但记录写不进去：恢复原 channel，报 channel-binding-failed 而不是成功", async () => {
  const trace: string[] = [];
  const port = createPort(tracingUpdates(trace), {
    channelBinding: {
      read: () => null,
      write() {
        throw new Error("kv write failed");
      },
    },
  });

  assert.deepEqual(await port.apply(policy), {
    state: "rejected",
    reason: "channel-binding-failed",
  });
  assert.equal(trace.at(-1), "override:clear");
});

test("恢复 channel 抛错时返回稳定失败，优先于其他结果", async () => {
  for (const checkThrows of [false, true]) {
    let overrideSet = false;
    const port = createPort({
      setUpdateRequestHeadersOverride(headers) {
        if (headers) {
          overrideSet = true;
          return;
        }
        if (overrideSet) throw new Error("native restore failed");
      },
      async checkForUpdateAsync() {
        if (checkThrows) throw new Error("native check failed");
        return { isAvailable: false };
      },
      async fetchUpdateAsync() {
        throw new Error("must not fetch");
      },
    });

    assert.deepEqual(await port.apply(policy), {
      state: "rejected",
      reason: "channel-clear-failed",
    });
  }
});

test("runtime 或 updateId 不匹配时 fail-closed，恢复覆盖且绝不 fetch", async () => {
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
    const binding = memoryBinding();
    const port = createPort(
      {
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
      { runtimeVersion, channelBinding: binding.store },
    );

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
    assert.equal(binding.current(), null);
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
      ...(updateGroupId === undefined ? {} : { metadata: { updateGroupId } }),
    };
    const port = createPort({
      setUpdateRequestHeadersOverride() {},
      async checkForUpdateAsync() {
        return { isAvailable: true, manifest };
      },
      async fetchUpdateAsync() {
        return { isNew: true, manifest };
      },
    });

    const result: ExpoOtaUpdateApplyResult = await port.apply(policy);
    assert.deepEqual(result, { state: "downloaded", reason: null });
  }
});

test("expo-updates 未启用或服务器无匹配更新时不覆盖错误 channel", async () => {
  let checks = 0;
  const disabled = createPort(
    {
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
    { enabled: false },
  );
  assert.deepEqual(await disabled.apply(policy), {
    state: "unavailable",
    reason: "updates-disabled",
  });

  const unavailable = createPort({
    setUpdateRequestHeadersOverride() {},
    async checkForUpdateAsync() {
      checks += 1;
      return { isAvailable: false };
    },
    async fetchUpdateAsync() {
      throw new Error("must not fetch");
    },
  });
  assert.deepEqual(await unavailable.apply(policy), {
    state: "unavailable",
    reason: "not-available",
  });
  assert.equal(checks, 1);
});

test("下载前复核失败时不 fetch、恢复 channel（下载即等于下次冷启动生效）", async () => {
  const trace: string[] = [];
  const port = createPort(tracingUpdates(trace));

  assert.deepEqual(await port.apply(policy, () => "restart-unsafe"), {
    state: "rejected",
    reason: "restart-unsafe",
  });
  assert.deepEqual(trace, [
    "override:clear",
    `override:${policy.channel}`,
    "check",
    "override:clear",
  ]);
});

test("连续应用不同 release：每次保留最新下载成功的 channel，记录随之更新", async () => {
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
  const binding = memoryBinding();
  const updatesByChannel = new Map([
    [first.channel, first],
    [second.channel, second],
  ]);
  const manifestFor = () => {
    const selected = activeChannel ? updatesByChannel.get(activeChannel) : undefined;
    return selected
      ? {
          id: selected.iosUpdateId,
          runtimeVersion: selected.runtimeVersion,
          metadata: { updateGroupId: selected.updateGroupId },
        }
      : undefined;
  };
  const port = createPort(
    {
      setUpdateRequestHeadersOverride(headers) {
        activeChannel = headers?.["expo-channel-name"] ?? null;
      },
      async checkForUpdateAsync() {
        const manifest = manifestFor();
        return { isAvailable: manifest !== undefined, manifest };
      },
      async fetchUpdateAsync() {
        const manifest = manifestFor();
        return { isNew: manifest !== undefined, manifest };
      },
    },
    { channelBinding: binding.store },
  );

  assert.equal((await port.apply(first)).state, "downloaded");
  assert.equal(activeChannel, first.channel);
  assert.equal((await port.apply(second)).state, "downloaded");
  assert.equal(activeChannel, second.channel);
  assert.deepEqual(binding.current(), { channel: second.channel, nativeBuild: NATIVE_BUILD });
});

test("回归：即使原生对象带 reloadAsync，下载成功后也绝不在进程内 reload", async () => {
  // 2026-11-25 build 18 真机：Updates.reloadAsync() 拆旧 Hermes 运行时时
  // expo-modules-core 3.0.30 的 SharedObjectRegistry 后台析构 jsi::WeakObject 闪退。
  let reloads = 0;
  // 用变量而不是对象字面量传入，模拟直接把 expo-updates 模块整个交给端口。
  const nativeUpdates = {
    setUpdateRequestHeadersOverride() {},
    async checkForUpdateAsync() {
      return { isAvailable: true, manifest: matchingManifest };
    },
    async fetchUpdateAsync() {
      return { isNew: true, manifest: matchingManifest };
    },
    async reloadAsync() {
      reloads += 1;
    },
  };
  const port = createPort(nativeUpdates);

  assert.deepEqual(await port.apply(policy), { state: "downloaded", reason: null });
  assert.equal(reloads, 0);
});

test("channel 记录存储：读写往返，损坏或缺字段按没有记录处理", () => {
  const data = new Map<string, string>();
  const store = createExpoOtaChannelBindingStore({
    getItemSync: (key) => data.get(key) ?? null,
    setItemSync: (key, value) => void data.set(key, value),
    removeItemSync: (key) => data.delete(key),
  });

  assert.equal(store.read(), null);
  store.write({ channel: policy.channel, nativeBuild: NATIVE_BUILD });
  assert.deepEqual(store.read(), { channel: policy.channel, nativeBuild: NATIVE_BUILD });
  store.write(null);
  assert.equal(store.read(), null);

  for (const raw of ["{not json", JSON.stringify({ channel: "x" }), JSON.stringify({ channel: "", nativeBuild: "1" })]) {
    data.set(EXPO_OTA_CHANNEL_BINDING_KEY, raw);
    assert.equal(store.read(), null, raw);
  }
});
