import test from 'node:test';
import assert from 'node:assert/strict';
import {
  validateProfiles,
  matchProfile,
  originMatchesAny,
  matchesListPage,
  normalizeCategoryConfig,
} from '../src/lib/profiles.js';
import { DEFAULT_PROFILES } from '../src/lib/profiles-default.js';

const validProfile = {
  supplierCode: 'DATS',
  displayName: 'DATS',
  enabled: true,
  origins: ['https://www.dats.com.au/*'],
  listPagePatterns: [],
  cardSelector: '.product[data-product-code]',
  itemNumber: {
    source: 'attribute',
    selector: null,
    attribute: 'data-product-code',
    transforms: ['trim', 'uppercase'],
  },
  mountSelector: '.widget-productlist-code',
  mountPosition: 'afterend',
};

test('validateProfiles 接受合法 profile', () => {
  const r = validateProfiles({ configVersion: 1, profiles: [validProfile] });
  assert.equal(r.valid, true);
  assert.equal(r.profiles.length, 1);
  assert.equal(r.errors.length, 0);
});

test('服务端可返回空 profile 列表以后台停用全部供应商', () => {
  const r = validateProfiles({ configVersion: '2', profiles: [] });
  assert.equal(r.valid, true);
  assert.deepEqual(r.profiles, []);
});

test('validateProfiles 拒绝非法 transform（不允许 eval）', () => {
  const p = {
    ...validProfile,
    itemNumber: { ...validProfile.itemNumber, transforms: [{ type: 'eval' }] },
  };
  const r = validateProfiles({ profiles: [p] });
  assert.equal(r.valid, false);
  assert.ok(r.errors.some((e) => /transforms/.test(e)));
});

test('validateProfiles 拒绝非法 source / mountPosition / 缺失字段', () => {
  assert.equal(validateProfiles({ profiles: [{ ...validProfile, itemNumber: { source: 'css' } }] }).valid, false);
  assert.equal(validateProfiles({ profiles: [{ ...validProfile, mountPosition: 'inside' }] }).valid, false);
  assert.equal(validateProfiles({ profiles: [{ ...validProfile, supplierCode: '' }] }).valid, false);
  assert.equal(validateProfiles(null).valid, false);
  assert.equal(validateProfiles({}).valid, false);
  assert.equal(
    validateProfiles({ profiles: [{ ...validProfile, origins: ['javascript:alert(1)'] }] }).valid,
    false,
  );
});

test('仅允许 TXK 的精确 HTTP 站点，拒绝其他明文 HTTP 配置', () => {
  const txk = {
    ...validProfile,
    supplierCode: 'SP2502280001',
    displayName: 'TXK',
    origins: ['http://txkorders.inzantsales.com/*'],
    listPagePatterns: ['http://txkorders.inzantsales.com/shop*'],
    cardSelector: '.single-product.grid-view',
    itemNumber: {
      source: 'text',
      selector: '.sku',
      attribute: null,
      transforms: ['after-sku', 'trim', 'uppercase'],
    },
    mountSelector: '.price-box',
  };

  assert.equal(validateProfiles({ profiles: [txk] }).valid, true);
  assert.equal(
    validateProfiles({
      profiles: [{ ...txk, origins: ['http://example.com/*'] }],
    }).valid,
    false,
  );
  for (const unsafeOrigin of [
    'http://txkorders.inzantsales.com.evil.example/*',
    'http://txkorders.inzantsales.com:8080/*',
    'http://user@txkorders.inzantsales.com/*',
  ]) {
    assert.equal(
      validateProfiles({ profiles: [{ ...txk, origins: [unsafeOrigin] }] }).valid,
      false,
      `必须拒绝 ${unsafeOrigin}`,
    );
  }
  assert.equal(originMatchesAny(txk.origins, 'http://txkorders.inzantsales.com'), true);
  assert.equal(originMatchesAny(txk.origins, 'http://evil.example.com'), false);
});

test('内置 DATS profile 通过校验', () => {
  const r = validateProfiles(DEFAULT_PROFILES);
  assert.equal(r.valid, true);
  assert.equal(DEFAULT_PROFILES.configVersion, '3');
  assert.equal(r.profiles[0].supplierCode, '240');
  assert.equal(r.profiles[0].displayName, 'DATS');
  assert.equal(r.profiles[0].mountPosition, 'afterend');
  assert.deepEqual(r.warnings, []);
});

test('内置 DATS profile 启用分类采集且选择器与公开站点结构一致', () => {
  const [dats] = validateProfiles(DEFAULT_PROFILES).profiles;
  const { category } = dats;
  assert.equal(category.enabled, true);
  assert.equal(category.passiveEnabled, true);
  assert.equal(category.crawlEnabled, true);
  assert.deepEqual(category.categoryPagePatterns, ['https://www.dats.com.au/*']);
  assert.ok(category.categoryExcludePatterns.includes('/search*'));
  assert.equal(category.breadcrumbSkip, 1);
  assert.match(category.breadcrumbSelector, /widget-breadcrumb/);
  assert.match(category.paginationNextSelector, /link\[rel="next"\]/);
  assert.equal(category.navRootUrl, 'https://www.dats.com.au/');
  assert.ok(category.promotionalPatterns.includes('clearance*'), '空促销列表回退默认模式');
});

test('category 缺省时停用分类采集且不产生 warnings', () => {
  const r = validateProfiles({ profiles: [validProfile] });
  assert.equal(r.valid, true);
  assert.equal(r.profiles[0].category.enabled, false);
  assert.deepEqual(r.warnings, []);
  // 原 profile 字段保持不变，只追加归一化后的 category。
  assert.equal(r.profiles[0].cardSelector, validProfile.cardSelector);
});

test('合法 category 归一化默认值、沿用列表页模式并追加内置排除路径', () => {
  const { config, errors } = normalizeCategoryConfig(
    {
      enabled: true,
      breadcrumbSelector: '.crumbs a',
      keyQueryParams: ['Cat', 'category', 'cat'],
      categoryExcludePatterns: ['/brands*'],
      navRootUrl: 'https://www.dats.com.au/menu',
    },
    { ...validProfile, listPagePatterns: ['/shop/*'] },
  );
  assert.deepEqual(errors, []);
  assert.equal(config.enabled, true);
  assert.deepEqual(config.categoryPagePatterns, ['/shop/*']);
  assert.ok(config.categoryExcludePatterns.includes('/login*'));
  assert.ok(config.categoryExcludePatterns.includes('/brands*'));
  assert.deepEqual(config.keyQueryParams, ['cat', 'category']);
  assert.equal(config.titleSelector, 'h1');
  assert.equal(config.paginationNextSelector, 'a[rel="next"]');
  assert.equal(config.maxPages, 20);
  assert.equal(config.maxDepth, 4);
  assert.equal(config.maxCategories, 400);
  assert.equal(config.crawlDelayMs, 1500);
  assert.equal(config.keySource, 'pathname');
});

test('非法 category 只降级该供应商分类采集并写入 warnings，不让 profile 失效', () => {
  const invalidCategories = [
    { breadcrumbSelector: '.crumb\na' },
    { breadcrumbSelector: 'a'.repeat(501) },
    { maxPages: 0 },
    { maxPages: 51 },
    { crawlDelayMs: 100 },
    { maxCategories: 5000 },
    { maxDepth: 1.5 },
    { breadcrumbSkip: 6 },
    { keySource: 'query' },
    { keyQueryParams: ['a', 'b', 'c', 'd', 'e', 'f'] },
    { keyQueryParams: ['bad name'] },
    { navRootUrl: 'https://evil.example.com/' },
    { navRootUrl: 'javascript:alert(1)' },
    { categoryPagePatterns: ['javascript:alert(1)'] },
    { promotionalPatterns: ['sale?'] },
    { promotionalPatterns: Array.from({ length: 51 }, (_, index) => `p${index}`) },
    { passiveEnabled: 'yes' },
  ];
  for (const invalid of invalidCategories) {
    const r = validateProfiles({
      profiles: [{ ...validProfile, category: { enabled: true, ...invalid } }],
    });
    assert.equal(r.valid, true, `profile 必须仍有效：${JSON.stringify(invalid).slice(0, 80)}`);
    assert.deepEqual(r.errors, []);
    assert.equal(r.profiles.length, 1);
    assert.equal(r.profiles[0].category.enabled, false, JSON.stringify(invalid).slice(0, 80));
    assert.ok(r.warnings.length > 0);
    assert.ok(r.warnings.every((warning) => warning.startsWith('profiles[0].category.')));
  }
});

test('一个供应商分类配置非法不影响其他供应商分类采集', () => {
  const r = validateProfiles({
    profiles: [
      { ...validProfile, supplierCode: 'A', category: { enabled: true, maxPages: 999 } },
      { ...validProfile, supplierCode: 'B', category: { enabled: true } },
    ],
  });
  assert.equal(r.valid, true);
  assert.equal(r.profiles[0].category.enabled, false);
  assert.equal(r.profiles[1].category.enabled, true);
  assert.equal(r.warnings.length, 1);
});

test('Hot Bargain 200 永不启用分类采集', () => {
  const r = validateProfiles({
    profiles: [{ ...validProfile, supplierCode: '200', category: { enabled: true } }],
  });
  assert.equal(r.profiles[0].category.enabled, false);
  assert.deepEqual(r.warnings, []);
});

test('后端下发的 null 选择器与空促销列表按默认值处理，归一化结果可重复校验', () => {
  const first = validateProfiles({
    profiles: [{
      ...validProfile,
      category: {
        enabled: true,
        breadcrumbSelector: null,
        titleSelector: null,
        navRootUrl: null,
        paginationNextSelector: '',
        promotionalPatterns: [],
        keyQueryParams: [],
      },
    }],
  });
  assert.equal(first.profiles[0].category.enabled, true);
  assert.equal(first.profiles[0].category.titleSelector, 'h1');
  assert.equal(first.profiles[0].category.breadcrumbSelector, null);
  const second = validateProfiles({ profiles: first.profiles });
  assert.deepEqual(second.profiles[0].category, first.profiles[0].category);
  assert.deepEqual(second.warnings, []);
});

test('originMatchesAny 与 matchesListPage', () => {
  assert.equal(originMatchesAny(['https://www.dats.com.au/*'], 'https://www.dats.com.au'), true);
  assert.equal(originMatchesAny(['https://www.dats.com.au/*'], 'https://evil.com'), false);
  assert.equal(
    matchesListPage(['https://www.dats.com.au/*'], 'https://www.dats.com.au/filing-notebooks-and-paper'),
    true,
  );
  assert.equal(matchesListPage(['/search/*'], 'https://example.com/search/123'), true);
  assert.equal(matchesListPage(['/search/*'], 'https://example.com/cart'), false);
  assert.equal(matchesListPage([], 'https://example.com/anything'), false);
});

test('matchProfile 按 origin 匹配且跳过 disabled', () => {
  const profiles = [
    validProfile,
    { ...validProfile, supplierCode: 'X', enabled: false, origins: ['https://x.com/*'] },
  ];
  assert.equal(matchProfile(profiles, { origin: 'https://www.dats.com.au', pathname: '/' }).supplierCode, 'DATS');
  assert.equal(matchProfile(profiles, { origin: 'https://x.com', pathname: '/' }), null);
  assert.equal(matchProfile(profiles, { origin: 'https://other.com', pathname: '/' }), null);
});
