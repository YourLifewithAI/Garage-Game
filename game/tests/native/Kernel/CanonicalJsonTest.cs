using Godot;
using System.Linq;
using System.Text;
using GarageGame.Native.Kernel;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The canonical JSON golden fixture reproduced by C#, plus a cross-check that GDScript's kernel
/// (scripts/creation_json.gd) writes the same bytes for the same parsed value.
/// </summary>
public partial class CanonicalJsonTest : Node
{
    private const string Fixture = "res://tests/fixtures/kernel/";
    private int _checks;
    private int _failures;

    public override void _Ready()
    {
        try
        {
            var input = FileAccess.GetFileAsBytes(Fixture + "canonical_input.json");
            var expected = FileAccess.GetFileAsBytes(Fixture + "canonical_expected.json");
            var digest = FileAccess.GetFileAsString(Fixture + "canonical_expected.sha256").Trim();
            Check(input.Length > 0 && expected.Length > 0 && digest.Length == 64, "golden fixture files are present");
            using (var document = CanonicalJson.Parse(input))
            {
                var produced = CanonicalJson.Bytes(document.RootElement);
                Check(produced.SequenceEqual(expected), "C# canonical bytes equal canonical_expected.json byte for byte");
                Check(CanonicalJson.Sha256Hex(produced) == digest, "C# SHA-256 equals canonical_expected.sha256");
            }
            var gdscript = GD.Load<GDScript>("res://scripts/creation_json.gd");
            var parsed = gdscript.Call("parse", Encoding.UTF8.GetString(input)).AsGodotDictionary();
            var gdBytes = Encoding.UTF8.GetBytes(gdscript.Call("canonical", parsed["value"]).AsString());
            Check(parsed["ok"].AsBool() && gdBytes.SequenceEqual(expected), "GDScript, called from C#, writes the same bytes");

            foreach (var (value, text) in new (double, string)[]
            {
                (1.0, "1"), (-0.0, "0"), (0.1, "0.1"), (1e-4, "0.0001"), (1e-5, "1e-05"), (1.5e-7, "1.5e-07"),
                (1e15 + 0.5, "1000000000000000.5"), (9007199254740994.0, "9007199254740994.0"), (1e16, "1e+16"),
                (5e-324, "5e-324"), (double.MaxValue, "1.7976931348623157e+308"), (-12.75, "-12.75"),
            })
                Check(CanonicalJson.Number(value) == text, $"number {value:R} is written {text} (got {CanonicalJson.Number(value)})");
            Check(CanonicalJson.CompareCodePoints("\uE000", "\U0001F600") < 0 && string.CompareOrdinal("\uE000", "\U0001F600") > 0,
                "keys sort by code point, not by UTF-16 unit");
            foreach (var bad in new[] { "{\"a\":1,\"a\":2}", "[NaN]", "[1e400]", "\"\\ud800\"", "[1,]", "[1] 2", "\uFEFF{}", "01", "[" + new string('[', 70) + new string(']', 71) })
            {
                var refused = false;
                try { using var _ = CanonicalJson.Parse(bad); }
                catch (CanonicalJsonException) { refused = true; }
                Check(refused, $"strict parser refuses {bad[..System.Math.Min(20, bad.Length)]}");
            }
            // The same limits as GDScript and Python: 64 nested containers and 800 significant digits (Lane P review).
            Check(Accepts(new string('[', 64) + new string(']', 64)) && Accepts(string.Concat(Enumerable.Repeat("{\"a\":", 64)) + "1" + new string('}', 64)), "64 nested containers are read");
            Check(!Accepts(new string('[', 65) + new string(']', 65)) && !Accepts(string.Concat(Enumerable.Repeat("{\"a\":", 65)) + "1" + new string('}', 65)) &&
                !Accepts(new string('[', 64) + "{}" + new string(']', 64)), "65 nested containers are refused");
            System.Text.Json.Nodes.JsonNode nested = new System.Text.Json.Nodes.JsonArray();
            for (var level = 0; level < 63; level++) nested = new System.Text.Json.Nodes.JsonArray(nested);
            var writes = CanonicalJson.Text(nested) == new string('[', 64) + new string(']', 64);
            try { CanonicalJson.Text(new System.Text.Json.Nodes.JsonArray(nested)); writes = false; }
            catch (CanonicalJsonException) { }
            Check(writes, "64 nested containers are written, 65 are not");
            Check(Accepts("[0." + new string('1', 800) + "]") && Accepts("[1" + new string('0', 300) + "]"), "800 significant digits are read; trailing zeros do not count");
            Check(!Accepts("[0." + new string('1', 801) + "]") && !Accepts("[" + new string('9', 5000) + "]") && !Accepts("[-1." + new string('2', 800) + "e-5]"), "801 significant digits are refused");
            GD.Print($"NATIVE_KERNEL_CANONICAL_JSON: {_checks - _failures}/{_checks} checks passed; golden fixture sha256 {digest} reproduced by C# and by GDScript");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (System.Exception error)
        {
            GD.PushError("Canonical JSON test exception: " + error);
            GetTree().Quit(1);
        }
    }

    private static bool Accepts(string text)
    {
        try { using var _ = CanonicalJson.Parse(text); return true; }
        catch (CanonicalJsonException) { return false; }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Canonical JSON: " + label);
    }
}
