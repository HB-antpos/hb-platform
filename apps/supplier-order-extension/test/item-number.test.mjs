import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_ITEM_NUMBER_LENGTH,
  normalizeCaptureItemNumber,
  normalizeCaptureItemNumbers,
  readItemNumberFrom,
  readItemNumbersFromCards,
} from '../src/lib/item-number.js';

// 最小 DOM 替身：只实现 readItemNumberFrom 用到的 querySelector/getAttribute/textContent。
function fakeElement({ attributes = {}, text = '', children = {} } = {}) {
  return {
    textContent: text,
    getAttribute: (name) => (name in attributes ? attributes[name] : null),
    querySelector: (selector) => children[selector] || null,
  };
}

test('attribute 来源按声明式 transforms 读取（DATS）', () => {
  const card = fakeElement({ attributes: { 'data-product-code': ' ab123 ' } });
  const itemCfg = {
    source: 'attribute',
    selector: null,
    attribute: 'data-product-code',
    transforms: ['trim', 'uppercase'],
  };
  assert.equal(readItemNumberFrom(card, itemCfg), 'AB123');
});

test('text 来源经子选择器读取（TXK SKU 前缀）', () => {
  const card = fakeElement({ children: { '.sku': fakeElement({ text: ' - SKU tx-9 ' }) } });
  const itemCfg = {
    source: 'text',
    selector: '.sku',
    attribute: null,
    transforms: ['after-sku', 'trim', 'uppercase'],
  };
  assert.equal(readItemNumberFrom(card, itemCfg), 'TX-9');
});

test('子选择器缺失或配置缺失时返回空串', () => {
  const card = fakeElement();
  assert.equal(readItemNumberFrom(card, { source: 'text', selector: '.missing', transforms: [] }), '');
  assert.equal(readItemNumberFrom(null, { source: 'text' }), '');
  assert.equal(readItemNumberFrom(card, null), '');
});

test('非法 transform 抛错，由调用方隔离', () => {
  const card = fakeElement({ text: 'x' });
  assert.throws(() => readItemNumberFrom(card, { source: 'text', transforms: ['eval'] }));
});

test('分类回传货号归一化：去空白、大写、超长与控制字符丢弃', () => {
  assert.equal(normalizeCaptureItemNumber('  ab-1 '), 'AB-1');
  assert.equal(normalizeCaptureItemNumber(''), '');
  assert.equal(normalizeCaptureItemNumber(null), '');
  assert.equal(normalizeCaptureItemNumber('x'.repeat(MAX_ITEM_NUMBER_LENGTH + 1)), '');
  assert.equal(normalizeCaptureItemNumber('a\u0000b'), '');
  assert.deepEqual(normalizeCaptureItemNumbers(['a', 'A', ' b ', '', null]), ['A', 'B']);
});

test('批量读取：单卡失败不影响其余卡片，结果去重保序', () => {
  const itemCfg = {
    source: 'attribute',
    selector: null,
    attribute: 'data-code',
    transforms: ['trim'],
  };
  const broken = {
    getAttribute: () => {
      throw new Error('detached');
    },
  };
  const cards = [
    fakeElement({ attributes: { 'data-code': 'b2' } }),
    broken,
    fakeElement({ attributes: { 'data-code': 'a1' } }),
    fakeElement({ attributes: { 'data-code': 'B2' } }),
    fakeElement({ attributes: {} }),
  ];
  assert.deepEqual(readItemNumbersFromCards(cards, itemCfg), ['B2', 'A1']);
});
