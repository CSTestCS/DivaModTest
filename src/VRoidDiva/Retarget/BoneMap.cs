using System.Text.Json;

namespace VRoidDiva.Retarget;

/// <summary>How a humanoid bone's mesh region is fitted onto the DIVA skeleton.</summary>
public enum FitMode
{
    /// <summary>Only moved onto the DIVA joint, keeping its parent's rotation.</summary>
    Follow,

    /// <summary>Rotated so the bone points along the DIVA bone.</summary>
    Rotate,

    /// <summary>Rotated, then stretched along its length to match the DIVA bone.</summary>
    RotateAndStretch
}

public sealed class HumanBoneSpec
{
    public string Name;
    public string Parent;
    public string[] Tails = Array.Empty<string>();
    public FitMode Fit;
    public string[] DivaCandidates = Array.Empty<string>();
}

/// <summary>
/// VRM humanoid → Project DIVA (Future Tone / Mega Mix+) skeleton mapping.
/// Bone names come from the game's CMN skeleton. Candidates are tried in order
/// and the first one present in the reference skeleton is used.
/// </summary>
public sealed class BoneMap
{
    public Dictionary<string, HumanBoneSpec> Bones { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<HumanBoneSpec> InHierarchyOrder()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HumanBoneSpec>();

        void Visit(HumanBoneSpec spec)
        {
            if (!visited.Add(spec.Name))
                return;
            if (spec.Parent != null && Bones.TryGetValue(spec.Parent, out var parent))
                Visit(parent);
            result.Add(spec);
        }

        foreach (var spec in Bones.Values)
            Visit(spec);

        return result;
    }

    private void Add(string name, string parent, FitMode fit, string[] tails, params string[] diva)
    {
        Bones[name] = new HumanBoneSpec
        {
            Name = name,
            Parent = parent,
            Fit = fit,
            Tails = tails ?? Array.Empty<string>(),
            DivaCandidates = diva
        };
    }

    public static BoneMap CreateDefault()
    {
        var map = new BoneMap();

        // Torso. DIVA splits the body at the waist: kl_kosi_etc_wj carries the
        // pelvis, the cl_mune chain (j_mune_wj → kl_mune_b_wj) the upper body.
        map.Add("hips", null, FitMode.Rotate, new[] { "spine" }, "kl_kosi_etc_wj");
        map.Add("spine", "hips", FitMode.Rotate, new[] { "chest", "upperChest", "neck" }, "j_mune_wj");
        map.Add("chest", "spine", FitMode.Rotate, new[] { "upperChest", "neck" }, "kl_mune_b_wj");
        map.Add("upperChest", "chest", FitMode.Rotate, new[] { "neck" }, "kl_mune_b_wj");
        map.Add("neck", "upperChest", FitMode.Rotate, new[] { "head" }, "kl_kubi");
        map.Add("head", "neck", FitMode.Follow, null, "j_kao_wj");
        map.Add("leftEye", "head", FitMode.Follow, null, "kl_eye_l");
        map.Add("rightEye", "head", FitMode.Follow, null, "kl_eye_r");
        map.Add("jaw", "head", FitMode.Follow, null, "kl_ago_wj");

        foreach (var (side, s) in new[] { ("left", "l"), ("right", "r") })
        {
            string Up(string bone) => side + bone;

            map.Add(Up("Shoulder"), "upperChest", FitMode.RotateAndStretch, new[] { Up("UpperArm") }, $"kl_waki_{s}_wj");
            map.Add(Up("UpperArm"), Up("Shoulder"), FitMode.RotateAndStretch, new[] { Up("LowerArm") }, $"j_kata_{s}_wj_cu");
            map.Add(Up("LowerArm"), Up("UpperArm"), FitMode.RotateAndStretch, new[] { Up("Hand") }, $"j_ude_{s}_wj");
            map.Add(Up("Hand"), Up("LowerArm"), FitMode.Rotate,
                new[] { Up("MiddleProximal"), Up("IndexProximal"), Up("RingProximal") }, $"kl_te_{s}_wj");

            var fingers = new (string Vrm, string Diva)[]
            {
                ("Thumb", "oya"), ("Index", "hito"), ("Middle", "naka"), ("Ring", "kusu"), ("Little", "ko")
            };

            foreach (var (finger, diva) in fingers)
            {
                string[] joints = finger == "Thumb"
                    ? new[] { "Metacarpal", "Proximal", "Distal" }
                    : new[] { "Proximal", "Intermediate", "Distal" };
                string[] divaJoints = { $"nl_{diva}_{s}_wj", $"nl_{diva}_b_{s}_wj", $"nl_{diva}_c_{s}_wj" };

                for (int i = 0; i < 3; i++)
                {
                    string parent = i == 0 ? Up("Hand") : Up(finger + joints[i - 1]);
                    string[] tails = i < 2 ? new[] { Up(finger + joints[i + 1]) } : null;
                    map.Add(Up(finger + joints[i]), parent, i < 2 ? FitMode.RotateAndStretch : FitMode.Follow,
                        tails, divaJoints[i]);
                }
            }

            map.Add(Up("UpperLeg"), "hips", FitMode.RotateAndStretch, new[] { Up("LowerLeg") }, $"j_momo_{s}_wj");
            map.Add(Up("LowerLeg"), Up("UpperLeg"), FitMode.RotateAndStretch, new[] { Up("Foot") }, $"j_sune_{s}_wj");
            map.Add(Up("Foot"), Up("LowerLeg"), FitMode.Rotate, new[] { Up("Toes") }, $"kl_asi_{s}_wj_co");
            map.Add(Up("Toes"), Up("Foot"), FitMode.Follow, null, $"kl_toe_{s}_wj");
        }

        return map;
    }

    /// <summary>
    /// Applies a JSON override of the form { "leftHand": ["kl_te_l_wj"], ... }.
    /// An empty array disables a bone (its weights go to the parent bone).
    /// </summary>
    public void ApplyOverrides(string jsonFilePath)
    {
        var overrides = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(jsonFilePath));
        if (overrides == null)
            return;

        foreach (var (bone, candidates) in overrides)
        {
            if (!Bones.TryGetValue(bone, out var spec))
                throw new InvalidDataException($"Bone map override names unknown VRM humanoid bone '{bone}'.");
            spec.DivaCandidates = candidates ?? Array.Empty<string>();
        }
    }
}
