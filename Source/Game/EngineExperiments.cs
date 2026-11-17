using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Brutal.VulkanApi;
using HarmonyLib;
using MoreUseResources.Core;
using UnityEngine;
using Verse;
using Waylith;
using Waylith.Rendering;
using Texture2D = UnityEngine.Texture2D;

namespace MoreUseResources;

internal sealed class EngineExperiments
{
    private const int MaxQualityOverrides = 1024;
    private const int MaxCandidatesPerFrame = 12;
    private readonly OwnedSetting<TextureCacheCodec> codec = new(
        () => GpuTextureService.CacheCodec, value => GpuTextureService.CacheCodec = value);
    private readonly Dictionary<Texture2D, OwnedSetting<int>> samplers = new(ReferenceEqualityComparer.Instance);
    private IEnumerator<GraphicData>? graphics;
    private IEnumerator<Texture2D>? textures;
    private bool graphicsDone;
    private bool qualityDone;
    private bool codecEnabled;
    private bool preferQuality;
    private long nextBudgetCheck;
    private VramInfo budget;
    private readonly VanillaSectionWork sections = new();
    private readonly PreparationRotation rotation = new(
        VanillaWorkUnit.GraphicPrepare, VanillaWorkUnit.SectionRegeneration);
    private long nextSectionPass;
    private bool sectionFaulted;

    internal VanillaSectionWork Sections => sections;
    internal bool SectionWorkFaulted => sectionFaulted;

    internal int PreparedGraphics { get; private set; }
    internal int GraphicsFailures { get; private set; }
    internal int RefinedTextures => samplers.Count;
    internal bool QualityConflict { get; private set; }
    internal bool UncompressedActive { get; private set; }

    internal void UpdateCodec(bool requested)
    {
        if (!TexturePrewarm.SupportedBuild) return;
        if (!requested)
        {
            codec.Restore();
            codecEnabled = false;
            UncompressedActive = false;
            return;
        }
        // Only take ownership of the engine default. A mod-selected codec wins.
        if (!codecEnabled && GpuTextureService.CacheCodec != TextureCacheCodec.Lz4) return;
        codecEnabled = true;
        UncompressedActive = codec.Apply(TextureCacheCodec.None);
    }

    internal void Update(ResourceSettings settings, GamePhase phase, ResourceActivation gpu, bool ready,
        bool loaded, bool throttled, bool memoryOk, long frameStart, WorkUnitYields yields)
    {
        if (!TexturePrewarm.SupportedBuild) return;
        bool active = settings.Enabled && settings.GpuEnabled && loaded;
        if (!active || !settings.GpuAnisotropicMeshes) RestoreQuality();
        if (!active || !settings.GpuPrepareGraphics) ResetGraphics();
        if (!active || !settings.GpuPrepareSections) sections.Reset();
        if (!ready || (!settings.GpuPrepareGraphics && !settings.GpuAnisotropicMeshes && !settings.GpuPrepareSections)
            || !HardwareWork.MayPrepare(phase, gpu.Active, throttled, memoryOk)) return;
        // Rebuilding a section is whole main-thread work, so it only runs while the game is paused.
        bool sectionWanted = settings.GpuPrepareSections && phase == GamePhase.Paused
            && !sectionFaulted && VanillaSectionWork.Available;

        var device = EngineApplication.Instance?.GraphicsDevice;
        if (device == null) return;
        long now = Environment.TickCount64;
        if (now >= nextBudgetCheck)
        {
            nextBudgetCheck = now + 250;
            if (!TexturePrewarm.TryReadTextureHeap(out budget)) return;
        }
        if (!HasHeadroom(settings)) return;
        if (sections.PassDone && now >= nextSectionPass)
        {
            // Sections change while the colony runs; look again at the texture maintenance interval.
            if (nextSectionPass != 0) sections.BeginNextPass();
            nextSectionPass = settings.GpuRefreshSeconds > 0 ? now + settings.GpuRefreshSeconds * 1000L : long.MaxValue;
        }

        // Each candidate is atomic. Share the existing per-frame GPU budget and recheck after it returns.
        bool quality = settings.GpuAnisotropicMeshes && !qualityDone;
        if (quality && textures == null)
        {
            QualityConflict = HasGraphicsMod();
            qualityDone = QualityConflict || !device.PhysicalDevice.GetFeatures().SamplerAnisotropy
                || device.PhysicalDevice.GetProperties().Limits.MaxSamplerAnisotropy < 2;
            if (!qualityDone) textures = CandidateTextures().GetEnumerator();
        }
        bool sectionMore = sectionWanted;
        for (int i = 0; i < MaxCandidatesPerFrame
            && HardwareWork.HasFrameBudget(Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds,
                settings.GpuFrameBudgetTenthMs, gpu.Intensity); i++)
        {
            bool refine = settings.GpuAnisotropicMeshes && !qualityDone;
            // A unit another mod already performs is skipped; its turn goes to the next unit with work.
            // The rotation only moves on when a unit really runs, so the filter step cannot use up a turn.
            VanillaWorkUnit? unit = refine && preferQuality ? null : rotation.Next(candidate => candidate switch
            {
                VanillaWorkUnit.GraphicPrepare => settings.GpuPrepareGraphics && !graphicsDone,
                VanillaWorkUnit.SectionRegeneration => sectionMore && !sections.PassDone,
                _ => false,
            }, yields);
            if (unit == null && !refine) break;

            bool allocated = false;
            if (unit == null) StepQuality();
            else if (unit == VanillaWorkUnit.GraphicPrepare) allocated = StepGraphics();
            else if (unit != null)
            {
                try
                {
                    // One rebuilt section is a whole unit of main-thread work for this frame. The engine
                    // uploads its new meshes on the next frame, so the budget is read again afterwards.
                    allocated = sections.Step();
                    sectionMore = false;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    sectionFaulted = true;
                    sections.Reset();
                    Log.Warning("[More Use Resources] Section preparation stopped. " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            preferQuality = !preferQuality;
            if (allocated && (!TexturePrewarm.TryReadTextureHeap(out budget) || !HasHeadroom(settings))) break;
        }
    }

    private bool HasHeadroom(ResourceSettings settings) =>
        budget.DriverReported && budget.UsageBytes < budget.CapBytes(settings.GpuTargetPercent,
            settings.GpuKeepFreeMiB * ResourceSettings.MiB) - 64 * ResourceSettings.MiB;

    private static bool HasGraphicsMod()
    {
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            if (pack.PackageId.Contains("graphicssettings", StringComparison.OrdinalIgnoreCase)
                || pack.PackageId.Contains("isorim", StringComparison.OrdinalIgnoreCase)
                || pack.FolderName is "1678847247" or "3837268204"
                || pack.Name.Contains("Graphics Settings", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void StepQuality()
    {
        if (textures == null) return;
        if (samplers.Count == MaxQualityOverrides || !textures.MoveNext())
        {
            textures.Dispose();
            textures = null;
            qualityDone = true;
            return;
        }

        Texture2D texture = textures.Current;
        var core = texture.CoreTexture;
        if (core == null || core.SourcePath == null || samplers.ContainsKey(texture)
            || !HardwareWork.CanImproveMeshFiltering(core.MipCount, core.anisoLevel,
                texture.wrapMode == UnityEngine.TextureWrapMode.Repeat, texture.filterMode != UnityEngine.FilterMode.Point))
            return;

        var setting = new OwnedSetting<int>(() => core.anisoLevel, value => SetMeshAnisotropy(core, value));
        setting.Apply(4);
        samplers.Add(texture, setting);
    }

    // The mesh descriptor cache keys samplers by both generations. The public
    // anisoLevel property alone leaves already-cached descriptors unchanged on rev24.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_residencyGeneration")]
    private static extern ref int ResidencyGeneration(Waylith.Rendering.Texture texture);

    private static void SetMeshAnisotropy(Waylith.Rendering.Texture texture, int level)
    {
        // Resolve the ABI-bound field before changing the public property.
        ref int generation = ref ResidencyGeneration(texture);
        texture.anisoLevel = level;
        Interlocked.Increment(ref generation);
        Interlocked.Increment(ref Waylith.Rendering.Texture.GlobalResidencyEpoch);
    }

    private static IEnumerable<Texture2D> CandidateTextures()
    {
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            var holder = pack.GetContentHolder<Texture2D>();
            if (holder == null) continue;
            foreach (Texture2D texture in holder.contentList.Values) yield return texture;
        }
    }

    private bool StepGraphics()
    {
        if (graphics == null)
        {
            graphics = CandidateGraphics().GetEnumerator();
        }
        if (!graphics.MoveNext())
        {
            graphicsDone = true;
            graphics.Dispose();
            graphics = null;
            return false;
        }

        GraphicData data = graphics.Current;
        if (data.GetType() != typeof(GraphicData) || string.IsNullOrEmpty(data.texPath)
            || (data.graphicClass != typeof(Graphic_Single) && data.graphicClass != typeof(Graphic_Multi))
            || data.shaderParameters?.Any(parameter => parameter == null || parameter.GetType().Assembly != typeof(ThingDef).Assembly) == true)
            return false;
        try
        {
            if (data.Graphic?.MatSingle == null) return false;
            PreparedGraphics++;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GraphicsFailures++;
            if (GraphicsFailures <= 5)
                Log.Warning("[More Use Resources] Graphic prebuild skipped " + data.texPath + ". " + ex.Message);
            return true; // a failed call may still have allocated resources
        }
    }

    private static IEnumerable<GraphicData> CandidateGraphics()
    {
        foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
        {
            if (def.graphicData is { } graphic)
            {
                yield return graphic;
                if (graphic.attachments != null)
                    foreach (GraphicData? attachment in graphic.attachments)
                        if (attachment != null) yield return attachment;
            }
            if (def.building is { } building)
            {
                if (building.fullGraveGraphicData is { } grave) yield return grave;
                if (building.trapUnarmedGraphicData is { } trap) yield return trap;
                if (building.gibbetCageTopGraphicData is { } cage) yield return cage;
                if (building.doorTopGraphic is { } door) yield return door;
            }
        }
    }

    private void RestoreQuality()
    {
        foreach (OwnedSetting<int> setting in samplers.Values) setting.Restore();
        samplers.Clear();
        textures?.Dispose();
        textures = null;
        qualityDone = false;
        QualityConflict = false;
    }

    private void ResetGraphics()
    {
        graphics?.Dispose();
        graphics = null;
        graphicsDone = false;
    }

    internal void ResetGraphicsScans()
    {
        RestoreQuality();
        ResetGraphics();
        sections.Reset();
        sectionFaulted = false;
    }

    internal void Restore()
    {
        UpdateCodec(false);
        ResetGraphicsScans();
    }
}
