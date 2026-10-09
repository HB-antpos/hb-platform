import {
  buildWpfDecisionLadder,
  canSavePolicy,
  compareVersions,
  createLatestRequestGuard,
  getPolicyValidationError,
  getWpfNewerActiveVersions,
  getWpfPolicyMode,
  inferRollback,
  policySummaryMatchesRequest,
} from "./logic";
import type { WpfRelease } from "./types";

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}

assert(
  compareVersions("1.2.10", "1.2.9")! > 0,
  "semantic version comparison should compare numeric parts",
);
assert(
  inferRollback("1.2.0", "1.3.0"),
  "lower target must be treated as rollback",
);
assert(!inferRollback("1.3.0", "1.3.0"), "same target is not rollback");

const basePolicy = {
  targetVersion: "1.3.0",
  minimumSupportedVersion: "1.2.0",
  targetScope: "all" as const,
  targetStoreGuids: [],
  targetDeviceRegistrationIds: [],
};
assert(
  getPolicyValidationError(basePolicy) === null,
  "valid all-target policy should pass",
);
assert(
  getPolicyValidationError({ ...basePolicy, activeVersions: ["1.2.0"] }) ===
    "targetVersionUnavailable",
  "target must be an active release in the current channel",
);
assert(
  getPolicyValidationError({ ...basePolicy, activeVersions: ["1.3.0"] }) ===
    "minimumVersionUnavailable",
  "minimum must be an active release in the current channel",
);
assert(
  getPolicyValidationError({
    ...basePolicy,
    minimumSupportedVersion: "1.4.0",
  }) === "minimumAboveTarget",
  "minimum above target must fail",
);
assert(
  getPolicyValidationError({
    ...basePolicy,
    targetScope: "devices",
    targetDeviceRegistrationIds: [],
  }) === "devicesRequired",
  "device policy must require devices",
);
assert(
  canSavePolicy({
    ...basePolicy,
    targetScope: "stores",
    targetStoreGuids: ["store-1"],
  }),
  "selected store policy should pass",
);

const policySummary = {
  channel: "production",
  targetVersion: "1.3.0",
  minimumSupportedVersion: "1.2.0",
  forceUpdate: true,
  isRollback: false,
  targetScope: "devices" as const,
  targetStoreGuids: [],
  targetDeviceRegistrationIds: [7],
  targetStoreSummaries: [],
  targetDeviceSummaries: [],
  policyUpdatedAt: null,
  policyUpdatedBy: null,
};
assert(
  policySummaryMatchesRequest(
    {
      ...basePolicy,
      channel: "production",
      forceUpdate: true,
      isRollback: false,
      targetScope: "devices",
      targetDeviceRegistrationIds: [7],
    },
    policySummary,
  ),
  "policy readback should match submitted fields",
);
assert(
  !policySummaryMatchesRequest(
    {
      ...basePolicy,
      channel: "production",
      forceUpdate: false,
      isRollback: false,
      targetScope: "devices",
      targetDeviceRegistrationIds: [7],
    },
    policySummary,
  ),
  "policy readback mismatch must fail verification",
);
assert(
  !policySummaryMatchesRequest(
    {
      ...basePolicy,
      channel: "production",
      forceUpdate: true,
      isRollback: false,
      targetScope: "devices",
      targetDeviceRegistrationIds: [8],
    },
    null,
  ),
  "empty policy readback must fail verification",
);

const guard = createLatestRequestGuard();
const first = guard.next();
const second = guard.next();
assert(
  !guard.isCurrent(first) && guard.isCurrent(second),
  "newer request must invalidate older response",
);
guard.invalidate();
assert(!guard.isCurrent(second), "invalidate must isolate in-flight response");

// 更新方式 / 新版本未投放 / 设备决策预览：与 Web 版本发布中心同口径。
function wpfRelease(patch: Partial<WpfRelease>): WpfRelease {
  return {
    id: patch.version ?? "id",
    version: "1.0.0",
    channel: "production",
    fileName: "",
    fileSize: null,
    sha256: null,
    installerType: "exe",
    installerArguments: null,
    downloadUrl: null,
    objectKey: null,
    releaseNotes: null,
    isActive: true,
    isCurrent: false,
    isRollback: false,
    forceUpdate: false,
    minimumSupportedVersion: null,
    targetVersion: null,
    targetScope: "all",
    targetStoreGuids: [],
    targetDeviceRegistrationIds: [],
    targetStoreSummaries: [],
    targetDeviceSummaries: [],
    policyUpdatedAt: null,
    policyUpdatedBy: null,
    createdAt: null,
    updatedAt: null,
    ...patch,
  };
}
const carrierWith = (minimum: string | null) => [
  wpfRelease({
    version: "1.0.59",
    isCurrent: true,
    targetVersion: "1.0.59",
    minimumSupportedVersion: minimum,
  }),
];
assert(
  getWpfPolicyMode(carrierWith(null), { targetVersion: "1.0.59", forceUpdate: false }).mode === "optional",
  "no stored minimum and no force means optional",
);
const minimumMode = getWpfPolicyMode(carrierWith("1.0.54"), {
  targetVersion: "1.0.59",
  forceUpdate: false,
});
assert(
  minimumMode.mode === "minimum" && minimumMode.minimum === "1.0.54",
  "minimum below target means minimum-forced",
);
assert(
  getWpfPolicyMode(carrierWith("1.0.59"), { targetVersion: "1.0.59", forceUpdate: false }).mode === "required",
  "minimum equal to target forces every older machine",
);
assert(
  getWpfPolicyMode(carrierWith("1.0.54"), { targetVersion: "1.0.59", forceUpdate: true }).mode === "required",
  "force flag wins over minimum",
);

const newer = getWpfNewerActiveVersions(
  [
    wpfRelease({ version: "1.0.59" }),
    wpfRelease({ version: "1.0.61" }),
    wpfRelease({ version: "1.0.60" }),
    wpfRelease({ version: "1.0.62", isActive: false }),
    wpfRelease({ version: "1.0.58" }),
  ],
  "1.0.59",
);
assert(
  newer.join(",") === "1.0.61,1.0.60",
  "only active versions above target count as pending, newest first",
);

const ladder = buildWpfDecisionLadder("minimum", "1.0.59", "1.0.54");
assert(
  ladder.map((segment) => `${segment.kind}:${segment.bound}`).join("|") ===
    "force:1.0.54|optional:1.0.59|latest:1.0.59|rollback:1.0.59",
  "minimum mode ladder: force below minimum, optional between, latest, rollback",
);
assert(
  buildWpfDecisionLadder("required", "1.0.59", null)[0]?.kind === "force" &&
    buildWpfDecisionLadder("optional", "1.0.59", null)[0]?.kind === "optional",
  "required ladder starts with force, optional ladder with optional",
);

console.log("wpf versions logic tests passed");
