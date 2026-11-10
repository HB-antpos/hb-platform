# 第二个 React Native Surface 接入说明

当前版本在 UIKit external scene 中保留本地媒体与等待占位，并在 JavaScript
完成 AppRegistry 注册后，通过 Expo 的 `rootViewFactory` 挂载第二个 Fabric
root view。React root 实际完成首帧 effect 后再回报 rendered handshake；
在此之前状态保持 `connecting`，不会提前报告 `ready`。

## 当前阻塞

React Native 0.81 的 `RCTRootViewFactory` 提供
`viewWithModuleName:initialProperties:launchOptions:`。本模块使用公开的
`ExpoAppDelegate.factory.rootViewFactory` 创建 `HBExternalDisplay` root view，
并用 registration/rendered 两阶段 handshake 消除 scene 早于 JavaScript 注册的竞态。

注意：必须调用 `ExpoReactRootViewFactory.superView(withModuleName:...)`，不能调用
`view(withModuleName:...)`。后者会重新进入 Expo ReactDelegate handler 链，
expo-updates 会把它当成“主 root 启动”再次执行 `AppController.start()`，
release 包里触发 `precondition` 闪退（0.2.0 (13) 真机接外屏即崩溃的根因）。

## 后续挂载点

1. 用 iPad 真机和 USB-C/HDMI 外屏验证冷启动、热插拔、分辨率变化和断线。
2. 验证 Development Build 与 release/OTA bundle 都会导入原生客显适配器，
   从而执行 AppRegistry 注册。
3. 记录不同显示器分辨率下 UIKit 本地视频层与透明 React 广告窗口的对齐结果。

Expo Go 不包含本地模块，仍明确返回 `disconnected`。真机验证完成前，第二
surface 只能标记为“已实现且可编译，硬件验收未完成”，不能标记为物理外屏已通过。
