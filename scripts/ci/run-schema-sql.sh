#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
shard="${1:?缺少 Schema SQL 分片名（a 或 b）}"
results_root="${RUNNER_TEMP:-$repository_root/.artifacts/ci}/schema-sql"
mkdir -p "$results_root"
cd "$repository_root"

required_variables=(
  HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION
  CI_SQL_PASSWORD
  CI_SCHEMA_SQL_CONTAINER_NAME
)
for variable in "${required_variables[@]}"; do
  if [[ -z "${!variable:-}" ]]; then
    echo "Schema SQL 缺少环境变量: $variable" >&2
    exit 1
  fi
done

# 分片过滤与完整性校验共用 scripts/ci/schema-sql-shards.mjs 的同一份定义；未知分片名在起容器之前就退出。
shard_filter="$(node scripts/ci/schema-sql-shards.mjs filter "$shard")"

container_id="$(docker inspect --format '{{.Id}}' "$CI_SCHEMA_SQL_CONTAINER_NAME" 2>/dev/null || true)"
if [[ -z "$container_id" ]]; then
  echo "Schema SQL 未找到预期的专用容器。" >&2
  exit 1
fi

if docker exec "$container_id" test -x /opt/mssql-tools18/bin/sqlcmd; then
  sqlcmd=/opt/mssql-tools18/bin/sqlcmd
else
  sqlcmd=/opt/mssql-tools/bin/sqlcmd
fi

ready=false
for _attempt in $(seq 1 60); do
  if docker exec "$container_id" "$sqlcmd" \
    -C -S localhost -U sa -P "$CI_SQL_PASSWORD" -Q 'SELECT 1' >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 2
done
if [[ "$ready" != true ]]; then
  echo "Schema SQL Server 在 120 秒内未就绪。" >&2
  exit 1
fi

preflight_database="HbSchemaPreflight_${GITHUB_RUN_ID:-local}_${GITHUB_RUN_ATTEMPT:-1}"
docker exec "$container_id" "$sqlcmd" \
  -C -S localhost -U sa -P "$CI_SQL_PASSWORD" \
  -Q "CREATE DATABASE [$preflight_database]; DROP DATABASE [$preflight_database];"

project="services/backend/BlazorApp.Api.Tests/BlazorApp.Api.Tests.csproj"
dotnet restore "$project"
dotnet build "$project" --configuration Release --no-restore

# 分片过滤只保证"命中的用例被执行"，漏片的用例会被静默跳过；用全量清单确认每个 Schema 用例恰好属于一个分片，
# 并把本片的用例数作为 TRX 的最低执行数。
listing_path="$results_root/schema-test-listing-$shard.txt"
DOTNET_CLI_UI_LANGUAGE=en VSLANG=1033 dotnet test "$project" \
  --configuration Release \
  --no-build \
  --list-tests > "$listing_path"
expected_test_count="$(node scripts/ci/schema-sql-shards.mjs coverage "$shard" "$listing_path")"

dotnet test "$project" \
  --configuration Release \
  --no-build \
  --filter "$shard_filter" \
  --logger "trx;LogFileName=schema-migration-sql-$shard.trx" \
  --results-directory "$results_root"
node scripts/ci/assert-trx-tests.mjs \
  "$results_root/schema-migration-sql-$shard.trx" \
  "PR schema migration SQL tests ($shard)" \
  "$expected_test_count"

# Mobile 绑定表使用独立测试项目；在同一专用 SQL Server lane 强制实跑，不能以 skip 假绿。只在 b 片执行一次。
if [[ "$shard" == "b" ]]; then
  mobile_activation_project="services/backend/BlazorApp.MobileDeviceActivation.Tests/BlazorApp.MobileDeviceActivation.Tests.csproj"
  dotnet restore "$mobile_activation_project"
  dotnet build "$mobile_activation_project" --configuration Release --no-restore
  dotnet test "$mobile_activation_project" \
    --configuration Release \
    --no-build \
    --filter 'FullyQualifiedName~BlazorApp.MobileDeviceActivation.Tests.MobileDeviceActivationSchemaSqlServerIntegrationTests' \
    --logger 'trx;LogFileName=mobile-device-activation-schema-sql.trx' \
    --results-directory "$results_root"
  node scripts/ci/assert-trx-tests.mjs \
    "$results_root/mobile-device-activation-schema-sql.trx" \
    'PR Mobile device activation schema SQL test' \
    1
fi
