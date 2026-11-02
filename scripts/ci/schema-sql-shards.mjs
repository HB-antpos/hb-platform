import { readFileSync } from 'node:fs'
import { pathToFileURL } from 'node:url'

export const SCHEMA_TEST_CLASS = 'BlazorApp.Api.Tests.SchemaMigrationSqlServerIntegrationTests'
export const RUSTDESK_SCHEMA_TEST =
  'BlazorApp.Api.Tests.RustDeskClientSchemaTests.SqlServerMigrationIsIdempotentAndRejectsMissingColumns'

export const SCHEMA_SQL_SHARDS = Object.freeze(['a', 'b'])

// 分片按方法名前缀切：a 片列出前缀，b 片是 SchemaMigrationSqlServerIntegrationTests 里其余全部用例，新增用例默认落入 b。
// 80 个用例里 10 个要跑完整迁移协调器、各约 25 秒（已*、POSM*、空*、真实*、旧POSM*、Linkly*、使用帮助FAQ_协调器），
// 其余约 70 个合计只有 40 多秒；按 2026-11-02 两次 TRX 的均值切成 a≈155 秒（含 RustDesk 一例）/ b≈150 秒
// （b 另跑约 7 秒的 Mobile 绑定表项目）。改前缀时用 scripts/ci/schema-sql-shards.test.mjs 的分组用例核对归属。
export const SHARD_A_PREFIXES = Object.freeze(['已', 'POSM', 'MYOB', '考勤', '供应商', '门店', '分店', '人脸'])
export const SHARD_A_EXTRA_TESTS = Object.freeze([RUSTDESK_SCHEMA_TEST])

// 每片至少要有这么多用例，防止分片过滤整体失配后 TRX 只跑零星用例假绿。
export const MINIMUM_TESTS_PER_SHARD = 10

const classMethodPrefix = `${SCHEMA_TEST_CLASS}.`

function assertKnownShard(shard) {
  if (!SCHEMA_SQL_SHARDS.includes(shard)) {
    throw new Error(`未知 Schema SQL 分片: ${shard}（可选 ${SCHEMA_SQL_SHARDS.join('、')}）`)
  }
}

// 与 VSTest 过滤的 ~ 语义一致：不区分大小写的包含匹配；前缀紧跟类名，等价于方法名前缀。
function containsIgnoreCase(testName, fragment) {
  return testName.toLowerCase().includes(fragment.toLowerCase())
}

function isSchemaClassTest(testName) {
  return containsIgnoreCase(testName, classMethodPrefix)
}

function matchesShardAPrefix(testName) {
  return SHARD_A_PREFIXES.some((prefix) => containsIgnoreCase(testName, `${classMethodPrefix}${prefix}`))
}

export function shardMatches(shard, testName) {
  assertKnownShard(shard)
  if (shard === 'a') {
    return SHARD_A_EXTRA_TESTS.some((name) => containsIgnoreCase(testName, name)) || matchesShardAPrefix(testName)
  }
  return isSchemaClassTest(testName) && !matchesShardAPrefix(testName)
}

// 同一份定义既生成 dotnet test 过滤表达式，也用于完整性校验，保证两者不会各自漂移。
export function shardFilter(shard) {
  assertKnownShard(shard)
  if (shard === 'a') {
    const terms = [
      ...SHARD_A_PREFIXES.map((prefix) => `FullyQualifiedName~${classMethodPrefix}${prefix}`),
      ...SHARD_A_EXTRA_TESTS.map((name) => `FullyQualifiedName~${name}`),
    ]
    return `(${terms.join('|')})`
  }
  return [
    `(FullyQualifiedName~${classMethodPrefix})`,
    ...SHARD_A_PREFIXES.map((prefix) => `(FullyQualifiedName!~${classMethodPrefix}${prefix})`),
  ].join('&')
}

// 解析 `dotnet test --list-tests` 的输出：标题行之后缩进 4 格的行是测试名；Theory 用例带参数后缀，只取方法全名。
export function parseTestListing(listing) {
  const lines = listing.split(/\r?\n/)
  const headerIndex = lines.findIndex((line) => line.trim() === 'The following Tests are available:')
  if (headerIndex < 0) {
    throw new Error('测试清单缺少标题行 "The following Tests are available:"，无法校验分片完整性')
  }
  const names = new Set()
  for (const line of lines.slice(headerIndex + 1)) {
    if (/^\s{4}\S/.test(line)) {
      names.add(line.trim().split('(', 1)[0])
    }
  }
  return [...names].sort()
}

export function isSchemaSqlTest(testName) {
  return isSchemaClassTest(testName) || SHARD_A_EXTRA_TESTS.some((name) => containsIgnoreCase(testName, name))
}

// 用全量清单确认每个 Schema SQL 用例恰好属于一个分片，并返回各分片的用例数（作为 TRX 的最低执行数）。
export function assertShardCoverage(testNames) {
  const schemaTests = testNames.filter(isSchemaSqlTest)
  if (schemaTests.length === 0) {
    throw new Error('测试清单里没有任何 Schema SQL 用例，拒绝假绿')
  }

  const counts = Object.fromEntries(SCHEMA_SQL_SHARDS.map((shard) => [shard, 0]))
  const problems = []
  for (const testName of schemaTests) {
    const matched = SCHEMA_SQL_SHARDS.filter((shard) => shardMatches(shard, testName))
    if (matched.length !== 1) {
      problems.push(`${testName} -> [${matched.join(', ')}]`)
      continue
    }
    counts[matched[0]] += 1
  }
  if (problems.length > 0) {
    throw new Error(
      `Schema SQL 有 ${problems.length} 个用例未落入或重复落入分片（最多显示 20 条）:\n  ${problems.slice(0, 20).join('\n  ')}`,
    )
  }
  for (const shard of SCHEMA_SQL_SHARDS) {
    if (counts[shard] < MINIMUM_TESTS_PER_SHARD) {
      throw new Error(`Schema SQL 分片 ${shard} 只有 ${counts[shard]} 个用例，低于下限 ${MINIMUM_TESTS_PER_SHARD}`)
    }
  }
  return counts
}

function main() {
  const [command, shard, listingPath] = process.argv.slice(2)
  if (command === 'filter') {
    process.stdout.write(`${shardFilter(shard)}\n`)
    return
  }
  if (command === 'coverage') {
    assertKnownShard(shard)
    const listing = readFileSync(listingPath ?? 0, 'utf8')
    const counts = assertShardCoverage(parseTestListing(listing))
    console.error(
      `Schema SQL 分片完整性校验通过: ${SCHEMA_SQL_SHARDS.map((name) => `${name}=${counts[name]}`).join(', ')}`,
    )
    process.stdout.write(`${counts[shard]}\n`)
    return
  }
  throw new Error('用法: node schema-sql-shards.mjs filter <shard> | coverage <shard> [listing-file]')
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main()
}
