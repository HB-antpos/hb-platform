// 保险库的读写层：只依赖注入的存储后端（真实后端见 vault-backend.ts），便于在 Node 里单测。
//
// 存储布局（Keychain / SecureStore）：
//   hbweb_acct_vault_index            → JSON 数组，记录有哪些账号（规范化后的 userGuid）
//   hbweb_acct_vault.<userGuid>       → 单个账号的 JSON（含刷新令牌）
// 每个账号单独一个键：iOS Keychain 单项有大小限制，且更新一个账号不会重写其它账号的令牌。
//
// iOS 卸载重装后 Keychain 会保留而 AsyncStorage 被清空：用 AsyncStorage 里的「哨兵」识别这种孤儿数据并清除，
// 避免重装后凭空冒出旧账号。

import {
  markNeedsRelogin,
  normalizeGuid,
  parseEntry,
  removeEntry,
  upsertEntry,
  type UpsertResult,
  type VaultEntry,
} from "./vault";

export const VAULT_INDEX_KEY = "hbweb_acct_vault_index";
export const VAULT_ENTRY_KEY_PREFIX = "hbweb_acct_vault.";
export const VAULT_SENTINEL_KEY = "hb.accountVault.sentinel.v1";

export interface VaultSecureBackend {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  delete(key: string): Promise<void>;
}

export interface VaultSentinelBackend {
  has(): Promise<boolean>;
  set(): Promise<void>;
  clear(): Promise<void>;
}

export function vaultEntryKey(userGuid: string): string {
  return `${VAULT_ENTRY_KEY_PREFIX}${normalizeGuid(userGuid)}`;
}

export interface VaultStore {
  load(): Promise<VaultEntry[]>;
  upsert(entry: VaultEntry): Promise<UpsertResult["status"]>;
  /** 只在账号已存在时更新（回写轮转后的新令牌等）；不存在返回 false，不会「顺手」新增。 */
  update(userGuid: string, change: (entry: VaultEntry) => VaultEntry): Promise<boolean>;
  remove(userGuid: string): Promise<void>;
  markNeedsRelogin(userGuid: string): Promise<void>;
  /** 清空保险库（退出全部 / 清除本机登录缓存）。尽力删除，单项失败不影响其余。 */
  clear(): Promise<void>;
}

export function createVaultStore(secure: VaultSecureBackend, sentinel: VaultSentinelBackend): VaultStore {
  // 所有读改写串行化：切换、回写令牌、保存可能同时触发，不排队会互相覆盖。
  let queue: Promise<unknown> = Promise.resolve();
  function serialize<T>(task: () => Promise<T>): Promise<T> {
    const next = queue.then(task, task);
    queue = next.catch(() => undefined);
    return next;
  }

  async function readIndex(): Promise<string[]> {
    const raw = await secure.get(VAULT_INDEX_KEY);
    if (!raw) return [];
    try {
      const parsed: unknown = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === "string") : [];
    } catch {
      return [];
    }
  }

  async function purgeAll(guids: readonly string[]): Promise<void> {
    await Promise.allSettled([
      ...guids.map((guid) => secure.delete(vaultEntryKey(guid))),
      secure.delete(VAULT_INDEX_KEY),
      sentinel.clear(),
    ]);
  }

  async function readAll(): Promise<VaultEntry[]> {
    const guids = await readIndex();
    if (guids.length === 0) return [];
    // 有数据却没有哨兵 = 卸载重装后残留的 Keychain 数据：清掉，不恢复。
    if (!(await sentinel.has())) {
      await purgeAll(guids);
      return [];
    }
    const entries: VaultEntry[] = [];
    for (const guid of guids) {
      const raw = await secure.get(vaultEntryKey(guid));
      if (!raw) continue;
      try {
        const entry = parseEntry(JSON.parse(raw));
        if (entry) entries.push(entry);
      } catch {
        // 单条损坏：丢弃这条，其余账号不受影响（用户需要重新添加）。
      }
    }
    return entries;
  }

  async function writeAll(before: readonly VaultEntry[], after: readonly VaultEntry[]): Promise<void> {
    // 旧索引里的账号（含解析失败被丢弃的损坏条目）都要在写完后清理，否则损坏条目会变成无索引的孤立键，clear 也扫不到。
    const previousGuids = await readIndex().catch(() => [] as string[]);
    // 先写新内容、再写索引、最后删被移除的：任何一步失败，最坏是留下一个无索引的孤立条目，不会丢账号。
    for (const entry of after) {
      await secure.set(vaultEntryKey(entry.userGuid), JSON.stringify(entry));
    }
    if (after.length > 0) {
      await sentinel.set();
      await secure.set(VAULT_INDEX_KEY, JSON.stringify(after.map((entry) => normalizeGuid(entry.userGuid))));
    } else {
      await secure.delete(VAULT_INDEX_KEY);
      await sentinel.clear();
    }
    const kept = new Set(after.map((entry) => normalizeGuid(entry.userGuid)));
    const stale = new Set([...previousGuids, ...before.map((entry) => entry.userGuid)].map(normalizeGuid));
    for (const guid of stale) {
      if (!kept.has(guid)) await secure.delete(vaultEntryKey(guid));
    }
  }

  return {
    load: () => serialize(readAll),

    upsert: (entry) =>
      serialize(async () => {
        const before = await readAll();
        const result = upsertEntry(before, entry);
        if (result.status !== "full") await writeAll(before, result.entries);
        return result.status;
      }),

    update: (userGuid, change) =>
      serialize(async () => {
        const before = await readAll();
        const target = normalizeGuid(userGuid);
        const existing = before.find((item) => normalizeGuid(item.userGuid) === target);
        if (!existing) return false;
        const after = before.map((item) => (item === existing ? change(item) : item));
        await writeAll(before, after);
        return true;
      }),

    remove: (userGuid) =>
      serialize(async () => {
        const before = await readAll();
        const after = removeEntry(before, userGuid);
        if (after.length !== before.length) await writeAll(before, after);
      }),

    markNeedsRelogin: (userGuid) =>
      serialize(async () => {
        const before = await readAll();
        await writeAll(before, markNeedsRelogin(before, userGuid));
      }),

    clear: () =>
      serialize(async () => {
        // 索引读不到也要尽力清：先按索引删，再删索引与哨兵。
        const guids = await readIndex().catch(() => [] as string[]);
        await purgeAll(guids);
      }),
  };
}
