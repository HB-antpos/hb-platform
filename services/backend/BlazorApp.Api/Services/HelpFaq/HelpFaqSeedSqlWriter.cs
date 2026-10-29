using System.Data;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;

namespace BlazorApp.Api.Services.HelpFaq;

/// <summary>
/// 把 <see cref="HelpFaqSeedPlan"/> 写进 SQL Server：单事务、仅在两张表都为空时灌入，重复执行安全。
/// 由 SchemaMigrationRuntime.ApplyHelpFaqAsync 在建表成功后调用；迁移本身持有应用级迁移锁，
/// 这里再对两表加 TABLOCKX + HOLDLOCK 判空，即便锁被绕过也不会重复灌入。
/// </summary>
internal static class HelpFaqSeedSqlWriter
{
    // 判空必须与插入同在一个事务内并持有表锁，否则并发迁移会各自判空后都插入。
    private const string IsAnyRowSql = """
        SELECT CASE WHEN EXISTS (SELECT 1 FROM [dbo].[HelpFaqQuestion] WITH (TABLOCKX, HOLDLOCK))
                      OR EXISTS (SELECT 1 FROM [dbo].[HelpFaqGroup] WITH (TABLOCKX, HOLDLOCK))
                    THEN 1 ELSE 0 END;
        """;

    private const string InsertGroupSql = """
        INSERT INTO [dbo].[HelpFaqGroup]
            ([GroupGUID], [Tab], [Code], [TitleZh], [TitleEn], [SortOrder],
             [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        VALUES
            (@GroupGUID, @Tab, @Code, @TitleZh, @TitleEn, @SortOrder,
             @CreatedAt, @CreatedBy, @UpdatedAt, @UpdatedBy, 0);
        """;

    private const string InsertQuestionSql = """
        INSERT INTO [dbo].[HelpFaqQuestion]
            ([QuestionGUID], [GroupGUID], [Code], [TitleZh], [TitleEn], [WhereZh], [WhereEn],
             [StepsZh], [StepsEn], [TipsZh], [TipsEn], [RulesZh], [RulesEn], [RelatedZh], [RelatedEn],
             [Permissions], [SortOrder], [Status], [DraftJson], [PublishedAt], [Version],
             [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        VALUES
            (@QuestionGUID, @GroupGUID, @Code, @TitleZh, @TitleEn, @WhereZh, @WhereEn,
             @StepsZh, @StepsEn, @TipsZh, @TipsEn, @RulesZh, @RulesEn, @RelatedZh, @RelatedEn,
             @Permissions, @SortOrder, @Status, NULL, @PublishedAt, @Version,
             @CreatedAt, @CreatedBy, @UpdatedAt, @UpdatedBy, 0);
        """;

    /// <summary>返回 true 表示本次真的灌入了种子；false 表示表非空而跳过。</summary>
    public static async Task<bool> SeedIfEmptyAsync(
        string connectionString,
        HelpFaqSeedPlan plan,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var probe = connection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText = IsAnyRowSql;
                probe.CommandTimeout = commandTimeoutSeconds;
                var hasRows = Convert.ToInt32(
                    await probe.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture
                );
                if (hasRows != 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            foreach (var group in plan.Groups)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = InsertGroupSql;
                command.CommandTimeout = commandTimeoutSeconds;
                Add(command, "@GroupGUID", SqlDbType.NVarChar, 50, group.GroupGUID);
                Add(command, "@Tab", SqlDbType.NVarChar, 16, group.Tab);
                Add(command, "@Code", SqlDbType.NVarChar, 64, group.Code);
                Add(command, "@TitleZh", SqlDbType.NVarChar, 200, group.TitleZh);
                Add(command, "@TitleEn", SqlDbType.NVarChar, 200, group.TitleEn);
                Add(command, "@SortOrder", SqlDbType.Int, 0, group.SortOrder);
                Add(command, "@CreatedAt", SqlDbType.DateTime2, 0, group.CreatedAt);
                Add(command, "@CreatedBy", SqlDbType.NVarChar, 100, group.CreatedBy);
                Add(command, "@UpdatedAt", SqlDbType.DateTime2, 0, group.UpdatedAt);
                Add(command, "@UpdatedBy", SqlDbType.NVarChar, 100, group.UpdatedBy);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var question in plan.Questions)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = InsertQuestionSql;
                command.CommandTimeout = commandTimeoutSeconds;
                Add(command, "@QuestionGUID", SqlDbType.NVarChar, 50, question.QuestionGUID);
                Add(command, "@GroupGUID", SqlDbType.NVarChar, 50, question.GroupGUID);
                Add(command, "@Code", SqlDbType.NVarChar, 96, question.Code);
                Add(command, "@TitleZh", SqlDbType.NVarChar, 400, question.TitleZh);
                Add(command, "@TitleEn", SqlDbType.NVarChar, 400, question.TitleEn);
                Add(command, "@WhereZh", SqlDbType.NVarChar, 400, question.WhereZh);
                Add(command, "@WhereEn", SqlDbType.NVarChar, 400, question.WhereEn);
                Add(command, "@StepsZh", SqlDbType.NVarChar, -1, question.StepsZh);
                Add(command, "@StepsEn", SqlDbType.NVarChar, -1, question.StepsEn);
                Add(command, "@TipsZh", SqlDbType.NVarChar, -1, question.TipsZh);
                Add(command, "@TipsEn", SqlDbType.NVarChar, -1, question.TipsEn);
                Add(command, "@RulesZh", SqlDbType.NVarChar, -1, question.RulesZh);
                Add(command, "@RulesEn", SqlDbType.NVarChar, -1, question.RulesEn);
                Add(command, "@RelatedZh", SqlDbType.NVarChar, -1, question.RelatedZh);
                Add(command, "@RelatedEn", SqlDbType.NVarChar, -1, question.RelatedEn);
                Add(command, "@Permissions", SqlDbType.NVarChar, -1, question.Permissions);
                Add(command, "@SortOrder", SqlDbType.Int, 0, question.SortOrder);
                Add(command, "@Status", SqlDbType.NVarChar, 16, question.Status);
                Add(command, "@PublishedAt", SqlDbType.DateTime2, 0, question.PublishedAt);
                Add(command, "@Version", SqlDbType.Int, 0, question.Version);
                Add(command, "@CreatedAt", SqlDbType.DateTime2, 0, question.CreatedAt);
                Add(command, "@CreatedBy", SqlDbType.NVarChar, 100, question.CreatedBy);
                Add(command, "@UpdatedAt", SqlDbType.DateTime2, 0, question.UpdatedAt);
                Add(command, "@UpdatedBy", SqlDbType.NVarChar, 100, question.UpdatedBy);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            // 任何一行失败整体回滚，绝不留下半截种子。
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void Add(SqlCommand command, string name, SqlDbType type, int size, object? value)
    {
        var parameter = type is SqlDbType.NVarChar
            ? command.Parameters.Add(name, type, size)
            : command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
    }
}
