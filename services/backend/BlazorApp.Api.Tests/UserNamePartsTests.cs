using BlazorApp.Api.Services;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// UserNameParts：创建 / 更新 / 导入共用的姓名写入规则（纯内存，不依赖数据库）。
    /// </summary>
    public sealed class UserNamePartsTests
    {
        private static User ExistingUser(
            string? fullName = "Mary Jane Smith",
            string? given = "Mary",
            string? middle = "Jane",
            string? family = "Smith",
            string? nickname = "MJ") =>
            new()
            {
                FullName = fullName,
                GivenName = given,
                MiddleName = middle,
                FamilyName = family,
                Nickname = nickname,
            };

        // ---------------- 创建 ----------------

        [Fact]
        public void ApplyOnCreate_拆分字段任一非空_存各段并合成FullName()
        {
            var user = new User();

            var error = UserNameParts.ApplyOnCreate(
                user,
                new UserNameInput("ignored", " Mary ", "Jane", " Smith", "  MJ ")
            );

            Assert.Null(error);
            Assert.Equal("Mary", user.GivenName);
            Assert.Equal("Jane", user.MiddleName);
            Assert.Equal("Smith", user.FamilyName);
            Assert.Equal("MJ", user.Nickname);
            Assert.Equal("Mary Jane Smith", user.FullName);
        }

        [Fact]
        public void ApplyOnCreate_中文姓名按姓名无空格合成()
        {
            var user = new User();

            Assert.Null(UserNameParts.ApplyOnCreate(user, new UserNameInput(null, "小明", null, "王", null)));

            Assert.Equal("王小明", user.FullName);
        }

        [Fact]
        public void ApplyOnCreate_都没传拆分字段_沿用FullName原值且拆分字段为空()
        {
            var user = new User();

            Assert.Null(UserNameParts.ApplyOnCreate(user, new UserNameInput("  Legacy Name ", null, " ", "", null)));

            // 原样沿用：是否 Trim 由调用方按各自既有口径决定。
            Assert.Equal("  Legacy Name ", user.FullName);
            Assert.Null(user.GivenName);
            Assert.Null(user.MiddleName);
            Assert.Null(user.FamilyName);
            Assert.Null(user.Nickname);
        }

        [Fact]
        public void ApplyOnCreate_只有昵称_合成为空时退回FullName并Trim()
        {
            var user = new User();

            Assert.Null(UserNameParts.ApplyOnCreate(user, new UserNameInput("  Mary Smith ", null, null, null, "MJ")));

            Assert.Equal("Mary Smith", user.FullName);
            Assert.Equal("MJ", user.Nickname);
        }

        [Fact]
        public void ApplyOnCreate_合成后超过100字符_返回中文校验错误且不修改实体()
        {
            var user = new User { FullName = "untouched" };
            var forty = new string('a', 40);

            var error = UserNameParts.ApplyOnCreate(user, new UserNameInput(null, forty, forty, forty, null));

            Assert.NotNull(error);
            Assert.Contains("100", error);
            Assert.Equal("untouched", user.FullName);
            Assert.Null(user.GivenName);
        }

        [Fact]
        public void ApplyOnCreate_单段超过50字符_返回校验错误()
        {
            var user = new User();

            var error = UserNameParts.ApplyOnCreate(user, new UserNameInput(null, null, null, null, new string('n', 51)));

            Assert.NotNull(error);
            Assert.Contains("昵称", error);
            Assert.Contains("50", error);
        }

        // ---------------- 更新 ----------------

        [Fact]
        public void ApplyOnUpdate_旧客户端只发FullName且与现有三段一致_拆分字段全部保留()
        {
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput("Mary Jane Smith", null, null, null, null)));

            Assert.Equal("Mary Jane Smith", user.FullName);
            Assert.Equal("Mary", user.GivenName);
            Assert.Equal("Jane", user.MiddleName);
            Assert.Equal("Smith", user.FamilyName);
            Assert.Equal("MJ", user.Nickname);
        }

        [Fact]
        public void ApplyOnUpdate_旧客户端把FullName改成不一致的值_清空三段_昵称不动()
        {
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput("Someone Else", null, null, null, null)));

            Assert.Equal("Someone Else", user.FullName);
            Assert.Null(user.GivenName);
            Assert.Null(user.MiddleName);
            Assert.Null(user.FamilyName);
            Assert.Equal("MJ", user.Nickname);
        }

        [Fact]
        public void ApplyOnUpdate_旧客户端FullName只差空白_视为一致不清空()
        {
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput("  Mary   Jane Smith ", null, null, null, null)));

            Assert.Equal("Mary", user.GivenName);
            Assert.Equal("Smith", user.FamilyName);
        }

        [Fact]
        public void ApplyOnUpdate_仅昵称变化_FullName与三段都不动()
        {
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, null, null, null, "Emmy")));

            Assert.Equal("Emmy", user.Nickname);
            Assert.Equal("Mary Jane Smith", user.FullName);
            Assert.Equal("Mary", user.GivenName);
            Assert.Equal("Jane", user.MiddleName);
            Assert.Equal("Smith", user.FamilyName);
        }

        [Fact]
        public void ApplyOnUpdate_昵称与不变的FullName同发_昵称生效且三段保留()
        {
            // Web 表单保存时总是带 FullName、只带变化的新字段：昵称变化不应清空三段。
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput("Mary Jane Smith", null, null, null, "Emmy")));

            Assert.Equal("Emmy", user.Nickname);
            Assert.Equal("Mary Jane Smith", user.FullName);
            Assert.Equal("Mary", user.GivenName);
        }

        [Fact]
        public void ApplyOnUpdate_传部分字段_用请求值覆盖现有值后重算FullName()
        {
            var user = ExistingUser(fullName: "Mary Smith", middle: null);

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, null, "Jane", null, null)));
            Assert.Equal("Mary Jane Smith", user.FullName);

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, "Maria", null, null, null)));
            Assert.Equal("Maria Jane Smith", user.FullName);
            Assert.Equal("Smith", user.FamilyName);
            Assert.Equal("MJ", user.Nickname);
        }

        [Fact]
        public void ApplyOnUpdate_请求带拆分字段时以拆分字段为准_忽略同时携带的FullName()
        {
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput("Stale Name", "Maria", null, null, null)));

            Assert.Equal("Maria Jane Smith", user.FullName);
        }

        [Fact]
        public void ApplyOnUpdate_空白表示清空_null表示不改()
        {
            var user = ExistingUser();

            // 清空姓；名、中间名不动；昵称传空白清空。
            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, null, null, "  ", " ")));

            Assert.Null(user.FamilyName);
            Assert.Null(user.Nickname);
            Assert.Equal("Mary", user.GivenName);
            Assert.Equal("Jane", user.MiddleName);
            Assert.Equal("Mary Jane", user.FullName);
        }

        [Fact]
        public void ApplyOnUpdate_三段全部被清空_保留原FullName而不是清成空()
        {
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, "", "", "", null)));

            Assert.Null(user.GivenName);
            Assert.Null(user.MiddleName);
            Assert.Null(user.FamilyName);
            Assert.Equal("Mary Jane Smith", user.FullName);
            Assert.Equal("MJ", user.Nickname);
        }

        [Fact]
        public void ApplyOnUpdate_合成后超过100字符_返回校验错误且实体保持原样()
        {
            var user = ExistingUser();
            var forty = new string('a', 40);

            var error = UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, forty, forty, forty, "NewNick"));

            Assert.NotNull(error);
            Assert.Contains("100", error);
            Assert.Equal("Mary Jane Smith", user.FullName);
            Assert.Equal("Mary", user.GivenName);
            Assert.Equal("MJ", user.Nickname);
        }

        [Fact]
        public void ApplyOnUpdate_没有任何新字段且FullName为空_保持旧行为清空FullName并清掉失配三段()
        {
            // 旧行为：FullName 缺省会把全名写成 null；此时三段与 FullName 必然失配，同样被清空。
            var user = ExistingUser();

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, null, null, null, null)));

            Assert.Null(user.FullName);
            Assert.Null(user.GivenName);
            Assert.Equal("MJ", user.Nickname);
        }

        [Fact]
        public void ApplyOnUpdate_中文三段更新_按姓名无空格合成()
        {
            var user = ExistingUser(fullName: "王小明", given: "小明", middle: null, family: "王", nickname: null);

            Assert.Null(UserNameParts.ApplyOnUpdate(user, new UserNameInput(null, "小红", null, null, null)));

            Assert.Equal("王小红", user.FullName);
        }
    }
}
