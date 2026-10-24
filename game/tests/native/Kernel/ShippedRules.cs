using System;
using System.Linq;
using GarageGame.Native;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The rules pack the kernel suites load as the shipped one: RoomWorld.DefaultRulesVersion, or the version a run names with
/// "-- --rules-version=N", so a packet can run every suite under the pack the default is about to move to before the
/// integrator moves it (the mechanics round's M1: storybook_wild v5).
/// </summary>
internal static class ShippedRules
{
    private static bool _said;

    public static int Version
    {
        get
        {
            var named = Godot.OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--rules-version=", StringComparison.Ordinal));
            if (named == null || !int.TryParse(named["--rules-version=".Length..], out var version) || version < 1) return RoomWorld.DefaultRulesVersion;
            if (!_said) Godot.GD.Print($"KERNEL_RULES the suites load storybook_wild v{version} as the shipped pack (RoomWorld's default is v{RoomWorld.DefaultRulesVersion})");
            _said = true;
            return version;
        }
    }
}
