using System;
using HarmonyLib;
using Verse;

namespace MoreUseResources;

/// <summary>
/// Rebuilds changed map sections before they come into view (RimWorld 1.7.5011 rev24).
///
/// A changed section outside the view is only flagged; the game rebuilds its layers in
/// <c>Section.DrawSection</c> on the frame it becomes visible. Calling the game's own
/// <c>RegenerateDirtyLayers</c> for such a section of the current map does that rebuild early and
/// clears the flag, so the later draw reuses the result.
///
/// Nothing is done about the GPU transfer of the rebuilt meshes, because nothing is left to do:
/// every rebuild path of the game ends in <c>MapDrawLayer.RefreshSubMeshBounds</c>, whose
/// <c>mesh.bounds</c> assignment converts the mesh to engine storage and puts it in the engine's
/// upload queue, and the engine drains that whole queue on the next frame whether or not the
/// section is visible.
///
/// Runs on the main thread inside the caller's frame budget. Between frames only the map being
/// walked is remembered; it is compared with the current map before use and dropped by Reset.
/// </summary>
internal sealed class VanillaSectionWork
{
    private static readonly AccessTools.FieldRef<MapDrawer, Section[,]?>? Sections;
    private static readonly AccessTools.FieldRef<Section, bool>? AnyLayerDirty;
    private static readonly Action<Section>? RegenerateDirtyLayers;

    private Map? sectionMap;
    private int sectionX;
    private int sectionZ;

    static VanillaSectionWork()
    {
        try
        {
            Sections = AccessTools.FieldRefAccess<MapDrawer, Section[,]?>("sections");
            AnyLayerDirty = AccessTools.FieldRefAccess<Section, bool>("anyLayerDirty");
            var method = AccessTools.DeclaredMethod(typeof(Section), "RegenerateDirtyLayers", Type.EmptyTypes);
            RegenerateDirtyLayers = method == null || method.ReturnType != typeof(void)
                ? null : AccessTools.MethodDelegate<Action<Section>>(method);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Sections = null;
            AnyLayerDirty = null;
            RegenerateDirtyLayers = null;
        }
    }

    internal static bool Available => Sections != null && AnyLayerDirty != null && RegenerateDirtyLayers != null;

    internal long SectionsRegenerated { get; private set; }
    internal bool PassDone { get; private set; }

    internal void Reset()
    {
        sectionMap = null;
        sectionX = sectionZ = 0;
        PassDone = false;
    }

    /// <summary>Starts another pass, for sections the game flagged since the last one.</summary>
    internal void BeginNextPass() => PassDone = false;

    /// <summary>Rebuilds at most one flagged off-screen section of the current map. Returns true when it rebuilt one.</summary>
    internal bool Step()
    {
        if (!Available || PassDone || Current.Game == null) return false;
        Map? map = Find.CurrentMap;
        Section[,]? sections = map == null ? null : Sections!(map.mapDrawer);
        if (map == null || sections == null || sections.GetLength(1) == 0)
        {
            sectionMap = null;
            PassDone = true;
            return false;
        }
        if (map != sectionMap)
        {
            sectionMap = map;
            sectionX = sectionZ = 0;
        }
        // Skipping clean sections is only a flag read, so look through a bounded number per call.
        for (int looked = 0; looked < 64; looked++)
        {
            if (sectionX >= sections.GetLength(0))
            {
                sectionMap = null;
                PassDone = true;
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
            return true;
        }
        return false;
    }
}
