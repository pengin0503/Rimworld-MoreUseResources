using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreUseResources.Core;
using UnityEngine;
using Verse;

namespace MoreUseResources;

/// <summary>
/// Advances two pieces of work the game otherwise does on the frame a map section first comes into
/// view (RimWorld 1.7.5011 rev24):
///
/// Mesh transfer. A section mesh built through the Unity-style API keeps its vertex data in a
/// pending form. The first draw converts it to engine storage, and that conversion queues the GPU
/// upload in the engine's own upload queue. Reading <c>Mesh.EngineMesh</c> performs the same
/// conversion ahead of time. Only meshes still pending are touched, so a mesh that was already
/// converted (uploaded or waiting in the engine's queue) is never registered twice, and the CPU
/// copy stays readable because no upload-and-discard is requested.
///
/// Section regeneration. A changed section outside the view is only flagged; the game rebuilds its
/// layers in <c>Section.DrawSection</c> when it becomes visible. Calling the game's own
/// <c>RegenerateDirtyLayers</c> for such a section of the current map does that rebuild early and
/// clears the flag, so the later draw reuses the result.
///
/// Both run on the main thread inside the caller's frame budget. Between frames only the map being
/// walked is remembered; it is compared with the game's map list before use and dropped by Reset.
/// </summary>
internal sealed class VanillaMeshWork
{
    private const int MaxVerticesPerFrame = 60_000;
    private static readonly AccessTools.FieldRef<MapDrawer, Section[,]?>? Sections;
    private static readonly AccessTools.FieldRef<Section, List<SectionLayer>>? Layers;
    private static readonly AccessTools.FieldRef<Section, bool>? AnyLayerDirty;
    private static readonly AccessTools.FieldRef<Mesh, bool>? CompatDirty;
    private static readonly Action<Section>? RegenerateDirtyLayers;

    private Map? meshMap;
    private int meshMapIndex;
    private int meshX;
    private int meshZ;
    private int verticesThisFrame;
    private long meshFrame = -1;
    private Map? sectionMap;
    private int sectionX;
    private int sectionZ;

    static VanillaMeshWork()
    {
        try
        {
            Sections = AccessTools.FieldRefAccess<MapDrawer, Section[,]?>("sections");
            Layers = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");
            CompatDirty = AccessTools.FieldRefAccess<Mesh, bool>("_compatDirty");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Sections = null;
            Layers = null;
            CompatDirty = null;
        }
        try
        {
            AnyLayerDirty = AccessTools.FieldRefAccess<Section, bool>("anyLayerDirty");
            var method = AccessTools.DeclaredMethod(typeof(Section), "RegenerateDirtyLayers", Type.EmptyTypes);
            RegenerateDirtyLayers = method == null || method.ReturnType != typeof(void)
                ? null : AccessTools.MethodDelegate<Action<Section>>(method);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnyLayerDirty = null;
            RegenerateDirtyLayers = null;
        }
    }

    internal static bool MeshTransferAvailable => Sections != null && Layers != null && CompatDirty != null;
    internal static bool SectionRegenerationAvailable =>
        Sections != null && Layers != null && AnyLayerDirty != null && RegenerateDirtyLayers != null;

    internal long MeshesQueued { get; private set; }
    internal long MeshVerticesQueued { get; private set; }
    internal int MeshPasses { get; private set; }
    internal bool MeshPassDone { get; private set; }
    internal long SectionsRegenerated { get; private set; }
    internal bool SectionPassDone { get; private set; }

    internal void Reset()
    {
        meshMap = sectionMap = null;
        meshMapIndex = meshX = meshZ = sectionX = sectionZ = 0;
        MeshPassDone = SectionPassDone = false;
    }

    /// <summary>Starts another pass, for sections the game rebuilt since the last one.</summary>
    internal void BeginNextPass()
    {
        MeshPassDone = SectionPassDone = false;
    }

    /// <summary>Converts the pending meshes of one section. Returns false when no work remains this frame.</summary>
    internal bool StepMeshTransfer(long frame)
    {
        if (!MeshTransferAvailable || MeshPassDone || Current.Game == null) return false;
        if (frame != meshFrame)
        {
            meshFrame = frame;
            verticesThisFrame = 0;
        }
        if (verticesThisFrame >= MaxVerticesPerFrame) return false;

        List<Map> maps = Find.Maps;
        if (meshMap == null || meshMapIndex >= maps.Count || maps[meshMapIndex] != meshMap)
        {
            // The map list changed: start over instead of trusting stale indices.
            if (meshMap != null) meshMapIndex = meshX = meshZ = 0;
            if (meshMapIndex >= maps.Count)
            {
                FinishMeshPass();
                return false;
            }
            meshMap = maps[meshMapIndex];
        }

        Section[,]? sections = Sections!(meshMap.mapDrawer);
        if (sections == null || meshX >= sections.GetLength(0))
        {
            meshMap = null;
            meshMapIndex++;
            meshX = meshZ = 0;
            if (meshMapIndex >= maps.Count) FinishMeshPass();
            return !MeshPassDone;
        }
        Section? section = sections[meshX, meshZ];
        if (++meshZ >= sections.GetLength(1))
        {
            meshZ = 0;
            meshX++;
        }
        if (section != null) TransferSection(section);
        return true;
    }

    private void FinishMeshPass()
    {
        meshMap = null;
        meshMapIndex = meshX = meshZ = 0;
        MeshPassDone = true;
        MeshPasses++;
    }

    private void TransferSection(Section section)
    {
        List<SectionLayer> layers = Layers!(section);
        for (int i = 0; i < layers.Count; i++)
        {
            SectionLayer layer = layers[i];
            if (layer == null || !layer.Visible) continue;
            List<LayerSubMesh> subMeshes = layer.subMeshes;
            for (int j = 0; j < subMeshes.Count; j++)
            {
                LayerSubMesh sub = subMeshes[j];
                // The same condition MapDrawLayer.DrawLayer uses before it draws a sub-mesh.
                if (sub == null || !sub.finalized || sub.disabled || sub.mesh == null) continue;
                if (!CompatDirty!(sub.mesh)) continue; // already in engine storage: uploaded or queued
                _ = sub.mesh.EngineMesh;
                MeshesQueued++;
                MeshVerticesQueued += sub.verts.Count;
                verticesThisFrame += sub.verts.Count;
            }
        }
    }

    /// <summary>Rebuilds at most one flagged off-screen section of the current map.</summary>
    internal bool StepSectionRegeneration(bool transferMeshes)
    {
        if (!SectionRegenerationAvailable || SectionPassDone || Current.Game == null) return false;
        Map? map = Find.CurrentMap;
        if (map == null)
        {
            SectionPassDone = true;
            return false;
        }
        if (map != sectionMap)
        {
            sectionMap = map;
            sectionX = sectionZ = 0;
        }
        Section[,]? sections = Sections!(map.mapDrawer);
        if (sections == null)
        {
            SectionPassDone = true;
            return false;
        }
        // Skipping clean sections is only a flag read, so look through a bounded number per call.
        for (int looked = 0; looked < 64; looked++)
        {
            if (sectionX >= sections.GetLength(0))
            {
                sectionMap = null;
                SectionPassDone = true;
                return false;
            }
            Section? section = sections[sectionX, sectionZ];
            if (++sectionZ >= sections.GetLength(1))
            {
                sectionZ = 0;
                sectionX++;
            }
            if (section == null || !AnyLayerDirty!(section)) continue;
            RegenerateDirtyLayers!(section);
            SectionsRegenerated++;
            if (transferMeshes && MeshTransferAvailable) TransferSection(section);
            return true;
        }
        return true;
    }
}
