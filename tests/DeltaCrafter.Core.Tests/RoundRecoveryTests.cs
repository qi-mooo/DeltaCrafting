using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L3;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class RoundRecoveryTests
{
    [Fact]
    public async Task Successful_round_does_not_restart()
    {
        int runs = 0, restarts = 0;
        await RoundRecovery.ExecuteAsync(_ => { runs++; return Task.CompletedTask; },
            (_, _) => { restarts++; return Task.CompletedTask; }, default);
        Assert.Equal(1, runs);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task Failed_round_restarts_before_running_again()
    {
        var calls = new List<string>();
        var failure = new StepFailedException("观察设施状态", "未获一致判定", "scene.png");
        await RoundRecovery.ExecuteAsync(_ =>
        {
            calls.Add("run");
            return calls.Count == 1 ? Task.FromException(failure) : Task.CompletedTask;
        }, (ex, _) =>
        {
            Assert.Same(failure, ex);
            calls.Add("restart");
            return Task.CompletedTask;
        }, default);
        Assert.Equal(new[] { "run", "restart", "run" }, calls);
    }

    [Fact]
    public async Task Second_failure_is_reported_without_another_restart()
    {
        int runs = 0, restarts = 0;
        var second = new StepFailedException("选中物品", "仍未选中", "second.png");
        var result = await Assert.ThrowsAsync<StepFailedException>(() => RoundRecovery.ExecuteAsync(
            _ => Task.FromException(++runs == 1 ? new StepFailedException("等待大厅", "超时") : second),
            (_, _) => { restarts++; return Task.CompletedTask; }, default));
        Assert.Same(second, result);
        Assert.Equal(2, runs);
        Assert.Equal(1, restarts);
    }

    [Theory]
    [InlineData("warehouse")]
    [InlineData("cancel")]
    [InlineData("config")]
    [InlineData("前置检查")]
    [InlineData("校验计划物品")]
    public async Task Nonrecoverable_failure_leaves_game_alone(string kind)
    {
        Exception failure = kind switch {
            "warehouse" => new WarehouseFullException("领取产物"),
            "cancel" => new OperationCanceledException(),
            "config" => new InvalidOperationException("游戏路径未配置"),
            _ => new StepFailedException(kind, "配置未完成"),
        };
        int runs = 0, restarts = 0;
        var result = await Record.ExceptionAsync(() => RoundRecovery.ExecuteAsync(
            _ => { runs++; return Task.FromException(failure); },
            (_, _) => { restarts++; return Task.CompletedTask; }, default));
        Assert.Same(failure, result);
        Assert.Equal(1, runs);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task Failed_shutdown_prevents_second_round()
    {
        int runs = 0;
        var failure = new StepFailedException("关闭游戏", "仍未退出");
        var result = await Assert.ThrowsAsync<StepFailedException>(() => RoundRecovery.ExecuteAsync(
            _ => { runs++; throw new StepFailedException("等待大厅", "超时"); },
            (_, _) => Task.FromException(failure), default));
        Assert.Same(failure, result);
        Assert.Equal(1, runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_prevents_restart_or_second_round(bool cancelDuringRestart)
    {
        using var cancel = new CancellationTokenSource();
        int runs = 0, restarts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoundRecovery.ExecuteAsync(token =>
        {
            Assert.Equal(cancel.Token, token);
            runs++;
            if (!cancelDuringRestart) cancel.Cancel();
            throw new StepFailedException("观察设施状态", "未读清");
        }, (_, token) =>
        {
            Assert.Equal(cancel.Token, token);
            restarts++;
            cancel.Cancel();
            return Task.CompletedTask;
        }, cancel.Token));
        Assert.Equal(1, runs);
        Assert.Equal(cancelDuringRestart ? 1 : 0, restarts);
    }
}
