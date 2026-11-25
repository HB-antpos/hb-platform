// 键盘模式扫码枪识别：扫码枪等同一把打字极快、以回车（或 Tab）收尾的键盘。
// 供应商页面（内容脚本）与侧栏共用；按键归属取决于焦点，所以两边各挂一份监听。

// 人手连续 8 个键都小于 80 毫秒基本不可能，用它区分扫码与打字。
export const SCAN_MAX_KEY_GAP_MS = 80;
// 最短的零售条码是 EAN-8。
export const SCAN_MIN_LENGTH = 8;
const SCAN_MAX_LENGTH = 50;
// 部分扫码枪在条码和回车之间有额外延迟，收尾键放宽。
const SCAN_TERMINATOR_GAP_MS = 200;
// 蓝牙扫码枪（尤其 HID 键盘模式）传输偶尔卡顿，一次扫码中间会冒出一两次 100～300ms 的停顿。
// 旧逻辑超过 80ms 就清空重来，结果把后半截（只要 ≥8 位）当成完整条码去查。
// 现在这个范围内的停顿视为同一次扫码接着拼，最多拼 SCAN_MAX_STALLS 次。
const SCAN_STALL_GAP_MS = 300;
// 前面已以扫码速度收到这么多字符才算「正在扫码」；此时再被打断，后面的残码整次作废。
const SCAN_MIN_BURST_LENGTH = 4;
const SCAN_MAX_STALLS = 2;
// 停顿超过这个时长就当作上一次输入已彻底结束，连「作废」标记一起清掉。
const SCAN_IDLE_RESET_MS = 1000;
const SCAN_CODE_PATTERN = /^[0-9A-Za-z._-]+$/;
const TERMINATOR_KEYS = new Set(['Enter', 'Tab']);

export const SCAN_REPEAT_WINDOW_MS = 1500;

export function createScanDetector({
  minLength = SCAN_MIN_LENGTH,
  maxKeyGapMs = SCAN_MAX_KEY_GAP_MS,
  terminatorGapMs = SCAN_TERMINATOR_GAP_MS,
  stallGapMs = SCAN_STALL_GAP_MS,
} = {}) {
  let buffer = '';
  let lastAt = 0;
  let stalls = 0;
  // 扫码中途断得太久时置位：后半截也是扫码速度、可能凑够 8 位，但它不是完整条码，收尾时整次作废。
  let broken = false;

  function reset() {
    buffer = '';
    lastAt = 0;
    stalls = 0;
    broken = false;
  }

  // 扫码中途被打断（停太久、夹进非字符键）：前半截已经丢了，后面那段即便凑够 8 位也只是残码。
  // 置 broken 让本次扫码在收尾时整体作废，而不是拿后半截去查。
  function interrupt(timeStamp) {
    broken = broken || buffer.length >= SCAN_MIN_BURST_LENGTH;
    buffer = '';
    stalls = 0;
    lastAt = timeStamp;
  }

  // 用事件自带的 timeStamp 而不是当前时间：页面主线程繁忙时事件会排队，当前时间会把间隔拉长。
  function handle(event) {
    if (event.ctrlKey || event.metaKey || event.altKey || event.isComposing || event.repeat) {
      reset();
      return null;
    }
    // 大写字母由 Shift + 字母组成，Shift 本身不打断序列。
    if (event.key === 'Shift') return null;

    if (TERMINATOR_KEYS.has(event.key)) {
      const code = buffer;
      const gap = event.timeStamp - lastAt;
      const incomplete = broken;
      reset();
      if (incomplete) return null;
      if (code.length < minLength || code.length > SCAN_MAX_LENGTH) return null;
      if (gap > terminatorGapMs || !SCAN_CODE_PATTERN.test(code)) return null;
      return code;
    }

    if (typeof event.key !== 'string' || event.key.length !== 1) {
      interrupt(event.timeStamp);
      return null;
    }
    const gap = event.timeStamp - lastAt;
    if (gap > SCAN_IDLE_RESET_MS) {
      // 空闲够久，上一次输入已彻底结束，作废标记一并清掉。
      reset();
    } else if (buffer && gap > maxKeyGapMs) {
      if (gap <= stallGapMs && stalls < SCAN_MAX_STALLS) {
        // 传输卡顿：停顿后接着拼，不丢前半截。卡顿可能发生在开头一两个字符之后，所以不要求前段长度；
        // 人手打字要连续多个键都快于 80ms 才可能凑够 8 位，最多 2 次中等停顿不足以让慢速打字被误判。
        stalls += 1;
      } else {
        interrupt(event.timeStamp);
      }
    }
    buffer += event.key;
    lastAt = event.timeStamp;
    return null;
  }

  return { handle, reset };
}

// 焦点在输入框时条码字符已经写了进去，判定为扫码后要还原，否则会留在表单里。
export function removeScannedText(element, code) {
  const tag = typeof element?.tagName === 'string' ? element.tagName.toUpperCase() : '';
  if ((tag !== 'INPUT' && tag !== 'TEXTAREA') || typeof element.value !== 'string') return false;
  const index = element.value.lastIndexOf(code);
  if (index < 0) return false;
  setInputValue(element, element.value.slice(0, index) + element.value.slice(index + code.length));
  return true;
}

// 走原型上的 setter 并派发 input，让依赖 input 事件的页面框架能感知到变化。
function setInputValue(element, value) {
  const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(element), 'value')?.set;
  if (setter) setter.call(element, value);
  else element.value = value;
  element.dispatchEvent(new Event('input', { bubbles: true }));
}

// 表单模式：填入供应商网站自己的搜索框并提交，CSRF 等隐藏字段由表单自带。
// 页面上可能有多个同样的搜索框（如手机版隐藏的那个），优先用看得见的。
export function submitSupplierSearchForm(doc, search, itemNumber) {
  let inputs;
  try {
    inputs = [...doc.querySelectorAll(search.inputSelector)];
  } catch {
    return false;
  }
  const input = inputs.find((item) => item.getClientRects().length > 0) ?? inputs[0];
  if (!input?.form) return false;
  setInputValue(input, itemNumber);
  input.form.requestSubmit();
  return true;
}

export function attachBarcodeScanner(target, { onScan, isEnabled = () => true, detectorOptions } = {}) {
  const detector = createScanDetector(detectorOptions);
  const handler = (event) => {
    if (!isEnabled()) {
      detector.reset();
      return;
    }
    const code = detector.handle(event);
    if (!code) return;
    // 只拦截收尾键：拦下后供应商页面不会把回车当成提交表单。
    event.preventDefault();
    event.stopImmediatePropagation();
    removeScannedText(event.composedPath?.()[0] ?? event.target, code);
    onScan(code);
  };
  target.addEventListener('keydown', handler, true);
  return () => target.removeEventListener('keydown', handler, true);
}

// 同一个码短时间内连扫只处理一次，防止手抖或扫码枪连发。
export function createRepeatGuard(windowMs = SCAN_REPEAT_WINDOW_MS) {
  let lastCode = null;
  let lastAt = 0;
  return {
    accept(code, now) {
      const repeated = code === lastCode && now - lastAt < windowMs;
      lastCode = code;
      lastAt = now;
      return !repeated;
    },
  };
}

export function buildSupplierSearchUrl(search, itemNumber) {
  // 用函数做替换值，货号里的 $ 不会被当成替换语法。
  return search.urlTemplate.replace('{item}', () => encodeURIComponent(itemNumber));
}

// 部分供应商只有一个结果时直接跳商品详情页，页面上没有列表卡片。
// 路径变了且页面文字里有这个货号才算找到，这样跳到登录页不会被误判。
export function isRedirectedToItemPage({ currentPath, searchUrl, pageText, itemNumber }) {
  let searchPath;
  try {
    searchPath = new URL(searchUrl).pathname;
  } catch {
    return false;
  }
  if (currentPath === searchPath) return false;
  return String(pageText ?? '').toUpperCase().includes(normalizeScanItemNumber(itemNumber));
}

// 系统货号是按 itemNumber 转换规则从网站写法转来的（GFA：网站 BB_X → 系统 BB/X），
// 去网站搜索时要转回去，否则搜不到。
export function toSupplierItemNumber(itemNumber, transforms) {
  return Array.isArray(transforms) && transforms.includes('underscore-to-slash')
    ? String(itemNumber).replaceAll('/', '_')
    : String(itemNumber);
}

export function normalizeScanItemNumber(value) {
  return String(value ?? '').trim().toUpperCase();
}
