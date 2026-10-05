using System.Numerics;
using VRoidDiva.Diva;
using VRoidDiva.Vrm;

namespace VRoidDiva.Retarget;

public sealed class RetargetOptions
{
    /// <summary>Stretch limbs so joints land exactly on DIVA's joints.</summary>
    public bool FitLimbLengths { get; set; } = true;

    /// <summary>Extra uniform scale applied on top of the automatic height match.</summary>
    public float ExtraScale { get; set; } = 1.0f;

    public float MinStretch { get; set; } = 0.5f;
    public float MaxStretch { get; set; } = 2.0f;
}

/// <summary>A primitive moved into DIVA's bind pose and skinned to DIVA bones.</summary>
public sealed class RetargetedPrimitive
{
    public VrmPrimitive Source;
    public Vector3[] Positions;
    public Vector3[] Normals;

    /// <summary>Four DIVA bone indices per vertex (into <see cref="RetargetResult.Bones"/>), -1 = unused.</summary>
    public int[] BoneIndices;

    public float[] BoneWeights;
}

public sealed class RetargetResult
{
    /// <summary>DIVA bones used by the retargeted geometry.</summary>
    public List<ReferenceBone> Bones { get; } = new();

    public List<RetargetedPrimitive> Primitives { get; } = new();

    /// <summary>Resolved humanoid bone → DIVA bone name.</summary>
    public Dictionary<string, string> Mapping { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Warnings { get; } = new();
    public float Scale { get; set; }
}

/// <summary>
/// Moves a VRM humanoid onto the DIVA skeleton.
///
/// DIVA motions drive the game's own skeleton, so a skinned object must be
/// authored in the game's bind pose: every joint of the mesh has to sit where
/// the game's joint is, and each bone must point the same way. The VRM is
/// first aligned and scaled as a whole, then every humanoid bone gets an
/// affine transform that carries its joint onto the DIVA joint and rotates
/// (and optionally stretches) it along the DIVA bone. Vertices are blended
/// through those transforms with their own skin weights, which keeps the
/// surface continuous across joints.
/// </summary>
public static class Retargeter
{
    private sealed class BoneFit
    {
        public HumanBoneSpec Spec;
        public int Node;
        public ReferenceBone Diva;
        public Vector3 SourceJoint; // after global alignment
        public Quaternion Rotation = Quaternion.Identity;
        public Matrix4x4 Transform = Matrix4x4.Identity;
        public bool Solved;
    }

    public static RetargetResult Retarget(VrmModel model, ReferenceSkeleton skeleton, BoneMap boneMap,
        RetargetOptions options = null)
    {
        options ??= new RetargetOptions();
        var result = new RetargetResult();

        // 1. Resolve each humanoid bone to a unique DIVA bone. Parents are
        //    resolved first so that e.g. chest claims kl_mune_b_wj before upperChest.
        var fits = new Dictionary<string, BoneFit>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in boneMap.InHierarchyOrder())
        {
            if (!model.HumanBones.TryGetValue(spec.Name, out int node))
                continue;

            string divaName = spec.DivaCandidates.FirstOrDefault(x => skeleton.Contains(x) && !claimed.Contains(x));
            if (divaName == null)
            {
                if (spec.DivaCandidates.Length > 0 && !spec.DivaCandidates.Any(claimed.Contains))
                {
                    result.Warnings.Add(
                        $"'{spec.Name}' → none of [{string.Join(", ", spec.DivaCandidates)}] exist in the reference skeleton; " +
                        "its vertices follow the parent bone.");
                }

                continue;
            }

            claimed.Add(divaName);
            fits[spec.Name] = new BoneFit { Spec = spec, Node = node, Diva = skeleton[divaName] };
            result.Mapping[spec.Name] = divaName;
        }

        if (!fits.ContainsKey("hips"))
        {
            throw new InvalidOperationException(
                "The hips bone could not be mapped. The reference object sets must include a skinned " +
                "character body (for example rom/objset/mikitm301.farc); run 'inspect' to check one.");
        }

        foreach (string required in new[] { "head", "leftUpperArm", "rightUpperArm" })
        {
            if (!fits.ContainsKey(required))
            {
                throw new InvalidOperationException(
                    $"'{required}' could not be mapped onto the reference skeleton. Pass reference object sets " +
                    "covering both a body and a head/hair item (run 'inspect' to list their bones).");
            }
        }

        // 2. Global alignment: rotate the VRM into DIVA's facing, scale it to DIVA's height.
        Vector3 SourcePos(string bone) => model.Nodes[fits[bone].Node].World.Translation;
        Vector3 DivaPos(string bone) => fits[bone].Diva.Position;

        var sourceBasis = Basis(SourcePos("hips"), SourcePos("head"),
            SourcePos("leftUpperArm"), SourcePos("rightUpperArm"));
        var divaBasis = Basis(DivaPos("hips"), DivaPos("head"),
            DivaPos("leftUpperArm"), DivaPos("rightUpperArm"));

        var globalRotation = Matrix4x4.Transpose(sourceBasis) * divaBasis;

        float sourceHeight = Height(SourcePos, fits);
        float divaHeight = Height(DivaPos, fits);
        float scale = divaHeight / Math.Max(sourceHeight, 1e-4f) * options.ExtraScale;
        result.Scale = scale;

        var sourceHips = SourcePos("hips");
        var divaHips = DivaPos("hips");
        var global = Matrix4x4.CreateTranslation(-sourceHips) * globalRotation *
                     Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(divaHips);

        foreach (var fit in fits.Values)
            fit.SourceJoint = Vector3.Transform(model.Nodes[fit.Node].World.Translation, global);

        // 3. Per-bone fit.
        foreach (var spec in boneMap.InHierarchyOrder())
        {
            if (!fits.TryGetValue(spec.Name, out var fit))
                continue;

            var tail = spec.Tails.Select(x => fits.GetValueOrDefault(x)).FirstOrDefault(x => x != null);
            var rotation = Quaternion.Identity;
            float stretch = 1.0f;
            var divaDirection = Vector3.Zero;

            bool canAim = spec.Fit != FitMode.Follow && tail != null;
            if (canAim)
            {
                var sourceVector = tail.SourceJoint - fit.SourceJoint;
                var divaVector = tail.Diva.Position - fit.Diva.Position;
                float sourceLength = sourceVector.Length();
                float divaLength = divaVector.Length();

                if (sourceLength > 1e-4f && divaLength > 1e-4f)
                {
                    divaDirection = divaVector / divaLength;
                    rotation = RotationBetween(sourceVector / sourceLength, divaDirection);

                    if (spec.Fit == FitMode.RotateAndStretch && options.FitLimbLengths)
                        stretch = Math.Clamp(divaLength / sourceLength, options.MinStretch, options.MaxStretch);
                }
                else
                {
                    canAim = false;
                }
            }

            if (!canAim)
            {
                // Inherit the orientation of the nearest fitted ancestor.
                var parent = NearestFittedAncestor(model, fits, fit.Node);
                rotation = parent?.Rotation ?? Quaternion.Identity;
            }

            fit.Rotation = rotation;

            var linear = Matrix4x4.CreateFromQuaternion(rotation);
            if (stretch != 1.0f)
                linear *= AxisScale(divaDirection, stretch);

            fit.Transform = Matrix4x4.CreateTranslation(-fit.SourceJoint) * linear *
                            Matrix4x4.CreateTranslation(fit.Diva.Position);
            fit.Solved = true;
        }

        // 4. Resolve every glTF node to the humanoid bone that drives it.
        var nodeToFit = new BoneFit[model.Nodes.Count];
        var hipsFit = fits["hips"];
        var fitByNode = fits.Values.ToDictionary(x => x.Node);

        for (int i = 0; i < model.Nodes.Count; i++)
            nodeToFit[i] = ResolveDrivingBone(model, fitByNode, i) ?? hipsFit;

        // 5. Collect output bones (stable order: hierarchy order of the bone map).
        var boneIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in boneMap.InHierarchyOrder())
        {
            if (fits.TryGetValue(spec.Name, out var fit) && !boneIndices.ContainsKey(fit.Diva.Name))
            {
                boneIndices[fit.Diva.Name] = result.Bones.Count;
                result.Bones.Add(fit.Diva);
            }
        }

        // 6. Deform the geometry.
        var globalNormal = globalRotation;
        foreach (var primitive in model.Primitives)
        {
            int vertexCount = primitive.Positions.Length;
            var output = new RetargetedPrimitive
            {
                Source = primitive,
                Positions = new Vector3[vertexCount],
                Normals = new Vector3[vertexCount],
                BoneIndices = new int[vertexCount * 4],
                BoneWeights = new float[vertexCount * 4]
            };

            var influences = new List<(BoneFit Fit, float Weight)>(4);

            for (int v = 0; v < vertexCount; v++)
            {
                influences.Clear();
                for (int k = 0; k < 4; k++)
                {
                    int node = primitive.JointNodes[v * 4 + k];
                    float weight = primitive.JointWeights[v * 4 + k];
                    if (node < 0 || weight <= 0)
                        continue;

                    var fit = nodeToFit[node];
                    int existing = influences.FindIndex(x => x.Fit == fit);
                    if (existing >= 0)
                        influences[existing] = (fit, influences[existing].Weight + weight);
                    else
                        influences.Add((fit, weight));
                }

                if (influences.Count == 0)
                    influences.Add((hipsFit, 1));

                float total = influences.Sum(x => x.Weight);
                var aligned = Vector3.Transform(primitive.Positions[v], global);
                var alignedNormal = Vector3.TransformNormal(primitive.Normals[v], globalNormal);

                var position = Vector3.Zero;
                var normal = Vector3.Zero;
                foreach (var (fit, weight) in influences)
                {
                    float w = weight / total;
                    position += Vector3.Transform(aligned, fit.Transform) * w;
                    normal += Vector3.Transform(alignedNormal, fit.Rotation) * w;
                }

                output.Positions[v] = position;
                output.Normals[v] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

                // Merge influences that share a DIVA bone, keep the strongest four.
                var merged = influences
                    .GroupBy(x => boneIndices[x.Fit.Diva.Name])
                    .Select(g => (Bone: g.Key, Weight: g.Sum(x => x.Weight) / total))
                    .OrderByDescending(x => x.Weight)
                    .Take(4)
                    .ToList();

                float mergedTotal = merged.Sum(x => x.Weight);
                for (int k = 0; k < 4; k++)
                {
                    output.BoneIndices[v * 4 + k] = k < merged.Count ? merged[k].Bone : -1;
                    output.BoneWeights[v * 4 + k] = k < merged.Count ? merged[k].Weight / mergedTotal : 0;
                }
            }

            result.Primitives.Add(output);
        }

        return result;
    }

    /// <summary>
    /// Finds the humanoid bone that moves a node: the nearest fitted ancestor, except
    /// that VRM 1.0 constraint helpers follow what they are constrained to. An aim
    /// helper (e.g. J_Aim_L_TopsUpperArm, parented to the shoulder but aimed at the
    /// elbow) moves with the bone that ends at its target; a rotation helper moves
    /// with its source. Roll helpers already sit under the bone they twist.
    /// </summary>
    private static BoneFit ResolveDrivingBone(VrmModel model, Dictionary<int, BoneFit> fitByNode, int node)
    {
        int current = node;
        for (int guard = 0; current >= 0 && guard < model.Nodes.Count; guard++)
        {
            if (fitByNode.TryGetValue(current, out var fit))
                return fit;

            var info = model.Nodes[current];
            int source = info.ConstraintSource;
            bool validSource = source >= 0 && source < model.Nodes.Count;

            current = info.Constraint switch
            {
                ConstraintKind.Aim when validSource => model.Nodes[source].Parent,
                ConstraintKind.Rotation when validSource => source,
                _ => info.Parent
            };
        }

        return null;
    }

    private static BoneFit NearestFittedAncestor(VrmModel model, Dictionary<string, BoneFit> fits, int node)
    {
        var byNode = fits.Values.Where(x => x.Solved).ToDictionary(x => x.Node);
        for (int current = model.Nodes[node].Parent; current >= 0; current = model.Nodes[current].Parent)
        {
            if (byNode.TryGetValue(current, out var fit))
                return fit;
        }

        return null;
    }

    /// <summary>Orthonormal basis (rows: left, up, forward) of a humanoid.</summary>
    internal static Matrix4x4 Basis(Vector3 hips, Vector3 head, Vector3 leftArm, Vector3 rightArm)
    {
        var up = Vector3.Normalize(head - hips);
        var left = leftArm - rightArm;
        left = Vector3.Normalize(left - Vector3.Dot(left, up) * up);
        var forward = Vector3.Cross(left, up);

        return new Matrix4x4(
            left.X, left.Y, left.Z, 0,
            up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
    }

    private static float Height(Func<string, Vector3> position, Dictionary<string, BoneFit> fits)
    {
        float height = Vector3.Distance(position("hips"), position("head"));

        if (fits.ContainsKey("leftFoot") && fits.ContainsKey("rightFoot"))
        {
            var feet = (position("leftFoot") + position("rightFoot")) * 0.5f;
            height += Vector3.Distance(position("hips"), feet);
        }

        return height;
    }

    internal static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);

        if (dot > 0.999999f)
            return Quaternion.Identity;

        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1 + dot));
    }

    /// <summary>Scales by <paramref name="factor"/> along <paramref name="axis"/> only.</summary>
    internal static Matrix4x4 AxisScale(Vector3 axis, float factor)
    {
        float f = factor - 1;
        return new Matrix4x4(
            1 + f * axis.X * axis.X, f * axis.X * axis.Y, f * axis.X * axis.Z, 0,
            f * axis.Y * axis.X, 1 + f * axis.Y * axis.Y, f * axis.Y * axis.Z, 0,
            f * axis.Z * axis.X, f * axis.Z * axis.Y, 1 + f * axis.Z * axis.Z, 0,
            0, 0, 0, 1);
    }
}
