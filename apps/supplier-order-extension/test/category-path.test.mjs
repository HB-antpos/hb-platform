import test from 'node:test';
import assert from 'node:assert/strict';
import {
  BUILTIN_CATEGORY_EXCLUDE_PATTERNS,
  DEFAULT_PROMOTIONAL_PATTERNS,
  detectPageNumber,
  globToRegex,
  humanizeSlug,
  isPromotionalNode,
  isPromotionalPath,
  matchesCategoryPage,
  matchesPagePattern,
  matchesPromotional,
  normalizeCategoryKey,
  normalizeCategoryName,
  resolveCategoryPath,
} from '../src/lib/category-path.js';
import { matchUrlPattern } from '../src/lib/profiles.js';

const DATS_CONFIG = {
  keySource: 'pathname',
  keyQueryParams: [],
  breadcrumbSkip: 1,
  categoryPagePatterns: ['https://www.dats.com.au/*'],
  categoryExcludePatterns: [...BUILTIN_CATEGORY_EXCLUDE_PATTERNS],
  promotionalPatterns: [...DEFAULT_PROMOTIONAL_PATTERNS],
};

test('分类 key：取路径、合并斜杠、去尾斜杠、小写、剥离分页并丢弃查询串', () => {
  assert.equal(normalizeCategoryKey('https://www.dats.com.au/Office-Stationery/'), '/office-stationery');
  assert.equal(normalizeCategoryKey('https://x.com//a///b//'), '/a/b');
  assert.equal(normalizeCategoryKey('https://x.com/product-category/toys/page/3/'), '/product-category/toys');
  assert.equal(
    normalizeCategoryKey('https://www.dats.com.au/office-stationery?PageProduct=2&PageSizeProduct=24'),
    '/office-stationery',
  );
  assert.equal(normalizeCategoryKey('https://x.com/'), '/');
  assert.equal(normalizeCategoryKey('https://x.com/page/2'), '/');
  assert.equal(normalizeCategoryKey('https://x.com/caf%C3%A9-items'), '/café-items');
  // 非法百分号编码保留原文，不抛异常。
  assert.equal(normalizeCategoryKey('https://x.com/a%E0%A4%A'), '/a%e0%a4%a');
  assert.equal(normalizeCategoryKey('not a url'), null);
  assert.equal(normalizeCategoryKey(`https://x.com/${'a'.repeat(400)}`), null);
});

test('分类 key：白名单查询参数按名排序追加，值 trim+小写，其余丢弃', () => {
  const config = { keyQueryParams: ['id', 'category', 'group'] };
  assert.equal(
    normalizeCategoryKey('https://gfa.example/products/?page=2&id=%20AB%20&category=Toys&x=1', config),
    '/products?category=toys&id=ab',
  );
  assert.equal(normalizeCategoryKey('https://gfa.example/products?x=1', config), '/products');
  // 参数名忽略大小写匹配。
  assert.equal(
    normalizeCategoryKey('https://gfa.example/list?Category=Cups', { keyQueryParams: ['category'] }),
    '/list?category=cups',
  );
});

test('分类 key：hash 路由取 hash 路径（含 #!）与 hash 内查询参数', () => {
  const config = { keySource: 'hash', keyQueryParams: ['cat'] };
  assert.equal(normalizeCategoryKey('https://spa.example/#/Toys/Cars/', { keySource: 'hash' }), '/toys/cars');
  assert.equal(normalizeCategoryKey('https://spa.example/shop#!/party?cat=9', config), '/party?cat=9');
  assert.equal(normalizeCategoryKey('https://spa.example/shop', { keySource: 'hash' }), '/');
});

test('分类名称与 URL 段人性化', () => {
  assert.equal(normalizeCategoryName('  Office   Stationery (123) '), 'Office Stationery');
  assert.equal(normalizeCategoryName(null), '');
  assert.equal(normalizeCategoryName('x'.repeat(250)).length, 200);
  assert.equal(humanizeSlug('office-stationery'), 'Office Stationery');
  assert.equal(humanizeSlug('party_supplies.html'), 'Party Supplies');
  assert.equal(humanizeSlug(''), '');
});

test('glob 只把 * 当通配，其余字符按字面量匹配', () => {
  assert.equal(globToRegex('sale-*').test('sale-summer'), true);
  assert.equal(globToRegex('a.b').test('axb'), false);
  assert.equal(globToRegex('(x)+').test('(x)+'), true);
  for (const [pattern, href] of [
    ['/search*', 'https://x.com/search?q=1'],
    ['/product-category/*', 'https://x.com/product-category/toys'],
    ['https://www.dats.com.au/*', 'https://www.dats.com.au/office'],
    ['/cart*', 'https://x.com/checkout'],
  ]) {
    assert.equal(matchesPagePattern(pattern, href), matchUrlPattern(pattern, href), `${pattern} ${href}`);
  }
});

test('分类页判定：先排除内置/配置路径，再匹配分类页模式', () => {
  assert.equal(matchesCategoryPage('https://www.dats.com.au/office-stationery', DATS_CONFIG), true);
  assert.equal(matchesCategoryPage('https://www.dats.com.au/search?q=pen', DATS_CONFIG), false);
  assert.equal(matchesCategoryPage('https://www.dats.com.au/my-account', DATS_CONFIG), false);
  assert.equal(matchesCategoryPage('https://evil.example/office', DATS_CONFIG), false);
  assert.equal(
    matchesCategoryPage('https://w.example/product-category/toys', {
      categoryPagePatterns: ['/product-category/*'],
      categoryExcludePatterns: [],
    }),
    true,
  );
});

test('促销判定：key 每段、完整 key 与名称（空格转 -）；wholesale 不误伤', () => {
  assert.equal(matchesPromotional('clearance-items'), true);
  assert.equal(matchesPromotional('wholesale'), false);
  assert.equal(isPromotionalNode({ key: '/clearance', name: 'Clearance' }), true);
  assert.equal(isPromotionalNode({ key: '/toys/summer-clearance', name: 'Summer' }), true);
  assert.equal(isPromotionalNode({ key: '/c/123', name: 'On Sale Now' }), true);
  assert.equal(isPromotionalNode({ key: '/sale.html', name: 'Big' }), true);
  assert.equal(isPromotionalNode({ key: '/wholesale-toys', name: 'Wholesale Toys' }), false);
  assert.equal(isPromotionalNode({ key: '/office-stationery', name: 'Office Stationery' }), false);
  assert.equal(
    isPromotionalPath([
      { key: '/clearance', name: 'Clearance' },
      { key: '/clearance/clearance-items', name: 'Clearance Items' },
    ]),
    true,
  );
  assert.equal(isPromotionalPath([{ key: '/pens', name: 'Pens' }]), false);
  assert.equal(matchesPromotional('deals-week', ['DEALS*']), true, '模式大小写不敏感');
});

test('DATS 面包屑：跳过 Home，末项无链接时用当前页 key', () => {
  const resolved = resolveCategoryPath({
    pageUrl: 'https://www.dats.com.au/office-stationery/adhesives-and-tape?PageProduct=2&PageSizeProduct=24',
    breadcrumbItems: [
      { name: 'Home', url: 'https://www.dats.com.au/' },
      { name: 'Office Stationery', url: 'https://www.dats.com.au/office-stationery' },
      { name: 'Adhesives and Tape', url: 'https://www.dats.com.au/office-stationery/adhesives-and-tape' },
    ],
    title: 'Adhesives and Tape',
    config: DATS_CONFIG,
  });
  assert.equal(resolved.source, 'breadcrumb');
  assert.equal(resolved.leafKey, '/office-stationery/adhesives-and-tape');
  assert.deepEqual(resolved.path.map((node) => [node.name, node.key]), [
    ['Office Stationery', '/office-stationery'],
    ['Adhesives and Tape', '/office-stationery/adhesives-and-tape'],
  ]);
  assert.equal(resolved.path[0].url, 'https://www.dats.com.au/office-stationery');
});

test('面包屑只到父级（WooCommerce 末项纯文本）时用标题补叶子', () => {
  const resolved = resolveCategoryPath({
    pageUrl: 'https://w.example/product-category/toys/cars/',
    breadcrumbItems: [
      { name: 'Home', url: 'https://w.example/' },
      { name: 'Toys', url: 'https://w.example/product-category/toys/' },
    ],
    title: 'Cars',
    config: { ...DATS_CONFIG, categoryPagePatterns: ['/product-category/*'] },
  });
  assert.deepEqual(resolved.path.map((node) => node.key), ['/product-category/toys', '/product-category/toys/cars']);
  assert.equal(resolved.path[1].name, 'Cars');
});

test('Home 未被 breadcrumbSkip 跳过时按根路径自动剔除；中间项缺链接回退 URL 段', () => {
  const noSkip = resolveCategoryPath({
    pageUrl: 'https://www.dats.com.au/pens',
    breadcrumbItems: [{ name: 'Home', url: 'https://www.dats.com.au/' }, { name: 'Pens' }],
    title: 'Pens',
    config: { ...DATS_CONFIG, breadcrumbSkip: 0 },
  });
  assert.deepEqual(noSkip.path.map((node) => node.key), ['/pens']);

  const fallback = resolveCategoryPath({
    pageUrl: 'https://www.dats.com.au/office-stationery/mailing',
    breadcrumbItems: [{ name: 'Home' }, { name: 'Office' }, { name: 'Mailing' }],
    title: 'Mailing Supplies',
    config: DATS_CONFIG,
  });
  assert.equal(fallback.source, 'url');
  assert.deepEqual(fallback.path.map((node) => [node.name, node.key]), [
    ['Office Stationery', '/office-stationery'],
    ['Mailing Supplies', '/office-stationery/mailing'],
  ]);
});

test('URL 段回退跳过不符合分类页模式的前缀段', () => {
  const resolved = resolveCategoryPath({
    pageUrl: 'https://w.example/product-category/toys',
    breadcrumbItems: [],
    title: '',
    config: { ...DATS_CONFIG, categoryPagePatterns: ['/product-category/*'] },
  });
  assert.deepEqual(resolved.path.map((node) => [node.name, node.key]), [['Toys', '/product-category/toys']]);
});

test('查询参数型站点只用标题生成单节点路径；首页与无标题时返回 null', () => {
  const config = { ...DATS_CONFIG, keyQueryParams: ['category'], categoryPagePatterns: ['/*'] };
  const resolved = resolveCategoryPath({
    pageUrl: 'https://gfa.example/product?category=12&page=3',
    breadcrumbItems: [],
    title: 'Cosmetics',
    config,
  });
  assert.equal(resolved.source, 'title');
  assert.deepEqual(resolved.path, [
    { name: 'Cosmetics', key: '/product?category=12', url: 'https://gfa.example/product?category=12&page=3' },
  ]);
  assert.equal(resolveCategoryPath({ pageUrl: 'https://gfa.example/product', title: '', config }), null);
  assert.equal(resolveCategoryPath({ pageUrl: 'https://www.dats.com.au/', title: 'Home', config: DATS_CONFIG }), null);
});

test('路径超过 8 级时放弃回传，避免错误截断', () => {
  const items = [{ name: 'Home', url: 'https://x.com/' }];
  let path = '';
  for (let index = 0; index < 9; index += 1) {
    path += `/c${index}`;
    items.push({ name: `C${index}`, url: `https://x.com${path}` });
  }
  assert.equal(
    resolveCategoryPath({
      pageUrl: `https://x.com${path}`,
      breadcrumbItems: items,
      title: 'Deep',
      config: { ...DATS_CONFIG, categoryPagePatterns: ['/*'] },
    }),
    null,
  );
});

test('分页页码识别', () => {
  assert.equal(detectPageNumber('https://x.com/toys/page/3/'), 3);
  assert.equal(detectPageNumber('https://www.dats.com.au/a?PageProduct=2&PageSizeProduct=24'), 2);
  assert.equal(detectPageNumber('https://x.com/a?paged=4'), 4);
  assert.equal(detectPageNumber('https://x.com/a'), undefined);
  assert.equal(detectPageNumber('https://x.com/a?page=abc'), undefined);
});
