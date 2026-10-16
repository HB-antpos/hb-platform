using BlazorApp.Shared.Helper;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>PersonNameFormatter：全名合成（中英文/混合/空白/CJK 扩展字符）与短名取值顺序。</summary>
    public sealed class PersonNameFormatterTests
    {
        [Theory]
        [InlineData("Mary", "Jane", "Smith", "Mary Jane Smith")]
        [InlineData("Mary", null, "Smith", "Mary Smith")]
        [InlineData("Mary", null, null, "Mary")]
        [InlineData(null, null, "Smith", "Smith")]
        [InlineData(null, "Jane", null, "Jane")]
        [InlineData("José", null, "Álvarez", "José Álvarez")]
        public void Compose_英文按名_中间名_姓用单个空格拼接并跳过空段(
            string? given, string? middle, string? family, string expected)
        {
            Assert.Equal(expected, PersonNameFormatter.Compose(given, middle, family));
        }

        [Theory]
        [InlineData("小明", null, "王", "王小明")]
        [InlineData("小", "大", "王", "王大小")]
        [InlineData(null, null, "王", "王")]
        [InlineData("小明", null, null, "小明")]
        [InlineData("민준", null, "김", "김민준")]
        [InlineData("たろう", null, "山田", "山田たろう")]
        public void Compose_含CJK时按姓_中间名_名无空格拼接(
            string? given, string? middle, string? family, string expected)
        {
            Assert.Equal(expected, PersonNameFormatter.Compose(given, middle, family));
        }

        [Theory]
        // 姓用汉字、名用拼音：任一段含 CJK 即整体走无空格路径，名内部的空格保持。
        [InlineData("Xiao Ming", null, "王", "王Xiao Ming")]
        [InlineData("Tom", null, "李", "李Tom")]
        // 名用汉字、姓用拼音：顺序仍是 姓 + 中间名 + 名。
        [InlineData("小明", null, "Wang", "Wang小明")]
        public void Compose_中英文混合同样无空格拼接(
            string? given, string? middle, string? family, string expected)
        {
            Assert.Equal(expected, PersonNameFormatter.Compose(given, middle, family));
        }

        [Theory]
        // CJK 统一表意文字扩展 A（U+3400）、兼容表意文字（U+F900）、扩展 B（代理对 U+20000）。
        [InlineData("㐀", null, "Lee", "Lee㐀")]
        [InlineData("豈", null, "Lee", "Lee豈")]
        [InlineData("𠀀", null, "Lee", "Lee𠀀")]
        public void Compose_CJK扩展与兼容字符也识别为CJK(
            string? given, string? middle, string? family, string expected)
        {
            Assert.Equal(expected, PersonNameFormatter.Compose(given, middle, family));
        }

        [Fact]
        public void Compose_各段Trim并折叠内部连续空白()
        {
            Assert.Equal(
                "Mary Ann van der Berg",
                PersonNameFormatter.Compose("  Mary   Ann ", " ", " van \t der   Berg ")
            );
            // 折叠同样作用于 CJK 路径里保留下来的非 CJK 段。
            Assert.Equal(
                "王Xiao Ming",
                PersonNameFormatter.Compose("  Xiao    Ming  ", null, "  王 ")
            );
        }

        [Theory]
        [InlineData(null, null, null)]
        [InlineData("", "", "")]
        [InlineData(" ", "\t", "\r\n")]
        public void Compose_全空返回null(string? given, string? middle, string? family)
        {
            Assert.Null(PersonNameFormatter.Compose(given, middle, family));
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("Mary", false)]
        [InlineData("José", false)]
        [InlineData("王", true)]
        [InlineData("Mary 王", true)]
        [InlineData("㐀", true)]
        public void ContainsCjk_按Unicode区块判断(string? value, bool expected)
        {
            Assert.Equal(expected, PersonNameFormatter.ContainsCjk(value));
        }

        [Fact]
        public void ShortName_按昵称_名_全名_用户名取第一个非空()
        {
            Assert.Equal("Ali", PersonNameFormatter.ShortName("Ali", "Alice", "Alice Smith", "alice01"));
            Assert.Equal("Alice", PersonNameFormatter.ShortName(null, "Alice", "Alice Smith", "alice01"));
            Assert.Equal("Alice", PersonNameFormatter.ShortName("  ", "Alice", "Alice Smith", "alice01"));
            Assert.Equal("Alice Smith", PersonNameFormatter.ShortName(null, "", "Alice Smith", "alice01"));
            Assert.Equal("alice01", PersonNameFormatter.ShortName(null, null, " ", "alice01"));
        }

        [Fact]
        public void ShortName_结果已Trim_全空返回null()
        {
            Assert.Equal("Ali", PersonNameFormatter.ShortName("  Ali  ", null, null, null));
            Assert.Null(PersonNameFormatter.ShortName(null, " ", "", "\t"));
            Assert.Null(PersonNameFormatter.ShortName(null, null, null, null));
        }
    }
}
