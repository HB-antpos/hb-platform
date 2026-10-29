// 路由参数解析（expo-router 的参数可能是 string 或 string[]）。

export function firstParam(value: unknown): string | undefined {
  const raw = Array.isArray(value) ? value[0] : value;
  return typeof raw === "string" && raw.trim() ? raw.trim() : undefined;
}

/** 周数参数只认 1 / 2，其他值返回 undefined 交给调用方给默认。 */
export function parseWeeksParam(value: unknown): 1 | 2 | undefined {
  const raw = Array.isArray(value) ? value[0] : value;
  return raw === "2" || raw === 2 ? 2 : raw === "1" || raw === 1 ? 1 : undefined;
}
