// 分店选择页（人员筛选、日结筛选、操作日志筛选共用）的纯逻辑：搜索、按首字母分组、「我的主分店」。不依赖 React Native，可在 Node 下测试。

export interface StoreOption {
  value: string;
  /** 形如「分店名 · 编号」，与列表页其它位置保持一致。 */
  label: string;
}

export interface StoreOptionGroup {
  letter: string;
  items: StoreOption[];
}

const LABEL_SEPARATOR = " · ";

/** 把「分店名 · 编号」拆成两段；没有编号时 code 为空串。 */
export function splitStoreLabel(label: string): { name: string; code: string } {
  const index = label.lastIndexOf(LABEL_SEPARATOR);
  if (index < 0) return { name: label, code: "" };
  return { name: label.slice(0, index), code: label.slice(index + LABEL_SEPARATOR.length) };
}

/** 名称或编号包含关键词即命中（不区分大小写、忽略首尾空格）；关键词为空返回全部。 */
export function filterStoreOptions(options: readonly StoreOption[], query: string): StoreOption[] {
  const keyword = query.trim().toLowerCase();
  if (!keyword) return [...options];
  return options.filter((option) => option.label.toLowerCase().includes(keyword));
}

/** 按分店名首字母（A–Z）分组；非字母开头（数字、中文等）归入「#」放最后。组内保持传入顺序。 */
export function groupStoreOptionsByLetter(options: readonly StoreOption[]): StoreOptionGroup[] {
  const groups = new Map<string, StoreOption[]>();
  for (const option of options) {
    const first = splitStoreLabel(option.label).name.trim().charAt(0).toUpperCase();
    const letter = /^[A-Z]$/.test(first) ? first : "#";
    const bucket = groups.get(letter);
    if (bucket) bucket.push(option);
    else groups.set(letter, [option]);
  }
  return [...groups.entries()]
    .sort(([a], [b]) => (a === "#" ? 1 : b === "#" ? -1 : a.localeCompare(b)))
    .map(([letter, items]) => ({ letter, items }));
}

/**
 * 「我的主分店」快捷项：当前账号的主分店中、确实在可选列表里的那些。
 * 若它们就是全部可选分店（店长只管自己的分店），快捷区没有意义，返回空数组。
 */
export function pickMyStoreOptions(options: readonly StoreOption[], myStoreGuids: readonly string[]): StoreOption[] {
  if (myStoreGuids.length === 0) return [];
  const mine = new Set(myStoreGuids.map((guid) => guid.toLowerCase()));
  const picked = options.filter((option) => mine.has(option.value.toLowerCase()));
  return picked.length === options.length ? [] : picked;
}
