using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.D3D11;

internal readonly record struct D3D11ResourceBinding(int Cbv, int Srv, int Uav, int Sampler)
{
    public static D3D11ResourceBinding Empty => new(-1, -1, -1, -1);
}

internal sealed class D3D11BindingLayout
{
    private readonly Dictionary<(ShaderStage Stage, int Set, int Binding), D3D11ResourceBinding> _bindings = new();

    public D3D11BindingLayout(ShaderInfo info)
    {
        if (info.ResourceLayout.SetUsages is null)
        {
            return;
        }

        foreach (ShaderStage stage in Enum.GetValues<ShaderStage>())
        {
            if (stage == ShaderStage.Count)
            {
                continue;
            }

            ResourceStages stageMask = ToResourceStage(stage);
            int cbv = 0;
            int srv = 0;
            int uav = 0;
            int sampler = 0;
            HashSet<int> usedUavSlots = new();

            for (int set = 0; set < info.ResourceLayout.SetUsages.Count; set++)
            {
                ResourceUsageCollection collection = info.ResourceLayout.SetUsages[set];
                if (collection.Usages is null)
                {
                    continue;
                }

                foreach (ResourceUsage usage in collection.Usages)
                {
                    if ((usage.Stages & stageMask) == 0)
                    {
                        continue;
                    }

                    for (int index = 0; index < Math.Max(1, usage.ArrayLength); index++)
                    {
                        int binding = usage.Binding + index;
                        D3D11ResourceBinding mapped = D3D11ResourceBinding.Empty;
                        switch (usage.Type)
                        {
                            case ResourceType.UniformBuffer:
                                mapped = mapped with { Cbv = cbv++ };
                                break;
                            case ResourceType.StorageBuffer:
                                // D3D11 feature levels used by UWP/Xbox do not
                                // expose storage-buffer UAVs to the vertex,
                                // hull, domain or geometry stages. SPIRV-Cross
                                // emits RWByteAddressBuffer for such resources,
                                // which makes CreateVertexShader fail with
                                // E_INVALIDARG and causes the whole draw to be
                                // skipped. These stages only need the read path
                                // for the affected titles, so bind them as SRV;
                                // the HLSL bridge rewrites the declaration to
                                // ByteAddressBuffer accordingly.
                                mapped = usage.Write && SupportsUav(stage)
                                    ? mapped with { Uav = uav++ }
                                    : mapped with { Srv = srv++ };
                                break;
                            case ResourceType.Texture:
                            case ResourceType.BufferTexture:
                                mapped = mapped with { Srv = srv++ };
                                break;
                            case ResourceType.Sampler:
                                mapped = mapped with { Sampler = sampler++ };
                                break;
                            case ResourceType.TextureAndSampler:
                                mapped = mapped with { Srv = srv++, Sampler = sampler++ };
                                break;
                            case ResourceType.Image:
                            case ResourceType.BufferImage:
                                mapped = usage.Write ? mapped with { Uav = uav++ } : mapped with { Srv = srv++ };
                                break;
                        }

                        if (mapped.Uav >= 0)
                        {
                            if (!usedUavSlots.Add(mapped.Uav))
                            {
                                throw new InvalidOperationException(
                                    $"D3D11 UAV binding collision for {stage}: set={set}, binding={binding}, slot=u{mapped.Uav}.");
                            }

                            D3D11DebugTelemetry.Append(
                                $"uav-layout: stage={stage}, set={set}, guestBinding={binding}, hostSlot=u{mapped.Uav}, " +
                                $"type={usage.Type}, write={usage.Write}\n");
                        }

                        _bindings[(stage, set, binding)] = mapped;
                    }
                }
            }

            if (cbv > 14 || srv > 128 || sampler > 16 || uav > 8)
            {
                throw new NotSupportedException(
                    $"D3D11 resource limits exceeded for {stage}: CBV={cbv}/14, SRV={srv}/128, sampler={sampler}/16, UAV={uav}/8.");
            }
        }
    }

    public bool TryGet(ShaderStage stage, int set, int binding, out D3D11ResourceBinding mapped)
    {
        return _bindings.TryGetValue((stage, set, binding), out mapped);
    }

    public IEnumerable<KeyValuePair<(ShaderStage Stage, int Set, int Binding), D3D11ResourceBinding>> Entries => _bindings;
    public bool HasBindings => _bindings.Count != 0;

    private static ResourceStages ToResourceStage(ShaderStage stage) => stage switch
    {
        ShaderStage.Compute => ResourceStages.Compute,
        ShaderStage.Vertex => ResourceStages.Vertex,
        ShaderStage.TessellationControl => ResourceStages.TessellationControl,
        ShaderStage.TessellationEvaluation => ResourceStages.TessellationEvaluation,
        ShaderStage.Geometry => ResourceStages.Geometry,
        ShaderStage.Fragment => ResourceStages.Fragment,
        _ => ResourceStages.None,
    };

    private static bool SupportsUav(ShaderStage stage) => stage is
        ShaderStage.Compute or ShaderStage.Fragment;
}
