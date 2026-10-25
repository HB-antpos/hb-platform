/**
 * POS API 首选入口的本机记忆（iPad POS）。
 *
 * 值来自后台按分店的设置（receipt-profile/sync 顶层 apiEntryMode），这里只负责把它记在本机，
 * 让 App 冷启动后第一批请求就走上次的首选入口，随后被服务端值覆盖。
 * 存储用 AsyncStorage（非敏感的短字符串）；读写失败一律由协调器吞掉，不影响同步与收银。
 * 另有「本机手动覆盖」（设置页收银入口）：独立 key，优先级高于后台值，规则见协调器。
 * 协调器是进程内单例（getApiEntryModeCoordinator）：运行时（接收后台值）与设置页（手动覆盖）必须共用同一份，
 * 否则两边各自记着不同的「后台值 / 覆盖」，合并规则就失效了。
 *
 * AsyncStorage 在首次需要时才 require：纯 Node（tsx）测试 import 本文件不会解析原生模块，
 * 测试也可以直接传入内存假存储。
 */
import {
  API_ENTRY_OVERRIDE_STORAGE_KEY,
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
  const backing = storage ?? lazyAsyncStorage();
  return createApiEntryModeCoordinator({
    storage: createAsyncStorageApiEntryModeStorage(backing),
    // 手动覆盖与后台值分 key 存放，后台同步永远不会读写它。
    overrideStorage: createAsyncStorageApiEntryModeStorage(
      backing,
      API_ENTRY_OVERRIDE_STORAGE_KEY,
    ),
  });
}

let sharedCoordinator: ApiEntryModeCoordinator | undefined;

/** 进程内共享的协调器（运行时与设置页共用）；首次调用时才创建，仍不触碰原生层。 */
export function getApiEntryModeCoordinator(): ApiEntryModeCoordinator {
  sharedCoordinator ??= createApiEntryModeCoordinatorFor();
  return sharedCoordinator;
}

/** 延迟到第一次读写才加载原生 AsyncStorage（构造协调器本身不触碰原生层）。 */
function lazyAsyncStorage(): AsyncKeyValueStorage {
  return {
    getItem: (key) => nativeAsyncStorage().getItem(key),
    setItem: (key, value) => nativeAsyncStorage().setItem(key, value),
    removeItem: (key) => nativeAsyncStorage().removeItem(key),
  };
}
