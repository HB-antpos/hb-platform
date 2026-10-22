import assert from 'node:assert/strict'
import { defaultCurve } from './pricingCurve'
import {
  MAX_INTERVALS, actualPrice, addInterval, chartRange, endPrice, intervalAt, intervalsError, isEditable, isLegalTail, linkedFlags,
  breakLink, makeCurve, makeFixed, priceDrops, relink, removeInterval, setBoundary, setBend, setEndpoint, setKind, setRate, setShape, startPrice,
  shiftBrokenAfterAdd, shiftBrokenAfterRemove, visibleMaxPrice, type Rule,
} from './pricingIntervals'

const NONE = new Set<number>()
const must = (r: { rules?: Rule[]; error?: string }) => { assert.equal(r.error, undefined, r.error); return r.rules! }
// 与后端测试同一组数据：≤10 固定 2.5×，10～50 曲线（25.00 → 100.00），>50 固定 2.0×
const mixed = (): Rule[] => [makeFixed(0.1, 10, 2.5), makeCurve(10, 50, 25, 100), makeFixed(50, 99999, 2)]

// ---- 校验：与后端 Validate 同口径 ----
assert.equal(intervalsError(defaultCurve()), null)
assert.equal(intervalsError(mixed()), null, '曲线端点跟随固定交界价时不要求合法尾数')
assert.ok(intervalsError([makeFixed(0.1, 10, 2.5), makeCurve(10, 50, 26, 100)]), '起点价与固定交界价对不上时仍要求合法尾数')
assert.ok(intervalsError([makeCurve(1, 10, 2.5, 24.99), makeCurve(10, 50, 25, 100)]), '曲线-曲线交界的 25.00 不是合法尾数')
assert.ok(intervalsError([makeFixed(0.1, 10, 3), makeFixed(11, 50, 2.5)]), '空隙')
assert.ok(intervalsError([makeFixed(0.1, 10, 3), makeFixed(9, 50, 2.5)]), '重叠')
assert.equal(intervalsError([makeFixed(0.1, 10, 3), makeFixed(10, 50, 2.5)]), null, '阶梯定价允许交界价差')
assert.ok(intervalsError([makeFixed(0.1, 50, 5.1)]), '成率上限')
assert.ok(intervalsError([makeFixed(0.1, 50, 1.4)]), '成率下限')
assert.ok(intervalsError([makeCurve(10, 50, 24.99, 24.5)]), '区间内售价不能下降')
const tens = (n: number) => Array.from({ length: n }, (_, i) => makeFixed(0.1 + i * 10, 10.1 + i * 10, 2.5))
assert.equal(intervalsError(tens(MAX_INTERVALS)), null)
assert.ok(intervalsError(tens(MAX_INTERVALS + 1))?.includes('10'))
assert.ok(isEditable(mixed()))
assert.ok(!isEditable([{ ...makeFixed(0.1, 10, 2), algorithm: 'Step' }]))
assert.ok(!isEditable([{ ...makeFixed(0.1, 10, 2), algorithm: 'Linear' }]), '没有节点价的旧版 Linear 不可用新编辑器')

// ---- 交界归属：上限含、下限不含 ----
assert.equal(intervalAt(mixed(), 10), 0)
assert.equal(intervalAt(mixed(), 10.01), 1)
assert.equal(intervalAt(mixed(), 50), 1)
assert.equal(intervalAt(mixed(), 50.01), 2)
assert.equal(intervalAt(mixed(), 0.1), 0)
assert.equal(intervalAt(mixed(), 0.05), -1)
assert.equal(actualPrice(mixed()[0], 10), 25)
assert.equal(actualPrice(makeFixed(10, 50, 2.5), 10.01), 25.03, '固定成率到分，不调整尾数')
assert.equal(actualPrice(mixed()[1], 50), 99.99, '曲线区间仍做尾数调整')

// ---- 回落检测与联动标记 ----
assert.deepEqual(priceDrops(mixed()), [])
const stepDown = [makeFixed(0.1, 10, 3), makeFixed(10, 50, 2.5)]
assert.deepEqual(priceDrops(stepDown).map(d => [d.at, d.from, d.to]), [[10, 30, 25]])
assert.deepEqual(linkedFlags(mixed(), NONE), [true, true])
assert.deepEqual(linkedFlags(mixed(), new Set([0])), [false, true], '用户断开的交界不联动')
assert.deepEqual(linkedFlags(stepDown, NONE), [false], '两个固定区间相邻没有联动')
assert.deepEqual(linkedFlags(defaultCurve(), NONE), [true, true], "3 个曲线区间有 2 个交界")
assert.deepEqual([...shiftBrokenAfterAdd(new Set([0, 2]), 1)].sort(), [0, 3])
assert.deepEqual([...shiftBrokenAfterRemove(new Set([0, 1, 3]), 2)].sort(), [0, 2], '合并掉的交界清除，其后的下标前移')

// ---- 固定成率：联动与断开 ----
{
  const r = must(setRate(mixed(), 0, 3, linkedFlags(mixed(), NONE)))
  assert.equal(r[0].startRate, 3)
  assert.equal(startPrice(r[1]), 30, '联动时曲线起点跟随 交界成本 × 成率')
  assert.deepEqual(priceDrops(r), [])
  // 不先断开就改成率，曲线起点既不跟随固定价、也不是合法尾数，会被校验拒绝
  assert.ok(setRate(mixed(), 0, 3, linkedFlags(mixed(), new Set([0]))).error)
  // 断开联动：25.00 不是合法尾数，吸附到 24.99；之后改成率，曲线起点保持不变，交界出现回落
  const detached = must(breakLink(mixed(), 0))
  assert.equal(startPrice(detached[1]), 24.99)
  assert.equal(intervalsError(detached), null)
  const broken = must(setRate(detached, 0, 3, linkedFlags(detached, new Set([0]))))
  assert.equal(startPrice(broken[1]), 24.99, '断开后曲线起点保持不变')
  assert.deepEqual(priceDrops(broken).map(d => [d.from, d.to]), [[30, 24.99]])
  // 重新联动后回到连续
  const again = must(relink(broken, 0, 'left'))
  assert.equal(startPrice(again[1]), 30)
  assert.deepEqual(priceDrops(again), [])
  assert.ok(setRate(mixed(), 0, 5.5, [true, true]).error)
  assert.ok(setRate(mixed(), 1, 2.5, [true, true]).error, '曲线区间不能设成率')
}

// ---- 交界成本 ----
{
  const flags = linkedFlags(mixed(), NONE)
  const r = must(setBoundary(mixed(), 0, 8, flags))
  assert.equal(r[0].maxPrice, 8)
  assert.equal(r[1].minPrice, 8)
  assert.equal(startPrice(r[1]), 20, '联动：曲线起点 = 8 × 2.5')
  assert.ok(setBoundary(mixed(), 0, 60, flags).error, '不能越过下一个区间的上限')
  assert.ok(setBoundary(mixed(), 0, 0.1, flags).error, '不能落到本区间下限')
  const curveBoundary = must(setBoundary(defaultCurve(), 0, 30, linkedFlags(defaultCurve(), NONE)))
  assert.equal(curveBoundary[0].maxPrice, 30)
  assert.equal(curveBoundary[1].minPrice, 30)
  assert.equal(endPrice(curveBoundary[0]), startPrice(curveBoundary[1]), '曲线-曲线交界联动后售价相同')
}

// ---- 曲线端点 ----
{
  const flags = linkedFlags(defaultCurve(), NONE)
  const r = must(setEndpoint(defaultCurve(), 0, 'end', 150.5, flags))
  assert.equal(endPrice(r[0]), 150.5)
  assert.equal(startPrice(r[1]), 150.5, '与曲线邻居联动时两侧同步')
  assert.ok(setEndpoint(mixed(), 1, 'start', 30, linkedFlags(mixed(), NONE)).error, '跟随固定区间时要先断开')
  const free = must(setEndpoint(mixed(), 1, 'start', 24.99, linkedFlags(mixed(), new Set([0]))))
  assert.equal(startPrice(free[1]), 24.99, '断开后可独立设置（并吸附到合法尾数）')
  assert.deepEqual(priceDrops(free).map(d => [d.from, d.to]), [[25, 24.99]])
  assert.ok(setEndpoint(mixed(), 0, 'start', 3, [true, true]).error, '固定区间没有端点')
}

// ---- 类型切换 ----
{
  const flags = linkedFlags(mixed(), NONE)
  const toCurve = must(setKind(mixed(), 0, 'curve', flags))
  assert.equal(toCurve[0].algorithm, 'Linear')
  assert.ok(isLegalTail(endPrice(toCurve[0])), '固定→曲线后端点吸附到合法尾数')
  assert.equal(startPrice(toCurve[1]), endPrice(toCurve[0]), '曲线-曲线交界保持连续')
  const toFixed = must(setKind(mixed(), 1, 'fixed', flags))
  assert.equal(toFixed[1].algorithm, 'Fixed')
  assert.equal(toFixed[1].startRate, 2.25, '曲线→固定取两端成率平均值：(2.5 + 2.0) / 2')
  assert.ok(setKind(mixed(), 2, 'curve', flags).error, '上限为「不限」的区间不能改曲线')
  const unchanged = mixed()
  assert.equal(setKind(unchanged, 0, 'fixed', flags).rules, unchanged, '类型未变时原样返回')
}

// ---- 形状与弧度 ----
{
  const up = must(setShape(defaultCurve(), 1, 'ArcUp'))
  assert.equal(up[1].algorithm, 'ArcUp')
  assert.equal(up[1].curveBend, 0)
  const bent = must(setBend(up, 1, 50))
  assert.ok((bent[1].curveBend ?? 0) > 0)
  assert.equal(intervalsError(bent), null)
  assert.ok(setShape(mixed(), 0, 'ArcUp').error)
}

// ---- 添加 / 删除区间 ----
{
  const added = must(addInterval(mixed(), 2))
  assert.equal(added.length, 4)
  assert.deepEqual(added.slice(2).map(r => [r.minPrice, r.maxPrice, r.algorithm]), [[50, 100, 'Fixed'], [100, 99999, 'Fixed']], '不限上限时取下限的两倍作为拆分点')
  const split = must(addInterval(defaultCurve(), 0))
  assert.equal(split.length, 4)
  assert.equal(endPrice(split[0]), startPrice(split[1]), '曲线拆分后交界连续')
  assert.equal(intervalsError(split), null)
  assert.ok(addInterval(tens(MAX_INTERVALS), 0).error, '已达 10 个区间')
  const removed = must(removeInterval(mixed(), 1))
  assert.equal(removed.length, 2)
  assert.equal(removed[0].maxPrice, 50, '并入上一个区间')
  assert.equal(removed[1].minPrice, 50)
  const first = must(removeInterval(defaultCurve(), 0))
  assert.equal(first[0].minPrice, 1)
  assert.equal(first.length, 2)
  assert.ok(removeInterval([makeFixed(0.1, 99999, 2.5)], 0).error)
}

// ---- 图表范围 ----
{
  const range = chartRange(mixed(), null)
  assert.equal(range.first, 0.1)
  assert.equal(range.fullEnd, 100, '不限上限时显示到最后一个交界的两倍（至少 100）')
  assert.deepEqual(range.presets, [10, 50])
  assert.equal(chartRange(mixed(), 50).end, 50)
  assert.equal(chartRange(mixed(), 500).end, 100, '超出全范围时退回全部')
  assert.equal(chartRange(defaultCurve(), null).fullEnd, 999)
  assert.ok(visibleMaxPrice(mixed(), 100) >= 200)
}

console.log('区间式定价逻辑测试通过')
