export interface HidBarcodeKeyEvent {
  key?: string | null;
  character?: string | null;
}

interface HidBarcodeKeyBufferOptions {
  idleMs: number;
  minLength: number;
  onBarcode: (barcode: string) => void | Promise<void>;
  onFallbackToTextInput?: () => void;
  now?: () => number;
  setTimeoutFn?: typeof setTimeout;
  clearTimeoutFn?: typeof clearTimeout;
}

const DUPLICATE_SUPPRESS_MS = 100;
const END_KEYS = new Set(["Enter", "NumpadEnter", "Tab"]);

export function normalizeHidBarcode(rawValue: string) {
  return rawValue.replace(/[\r\n\t]/g, "").trim();
}

interface HidTextInputCaptureOptions {
  /** 输入停顿多久算一次扫码结束（兼容不带回车后缀的扫码枪）。 */
  idleMs: number;
  /** 收到一段尚未消费的新增原生文本（可能带回车 / Tab，由调用方 normalize）。 */
  onText: (pendingText: string) => void;
  /** 尽力清空原生输入框；被原生丢弃也无妨，下次按已消费前缀取差量。 */
  clearNative: () => void;
  /** 在每次文本变化时捕获一次提交许可；停顿提交前许可已失效（如页面停用过）就只丢弃不提交。 */
  captureSubmission?: () => () => boolean;
  setTimeoutFn?: typeof setTimeout;
  clearTimeoutFn?: typeof clearTimeout;
}

/**
 * 隐藏 TextInput 回退模式的扫码捕获（非受控）。
 *
 * RN 新架构下受控 TextInput 的回写 / 清空要带 JS 侧事件计数，扫码枪敲字比 JS 处理快时计数对不上，
 * 原生会静默丢弃这次写入，JS 却以为已清空，下一枪就会追加在旧值后面（TC26 历史现象）。
 * 这里不再回写 value，以原生回传的文本为准：记住「原生文本里已提交 / 丢弃到哪一段」，
 * 每次只取其后新增的部分。原生清空成功（文本不再以已消费前缀开头，或变得不比它长）
 * 与清空被丢弃（文本 = 已消费前缀 + 新码）两种情况都不会串码，新码比旧码短也一样。
 *
 * 安卓上 JS 清空成功时不回传文本变化事件，单看文本分不清「清空成功后整段写入一个恰好以上一枪开头的新码」
 * 和「清空被丢弃后追加」（DataWedge 关闭逐字符发送时会整段写入）。清空成功时光标会回到 0 并派发选区事件，
 * 所以清空后、下一次文本变化前收到选区 (0,0)，就确认原生已空，把已消费前缀清掉（handleSelectionChange）。
 * 收不到（iOS 不派发 / 事件被合并）就维持按前缀扣减，与之前一致。
 */
export function createHidTextInputCapture({
  idleMs,
  onText,
  clearNative,
  captureSubmission,
  setTimeoutFn = setTimeout,
  clearTimeoutFn = clearTimeout,
}: HidTextInputCaptureOptions) {
  // 原生输入框最近一次回传的完整文本。
  let seenText = "";
  // 原生文本中已经提交或丢弃过的前缀；原生清空被丢弃时，残留就是这一段。
  let consumedText = "";
  let idleTimer: ReturnType<typeof setTimeout> | null = null;
  let submissionAllowed: (() => boolean) | null = null;
  // 已请求原生清空、还没收到下一次文本变化：此时的选区 (0,0) 才算「清空成功」的确认。
  let awaitingClearConfirmation = false;

  const clearIdleTimer = () => {
    if (!idleTimer) {
      return;
    }
    clearTimeoutFn(idleTimer);
    idleTimer = null;
  };

  /** 当前原生文本里尚未消费的部分；原生已被清空（不再以已消费前缀开头）时整段都算新输入。 */
  const pendingTextOf = (nativeText: string) => {
    if (!nativeText.startsWith(consumedText)) {
      consumedText = "";
      return nativeText;
    }
    return nativeText.slice(consumedText.length);
  };

  /** 把原生现有文本整体记为已消费，并尽力清空原生输入框。 */
  const consumeNativeText = (nativeText: string) => {
    consumedText = nativeText;
    if (nativeText) {
      awaitingClearConfirmation = true;
      clearNative();
    }
  };

  const flush = (nativeText: string | undefined, allowed: boolean) => {
    clearIdleTimer();
    submissionAllowed = null;
    const text = nativeText ?? seenText;
    seenText = text;
    const pending = pendingTextOf(text);
    // 先同步记为已消费：停顿提交后迟到的 submitEditing 再带同一段全文时取到空串，不会重放。
    consumeNativeText(text);
    if (pending && allowed) {
      onText(pending);
    }
  };

  return {
    handleChangeText(nativeText: string) {
      // 清空后的第一次文本变化已经到了：之后的选区事件只是光标随输入移动，不再代表清空结果。
      awaitingClearConfirmation = false;
      seenText = nativeText;
      // HID 逐字符触发变化事件；不比已消费前缀长，只能是原生已清空后重新输入。
      if (nativeText.length <= consumedText.length) {
        consumedText = "";
      }
      submissionAllowed = captureSubmission?.() ?? null;
      clearIdleTimer();
      idleTimer = setTimeoutFn(() => {
        idleTimer = null;
        const allowed = submissionAllowed ? submissionAllowed() : true;
        flush(undefined, allowed);
      }, idleMs);
    },
    /**
     * 选区变化：请求清空后、下一次文本变化前光标回到 (0,0)，说明原生清空确实生效（安卓 JS 清空不回传文本事件，
     * 只派发选区事件），已消费前缀作废，下一枪整段都是新码。
     */
    handleSelectionChange(start: number, end: number) {
      if (!awaitingClearConfirmation || start !== 0 || end !== 0) {
        return;
      }
      awaitingClearConfirmation = false;
      consumedText = "";
      seenText = "";
    },
    /** 回车路径：以 submitEditing 带回的原生全文为准，没带就用最近一次变化事件的文本。 */
    handleSubmitEditing(nativeText?: string | null) {
      flush(nativeText || undefined, true);
    },
    /** 丢弃当前原生文本（页面停用等），不提交。 */
    discard(nativeText?: string) {
      if (nativeText !== undefined) {
        seenText = nativeText;
        if (nativeText.length <= consumedText.length) {
          consumedText = "";
        }
      }
      flush(undefined, false);
    },
    dispose() {
      clearIdleTimer();
      submissionAllowed = null;
    },
  };
}

export function isHidNativeTextInputFallbackEvent(event: HidBarcodeKeyEvent) {
  const key = event.key ?? "";
  const character = event.character ?? "";
  return /^\d+$/.test(key) && !character;
}

function isPrintableScannerCharacter(character: string) {
  return Boolean(character) && !/[\r\n\t]/.test(character);
}

export function createHidBarcodeKeyBuffer({
  idleMs,
  minLength,
  onBarcode,
  onFallbackToTextInput,
  now = () => Date.now(),
  setTimeoutFn = setTimeout,
  clearTimeoutFn = clearTimeout,
}: HidBarcodeKeyBufferOptions) {
  let buffer = "";
  let idleTimer: ReturnType<typeof setTimeout> | null = null;
  let lastSubmitted = "";
  let lastSubmittedAt = 0;

  const clearIdleTimer = () => {
    if (!idleTimer) {
      return;
    }
    clearTimeoutFn(idleTimer);
    idleTimer = null;
  };

  const submit = (rawValue: string) => {
    const barcode = normalizeHidBarcode(rawValue);
    if (!barcode || barcode.length < minLength) {
      return;
    }

    const submittedAt = now();
    if (barcode === lastSubmitted && submittedAt - lastSubmittedAt < DUPLICATE_SUPPRESS_MS) {
      return;
    }

    lastSubmitted = barcode;
    lastSubmittedAt = submittedAt;
    void onBarcode(barcode);
  };

  const flush = () => {
    clearIdleTimer();
    const nextValue = buffer;
    buffer = "";
    submit(nextValue);
  };

  const scheduleIdleFlush = () => {
    clearIdleTimer();
    // HID 扫码枪通常会快速连续输入；没有 Enter/Tab 后缀时，用短暂停顿作为一次扫码结束。
    idleTimer = setTimeoutFn(flush, idleMs);
  };

  return {
    handleKeyPress(event: HidBarcodeKeyEvent) {
      const key = event.key ?? "";
      const character = event.character ?? "";

      if (END_KEYS.has(key)) {
        flush();
        return;
      }

      if (isHidNativeTextInputFallbackEvent(event)) {
        clearIdleTimer();
        buffer = "";
        onFallbackToTextInput?.();
        return;
      }

      if (!isPrintableScannerCharacter(character)) {
        return;
      }

      buffer += character;
      scheduleIdleFlush();
    },
    flush,
    dispose() {
      clearIdleTimer();
      buffer = "";
      lastSubmitted = "";
      lastSubmittedAt = 0;
    },
  };
}
