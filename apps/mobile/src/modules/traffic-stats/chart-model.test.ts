import assert from "node:assert/strict";
import {
  areaPath,
  labelStep,
  monotonePath,
  nearestIndex,
  niceScale,
  plotHeight,
  projectSeries,
  seriesMax,
  tooltipLeft,
  xAt,
  yAt,
  type ChartLayout,
} from "./chart-model";

const layout: ChartLayout = {
  width: 334,
  height: 196,
  margin: { left: 34, right: 12, top: 12, bottom: 24 },
  count: 13,
  yMax: 80,
  mode: "line",
};

// 刻度：最多 5 格，顶部留白
assert.deepEqual(niceScale(66), { max: 80, step: 20 });
assert.deepEqual(niceScale(389), { max: 500, step: 100 });
assert.deepEqual(niceScale(312), { max: 400, step: 100 });
assert.deepEqual(niceScale(3), { max: 4, step: 1 });
assert.deepEqual(niceScale(0), { max: 2, step: 1 });
assert.equal(niceScale(1_000_000).max >= 1_000_000, true);
assert.equal(seriesMax([1, null, 5], [3, 2]), 5);

// 坐标：首尾贴着绘图区边缘；yMax 在顶部、0 在底部；超出范围夹紧
assert.equal(xAt(layout, 0), 34);
assert.equal(xAt(layout, 12), 334 - 12);
assert.equal(yAt(layout, 0), 12 + plotHeight(layout));
assert.equal(yAt(layout, 80), 12);
assert.equal(yAt(layout, 999), 12);
assert.equal(yAt(layout, -5), yAt(layout, 0));
// 柱状图每格居中
const bars: ChartLayout = { ...layout, mode: "bar", count: 7 };
assert.ok(Math.abs(xAt(bars, 0) - (34 + 288 / 14)) < 1e-9);
// 单点居中
assert.equal(xAt({ ...layout, count: 1 }, 0), 34 + 288 / 2);

// 触点吸附到最近的数据点
assert.equal(nearestIndex(layout, 0), 0);
assert.equal(nearestIndex(layout, 9999), 12);
assert.equal(nearestIndex(layout, xAt(layout, 4) + 3), 4);
assert.equal(nearestIndex({ ...layout, count: 1 }, 100), 0);

// 单调插值：曲线上所有控制点的 y 不越出相邻数据点范围（不过冲）
{
  const points = [0, 0, 10, 10, 0].map((y, i) => ({ x: i * 10, y }));
  const path = monotonePath(points);
  assert.ok(path.startsWith("M0,0 C"));
  const numbers = (path.match(/-?\d+(\.\d+)?/g) ?? []).map(Number);
  const ys = numbers.filter((_, index) => index % 2 === 1);
  assert.ok(Math.min(...ys) >= 0 && Math.max(...ys) <= 10, "控制点不应过冲");
}
assert.equal(monotonePath([]), "");
assert.equal(monotonePath([{ x: 1, y: 2 }]), "M1,2");

// 序列投影：前导 null 跳过，首个缺口之后不再连线
{
  const points = projectSeries(layout, [null, 5, 7, null, 9]);
  assert.deepEqual(points.map((p) => p.index), [1, 2]);
  assert.deepEqual(projectSeries(layout, [null, null]), []);
}

// 面积封口
{
  const points = [{ x: 0, y: 5 }, { x: 10, y: 3 }];
  assert.ok(areaPath("M0,5 C", points, 20).endsWith("L10,20 L0,20 Z"));
  assert.equal(areaPath("", points, 20), "");
}

// 提示框夹在绘图区内
assert.equal(tooltipLeft(layout, 0, 120), 34);
assert.equal(tooltipLeft(layout, 9999, 120), 334 - 12 - 120);
assert.equal(tooltipLeft(layout, 150, 120), 90);

// 横轴标签间隔
assert.equal(labelStep(7), 1);
assert.equal(labelStep(13), 2);
assert.equal(labelStep(24), 4);
assert.equal(labelStep(31), 4);

console.log("traffic-stats chart-model tests passed");
