// 大屏收银 App 内「订货助手」：注入供应商网页的浏览器扩展接口替身。
//
// 中文注释：
// 1. 插件的供应商页脚本（apps/supplier-order-extension/src/content/list.js）原样复用，
//    它用到的 chrome.runtime / chrome.storage 在这里换成和 App 原生层之间的消息。
// 2. 这段脚本和供应商网站自己的脚本跑在同一个 JS 环境里（App 内网页窗口没有扩展那样的隔离），
//    所以这里只转发消息，不保存任何令牌；App 原生层收到消息后还会核对来源网址和内容。
// 3. 由 scripts/build-supplier-assistant-content.mjs 打包进生成文件，不要直接在 App 代码里 import。
export function createChromeShim(windowRef, initialStorage) {
  const bridge = windowRef.ReactNativeWebView;
  // 先把发送函数取出来保存，网站脚本之后改写 window 上的同名对象也不影响这里。
  const postToNative = bridge && typeof bridge.postMessage === 'function'
    ? bridge.postMessage.bind(bridge)
    : null;
  const runtimeId = 'hb-pos-supplier-assistant';
  const pending = new Map();
  const messageListeners = new Set();
  const storageListeners = new Set();
  const localArea = { ...(initialStorage || {}) };
  const sessionArea = {};
  let sequence = 0;

  function send(envelope) {
    if (!postToNative) return false;
    postToNative(JSON.stringify({ channel: 'hb-supplier-assistant', ...envelope }));
    return true;
  }

  function pick(area, keys) {
    if (keys == null) return { ...area };
    const list = Array.isArray(keys) ? keys : typeof keys === 'string' ? [keys] : Object.keys(keys);
    const out = {};
    for (const key of list) {
      if (Object.prototype.hasOwnProperty.call(area, key)) {
        out[key] = area[key];
      } else if (keys && typeof keys === 'object' && !Array.isArray(keys)) {
        out[key] = keys[key];
      }
    }
    return out;
  }

  function notifyStorage(changes, areaName) {
    if (Object.keys(changes).length === 0) return;
    for (const listener of storageListeners) {
      try {
        listener(changes, areaName);
      } catch {
        // 单个监听出错不影响其他监听
      }
    }
  }

  function applyToArea(area, values, areaName) {
    const changes = {};
    for (const [key, value] of Object.entries(values || {})) {
      changes[key] = { oldValue: area[key], newValue: value };
      area[key] = value;
    }
    notifyStorage(changes, areaName);
  }

  function makeArea(area, areaName, persist) {
    return {
      get(keys) {
        return Promise.resolve(pick(area, keys));
      },
      set(values) {
        applyToArea(area, values, areaName);
        // local 区由 App 保存；session 只在本页内存里。
        if (persist) send({ kind: 'storage-set', values });
        return Promise.resolve();
      },
      remove(keys) {
        const list = Array.isArray(keys) ? keys : [keys];
        const changes = {};
        for (const key of list) {
          if (!Object.prototype.hasOwnProperty.call(area, key)) continue;
          changes[key] = { oldValue: area[key], newValue: undefined };
          delete area[key];
        }
        notifyStorage(changes, areaName);
        return Promise.resolve();
      },
    };
  }

  const chromeShim = {
    runtime: {
      id: runtimeId,
      // 插件脚本里的 chrome.runtime.getURL(lib/...) 已在打包时换成静态 import，这里只是兜底。
      getURL(path) {
        return String(path || '');
      },
      sendMessage(message) {
        return new Promise((resolve) => {
          sequence += 1;
          const id = `m${sequence}`;
          pending.set(id, resolve);
          if (!send({ kind: 'request', id, message })) {
            pending.delete(id);
            resolve({ ok: false, error: 'App 通道不可用' });
          }
        });
      },
      onMessage: {
        addListener(listener) {
          messageListeners.add(listener);
        },
        removeListener(listener) {
          messageListeners.delete(listener);
        },
      },
    },
    storage: {
      local: makeArea(localArea, 'local', true),
      session: makeArea(sessionArea, 'session', false),
      onChanged: {
        addListener(listener) {
          storageListeners.add(listener);
        },
        removeListener(listener) {
          storageListeners.delete(listener);
        },
      },
    },
  };

  // App 原生层通过 injectJavaScript 调这三个入口：回请求结果、下发指令（例如蓝牙扫码）、同步设置。
  const control = Object.freeze({
    resolve(id, response) {
      const resolve = pending.get(id);
      if (!resolve) return;
      pending.delete(id);
      resolve(response);
    },
    dispatch(message) {
      // sender 只有 id 没有 tab：插件脚本据此认定是「后台」下发的指令。
      const sender = { id: runtimeId };
      for (const listener of messageListeners) {
        try {
          listener(message, sender, () => {});
        } catch {
          // 忽略单个监听的异常
        }
      }
    },
    applyStorage(values) {
      applyToArea(localArea, values, 'local');
    },
  });

  return { chrome: chromeShim, control };
}
