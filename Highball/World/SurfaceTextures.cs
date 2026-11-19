namespace Highball.World;

/// The textures paved surfaces are laid with: the roads' asphalt, and a grade crossing's panels (asphalt, concrete or wood). Each
/// kind's are whatever ambientCG zips are in its folder under Content/Textures (Asphalt, Concrete, Wood), in name order, however
/// many there are.
///
/// Only the ones in use are loaded: the roads' own, and whichever each crossing's laid with (Use, as its deck's built). line.frag
/// has Slots of them bound (TrackTextures, after the paints' palette), the roads' always in the first, and a lookup from a
/// texture's number (its place in All) to the slot it's in. A line's asphalt code carries which it's laid with
/// (NetworkMeshes.SurfaceBits): 0 for the roads' own, otherwise 1 + its number.
public static class SurfaceTextures
{
    public enum Kind { Asphalt, Concrete, Wood }

    /// How many are bound at once, and how many there can be in all (the lookup's width, and what the code's bits hold).
    public const int Slots = 16;
    public const int Max = 62;

    /// The surface code (NetworkMeshes.SurfaceBits) of unpaved roads' dirt, which is always in the second slot (TrackMaterials.RoadDirt);
    /// the roads' asphalt is 0, always in the first.
    public const int RoadDirtCode = 63;

    /// The slots before the crossings': the roads' asphalt and dirt.
    private const int RoadSlots = 2;

    private static string[][]? _found;
    private static string[]? _all;
    /// Which texture each crossing's laid with, by its id: only what's in use now is bound, so trying one after another never
    /// fills the slots up.
    private static readonly Dictionary<long, int> _used = [];
    private static int[] _bound = [];
    private static readonly object Gate = new();

    /// Moves on whenever another texture comes into use.
    public static int Version { get; private set; }

    /// Each kind's textures, by id (as TrackMaterials names them: "Asphalt/Road012C").
    public static IReadOnlyList<string> Of(Kind kind) => (_found ??= Find())[(int)kind];

    /// Every kind's, one after the other: a texture's number is its place here.
    public static IReadOnlyList<string> All => _all ??= [.. Enumerable.Range(0, 3).SelectMany(k => Of((Kind)k))];

    /// The number of a kind's texture, by its id (the first of the kind if it isn't there any more).
    public static int Number(Kind kind, string? id)
    {
        var ids = Of(kind);
        var index = id is null ? -1 : ids.ToList().FindIndex(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
        var offset = Enumerable.Range(0, (int)kind).Sum(k => Of((Kind)k).Count);
        return Math.Min(offset + Math.Max(index, 0), Max - 1);
    }

    /// Marks the texture a crossing's laid with as in use (in place of what it was laid with before), so it's loaded and bound.
    public static void Use(long owner, int number)
    {
        lock (Gate)
        {
            _used[owner] = number;
            // The ones bound stay in their slots while they're used; one no longer used gives its slot up.
            var used = _used.Values.Distinct().ToHashSet();
            var bound = _bound.Where(used.Contains).ToList();
            foreach (var n in used.Order())
                if (!bound.Contains(n) && bound.Count < Slots - RoadSlots)
                    bound.Add(n);
            if (!bound.SequenceEqual(_bound))
            {
                _bound = [.. bound];
                Version++;
            }
        }
    }

    /// What's bound in each slot: the roads' asphalt and dirt, then the rest in use, in the order they came into use ("" for none). And by
    /// each texture's number, its slot and its kind, for line.frag to look up.
    public static string[] Bound(out (byte Slot, byte Kind)[] lookup)
    {
        var all = All;
        var slots = new string[Slots];
        Array.Fill(slots, "");
        lookup = new (byte, byte)[Max];
        var materials = TrackMaterials.Current;
        slots[0] = materials.RoadAsphaltTexture;
        slots[1] = materials.RoadDirtTexture;
        lock (Gate)
        {
            for (var s = 0; s < _bound.Length && s + RoadSlots < Slots; s++)
            {
                var number = _bound[s];
                if (number < 0 || number >= all.Count || number >= Max)
                    continue;
                slots[s + RoadSlots] = all[number];
                lookup[number] = ((byte)(s + RoadSlots), (byte)KindOf(number));
            }
        }

        return slots;
    }

    /// Which kind a texture's number is.
    public static Kind KindOf(int number)
    {
        for (var k = 0; k < 3; k++)
        {
            if (number < Of((Kind)k).Count)
                return (Kind)k;
            number -= Of((Kind)k).Count;
        }

        return Kind.Asphalt;
    }

    /// A texture's name to show: its file's, without the folder.
    public static string Label(string id) => Path.GetFileName(id.TrimEnd('/'));

    private static string[][] Find()
    {
        var found = new string[3][];
        for (var k = 0; k < 3; k++)
        {
            var folder = ((Kind)k).ToString();
            var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in (string[])[Path.Combine(RailWerkz.Core.IO.ContentPath.SourceRoot, "Textures"), Path.Combine(RailWerkz.Core.IO.ContentPath.Root, "Textures")])
            {
                var dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir))
                    continue;
                foreach (var zip in Directory.EnumerateFiles(dir, "*.zip"))
                {
                    // ambientCG's names: Road012C_1K-JPG.zip, Road012C_2K-JPG.zip, one texture.
                    var name = Path.GetFileNameWithoutExtension(zip);
                    var cut = name.IndexOf('_');
                    ids.Add(folder + "/" + (cut > 0 ? name[..cut] : name));
                }
            }

            found[k] = [.. ids];
        }

        return found;
    }
}
