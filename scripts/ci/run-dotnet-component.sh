#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
component="${1:?缺少 .NET 组件名}"
results_root="${RUNNER_TEMP:-$repository_root/.artifacts/ci}/$component"
mkdir -p "$results_root"
cd "$repository_root"

case "$component" in
  noop)
    echo "该 PR 没有需要此 runner 执行的组件。"
    ;;
  backend)
    project="services/backend/BlazorApp.Api.Tests/BlazorApp.Api.Tests.csproj"
    mobile_activation_project="services/backend/BlazorApp.MobileDeviceActivation.Tests/BlazorApp.MobileDeviceActivation.Tests.csproj"
    face_ingest_project="services/backend/BlazorApp.FaceIngest.Tests/BlazorApp.FaceIngest.Tests.csproj"
    node scripts/ci/backend-test-inventory.mjs
    dotnet restore "$project"
    dotnet restore "$mobile_activation_project"
    dotnet restore "$face_ingest_project"
    dotnet build "$project" --configuration Release --no-restore
    dotnet build "$mobile_activation_project" --configuration Release --no-restore
    dotnet build "$face_ingest_project" --configuration Release --no-restore
    dotnet test "$project" \
      --configuration Release \
      --no-build \
      --filter 'Category!=SQL&Category!=Performance&Category!=LiveE2e' \
      --logger 'trx;LogFileName=backend.trx' \
      --results-directory "$results_root"
    node scripts/ci/assert-trx-tests.mjs "$results_root/backend.trx" 'Backend tests'

    dotnet test "$project" \
      --configuration Release \
      --no-build \
      --filter 'FullyQualifiedName~Contract&Category!=SQL&Category!=LiveE2e' \
      --logger 'trx;LogFileName=backend-contract.trx' \
      --results-directory "$results_root"
    node scripts/ci/assert-trx-tests.mjs "$results_root/backend-contract.trx" 'Backend API contract'

    dotnet test "$mobile_activation_project" \
      --configuration Release \
      --no-build \
      --filter 'Category!=SQL' \
      --logger 'trx;LogFileName=mobile-device-activation.trx' \
      --results-directory "$results_root"
    node scripts/ci/assert-trx-tests.mjs "$results_root/mobile-device-activation.trx" 'Mobile device activation contracts'

    dotnet test "$face_ingest_project" \
      --configuration Release \
      --no-build \
      --filter 'Category!=SQL' \
      --logger 'trx;LogFileName=face-ingest.trx' \
      --results-directory "$results_root"
    node scripts/ci/assert-trx-tests.mjs "$results_root/face-ingest.trx" 'Face ingest tests'
    ;;
  pos-api)
    project="apps/pos-wpf/tests/Hbpos.Api.Tests/Hbpos.Api.Tests.csproj"
    dotnet restore "$project"
    dotnet build "$project" --configuration Release --no-restore
    dotnet test "$project" \
      --configuration Release \
      --no-build \
      --filter 'Category!=SQL&Category!=Performance&Category!=LiveE2e' \
      --logger 'trx;LogFileName=pos-api.trx' \
      --results-directory "$results_root"
    node scripts/ci/assert-trx-tests.mjs "$results_root/pos-api.trx" 'POS API tests'
    ;;
  pos-contract)
    project="apps/pos-wpf/tests/Hbpos.Api.Tests/Hbpos.Api.Tests.csproj"
    dotnet restore "$project"
    npm ci --no-audit --no-fund
    npm run test:hbpos-client
    ;;
  *)
    echo "未知 .NET 组件: $component" >&2
    exit 2
    ;;
esac
