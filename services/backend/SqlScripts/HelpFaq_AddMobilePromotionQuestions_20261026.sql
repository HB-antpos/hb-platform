-- 使用帮助 FAQ：移动端「商品与订货」新增 2 条促销问答（promotions-manage、promotion-not-applied）。
-- 背景：迁移 20261025.001-help-faq 只在两张表为空时灌种子，生产库已灌过，新问答需单独插入。
-- 内容与 Data/SeedData/help-faq-seed.json 一致；按 Code 判断是否已存在，可重复执行；只 INSERT，不改已有行。
-- 回滚：DELETE FROM dbo.HelpFaqQuestion WHERE Code IN (N'promotions-manage', N'promotion-not-applied');（公开接口有 5 分钟内存缓存）
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;

DECLARE @GroupGUID nvarchar(50) = (SELECT [GroupGUID] FROM [dbo].[HelpFaqGroup] WHERE [Tab] = N'mobile' AND [Code] = N'products' AND [IsDeleted] = 0);
IF @GroupGUID IS NULL
    THROW 51990, N'找不到移动端 products 分组（HelpFaqGroup），中止。', 1;

DECLARE @Now datetime2 = SYSUTCDATETIME();

IF NOT EXISTS (SELECT 1 FROM [dbo].[HelpFaqQuestion] WHERE [Code] = N'promotions-manage')
    INSERT INTO [dbo].[HelpFaqQuestion]
        ([QuestionGUID], [GroupGUID], [Code], [TitleZh], [TitleEn], [WhereZh], [WhereEn],
         [StepsZh], [StepsEn], [TipsZh], [TipsEn], [RulesZh], [RulesEn], [RelatedZh], [RelatedEn],
         [Permissions], [SortOrder], [Status], [DraftJson], [PublishedAt], [Version],
         [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
    VALUES
        (CONVERT(nvarchar(50), NEWID()), @GroupGUID, N'promotions-manage', N'如何新建、修改本店促销？', N'How do I create or change a store promotion?', N'工作台 · 运营与报表 · [k:Promotions|促销]；或 商品查询 · [k:Promotions for this item|本商品促销]', N'Home · Operations & reports · [k:Promotions|促销]; or Product lookup · [k:Promotions for this item|本商品促销]',
         N'["打开 [k:Promotions|促销]，右上角选分店（只列你管理的分店）；用 [tab:Active|生效中]、[tab:Upcoming|未开始]、[tab:Disabled|已停用]、[tab:Ended|已结束]、[tab:All|全部] 切换查看。","点右下 [p:New promotion|新建促销]，填 [k:Promotion name|促销名称]、[k:Buy quantity|买满数量]（至少 2 件）和 [k:Bundle price|组合总价]，再选有效期。","在 [k:Products|参与商品] 点 [k:Scan|扫码添加] 或 [k:Search|搜索] 加入商品；每行会显示本店零售价和促销后的均价。","点 [p:Save|保存]。本店独享的促销可以直接用卡片右上角的开关启用或停用；要停掉正在进行的促销，进编辑页点 [k:End now|提前结束]。","在商品查询页扫到商品后，也可以在 [k:Promotions for this item|本商品促销] 里点 [k:New promotion for this item|为本商品新建促销]：填数量和总价，点 [p:Save & enable|保存并启用] 或 [k:Save as disabled|存为停用]。"]',
         N'["Open [k:Promotions|促销] and pick a store at the top right (only stores you manage are listed). Switch between [tab:Active|生效中], [tab:Upcoming|未开始], [tab:Disabled|已停用], [tab:Ended|已结束] and [tab:All|全部].","Tap [p:New promotion|新建促销] at the bottom right. Enter the [k:Promotion name|促销名称], [k:Buy quantity|买满数量] (at least 2) and [k:Bundle price|组合总价], then set the valid period.","Under [k:Products|参与商品], tap [k:Scan|扫码添加] or [k:Search|搜索] to add items. Each row shows the store retail price and the average price in the deal.","Tap [p:Save|保存]. A store-only promotion can be turned on or off with the switch on its card. To stop a running promotion, open it and tap [k:End now|提前结束].","From product lookup, after scanning an item you can also tap [k:New promotion for this item|为本商品新建促销] under [k:Promotions for this item|本商品促销]. Enter the quantity and price, then tap [p:Save & enable|保存并启用] or [k:Save as disabled|存为停用]."]',
         N'["App 里新建的促销只属于当前分店（本店独享）。覆盖多家店的促销只能查看；要调整时点 [k:Copy to this store|复制到本店]，复制出的本店版默认停用，改好后再启用。","只有一个商品时，组合价必须比「零售价 × 数量」便宜才能保存；多个商品混搭时，买满也没有优惠的商品会标红提醒，但仍可保存。","[k:End now|提前结束] 只把结束时间改为现在，不会删除促销；App 里不能删除促销。","保存时如果和排他促销时间重叠会弹出提示，可以选 [k:End on …|改到…结束] 或 [k:Save anyway|仍然保存]。"]',
         N'["Promotions created in the app belong to the current store only. Multi-store promotions are view-only; to adjust one, tap [k:Copy to this store|复制到本店]. The copy starts disabled, so edit it and then enable it.","With a single item, the bundle price must be lower than retail × quantity before you can save. In a mixed deal, items that get no saving are highlighted in red, but you can still save.","[k:End now|提前结束] only sets the end time to now; it does not delete the promotion. Promotions can''t be deleted in the app.","If the dates overlap an exclusive promotion, you''ll be warned when saving. Choose [k:End on …|改到…结束] or [k:Save anyway|仍然保存]."]',
         NULL, NULL, NULL, NULL,
         N'["Promotions.View","Promotions.Edit"]', 30, N'Published', NULL, @Now, 1,
         @Now, N'faq-sql-20261026', @Now, N'faq-sql-20261026', 0);

IF NOT EXISTS (SELECT 1 FROM [dbo].[HelpFaqQuestion] WHERE [Code] = N'promotion-not-applied')
    INSERT INTO [dbo].[HelpFaqQuestion]
        ([QuestionGUID], [GroupGUID], [Code], [TitleZh], [TitleEn], [WhereZh], [WhereEn],
         [StepsZh], [StepsEn], [TipsZh], [TipsEn], [RulesZh], [RulesEn], [RelatedZh], [RelatedEn],
         [Permissions], [SortOrder], [Status], [DraftJson], [PublishedAt], [Version],
         [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
    VALUES
        (CONVERT(nvarchar(50), NEWID()), @GroupGUID, N'promotion-not-applied', N'收银没有按促销价计算怎么办？', N'Why isn''t the POS applying a promotion?', N'工作台 · 运营与报表 · [k:Promotions|促销]', N'Home · Operations & reports · [k:Promotions|促销]',
         N'["打开 [k:Promotions|促销]，点搜索框右侧的扫码按钮扫这个商品，只看包含它的促销。","看状态：[tab:Upcoming|未开始] 要到开始时间才生效；[tab:Disabled|已停用] 需要打开卡片开关；[tab:Ended|已结束] 需要重新新建。","核对数量：顾客买的件数要达到 [k:Buy quantity|买满数量]，收银才会按组合总价计算。","如果同一时段还有排他促销，收银只会按优先级高的那一条计价。"]',
         N'["Open [k:Promotions|促销], tap the scan button next to the search box and scan the item to see only promotions that include it.","Check the status: [tab:Upcoming|未开始] starts at its start time; [tab:Disabled|已停用] needs its switch turned on; [tab:Ended|已结束] needs a new promotion.","Check the quantity: the customer must buy at least the [k:Buy quantity|买满数量] for the bundle price to apply.","If an exclusive promotion runs at the same time, the POS applies only the one with higher priority."]',
         N'["促销必须绑定分店才会被收银计算；后台新建促销时分店为必选项。"]',
         N'["A promotion is only applied at the POS when it is assigned to a store. Choosing stores is required when creating one in the back office."]',
         NULL, NULL, NULL, NULL,
         N'["Promotions.View"]', 40, N'Published', NULL, @Now, 1,
         @Now, N'faq-sql-20261026', @Now, N'faq-sql-20261026', 0);

SELECT q.[Code], q.[SortOrder], q.[Status] FROM [dbo].[HelpFaqQuestion] q WHERE q.[GroupGUID] = @GroupGUID ORDER BY q.[SortOrder];

COMMIT TRANSACTION;
