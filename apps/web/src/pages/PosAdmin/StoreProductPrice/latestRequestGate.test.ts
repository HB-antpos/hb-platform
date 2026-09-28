import assert from 'node:assert/strict'

import { createLatestRequestGate, type LatestRequestGate } from './latestRequestGate'

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => { resolve = done })
  return { promise, resolve }
}

// 模拟页面的查询函数：响应回来时只有仍是最后一次查询才写入表格
async function load(gate: LatestRequestGate, request: Promise<string>, shown: string[]) {
  const seq = gate.begin()
  const result = await request
  if (!gate.isLatest(seq)) return
  shown.push(result)
}

async function main() {
  // 先发的慢请求（旧分店选择）晚于后发的快请求返回，不能覆盖新结果
  const gate = createLatestRequestGate()
  const shown: string[] = []
  const slowOld = deferred<string>()
  const fastNew = deferred<string>()
  const first = load(gate, slowOld.promise, shown)
  const second = load(gate, fastNew.promise, shown)
  fastNew.resolve('新分店')
  await second
  slowOld.resolve('旧分店')
  await first
  assert.deepEqual(shown, ['新分店'], '旧响应晚到时应被丢弃，表格保留最后一次查询的结果')

  // 前一个请求还没返回时发起的新请求必须照常发出并生效（旧写法会直接跳过它）
  const gate2 = createLatestRequestGate()
  const shown2: string[] = []
  const a = deferred<string>()
  const b = deferred<string>()
  const loadA = load(gate2, a.promise, shown2)
  const loadB = load(gate2, b.promise, shown2)
  a.resolve('第 1 页')
  b.resolve('第 2 页')
  await Promise.all([loadA, loadB])
  assert.deepEqual(shown2, ['第 2 页'], '按顺序返回时也只采用最后一次查询')

  // 各自独立的闸门互不影响，单次查询正常生效
  const gate3 = createLatestRequestGate()
  const shown3: string[] = []
  await load(gate3, Promise.resolve('单次'), shown3)
  assert.deepEqual(shown3, ['单次'])

  console.log('latestRequestGate.test: ok')
}

void main().catch((error) => {
  console.error(error)
  process.exit(1)
})
