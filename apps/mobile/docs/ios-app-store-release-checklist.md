# iOS App Store 上架门禁

此清单用于 `apps/mobile` production 二进制。代码检查通过不等于已满足 App Store Connect 外部门禁。

## 构建前

- [ ] 隐私政策正文已由业务负责人或法律顾问确认，App 内 `/privacy` 与 `https://hotbargain.vip/privacy/mobile` 内容一致。
- [ ] 公共隐私政策 URL 无需登录即可直接访问，深链刷新返回 HTTP 200。
- [ ] App Store Connect 的 Privacy Policy URL 和 Support URL 已填写并可访问。
- [ ] App Privacy 数据类型、关联用户、追踪状态和用途与生成的 `PrivacyInfo.xcprivacy` 一致。
- [ ] 年龄分级问卷已按当前功能重新完成。
- [ ] Review Information 已填写审核账号口令、离线演示模式说明、样例 QR 和后台定位说明。
- [ ] 截图、描述、关键词、版权和出口合规答案已复核。

## 二进制验证

- [ ] 从干净源码执行 fresh CNG prebuild；不使用仓库忽略的旧 `ios/` 快照作为证据。
- [ ] 生成的 `Info.plist` 没有麦克风权限，且相机、照片库和定位文案与实际功能一致。
- [ ] 生成的 `PrivacyInfo.xcprivacy` 包含第一方数据收集声明和 Required Reason API 声明。
- [ ] 真机确认现场广告视频没有音轨；相册中的既有视频可保留音轨并正常上传。
- [ ] 真机覆盖相机、受限照片库、定位允许/拒绝、当班后台定位、扫码以及设备登录路径。
- [ ] 设备日志不包含条码、QR token 或其前缀片段。

## 发布

- [ ] 只在源码提交和验证结果锁定后启动一次 production EAS iOS build，避免无意消耗远程 build number。
- [ ] EAS 构建日志确认使用 Xcode 26 或更高版本以及 iOS 26 SDK 或更高版本。
- [ ] 上传后在 App Store Connect 确认 Apple processing 状态为 `VALID`。
- [ ] TestFlight 安装全新版本并重新验证首次权限请求、登录、核心业务与 Review 模式。
- [ ] 未取得上述证据前，不把版本标记为“可提交审核”。
