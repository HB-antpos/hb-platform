/**
 * 订货模式的「员工还在用」信号。
 *
 * 中文注释：网页窗口里的点按 / 输入不会经过 React Native 的触摸系统，App 看不到；
 * 所以往两个网页里注入一小段脚本，有操作时（最多每 20 秒一次）发一条消息给 App，
 * 订货工作台据此重置闲置自动退出的计时。消息里不带任何页面内容。
 */
export const ORDERING_ACTIVITY_CHANNEL = "hb-ordering-activity";

export const ORDERING_ACTIVITY_SCRIPT = `(function(){
  if (window.__hbOrderingActivity) return;
  window.__hbOrderingActivity = true;
  var last = 0;
  function ping(){
    var now = Date.now();
    if (now - last < 20000) return;
    last = now;
    try { window.ReactNativeWebView.postMessage(JSON.stringify({ channel: ${JSON.stringify(ORDERING_ACTIVITY_CHANNEL)} })); } catch (e) {}
  }
  ["pointerdown","keydown","touchstart","scroll"].forEach(function(name){
    window.addEventListener(name, ping, { capture: true, passive: true });
  });
})()`;
