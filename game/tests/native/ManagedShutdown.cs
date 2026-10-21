using System;
using Godot;

namespace GarageGame.Tests;

/// <summary>
/// For GDScript suites run with --script that create C# objects (invention_runtime_smoke.gd's two bodies): collect the managed
/// wrappers of what the suite freed before it quits. At exit the engine shuts its C# runtime down whether or not .NET has yet
/// finalized the wrappers of freed bodies and of the resources they held (their capsule, contacts, materials and meshes). Those
/// still alive are reported as "Leaked unsafe reference", and the debug engine then aborts on csharp_script.cpp's
/// script_bindings check (exit 0xC000001D) after every check has passed: at random, and more often on a busy machine.
/// </summary>
public partial class ManagedShutdown : RefCounted
{
    /// <summary>A full collection, the finalizers, and another collection: what nothing reaches lets go of its Godot objects.</summary>
    public void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
