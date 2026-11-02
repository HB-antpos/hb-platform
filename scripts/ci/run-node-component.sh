#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
component="${1:?缺少 Node 组件名}"
cd "$repository_root"

# 并行执行互不依赖的检查：参数形如 'label=命令'，每项输出各自落盘，全部结束后按参数顺序回放，
# 任一失败整体失败（先等齐再汇总，失败原因不会被后续项的日志冲掉）。
run_parallel() {
  local log_root="${RUNNER_TEMP:-$repository_root/.artifacts/ci}/parallel-$component-$$"
  mkdir -p "$log_root"
  local -a labels=()
  local -a pids=()
  local entry label command
  for entry in "$@"; do
    label="${entry%%=*}"
    command="${entry#*=}"
    echo "并行启动: $label"
    bash -c "$command" >"$log_root/$label.log" 2>&1 &
    labels+=("$label")
    pids+=("$!")
  done

  local index exit_code status=0
  for index in "${!pids[@]}"; do
    exit_code=0
    wait "${pids[$index]}" || exit_code=$?
    echo "::group::${labels[$index]} (exit=$exit_code)"
    cat "$log_root/${labels[$index]}.log"
    echo "::endgroup::"
    if [[ "$exit_code" -ne 0 ]]; then
      echo "并行检查失败: ${labels[$index]} (exit=$exit_code)" >&2
      status=1
    fi
  done
  return "$status"
}

case "$component" in
  noop)
    echo "该 PR 没有需要此 runner 执行的组件。"
    ;;
  web)
    npm --prefix apps/web ci --no-audit --no-fund
    npm --prefix apps/web run typecheck
    npm --prefix apps/web run test:ci
    VITE_CENTER_LOG_KEY=ci-test-only \
      VITE_CENTER_LOG_PROJECT=hbweb_rv \
      VITE_CENTER_LOG_ENVIRONMENT=Production \
      VITE_CENTER_LOG_SERVICE_NAME=hbweb_rv-web \
      npm --prefix apps/web run build:ci -- --manifest
    npm --prefix apps/web run verify:bundle
    ;;
  mobile)
    npm --prefix apps/mobile ci --no-audit --no-fund
    # typecheck / lint / test:ci 与两次 expo export 互不读写对方产物（tsc --noEmit、eslint 无缓存、测试不读 dist），
    # 串行约 5 分钟，改为并行后以 build:ci 的约 2 分钟为界。
    run_parallel \
      'typecheck=npm --prefix apps/mobile run typecheck' \
      'lint=npm --prefix apps/mobile run lint' \
      'test-ci=npm --prefix apps/mobile run test:ci' \
      'build-ci=EXPO_NO_TELEMETRY=1 npm --prefix apps/mobile run build:ci'
    ;;
  pos-ipad)
    npm ci --no-audit --no-fund
    # test:pos-shared-ci 末尾的 typecheck:pos 已对所有 workspace（含 @hb/pos-ipad）跑过 tsc，这里不再单独 typecheck。
    npm run test:pos-shared-ci
    npm run lint --workspace=@hb/pos-ipad
    npm run test:ci --workspace=@hb/pos-ipad
    # PR 的质量基线复用本任务；保留原质量任务独有的 Expo/Metro 打包检查。
    npm run verify:metro-bundle --workspace=@hb/pos-ipad
    ;;
  pos-handheld)
    npm ci --no-audit --no-fund
    npm run typecheck --workspace=@hb/pos-handheld
    npm run lint --workspace=@hb/pos-handheld
    npm run test:ci --workspace=@hb/pos-handheld
    npm run verify:metro-bundle --workspace=@hb/pos-handheld
    ;;
  supplier-extension)
    npm --prefix apps/supplier-order-extension ci --no-audit --no-fund
    npm --prefix apps/supplier-order-extension test
    npm --prefix apps/supplier-order-extension run build
    node scripts/ci/extension-manifest.mjs
    ;;
  antpos-web)
    npm --prefix apps/antpos-web run check
    npm --prefix apps/antpos-web run build
    ;;
  *)
    echo "未知 Node 组件: $component" >&2
    exit 2
    ;;
esac
