import test from 'node:test';
import assert from 'node:assert/strict';
import {
  formatLastYearRange,
  lastYearWeekday,
  normalizeLastYear,
  normalizeLastYearDays,
  pickLastYearQuantity,
} from '../src/lib/last-year.js';
import { computeButtonState, normalizeSummaryItem } from '../src/lib/dats-state.js';

test('窗口天数只认 30 / 60，其它值回到默认 30 天', () => {
  assert.equal(normalizeLastYearDays(60), 60);
  assert.equal(normalizeLastYearDays('60'), 60);
  assert.equal(normalizeLastYearDays(30), 30);
  assert.equal(normalizeLastYearDays(undefined), 30);
  assert.equal(normalizeLastYearDays('90'), 30);
});

test('后端字段归一化：0 保留，缺失或类型不对整体不可用', () => {
  const ok = normalizeLastYear({
    lastYearWindowStartDate: '2025-11-09',
    lastYearSalesQuantity30Days: 0,
    lastYearSalesQuantity60Days: 14,
  });
  assert.deepEqual(ok, { windowStartDate: '2025-11-09', quantity30: 0, quantity60: 14 });
  assert.equal(normalizeLastYear({}), null);
  assert.equal(normalizeLastYear(null), null);
  assert.equal(normalizeLastYear({ lastYearWindowStartDate: '2025-11-09', lastYearSalesQuantity30Days: null, lastYearSalesQuantity60Days: 3 }), null);
  assert.equal(normalizeLastYear({ lastYearWindowStartDate: 'bad', lastYearSalesQuantity30Days: 1, lastYearSalesQuantity60Days: 3 }), null);
  assert.equal(normalizeLastYear({ lastYearWindowStartDate: '2025-11-09', lastYearSalesQuantity30Days: '5', lastYearSalesQuantity60Days: '9' }), null);
});

test('按所选窗口取销量，不可用为 null', () => {
  const lastYear = { windowStartDate: '2025-11-09', quantity30: 0, quantity60: 14 };
  assert.equal(pickLastYearQuantity(lastYear, 30), 0);
  assert.equal(pickLastYearQuantity(lastYear, 60), 14);
  assert.equal(pickLastYearQuantity(lastYear, 'x'), 0);
  assert.equal(pickLastYearQuantity(null, 30), null);
});

test('日期范围含首尾；同年只在终点写年份，跨年两端都写', () => {
  assert.equal(formatLastYearRange('2025-11-09', 30), '9 Nov – 8 Dec 2025');
  assert.equal(formatLastYearRange('2025-11-09', 60), '9 Nov 2025 – 7 Jan 2026');
  assert.equal(formatLastYearRange('2025-03-29', 30), '29 Mar – 27 Apr 2025');
  assert.equal(formatLastYearRange('2025-11-09T00:00:00', 30), '9 Nov – 8 Dec 2025');
  assert.equal(formatLastYearRange(null, 30), '');
});

test('起点星期几：2025-11-09 是周日，与 2026-11-08 同为周日', () => {
  assert.equal(lastYearWeekday('2025-11-09', 'zh'), '周日');
  assert.equal(lastYearWeekday('2025-11-09', 'en'), 'Sunday');
  assert.equal(lastYearWeekday('bad', 'zh'), '');
});

test('摘要归一化带上去年同期；旧后端没有字段时按钮状态与改动前完全一致', () => {
  const withLastYear = normalizeSummaryItem({
    matchStatus: 'matched',
    latestPurchaseDate: '2026-10-27',
    latestPurchaseQuantity: 48,
    salesSinceLatestPurchase: 21,
    lastYearWindowStartDate: '2025-11-09',
    lastYearSalesQuantity30Days: 34,
    lastYearSalesQuantity60Days: 58,
  });
  const state = computeButtonState(withLastYear);
  assert.equal(state.kind, 'ok');
  assert.deepEqual(state.lastYear, { windowStartDate: '2025-11-09', quantity30: 34, quantity60: 58 });

  const legacy = computeButtonState(normalizeSummaryItem({
    matchStatus: 'matched',
    latestPurchaseDate: '2026-10-27',
    latestPurchaseQuantity: 48,
    salesSinceLatestPurchase: 21,
  }));
  assert.equal('lastYear' in legacy, false);
  assert.deepEqual(legacy, {
    kind: 'ok',
    lastOrderDate: '2026-10-27',
    lastOrderQuantity: 48,
    salesToDate: 21,
  });
});

test('无采购也带去年同期；没匹配商品不带', () => {
  const noPurchase = computeButtonState(normalizeSummaryItem({
    matchStatus: 'no-purchase',
    lastYearWindowStartDate: '2025-11-09',
    lastYearSalesQuantity30Days: 2,
    lastYearSalesQuantity60Days: 5,
  }));
  assert.equal(noPurchase.reason, 'noPurchase');
  assert.deepEqual(noPurchase.lastYear, { windowStartDate: '2025-11-09', quantity30: 2, quantity60: 5 });

  const unmatched = computeButtonState(normalizeSummaryItem({ matchStatus: 'unmatched' }));
  assert.equal('lastYear' in unmatched, false);
});
