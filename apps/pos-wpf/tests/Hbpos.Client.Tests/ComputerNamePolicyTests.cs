using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>电脑名规则：取名结果会拼进管理员脚本，必须只含字母/数字/短横线，且和旧名字不同（否则硬件码不变）。</summary>
public sealed class ComputerNamePolicyTests
{
    [Theory]
    [InlineData("HB-2010-A7K3", true)]
    [InlineData("a", true)]
    [InlineData("ABCDEFGHIJKLMNO", true)]       // 恰好 15 个字符
    [InlineData("ABCDEFGHIJKLMNOP", false)]     // 16 个字符
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-HB", false)]                   // 短横线开头
    [InlineData("HB-", false)]                   // 短横线结尾
    [InlineData("12345", false)]                 // 纯数字，Windows 不接受
    [InlineData("HB 2010", false)]               // 空格
    [InlineData("HB_2010", false)]               // 下划线
    [InlineData("HB.2010", false)]               // 点
    [InlineData("HB'2010", false)]               // 引号：绝不能进入管理员脚本
    [InlineData("HB;calc", false)]
    [InlineData("电脑01", false)]                // 非 ASCII
    public void IsValid_only_accepts_netbios_safe_names(string? name, bool expected)
    {
        Assert.Equal(expected, ComputerNamePolicy.IsValid(name));
    }

    [Fact]
    public void Generate_builds_store_prefixed_name_from_the_random_alphabet()
    {
        var name = ComputerNamePolicy.Generate("2010", "BF-202508261629", _ => 0);

        // 随机字母表第 0 位是 '2'。
        Assert.Equal("HB-2010-2222", name);
    }

    [Theory]
    [InlineData(null, "HB-2222")]
    [InlineData("", "HB-2222")]
    [InlineData("  ", "HB-2222")]
    [InlineData("20 10!", "HB-2010-2222")]       // 非字母数字被去掉
    [InlineData("abcdefghij", "HB-ABCDEF-2222")] // 店号最多 6 位，并转大写
    public void Generate_sanitizes_the_store_code(string? storeCode, string expected)
    {
        Assert.Equal(expected, ComputerNamePolicy.Generate(storeCode, "OLD", _ => 0));
    }

    [Fact]
    public void Generate_never_returns_the_current_name()
    {
        var calls = 0;
        // 前 4 次取第 0 位（得到 HB-2010-2222 = 当前名），之后取第 1 位。
        var name = ComputerNamePolicy.Generate("2010", "hb-2010-2222", _ => calls++ < 4 ? 0 : 1);

        Assert.Equal("HB-2010-3333", name);
    }

    [Fact]
    public void Generate_always_yields_a_valid_name_within_the_length_limit()
    {
        for (var i = 0; i < 200; i++)
        {
            var name = ComputerNamePolicy.Generate("ABCDEFGHIJ", "OLD");

            Assert.True(ComputerNamePolicy.IsValid(name), name);
            Assert.True(name.Length <= ComputerNamePolicy.MaxLength, name);
        }
    }

    [Fact]
    public void Generate_does_not_use_look_alike_characters()
    {
        var seen = new HashSet<char>();
        for (var i = 0; i < 500; i++)
        {
            foreach (var ch in ComputerNamePolicy.Generate(null, "OLD")["HB-".Length..])
            {
                seen.Add(ch);
            }
        }

        Assert.DoesNotContain(seen, ch => ch is '0' or '1' or 'O' or 'I' or 'L');
    }
}
