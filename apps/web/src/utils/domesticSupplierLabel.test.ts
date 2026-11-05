import {
  formatDomesticSupplierLabel,
  formatDomesticSupplierNameFirst,
  hasDomesticSupplierName,
  pickDomesticSupplierName,
} from './domesticSupplierLabel'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}\nExpected: ${String(expected)}\nActual: ${String(actual)}`)
  }
}

// ---------- 是否有名称 ----------
assertEqual(hasDomesticSupplierName('HB196', '义乌玩具厂'), true, '有名称时应返回 true')
assertEqual(hasDomesticSupplierName('HB196', null), false, '后端返回 null 视为没有名称')
assertEqual(hasDomesticSupplierName('HB196', undefined), false, 'undefined 视为没有名称')
assertEqual(hasDomesticSupplierName('HB196', ''), false, '空串视为没有名称')
assertEqual(hasDomesticSupplierName('HB196', '   '), false, '空白视为没有名称')
assertEqual(hasDomesticSupplierName('HB196', 'HB196'), false, '服务层用代码兜底的名称视为没有名称')
assertEqual(hasDomesticSupplierName('HB196', ' HB196 '), false, '比较前应去掉首尾空白')

// ---------- 代码在前 ----------
assertEqual(formatDomesticSupplierLabel('HB196', '义乌玩具厂'), 'HB196 - 义乌玩具厂', '有名称时保持「代码 - 名称」')
assertEqual(formatDomesticSupplierLabel('HB196', null), 'HB196', '名称为 null 时只显示代码')
assertEqual(formatDomesticSupplierLabel('HB196', undefined), 'HB196', '名称为 undefined 时只显示代码')
assertEqual(formatDomesticSupplierLabel('HB196', ''), 'HB196', '名称为空串时不得留下「HB196 - 」')
assertEqual(formatDomesticSupplierLabel('HB196', '  '), 'HB196', '名称为空白时只显示代码')
assertEqual(formatDomesticSupplierLabel('HB196', 'HB196'), 'HB196', '名称与代码相同时不得重复显示')
assertEqual(formatDomesticSupplierLabel('HB196', '义乌玩具厂', ' · '), 'HB196 · 义乌玩具厂', '应支持自定义分隔符')
assertEqual(formatDomesticSupplierLabel('', '义乌玩具厂'), '义乌玩具厂', '没有代码时只显示名称，不留分隔符')
assertEqual(formatDomesticSupplierLabel(null, null), '-', '代码和名称都没有时返回默认兜底')
assertEqual(formatDomesticSupplierLabel(undefined, '', ' - ', '--'), '--', '应支持调用方指定兜底')
assertEqual(formatDomesticSupplierLabel(' HB196 ', ' 义乌玩具厂 '), 'HB196 - 义乌玩具厂', '首尾空白不进入显示文本')

// ---------- 名称在前 ----------
assertEqual(formatDomesticSupplierNameFirst('HB196', '义乌玩具厂'), '义乌玩具厂 · HB196', '有名称时保持「名称 · 代码」')
assertEqual(formatDomesticSupplierNameFirst('HB196', null), 'HB196', '名称为 null 时不得出现「null · HB196」')
assertEqual(formatDomesticSupplierNameFirst('HB196', ''), 'HB196', '名称为空串时不得出现「 · HB196」')
assertEqual(formatDomesticSupplierNameFirst('HB196', 'HB196'), 'HB196', '名称与代码相同时不得出现「HB196 · HB196」')
assertEqual(formatDomesticSupplierNameFirst('HB196', '义乌玩具厂', ' (', ')'), '义乌玩具厂 (HB196)', '应支持「名称 (代码)」形式')
assertEqual(formatDomesticSupplierNameFirst('HB196', null, ' (', ')'), 'HB196', '括号形式在没有名称时也只显示代码')
assertEqual(formatDomesticSupplierNameFirst('', '义乌玩具厂'), '义乌玩具厂', '没有代码时只显示名称')
assertEqual(formatDomesticSupplierNameFirst(null, undefined), '-', '都没有时返回默认兜底')
assertEqual(formatDomesticSupplierNameFirst(null, undefined, ' · ', '', '全部'), '全部', '应支持调用方指定兜底')

// ---------- 单值显示 ----------
assertEqual(pickDomesticSupplierName('HB196', '义乌玩具厂'), '义乌玩具厂', '有名称时显示名称')
assertEqual(pickDomesticSupplierName('HB196', null), 'HB196', '没有名称时回退到代码')
assertEqual(pickDomesticSupplierName('HB196', '  '), 'HB196', '空白名称回退到代码')
assertEqual(pickDomesticSupplierName(undefined, undefined), '-', '都没有时返回默认兜底')
assertEqual(pickDomesticSupplierName('', '', '--'), '--', '应支持调用方指定兜底')

// 任何情况下都不得把 null / undefined 拼进显示文本
for (const name of [null, undefined, '', ' ']) {
  for (const text of [
    formatDomesticSupplierLabel('HB196', name),
    formatDomesticSupplierNameFirst('HB196', name),
    pickDomesticSupplierName('HB196', name),
  ]) {
    assertEqual(/null|undefined| - | · /.test(text), false, `显示文本不得含残留：${text}`)
    assertEqual(text, 'HB196', '没有名称时显示文本应正好是代码')
  }
}

console.log('domesticSupplierLabel: ok')
