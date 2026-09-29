import { applyTransforms } from './transforms.js';

// 货号在 HB 后端按 nvarchar(50) 存储；超长值必然是解析错误，直接丢弃。
export const MAX_ITEM_NUMBER_LENGTH = 50;

// 读取卡片商品号：attribute/text + 声明式 transforms。
// 按钮注入与分类采集共用同一实现，保证两边拿到的货号完全一致。
export function readItemNumberFrom(card, itemCfg) {
  if (!card || !itemCfg) return '';
  let el = card;
  if (itemCfg.selector) {
    const sub = card.querySelector(itemCfg.selector);
    if (!sub) return '';
    el = sub;
  }
  const raw = itemCfg.source === 'attribute' ? el.getAttribute(itemCfg.attribute) : el.textContent;
  return applyTransforms(raw, itemCfg.transforms);
}

// 分类回传用的货号归一化：去空白、大写、限制长度；非法值返回空串。
export function normalizeCaptureItemNumber(value) {
  if (value == null) return '';
  const normalized = String(value).trim().toUpperCase();
  if (!normalized || normalized.length > MAX_ITEM_NUMBER_LENGTH) return '';
  // 控制字符说明读到了异常文本节点，不能作为货号回传。
  if (/[\u0000-\u001f\u007f]/u.test(normalized)) return '';
  return normalized;
}

// 批量读取卡片货号：单张卡片解析失败不影响其余卡片，结果去重并保持页面顺序。
export function readItemNumbersFromCards(cards, itemCfg) {
  const seen = new Set();
  const out = [];
  for (const card of cards || []) {
    let value = '';
    try {
      value = normalizeCaptureItemNumber(readItemNumberFrom(card, itemCfg));
    } catch {
      value = '';
    }
    if (!value || seen.has(value)) continue;
    seen.add(value);
    out.push(value);
  }
  return out;
}

// 已由按钮注入读取过的货号（可能未大写）统一归一化并去重。
export function normalizeCaptureItemNumbers(values) {
  const seen = new Set();
  const out = [];
  for (const value of values || []) {
    const normalized = normalizeCaptureItemNumber(value);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    out.push(normalized);
  }
  return out;
}
