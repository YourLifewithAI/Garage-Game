using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The player's modes (docs/magic/README.md "Modes"): the vitals model and its store, world.set_mode through the command
/// path (the player's alone, replayed, transient), stamina for climbing and resting, and injury by the trusted hazard
/// hooks under Challenge and Live, never Creative.
/// </summary>
public partial class CommandHostTest
{
    private async Task TestPlayerModes()
    {
        TestVitalsModelAndStore();
        TestModeCommands();
        TestModeLandingCallbacks();
        await TestModeClimbingBody();
        await TestKnockoutOffTheFloor();
        _host.ConfigurePlayerVitals(new ModeTestStore());
    }

    /// <summary>
    /// The characters round's review (minor 4): a knockout's recovery waits for the tumble only where one plays, a body on the floor.
    /// The out-of-bounds net reports its fall while the body is still falling: no tumble, so the recovery at the spawn is at once,
    /// and the player is never frozen for the tumble's length with nothing to see.
    /// </summary>
    private async Task TestKnockoutOffTheFloor()
    {
        _host.ConfigurePlayerVitals(new ModeTestStore());
        Check(Ok(_host.PlayerMode("challenge")), "the knockout fixture plays in Challenge");
        var spawn = _room.SpawnFor("player").PositionM;
        var knockout = PlayerVitals.SafeLandingSpeedMps + (PlayerVitals.MaximumHealth + 1) / PlayerVitals.FallDamagePerMps;
        _player.KnockoutRecoveryDelayS = 2;
        try
        {
            _player.ReleaseControl();
            _player.GlobalPosition = spawn + new Vector3(0, 0.25f, 0);
            _player.Velocity = Vector3.Zero;
            _player.ResetVitalsObservation();
            await Frames(3);
            var falling = !_player.IsOnFloor();
            var recoveries = _host.Vitals.Recoveries;
            _player.FallObserved?.Invoke(knockout, false);
            Check(falling && _host.Vitals.Recoveries == recoveries + 1 && !_host.RecoveryPending && !_player.ControlHeld &&
                  _player.GlobalPosition.DistanceTo(spawn) < 0.05f,
                "a knockout reported while the body is still falling (the out-of-bounds net) recovers at the spawn at once: no tumble to wait for, control never held");
            // Standing again, past the recovery's grace: a knockout on the floor waits for its tumble, control held meanwhile.
            for (var i = 0; i < 120 && !_player.IsOnFloor(); i++) await Frames(1);
            _player.VitalsTick?.Invoke(1.1f, true);
            _player.FallObserved?.Invoke(knockout, false);
            Check(_player.IsOnFloor() && _host.RecoveryPending && _player.ControlHeld && _host.Vitals.Recoveries == recoveries + 1,
                "a knockout on the floor waits for the tumble, control held");
            _player.VitalsTick?.Invoke(2.01f, false);
            Check(!_host.RecoveryPending && _host.Vitals.Recoveries == recoveries + 2, "and recovers when the wait ends");
        }
        finally
        {
            _player.KnockoutRecoveryDelayS = 0;
            _player.ReleaseControl();
        }
    }

    private void TestVitalsModelAndStore()
    {
        var fresh = PlayerVitals.Fresh;
        var climbed = fresh.SpendClimb(3, 0.35f, out var tree);
        var exhausted = climbed.SpendClimb(5, 0.6f, out var mountain);
        var hanging = fresh.SpendClimb(5, 0, out var held);
        Check(tree && !mountain && held && exhausted.Stamina == 0 && hanging.Stamina < fresh.Stamina &&
            fresh.Health == 100 && fresh.Stamina == 0.45f && climbed.Rest(20, false).Stamina == climbed.MaxStamina,
            "starting stamina affords a 35 cm tree, cannot afford a mountain, drains while hanging and is capped when replenished");
        var earned = climbed.CompleteClimb(new string('a', 64));
        Check(earned.CompletedClimbs == 1 && Mathf.IsEqualApprox(earned.MaxStamina, 0.55f) && earned.Stamina == climbed.Stamina &&
            ReferenceEquals(earned, earned.CompleteClimb(new string('a', 64))),
            "a distinct climb raises capacity once, without refilling current energy");
        var defaultLandingSpeed = MathF.Sqrt(2 * 3.5f * 1.5f);
        var lowGravityLandingSpeed = MathF.Sqrt(2 * 0.6f * 1.5f);
        Check(Math.Abs(PlayerVitals.FallDamage(defaultLandingSpeed, false) - 96) < 0.1f &&
            PlayerVitals.FallDamage(PlayerVitals.SafeLandingSpeedMps, false) == 0,
            "default-gravity falls retain the 30 cm safe threshold and about 96 damage from 1.5 m");
        Check(PlayerVitals.FallDamage(lowGravityLandingSpeed, false) == 0 && PlayerVitals.FallDamage(0.5f, false) == 0 &&
            PlayerVitals.FallDamage(defaultLandingSpeed, true) == 0,
            "a 1.5 m low-gravity fall, slow glider descent and deep-water landing cause no injury");
        Check(new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f }.All(speed => PlayerVitals.FallDamage(speed, false) == 0) &&
            PlayerVitals.FallDamage(float.MaxValue, false) == PlayerVitals.ContractMaxHealth,
            "invalid landing speeds cause no injury and finite extreme speeds clamp damage to the contract's most health (absolute: a fall still knocks out 30 hearts)");
        Check(PlayerVitals.FallDamage(0.2f, false) == 0 && PlayerVitals.FallDamage(3, false) > 0 &&
            fresh.Damage(200).Health == 0 && fresh.Damage(200).Recovered().Health == 50 && fresh.Damage(200).Recovered().Recoveries == 1,
            "slow landings are safe; fast impacts injure, and recovery leaves half health with a counted return");
        TestVitalsStoreVersionTwo(earned);
    }

    /// <summary>
    /// The vitals store, version 2 (contracts/team.schema.json $defs/vitals_store): one document per mode under the store's root
    /// (a version 1 file's folder today, the team folder once the teams packet sets the path), written only for the modes the
    /// host stamped, strict on reading; version 1 read for a mode with no document yet and never written or removed; another
    /// session's change and a bad save kept, never overwritten.
    /// </summary>
    private void TestVitalsStoreVersionTwo(PlayerVitals earned)
    {
        var folder = ProjectSettings.GlobalizePath($"{TestRoot}/modes/{Guid.NewGuid():N}");
        var path = folder + "/player_modes_v1.json";
        var store = new LocalPlayerVitalsStore(path);
        Check(store.Load().Mode == "creative" && !Directory.Exists(folder), "a missing mode profile starts Creative without creating a save");
        var at = new DateTime(2026, 10, 14, 19, 30, 0, DateTimeKind.Utc);
        var profile = PlayerModesProfile.Fresh.Select("challenge").WithCurrent(earned.Damage(20)).Stamped(at);
        store.Save(profile);
        var file = store.ModePath("challenge");
        var saved = File.ReadAllBytes(file);
        var restored = new LocalPlayerVitalsStore(path).Load();
        Check(file.Replace('\\', '/').EndsWith("/modes/challenge/vitals.json", StringComparison.Ordinal) && !File.Exists(store.ModePath("live")) && !File.Exists(store.ModePath("creative")) &&
            restored.Mode == "challenge" && restored.Current.Health == 80 && restored.Current.CompletedClimbs == 1 && restored.Written("challenge") == at &&
            restored.For("live").CompletedClimbs == 0 && restored.For("live").Health == 100 && saved[0] == (byte)'{' && saved[^1] == (byte)'\n',
            "health, energy and earned traits survive reload in the mode's own document, stamped, and stay separate between Challenge and Live");
        var text = Encoding.UTF8.GetString(saved);
        Check(text.Contains("\"schema\":\"garage_game.player_modes\",\"version\":2,\"team_id\":\"local\",\"mode\":\"challenge\",\"written_utc\":\"2026-10-14T19:30:00.000Z\"", StringComparison.Ordinal) &&
            text.Contains("\"fractals\":{\"found\":0,\"unspent\":0,\"hearts\":0,\"bands\":0},\"pips\":0", StringComparison.Ordinal),
            "the document is the contract's: schema, version 2, team, mode, the time it was written, and the vitals with their Fractals and pool: " + text);
        var bad = new[]
        {
            "{", text.Replace("\"version\":2", "\"version\":99", StringComparison.Ordinal),
            text.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal),
            text.Replace("\"health\":80", "\"health\":101", StringComparison.Ordinal),
            text.Replace("\"recoveries\":0", "\"recoveries\":-1", StringComparison.Ordinal),
            text.Replace("\"mode\":\"challenge\"", "\"mode\":\"unknown\"", StringComparison.Ordinal),
            text.Replace("\"schema\":", "\"extra\":true,\"schema\":", StringComparison.Ordinal),
            text.Replace("\"pips\":0", "\"pips\":41", StringComparison.Ordinal),
            text.Replace("\"pips\":0", "\"pips\":1.5", StringComparison.Ordinal),
            text.Replace("\"unspent\":0", "\"unspent\":1", StringComparison.Ordinal),
            text.Replace("\"hearts\":0", "\"hearts\":30", StringComparison.Ordinal),
            text.Replace("\"team_id\":\"local\"", "\"team_id\":\"Local\"", StringComparison.Ordinal),
            text.Replace("\"written_utc\":\"2026-10-14T19:30:00.000Z\"", "\"written_utc\":\"yesterday\"", StringComparison.Ordinal),
        };
        Check(bad.All(value => VitalsRefused(() => LocalPlayerVitalsStore.ParseMode(Encoding.UTF8.GetBytes(value)))) &&
            VitalsRefused(() => LocalPlayerVitalsStore.ParseMode(new byte[] { 0xff })) &&
            VitalsRefused(() => PlayerVitals.Restore(100, 8, Array.Empty<string>(), 0)),
            "mode documents reject malformed, older and newer saves, excessive vitals, unknown modes, unknown fields, a pool past 40 or in fractions, Fractals in hand never found, 31 hearts, a bad team or time and invalid UTF-8");
        // The contract's own examples: its vitals store reads, and each invalid vitals document is refused.
        var examples = ProjectSettings.GlobalizePath("res://") + "../contracts/examples/";
        var example = LocalPlayerVitalsStore.ParseMode(File.ReadAllBytes(examples + "teams/example_team/modes/challenge/vitals.json"));
        var invalid = Directory.GetFiles(examples + "teams_invalid", "vitals_*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Check(example is { TeamId: "example_team", Mode: "challenge", Vitals: { Health: 85, MaxHealth: 120, Pips: 6, Fractals: { Found: 13, Unspent: 3, Hearts: 1, Bands: 1 } } } &&
            invalid.Length == 4 && invalid.All(f => VitalsRefused(() => LocalPlayerVitalsStore.ParseMode(File.ReadAllBytes(f)))),
            $"the contract's vitals store example reads, and its {invalid.Length} invalid vitals documents are refused");
        var second = new LocalPlayerVitalsStore(path);
        second.Load();
        store.Save(profile.WithCurrent(earned.Damage(21)).Stamped(at.AddSeconds(1)));
        var latest = File.ReadAllBytes(file);
        Check(VitalsRefused(() => second.Save(profile.Stamped(at.AddSeconds(2)))) && File.ReadAllBytes(file).SequenceEqual(latest),
            "another mode session cannot silently overwrite newly saved traits");
        File.WriteAllText(file, "{\"version\":99}", new UTF8Encoding(false));
        var corrupt = new LocalPlayerVitalsStore(path);
        var invalidBytes = File.ReadAllBytes(file);
        Check(VitalsRefused(() => corrupt.Load()) && VitalsRefused(() => corrupt.Save(PlayerModesProfile.Fresh.Stamped(at))) &&
            File.ReadAllBytes(file).SequenceEqual(invalidBytes), "a bad mode save is preserved instead of silently resetting physical progress");
        // Version 1 (the three modes in one file) is read for the modes without a document, and kept as it was.
        var legacyFolder = ProjectSettings.GlobalizePath($"{TestRoot}/modes/{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyFolder);
        var legacyPath = legacyFolder + "/player_modes_v1.json";
        var climb = new string('b', 64);
        var legacyText = "{\"schema\":\"enfractal.player_modes\",\"version\":1,\"mode\":\"live\",\"modes\":{" +
            "\"creative\":{\"health\":100,\"stamina\":0.45,\"challenges\":[],\"recoveries\":0}," +
            $"\"challenge\":{{\"health\":73.5,\"stamina\":0.3,\"challenges\":[\"{climb}\"],\"recoveries\":2}}," +
            "\"live\":{\"health\":40,\"stamina\":0.1,\"challenges\":[],\"recoveries\":1}}}\n";
        File.WriteAllText(legacyPath, legacyText, new UTF8Encoding(false));
        var legacyStore = new LocalPlayerVitalsStore(legacyPath);
        var migrated = legacyStore.Load();
        Check(migrated.Mode == "live" && migrated.Current.Health == 40 && migrated.Current.Pips == PlayerVitals.UnsetPool && migrated.Written("live") == null &&
            migrated.For("challenge") is { Health: 73.5f, CompletedClimbs: 1, Recoveries: 2 } && Math.Abs(migrated.For("challenge").MaxStamina - 0.55f) < 1e-6,
            "a version 1 save reads as it was: its mode, each mode's body, an unset pool and no time written");
        legacyStore.Save(migrated.Stamped(at));
        var reread = new LocalPlayerVitalsStore(legacyPath).Load();
        Check(File.Exists(legacyStore.ModePath("live")) && !File.Exists(legacyStore.ModePath("challenge")) && File.ReadAllText(legacyPath) == legacyText &&
            reread.Mode == "live" && reread.For("challenge").Health == 73.5f && reread.Current.Health == 40 && reread.Written("live") == at,
            "saving writes the played mode's version 2 document only; the other modes still read from version 1, which is never written or removed");
        // The teams packet's layout: <team>/modes/<mode>/vitals.json names the team folder as the root and the team.
        var teamPath = ProjectSettings.GlobalizePath($"{TestRoot}/modes/{Guid.NewGuid():N}/teams/mira_team/modes/challenge/vitals.json");
        var team = new LocalPlayerVitalsStore(teamPath);
        team.Load();
        team.Save(PlayerModesProfile.Fresh.Select("challenge").Stamped(at));
        Check(File.Exists(teamPath) && File.ReadAllText(teamPath).Contains("\"team_id\":\"mira_team\"", StringComparison.Ordinal) &&
            team.ModePath("live").Replace('\\', '/').EndsWith("/teams/mira_team/modes/live/vitals.json", StringComparison.Ordinal),
            "in a team's folder the store writes <team>/modes/<mode>/vitals.json with the team's id (the contract's save layout)");
    }

    private void TestModeCommands()
    {
        var store = new ModeTestStore();
        _host.ConfigurePlayerVitals(store);
        _host.ApplyPlayerDamage(70, PlayerDamageSource.Fire);
        Check(_host.Mode == "creative" && _host.Vitals.Health == 100 && _player.ClimbBudget?.Invoke(10, 8) == true && _host.Vitals.Stamina == 0.45f,
            "Creative ignores fire injury and climbing energy costs");
        var choose = Command("mode-challenge", "world.set_mode", new JsonObject { ["mode"] = "challenge" });
        var preview = (JsonObject)choose.DeepClone();
        preview["preview"] = true;
        Check(Ok(Send(preview, Player)) && _host.Mode == "creative" && store.Saves == 0,
            "previewing a mode choice neither changes mode nor saves it");
        Check(Code(Send(choose, Companion)) == "permission_denied" && _host.Mode == "creative", "the companion cannot choose the player's consequences");
        Check(Ok(Send(choose, Player)) && _host.Mode == "challenge", "the player's mode command enters Challenge through the shared command path");
        _player.FallObserved?.Invoke(0.5f, false);
        _player.FallObserved?.Invoke(3.24f, true);
        _player.FallObserved?.Invoke(float.NaN, false);
        Check(_host.Vitals.Health == 100, "the trusted fall callback ignores slow, deep-water and non-finite landing speeds in Challenge");
        var saves = store.Saves;
        Check(Send(choose, Player)["replayed"]!.GetValue<bool>() && store.Saves == saves, "retrying a successful mode command replays without a second mode change");
        _host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        Check(_host.Vitals.Health == 80 && _host.LastDamageSource == PlayerDamageSource.Fire,
            "the same Fire hook damages the player in Challenge");
        Check(_player.ClimbBudget?.Invoke(1, 0.12f) == true && _host.Vitals.Stamina < _host.Vitals.MaxStamina,
            "the bound body spends the host's authoritative climbing energy");
        var energy = _host.Vitals.Stamina;
        _player.VitalsTick?.Invoke(1, false);
        Check(_host.Vitals.Stamina == energy, "energy does not regenerate while the body hangs or is airborne");
        _player.VitalsTick?.Invoke(1, true);
        Check(_host.Vitals.Stamina > energy, "safe ground replenishes the body's energy");
        _player.ClimbCompleted?.Invoke(new Vector3(0, 0, 0), new Vector3(0.2f, 0.35f, 0.2f));
        _player.ClimbCompleted?.Invoke(new Vector3(0, 0, 0), new Vector3(0.2f, 0.35f, 0.2f));
        Check(_host.Vitals.CompletedClimbs == 1 && Mathf.IsEqualApprox(_host.Vitals.MaxStamina, 0.55f),
            "a meaningful body-observed ledge challenge grants capacity once per mode");
        Check(Ok(_host.PlayerMode("live")) && _host.Vitals.CompletedClimbs == 0 && _host.Vitals.Health == 100,
            "Live begins with its own physical traits");
        _host.ApplyPlayerDamage(15, PlayerDamageSource.Critter);
        _host.ApplyPlayerDamage(10, PlayerDamageSource.Player);
        Check(_host.Vitals.Health == 75, "trusted future critter and player-attack hooks share Live injury rules");
        Check(Ok(_host.PlayerMode("challenge")) && _host.Vitals.Health == 80 && _host.Vitals.CompletedClimbs == 1,
            "returning to Challenge keeps its injury and earned capacity instead of importing or refilling Live's state");
        var status = Query("player.status", new JsonObject(), Companion);
        Check(Ok(status) && status["data"]!["mode"]!.GetValue<string>() == "challenge" &&
            status["data"]!["health"]!.GetValue<float>() == 80 && status["data"]!["consequences"]!.GetValue<bool>(),
            "player.status reports actual mode and vitals through the read-only companion surface");
        _host.ApplyPlayerDamage(100, PlayerDamageSource.Fall);
        Check(_host.Vitals.Health == 50 && _host.Vitals.Recoveries == 1 && _host.Vitals.CompletedClimbs == 1 &&
            _player.GlobalPosition.DistanceTo(_room.SpawnFor("player").PositionM) < 0.05f,
            "zero health recovers at the spawn with half health, preserving learned traits");
        // The characters round: a knockout fall waits for its tumble before the recovery, with control held meanwhile.
        _player.VitalsTick?.Invoke(1.1f, false);
        _player.KnockoutRecoveryDelayS = 2;
        _host.ApplyPlayerDamage(100, PlayerDamageSource.Fall);
        Check(_host.Vitals.Health == 0 && _host.Vitals.Recoveries == 1 && _host.RecoveryPending && _player.ControlHeld,
            "a knockout fall waits for the tumble: no recovery yet, control held");
        _host.ApplyPlayerDamage(30, PlayerDamageSource.Fire);
        _player.VitalsTick?.Invoke(1, false);
        Check(_host.Vitals.Recoveries == 1 && _host.RecoveryPending, "nothing hurts and nothing recovers while the tumble plays");
        _player.VitalsTick?.Invoke(1.01f, false);
        Check(!_host.RecoveryPending && _host.Vitals.Recoveries == 2 && _host.Vitals.Health == 50 &&
            _player.GlobalPosition.DistanceTo(_room.SpawnFor("player").PositionM) < 0.05f,
            "when the tumble's wait ends, the recovery at the spawn follows");
        _player.KnockoutRecoveryDelayS = 0;
        _player.ReleaseControl();
        _host.ApplyPlayerDamage(100, PlayerDamageSource.Fire);
        Check(_host.Vitals.Recoveries == 2, "the recovery grace still follows a delayed recovery");
        _player.VitalsTick?.Invoke(1.1f, false);
        var before = _host.Mode;
        store.FailSave = true;
        Check(Code(_host.PlayerMode("creative")) == "not_ready" && _host.Mode == before && _host.VitalsNotice.Length > 0,
            "a mode change whose profile cannot save is refused without changing the mode");
        _host.ConfigurePlayerVitals(new ModeTestStore { FailLoad = true });
        Check(Code(_host.PlayerMode("live")) == "not_ready" && _host.VitalsNotice.Length > 0,
            "a corrupt profile prevents mode changes from overwriting its saved state");
        _host.ConfigurePlayerVitals(new ModeTestStore());
    }

    /// <summary>The trusted body's speed observation follows the same consequences in Challenge and Live.</summary>
    private void TestModeLandingCallbacks()
    {
        foreach (var mode in PlayerModesProfile.Modes)
        {
            _host.ConfigurePlayerVitals(new ModeTestStore());
            Check(Ok(_host.PlayerMode(mode)), $"the landing callback fixture selects {mode}");
            _player.FallObserved?.Invoke(0.5f, false);
            _player.FallObserved?.Invoke(0.6f, false);
            _player.FallObserved?.Invoke(3.24f, true);
            _player.FallObserved?.Invoke(float.PositiveInfinity, false);
            Check(_host.Vitals.Health == 100, $"slow glider, floaty-air, deep-water and invalid impacts leave {mode} health intact");
            _player.FallObserved?.Invoke(MathF.Sqrt(2 * 3.5f * 1.5f), false);
            var expected = mode == "creative" ? 100 : 4;
            Check(Math.Abs(_host.Vitals.Health - expected) < 0.1f,
                $"a default-gravity 1.5 m impact follows {mode} rules through the trusted callback (health {_host.Vitals.Health:0.00})");
        }
        _host.ConfigurePlayerVitals(new ModeTestStore());
    }

    private async Task TestModeClimbingBody()
    {
        var fixture = new Node3D { Name = "ModeBodyFixture" };
        AddChild(fixture);
        var origin = new Vector3(20, 0, 20);
        void Box(Vector3 centre, Vector3 size)
        {
            var solid = new StaticBody3D { Position = origin + centre, CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
            solid.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            fixture.AddChild(solid);
        }
        Box(new Vector3(0, -0.05f, 0), new Vector3(4, 0.1f, 4));
        Box(new Vector3(0, 0.175f, -0.25f), new Vector3(0.6f, 0.35f, 0.3f));
        Box(new Vector3(1, 0.5f, -0.25f), new Vector3(0.6f, 1, 0.3f));
        var body = new SmallPlayerController { Name = "ModeBody", ReadKeyboard = false, Position = origin + new Vector3(0, 0.003f, 0.25f) };
        fixture.AddChild(body);
        var vitals = PlayerVitals.Fresh;
        body.ClimbBudget = (seconds, rise) => { vitals = vitals.SpendClimb(seconds, rise, out var enough); return enough; };
        var completed = 0;
        body.ClimbCompleted = (start, finish) => { if (finish.Y - start.Y >= 0.25f) completed++; };
        await Frames(10);
        body.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 420 && completed == 0; i++) await Frames(1);
        body.SetControlInput(Vector2.Zero);
        Check(completed == 1 && body.GlobalPosition.Y >= 0.34f && vitals.Stamina > 0,
            $"the real body can finish a tree-sized 35 cm climb on its initial stamina (height {body.GlobalPosition.Y:0.000}, energy {vitals.Stamina:0.000})");
        body.TryTeleportTo(origin + new Vector3(1, 0.003f, 0.25f));
        vitals = PlayerVitals.Fresh;
        var releases = body.ClimbReleases;
        var highest = body.GlobalPosition.Y;
        body.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 420 && body.ClimbReleases == releases; i++)
        { await Frames(1); highest = Math.Max(highest, body.GlobalPosition.Y); }
        body.SetControlInput(Vector2.Zero);
        Check(body.ClimbReleases > releases && !body.IsClimbing && vitals.Stamina == 0 && highest < 0.6f && completed == 1,
            $"the real body lets go when exhausted before reaching a 1 m cliff (highest {highest:0.000})");
        var observedFall = 0f;
        body.FallObserved = (landingSpeedMps, deep) => observedFall = Math.Max(observedFall, PlayerVitals.FallDamage(landingSpeedMps, deep));
        body.GlobalPosition = origin + new Vector3(-1, 1, 0);
        body.Velocity = Vector3.Zero;
        body.ResetVitalsObservation();
        await Frames(100);
        Check(observedFall > 0 && body.IsOnFloor(), "a real high landing reports injury to the host after physics resolves the fall");
        fixture.QueueFree();
        await Frames(2);
    }

    private static bool VitalsRefused(Action action)
    {
        try { action(); return false; }
        catch (PlayerVitalsException) { return true; }
    }

    private sealed class ModeTestStore : IPlayerVitalsStore
    {
        public int Saves { get; private set; }
        public bool FailSave { get; set; }
        public bool FailLoad { get; set; }
        private PlayerModesProfile _state;
        public ModeTestStore(PlayerModesProfile? profile = null) => _state = profile ?? PlayerModesProfile.Fresh;
        public PlayerModesProfile Load() => FailLoad ? throw new PlayerVitalsException("The test profile cannot load.") : _state;
        public void Save(PlayerModesProfile profile)
        {
            if (FailSave) throw new PlayerVitalsException("The test profile cannot save.");
            _state = profile;
            Saves++;
        }
    }
}
