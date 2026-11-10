#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
component="${1:?缺少 Android 组件名}"
profile="${CI_PROFILE:-weekly}"
cd "$repository_root"

if [[ "$profile" != "pr" && "$profile" != "weekly" ]]; then
  echo "未知 CI profile: $profile" >&2
  exit 2
fi

case "$component" in
  noop)
    echo "该 PR 没有需要 Android runner 执行的组件。"
    ;;
  mobile-android)
    # apps/mobile 的锁文件里有 700 多个包的下载地址指向 registry.npmmirror.com，GitHub runner 访问它会卡死到任务超时。
    # 平时靠 npm 缓存命中而不联网；本任务的缓存 key 同时含根锁文件，根锁文件一变缓存就失效，问题才暴露。
    # 这里让 npm 一律改从配置的官方源下载（完整性哈希不变，仍按锁文件校验），不依赖缓存是否命中。
    npm --prefix apps/mobile ci --no-audit --no-fund --replace-registry-host=always
    (
      cd apps/mobile
      # 保留仓库内的打印机等原生定制，只同步配置并刷新本地 Expo 模块自动链接。
      EXPO_NO_TELEMETRY=1 npx expo prebuild --platform android --no-install
    )
    bash apps/mobile/android/gradlew \
      -p apps/mobile/android \
      :hb-app-installer:testDebugUnitTest \
      :app:compileDebugKotlin \
      :app:processDebugResources \
      -PreactNativeArchitectures=arm64-v8a \
      --no-daemon
    ;;
  pos-handheld-android)
    npm ci --no-audit --no-fund
    npm run prebuild:android --workspace=@hb/pos-handheld -- --clean
    (
      cd apps/pos-handheld/android
      node --print "require.resolve('@sentry/react-native/package.json')" >/dev/null
    )
    gradle_tasks=(
      :hb-app-installer:testDebugUnitTest
      :hb-attendance-security:testDebugUnitTest
      :app:assembleDebug
    )
    # Android lint 约占 2 分钟且不影响编译与单元测试结果；PR 只做编译与测试，lint 留给 weekly 全量。
    if [[ "$profile" == "weekly" ]]; then
      gradle_tasks+=(:app:lintDebug)
    else
      echo "PR profile 跳过 :app:lintDebug；Android lint 由 weekly 执行。"
    fi
    bash apps/pos-handheld/android/gradlew \
      -p apps/pos-handheld/android \
      "${gradle_tasks[@]}" \
      -PreactNativeArchitectures=arm64-v8a \
      --no-daemon
    ;;
  *)
    echo "未知 Android 组件: $component" >&2
    exit 2
    ;;
esac
