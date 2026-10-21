using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// A fire teaches nothing by burning (M11): Fire's expertise come from the camp (CommandHostCamps: the first rest teaches Fire, the first
/// firepit lit Campfire) and Fire's behaviours (CommandHostFireKinds), so a fire lit, burning, put out or burnt out teaches nothing; each
/// committed cast is counted toward Fire's hints (the shared rules, since Fire's list is final), and only that is saved.
/// </summary>
public partial class NatureTest
{
    private async Task TestExpertise()
    {
        await NewWorld(SavePath("expertise-fire"));
        var store = new CountingProgressStore();
        _host.ConfigureGlowProgress(store);
        _host.LearnFireForTests();
        var preview = Send(Command(NextId("preview"), "nature.cast", Cast("fire", new Vector3(0.2f, 0.01f, -0.6f), 0.05, 6), preview: true), Player);
        var lit = Fire(Player, new Vector3(0.2f, 0.01f, -0.6f), 0.05, 6, out _);
        var steps = 0;
        while (_host.Nature.Fire != null && steps++ < 200) Steps(1);
        var companion = Fire(Companion, new Vector3(0.5f, 0.01f, -0.6f), 0.05, 6, out _);
        Steps(2);
        var stopped = _host.PlayerGoal("stop");
        Check(Ok(preview) && Ok(lit) && Ok(companion) && Ok(stopped) && _host.Nature.Cells.Values.Any(c => c.Ash > 0) && _host.Nature.Fire == null,
            "a fire was previewed, lit and burnt out by the player, and lit by the Gubble and put out");
        Check(_host.GlowProgress.Others.Keys.SequenceEqual(new[] { "fire" }) && _host.ExpertiseCount() == 1 && _host.GlowProgress.Of("fire")!.SuccessfulCasts == 2 &&
              _host.GlowProgress.Of("fire")!.DeliveredHints.Count == 0 && store.Saves == 2 && _host.GlowProgressNotice.Length == 0,
            "and none of it taught anything beyond Fire itself (the test's rest): a fire teaches nothing by burning; the two committed casts are counted toward Fire's hints");
    }

    private sealed class CountingProgressStore : IGlowProgressStore
    {
        public int Saves { get; private set; }
        public GlowProgress Load() => GlowProgress.Fresh;
        public void Save(GlowProgress state) => Saves++;
    }
}
