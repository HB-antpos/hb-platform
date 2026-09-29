import test from 'node:test';
import assert from 'node:assert/strict';
import {
  existsSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { normalizeCategoryConfig } from '../src/lib/profiles.js';
import { DEFAULT_PROFILES } from '../src/lib/profiles-default.js';
import { buildNavTree } from '../src/lib/category-crawl.js';
import { resolveCategoryPath } from '../src/lib/category-path.js';

const PACKAGE_ROOT = dirname(dirname(fileURLToPath(import.meta.url)));

function findChromiumBrowser() {
  return [
    process.env.CHROME_BIN,
    process.env.EDGE_BIN,
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
  ].find((candidate) => candidate && existsSync(candidate));
}

// 与 2026-09-23 抓取的 DATS 公开分类页结构一致（精简）：schema.org 面包屑、mega menu、侧栏分类树、head 分页。
const DATS_HTML = `<!doctype html><html><head>
<link rel="next" href="/office-stationery/adhesives-and-tape?PageProduct=2&PageSizeProduct=24">
</head><body class="page-ProductList">
<nav class="widget-navigation-menu"><ul class="navigation-menu"><li class="dropdown">
  <a href="javascript:$.noop()">Product Categories</a>
  <div class="dropdown-area"><div class="mm-col">
    <a class="mm-heading" href="/clearance">Clearance</a>
    <ul><li><a href="/clearance/clearance-items">Clearance Items</a></li></ul>
    <a class="mm-heading" href="/office-stationery">Office Stationery</a>
    <ul>
      <li><a href="/office-stationery/adhesives-and-tape">Adhesives and Tape</a></li>
      <li><a href="/office-stationery/mailing">Mailing</a></li>
    </ul>
    <a class="mm-heading" href="/everyday-dinnerware">Everyday Dinnerware</a>
    <ul><li><a href="/disposable-dinnerware/serviettes">Serviettes</a></li></ul>
  </div></div>
</li></ul></nav>
<div class="widget-breadcrumb product-category"><div class="container"><ul itemscope itemtype="http://schema.org/BreadcrumbList">
  <li itemprop="itemListElement" itemscope itemtype="http://schema.org/ListItem"><a itemprop="item" href="https://www.dats.com.au/"><meta itemprop="name" content="Home"/><meta itemprop="position" content="1"/><span class="cv-ico-general-house"></span></a></li>
  <li itemprop="itemListElement" itemscope itemtype="http://schema.org/ListItem"><a itemprop="item" href="https://www.dats.com.au/office-stationery"><span itemprop="name">Office Stationery</span></a><meta itemprop="position" content="2"/></li>
  <li itemprop="itemListElement" itemscope itemtype="http://schema.org/ListItem" class="last-breadcrumb-item" data-url="https://www.dats.com.au/office-stationery/adhesives-and-tape"><meta itemprop="item" href="https://www.dats.com.au/office-stationery/adhesives-and-tape"/><span itemprop="name">Adhesives and Tape</span><meta itemprop="position" content="3"/></li>
</ul></div></div>
<aside><div class="widget-product-category-list"><ul class="top-level">
  <li class="expandable"><a class="box-title" href="/office-stationery">Office Stationery</a>
    <ul class="second-level">
      <li class="non-expandable"><a class="box-title" href="/office-stationery/adhesives-and-tape">Adhesives and Tape</a></li>
      <li class="non-expandable"><a class="box-title" href="/office-stationery/labels-and-labelmakers">Labels and Labelmakers</a></li>
    </ul>
  </li>
</ul></div></aside>
<div class="product-list-title"><h1 class="widget-product-list-title page-title ">Adhesives and Tape</h1></div>
<div id="product-grid">
  <div class="product " data-role="product" data-product-code="69798"><span class="widget-productlist-code">69798</span></div>
  <div class="product " data-role="product" data-product-code="69792"><span class="widget-productlist-code">69792</span></div>
</div>
</body></html>`;

// WooCommerce（Windragon/Boom Up 同平台）：面包屑末项是纯文本，分页 a.next，分类树 li 嵌套。
const WOO_HTML = `<!doctype html><html><head></head><body>
<nav class="woocommerce-breadcrumb"><a href="/">Home</a> / <a href="/product-category/toys/">Toys</a> / Cars</nav>
<ul class="product-categories">
  <li class="cat-item"><a href="/product-category/toys/">Toys</a>
    <ul class="children"><li class="cat-item"><a href="/product-category/toys/cars/">Cars</a></li></ul>
  </li>
</ul>
<h1 class="page-title">Cars</h1>
<nav class="woocommerce-pagination"><a class="page-numbers" href="/product-category/toys/cars/page/1/">1</a><a class="next page-numbers" href="page/2/">→</a></nav>
</body></html>`;

// GFA：查询参数型分类，链接相对当前目录，带 <base href>。
const GFA_HTML = `<!doctype html><html><head><base href="/product/"></head><body>
<ul class="cat-menu">
  <li><a href="list?category=5">Cosmetics <span class="count">(12)</span></a>
    <ul><li><a href="list?category=6&amp;page=1">Skin Care</a></li></ul>
  </li>
  <li><a href="#top">Back to top</a></li>
  <li><a href="mailto:sales@gfa.example">Email</a></li>
</ul>
<form><input type="password" name="pw"></form>
</body></html>`;

function fixtureHtml(domSource) {
  return `<!doctype html><html><head><meta charset="utf-8"></head><body><pre id="result"></pre>
<script>${domSource}</script>
<script>
  const cases = ${JSON.stringify({ dats: DATS_HTML, woo: WOO_HTML, gfa: GFA_HTML })};
  const datsUrl = 'https://www.dats.com.au/office-stationery/adhesives-and-tape';
  const wooUrl = 'https://windragon.example/product-category/toys/cars/';
  const gfaUrl = 'https://gfa.example/product/list?category=1';
  const dats = parseHtml(cases.dats);
  const woo = parseHtml(cases.woo);
  const gfa = parseHtml(cases.gfa);
  const result = {
    datsBreadcrumb: readBreadcrumbItems(dats, '.widget-breadcrumb li[itemprop="itemListElement"]', datsUrl),
    datsTitle: readTitle(dats, 'h1.page-title, h1'),
    datsNext: readNextPageUrl(dats, 'link[rel="next"], a[rel="next"]', datsUrl),
    datsNav: readNavAnchors(
      dats,
      '.widget-navigation-menu .dropdown-area a[href], .widget-product-category-list a.box-title',
      datsUrl,
    ),
    datsSubcategories: readSubcategoryLinks(dats, '.widget-product-category-list a.box-title', datsUrl),
    datsCards: readCards(dats, '.product[data-product-code]').map((card) => card.getAttribute('data-product-code')),
    datsPassword: hasPasswordField(dats),
    wooBreadcrumb: readBreadcrumbItems(woo, '.woocommerce-breadcrumb a', wooUrl),
    wooNext: readNextPageUrl(woo, '.woocommerce-pagination a.next', wooUrl),
    wooNav: readNavAnchors(woo, '.product-categories a', wooUrl),
    gfaNav: readNavAnchors(gfa, '.cat-menu a', gfaUrl),
    gfaPassword: hasPasswordField(gfa),
    invalidSelector: readNavAnchors(dats, 'a[[', datsUrl),
    probeValid: probeSelector(document, 'h1.page-title, h1'),
    probeInvalid: probeSelector(document, 'a[['),
    context: readPageContext(dats, { breadcrumbSelector: '.widget-breadcrumb li[itemprop="itemListElement"]', titleSelector: 'h1' }, datsUrl),
  };
  document.getElementById('result').textContent = btoa(unescape(encodeURIComponent(JSON.stringify(result))));
</script></body></html>`;
}

function runFixture(browser, htmlPath, profilePath) {
  const result = spawnSync(browser, [
    '--headless=new',
    '--disable-gpu',
    '--no-sandbox',
    `--user-data-dir=${profilePath}`,
    '--virtual-time-budget=1000',
    '--dump-dom',
    pathToFileURL(htmlPath).href,
  ], { encoding: 'utf8', timeout: 30000, maxBuffer: 10 * 1024 * 1024 });
  assert.equal(result.status, 0, result.stderr || '无头浏览器运行失败');
  const encoded = result.stdout.match(/<pre id="result">([A-Za-z0-9+/=]+)<\/pre>/)?.[1];
  assert.ok(encoded, `无头浏览器没有返回解析结果：${result.stdout.slice(-500)}`);
  return JSON.parse(Buffer.from(encoded, 'base64').toString('utf8'));
}

const browser = findChromiumBrowser();

test(
  '分类 DOM 适配层：DOMParser 文档的链接按被抓取页面解析，覆盖 DATS / WooCommerce / GFA 结构',
  { timeout: 60000, skip: browser ? false : '未找到 Edge、Chrome 或 Chromium，跳过无头浏览器测试' },
  () => {
    // 模块刻意不含 import，去掉 export 后即可作为经典脚本内联执行。
    const domSource = readFileSync(join(PACKAGE_ROOT, 'src/lib/category-dom.js'), 'utf8')
      .replace(/^export /gmu, '');
    assert.ok(!/^import\s/mu.test(domSource), 'category-dom.js 不得引入其他模块');
    const tempRoot = mkdtempSync(join(tmpdir(), 'hb-category-dom-'));
    try {
      const htmlPath = join(tempRoot, 'fixture.html');
      writeFileSync(htmlPath, fixtureHtml(domSource));
      const result = runFixture(browser, htmlPath, join(tempRoot, 'profile'));

      assert.deepEqual(result.datsBreadcrumb, [
        { name: 'Home', url: 'https://www.dats.com.au/' },
        { name: 'Office Stationery', url: 'https://www.dats.com.au/office-stationery' },
        { name: 'Adhesives and Tape', url: 'https://www.dats.com.au/office-stationery/adhesives-and-tape' },
      ]);
      assert.equal(result.datsTitle, 'Adhesives and Tape');
      assert.equal(
        result.datsNext,
        'https://www.dats.com.au/office-stationery/adhesives-and-tape?PageProduct=2&PageSizeProduct=24',
        'head 中的相对 link[rel=next] 必须按被抓取页面解析，而不是 file:// 当前页',
      );
      assert.deepEqual(result.datsCards, ['69798', '69792']);
      assert.equal(result.datsPassword, false);
      assert.deepEqual(result.context.breadcrumbItems.length, 3);

      const navByUrl = Object.fromEntries(result.datsNav.map((item) => [item.url, item]));
      assert.equal(navByUrl['https://www.dats.com.au/clearance'].domParentUrl, null);
      assert.equal(
        navByUrl['https://www.dats.com.au/clearance/clearance-items'].domParentUrl,
        'https://www.dats.com.au/clearance',
        'mega menu 标题链接 + 兄弟 ul 推父级',
      );
      assert.equal(
        navByUrl['https://www.dats.com.au/disposable-dinnerware/serviettes'].domParentUrl,
        'https://www.dats.com.au/everyday-dinnerware',
      );
      assert.equal(
        navByUrl['https://www.dats.com.au/office-stationery/labels-and-labelmakers'].domParentUrl,
        'https://www.dats.com.au/office-stationery',
        '侧栏分类树 li 嵌套推父级',
      );
      assert.ok(!result.datsNav.some((item) => item.url.startsWith('javascript:')));
      assert.equal(result.datsSubcategories.length, 3);

      // DOM 结果接入纯逻辑：DATS 默认配置生成的导航树剔除促销并保留 DOM 父级。
      const { config } = normalizeCategoryConfig(DEFAULT_PROFILES.profiles[0].category, DEFAULT_PROFILES.profiles[0]);
      const tree = buildNavTree(result.datsNav, { config, origin: 'https://www.dats.com.au' });
      assert.deepEqual(
        tree.nodes.map((node) => [node.key, node.parentKey]),
        [
          ['/office-stationery', null],
          ['/everyday-dinnerware', null],
          ['/office-stationery/adhesives-and-tape', '/office-stationery'],
          ['/office-stationery/mailing', '/office-stationery'],
          ['/office-stationery/labels-and-labelmakers', '/office-stationery'],
          ['/disposable-dinnerware/serviettes', '/everyday-dinnerware'],
        ],
      );
      const path = resolveCategoryPath({
        pageUrl: 'https://www.dats.com.au/office-stationery/adhesives-and-tape',
        breadcrumbItems: result.datsBreadcrumb,
        title: result.datsTitle,
        config,
      });
      assert.deepEqual(path.path.map((node) => node.key), ['/office-stationery', '/office-stationery/adhesives-and-tape']);

      assert.deepEqual(result.wooBreadcrumb, [
        { name: 'Home', url: 'https://windragon.example/' },
        { name: 'Toys', url: 'https://windragon.example/product-category/toys/' },
      ]);
      assert.equal(result.wooNext, 'https://windragon.example/product-category/toys/cars/page/2/');
      assert.deepEqual(result.wooNav, [
        { name: 'Toys', url: 'https://windragon.example/product-category/toys/', domParentUrl: null },
        {
          name: 'Cars',
          url: 'https://windragon.example/product-category/toys/cars/',
          domParentUrl: 'https://windragon.example/product-category/toys/',
        },
      ]);

      assert.deepEqual(result.gfaNav, [
        { name: 'Cosmetics (12)', url: 'https://gfa.example/product/list?category=5', domParentUrl: null },
        {
          name: 'Skin Care',
          url: 'https://gfa.example/product/list?category=6&page=1',
          domParentUrl: 'https://gfa.example/product/list?category=5',
        },
      ]);
      assert.equal(result.gfaPassword, true);
      assert.deepEqual(result.invalidSelector, [], '非法选择器返回空结果而不是抛异常');
      assert.equal(result.probeValid, 'h1.page-title, h1');
      assert.equal(result.probeInvalid, null);
    } finally {
      rmSync(tempRoot, { recursive: true, force: true });
    }
  },
);
