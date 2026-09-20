using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cdmw.MeshEditorExperiment;

internal sealed partial class NetTextureSet
{
    private long _materialLayerCompositeCount;

    public long MaterialLayerCompositeCount
    {
        get { lock (_gate) return _materialLayerCompositeCount; }
    }

    public NetMaterialTextureReference SynthesizedBaseReferenceForSubmesh(NetMaterialSet materials, int submeshIndex) =>
        SynthesizeLayers(materials, submeshIndex, "diffuse");

    public NetMaterialTextureReference SynthesizedSurfaceReferenceForSubmesh(NetMaterialSet materials, int submeshIndex) =>
        SynthesizeLayers(materials, submeshIndex, "material");

    public NetMaterialTextureReference SynthesizedSupportReferenceForSubmesh(NetMaterialSet materials, int submeshIndex, string map) =>
        SynthesizeLayers(materials, submeshIndex, map);

    private NetMaterialTextureReference SynthesizeLayers(NetMaterialSet materials, int submeshIndex, string map)
    {
        var layers = materials.MaterialLayersForSubmesh(submeshIndex);
        string ResourceId(NetMaterialLayerBinding layer) => map switch
        {
            "diffuse" => layer.DiffuseResourceId,
            "normal" => layer.NormalResourceId,
            "height" => layer.HeightResourceId,
            _ => layer.MaterialResourceId,
        };
        var colorSpace = map == "diffuse" ? "srgb" : "linear";
        var baseReference = map switch
        {
            "diffuse" => materials.TextureReferenceForSubmesh(submeshIndex, "base", "albedo", "diffuse"),
            "material" => materials.TextureReferenceForSubmesh(submeshIndex, "roughness", "metallic"),
            _ => materials.TextureReferenceForSubmesh(submeshIndex, map),
        };
        var hasContribution = layers.Any(layer =>
            map == "diffuse" && layer.LayerRole == "color_seed"
            || !string.IsNullOrWhiteSpace(ResourceId(layer))
                && (map == "material" || layer.LayerRole != "base" || baseReference.IsEmpty));
        if (!hasContribution) return NetMaterialTextureReference.Empty;

        var references = layers.Select(layer => (
            Binding: layer,
            Map: materials.TextureReferenceForResource(ResourceId(layer), "layer_" + map, colorSpace),
            Mask: materials.TextureReferenceForResource(layer.MaskResourceId, "layer_mask", "linear"))).ToArray();
        var parameters = materials.ParametersForSubmesh(submeshIndex);
        var signature = string.Join("|", "material-composer-v2", _materialLayerMaximumDimension, materials.Signature, submeshIndex, map,
            baseReference.SourceCacheKey, JsonSerializer.Serialize(layers), parameters.BaseTintColor,
            string.Join(";", references.Select(item => item.Map.SourceCacheKey + "," + item.Mask.SourceCacheKey)));
        var fingerprint = "managed-layer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        var resourceId = $"material-layer:{fingerprint}";
        var path = !baseReference.IsEmpty ? baseReference.Path
            : references.SelectMany(item => new[] { item.Map, item.Mask }).FirstOrDefault(item => !item.IsEmpty).Path;
        if (string.IsNullOrWhiteSpace(path)) return NetMaterialTextureReference.Empty;
        var reference = new NetMaterialTextureReference(resourceId, path, fingerprint,
            map == "diffuse" ? "base" : map == "material" ? "roughness" : map,
            colorSpace, path, "archive_lite_managed_material_compiler_v2");
        lock (_gate)
        {
            if (_decodedByFingerprint.ContainsKey(reference.SourceCacheKey)) return reference;
            var baseBitmap = baseReference.IsEmpty ? null : BitmapForReference(baseReference);
            var colors = new List<NetMaterialLayerSource>();
            var support = new List<NetMaterialLayerSurfaceSource>();
            foreach (var item in references)
            {
                var bitmap = item.Map.IsEmpty ? null : BitmapForReference(item.Map);
                var mask = item.Mask.IsEmpty ? null : BitmapForReference(item.Mask);
                // Failed masks must not turn local detail into full coverage.
                if (!string.IsNullOrWhiteSpace(item.Binding.MaskResourceId) && mask is null) continue;
                if (item.Binding.LayerRole == "base" && bitmap is not null) baseBitmap = bitmap;
                if (map == "diffuse" && (bitmap is not null || item.Binding.LayerRole is "base" or "color_seed"))
                    colors.Add(new NetMaterialLayerSource(item.Binding, bitmap, mask));
                else if (bitmap is not null)
                    support.Add(new NetMaterialLayerSurfaceSource(item.Binding, bitmap, mask));
            }
            var tint = parameters.BaseTintColor ?? new System.Numerics.Vector3(0.62f);
            var fallback = Color.FromArgb((int)(Math.Clamp(tint.X, 0, 1) * 255),
                (int)(Math.Clamp(tint.Y, 0, 1) * 255), (int)(Math.Clamp(tint.Z, 0, 1) * 255));
            var compiled = map switch
            {
                "diffuse" => NetMaterialLayerCompiler.Compile(baseBitmap, colors, fallback, _materialLayerMaximumDimension),
                "material" => NetMaterialLayerCompiler.CompileSurface(baseBitmap, support, _materialLayerMaximumDimension),
                _ => NetMaterialLayerCompiler.CompileSupport(baseBitmap, support, map == "normal", _materialLayerMaximumDimension),
            };
            if (compiled is null) return NetMaterialTextureReference.Empty;
            _decodedByFingerprint[reference.SourceCacheKey] = compiled;
            _lastGoodResourceKeys[resourceId] = reference.SourceCacheKey;
            _materialLayerCompositeCount++;
            return reference;
        }
    }
}
