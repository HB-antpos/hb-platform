import assert from "node:assert/strict";
import test from "node:test";

import plugin from "./with-pods-deployment-target-floor.js";

const { applyPodsDeploymentTargetFloorPodfile } = plugin;

const expoSdk54Podfile = `target 'HB' do
  use_expo_modules!

  post_install do |installer|
    react_native_post_install(
      installer,
      config[:reactNativePath],
      :mac_catalyst_enabled => false,
      :ccache_enabled => ccache_enabled?(podfile_properties),
    )
  end
end
`;

test("Xcode 27 Pod 部署版本下限补丁插入 post_install 且重复执行保持幂等", () => {
  const once = applyPodsDeploymentTargetFloorPodfile(expoSdk54Podfile);
  const twice = applyPodsDeploymentTargetFloorPodfile(once);

  assert.equal(twice, once);
  assert.match(once, /HB Xcode 27 pods deployment target floor \[v1\]/);
  // 取 app 自己的部署版本，缺省才回落到 SDK 54 最低的 15.1。
  assert.match(
    once,
    /hb_floor_target = podfile_properties\['ios\.deploymentTarget'\] \|\| '15\.1'/,
  );
  // 只抬不降：必须是「小于才改」的比较。
  assert.match(
    once,
    /if Gem::Version\.new\(current_target\.to_s\) < Gem::Version\.new\(hb_floor_target\)/,
  );
  assert.ok(
    once.indexOf("react_native_post_install(") <
      once.indexOf("HB Xcode 27 pods deployment target floor"),
    "补丁必须在 react_native_post_install 之后执行，避免被其覆盖",
  );
});

test("找不到 Expo SDK 54 post_install 锚点时拒绝改写", () => {
  assert.throws(
    () => applyPodsDeploymentTargetFloorPodfile("post_install do |installer|\nend\n"),
    /post_install anchor was not found/,
  );
});

test("标记存在但内容残缺时拒绝继续", () => {
  assert.throws(
    () =>
      applyPodsDeploymentTargetFloorPodfile(
        `${expoSdk54Podfile}\n# HB Xcode 27 pods deployment target floor [v1]\n`,
      ),
    /marker is incomplete/,
  );
});
