# Internal TestFlight 验收清单

构建：1.5.0 (5)，内部测试组，不创建外部测试组。

必须分别在实体 iPhone 与实体 iPad 完成：

- [ ] 宿主 App 正常安装和启动。
- [ ] “设置 → Apps → Safari → 扩展”中可启用 HB Supplier Order。
- [ ] 默认远端地址显示 https://hotbargain.vip。
- [ ] 先在 https://hotbargain.vip/shop 使用现有内部测试账号登录；从网页入口或 Safari 工具栏打开助手后自动识别同一账号，全程不出现扩展账号密码输入。
- [ ] 选择授权门店；点击“断开扩展”后只断开助手，HB SHOP 网站仍保持登录，重新检查可无密码连接。
- [ ] 网站退出后，助手短期会话失效并回到“打开或登录 HB SHOP”状态，不残留已连接账号。
- [ ] 网站父会话刷新后，保持 `/shop` 标签页打开；助手会清除失效短期 token，并通过该网页会话重新授权。
- [ ] 关闭全部 HB SHOP 标签页后打开助手，会显示网站引导；点击“打开 HB SHOP”可创建/聚焦 `/shop`，网站 Cookie 仍有效时自动继续授权。
- [ ] 拒绝 hotbargain.vip 站点访问权限时安全失败且不显示凭据表单；重新授予权限后可恢复。
- [ ] 授权过程中将 Safari 切到后台再回到前台，重新检查后状态和数据正确，无重复登录或崩溃。
- [ ] 打开 https://www.meteorparty.com.au/Party-Favors/Party-Favors-Allfavors，无需供应商登录。
- [ ] 授予该供应商站点权限，点击 Safari 工具栏 HB 图标可打开完整助手。
- [ ] 验证网页内商品入口、采购记录、销售记录，以及商品的 TOP 10% / TOP 20% / TOP 30% 销量档位。
- [ ] 验证供应商热销 TOP 30% 的 60/90 天切换、50/100/200 每页选择、跨页全局名次和窄屏分页布局。
- [ ] 在供应商热销排行榜点击商品总销量，验证分店销量明细、搜索、排序、零销量门店和返回后的焦点恢复。
- [ ] 授权 www.dats.com.au 后打开 https://www.dats.com.au/office-stationery，停留数秒后在 HB 后台“POS 商品管理 → 供应商分类管理”看到 DATS / Office Stationery 与对应商品；助手“浏览分类页时自动采集”关闭后不再回传。
- [ ] 保持 DATS 标签页为当前页，在助手点击“采集全部分类”，验证进度、当前分类、已回传货号计数；点击“中止”立即停止。
- [ ] 采集过程中切到其他 App 让 iOS 挂起 Safari 或让标签页跳转，助手显示“已中断”；返回 DATS 标签页点击“继续”从断点接着采集，失败分类可“重试失败”。
- [ ] 验证重新启动、供应商权限拒绝和网络失败的安全状态。
- [ ] TestFlight 没有新增阻断崩溃。

若失败，修复后只将 CURRENT_PROJECT_VERSION 从 6 起继续递增；MARKETING_VERSION 与扩展语义版本仍保持 1.5.0。

两台设备全部通过后，按授权直接进入正式 App Review 与 Unlisted 申请，不再追加发布确认。
