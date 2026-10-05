using System.Globalization;
using VRoidDiva.Diva;
using VRoidDiva.Retarget;
using VRoidDiva.Vrm;

namespace VRoidDiva;

public static class Program
{
    private const string Usage = """
        VRoidDiva - turn a VRoid (.vrm) model into a Project DIVA Mega Mix+ module mod.

        Usage:
          VRoidDiva convert <model.vrm> --reference <objset> --out <mod folder> [options]
          VRoidDiva inspect <objset>         List the DIVA bones found in a game object set.
          VRoidDiva vrm-info <model.vrm>     List the humanoid bones and materials of a VRM.

        <objset> is a stock character object set from the game: an extracted .farc
        (e.g. rom/objset/mikitm301.farc), a *_obj.bin, or the game's .cpk itself.
        --reference can be given several times; the bones of all of them are merged,
        so pass the hair, body, hands and shoes items of one module together.

        Options for convert:
          --name <text>           Module name shown in game (default: VRM title)
          --chara <name>          MIKU, RIN, LEN, LUKA, NERU, HAKU, KAITO, MEIKO, SAKINE, TETO (default MIKU)
          --module-id <n>         Module ID (default: derived from the name)
          --item-no <n>           Character item number (default: module ID)
          --cos <n>               Costume number, COS_<n> (default: derived from the name)
          --objset-id <n>         Object set ID, 1-65535 (default: derived from the name)
          --sort-index <n>        Position in the module list (default: module ID)
          --cpk-entry <path>      Archive to read from a .cpk; repeatable (default: Miku's
                                  mikitm001/301/501/681.farc)
          --bone-map <file.json>  Override bone mapping, e.g. {"leftHand": ["kl_te_l_wj"]}
          --max-texture <px>      Downscale textures larger than this (default 2048)
          --scale <f>             Extra uniform scale after height matching (default 1.0)
          --no-stretch            Don't stretch limbs to DIVA's bone lengths
          --all-cloth             Use the CLOTH shader for every material
          --author <text>         Author written to config.toml
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            var options = Arguments.Parse(args.Skip(1));

            return args[0].ToLowerInvariant() switch
            {
                "convert" => Convert(options),
                "inspect" => Inspect(options),
                "vrm-info" => VrmInfo(options),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Run with --help.")
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException
                                              or InvalidOperationException or FileNotFoundException
                                              or NotSupportedException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int Convert(Arguments args)
    {
        var request = new ConvertRequest
        {
            VrmPath = args.Positional(0, "model.vrm"),
            OutputDirectory = args.Required("out"),
            References = args.All("reference").ToList(),
            CpkEntries = args.All("cpk-entry").ToList(),
            Name = args.Optional("name"),
            Character = args.Optional("chara") ?? "MIKU",
            Author = args.Optional("author"),
            ModuleId = args.OptionalInt("module-id"),
            ItemNumber = args.OptionalInt("item-no"),
            Costume = args.OptionalInt("cos"),
            ObjectSetId = args.OptionalInt("objset-id"),
            SortIndex = args.OptionalInt("sort-index"),
            BoneMapPath = args.Optional("bone-map"),
            MaxTextureSize = args.Int("max-texture", 2048),
            Scale = args.Float("scale", 1.0f),
            NoStretch = args.Flag("no-stretch"),
            AllCloth = args.Flag("all-cloth")
        };

        if (request.References.Count == 0)
            throw new ArgumentException("--reference is required (a stock character object set from the game).");

        Converter.Run(request, Console.WriteLine);
        Console.WriteLine("Copy the folder into the game's 'mods' directory (DIVA Mod Loader required).");
        return 0;
    }

    private static int Inspect(Arguments args)
    {
        var skeleton = Converter.LoadReferences(new[] { args.Positional(0, "objset") }, args.All("cpk-entry"),
            Console.WriteLine);

        foreach (string obj in skeleton.LoadedObjects)
            Console.WriteLine($"  object {obj}");

        Console.WriteLine();
        Console.WriteLine($"{"bone",-28} {"id",5}  {"x",8} {"y",8} {"z",8}  object");
        foreach (var bone in skeleton.Bones.Values.OrderBy(x => x.Id))
        {
            var p = bone.Position;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{bone.Name,-28} {bone.Id,5}  {p.X,8:F4} {p.Y,8:F4} {p.Z,8:F4}  {bone.SourceObject}"));
        }

        Console.WriteLine();
        Console.WriteLine("Default bone map coverage:");
        foreach (var spec in BoneMap.CreateDefault().InHierarchyOrder())
        {
            string found = spec.DivaCandidates.FirstOrDefault(skeleton.Contains);
            Console.WriteLine($"  {spec.Name,-26} {(found ?? "MISSING (" + string.Join("/", spec.DivaCandidates) + ")")}");
        }

        return 0;
    }

    private static int VrmInfo(Arguments args)
    {
        var model = VrmModel.Load(args.Positional(0, "model.vrm"));
        Console.WriteLine($"VRM {model.SpecVersion} \"{model.Title}\" by {model.Author}");
        Console.WriteLine();
        Console.WriteLine("Humanoid bones:");
        foreach (var (bone, node) in model.HumanBones.OrderBy(x => x.Value))
            Console.WriteLine($"  {bone,-26} {model.Nodes[node].Name}");
        Console.WriteLine();
        Console.WriteLine("Materials:");
        foreach (var material in model.Materials)
        {
            Console.WriteLine($"  {material.Name,-48} {material.AlphaMode,-6} -> {ObjectBuilder.ChooseShader(material.Name)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{model.Primitives.Count} primitives, " +
                          $"{model.Primitives.Sum(x => x.Positions.Length)} vertices, " +
                          $"{model.Primitives.Sum(x => x.Indices.Length / 3)} triangles");
        return 0;
    }
}

/// <summary>Tiny "--key value" / "--flag" argument parser.</summary>
internal sealed class Arguments
{
    private readonly List<string> mPositional = new();
    private readonly Dictionary<string, List<string>> mNamed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> mFlags = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FlagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-stretch", "all-cloth"
    };

    public static Arguments Parse(IEnumerable<string> args)
    {
        var result = new Arguments();
        var list = args.ToList();

        for (int i = 0; i < list.Count; i++)
        {
            string arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.mPositional.Add(arg);
                continue;
            }

            string name = arg[2..];
            if (FlagNames.Contains(name))
            {
                result.mFlags.Add(name);
                continue;
            }

            if (i + 1 >= list.Count)
                throw new ArgumentException($"--{name} needs a value.");

            if (!result.mNamed.TryGetValue(name, out var values))
                result.mNamed[name] = values = new List<string>();
            values.Add(list[++i]);
        }

        return result;
    }

    public string Positional(int index, string description) =>
        index < mPositional.Count ? mPositional[index] : throw new ArgumentException($"Missing <{description}>.");

    public string Optional(string name) => mNamed.TryGetValue(name, out var values) ? values[^1] : null;

    public string Required(string name) => Optional(name) ?? throw new ArgumentException($"--{name} is required.");

    public IReadOnlyList<string> All(string name) => mNamed.TryGetValue(name, out var values) ? values : Array.Empty<string>();

    public bool Flag(string name) => mFlags.Contains(name);

    public int? OptionalInt(string name) => Optional(name) == null ? null : Int(name, 0);

    public int Int(string name, int fallback)
    {
        string value = Optional(name);
        if (value == null)
            return fallback;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new ArgumentException($"--{name} expects an integer, got '{value}'.");
    }

    public float Float(string name, float fallback)
    {
        string value = Optional(name);
        if (value == null)
            return fallback;
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result
            : throw new ArgumentException($"--{name} expects a number, got '{value}'.");
    }
}
