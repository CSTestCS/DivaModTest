using System.Globalization;
using System.Text;

namespace VRoidDiva.Diva;

/// <summary>
/// The game's "key=value" text tables (gm_module_id.bin, *itm_tbl.txt).
/// Keys are written sorted, as in the stock files.
/// </summary>
public sealed class KeyValueFile
{
    private readonly SortedDictionary<string, string> mValues = new(StringComparer.Ordinal);

    public void Set(string key, string value) => mValues[key] = value;
    public void Set(string key, int value) => mValues[key] = value.ToString(CultureInfo.InvariantCulture);
    public void Set(string key, float value) => mValues[key] = value.ToString("F6", CultureInfo.InvariantCulture);

    public IReadOnlyDictionary<string, string> Values => mValues;

    public byte[] ToBytes()
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in mValues)
            builder.Append(key).Append('=').Append(value).Append('\n');
        return new UTF8Encoding(false).GetBytes(builder.ToString());
    }

    public static KeyValueFile Parse(string text)
    {
        var file = new KeyValueFile();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '!')
                continue;
            int equals = trimmed.IndexOf('=');
            if (equals > 0)
                file.Set(trimmed[..equals], trimmed[(equals + 1)..]);
        }

        return file;
    }
}

public sealed class Character
{
    public string ModuleName { get; init; } // value of module.N.chara
    public string Prefix { get; init; } // object / item table prefix

    public string ItemTableFileName => $"{Prefix.ToLowerInvariant()}itm_tbl.txt";

    public static readonly IReadOnlyList<Character> All = new[]
    {
        new Character { ModuleName = "MIKU", Prefix = "MIK" },
        new Character { ModuleName = "RIN", Prefix = "RIN" },
        new Character { ModuleName = "LEN", Prefix = "LEN" },
        new Character { ModuleName = "LUKA", Prefix = "LUK" },
        new Character { ModuleName = "NERU", Prefix = "NER" },
        new Character { ModuleName = "HAKU", Prefix = "HAK" },
        new Character { ModuleName = "KAITO", Prefix = "KAI" },
        new Character { ModuleName = "MEIKO", Prefix = "MEI" },
        new Character { ModuleName = "SAKINE", Prefix = "SAK" },
        new Character { ModuleName = "TETO", Prefix = "TET" }
    };

    public static Character Find(string name) =>
        All.FirstOrDefault(x => x.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                x.Prefix.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Unknown character '{name}'. Use one of: {string.Join(", ", All.Select(x => x.ModuleName))}.");
}

/// <summary>Values from the game's ROB_PARTS_KIND / ROB_ITEM_* enums.</summary>
public static class RobItem
{
    public const int TypeReplace = 1;
    public const int AttrObj = 0x1;
    public const int SubIdInner = 9;

    // ROB_PARTS_KIND base body parts (RPK_ATAMA .. RPK_ASI).
    public const int RpkAtama = 1;
    public const int RpkMune = 3;
    public const int RpkAsi = 14;
}

public sealed class ModuleDefinition
{
    public Character Character { get; init; }
    public int ModuleId { get; init; }
    public int CostumeNumber { get; init; } // COS_xxx, 1-based
    public int ItemNumber { get; init; }
    public int SortIndex { get; init; }
    public string DisplayName { get; init; }
    public string ObjectSetName { get; init; }
    public string BodyObjectName { get; init; }
    public string DummyObjectName { get; init; }
}

public static class ModuleTables
{
    /// <summary>
    /// The item replaces every stock base body part (head, torso, arms, legs…):
    /// the converted model takes the chest slot, the rest get an invisible object.
    /// It is equipped in the INNER slot, which the game processes as a REPLACE item.
    /// </summary>
    public static KeyValueFile CreateItemTable(ModuleDefinition module)
    {
        var table = new KeyValueFile();

        table.Set("cos.0.id", module.CostumeNumber - 1);
        table.Set("cos.0.item.0", module.ItemNumber);
        table.Set("cos.0.item.length", 1);
        table.Set("cos.length", 1);

        const string item = "item.0";
        table.Set($"{item}.attr", RobItem.AttrObj);

        int objIndex = 0;
        table.Set($"{item}.data.obj.{objIndex}.rpk", RobItem.RpkMune);
        table.Set($"{item}.data.obj.{objIndex}.uid", module.BodyObjectName);
        objIndex++;

        for (int rpk = RobItem.RpkAtama; rpk <= RobItem.RpkAsi; rpk++)
        {
            if (rpk == RobItem.RpkMune)
                continue;
            table.Set($"{item}.data.obj.{objIndex}.rpk", rpk);
            table.Set($"{item}.data.obj.{objIndex}.uid", module.DummyObjectName);
            objIndex++;
        }

        table.Set($"{item}.data.obj.length", objIndex);
        table.Set($"{item}.des_id", 0);
        table.Set($"{item}.exclusion", 0);
        table.Set($"{item}.face_depth", 0.0f);
        table.Set($"{item}.flag", 0);
        table.Set($"{item}.name", module.DisplayName);
        table.Set($"{item}.no", module.ItemNumber);
        table.Set($"{item}.objset.0", module.ObjectSetName);
        table.Set($"{item}.objset.length", 1);
        table.Set($"{item}.org_itm", 0);
        table.Set($"{item}.point", 0);
        table.Set($"{item}.sub_id", RobItem.SubIdInner);
        table.Set($"{item}.type", RobItem.TypeReplace);
        table.Set("item.length", 1);

        return table;
    }

    public static KeyValueFile CreateModuleTable(ModuleDefinition module)
    {
        var table = new KeyValueFile();
        const string entry = "module.0";

        table.Set($"{entry}.attr", 0);
        table.Set($"{entry}.chara", module.Character.ModuleName);
        table.Set($"{entry}.cos", $"COS_{module.CostumeNumber:D3}");
        table.Set($"{entry}.id", module.ModuleId);
        table.Set($"{entry}.name", module.DisplayName);
        table.Set($"{entry}.ng", 0);
        table.Set($"{entry}.shop_ed_day", 1);
        table.Set($"{entry}.shop_ed_month", 1);
        table.Set($"{entry}.shop_ed_year", 2029);
        table.Set($"{entry}.shop_price", 0);
        table.Set($"{entry}.shop_st_day", 1);
        table.Set($"{entry}.shop_st_month", 1);
        table.Set($"{entry}.shop_st_year", 2009);
        table.Set($"{entry}.sort_index", module.SortIndex);
        table.Set("module.data_list.length", 1);

        return table;
    }

    /// <summary>DIVA Mod Loader string array (grouped "module.&lt;id&gt;" format).</summary>
    public static string CreateStringArray(ModuleDefinition module) =>
        $"module.{module.ModuleId} = \"{EscapeToml(module.DisplayName)}\"\n";

    internal static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
}
