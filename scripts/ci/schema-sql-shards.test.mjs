import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  MINIMUM_TESTS_PER_SHARD,
  RUSTDESK_SCHEMA_TEST,
  SCHEMA_SQL_SHARDS,
  SCHEMA_TEST_CLASS,
  SHARD_A_PREFIXES,
  assertShardCoverage,
  isSchemaSqlTest,
  parseTestListing,
  shardFilter,
  shardMatches,
} from './schema-sql-shards.mjs'

const shardsScriptPath = fileURLToPath(new URL('./schema-sql-shards.mjs', import.meta.url))
const schemaTestsSourcePath = new URL(
  '../../services/backend/BlazorApp.Api.Tests/SchemaMigrationSqlServerIntegrationTests.cs',
  import.meta.url,
)

// 从测试源码读出全部 Schema 用例方法名，和 CI 里 --list-tests 得到的集合一致。
function schemaTestNamesFromSource() {
  const source = readFileSync(schemaTestsSourcePath, 'utf8')
  const names = [...source.matchAll(/^\s*\[SchemaMigrationSqlServerFact[^\n]*\n(?:\s*\[[^\n]*\n)*\s*public (?:async )?(?:Task|void) (\S+?)\(/gm)]
    .map((match) => `${SCHEMA_TEST_CLASS}.${match[1]}`)
  assert.ok(names.length >= 70, `源码里只识别出 ${names.length} 个 Schema 用例`)
  return names
}

function listingFrom(names) {
  return [
    'Microsoft (R) Test Execution Command Line Tool',
    'The following Tests are available:',
    ...names.map((name) => `    ${name}`),
    '',
  ].join('\n')
}

test('两个分片的过滤表达式由同一份前缀定义生成', () => {
  assert.deepEqual(SCHEMA_SQL_SHARDS, ['a', 'b'])
  const a = shardFilter('a')
  const b = shardFilter('b')
  for (const prefix of SHARD_A_PREFIXES) {
    assert.ok(a.includes(`FullyQualifiedName~${SCHEMA_TEST_CLASS}.${prefix}`), prefix)
    assert.ok(b.includes(`(FullyQualifiedName!~${SCHEMA_TEST_CLASS}.${prefix})`), prefix)
  }
  assert.ok(a.includes(`FullyQualifiedName~${RUSTDESK_SCHEMA_TEST}`))
  assert.ok(b.startsWith(`(FullyQualifiedName~${SCHEMA_TEST_CLASS}.)&`))
  assert.doesNotMatch(b, /RustDesk/)
  assert.throws(() => shardFilter('c'), /未知 Schema SQL 分片/)
})

test('源码里的每个 Schema 用例恰好属于一个分片，RustDesk 用例随 a 片执行', () => {
  const names = [...schemaTestNamesFromSource(), RUSTDESK_SCHEMA_TEST]
  const counts = assertShardCoverage(names)
  assert.equal(counts.a + counts.b, names.length)
  assert.ok(counts.a >= MINIMUM_TESTS_PER_SHARD && counts.b >= MINIMUM_TESTS_PER_SHARD, JSON.stringify(counts))
  assert.ok(shardMatches('a', RUSTDESK_SCHEMA_TEST))
  assert.ok(!shardMatches('b', RUSTDESK_SCHEMA_TEST))

  // 重用例按实测耗时分到两片：已*/POSM* 归 a，空*/真实*/旧POSM*/Linkly*/使用帮助FAQ_协调器 归 b。
  const heavy = (method) => `${SCHEMA_TEST_CLASS}.${method}`
  for (const method of ['已登记Baseline后_生产Runtime仍可执行并登记后续版本步骤', 'POSM激活表不兼容_主库账本保留且修复后只续跑POSM']) {
    assert.ok(names.includes(heavy(method)), method)
    assert.ok(shardMatches('a', heavy(method)), method)
  }
  for (const method of [
    '空隔离库_迁移检查并重复迁移_两个账本均保持正确',
    '真实API进程_显式命令不监听且普通启动通过门禁后才监听',
    '旧POSM账本缺少Linkly步骤_迁移只补齐多终端结构并登记新版本',
    'Linkly同名错误Check或Cascade外键_检查返回稳定不兼容诊断',
    '使用帮助FAQ_协调器迁移登记账本且启动门禁通过',
  ]) {
    assert.ok(names.includes(heavy(method)), method)
    assert.ok(shardMatches('b', heavy(method)), method)
  }
})

test('清单解析只取标题行之后缩进 4 格的方法全名，并忽略 Theory 参数后缀', () => {
  const listing = listingFrom([
    `${SCHEMA_TEST_CLASS}.门店分时客流报表表_可重复执行`,
    `${SCHEMA_TEST_CLASS}.空隔离库_迁移检查(mode: "strict")`,
    'BlazorApp.Api.Tests.OtherTests.Unrelated',
  ])
  assert.deepEqual(parseTestListing(listing), [
    'BlazorApp.Api.Tests.OtherTests.Unrelated',
    `${SCHEMA_TEST_CLASS}.空隔离库_迁移检查`,
    `${SCHEMA_TEST_CLASS}.门店分时客流报表表_可重复执行`,
  ])
  assert.throws(() => parseTestListing('no header'), /缺少标题行/)
})

test('完整性校验拒绝空清单、过少用例与落不进分片的用例', () => {
  assert.throws(() => assertShardCoverage(['BlazorApp.Api.Tests.OtherTests.Unrelated']), /没有任何 Schema SQL 用例/)

  const few = Array.from({ length: MINIMUM_TESTS_PER_SHARD - 1 }, (_, index) => `${SCHEMA_TEST_CLASS}.门店表${index}`)
  assert.throws(() => assertShardCoverage([...few, ...few.map((name) => `${name}_其他`)]), /低于下限/)

  // 名字只带类名片段（类名后没有紧跟“.”）的用例不属于 Schema 集合：既不被 a 片前缀误收，也不计入 b 片。
  const foreign = `${SCHEMA_TEST_CLASS}Extra.已有`
  assert.ok(!isSchemaSqlTest(foreign))
  assert.ok(!shardMatches('a', foreign) && !shardMatches('b', foreign))
  const names = [...schemaTestNamesFromSource(), RUSTDESK_SCHEMA_TEST]
  assert.deepEqual(assertShardCoverage([...names, foreign]), assertShardCoverage(names))
})

test('命令行 filter 与 coverage 子命令可直接供 run-schema-sql.sh 使用', () => {
  const filter = spawnSync(process.execPath, [shardsScriptPath, 'filter', 'b'], { encoding: 'utf8' })
  assert.equal(filter.status, 0, filter.stderr)
  assert.equal(filter.stdout.trim(), shardFilter('b'))

  const names = [...schemaTestNamesFromSource(), RUSTDESK_SCHEMA_TEST, 'BlazorApp.Api.Tests.OtherTests.Unrelated']
  const coverage = spawnSync(process.execPath, [shardsScriptPath, 'coverage', 'a'], {
    encoding: 'utf8',
    input: listingFrom(names),
  })
  assert.equal(coverage.status, 0, coverage.stderr)
  assert.equal(Number(coverage.stdout.trim()), assertShardCoverage(names).a)
  assert.match(coverage.stderr, /Schema SQL 分片完整性校验通过: a=\d+, b=\d+/)

  const unknown = spawnSync(process.execPath, [shardsScriptPath, 'coverage', 'z'], { encoding: 'utf8', input: '' })
  assert.notEqual(unknown.status, 0)
  assert.match(unknown.stderr, /未知 Schema SQL 分片/)
})
