/**
 * POS API 首选入口的本机记忆（iPad POS）。
 *
 * 值来自后台按分店的设置（receipt-profile/sync 顶层 apiEntryMode），这里只负责把它记在本机，
 * 让 App 冷启动后第一批请求就走上次的首选入口，随后被服务端值覆盖。
 * 存储用 AsyncStorage（非敏感的短字符串）；读写失败一律由协调器吞掉，不影响同步与收银。
 *
 * AsyncStorage 在首次需要时才 require：纯 Node（tsx）测试 import 本文件不会解析原生模块，
 * 测试也可以直接传入内存假存储。
 */
import {
  createApiEntryModeCoordinator,
  createAsyncStorageApiEntryModeStorage,
  type ApiEntryModeCoordinator,
  type AsyncKeyValueStorage,
} from "@hb/pos-api-client/api-entry-mode-coordinator";

function nativeAsyncStorage(): AsyncKeyValueStorage {
  // 同步 require 让 Metro 把原生模块放进主 bundle，同时避免 Node / tsx 测试在未调用时解析它。
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const loaded = require("@react-native-async-storage/async-storage") as
    | { default: AsyncKeyValueStorage }
    | AsyncKeyValueStorage;
  return "default" in loaded ? loaded.default : loaded;
}

/** 绑定共享选择器（sharedApiTransportSelector）与本机存储的协调器；storage 缺省为真机 AsyncStorage。 */
export function createApiEntryModeCoordinatorFor(
  storage?: AsyncKeyValueStorage,
): ApiEntryModeCoordinator {
  return createApiEntryModeCoordinator({
    storage: createAsyncStorageApiEntryModeStorage(
      storage ?? lazyAsyncStorage(),
    ),
  });
}

/** 延迟到第一次读写才加载原生 AsyncStorage（构造协调器本身不触碰原生层）。 */
function lazyAsyncStorage(): AsyncKeyValueStorage {
  return {
    getItem: (key) => nativeAsyncStorage().getItem(key),
    setItem: (key, value) => nativeAsyncStorage().setItem(key, value),
    removeItem: (key) => nativeAsyncStorage().removeItem(key),
  };
}
