import test from 'node:test';
import assert from 'node:assert/strict';
import {
  attachBarcodeScanner,
  buildSupplierSearchUrl,
  createRepeatGuard,
  createScanDetector,
  isRedirectedToItemPage,
  normalizeScanItemNumber,
  removeScannedText,
  submitSupplierSearchForm,
  toSupplierItemNumber,
} from '../src/lib/barcode-scan.js';
import { normalizeSearchConfig, validateProfiles } from '../src/lib/profiles.js';
import { DEFAULT_PROFILES } from '../src/lib/profiles-default.js';

function feed(detector, text, { gap = 10, start = 1000, terminator = 'Enter', terminatorGap = gap } = {}) {
  let at = start;
  for (const key of text) {
    assert.equal(detector.handle({ key, timeStamp: at }), null);
    at += gap;
  }
  return detector.handle({ key: terminator, timeStamp: at - gap + terminatorGap });
}

test('快速输入并以回车收尾时识别为扫码', () => {
  assert.equal(feed(createScanDetector(), '9311192573830'), '9311192573830');
});

test('Tab 收尾的扫码枪同样识别', () => {
  assert.equal(feed(createScanDetector(), '93111925', { terminator: 'Tab' }), '93111925');
});

test('人工慢速打字后回车不算扫码', () => {
  assert.equal(feed(createScanDetector(), '9311192573830', { gap: 150 }), null);
});

test('快速输入但短于 8 位不算扫码', () => {
  assert.equal(feed(createScanDetector(), '1234567'), null);
});

test('中途长停顿会重新开始计数', () => {
  const detector = createScanDetector();
  detector.handle({ key: '1', timeStamp: 0 });
  detector.handle({ key: '2', timeStamp: 10 });
  assert.equal(feed(detector, '93111925', { start: 5000 }), '93111925');
});

test('带组合键或收尾键过晚都不算扫码', () => {
  const detector = createScanDetector();
  detector.handle({ key: '9', timeStamp: 0 });
  detector.handle({ key: 'v', timeStamp: 5, ctrlKey: true });
  assert.equal(detector.handle({ key: 'Enter', timeStamp: 10 }), null);
  assert.equal(feed(createScanDetector(), '93111925', { terminatorGap: 500 }), null);
});

test('Shift 不打断含大写字母的条码', () => {
  const detector = createScanDetector();
  let at = 0;
  for (const key of ['Shift', 'A', 'Shift', 'B', '1', '2', '3', '4', '5', '6']) {
    detector.handle({ key, timeStamp: at });
    at += 5;
  }
  assert.equal(detector.handle({ key: 'Enter', timeStamp: at }), 'AB123456');
});

function fakeInput(value) {
  const events = [];
  return { tagName: 'INPUT', value, events, dispatchEvent: (event) => events.push(event.type) };
}

test('从输入框里还原扫进去的条码并触发 input 事件', () => {
  const input = fakeInput('abc93111925');
  assert.equal(removeScannedText(input, '93111925'), true);
  assert.equal(input.value, 'abc');
  assert.deepEqual(input.events, ['input']);
});

test('非输入元素或找不到文本时不做改动', () => {
  assert.equal(removeScannedText({ tagName: 'DIV' }, '93111925'), false);
  const input = fakeInput('other');
  assert.equal(removeScannedText(input, '93111925'), false);
  assert.equal(input.value, 'other');
  assert.deepEqual(input.events, []);
});

function fakeTarget() {
  const listeners = [];
  return {
    addEventListener: (type, handler, capture) => listeners.push({ type, handler, capture }),
    removeEventListener: (type, handler) => {
      const index = listeners.findIndex((item) => item.type === type && item.handler === handler);
      if (index >= 0) listeners.splice(index, 1);
    },
    fire: (event) => listeners.forEach((item) => item.handler(event)),
    listeners,
  };
}

function fireScan(target, text, element) {
  let at = 0;
  const calls = { prevented: 0, stopped: 0 };
  const makeEvent = (key) => ({
    key,
    timeStamp: at,
    target: element,
    preventDefault: () => { calls.prevented += 1; },
    stopImmediatePropagation: () => { calls.stopped += 1; },
  });
  for (const key of text) {
    target.fire(makeEvent(key));
    at += 5;
  }
  target.fire(makeEvent('Enter'));
  return calls;
}

test('识别成功时只拦截收尾键，并在捕获阶段监听', () => {
  const target = fakeTarget();
  const scans = [];
  attachBarcodeScanner(target, { onScan: (code) => scans.push(code) });
  assert.equal(target.listeners[0].capture, true);
  const calls = fireScan(target, '93111925', { tagName: 'DIV' });
  assert.deepEqual(scans, ['93111925']);
  assert.deepEqual(calls, { prevented: 1, stopped: 1 });
});

test('未启用时完全不拦截，卸载后不再监听', () => {
  const target = fakeTarget();
  const scans = [];
  const detach = attachBarcodeScanner(target, { onScan: (code) => scans.push(code), isEnabled: () => false });
  assert.deepEqual(fireScan(target, '93111925', { tagName: 'DIV' }), { prevented: 0, stopped: 0 });
  assert.deepEqual(scans, []);
  detach();
  assert.equal(target.listeners.length, 0);
});

test('同一条码短时间内只处理一次，换码或过窗口后放行', () => {
  const guard = createRepeatGuard(1500);
  assert.equal(guard.accept('A', 0), true);
  assert.equal(guard.accept('A', 1000), false);
  assert.equal(guard.accept('B', 1100), true);
  assert.equal(guard.accept('B', 2700), true);
});

test('搜索网址对货号编码，$ 不被当作替换语法', () => {
  const search = { urlTemplate: 'https://www.dats.com.au/search?ProductSearch={item}' };
  assert.equal(buildSupplierSearchUrl(search, '74817'), 'https://www.dats.com.au/search?ProductSearch=74817');
  assert.equal(buildSupplierSearchUrl(search, 'A B$&'), 'https://www.dats.com.au/search?ProductSearch=A%20B%24%26');
});

test('单结果跳详情页算找到，留在搜索页或跳登录页不算', () => {
  const searchUrl = 'https://www.yatsal.com.au/search?ProductSearch=HAR-909';
  const base = { searchUrl, itemNumber: 'har-909' };
  assert.equal(isRedirectedToItemPage({ ...base, currentPath: '/Hardware/Door-Lock-HAR-909', pageText: 'Item HAR-909 Lock' }), true);
  assert.equal(isRedirectedToItemPage({ ...base, currentPath: '/search', pageText: 'Search HAR-909 returned no results' }), false);
  assert.equal(isRedirectedToItemPage({ ...base, currentPath: '/login', pageText: 'Please log in' }), false);
  assert.equal(isRedirectedToItemPage({ ...base, searchUrl: 'bad', currentPath: '/x', pageText: 'HAR-909' }), false);
});

function fakeSearchInput({ visible = true, form = true } = {}) {
  const input = fakeInput('');
  input.getClientRects = () => (visible ? [{}] : []);
  input.submitted = 0;
  input.form = form ? { requestSubmit: () => { input.submitted += 1; } } : null;
  return input;
}

test('表单模式优先填看得见的搜索框并提交一次', () => {
  const hidden = fakeSearchInput({ visible: false });
  const shown = fakeSearchInput();
  const doc = { querySelectorAll: () => [hidden, shown] };
  assert.equal(submitSupplierSearchForm(doc, { inputSelector: 'x' }, 'GLA 349494'), true);
  assert.equal(shown.value, 'GLA 349494');
  assert.equal(shown.submitted, 1);
  assert.equal(hidden.submitted, 0);
});

test('表单模式找不到搜索框、没有表单或选择器非法时返回 false', () => {
  assert.equal(submitSupplierSearchForm({ querySelectorAll: () => [] }, { inputSelector: 'x' }, 'A'), false);
  const orphan = fakeSearchInput({ form: false });
  assert.equal(submitSupplierSearchForm({ querySelectorAll: () => [orphan] }, { inputSelector: 'x' }, 'A'), false);
  const bad = { querySelectorAll: () => { throw new Error('bad selector'); } };
  assert.equal(submitSupplierSearchForm(bad, { inputSelector: '[' }, 'A'), false);
});

test('搜索配置支持搜索框模式，网址模板优先', () => {
  assert.deepEqual(
    normalizeSearchConfig({ urlTemplate: null, inputSelector: ' input[name="k"] ' }, profile),
    { config: { inputSelector: 'input[name="k"]' }, errors: [] },
  );
  assert.deepEqual(
    normalizeSearchConfig({ urlTemplate: 'https://www.dats.com.au/s?q={item}', inputSelector: 'input' }, profile).config,
    { urlTemplate: 'https://www.dats.com.au/s?q={item}' },
  );
  assert.equal(normalizeSearchConfig({ inputSelector: 'a\nb' }, profile).config, null);
  assert.equal(normalizeSearchConfig({ inputSelector: '' }, profile).config, null);
});

test('下划线转斜线的供应商搜索前转回网站写法，其余不变', () => {
  assert.equal(toSupplierItemNumber('BB/MKNUNI', ['trim', 'uppercase', 'underscore-to-slash']), 'BB_MKNUNI');
  assert.equal(toSupplierItemNumber('BAG045-AUS/RS', ['trim', 'uppercase']), 'BAG045-AUS/RS');
  assert.equal(toSupplierItemNumber('74817', undefined), '74817');
});

test('长度接近上限的真实搜索网址模板通过校验', () => {
  const urlTemplate = `https://www.dats.com.au/view?a=${'x'.repeat(400)}&k={item}`;
  assert.deepEqual(normalizeSearchConfig({ urlTemplate }, profile).config, { urlTemplate });
});

test('货号比较忽略大小写和首尾空白', () => {
  assert.equal(normalizeScanItemNumber(' ab12 '), 'AB12');
  assert.equal(normalizeScanItemNumber(null), '');
});

const profile = { origins: ['https://www.dats.com.au/*'] };

test('搜索配置：缺省表示不启用，同源模板通过', () => {
  assert.deepEqual(normalizeSearchConfig(null, profile), { config: null, errors: [] });
  assert.deepEqual(
    normalizeSearchConfig({ urlTemplate: 'https://www.dats.com.au/search?q={item}' }, profile),
    { config: { urlTemplate: 'https://www.dats.com.au/search?q={item}' }, errors: [] },
  );
});

test('搜索配置：外站、缺占位符、多占位符、占位符在域名里都拒绝', () => {
  for (const urlTemplate of [
    'https://evil.example.com/search?q={item}',
    'https://www.dats.com.au/search',
    'https://www.dats.com.au/{item}/{item}',
    'https://{item}.dats.com.au/search',
    'https://user:pw@www.dats.com.au/search?q={item}',
    'javascript:alert({item})',
  ]) {
    const { config, errors } = normalizeSearchConfig({ urlTemplate }, profile);
    assert.equal(config, null, urlTemplate);
    assert.equal(errors.length, 1, urlTemplate);
  }
});

test('搜索配置非法只停用扫码并记警告，商品按钮配置仍有效', () => {
  const dats = { ...DEFAULT_PROFILES.profiles[0], search: { urlTemplate: 'https://evil.example.com/?q={item}' } };
  const result = validateProfiles({ profiles: [dats] });
  assert.equal(result.valid, true);
  assert.equal(result.profiles[0].search, null);
  assert.ok(result.warnings.some((warning) => warning.includes('search')));
});

test('内置 DATS 配置带按货号搜索的网址', () => {
  const result = validateProfiles(DEFAULT_PROFILES);
  assert.equal(result.profiles[0].search.urlTemplate, 'https://www.dats.com.au/search?ProductSearch={item}');
});
