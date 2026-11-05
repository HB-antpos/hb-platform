-- 使用帮助 FAQ：未成年员工监护人门槛由「未满 18 岁」改为「未满 16 岁」（2026-11-03 业务决定），同步 2 条问答的文案。
-- 背景：迁移 20261025.001-help-faq 只在两张表为空时灌种子，生产库已灌过；种子文件（help-faq-seed.json）改了不会自动进库，需执行本脚本。
-- 内容与 Data/SeedData/help-faq-seed.json、apps/web/.../faqSnapshot.json 一致：只把下列短语按原样替换，不重写整段 JSON。
--   create-staff（移动端 · 如何创建员工账号？）：StepsZh「未满 18 岁」→「未满 16 岁」；StepsEn「under 18」→「under 16」；TipsEn「under-18」→「under-16」。
--   minor-guardian-link（员工与权限 · 如何给未成年员工的家长发签字链接？）：TitleEn / StepsEn「under-18」→「under-16」；
--                        WhereZh / WhereEn / StepsZh / StepsEn「Under-18 employment compliance」→「Under-16 employment compliance」；StepsZh「未满 18 岁」→「未满 16 岁」。
-- 安全措施：
--   1) 先把 2 条旧行完整备份到 dbo.HelpFaqQuestion_Bak_20261103（已存在则不覆盖，保留最初备份）；
--   2) 只改仍是 faq-sql-20261031 写入、未被后台改过的行（后台改过的行 UpdatedBy 不同，会 THROW 并整体回滚，避免盖掉人工修改）；
--   3) REPLACE 显式用区分大小写的排序规则，避免默认不区分大小写把「Under-18」换成小写「under-16」；
--   4) 按短语替换，可重复执行：第二次执行时文本已无旧短语，UPDATE 不会改坏内容。
-- 回滚（公开接口有 5 分钟内存缓存，回滚后同样要等几分钟）：
--   UPDATE q SET q.TitleEn=b.TitleEn, q.WhereZh=b.WhereZh, q.WhereEn=b.WhereEn, q.StepsZh=b.StepsZh, q.StepsEn=b.StepsEn,
--                q.TipsZh=b.TipsZh, q.TipsEn=b.TipsEn, q.Version=b.Version, q.UpdatedAt=b.UpdatedAt, q.UpdatedBy=b.UpdatedBy
--     FROM dbo.HelpFaqQuestion q JOIN dbo.HelpFaqQuestion_Bak_20261103 b ON b.QuestionGUID = q.QuestionGUID;
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;

DECLARE @Now datetime2 = SYSUTCDATETIME();

-- 0) 前置检查：2 条问答必须都存在、未删除，且仍是 10-31 脚本写入的版本
IF (SELECT COUNT(*) FROM [dbo].[HelpFaqQuestion] WHERE [Code] IN (N'create-staff', N'minor-guardian-link') AND [IsDeleted] = 0) <> 2
    THROW 51993, N'待更新的 FAQ 问答不全（应有 create-staff、minor-guardian-link 共 2 条），中止。请先核对 HelpFaqQuestion。', 1;
IF EXISTS (SELECT 1 FROM [dbo].[HelpFaqQuestion] WHERE [Code] IN (N'create-staff', N'minor-guardian-link') AND [IsDeleted] = 0 AND ISNULL([UpdatedBy], N'') <> N'faq-sql-20261031')
    THROW 51994, N'create-staff / minor-guardian-link 已被后台改过（UpdatedBy 不是 faq-sql-20261031），为避免盖掉人工修改中止；请人工核对后在后台改文案。', 1;

-- 1) 备份旧行（已存在则保留最初备份）
IF OBJECT_ID(N'dbo.HelpFaqQuestion_Bak_20261103', N'U') IS NULL
    SELECT * INTO [dbo].[HelpFaqQuestion_Bak_20261103] FROM [dbo].[HelpFaqQuestion] WHERE [Code] IN (N'create-staff', N'minor-guardian-link') AND [IsDeleted] = 0;

-- 2) 按短语替换（区分大小写）
UPDATE [dbo].[HelpFaqQuestion]
    SET [TitleEn] = REPLACE([TitleEn] COLLATE Latin1_General_CS_AS, N'under-18', N'under-16'),
        [WhereZh] = REPLACE([WhereZh] COLLATE Latin1_General_CS_AS, N'Under-18', N'Under-16'),
        [WhereEn] = REPLACE([WhereEn] COLLATE Latin1_General_CS_AS, N'Under-18', N'Under-16'),
        [StepsZh] = REPLACE(REPLACE([StepsZh] COLLATE Latin1_General_CS_AS, N'未满 18 岁', N'未满 16 岁'), N'Under-18', N'Under-16'),
        [StepsEn] = REPLACE(REPLACE(REPLACE([StepsEn] COLLATE Latin1_General_CS_AS, N'under 18', N'under 16'), N'under-18', N'under-16'), N'Under-18', N'Under-16'),
        [TipsZh]  = REPLACE([TipsZh] COLLATE Latin1_General_CS_AS, N'未满 18 岁', N'未满 16 岁'),
        [TipsEn]  = REPLACE([TipsEn] COLLATE Latin1_General_CS_AS, N'under-18', N'under-16'),
        [Version] = [Version] + 1, [UpdatedAt] = @Now, [UpdatedBy] = N'faq-sql-20261103'
    WHERE [Code] IN (N'create-staff', N'minor-guardian-link') AND [IsDeleted] = 0;

-- 3) 校验：两条里不应再出现旧短语（出现则回滚）
IF EXISTS (
    SELECT 1 FROM [dbo].[HelpFaqQuestion]
    WHERE [Code] IN (N'create-staff', N'minor-guardian-link') AND [IsDeleted] = 0
      AND (ISNULL([TitleEn], N'') + ISNULL([WhereZh], N'') + ISNULL([WhereEn], N'') + ISNULL([StepsZh], N'') + ISNULL([StepsEn], N'') + ISNULL([TipsZh], N'') + ISNULL([TipsEn], N'')) COLLATE Latin1_General_CS_AS
          LIKE N'%' + N'18' + N'%')
    THROW 51995, N'替换后文本里仍含「18」，请人工核对，已回滚。', 1;

-- 4) 结果清单（应为 2 条，Version 各 +1，UpdatedBy = faq-sql-20261103）
SELECT g.[Tab], g.[Code] AS [Group], q.[Code], q.[TitleEn], q.[Version], q.[UpdatedBy]
FROM [dbo].[HelpFaqQuestion] q JOIN [dbo].[HelpFaqGroup] g ON g.[GroupGUID] = q.[GroupGUID]
WHERE q.[Code] IN (N'create-staff', N'minor-guardian-link')
ORDER BY g.[Tab], g.[SortOrder], q.[SortOrder];

COMMIT TRANSACTION;
