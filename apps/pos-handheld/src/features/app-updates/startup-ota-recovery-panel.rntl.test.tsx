import { afterEach, expect, jest, test } from "@jest/globals";
import {
  cleanup,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";

import type { ExpoOtaUpdateApplyResult } from "./expo-ota-update-port";
import type { OtaUpdateRefreshResult } from "./ota-update-coordinator";
import { StartupOtaRecovery } from "./startup-ota-recovery";
import { StartupOtaRecoveryPanel } from "./startup-ota-recovery-panel";

import {
  createPosHandheldOtaNonePolicy,
  type PosHandheldOtaUpdatePolicy,
} from "@/core/contracts/ota-app-updates";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

const requiredPolicy: PosHandheldOtaUpdatePolicy = Object.freeze({
  state: "required",
  policyVersion: "11",
  appKey: "pos-handheld",
  projectName: "hb-pos-handheld",
  platform: "Android",
  required: true,
  channel: "pos-handheld-production-android-release-v11",
  runtimeVersion: "0.1.1",
  updateId: "android-update-11",
  updateGroupId: "86a1abec-0000-4000-8000-000000000001",
  releaseMessage: "修复收款中途重启后无法启动",
});

function createRecovery(
  options: Readonly<{
    enabled?: boolean;
    policy?: PosHandheldOtaUpdatePolicy;
    apply?: () => Promise<ExpoOtaUpdateApplyResult>;
  }> = {},
) {
  const apply = jest.fn(
    options.apply ??
      (() => new Promise<ExpoOtaUpdateApplyResult>(() => undefined)),
  );
  const refresh = jest.fn(
    async (reason: "startup" | "foreground"): Promise<OtaUpdateRefreshResult> => ({
      reason,
      source: "remote",
      policy: options.policy ?? requiredPolicy,
    }),
  );
  const recovery = new StartupOtaRecovery({
    enabled: options.enabled ?? true,
    hasDeviceCredentials: async () => true,
    coordinator: { refresh, apply },
    canReload: () => true,
  });
  return { recovery, apply, refresh };
}

afterEach(async () => {
  await cleanup();
});

test("失败页自动检查并展示受控更新，店员确认后才安装且通知启动页忙碌", async () => {
  const { recovery, apply } = createRecovery();
  const busy: boolean[] = [];
  const screen = await render(
    <StartupOtaRecoveryPanel
      load={async () => recovery}
      onBusyChange={(value) => busy.push(value)}
    />,
  );

  await waitFor(() =>
    expect(
      screen.getByText("bootstrap.otaRecovery.availableRequired"),
    ).toBeTruthy(),
  );
  expect(screen.getByText("修复收款中途重启后无法启动")).toBeTruthy();
  expect(screen.getByText("bootstrap.otaRecovery.keepsData")).toBeTruthy();
  expect(apply).not.toHaveBeenCalled();

  await fireEvent.press(screen.getByTestId("startup-ota-recovery-apply"));

  await waitFor(() =>
    expect(screen.getByText("bootstrap.otaRecovery.applying")).toBeTruthy(),
  );
  expect(apply).toHaveBeenCalledTimes(1);
  expect(busy.at(-1)).toBe(true);
});

test("后台无更新时提示并允许重新检查", async () => {
  const { recovery, refresh } = createRecovery({
    policy: createPosHandheldOtaNonePolicy("Android"),
  });
  const screen = await render(
    <StartupOtaRecoveryPanel load={async () => recovery} />,
  );

  await waitFor(() =>
    expect(screen.getByText("bootstrap.otaRecovery.upToDate")).toBeTruthy(),
  );
  await fireEvent.press(screen.getByTestId("startup-ota-recovery-check"));

  await waitFor(() => expect(refresh).toHaveBeenCalledTimes(2));
});

test("核验失败给出稳定的本地化原因，不展示安装按钮", async () => {
  const { recovery } = createRecovery({
    apply: async () => ({ state: "rejected", reason: "runtime-mismatch" }),
  });
  const screen = await render(
    <StartupOtaRecoveryPanel load={async () => recovery} />,
  );
  await waitFor(() =>
    expect(screen.getByTestId("startup-ota-recovery-apply")).toBeTruthy(),
  );

  await fireEvent.press(screen.getByTestId("startup-ota-recovery-apply"));

  await waitFor(() =>
    expect(screen.getByText("bootstrap.otaRecovery.mismatch")).toBeTruthy(),
  );
  expect(screen.queryByTestId("startup-ota-recovery-apply")).toBeNull();
});

test("未启用 OTA 的构建不渲染面板", async () => {
  const { recovery, refresh } = createRecovery({ enabled: false });
  const screen = await render(
    <StartupOtaRecoveryPanel load={async () => recovery} />,
  );

  await waitFor(() =>
    expect(recovery.getState()).toEqual({
      phase: "unavailable",
      reason: "updates-disabled",
    }),
  );
  expect(screen.queryByTestId("startup-ota-recovery")).toBeNull();
  expect(refresh).not.toHaveBeenCalled();
});

test("恢复通道自身加载失败时显示失败提示而不影响启动页", async () => {
  const screen = await render(
    <StartupOtaRecoveryPanel
      load={() => Promise.reject(new Error("secure store unavailable"))}
    />,
  );

  await waitFor(() =>
    expect(screen.getByText("bootstrap.otaRecovery.failed")).toBeTruthy(),
  );
  expect(screen.queryByTestId("startup-ota-recovery-check")).toBeNull();
});
