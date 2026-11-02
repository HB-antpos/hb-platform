-- 使用帮助 FAQ：同步 WPF 与移动端最近的功能改动（改 9 条旧问答 + 新增 2 条）。
-- 背景：迁移 20261025.001-help-faq 只在两张表为空时灌种子，生产库已灌过；种子文件（help-faq-seed.json）改了不会自动进库，需执行本脚本。
-- 内容与 Data/SeedData/help-faq-seed.json、apps/web/.../faqSnapshot.json 一致（本文件由种子生成，勿手改）。
--   改动的 9 条：color-theme、app-update、pos-daily-close、order-history（Windows 收银端）；
--                product-sales、supplier-invoices、create-staff、promotions-manage、promotion-not-applied（移动端）。
--   新增的 2 条：online-receipt（销售与报表）、minor-guardian-link（员工与权限）。
-- 安全措施：
--   1) 先把 9 条旧行完整备份到 dbo.HelpFaqQuestion_Bak_20261031（已存在则不覆盖，保留最初备份）；
--   2) 只改 Version = 1 且尚未被本脚本改过的行（有人在后台改过的行 Version > 1，不会被盖掉；若出现这种行会 THROW 并整体回滚）；
--   3) 按 Code 判断，可重复执行：重复执行时 UPDATE 命中 0 行、INSERT 因已存在而跳过。
-- 回滚（公开接口有 5 分钟内存缓存，回滚后同样要等几分钟）：
--   UPDATE q SET q.TitleZh=b.TitleZh, q.TitleEn=b.TitleEn, q.WhereZh=b.WhereZh, q.WhereEn=b.WhereEn,
--                q.StepsZh=b.StepsZh, q.StepsEn=b.StepsEn, q.TipsZh=b.TipsZh, q.TipsEn=b.TipsEn,
--                q.RulesZh=b.RulesZh, q.RulesEn=b.RulesEn, q.RelatedZh=b.RelatedZh, q.RelatedEn=b.RelatedEn,
--                q.Permissions=b.Permissions, q.Version=b.Version, q.UpdatedAt=b.UpdatedAt, q.UpdatedBy=b.UpdatedBy
--     FROM dbo.HelpFaqQuestion q JOIN dbo.HelpFaqQuestion_Bak_20261031 b ON b.QuestionGUID = q.QuestionGUID;
--   DELETE FROM dbo.HelpFaqQuestion WHERE Code IN (N'online-receipt', N'minor-guardian-link');
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;

DECLARE @Now datetime2 = SYSUTCDATETIME();

-- 0) 前置检查：9 条旧问答必须都存在且未删除
IF (SELECT COUNT(*) FROM [dbo].[HelpFaqQuestion] WHERE [Code] IN (N'color-theme', N'app-update', N'pos-daily-close', N'order-history', N'product-sales', N'supplier-invoices', N'create-staff', N'promotions-manage', N'promotion-not-applied') AND [IsDeleted] = 0) <> 9
    THROW 51991, N'待更新的 FAQ 问答不全（应有 9 条），中止。请先核对 HelpFaqQuestion。', 1;

-- 1) 备份旧行（已存在则保留最初备份）
IF OBJECT_ID(N'dbo.HelpFaqQuestion_Bak_20261031', N'U') IS NULL
    SELECT * INTO [dbo].[HelpFaqQuestion_Bak_20261031] FROM [dbo].[HelpFaqQuestion] WHERE [Code] IN (N'color-theme', N'app-update', N'pos-daily-close', N'order-history', N'product-sales', N'supplier-invoices', N'create-staff', N'promotions-manage', N'promotion-not-applied');

-- 2) 更新 9 条旧问答
UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'如何切换界面主题（配色 / 长者模式）？',
        [TitleEn] = N'How do I change the colour theme or use Easy View?',
        [WhereZh] = N'窗口顶部标题栏 · 调色板按钮（旁边显示当前主题名）',
        [WhereEn] = N'Window title bar · palette button (shows the current theme name)',
        [StepsZh] = N'["点窗口顶部标题栏的调色板按钮 [k:Color theme|界面配色]，按钮旁显示当前主题名。","选择一个配色：[k:Blue|蓝色]（默认）、[k:Graphite|石墨夜]、[k:Celadon|青瓷]、[k:Candy|糖果紫]、[k:Sunny|阳光橙]、[k:Ocean|海洋青]、[k:Rainbow|彩虹分区] 或 [k:Easy View|长者模式]。","点选后立即生效，并记在你的员工账号上，下次登录自动沿用。"]',
        [StepsEn] = N'["Click the palette button [k:Color theme|界面配色] in the window title bar. It shows the name of the current theme.","Pick one: [k:Blue|蓝色] (default), [k:Graphite|石墨夜], [k:Celadon|青瓷], [k:Candy|糖果紫], [k:Sunny|阳光橙], [k:Ocean|海洋青], [k:Rainbow|彩虹分区] or [k:Easy View|长者模式].","It applies straight away and is saved to your staff account, so it is used the next time you sign in."]',
        [TipsZh] = N'["「长者模式」只放大字号，按钮名称和位置不变。","每个员工有自己的配色：登录后会自动切换成你选过的配色，换哪台收银机登录都会跟着你。","第一次登录、还没选过配色时，会弹出「欢迎」窗口：点卡片可以预览整屏效果，点 [p:Start with …|使用「…」开始收银] 保存（「…」是你选中的配色名）。这个窗口没有关闭按钮，选定后以后不再弹出。","离线登录时沿用这台收银机缓存的配色，不弹出选择窗口；离线时换的配色会在下次联网登录时补传。","旧的「经典」「暖纸」配色已取消：原来用「经典」的收银机现在是 [k:Candy|糖果紫]，用「暖纸」的回到 [k:Blue|蓝色]。"]',
        [TipsEn] = N'["Easy View only enlarges text. Button names and positions stay the same.","Every staff member has their own theme. After you sign in the till switches to the theme you chose, on any register.","The first time you sign in without a saved theme, a Welcome window appears. Tap a card to preview the whole screen, then tap [p:Start with …|使用「…」开始收银] to save it (“…” is the theme you picked). The window has no close button and does not appear again once you choose.","When you sign in offline, the till keeps using the theme cached on this register and does not show the picker. A theme you change while offline is uploaded the next time you sign in online.","The old Classic and Paper themes have been removed: tills that used Classic now show [k:Candy|糖果紫], and tills that used Paper go back to [k:Blue|蓝色]."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = NULL,
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'color-theme' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'收银端如何升级到新版本？',
        [TitleEn] = N'How do I update the POS to a new version?',
        [WhereZh] = N'右下角状态栏 · [d:Update ready|更新已准备好]',
        [WhereEn] = N'Bottom-right status bar · [d:Update ready|更新已准备好]',
        [StepsZh] = N'["收银端会在启动时和运行中（约每小时）自动检查新版本；下载完成后右下角出现 [d:Update ready|更新已准备好] 或 [d:Update required|必须更新]。","先结束当前交易：清空购物车，确认没有正在刷卡或已收款未完成的订单。","点 [p:Install update|安装更新]；可选更新会弹出 [d:Software update|软件更新] 窗口，可看 [d:What''s new|更新内容]，点 [p:Restart and install|重启并安装]（或 [k:Install later|稍后安装]）。","收银端自动关闭并安装，完成后自动重新打开，期间不要关机或断电。","想手动检查：[k:Settings|设置] → [k:Data maintenance|数据维护] → [d:Software update|软件更新] → [k:Check update|检查更新]。"]',
        [StepsEn] = N'["The POS checks for updates at startup and about hourly. When a download is ready, [d:Update ready|更新已准备好] or [d:Update required|必须更新] appears at the bottom right.","Finish the current sale first: clear the cart and make sure no card payment is in progress.","Tap [p:Install update|安装更新]. For an optional update, the [d:Software update|软件更新] window shows [d:What''s new|更新内容]; tap [p:Restart and install|重启并安装] (or [k:Install later|稍后安装]).","The POS closes, installs and reopens by itself. Do not switch off the PC.","To check manually: [k:Settings|设置] → [k:Data maintenance|数据维护] → [d:Software update|软件更新] → [k:Check update|检查更新]."]',
        [TipsZh] = N'["当前版本号显示在右下角状态栏 [d:Current version|当前版本]。","晚上收银机空闲时，也会自动安装已下载的更新。","新版收银端通过管理员更新代理安装：白天点 [p:Install update|安装更新] 和夜间自动安装，都不再弹出 Windows 的授权窗口。第一次升级到带这项功能的版本时，那一次仍可能弹出授权窗口，选「是」即可；个别电脑上代理不可用时，更新窗口会退回原来的安装方式，同样选「是」。","更新失败时版本不变、收银数据不受影响，可在更新窗口里点「重试更新」。","推送哪个版本、哪些分店或机器先升级，由总部在 Web 后台的版本发布中心控制。"]',
        [TipsEn] = N'["The current version is shown at the bottom right as [d:Current version|当前版本].","Downloaded updates also install automatically when the till is idle overnight.","New POS versions install updates through an admin update agent, so neither the daytime [p:Install update|安装更新] nor the overnight install shows the Windows permission prompt. The first upgrade to a version that includes this may still ask once — choose Yes. If the agent is unavailable on a particular PC, the update window falls back to the old way of installing; choose Yes there too.","If an update fails, the version and sales data stay as they were; use “Retry update” in the update window.","Head office controls which version goes to which stores or tills in the Web admin Release Center."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = NULL,
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'app-update' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'如何日结（清点现金、Linkly 结算、打印日结单）？',
        [TitleEn] = N'How do I do the daily close (cash count, Linkly settlement, printout)?',
        [WhereZh] = N'区域 ③ 功能砖 [k:Daily Close|日结]',
        [WhereEn] = N'Area ③ tile [k:Daily Close|日结]',
        [StepsZh] = N'["§清点现金并保存","先结束当前交易，点右侧功能砖 [k:Daily Close|日结]。","核对顶部 [i:Business Date|营业日期]（点 [k:Today|今天] 切回今天），点 [p:New Daily Close|创建新日结]；上次没做完时按钮显示为 [p:Continue Daily Close|继续日结]。","在 [d:Cash Count|现金盘点] 里逐个点选纸币、硬币面额，用数字键输入钱箱里的数量，点 [k:Apply · Next|应用 · 下一项] 跳到下一个面额。","在右侧 [d:Cash Reconciliation|现金对账] 核对 [d:Expected Cash|应有现金]、[d:Cash Counted|现金实盘] 和 [d:Over / Short|溢长 / 短款]（显示短款、长款或平账）；[d:System Totals|系统汇总] 里可看销售、退款和订单数。","点 [p:Save & Finalize|保存并完成]，日结保存并自动打印日结单。","§Linkly 刷卡结算","按钮下方显示 [d:Also sends Linkly settlement|同时发送 Linkly 结算] 时，保存后会自动向刷卡机结算、打印银行结算单，并停在 [tab:Linkly Settlement|Linkly 结算] 页签显示结果。","没发出或失败（例如刷卡机离线）：检查刷卡机和 Linkly 程序后，在 [tab:Linkly Settlement|Linkly 结算] 点 [k:Settle & Print|结算并打印]，在 [d:Confirm Linkly settlement|确认刷卡机日结] 里再点 [k:Settle & Print|结算并打印] 重试。","§重打日结单或银行结算单","在 [tab:Daily Close History|日结历史] 选好营业日期，点选一条记录，再点 [k:Reprint Daily Close|重打印日结单]。","银行结算单：在 [tab:Linkly Settlement|Linkly 结算] 选中记录，点 [k:Reprint Selected Settlement|重打所选结算单]。"]',
        [StepsEn] = N'["§Count the cash and save","Finish the current sale, then tap the [k:Daily Close|日结] tile on the right.","Check the [i:Business Date|营业日期] at the top (tap [k:Today|今天] to go back to today), then tap [p:New Daily Close|创建新日结]. If one was left unfinished, the button shows [p:Continue Daily Close|继续日结].","In [d:Cash Count|现金盘点], tap each note and coin denomination, type how many are in the till, then tap [k:Apply · Next|应用 · 下一项] to move to the next one.","On the right, check [d:Expected Cash|应有现金], [d:Cash Counted|现金实盘] and [d:Over / Short|溢长 / 短款] under [d:Cash Reconciliation|现金对账] (shown as Short, Over or Balanced). [d:System Totals|系统汇总] shows sales, refunds and order count.","Tap [p:Save & Finalize|保存并完成]. The daily close is saved and printed.","§Linkly (EFTPOS) settlement","If [d:Also sends Linkly settlement|同时发送 Linkly 结算] shows under the button, saving also settles the card terminal and prints the bank settlement receipt, then stays on the [tab:Linkly Settlement|Linkly 结算] tab to show the result.","Not sent or failed (e.g. terminal offline): check the terminal and the Linkly program, then on [tab:Linkly Settlement|Linkly 结算] tap [k:Settle & Print|结算并打印] and confirm with [k:Settle & Print|结算并打印] in [d:Confirm Linkly settlement|确认刷卡机日结].","§Reprint the daily close or bank settlement","On [tab:Daily Close History|日结历史], pick the business date, select a record and tap [k:Reprint Daily Close|重打印日结单].","Bank settlement receipt: on [tab:Linkly Settlement|Linkly 结算], select the record and tap [k:Reprint Selected Settlement|重打所选结算单]."]',
        [TipsZh] = N'["清点到一半可点 [k:Return to History|返回历史] 保留草稿，之后点「继续日结」接着做；[k:Discard Draft|放弃草稿] 会清空已输入的数量，无法撤销。","一天可以做多次日结；当天 Linkly 已结算成功后，再次日结不会重复结算。补做以前日期的日结不会发送结算，[k:Settle & Print|结算并打印] 只能结算今天。","只有这台收银机接的是 Linkly 刷卡机、且没开「使用手动刷卡」时，收银端才会发送 Linkly 结算。","提示结算结果未知时不要再次提交：有 [k:Query result from Linkly|向 Linkly 查询结果] 按钮就先点它；仍查不到，由主管对照刷卡机在 [d:Supervisor resolution|主管人工处理] 选 [k:Confirmed succeeded|确认结算成功]、[k:Confirmed failed|确认结算失败] 或 [k:Confirmed not submitted|确认未提交结算]，再点 [k:Confirm decision|确认结论]（只记录结论，不会再次结算）。","日结保存成功约 30 秒后，收银端会自动登出当前收银员，回到扫码登录页并提示「日结已完成，已自动登出。」，留这段时间让你看清日结和结算结果。如果这期间又开始收银（购物车有变化）或正在付款，就不会登出；购物车和本机数据都会保留。","这台收银机已连续运行 7 天以上时，日结后不再弹出「建议重启」，而是自动排到凌晨（默认 3:00）重启电脑，日结页和登录页会提示重启时间。重启前有 30 秒可点 [k:Cancel restart|取消重启] 的倒计时；有未完成的销售、付款或刷卡任务就不重启。重启后 HB POS 会自动打开（Windows 账户有密码时，需要先登录 Windows）。收银机要整晚开着；想关掉这项功能，到 [k:Settings|设置] → [k:Data maintenance|数据维护] 关闭 [t:Restart overnight after daily close|日结后凌晨自动重启]。","日结记录会上传总部：Web 后台 [k:POS Admin|收银管理] → [k:Daily Closes|日结记录]，或 App 工作台 [k:Daily closes|日结记录] 都能查看。"]',
        [TipsEn] = N'["Halfway through counting? Tap [k:Return to History|返回历史] to keep the draft, then use Continue Daily Close later. [k:Discard Draft|放弃草稿] clears the counts and cannot be undone.","You can close more than once a day; once today’s Linkly settlement has succeeded it is not sent again. A daily close for an earlier date never settles, and [k:Settle & Print|结算并打印] only works for today.","The POS only sends a Linkly settlement when this till uses a Linkly terminal and “Use manual card payment” is off.","If the settlement result is unknown, do not resubmit. Tap [k:Query result from Linkly|向 Linkly 查询结果] if it is shown; if there is still no answer, a supervisor checks the terminal and picks [k:Confirmed succeeded|确认结算成功], [k:Confirmed failed|确认结算失败] or [k:Confirmed not submitted|确认未提交结算] under [d:Supervisor resolution|主管人工处理], then [k:Confirm decision|确认结论] (this only records the outcome; nothing is settled again).","About 30 seconds after the daily close is saved, the POS signs out the current cashier and returns to the scan-to-sign-in screen with “Daily close done. Signed out automatically.” The pause lets you read the close and settlement results. It will not sign out if you start another sale (the cart changes) or a payment is in progress. The cart and local data are kept.","When this till has been running for 7 days or more, it no longer shows “Restart this POS?” after the close. Instead the restart is scheduled for the early morning (3:00 by default), and the daily close and sign-in screens show the time. Before restarting there is a 30-second countdown you can stop with [k:Cancel restart|取消重启], and the restart is skipped if a sale, payment or card task is unfinished. HB POS opens by itself afterwards (if the Windows account has a password, sign in to Windows first). Leave the PC on overnight. To turn this off, go to [k:Settings|设置] → [k:Data maintenance|数据维护] and switch off [t:Restart overnight after daily close|日结后凌晨自动重启].","Daily closes are uploaded to head office. View them in Web admin under [k:POS Admin|收银管理] → [k:Daily Closes|日结记录], or on the app’s Home under [k:Daily closes|日结记录]."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = N'["DailyClose.View","DailyClose.Save","DailyClose.Reprint"]',
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'pos-daily-close' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'如何查找历史订单、补打小票？',
        [TitleEn] = N'How do I find a past order or reprint a receipt?',
        [WhereZh] = N'[k:Recall Order|取单] → [tab:Local|本地] / [tab:Online|在线]',
        [WhereEn] = N'[k:Recall Order|取单] → [tab:Local|本地] / [tab:Online|在线]',
        [StepsZh] = N'["点 [k:Recall Order|取单]，切到 [tab:Local|本地] 或 [tab:Online|在线] 标签。","选日期：[k:Today|今天]、[k:Yesterday|昨天] 或 [k:Last 7 days|近 7 天]。","在 [d:ORDER LOOKUP|订单查询] 搜索框输入订单号、商品货号或条码；也可以直接扫小票或商品条码，会自动搜索。","结果超过一页时，用底栏的 [k:Previous|上一页]、[k:Next|下一页] 翻页，中间显示当前页码；第一页的上一页、最后一页的下一页会变灰。每页 50 条，换了筛选、日期或来源会回到第 1 页。","在结果行点 [k:View receipt|查看小票] 或 [k:Order details|订单明细]；要补打，在小票预览里点 [k:Reprint|重打]。"]',
        [StepsEn] = N'["Tap [k:Recall Order|取单] and open the [tab:Local|本地] or [tab:Online|在线] tab.","Pick a date: [k:Today|今天], [k:Yesterday|昨天] or [k:Last 7 days|近 7 天].","In [d:ORDER LOOKUP|订单查询], type the order number, item number or barcode — or just scan the receipt or item barcode to search.","If there is more than one page of results, use [k:Previous|上一页] and [k:Next|下一页] at the bottom; the page number shows in between. Previous is greyed out on the first page and Next on the last. Each page has 50 orders, and changing the filter, date or source goes back to page 1.","On the result, tap [k:View receipt|查看小票] or [k:Order details|订单明细]. To reprint, tap [k:Reprint|重打] in the receipt preview."]',
        [TipsZh] = N'["历史订单页不能直接退款，退款请用功能砖 [k:Refund|退款]（见「有小票，如何退款？」）。","只有 [tab:Local|本地] 和 [tab:Online|在线] 两个页签分页；[tab:Lay-bys|分期订单] 和 [tab:Held|共享挂单] 页签一次全部显示。"]',
        [TipsEn] = N'["You cannot refund from order history. Use the [k:Refund|退款] tile instead (see “How do I refund with a receipt?”).","Only the [tab:Local|本地] and [tab:Online|在线] tabs are paged; [tab:Lay-bys|分期订单] and [tab:Held|共享挂单] show everything at once."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = N'["Sales.RecallOrder"]',
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'order-history' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'如何查看商品销售数据？',
        [TitleEn] = N'How do I see product sales?',
        [WhereZh] = N'底部 [tab:Reports|报表] 或 工作台 [k:Product activity|商品进销查询]',
        [WhereEn] = N'Bottom [tab:Reports|报表] tab, or Home [k:Product activity|商品进销查询]',
        [StepsZh] = N'["§看各供应商、各商品的销售汇总","点底部 [tab:Reports|报表]，切到 [tab:Product Report|商品报告]。","选分店和日期，或点快捷 [k:Today|今天]、[k:This week|本周]、[k:This month|本月]。","输入货号或条码，点 [k:Search|查询] 只看这个商品。","§查某一个商品的进货和销售（进销对照）","工作台「商品销售」分组点 [k:Product activity|商品进销查询]，选分店后扫码或输入货号，点 [k:Search|查询]。","在顶部选统计周期：[k:30 days|近 30 天]、[k:90 days|近 90 天]（默认）、[k:180 days|近 180 天]；或点日历按钮选 [k:Custom period|自定义周期]，点 [k:Reset to 90 days|恢复近 90 天] 回到默认。","看 [d:Movement summary|进销汇总]：左边是进货（仓库商品是仓库配货），右边是销量，下面的进度条是 [d:Sell-through|售罄率]（销量 ÷ 进货量）；再往下依次是 [d:Net in − out|进销差]、[d:Daily avg sold|日均销量]、[d:Avg price|平均售价]（仓库商品显示 [d:Fill rate|配货满足率]）。","看趋势图：[tab:Periods|分段] 用绿色柱表示每段时间的销量，蓝色气泡标出到货的日期和数量；切到 [tab:Cumulative|累计] 看累计进货和累计销量两条线。","往下是流水，用 [k:By delivery|按进货批次] 或 [k:By date|按日期] 切换（仓库商品是 [k:By allocation|按配货批次]）。按批次时，每批进货下方显示这批货的销量、销售天数和售罄率，点「展开 N 个销售日」看每天的销量；按日期时进货和销售混排，可用 [k:All|全部]、[k:Received only|只看进货]、[k:Sales only|只看销售] 筛选。","点底部 [k:View all POS store sales|查看全部 POS 分店销售] 对比各店。"]',
        [StepsEn] = N'["§Sales summary by supplier and product","Tap the [tab:Reports|报表] tab, then open [tab:Product Report|商品报告].","Choose store and dates, or tap [k:Today|今天], [k:This week|本周], [k:This month|本月].","Type an item number or barcode and tap [k:Search|查询] to see one product.","§Received vs sold for one product","In the Product sales group on Home, tap [k:Product activity|商品进销查询]. Choose a store, scan or type the item, and tap [k:Search|查询].","Pick the period at the top: [k:30 days|近 30 天], [k:90 days|近 90 天] (default) or [k:180 days|近 180 天]; or tap the calendar button for a [k:Custom period|自定义周期]. Tap [k:Reset to 90 days|恢复近 90 天] to go back.","Read the [d:Movement summary|进销汇总]: received (or warehouse allocation) on the left, units sold on the right, and the bar below is [d:Sell-through|售罄率] (sold ÷ received). Then come [d:Net in − out|进销差], [d:Daily avg sold|日均销量] and [d:Avg price|平均售价] (warehouse items show [d:Fill rate|配货满足率]).","Check the chart: on [tab:Periods|分段], green bars show units sold per period and blue bubbles mark delivery dates and quantities; switch to [tab:Cumulative|累计] to see cumulative received and sold as two lines.","Below is the history, switched with [k:By delivery|按进货批次] or [k:By date|按日期] (warehouse items use [k:By allocation|按配货批次]). By delivery shows, under each delivery, how many sold, over how many selling days and the sell-through; tap “Show N selling days” for the daily sales. By date mixes deliveries and sales; filter with [k:All|全部], [k:Received only|只看进货] or [k:Sales only|只看销售].","Tap [k:View all POS store sales|查看全部 POS 分店销售] at the bottom to compare stores."]',
        [TipsZh] = N'["默认看近 90 天，「今天」按分店所在时区计算。","售罄率超过 100% 会变橙色：卖得比本区间进得多，多半是在卖区间开始前的存货。这个页面不显示库存。","本区间没有进货时，销售会归在「本区间无进货」下，不计算售罄率；上一次进货会作为历史记录显示，不计入合计。","仓库商品的「进」是仓库出库配货；订货还没配齐的，会提示还有多少件待配货。"]',
        [TipsEn] = N'["The default is the last 90 days; “today” follows the store’s own time zone.","Sell-through turns orange above 100%: more was sold than was received in the period, usually stock from before the period. This page does not show stock on hand.","If nothing was received in the period, the sales are grouped under “No purchases in this period” and sell-through is not calculated; the last purchase is shown as history and is not counted in the totals.","For warehouse items, “in” means warehouse dispatches (allocations). If orders are not fully allocated yet, the page shows how many units are still waiting."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = N'["Reports.ProductMovement.View","StoreProducts.View"]',
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'product-sales' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'如何查看澳洲供应商发票，并打印商品标签？',
        [TitleEn] = N'How do I view Australian supplier invoices and print labels?',
        [WhereZh] = N'工作台 · 商品进货 · [k:Local supplier invoices|供应商发票]',
        [WhereEn] = N'Home · Product purchasing · [k:Local supplier invoices|供应商发票]',
        [StepsZh] = N'["工作台「商品进货」分组点 [k:Local supplier invoices|供应商发票]。","用 [i:Search invoice no.|搜索发票号] 查找，或按入库状态 [tab:Not received|未入库]、[tab:Partially received|部分入库]、[tab:Received|已入库] 筛选；分店、供应商、日期在 [k:More filters|更多筛选]。列表默认显示近 90 天创建的发票，新的在前；日期可在 [k:Created date|创建日期] 和 [k:Order date|订单日期] 之间切换，还能按 [tab:To check|待检测]、[tab:Checked|已检测] 和 [k:Has uncreated products|有商品未创建] 筛选。","点一张发票进入 [d:Invoice details|发票明细]，查看每行的上次进货价、进货价、数量和小计；可用 [tab:Price up|涨价]、[tab:Price down|减价] 只看价格变动的商品。","点 [k:Sales analysis|销量分析]（发票列表卡片和明细顶部都有入口），按商品查看进货后 30 / 60 / 90 天的销量。","§打印商品标签","在明细行点 [k:Edit product|商品编辑]，会跳到商品页并带出这个商品。","在商品页底部点 [k:Regular|普通]、[k:Small|小标签]、[k:Discount|折扣] 或 [k:Large|大标签] 打印标签；左边的打印机图标用颜色表示连接状态。","改份数：点左边的打印机图标打开 [d:Print settings|打印设置]，调 [i:Print quantity|打印数量]；连打印机：[k:Printer settings|打印机设置] → [k:Scan for printers|扫描打印机] → [k:Connect|连接]。","§管理员：批量处理发票明细","进入 [d:Invoice details|发票明细]，先点 [k:Check products|检测商品]，看每行商品是否已建档（设置了筛选时，可选只检测筛选出的行或整张单）。","点 [k:Select|多选] 勾选明细行；底栏可点「全选当前 N 行」或 [k:Clear all|取消全选]。","底栏可以：[k:Set action|设置操作]（选中即保存）；[k:Bulk edit|批量编辑]（进货价、零售价、自动定价、特殊商品、折扣率，每项单独勾选才会修改）；[k:Link match|选用匹配]（对检测后主档不存在、但条码只匹配到一个已有商品的行，自动选用该商品，避免重复新建）。","点 [k:Run selected|执行勾选] 只执行勾选的行，或点「执行待执行 N」执行整张发票所有待执行的行。确认页按 [d:Create product|新建商品]、[d:Update cost · up|更新进货价 · 涨价]、[d:Update cost · down|更新进货价 · 降价]、[d:Update cost · same or no last cost|更新进货价 · 持平或无上次价] 分组，勾选要执行的组，再点「执行 N 行」。","有行失败时，可以点「跳过 N 行，执行其余 M 行」，先把没问题的行执行掉。"]',
        [StepsEn] = N'["In the Product purchasing group on Home, tap [k:Local supplier invoices|供应商发票].","Search with [i:Search invoice no.|搜索发票号], or filter by [tab:Not received|未入库], [tab:Partially received|部分入库] or [tab:Received|已入库]. Store, supplier and dates are under [k:More filters|更多筛选]. The list shows invoices created in the last 90 days, newest first; switch the date between [k:Created date|创建日期] and [k:Order date|订单日期], and filter by [tab:To check|待检测], [tab:Checked|已检测] and [k:Has uncreated products|有商品未创建].","Tap an invoice to open [d:Invoice details|发票明细]: last cost, cost, quantity and subtotal per line. Use [tab:Price up|涨价] or [tab:Price down|减价] to see only price changes.","Tap [k:Sales analysis|销量分析] (on the invoice card and at the top of the details) to see, per product, sales in the 30 / 60 / 90 days after the purchase.","§Print product labels","Tap [k:Edit product|商品编辑] on a line. The product page opens with that item.","At the bottom, tap [k:Reg|普通], [k:Small|小标签], [k:Disc|折扣] or [k:Large|大标签] to print. The printer icon on the left shows the connection status by colour.","To change copies, tap the printer icon on the left to open [d:Print settings|打印设置] and set [i:Print quantity|打印数量]. To connect a printer: [k:Printer settings|打印机设置] → [k:Scan for printers|扫描打印机] → [k:Connect|连接].","§Admins: bulk-process invoice lines","Open [d:Invoice details|发票明细] and tap [k:Check products|检测商品] first to see whether each product already exists (with a filter on, you can check only the filtered lines or the whole invoice).","Tap [k:Select|多选] and tick the lines; the bottom bar offers “Select all N” or [k:Clear all|取消全选].","The bottom bar offers [k:Set action|设置操作] (saved as soon as you pick one); [k:Bulk edit|批量编辑] (cost, retail price, auto pricing, special product and discount rate — only the items you tick are changed); and [k:Link match|选用匹配] (for checked lines that are missing from the master but whose barcode matches exactly one existing product, it links that product so you do not create a duplicate).","Tap [k:Run selected|执行勾选] to run only the ticked lines, or “Run pending N” to run every pending line on the invoice. The confirmation page groups them as [d:Create product|新建商品], [d:Update cost · up|更新进货价 · 涨价], [d:Update cost · down|更新进货价 · 降价] and [d:Update cost · same or no last cost|更新进货价 · 持平或无上次价]; tick the groups to run, then tap “Run N lines”.","If some lines fail, tap “Skip N, run the other M” to run the ones that are fine first."]',
        [TipsZh] = N'["发票页本身不能直接打标签，要通过「商品编辑」跳到商品页打印。","店长和仓库管理员只能查看；检测、批量编辑和执行只有管理员账号能用。","手机上只执行「新建商品」和「更新进货价」；「更新货号」「添加多码」请到 Web 后台处理。","新建的商品不会同步到总部（HQ），需要之后在 Web 后台推送；进货价只更新这张发票所属分店的价格。","进货价涨跌超过 40% 时，会再弹一次确认。"]',
        [TipsEn] = N'["Labels cannot be printed from the invoice itself — go through Edit product.","Store managers and warehouse admins can only view; checking, bulk editing and running are for admin accounts.","The app only runs Create product and Update cost. Run Update item no. and Add multi-code on the Web admin.","New products are not synced to head office (HQ); push them from the Web admin later. Cost updates only apply to the store this invoice belongs to.","A cost change of more than 40% up or down asks for one more confirmation."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = N'["LocalPurchase.MobileView","StoreProducts.View","LocalPurchase.Edit"]',
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'supplier-invoices' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'如何创建员工账号？',
        [TitleEn] = N'How do I create a staff account?',
        [WhereZh] = N'工作台 · 人员与管理 · [k:Staff|人员管理]',
        [WhereEn] = N'Home · Staff & admin · [k:Staff|人员管理]',
        [StepsZh] = N'["在 [tab:Home|工作台] 点 [k:Staff|人员管理]。","顶部选择你管理的分店（选「全部分店」时不会出现新增按钮）。","点右下角 [p:Add staff member|新增员工]。","在 [d:Name and contact|姓名与联系] 填姓、名（必填）和电话（必填，04 开头的澳洲手机号）；在 [d:Login details|登录信息] 填员工邮箱和账号（账号默认等于邮箱）。","选择 [i:Date of birth|生日]（必填，用于判断是否未成年）。未满 18 岁时会出现 [d:Guardian|监护人] 区域，必须填 [i:Guardian name|监护人姓名]、[i:Relationship|关系]、[i:Guardian mobile|监护人手机]（04 开头）和 [i:Guardian email|监护人邮箱]。","按需打开 [t:Enable now|立即启用]，点 [p:Create account|创建账号]。"]',
        [StepsEn] = N'["On [tab:Home|工作台], tap [k:Staff|人员管理].","Pick a store you manage at the top (Add is hidden when “All stores” is selected).","Tap [p:Add staff member|新增员工] at the bottom right.","Under [d:Name and contact|姓名与联系], enter first and last name (required) and phone (required, an Australian mobile starting with 04). Under [d:Login details|登录信息], enter the staff email and username (defaults to the email).","Choose the [i:Date of birth|生日] (required; it is used to check whether the person is under 18). If they are under 18, a [d:Guardian|监护人] section appears and you must fill in [i:Guardian name|监护人姓名], [i:Relationship|关系], [i:Guardian mobile|监护人手机] (starting with 04) and [i:Guardian email|监护人邮箱].","Turn on [t:Enable now|立即启用] if needed, then tap [p:Create account|创建账号]."]',
        [TipsZh] = N'["新员工默认归属本店，身份为店员 · 临时工。","账号创建后不能修改，提交前请核对。","监护人手机不能和员工本人的电话相同，监护人邮箱也不能和员工本人的邮箱相同。","账号创建后，到员工详情里给家长发英文签字链接（见「如何给未成年员工的家长发签字链接？」）。"]',
        [TipsEn] = N'["New staff belong to this store as Staff · Casual by default.","The username cannot be changed after creation. Check it first.","The guardian’s mobile cannot be the same as the staff member’s phone, and the guardian’s email cannot be the same as the staff member’s email.","After the account is created, send the parent an English signing link from the staff details (see “How do I send a parent the signing link for an under-18 staff member?”)."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = N'["Users.CreateStoreStaff"]',
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'create-staff' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'如何新建、修改本店促销？',
        [TitleEn] = N'How do I create or change a store promotion?',
        [WhereZh] = N'工作台 · 运营与报表 · [k:Promotions|促销]；或 商品查询 · [k:Promotions for this item|本商品促销]',
        [WhereEn] = N'Home · Operations & reports · [k:Promotions|促销]; or Product lookup · [k:Promotions for this item|本商品促销]',
        [StepsZh] = N'["打开 [k:Promotions|促销]，右上角选分店（只列你管理的分店）；用 [tab:Active|生效中]、[tab:Upcoming|未开始]、[tab:Disabled|已停用]、[tab:Ended|已结束]、[tab:All|全部] 切换查看。","点右下 [p:New promotion|新建促销]，填 [k:Promotion name|促销名称]、[k:Buy quantity|买满数量]（至少 2 件）和 [k:Bundle price|组合总价]，再选有效期。","在 [k:Products|参与商品] 点 [k:Scan|扫码添加] 或 [k:Search|搜索] 加入商品；每行会显示本店零售价和促销后的均价。","点 [p:Save|保存]。本店独享的促销可以直接用卡片右上角的开关启用或停用；要停掉正在进行的促销，进编辑页点 [k:End now|提前结束]。","在商品查询页扫到商品后，也可以在 [k:Promotions for this item|本商品促销] 里点 [k:New promotion for this item|为本商品新建促销]：填数量和总价，点 [p:Save & enable|保存并启用] 或 [k:Save as disabled|存为停用]。"]',
        [StepsEn] = N'["Open [k:Promotions|促销] and pick a store at the top right (only stores you manage are listed). Switch between [tab:Active|生效中], [tab:Upcoming|未开始], [tab:Disabled|已停用], [tab:Ended|已结束] and [tab:All|全部].","Tap [p:New promotion|新建促销] at the bottom right. Enter the [k:Promotion name|促销名称], [k:Buy quantity|买满数量] (at least 2) and [k:Bundle price|组合总价], then set the valid period.","Under [k:Products|参与商品], tap [k:Scan|扫码添加] or [k:Search|搜索] to add items. Each row shows the store retail price and the average price in the deal.","Tap [p:Save|保存]. A store-only promotion can be turned on or off with the switch on its card. To stop a running promotion, open it and tap [k:End now|提前结束].","From product lookup, after scanning an item you can also tap [k:New promotion for this item|为本商品新建促销] under [k:Promotions for this item|本商品促销]. Enter the quantity and price, then tap [p:Save & enable|保存并启用] or [k:Save as disabled|存为停用]."]',
        [TipsZh] = N'["App 里新建的促销只属于当前分店（本店独享）。覆盖多家店的促销只能查看；要调整时点 [k:Copy to this store|复制到本店]，复制出的本店版默认停用，改好后再启用。","只有一个商品时，组合价必须比「零售价 × 数量」便宜才能保存；多个商品混搭时，买满也没有优惠的商品会标红提醒，但仍可保存。","[k:End now|提前结束] 只把结束时间改为现在，不会删除促销；App 里不能删除促销。","保存时如果和排他促销时间重叠会弹出提示，可以选 [k:End on …|改到…结束] 或 [k:Save anyway|仍然保存]。","[tab:All|全部] 分段里有 [k:Hide ended|隐藏已结束] 开关，打开后不再显示已结束的促销。","列表和编辑页的扫码按钮，除了手机相机，也支持斑马 PDA 和扫码枪输入：列表只显示含该商品的促销，编辑页则把商品加入参与商品。"]',
        [TipsEn] = N'["Promotions created in the app belong to the current store only. Multi-store promotions are view-only; to adjust one, tap [k:Copy to this store|复制到本店]. The copy starts disabled, so edit it and then enable it.","With a single item, the bundle price must be lower than retail × quantity before you can save. In a mixed deal, items that get no saving are highlighted in red, but you can still save.","[k:End now|提前结束] only sets the end time to now; it does not delete the promotion. Promotions can''t be deleted in the app.","If the dates overlap an exclusive promotion, you''ll be warned when saving. Choose [k:End on …|改到…结束] or [k:Save anyway|仍然保存].","The [tab:All|全部] tab has a [k:Hide ended|隐藏已结束] switch that hides promotions that have already ended.","The scan buttons in the list and the editor work with the phone camera as well as a Zebra PDA or scanner gun: the list then shows only promotions that include the item, and the editor adds it to the promotion’s products."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = N'["Promotions.View","Promotions.Edit"]',
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'promotions-manage' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleZh] = N'收银没有按促销价计算怎么办？',
        [TitleEn] = N'Why isn''t the POS applying a promotion?',
        [WhereZh] = N'工作台 · 运营与报表 · [k:Promotions|促销]',
        [WhereEn] = N'Home · Operations & reports · [k:Promotions|促销]',
        [StepsZh] = N'["打开 [k:Promotions|促销]，点搜索框右侧的扫码按钮扫这个商品，只看包含它的促销。","看状态：[tab:Upcoming|未开始] 要到开始时间才生效；[tab:Disabled|已停用] 需要打开卡片开关；[tab:Ended|已结束] 需要重新新建。","核对数量：顾客买的件数要达到 [k:Buy quantity|买满数量]，收银才会按组合总价计算。","如果同一时段还有排他促销，收银只会按优先级高的那一条计价。"]',
        [StepsEn] = N'["Open [k:Promotions|促销], tap the scan button next to the search box and scan the item to see only promotions that include it.","Check the status: [tab:Upcoming|未开始] starts at its start time; [tab:Disabled|已停用] needs its switch turned on; [tab:Ended|已结束] needs a new promotion.","Check the quantity: the customer must buy at least the [k:Buy quantity|买满数量] for the bundle price to apply.","If an exclusive promotion runs at the same time, the POS applies only the one with higher priority."]',
        [TipsZh] = N'["促销必须绑定分店才会被收银计算；后台新建促销时分店为必选项。","新版收银端约每 60 秒自动刷新一次促销规则：在手机或 PDA 上新建、修改促销后，不用重启或手动同步，等一分钟左右即可。"]',
        [TipsEn] = N'["A promotion is only applied at the POS when it is assigned to a store. Choosing stores is required when creating one in the back office.","Current POS versions refresh promotion rules about every 60 seconds: after you create or change a promotion on the phone or PDA, no restart or manual sync is needed — just wait about a minute."]',
        [RulesZh] = NULL,
        [RulesEn] = NULL,
        [RelatedZh] = NULL,
        [RelatedEn] = NULL,
        [Permissions] = N'["Promotions.View"]',
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261031'
    WHERE [Code] = N'promotion-not-applied' AND [IsDeleted] = 0 AND [Version] = 1 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031';

-- 更新结果核对：9 条都必须已是本脚本改过的版本（命中 0 条说明有人在后台改过，整体回滚后人工处理）
IF (SELECT COUNT(*) FROM [dbo].[HelpFaqQuestion] WHERE [Code] IN (N'color-theme', N'app-update', N'pos-daily-close', N'order-history', N'product-sales', N'supplier-invoices', N'create-staff', N'promotions-manage', N'promotion-not-applied') AND [IsDeleted] = 0 AND [UpdatedBy] = N'faq-sql-20261031') <> 9
    THROW 51992, N'有 FAQ 问答不是初始版本（Version <> 1），未被更新，已中止并回滚。请比对备份后人工处理。', 1;

-- 3) 新增 2 条（SortOrder 取所在分组当前最大值 + 10，追加在分组末尾）
IF NOT EXISTS (SELECT 1 FROM [dbo].[HelpFaqQuestion] WHERE [Code] = N'online-receipt')
BEGIN
    DECLARE @G_online_receipt nvarchar(50) = (SELECT [GroupGUID] FROM [dbo].[HelpFaqGroup] WHERE [Tab] = N'mobile' AND [Code] = N'data' AND [IsDeleted] = 0);
    IF @G_online_receipt IS NULL
        THROW 51993, N'找不到 mobile/data 分组（HelpFaqGroup），中止。', 1;
    INSERT INTO [dbo].[HelpFaqQuestion]
        ([QuestionGUID], [GroupGUID], [Code], [TitleZh], [TitleEn], [WhereZh], [WhereEn], [StepsZh], [StepsEn], [TipsZh], [TipsEn], [RulesZh], [RulesEn], [RelatedZh], [RelatedEn], [Permissions],
         [SortOrder], [Status], [DraftJson], [PublishedAt], [Version],
         [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
    VALUES
        (CONVERT(nvarchar(50), NEWID()), @G_online_receipt, N'online-receipt', N'如何给客人发在线小票（二维码 / 链接）？', N'How do I send a customer an online receipt (QR code or link)?', N'工作台 · 商品销售 · [k:POS Orders|POS 订单] → [d:Order details|订单详情] → [tab:Receipt|小票]', N'Home · Product sales · [k:POS Orders|POS 订单] → [d:Order details|订单详情] → [tab:Receipt|小票]', N'["工作台「商品销售」分组点 [k:POS Orders|POS 订单]，找到订单（可按订单号搜索或扫小票），点开 [d:Order details|订单详情]。","默认停在 [tab:Receipt|小票] 页签，显示和收银机小票一样的纸面，顶部是订单号条码；切到 [tab:Items|商品明细] 看逐行明细。","点 [k:Online QR|在线二维码]：第一次点会生成这张订单的专属链接，并弹出 [d:Online receipt|在线小票] 二维码。客人用手机扫码就能查看，不用登录。","要发给客人：点 [k:Send to customer|发送给客人]，用手机的分享面板（微信、短信等）发送；也可以点 [k:Copy link|复制链接]，自己粘贴到聊天里。","链接发错了，或想让旧链接失效：点 [k:Regenerate|重新生成]，确认后旧链接立刻失效，新链接生效。"]', N'["In the Product sales group on Home, tap [k:POS Orders|POS 订单], find the order (search by order number or scan the receipt) and open [d:Order details|订单详情].","It opens on the [tab:Receipt|小票] tab, which looks like the till receipt, with the order barcode at the top. Switch to [tab:Items|商品明细] for the line-by-line list.","Tap [k:Online QR|在线二维码]. The first tap creates a link just for this order and opens the [d:Online receipt|在线小票] QR code. The customer scans it with their phone to view the receipt — no login needed.","To send it, tap [k:Send to customer|发送给客人] and pick WeChat, SMS or another app from the phone’s share sheet; or tap [k:Copy link|复制链接] and paste it yourself.","If the wrong link was sent, or you want the old one to stop working, tap [k:Regenerate|重新生成]. After you confirm, the old link stops working immediately and a new one takes over."]', N'["二维码下方会显示链接的有效期和被打开的次数。","只有点过 [k:Online QR|在线二维码] 的订单才会生成公开链接，没人点的订单不会产生链接。","部分订单状态暂不支持在线小票，页面会提示「该订单状态暂不支持在线小票」。","在线二维码是给客人看小票用的；订单详情里另有用于收银台扫码退货的订单二维码，两者不同。"]', N'["The QR panel shows how long the link is valid and how many times it has been opened.","A public link is only created for orders where [k:Online QR|在线二维码] was tapped; orders nobody taps get no link.","Some order statuses do not support online receipts yet; the page then says “Online receipts are not available for this order status”.","The online QR is for the customer to view the receipt. The order details also have a separate order QR used to scan a return at the till — they are not the same."]', NULL, NULL, NULL, NULL, N'["Orders.View"]',
         ISNULL((SELECT MAX([SortOrder]) FROM [dbo].[HelpFaqQuestion] WHERE [GroupGUID] = @G_online_receipt AND [IsDeleted] = 0), -10) + 10,
         N'Published', NULL, @Now, 1,
         @Now, N'faq-sql-20261031', @Now, N'faq-sql-20261031', 0);
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[HelpFaqQuestion] WHERE [Code] = N'minor-guardian-link')
BEGIN
    DECLARE @G_minor_guardian_link nvarchar(50) = (SELECT [GroupGUID] FROM [dbo].[HelpFaqGroup] WHERE [Tab] = N'mobile' AND [Code] = N'people' AND [IsDeleted] = 0);
    IF @G_minor_guardian_link IS NULL
        THROW 51993, N'找不到 mobile/people 分组（HelpFaqGroup），中止。', 1;
    INSERT INTO [dbo].[HelpFaqQuestion]
        ([QuestionGUID], [GroupGUID], [Code], [TitleZh], [TitleEn], [WhereZh], [WhereEn], [StepsZh], [StepsEn], [TipsZh], [TipsEn], [RulesZh], [RulesEn], [RelatedZh], [RelatedEn], [Permissions],
         [SortOrder], [Status], [DraftJson], [PublishedAt], [Version],
         [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
    VALUES
        (CONVERT(nvarchar(50), NEWID()), @G_minor_guardian_link, N'minor-guardian-link', N'如何给未成年员工的家长发签字链接？', N'How do I send a parent the signing link for an under-18 staff member?', N'工作台 · 人员与管理 · [k:Staff|人员管理] → 员工详情 → [d:Under-18 employment compliance|未成年用工合规]', N'Home · Staff & admin · [k:Staff|人员管理] → staff details → [d:Under-18 employment compliance|未成年用工合规]', N'["在 [k:Staff|人员管理] 点未满 18 岁员工所在行，进入 [d:Staff details|员工详情]，找到 [d:Under-18 employment compliance|未成年用工合规] 区域。","核对监护人姓名和联系方式；同意书类型按门店所在州自动选择（QLD 用儿童雇佣同意书 CE1，NSW 用公司未成年员工同意书）。","点 [p:Send guardian signing link|发送家长签字链接]，家长会收到英文的邮件 / 短信。如果其中一种没发出去，页面会提示是哪一种、什么原因。","家长打开链接，补全资料并手写签名。签完后状态变为 [d:Pending review|待审核]。","到工作台的 [k:Staff profile review|员工资料审核] 审核。通过后状态是 [d:Approved|审核通过]；需要补充的退回，状态变为 [d:Returned|退回修改]，可以回到这里重新发送。"]', N'["In [k:Staff|人员管理], tap the row of the under-18 staff member to open [d:Staff details|员工详情] and find the [d:Under-18 employment compliance|未成年用工合规] section.","Check the guardian’s name and contact details. The consent form is chosen automatically by the store’s state (QLD uses the CE1 child employment consent; NSW uses the company consent for under-18 staff).","Tap [p:Send guardian signing link|发送家长签字链接]. The parent gets an English email / SMS. If one of them could not be sent, the page tells you which one and why.","The parent opens the link, completes the details and signs by hand. After signing, the status becomes [d:Pending review|待审核].","Review it under [k:Staff profile review|员工资料审核] on Home. Once passed, the status is [d:Approved|审核通过]; if more information is needed it is returned and the status becomes [d:Returned|退回修改], so you can resend it from here."]', N'["链接过期了或家长没收到：点 [k:Resend|重新发送]，重新发送会作废旧链接；发错了可点 [k:Void link|作废链接]，也可以点 [k:Copy link|复制链接] 自己转发。","状态含义：未发送、待家长签字、链接已过期、待审核、退回修改、审核通过。","员工本人不再收到提醒，也不用自己填写，他们的页面只显示资料和审核进度。","新建员工时就要填生日和监护人信息（见「如何创建员工账号？」）。"]', N'["If the link has expired or the parent did not get it, tap [k:Resend|重新发送]; resending voids the old link. Tap [k:Void link|作废链接] if it went to the wrong person, or [k:Copy link|复制链接] to forward it yourself.","Statuses: Not sent, Awaiting guardian, Link expired, Pending review, Returned, Approved.","Staff members no longer get reminders and do not fill anything in themselves; their page only shows the details and review progress.","The date of birth and guardian details are already required when you create the staff member (see “How do I create a staff account?”)."]', NULL, NULL, NULL, NULL, N'["Attendance.Schedule.ViewStore","Attendance.Schedule.EditManagedStore"]',
         ISNULL((SELECT MAX([SortOrder]) FROM [dbo].[HelpFaqQuestion] WHERE [GroupGUID] = @G_minor_guardian_link AND [IsDeleted] = 0), -10) + 10,
         N'Published', NULL, @Now, 1,
         @Now, N'faq-sql-20261031', @Now, N'faq-sql-20261031', 0);
END

-- 4) 结果清单（应为：9 条 Version=2 + 2 条新增 Version=1）
SELECT g.[Tab], g.[Code] AS [Group], q.[Code], q.[SortOrder], q.[Status], q.[Version], q.[UpdatedBy]
FROM [dbo].[HelpFaqQuestion] q JOIN [dbo].[HelpFaqGroup] g ON g.[GroupGUID] = q.[GroupGUID]
WHERE q.[Code] IN (N'color-theme', N'app-update', N'pos-daily-close', N'order-history', N'product-sales', N'supplier-invoices', N'create-staff', N'promotions-manage', N'promotion-not-applied', N'online-receipt', N'minor-guardian-link')
ORDER BY g.[Tab], g.[SortOrder], q.[SortOrder];

COMMIT TRANSACTION;
