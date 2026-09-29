import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MESSAGES,
  categoryErrorMessageKey,
  normalizeLocale,
  resolveInitialLocale,
  t,
} from '../src/lib/i18n.js';
import { CRAWL_ERROR_CODES } from '../src/lib/category-crawl.js';

test('normalizeLocale', () => {
  assert.equal(normalizeLocale('en'), 'en');
  assert.equal(normalizeLocale('zh'), 'zh');
  assert.equal(normalizeLocale('fr'), 'zh');
  assert.equal(normalizeLocale(null), 'zh');
});

test('首次打开按系统语言选择中文或英文', () => {
  for (const systemLocale of ['zh', 'zh-CN', 'zh-TW', 'zh-Hans', 'zh-Hant']) {
    assert.equal(resolveInitialLocale(null, [systemLocale]), 'zh', systemLocale);
  }

  assert.equal(resolveInitialLocale(undefined, ['en-AU']), 'en');
  assert.equal(resolveInitialLocale(undefined, ['fr-FR']), 'en');
  assert.equal(resolveInitialLocale(undefined, []), 'en');
});

test('已保存语言优先，非法保存值回退系统语言', () => {
  assert.equal(resolveInitialLocale('zh', ['en-AU']), 'zh');
  assert.equal(resolveInitialLocale('en', ['zh-CN']), 'en');
  assert.equal(resolveInitialLocale('fr', ['zh-TW']), 'zh');
  assert.equal(resolveInitialLocale('fr', ['de-DE']), 'en');
});

test('t 语言区分与回退', () => {
  assert.equal(typeof t('zh', 'sessionConnectedTitle'), 'string');
  assert.equal(typeof t('en', 'sessionConnectedTitle'), 'string');
  assert.notEqual(t('zh', 'sessionConnectedTitle'), t('en', 'sessionConnectedTitle'));
  assert.equal(t('zh', 'missing.key.xyz'), 'missing.key.xyz');
});

test('后端接口设置提供完整中英文文案', () => {
  for (const key of [
    'apiTitle',
    'apiRemote',
    'apiLocal',
    'apiApply',
    'apiHint',
    'apiSaved',
    'apiSwitched',
    'apiInvalid',
    'apiPermissionDenied',
  ]) {
    assert.equal(typeof t('zh', key), 'string', `zh.${key}`);
    assert.equal(typeof t('en', key), 'string', `en.${key}`);
    assert.notEqual(t('zh', key), key, `zh.${key}`);
    assert.notEqual(t('en', key), key, `en.${key}`);
  }
});

test('网站会话状态与操作提供完整中英文文案', () => {
  for (const key of [
    'sessionCheckingTitle',
    'sessionCheckingDescription',
    'sessionNeedsWebsiteTitle',
    'sessionNeedsWebsiteDescription',
    'sessionConnectedTitle',
    'openShop',
    'recheckSession',
    'disconnectExtension',
    'apiOriginMismatch',
  ]) {
    assert.equal(typeof t('zh', key), 'string', `zh.${key}`);
    assert.equal(typeof t('en', key), 'string', `en.${key}`);
    assert.notEqual(t('zh', key), key, `zh.${key}`);
    assert.notEqual(t('en', key), key, `en.${key}`);
  }
});

test('TOP 30 分页、旧服务提示与重试提供完整中英文文案', () => {
  for (const key of [
    'rankingTab',
    'rankingTitle',
    'rankingPageSize',
    'rankingPageSummary',
    'rankingLoading',
    'rankingLoadFailed',
    'rankingRetry',
    'rankingLegacyHint',
    'salesRankBand',
  ]) {
    assert.equal(typeof t('zh', key), 'string', `zh.${key}`);
    assert.equal(typeof t('en', key), 'string', `en.${key}`);
    assert.notEqual(t('zh', key), key, `zh.${key}`);
    assert.notEqual(t('en', key), key, `en.${key}`);
  }
  assert.match(t('zh', 'rankingTab'), /\{percent\}/);
  assert.match(t('en', 'rankingPageSummary'), /\{total\}/);
  assert.match(t('zh', 'salesRankBand'), /\{days\}.*\{band\}/);
  assert.match(t('en', 'salesRankBand'), /\{days\}.*\{band\}/);
});

test('中英文文案键集合完全一致', () => {
  assert.deepEqual(Object.keys(MESSAGES.en).sort(), Object.keys(MESSAGES.zh).sort());
});

test('供应商分类采集区提供完整中英文文案', () => {
  const keys = Object.keys(MESSAGES.zh).filter((key) => key.startsWith('category'));
  assert.ok(keys.length >= 22, `分类采集文案数量不足：${keys.length}`);
  for (const key of keys) {
    assert.equal(typeof t('en', key), 'string', `en.${key}`);
    assert.notEqual(t('en', key), key, `en.${key}`);
    assert.notEqual(t('zh', key), t('en', key), `${key} 中英文不应相同`);
  }
  assert.match(t('zh', 'categoryProgress'), /\{done\}.*\{total\}.*\{failed\}/);
  assert.match(t('en', 'categoryProgress'), /\{done\}.*\{total\}.*\{failed\}/);
  assert.match(t('zh', 'categoryLastRun'), /\{time\}.*\{status\}/);
  assert.match(t('en', 'categoryErrorGeneric'), /\{code\}/);
});

test('侧栏展示的每个分类采集错误码都有专属中英文文案', () => {
  for (const code of CRAWL_ERROR_CODES) {
    const key = categoryErrorMessageKey(code);
    assert.notEqual(key, 'categoryErrorGeneric', `${code} 缺少专属文案`);
    assert.notEqual(t('zh', key), key, `zh.${key}`);
    assert.notEqual(t('en', key), key, `en.${key}`);
  }
  assert.equal(categoryErrorMessageKey('SOMETHING_NEW'), 'categoryErrorGeneric');
});

test('供应商折叠控件提供完整中英文文案', () => {
  for (const key of ['supplierExpand', 'supplierCollapse', 'supplierCollapsedHint']) {
    assert.equal(typeof t('zh', key), 'string', `zh.${key}`);
    assert.equal(typeof t('en', key), 'string', `en.${key}`);
    assert.notEqual(t('zh', key), key, `zh.${key}`);
    assert.notEqual(t('en', key), key, `en.${key}`);
  }
});
