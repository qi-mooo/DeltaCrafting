using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class AssistantGameSessionTests
{
    private static readonly DateTime Launch = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private static readonly GameProcessIdentity Game = new(123, Launch.AddSeconds(10), @"C:\Games\Delta Force\Game.exe");

    [Fact]
    public void Existing_or_unrequested_process_is_not_owned()
    {
        var session = new AssistantGameSession();
        Assert.False(session.Record(Game, null));
        Assert.False(session.Record(Game, Game.StartedUtc.AddTicks(1)));
        Assert.False(session.Record(null, Launch));
        Assert.False(session.IsRunning(_ => Game));
    }

    [Fact]
    public void Only_the_same_process_remains_owned_until_it_exits()
    {
        var session = new AssistantGameSession();
        Assert.True(session.Record(Game, Launch));
        Assert.True(session.IsRunning(_ => Game with { ExecutablePath = Game.ExecutablePath.ToUpperInvariant() }));
        Assert.False(session.IsRunning(_ => null));
        Assert.False(session.IsRunning(_ => Game)); // Ownership cannot return just because a PID is reused.
    }

    [Fact]
    public void Restarted_game_or_changed_executable_cannot_inherit_ownership()
    {
        var session = new AssistantGameSession();
        session.Record(Game, Launch);
        Assert.False(session.IsRunning(_ => Game with { StartedUtc = Game.StartedUtc.AddSeconds(1) }));
        session.Record(Game, Launch);
        Assert.False(session.IsRunning(_ => Game with { ExecutablePath = @"C:\Steam\streaming_client.exe" }));
    }
}
