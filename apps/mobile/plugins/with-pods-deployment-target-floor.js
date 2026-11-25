const fs = require("node:fs");
const path = require("node:path");

const {
  createRunOncePlugin,
  withDangerousMod,
} = require("@expo/config-plugins");

const PATCH_MARKER =
  "# HB Xcode 27 pods deployment target floor [v1]";
const POST_INSTALL_ANCHOR = `    react_native_post_install(
      installer,
      config[:reactNativePath],
      :mac_catalyst_enabled => false,
      :ccache_enabled => ccache_enabled?(podfile_properties),
    )
`;
// Expo SDK 54 的 iOS 最低部署版本；app 没写 ios.deploymentTarget 时用它兜底。
const FALLBACK_DEPLOYMENT_TARGET = "15.1";
const FLOOR_PATCH = `
    ${PATCH_MARKER}
    hb_floor_target = podfile_properties['ios.deploymentTarget'] || '${FALLBACK_DEPLOYMENT_TARGET}'
    installer.pods_project.targets.each do |pod_target|
      pod_target.build_configurations.each do |build_config|
        current_target = build_config.build_settings['IPHONEOS_DEPLOYMENT_TARGET']
        next if current_target.nil? || current_target.to_s.empty?
        if Gem::Version.new(current_target.to_s) < Gem::Version.new(hb_floor_target)
          build_config.build_settings['IPHONEOS_DEPLOYMENT_TARGET'] = hb_floor_target
        end
      end
    end
`;

/**
 * Xcode 27（iOS 27 SDK）把可用的最低部署版本抬到 iOS 15.0，并把低于它的
 * Pod 目标（多为 Sentry、RNDeviceInfo 等的资源包子目标，podspec 里还写着
 * 9.0~13.4）从警告升级为硬错误，工程校验阶段就直接失败。
 * 这里在 post_install 里把这些 Pod 目标抬到 app 自己的部署版本：
 * - 只抬不降：已经 >= app 部署版本的目标原样保留；
 * - 没有显式设置的目标不动，继续继承 Pods 工程级设置；
 * - 抬到的值与 app 一致，不会让任何 Pod 支持比 app 更低的系统。
 */
function applyPodsDeploymentTargetFloorPodfile(source) {
  if (source.includes(PATCH_MARKER)) {
    if (!source.includes("hb_floor_target = podfile_properties['ios.deploymentTarget']")) {
      throw new Error(
        "Xcode 27 pods deployment target floor marker is incomplete; refusing to continue.",
      );
    }
    return source;
  }
  if (!source.includes(POST_INSTALL_ANCHOR)) {
    throw new Error(
      "Expo SDK 54 Podfile post_install anchor was not found; refusing an unsafe deployment target transform.",
    );
  }
  return source.replace(
    POST_INSTALL_ANCHOR,
    `${POST_INSTALL_ANCHOR}${FLOOR_PATCH}`,
  );
}

function withPodsDeploymentTargetFloor(config) {
  return withDangerousMod(config, [
    "ios",
    async (modConfig) => {
      const podfilePath = path.join(
        modConfig.modRequest.platformProjectRoot,
        "Podfile",
      );
      const source = fs.readFileSync(podfilePath, "utf8");
      fs.writeFileSync(
        podfilePath,
        applyPodsDeploymentTargetFloorPodfile(source),
      );
      return modConfig;
    },
  ]);
}

const plugin = createRunOncePlugin(
  withPodsDeploymentTargetFloor,
  "with-pods-deployment-target-floor",
  "0.1.0",
);

Object.assign(plugin, {
  applyPodsDeploymentTargetFloorPodfile,
});

module.exports = plugin;
