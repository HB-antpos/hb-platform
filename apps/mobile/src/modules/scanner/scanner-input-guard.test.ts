import assert from "node:assert/strict";
import { createScannerInputGuard } from "./scanner-input-guard";

/** 手动推进的假时钟：timers 按到期时间触发。 */
function createHarness(initial: string, options: { candidate?: boolean; sanitize?: (value: string) => string } = {}) {
  let clock = 0;
  let nextId = 1;
  const timers = new Map<number, { at: number; callback: () => void }>();
  const state = { value: initial, scans: [] as string[], applied: [] as string[] };
  let candidate = options.candidate ?? true;
  const guard = createScannerInputGuard({
    apply: (next) => {
      const value = options.sanitize ? options.sanitize(next) : next;
      state.applied.push(value);
      state.value = value;
    },
    onScan: (barcode) => state.scans.push(barcode),
    isScannerCandidate: () => candidate,
    now: () => clock,
    setTimer: (callback, ms) => {
      const id = nextId++;
      timers.set(id, { at: clock + ms, callback });
      return id;
    },
    clearTimer: (handle) => {
      timers.delete(handle as number);
    },
  });
  guard.syncValue(initial);
  const advance = (ms: number) => {
    const target = clock + ms;
    for (;;) {
      const due = [...timers.entries()].filter(([, timer]) => timer.at <= target).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      timers.delete(due[0]);
      clock = due[1].at;
      due[1].callback();
    }
    clock = target;
  };
  /** 模拟受控组件重渲染后把外部值同步回 guard。 */
  const type = (next: string, gapMs = 0) => {
    advance(gapMs);
    guard.change(next);
    guard.syncValue(state.value);
  };
  const typeChars = (base: string, chars: string, gapMs: number) => {
    let current = base;
    for (const char of chars) {
      current += char;
      type(current, gapMs);
    }
  };
  return {
    guard,
    state,
    type,
    typeChars,
    advance,
    setCandidate: (value: boolean) => {
      candidate = value;
    },
  };
}

const BARCODE = "9300601234567";

// 整段写入：先照常写入，再在下一轮恢复成原值并路由条码（避免 React 合并导致原生框不刷新）。
{
  const h = createHarness("Promo");
  h.type(`Promo${BARCODE}`);
  assert.equal(h.state.value, `Promo${BARCODE}`, "整段写入当轮先照常写入");
  assert.deepEqual(h.state.scans, []);
  h.advance(0);
  assert.equal(h.state.value, "Promo", "下一轮恢复成扫码前的值");
  assert.deepEqual(h.state.scans, [BARCODE]);
}

// 逐字符快速写入（DataWedge 键盘模拟）：停顿后恢复原值并路由。
{
  const h = createHarness("12.50");
  h.typeChars("12.50", BARCODE, 10);
  assert.equal(h.state.value, `12.50${BARCODE}`);
  h.advance(60);
  assert.equal(h.state.value, "12.50", "价格框恢复原价");
  assert.deepEqual(h.state.scans, [BARCODE]);
}

// 慢速手输纯数字：不是扫码。
{
  const h = createHarness("");
  h.typeChars("", "12345678", 200);
  h.advance(500);
  assert.equal(h.state.value, "12345678");
  assert.deepEqual(h.state.scans, []);
}

// 快速输入但不是 8~18 位纯数字（例如手输时间）：保留。
{
  const h = createHarness("");
  h.typeChars("", "12:30", 10);
  h.advance(100);
  assert.equal(h.state.value, "12:30");
  assert.deepEqual(h.state.scans, []);
}

// 扫码枪追加回车：计时器还没到点，flush 立即按扫码处理并告知调用方跳过原提交。
{
  const h = createHarness("10:00");
  h.typeChars("10:00", BARCODE, 5);
  assert.equal(h.guard.isPending(), true, "连续写入期间视为扫码进行中");
  assert.equal(h.guard.flush(), true);
  assert.equal(h.state.value, "10:00");
  assert.deepEqual(h.state.scans, [BARCODE]);
  h.advance(100);
  assert.deepEqual(h.state.scans, [BARCODE], "计时器不得再触发第二次");
}

// 整段写入后立即回车：同样只路由一次。
{
  const h = createHarness("");
  h.type(BARCODE);
  assert.equal(h.guard.flush(), true);
  h.advance(10);
  assert.equal(h.state.value, "");
  assert.deepEqual(h.state.scans, [BARCODE]);
}

// 手输后回车：flush 返回 false，调用方照常提交。
{
  const h = createHarness("");
  h.typeChars("", "0930", 200);
  assert.equal(h.guard.flush(), false);
  assert.equal(h.state.value, "0930");
}

// 非扫码候选（iOS 或未聚焦）：原样写入，不做识别。
{
  const h = createHarness("", { candidate: false });
  h.type(BARCODE);
  h.advance(100);
  assert.equal(h.state.value, BARCODE);
  assert.deepEqual(h.state.scans, []);
}

// 带清洗的数字框：恢复后仍是清洗后的原值。
{
  const h = createHarness("3", { sanitize: (value) => value.replace(/[^\d]/g, "") });
  h.typeChars("3", BARCODE, 8);
  h.advance(60);
  assert.equal(h.state.value, "3");
  assert.deepEqual(h.state.scans, [BARCODE]);
}

// 删除不是扫码；dispose 后计时器不再回调。
{
  const h = createHarness(BARCODE);
  h.type(BARCODE.slice(0, -1), 5);
  h.type(BARCODE.slice(0, -2), 5);
  h.advance(100);
  assert.deepEqual(h.state.scans, []);

  const disposed = createHarness("");
  disposed.typeChars("", BARCODE, 5);
  disposed.guard.dispose();
  disposed.advance(100);
  assert.deepEqual(disposed.state.scans, []);
}

console.log("scanner-input-guard tests passed");
