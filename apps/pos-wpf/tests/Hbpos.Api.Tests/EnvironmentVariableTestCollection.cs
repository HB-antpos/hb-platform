namespace Hbpos.Api.Tests;

/// <summary>
/// 修改进程级环境变量的测试必须串行执行：环境变量对整个测试进程可见，并行时会被其他用例读到。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableTestCollection
{
    public const string Name = "EnvironmentVariableTests";
}
