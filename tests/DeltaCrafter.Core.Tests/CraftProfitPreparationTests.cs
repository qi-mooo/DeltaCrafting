using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class CraftProfitPreparationTests
{
    private static AutomationCoordinator Coordinator(Func<FacilityPlan, CancellationToken, Task> prepare)
    {
        var clock = new SystemClock();
        return new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, () => new AppSettings(), CraftPlanConfig.CreateDefault,
            null!, clock, new LoggerConfiguration().CreateLogger(),
            new SteamActivityGuard(new SteamActivityClient(), clock), prepare);
    }

    [Theory]
    [InlineData(CraftMode.Custom, 0)]
    [InlineData(CraftMode.TotalProfit, 0)]
    [InlineData(CraftMode.HourlyProfit, 1)]
    public async Task Only_hourly_mode_queries_at_craft_preparation(CraftMode mode, int expected)
    {
        int calls = 0;
        using var coordinator = Coordinator((plan, _) =>
        {
            calls++;
            plan.ItemName = "最新推荐";
            return Task.CompletedTask;
        });
        Assert.Equal(0, calls); // Construction must not prewarm market data.
        var plan = new FacilityPlan { Key = FacilityKey.Workbench, Mode = mode, ItemName = "原选择" };
        await coordinator.PrepareCraftAsync(plan, default);
        Assert.Equal(expected, calls);
        Assert.Equal(expected == 1 ? "最新推荐" : "原选择", plan.ItemName);
    }

    [Fact]
    public async Task Query_failure_stops_craft_preparation_and_preserves_selection()
    {
        using var coordinator = Coordinator((_, _) => throw new InvalidOperationException("行情不可用"));
        var plan = new FacilityPlan { Mode = CraftMode.HourlyProfit, ItemName = "原选择" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareCraftAsync(plan, default));
        Assert.Equal("原选择", plan.ItemName);
    }

    [Fact]
    public async Task Cancelled_run_does_not_query_market()
    {
        using var coordinator = Coordinator((_, _) => throw new Exception("Must not query"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.PrepareCraftAsync(
            new FacilityPlan { Mode = CraftMode.HourlyProfit }, new CancellationToken(true)));
    }
}
