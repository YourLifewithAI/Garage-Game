using Godot;

namespace GarageGame.Native.Look.Charm;

/// <summary>
/// The charm's cloth, built in code as the fish are (Run 2, L11): a bunting flag and a garment, each one unit wide and one unit
/// deep, hanging from its top edge at y = 0 down to y = -1, with a bit of string along its top. An instance scales it to its own
/// width and drop. UV.y is how far down the cloth a vertex hangs (0 at the string, 1 at the free edge), which the cloth shader's
/// sway reads; UV2.x marks the string (1) from the cloth (0), and an instance's custom w says which of the two it draws.
/// </summary>
public static class CharmMeshes
{
    /// <summary>An instance's parts (its custom w): the cloth and its bit of string, the cloth alone, or the string alone.</summary>
    public const float ClothAndString = 0f, ClothOnly = 1f, StringOnly = 2f;

    /// <summary>A pennant: a triangle pointing down, in three rows so its tip can bend in the wind; its string runs the flags' spacing.</summary>
    public static ArrayMesh Flag()
    {
        var tool = Begin();
        const int rows = 3;
        for (var r = 0; r < rows; r++)
        {
            float v0 = r / (float)rows, v1 = (r + 1) / (float)rows;
            // The row's edges narrow toward the tip.
            float w0 = 0.5f * (1f - v0), w1 = 0.5f * (1f - v1);
            var a = new Vector2(-w0, -v0);
            var b = new Vector2(w0, -v0);
            var c = new Vector2(w1, -v1);
            var d = new Vector2(-w1, -v1);
            Cloth(tool, a, b, c);
            if (w1 > 1e-4f) Cloth(tool, a, c, d);
        }
        String(tool, 0.5f * CharmLook.Tuning.FlagSpacingM / CharmLook.Tuning.FlagWidthM);
        return (ArrayMesh)tool.Commit();
    }

    /// <summary>A garment: a rectangle of cloth in a grid of three columns and four rows, so it bends as it sways; its string is one unit long.</summary>
    public static ArrayMesh Garment()
    {
        var tool = Begin();
        const int columns = 3, rows = 4;
        for (var r = 0; r < rows; r++)
            for (var k = 0; k < columns; k++)
            {
                float x0 = -0.5f + k / (float)columns, x1 = -0.5f + (k + 1) / (float)columns;
                float y0 = -r / (float)rows, y1 = -(r + 1) / (float)rows;
                Cloth(tool, new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1));
                Cloth(tool, new Vector2(x0, y0), new Vector2(x1, y1), new Vector2(x0, y1));
            }
        String(tool, 0.5f);
        return (ArrayMesh)tool.Commit();
    }

    /// <summary>How many triangles a mesh has (the look suite holds the cloth to the fish's 200).</summary>
    public static int Triangles(ArrayMesh mesh) => mesh.SurfaceGetArrayLen(0) / 3;

    private static SurfaceTool Begin()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        return tool;
    }

    /// <summary>A triangle of cloth, clockwise seen from its face (+Z: Godot's front), its normal toward it.</summary>
    private static void Cloth(SurfaceTool tool, Vector2 a, Vector2 b, Vector2 c)
    {
        foreach (var p in new[] { a, b, c })
            Vertex(tool, p, new Vector2(p.X + 0.5f, -p.Y), 0f);
    }

    /// <summary>The string: a strip along the top edge from -half to half; its thickness is the shader's (string_half_m), whatever the instance's drop.</summary>
    private static void String(SurfaceTool tool, float half)
    {
        var a = new Vector2(-half, 1f);
        var b = new Vector2(half, 1f);
        var c = new Vector2(half, -1f);
        var d = new Vector2(-half, -1f);
        foreach (var p in new[] { a, b, c, a, c, d })
            Vertex(tool, p, new Vector2(p.X + 0.5f, 0f), 1f);
    }

    private static void Vertex(SurfaceTool tool, Vector2 p, Vector2 uv, float isString)
    {
        tool.SetNormal(Vector3.Back);
        tool.SetUV(uv);
        tool.SetUV2(new Vector2(isString, 0f));
        tool.AddVertex(new Vector3(p.X, p.Y, 0f));
    }
}
