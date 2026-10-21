using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look.Fauna;

/// <summary>The kinds of fish: little olive minnows with a dark stripe, golden rudd, and the odd big bronze carp.</summary>
public enum FishVariety { Minnow = 0, Golden = 1, Carp = 2 }

public sealed class Fish
{
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Unit direction the body points (it turns toward its velocity, never snaps).</summary>
    public Vector3 Heading = Vector3.Forward;
    public float LengthM;
    public FishVariety Variety;
    /// <summary>Where between the bed (0) and the surface (1) this fish likes to swim.</summary>
    public float DepthFraction;
    /// <summary>The tail beat's phase (radians) and a 0 to 1 pattern seed for the paint.</summary>
    public float Phase;
    public float PatternSeed;
    /// <summary>1 when it has just been startled, easing back to 0 as it settles.</summary>
    public float Fear;
    public Vector3 FleeFrom;
    public int School;
}

public sealed class School
{
    public required Pond Pond { get; init; }
    public required int[] Members { get; init; }
    public bool Solitary { get; init; }
    public Vector3 Goal;
    public float GoalTimeS;
}

/// <summary>
/// The fish of a room as living scenery (Run 2, Lane L; the founder: "small schools swim around the ponds and dart away when
/// you come close, no catching yet, bigger ponds get more fish"). Placement is deterministic from the room's id and its water;
/// the motion is a small boids model per school (keep together, swim the same way, keep apart, wander toward a goal), held
/// inside water deep enough to swim in and between the bed and the surface, turning where the water shallows. An avatar in or
/// at the water close by startles the fish near it: they dart away, the whole school takes fright, and they settle back over a
/// couple of seconds. Given the same steps and the same avatar positions the motion is the same, so tests can drive it; the game
/// runs it freely. Fish are not entities: not saved, not in the kernel, sight or map, and they collide with nothing.
/// </summary>
public sealed class Shoal
{
    // ---- where fish live (metres; the 10 cm avatar's world) ----
    /// <summary>A school's home needs water at least this deep. The founder (8 October): back to 6 cm once ponds are deep (C6's garage tarn is 23 cm).</summary>
    public const float HomeDepthM = 0.06f;
    /// <summary>Fish swim anywhere at least this deep, and turn back where it shallows.</summary>
    public const float SwimDepthM = 0.03f;
    /// <summary>A pond needs at least this much home water for any fish, and gets one school per AreaPerSchoolM2 of it.</summary>
    public const float MinHomeAreaM2 = 0.015f;
    public const float AreaPerSchoolM2 = 0.15f;
    public const int MaxSchoolsPerPond = 6;
    public const int MaxFish = 72;
    /// <summary>A big pond (this much water, and a deep middle this large) has one big fish of its own.</summary>
    public const float BigFishWetAreaM2 = 1.0f;
    public const float BigFishHomeAreaM2 = 0.25f;
    /// <summary>Small fish are 1.5 to 3 cm long; the big one about 4.5 cm.</summary>
    public const float SmallMinM = 0.015f, SmallMaxM = 0.03f, BigM = 0.045f;
    /// <summary>The gap a fish keeps from the bed and from the surface, as a fraction of its length (at least 4 mm).</summary>
    public const float ClearanceOfLength = 0.3f;
    /// <summary>However deep the water, fish swim within this much of its surface.</summary>
    public const float MaxBelowSurfaceM = 0.09f;

    // ---- how they move ----
    public const float CruiseMinMps = 0.012f, CruiseMaxMps = 0.05f, DartMps = 0.32f;
    /// <summary>An avatar this close (horizontally) startles a fish, when it is in the water or at its edge (feet no higher than ThreatAboveM over the surface).</summary>
    public const float FleeRadiusM = 0.16f;
    public const float ThreatAboveM = 0.07f;
    /// <summary>How long a fright takes to fade: the dart is over in about a third of it, the school has regrouped by the end.</summary>
    public const float SettleS = 1.6f;
    /// <summary>A school is loose: inside this distance of its middle a fish feels no pull back toward it.</summary>
    public const float LooseM = 0.035f;

    public List<Pond> Ponds { get; }
    public List<Fish> Fish { get; } = new();
    public List<School> Schools { get; } = new();
    public ulong Seed { get; }
    private ulong _state;

    public Shoal(string roomId, IEnumerable<Pond> ponds)
    {
        Ponds = ponds.ToList();
        Seed = Hash(roomId);
        foreach (var pond in Ponds) Populate(roomId, pond);
    }

    /// <summary>FNV-1a, 64 bits: a stable seed from text (never .NET's per-process string hash).</summary>
    public static ulong Hash(string text)
    {
        var hash = 14695981039346656037ul;
        foreach (var c in text) hash = (hash ^ c) * 1099511628211ul;
        return hash == 0 ? 1 : hash;
    }

    private float Next()
    {
        // xorshift64*: deterministic, cheap, and independent of the engine's generators.
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return ((_state * 2685821657736338717ul) >> 40) / (float)(1ul << 24);
    }

    private float Range(float a, float b) => a + (b - a) * Next();

    /// <summary>How many schools a pond with this much home water gets (bigger ponds get more fish).</summary>
    public static int SchoolsFor(float homeAreaM2) =>
        homeAreaM2 < MinHomeAreaM2 ? 0 : Math.Clamp((int)MathF.Round(homeAreaM2 / AreaPerSchoolM2), 1, MaxSchoolsPerPond);

    private void Populate(string roomId, Pond pond)
    {
        if (pond.Kind == WaterKind.Flowing && pond.MaxDepthM < HomeDepthM) return;
        var homes = pond.CellsDeeperThan(HomeDepthM).ToArray();
        var homeArea = homes.Length * pond.Cell * pond.Cell;
        var schools = SchoolsFor(homeArea);
        if (schools == 0) return;
        // Seeded by the room and by this water (where it lies and its level), so the same room always gets the same fish.
        _state = Hash($"{roomId}|{pond.Kind}|{MathF.Round(pond.Centroid.X, 2):0.00}|{MathF.Round(pond.Centroid.Z, 2):0.00}|{MathF.Round(pond.Level, 3):0.000}");
        var placed = new List<Vector3>();
        var anyGolden = false;
        for (var s = 0; s < schools && Fish.Count < MaxFish; s++)
        {
            // Spread the schools out: of a few deep candidates, the one farthest from the schools already placed.
            var home = Enumerable.Range(0, 8).Select(_ => pond.CellCentre(homes[(int)(Next() * homes.Length) % homes.Length]))
                .OrderByDescending(c => placed.Count == 0 ? pond.DepthAt(c.X, c.Z) : placed.Min(p => (p - c).Length())).First();
            placed.Add(home);
            var golden = Next() < 0.3f || (s == schools - 1 && schools >= 2 && !anyGolden);
            anyGolden |= golden;
            var size = (int)Range(5f, 9.99f);
            AddSchool(pond, home, Math.Min(size, MaxFish - Fish.Count), golden ? FishVariety.Golden : FishVariety.Minnow, solitary: false);
        }
        if (pond.WetAreaM2 >= BigFishWetAreaM2 && homeArea >= BigFishHomeAreaM2 && Fish.Count < MaxFish)
        {
            var deepest = homes.OrderByDescending(pond.DepthOfCell).First();
            AddSchool(pond, pond.CellCentre(deepest), 1, FishVariety.Carp, solitary: true);
        }
    }

    private void AddSchool(Pond pond, Vector3 home, int count, FishVariety variety, bool solitary)
    {
        var members = new int[count];
        var heading = new Vector3(Range(-1f, 1f), 0f, Range(-1f, 1f));
        heading = heading.LengthSquared() > 1e-4f ? heading.Normalized() : Vector3.Forward;
        for (var i = 0; i < count; i++)
        {
            var fish = new Fish
            {
                LengthM = variety == FishVariety.Carp ? BigM * Range(0.9f, 1.1f) : Range(SmallMinM, SmallMaxM),
                Variety = variety,
                DepthFraction = Range(0.4f, 0.85f),
                Phase = Range(0f, Mathf.Tau),
                PatternSeed = Next(),
                School = Schools.Count,
                Heading = heading,
            };
            // Loosely together around the home, each in water it can swim in.
            var at = home;
            for (var tries = 0; tries < 12; tries++)
            {
                var candidate = home + new Vector3(Range(-0.04f, 0.04f), 0f, Range(-0.04f, 0.04f));
                if (pond.DepthAt(candidate.X, candidate.Z) >= SwimDepthM) { at = candidate; break; }
            }
            fish.Position = new Vector3(at.X, TargetY(pond, fish, at.X, at.Z), at.Z);
            fish.Velocity = heading * Range(CruiseMinMps, CruiseMaxMps) * 0.6f;
            members[i] = Fish.Count;
            Fish.Add(fish);
        }
        Schools.Add(new School { Pond = pond, Members = members, Solitary = solitary, Goal = home, GoalTimeS = Range(2f, 5f) });
    }

    /// <summary>The gap a fish keeps from the bed and from the surface.</summary>
    public static float Clearance(Fish fish) => Math.Max(0.004f, fish.LengthM * ClearanceOfLength);

    /// <summary>The height band a fish may swim in at (x, z): (lowest, highest). In water too shallow for both gaps, the middle.</summary>
    public static (float Low, float High) Band(Pond pond, Fish fish, float x, float z)
    {
        var surface = pond.SurfaceAt(x, z);
        var bed = pond.BedAt(x, z);
        if (float.IsNaN(surface) || float.IsNaN(bed)) return (fish.Position.Y, fish.Position.Y);
        var gap = Clearance(fish);
        if (surface - bed < 2f * gap) return ((surface + bed) * 0.5f, (surface + bed) * 0.5f);
        return (bed + gap, surface - gap);
    }

    private static float TargetY(Pond pond, Fish fish, float x, float z)
    {
        var (low, high) = Band(pond, fish, x, z);
        // In deep water they keep to the upper few centimetres, where light reaches and a passer-by can see them.
        return Math.Max(low + (high - low) * fish.DepthFraction, high - MaxBelowSurfaceM);
    }

    /// <summary>
    /// Advance every fish by dt seconds. threats are the avatars' feet (the player and the companion); one in the water or at
    /// its edge within FleeRadiusM startles the fish near it.
    /// </summary>
    public void Step(float dt, IReadOnlyList<Vector3> threats)
    {
        dt = Math.Clamp(dt, 0f, 0.05f);
        if (dt <= 0f) return;
        foreach (var school in Schools) StepSchool(school, dt, threats);
    }

    private void StepSchool(School school, float dt, IReadOnlyList<Vector3> threats)
    {
        var pond = school.Pond;
        var members = school.Members;
        var centre = Vector3.Zero;
        var heading = Vector3.Zero;
        foreach (var m in members)
        {
            centre += Fish[m].Position;
            heading += Fish[m].Velocity;
        }
        centre /= members.Length;
        heading /= members.Length;

        // A new place to wander to, now and then, or when the school has arrived.
        school.GoalTimeS -= dt;
        var toGoal = school.Goal - centre;
        toGoal.Y = 0f;
        if (school.GoalTimeS <= 0f || toGoal.Length() < 0.03f) PickGoal(school, centre);

        // Startle: an avatar in or at the water, close to a fish, frightens it, and the fright runs through the school.
        var schoolFright = 0f;
        var frightFrom = Vector3.Zero;
        foreach (var m in members)
        {
            var fish = Fish[m];
            foreach (var threat in threats)
            {
                var surface = pond.SurfaceAt(fish.Position.X, fish.Position.Z);
                if (float.IsNaN(surface) || threat.Y > surface + ThreatAboveM || threat.Y < fish.Position.Y - 0.25f) continue;
                var away = new Vector2(fish.Position.X - threat.X, fish.Position.Z - threat.Z);
                if (away.Length() >= FleeRadiusM) continue;
                fish.Fear = 1f;
                fish.FleeFrom = threat;
                schoolFright = 1f;
                frightFrom = threat;
            }
        }
        if (schoolFright > 0f && !school.Solitary)
            foreach (var m in members)
                if (Fish[m].Fear < 0.75f)
                {
                    Fish[m].Fear = 0.75f;
                    Fish[m].FleeFrom = frightFrom;
                }

        foreach (var m in members)
        {
            var fish = Fish[m];
            var p = fish.Position;
            var v = fish.Velocity;
            var accel = Vector3.Zero;
            var calm = 1f - fish.Fear;
            if (!school.Solitary)
            {
                // Keep loosely together (no pull inside a few centimetres of the school's middle, less in a fright), swim the
                // same way, and keep a couple of body lengths apart.
                var toCentre = centre - p;
                toCentre.Y *= 0.3f;
                var out_ = toCentre.Length();
                if (out_ > LooseM) accel += toCentre / out_ * (out_ - LooseM) * 2.2f * (0.3f + 0.7f * calm);
                accel += (heading - v) * 1.0f * calm;
                foreach (var o in members)
                {
                    if (o == m) continue;
                    var apart = p - Fish[o].Position;
                    var distance = apart.Length();
                    var personal = 2.2f * Math.Max(fish.LengthM, Fish[o].LengthM);
                    if (distance < personal && distance > 1e-5f) accel += apart / distance * (personal - distance) / personal * 0.9f;
                }
            }
            // Wander toward the school's goal, with a little restlessness of each fish's own.
            var goal = school.Goal - p;
            goal.Y = 0f;
            if (goal.LengthSquared() > 1e-8f) accel += goal.Normalized() * (school.Solitary ? 0.05f : 0.08f) * calm;
            accel += new Vector3(Range(-1f, 1f), 0f, Range(-1f, 1f)) * 0.06f;
            // Dart away from what frightened it.
            if (fish.Fear > 0.01f)
            {
                var away = p - fish.FleeFrom;
                away.Y = 0f;
                away = away.LengthSquared() > 1e-8f ? away.Normalized() : (v.LengthSquared() > 1e-8f ? new Vector3(v.X, 0f, v.Z).Normalized() : Vector3.Right);
                var desired = away * DartMps * fish.Fear;
                accel += (desired - new Vector3(v.X, 0f, v.Z)) * 9f * fish.Fear;
            }
            // Turn at the edges: look ahead along the way it swims; where that water is too shallow, steer toward deeper.
            var flat = new Vector3(v.X, 0f, v.Z);
            var speed = flat.Length();
            if (speed > 1e-5f)
            {
                var lookahead = 0.03f + speed * 0.3f;
                var ahead = p + flat / speed * lookahead;
                if (pond.DepthAt(ahead.X, ahead.Z) < SwimDepthM + 0.004f)
                {
                    accel += pond.DeeperAt(p.X, p.Z) * (0.6f + speed * 6f);
                }
            }
            // Between the bed and the surface, each at the depth it likes.
            var targetY = TargetY(pond, fish, p.X, p.Z);
            accel.Y += (targetY - p.Y) * 6f - v.Y * 3f;

            v += accel * dt;
            // Speed: cruising, up to a dart when frightened; never quite still.
            flat = new Vector3(v.X, 0f, v.Z);
            speed = flat.Length();
            var cruiseMax = school.Solitary ? CruiseMaxMps * 0.75f : CruiseMaxMps;
            var top = Mathf.Lerp(cruiseMax, school.Solitary ? DartMps * 0.8f : DartMps, fish.Fear);
            if (speed > top) flat *= top / speed;
            else if (speed < CruiseMinMps) flat = (speed > 1e-6f ? flat / speed : Flat(fish.Heading)) * CruiseMinMps;
            v = new Vector3(flat.X, Math.Clamp(v.Y, -0.03f, 0.03f), flat.Z);

            // Move, never out of the water: a step into water too shallow slides along the edge or turns back.
            var next = p + v * dt;
            if (pond.DepthAt(next.X, next.Z) < SwimDepthM)
            {
                if (pond.DepthAt(next.X, p.Z) >= SwimDepthM) { next.Z = p.Z; v.Z = -v.Z * 0.3f; }
                else if (pond.DepthAt(p.X, next.Z) >= SwimDepthM) { next.X = p.X; v.X = -v.X * 0.3f; }
                else
                {
                    next.X = p.X;
                    next.Z = p.Z;
                    var deeper = pond.DeeperAt(p.X, p.Z);
                    v = new Vector3(deeper.X, 0f, deeper.Z) * Math.Max(CruiseMinMps, flat.Length() * 0.5f) + new Vector3(0f, v.Y, 0f);
                }
            }
            var (low, high) = Band(pond, fish, next.X, next.Z);
            next.Y = Math.Clamp(next.Y, low, high);
            fish.Position = next;
            fish.Velocity = v;

            // The body turns toward where it swims; the tail beats faster the faster it goes.
            var want = Flat(v) + new Vector3(0f, Math.Clamp(v.Y / Math.Max(speed, 0.01f), -0.4f, 0.4f), 0f);
            if (want.LengthSquared() > 1e-8f) fish.Heading = fish.Heading.Lerp(want.Normalized(), 1f - MathF.Exp(-dt * 7f)).Normalized();
            var bodyLengthsPerSecond = flat.Length() / fish.LengthM;
            fish.Phase = (fish.Phase + dt * Mathf.Tau * (2.2f + 1.1f * bodyLengthsPerSecond)) % Mathf.Tau;
            fish.Fear = Math.Max(0f, fish.Fear - dt / SettleS);
        }
    }

    private static Vector3 Flat(Vector3 v)
    {
        var f = new Vector3(v.X, 0f, v.Z);
        return f.LengthSquared() > 1e-10f ? f.Normalized() : Vector3.Forward;
    }

    private void PickGoal(School school, Vector3 centre)
    {
        var pond = school.Pond;
        school.GoalTimeS = Range(3f, 7f);
        for (var tries = 0; tries < 16; tries++)
        {
            var reach = school.Solitary ? 0.5f : 0.3f;
            var x = centre.X + Range(-reach, reach);
            var z = centre.Z + Range(-reach, reach);
            if (pond.DepthAt(x, z) >= HomeDepthM)
            {
                school.Goal = new Vector3(x, centre.Y, z);
                return;
            }
        }
        // Nowhere deep enough near by: go back toward the deepest water there is.
        var homes = pond.CellsDeeperThan(HomeDepthM).ToArray();
        if (homes.Length > 0) school.Goal = pond.CellCentre(homes[(int)(Next() * homes.Length) % homes.Length]);
    }

    /// <summary>Fill a MultiMesh buffer (12 floats of transform and 4 of custom data per fish): pose, tail phase, how hard it swims, kind, pattern.</summary>
    public void WriteBuffer(float[] buffer)
    {
        for (var i = 0; i < Fish.Count; i++)
        {
            var fish = Fish[i];
            var forward = fish.Heading.LengthSquared() > 1e-8f ? fish.Heading.Normalized() : Vector3.Forward;
            // The fish mesh points along -Z; a little bank into its turns would be nice, but upright reads best at this size.
            var basis = Basis.LookingAt(forward, Vector3.Up).Scaled(Vector3.One * fish.LengthM);
            var o = i * 16;
            buffer[o + 0] = basis.Column0.X; buffer[o + 1] = basis.Column1.X; buffer[o + 2] = basis.Column2.X; buffer[o + 3] = fish.Position.X;
            buffer[o + 4] = basis.Column0.Y; buffer[o + 5] = basis.Column1.Y; buffer[o + 6] = basis.Column2.Y; buffer[o + 7] = fish.Position.Y;
            buffer[o + 8] = basis.Column0.Z; buffer[o + 9] = basis.Column1.Z; buffer[o + 10] = basis.Column2.Z; buffer[o + 11] = fish.Position.Z;
            var effort = Math.Clamp(new Vector2(fish.Velocity.X, fish.Velocity.Z).Length() / DartMps, 0f, 1f);
            buffer[o + 12] = fish.Phase;
            buffer[o + 13] = 0.35f + 0.65f * effort;
            buffer[o + 14] = (float)fish.Variety;
            buffer[o + 15] = fish.PatternSeed;
        }
    }
}
