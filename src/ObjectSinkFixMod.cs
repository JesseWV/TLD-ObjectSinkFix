using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ObjectSinkFix.ObjectSinkFixMod), "ObjectSinkFix", "1.0.0", "Lycanthor")]
[assembly: MelonGame("Hinterland", "TheLongDark")]
[assembly: MelonOptionalDependencies("ModSettings")]

namespace ObjectSinkFix;

public sealed class ObjectSinkFixMod : MelonMod
{
    public static bool PreventionEnabled = true;

    public static bool ApplyRepairs = true;

    public static bool RegistryMatchGuardEnabled = true;

    public static bool RecoveryEnabled = true;

    public static bool RecoverDestroyable = false;

    public static bool RestoreAsPlacement = true;

    internal static MelonLogger.Instance Log = null!;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        LoggerInstance.Msg("Loaded. Preventing furniture displacement and restoring displaced furniture.");
        LoggerInstance.Msg("build " + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? "unknown"));

        if (System.AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "ModSettings"))
        {
            try
            {
                ModSettingsIntegration.Register();
            }
            catch (System.Exception error)
            {
                LoggerInstance.Warning($"Could not load settings; recovery of destroyable objects stays off. {error.Message}");
            }
        }

        PatchRegistryMatchGuard();
        PatchTwinRecovery();
        PatchPlacementHooks();
    }

    private void PatchPlacementHooks()
    {
        var managerType = typeof(Il2Cpp.BreakDown).Assembly.GetType("Il2CppTLD.Placement.PlaceableManager");
        if (managerType == null)
        {
            LoggerInstance.Warning(
                "PlaceableManager not found; restoring already-displaced furniture is disabled this " +
                "session. Prevention is unaffected. This usually means the game has updated.");
            return;
        }

        PatchPlacementMethod(managerType, "Deserialize");
        PatchPlacementMethod(managerType, "RestoreExistingPlacements");
    }

    private void PatchRegistryMatchGuard()
    {
        var method = AccessTools.Method(typeof(Il2Cpp.BreakDown), "FindBreakDownByPosition");
        if (method == null)
        {
            LoggerInstance.Warning(
                "BreakDown.FindBreakDownByPosition not found; containers beside a difficulty variant are not " +
                "protected from wrongful removal this session. Preventing and restoring displacement is " +
                "unaffected. This usually means the game has updated.");
            return;
        }

        var postfix = new HarmonyMethod(typeof(RegistryMatchGuard), nameof(RegistryMatchGuard.Postfix))
        {
            priority = HarmonyLib.Priority.First,
        };
        HarmonyInstance.Patch(method, postfix: postfix);
    }

    private void PatchTwinRecovery()
    {
        var managerType = typeof(Il2Cpp.BreakDown).Assembly.GetType("Il2CppTLD.Placement.PlaceableManager");
        var load = AccessTools.Method(typeof(Il2Cpp.SaveGameSystem), "LoadSceneData");
        var deserialize = managerType == null ? null : AccessTools.Method(managerType, "Deserialize");
        var byGuid = AccessTools.Method(typeof(Il2Cpp.BreakDown), "FindBreakDownByGuid");
        var byPosition = AccessTools.Method(typeof(Il2Cpp.BreakDown), "FindBreakDownByPosition");
        if (load == null || deserialize == null || byGuid == null || byPosition == null)
        {
            LoggerInstance.Warning(
                "A scene load method was not found; removed twin-pair objects are not recovered this session. " +
                "Prevention, repairs and the registry fix are unaffected.");
            return;
        }

        try
        {
            HarmonyInstance.Patch(
                load,
                prefix: new HarmonyMethod(typeof(TwinRecovery), nameof(TwinRecovery.LoadSceneDataPrefix)),
                postfix: new HarmonyMethod(typeof(TwinRecovery), nameof(TwinRecovery.LoadSceneDataPostfix)));
            HarmonyInstance.Patch(
                deserialize, prefix: new HarmonyMethod(typeof(TwinRecovery), nameof(TwinRecovery.DeserializePrefix)));
            HarmonyInstance.Patch(
                byGuid,
                postfix: new HarmonyMethod(typeof(TwinRecovery), nameof(TwinRecovery.ByGuidPostfix))
                {
                    priority = HarmonyLib.Priority.First,
                });
            HarmonyInstance.Patch(
                byPosition,
                postfix: new HarmonyMethod(typeof(TwinRecovery), nameof(TwinRecovery.ByPositionPostfix))
                {
                    priority = HarmonyLib.Priority.First,
                });
            TwinRecovery.Installed = true;
        }
        catch (System.Exception error)
        {
            LoggerInstance.Warning($"Twin-pair recovery could not be installed and is off this session: {error.Message}");
        }
    }

    private void PatchPlacementMethod(System.Type managerType, string methodName)
    {
        var method = AccessTools.Method(managerType, methodName);
        if (method == null)
        {
            LoggerInstance.Warning($"PlaceableManager.{methodName} not found; that capture point is disabled.");
            return;
        }

        HarmonyInstance.Patch(
            method,
            prefix: new HarmonyMethod(typeof(PlacementHooks), nameof(PlacementHooks.Prefix)),
            postfix: new HarmonyMethod(typeof(PlacementHooks), nameof(PlacementHooks.Postfix)));
    }
}

[HarmonyPatch(typeof(Il2Cpp.BreakDown), "StickToGround")]
internal static class BreakDownStickToGroundPatch
{
    private const float SupportTolerance = 0.05f;

    private static bool OverlapsInPlan(Bounds owner, Bounds target)
    {
        return target.max.x >= owner.min.x
            && target.min.x <= owner.max.x
            && target.max.z >= owner.min.z
            && target.min.z <= owner.max.z;
    }

    private static bool Prefix(Il2Cpp.BreakDown __instance, GameObject go)
    {
        GameObject owner = __instance.gameObject;
        GameObject movableRoot = ResolveMovableRoot(go);

        bool measured = GeometryUtil.TryOwnerBounds(owner, out Bounds ownerBounds, out _)
            & GeometryUtil.TryRendererBounds(movableRoot, out Bounds targetBounds);

        bool overlapsInPlan = measured && OverlapsInPlan(ownerBounds, targetBounds);
        bool belowOwnerTop = measured && targetBounds.min.y < ownerBounds.max.y - SupportTolerance;

        bool cargo = measured && overlapsInPlan && !belowOwnerTop;
        bool skip = !cargo && ObjectSinkFixMod.PreventionEnabled;

        return !skip;
    }

    private static GameObject ResolveMovableRoot(GameObject target)
    {
        var placeable = target.GetComponentInParent<Il2CppTLD.Placement.Placeable>();
        return placeable != null ? placeable.gameObject : target;
    }
}

internal static class GeometryUtil
{
    internal static bool TryRendererBounds(GameObject gameObject, out Bounds bounds)
    {
        var renderers = gameObject.GetComponentsInChildren<Renderer>(true);
        bool found = false;
        bounds = default;

        for (int index = 0; index < renderers.Length; index++)
        {
            Renderer renderer = renderers[index];
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            Bounds candidate = renderer.bounds;
            if (candidate.size == Vector3.zero)
            {
                continue;
            }

            if (!found)
            {
                bounds = candidate;
                found = true;
            }
            else
            {
                bounds.Encapsulate(candidate);
            }
        }

        return found;
    }

    internal static bool TryOwnerBounds(GameObject gameObject, out Bounds bounds, out bool fromMesh)
    {
        fromMesh = false;
        if (TryRendererBounds(gameObject, out bounds))
        {
            return true;
        }

        var filters = gameObject.GetComponentsInChildren<MeshFilter>(true);
        bool found = false;

        for (int index = 0; index < filters.Length; index++)
        {
            MeshFilter filter = filters[index];
            if (filter == null)
            {
                continue;
            }

            Mesh? mesh = filter.sharedMesh;
            if (mesh == null)
            {
                continue;
            }

            Bounds local = mesh.bounds;
            if (local.size == Vector3.zero)
            {
                continue;
            }

            Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
            Vector3 center = local.center;
            Vector3 extents = local.extents;

            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = toWorld.MultiplyPoint3x4(center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z));

                if (!found)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(point);
                }
            }
        }

        fromMesh = found;
        return found;
    }

    internal static Bounds RendererBounds(GameObject gameObject)
    {
        return TryRendererBounds(gameObject, out Bounds bounds)
            ? bounds
            : new Bounds(gameObject.transform.position, Vector3.zero);
    }
}

internal static class AuthoredPoseSnapshot
{
    internal readonly struct Pose
    {
        internal Pose(string sceneName, Vector3 position, Quaternion rotation)
        {
            SceneName = sceneName;
            Position = position;
            Rotation = rotation;
        }

        internal string SceneName { get; }
        internal Vector3 Position { get; }
        internal Quaternion Rotation { get; }
    }

    private static readonly Dictionary<string, Pose> Authored = new();

    internal static int Count => Authored.Count;

    internal static void Capture()
    {
        var placeables = UnityEngine.Object.FindObjectsOfType<Il2CppTLD.Placement.Placeable>();

        for (int index = 0; index < placeables.Length; index++)
        {
            var placeable = placeables[index];
            GameObject go = placeable.gameObject;

            if (go.transform.root.name == "DesignPlaceable")
            {
                continue;
            }

            string guid = placeable.m_Guid;
            if (string.IsNullOrEmpty(guid) || Authored.ContainsKey(guid))
            {
                continue;
            }

            Authored[guid] = new Pose(go.scene.name, go.transform.position, go.transform.rotation);
        }
    }

    internal static bool TryGet(string guid, out Pose pose) => Authored.TryGetValue(guid, out pose);
}

internal static class PlacementHooks
{
    internal static void Prefix()
    {
        AuthoredPoseSnapshot.Capture();
    }

    internal static void Postfix()
    {
        RestorePass.Run();
    }
}

internal static class RegistryMatchGuard
{
    internal static void Postfix(Il2Cpp.BreakDownSaveData proxy, ref Il2Cpp.BreakDown __result)
    {
        if (!ObjectSinkFixMod.RegistryMatchGuardEnabled || ReferenceEquals(proxy, null) || __result == null)
        {
            return;
        }

        try
        {
            string entryGuid = proxy.m_Guid;
            if (string.IsNullOrEmpty(entryGuid))
            {
                return;
            }

            GameObject found = __result.gameObject;
            string foundGuid = Il2Cpp.ObjectGuid.MaybeGetGuidFromGameObject(found);
            if (string.IsNullOrEmpty(foundGuid) || foundGuid == entryGuid)
            {
                return;
            }

            ObjectSinkFixMod.Log.Msg(
                $"OSF-REGISTRY01 refused registry match: entry guid={entryGuid} broken={proxy.m_HasBeenBrokenDown} " +
                $"would have been applied to {found.name} guid={foundGuid} in {found.scene.name}.");
            __result = null!;
        }
        catch (System.Exception)
        {
        }
    }
}

internal static class TwinRecovery
{
    internal const string Marker = "OSF-RECOVER01";

    private const float TwinDistance = 0.01f;

    internal static bool Installed;

    private static string? _slotName;
    private static string? _sceneSaveName;

    private static readonly HashSet<int> Kept = new();

    private static readonly List<Il2Cpp.Container> ToEmpty = new();

    private readonly record struct Authored(Il2Cpp.BreakDown BreakDown, GameObject Root, int Id);

    internal static void LoadSceneDataPrefix(string name, string sceneSaveName)
    {
        Kept.Clear();
        ToEmpty.Clear();
        _slotName = name;
        _sceneSaveName = sceneSaveName;
    }

    internal static void LoadSceneDataPostfix()
    {
        try
        {
            EmptyRecordless();
        }
        catch (System.Exception error)
        {
            ObjectSinkFixMod.Log.Msg($"{Marker} scene={_sceneSaveName} emptying failed: {error}");
        }
        finally
        {
            Kept.Clear();
            ToEmpty.Clear();
            _slotName = null;
            _sceneSaveName = null;
        }
    }

    internal static void DeserializePrefix(
        Il2CppSystem.Collections.Generic.List<Il2CppTLD.Placement.PlaceableInfoSaveData> plList, bool isGlobal)
    {
        if (!Installed || !ObjectSinkFixMod.RecoveryEnabled || plList == null)
        {
            return;
        }

        if (isGlobal || _slotName == null || _sceneSaveName == null)
        {
            ObjectSinkFixMod.Log.Msg(
                $"{Marker} skipped Deserialize: isGlobal={isGlobal} insideLoadSceneData={_sceneSaveName != null} " +
                $"records={plList.Count}");
            return;
        }

        try
        {
            Decide(plList);
        }
        catch (System.Exception error)
        {
            Kept.Clear();
            ToEmpty.Clear();
            ObjectSinkFixMod.Log.Msg($"{Marker} scene={_sceneSaveName} kept nothing after exception: {error}");
        }
    }

    internal static void ByGuidPostfix(ref Il2Cpp.BreakDown __result) => RefuseKept(ref __result, "guid");

    internal static void ByPositionPostfix(ref Il2Cpp.BreakDown __result) => RefuseKept(ref __result, "position");

    private static void RefuseKept(ref Il2Cpp.BreakDown __result, string lookup)
    {
        if (Kept.Count == 0 || __result == null)
        {
            return;
        }

        try
        {
            GameObject go = __result.gameObject;
            if (!Kept.Contains(go.GetInstanceID()))
            {
                return;
            }

            ObjectSinkFixMod.Log.Msg(
                $"{Marker} scene={_sceneSaveName} refused {lookup} match to kept {go.name} " +
                $"guid={Key(Il2Cpp.ObjectGuid.MaybeGetGuidFromGameObject(go))}: its registry entry would switch it off again.");
            __result = null!;
        }
        catch (System.Exception)
        {
        }
    }

    private static void Decide(Il2CppSystem.Collections.Generic.List<Il2CppTLD.Placement.PlaceableInfoSaveData> plList)
    {
        int recordCount = plList.Count;
        var candidates = new List<int>();
        for (int index = 0; index < plList.Count; index++)
        {
            var record = plList[index];
            if (record == null || string.IsNullOrEmpty(record.m_Guid))
            {
                continue;
            }

            if (record.m_State == Il2CppTLD.Placement.PlacementState.Removed ||
                record.m_State == Il2CppTLD.Placement.PlacementState.Inactive)
            {
                candidates.Add(index);
            }
        }

        if (candidates.Count == 0)
        {
            ObjectSinkFixMod.Log.Msg($"{Marker} scene={_sceneSaveName} records={recordCount} candidates=0 kept=0");
            return;
        }

        var registry = Il2Cpp.BreakDown.m_BreakDownObjects;
        if (registry == null)
        {
            ObjectSinkFixMod.Log.Msg($"{Marker} scene={_sceneSaveName} kept nothing: the BreakDown object list is null.");
            return;
        }

        var authored = new List<Authored>();
        var byGuid = new Dictionary<string, Authored>();
        for (int index = 0; index < registry.Count; index++)
        {
            var breakDown = registry[index];
            if (breakDown == null)
            {
                continue;
            }

            GameObject go = breakDown.gameObject;
            if (go.transform.root.name == "DesignPlaceable")
            {
                continue;
            }

            var placeable = go.GetComponentInParent<Il2CppTLD.Placement.Placeable>(true);
            if (placeable == null)
            {
                continue;
            }

            var entry = new Authored(breakDown, placeable.gameObject, go.GetInstanceID());
            authored.Add(entry);
            if (!string.IsNullOrEmpty(placeable.m_Guid))
            {
                byGuid.TryAdd(placeable.m_Guid, entry);
            }
        }

        string mode = Il2Cpp.ExperienceModeManager.GetCurrentExperienceModeType().ToString();
        ContainerRecords? containers = null;
        var drop = new List<int>();
        var restore = new List<Authored>();
        var kept = new List<int>();
        var toEmpty = new List<Il2Cpp.Container>();
        var lines = new List<string>();

        foreach (int index in candidates)
        {
            var record = plList[index];
            string guid = record.m_Guid;
            string objectName = "-";
            bool keep = false;
            string reason;

            if (!byGuid.TryGetValue(guid, out var subject))
            {
                reason = "no authored object carrying BreakDown has this guid";
            }
            else
            {
                objectName = subject.Root.name;
                if (GatedOff(subject.BreakDown.gameObject))
                {
                    reason = $"its own gating switches it off on {mode}";
                }
                else if (HiddenByAncestor(subject.Root, out string hider))
                {
                    reason = $"the scene hides it: the ancestor {hider} is switched off";
                }
                else if (FindGatedTwin(subject, authored) is not { } twin)
                {
                    reason = $"not a twin pair: no gated BreakDown within {TwinDistance} m";
                }
                else
                {
                    keep = JudgeContents(subject.Root, guid, ref containers, out reason, out var emptyThis);
                    reason += $"; gated twin {twin.Root.name} guid={Key(PlaceableGuid(twin.Root))} on {mode}";
                    if (keep)
                    {
                        kept.Add(subject.Id);
                        if (emptyThis != null)
                        {
                            toEmpty.AddRange(emptyThis);
                        }

                        restore.Add(subject);
                    }
                }
            }

            if (keep)
            {
                drop.Add(index);
            }

            lines.Add(
                $"{Marker} scene={_sceneSaveName} guid={Key(guid)} state={record.m_State} object={objectName} " +
                $"{(keep ? "KEPT" : "rejected")}: {reason}");
        }

        if (ObjectSinkFixMod.RestoreAsPlacement)
        {
            for (int position = 0; position < drop.Count; position++)
            {
                lines.Add(RestoreRecord(plList[drop[position]], restore[position]));
            }
        }
        else
        {
            for (int position = drop.Count - 1; position >= 0; position--)
            {
                plList.RemoveAt(drop[position]);
            }
        }

        foreach (int id in kept)
        {
            Kept.Add(id);
        }

        ToEmpty.AddRange(toEmpty);

        foreach (string line in lines)
        {
            ObjectSinkFixMod.Log.Msg(line);
        }

        ObjectSinkFixMod.Log.Msg(
            $"{Marker} scene={_sceneSaveName} records={recordCount} candidates={candidates.Count} kept={drop.Count}");
    }

    private static string RestoreRecord(
        Il2CppTLD.Placement.PlaceableInfoSaveData record, Authored subject)
    {
        var state = record.m_State;
        Transform transform = subject.Root.transform;
        var payload = new Il2CppTLD.Placement.PlaceableSaveData
        {
            m_ActiveSelf = true,
            m_Name = subject.Root.name,
            m_Position = transform.position,
            m_Rotation = transform.rotation,
            m_Scale = transform.localScale,
        };

        record.m_Serialized = payload;
        record.m_State = Il2CppTLD.Placement.PlacementState.Active;

        return
            $"{Marker} scene={_sceneSaveName} guid={Key(record.m_Guid)} rewrote {state} to Active on {payload.m_Name} " +
            $"at X {payload.m_Position.x:F3}, Z {payload.m_Position.z:F3}: the game restores it as a placement instead of a keep.";
    }

    private static bool JudgeContents(
        GameObject root, string guid, ref ContainerRecords? containers, out string reason,
        out List<Il2Cpp.Container>? toEmpty)
    {
        toEmpty = null;
        bool setting = ObjectSinkFixMod.RecoverDestroyable;

        var found = root.GetComponentsInChildren<Il2Cpp.Container>(true);
        var subtree = new List<Il2Cpp.Container>();
        for (int i = 0; found != null && i < found.Length; i++)
        {
            if (found[i] != null)
            {
                subtree.Add(found[i]);
            }
        }

        if (subtree.Count == 0)
        {
            reason = setting ? "furniture, restored by the setting" : "furniture, and the setting is off";
            return setting;
        }

        containers ??= ContainerRecords.Load(_slotName!, _sceneSaveName!);
        if (containers.Failure != null)
        {
            reason = $"container records unreadable: {containers.Failure}";
            return false;
        }

        string recordGuid = containers.Has(guid) ? guid : string.Empty;
        foreach (var candidate in subtree)
        {
            if (recordGuid.Length != 0)
            {
                break;
            }

            string containerGuid = Il2Cpp.ObjectGuid.MaybeGetGuidFromGameObject(candidate.gameObject) ?? string.Empty;
            if (containerGuid.Length != 0 && containers.Has(containerGuid))
            {
                recordGuid = containerGuid;
            }
        }

        string scope = subtree.Count == 1 ? string.Empty : $", {subtree.Count} containers";
        if (recordGuid.Length == 0)
        {
            if (!setting)
            {
                reason = $"container record gone{scope}, and the setting is off";
                return false;
            }

            toEmpty = subtree;
            reason = $"container record gone{scope}, restored empty by the setting";
            return true;
        }

        if (!containers.TryGet(recordGuid, out var saved))
        {
            reason = "container record present but unreadable";
            return false;
        }

        if (!saved.Inspected)
        {
            reason = "container unsearched, restored with its saved contents";
            return true;
        }

        if (saved.HasContents)
        {
            reason = "container searched and still holding items, restored with them";
            return true;
        }

        reason = setting ? "container searched and empty, restored by the setting" : "container searched and empty, and the setting is off";
        return setting;
    }

    private static bool HiddenByAncestor(GameObject go, out string hider)
    {
        for (Transform? current = go.transform.parent; current != null; current = current.parent)
        {
            if (!current.gameObject.activeSelf)
            {
                hider = current.gameObject.name;
                return true;
            }
        }

        hider = string.Empty;
        return false;
    }

    private static bool GatedOff(GameObject go)
    {
        for (Transform? current = go.transform; current != null; current = current.parent)
        {
            if (Il2CppTLD.Gameplay.DisableForCurrentModeExtensions.ShouldDisableForCurrentMode(current.gameObject))
            {
                return true;
            }
        }

        return false;
    }

    private static void GatingModes(GameObject go, HashSet<int> enable, HashSet<int> disable)
    {
        for (Transform? current = go.transform; current != null; current = current.parent)
        {
            var off = current.GetComponent<Il2Cpp.DisableObjectForXPMode>();
            if (off != null)
            {
                var modes = off.m_XPModesToDisable;
                if (modes != null)
                {
                    for (int i = 0; i < modes.Count; i++)
                    {
                        disable.Add((int)modes[i]);
                    }
                }
            }

            var on = current.GetComponent<Il2Cpp.EnableObjectForXPMode>();
            if (on != null)
            {
                var target = on.m_ObjectToEnable;
                if (target == null || target.GetInstanceID() == current.gameObject.GetInstanceID())
                {
                    var modes = on.m_XPModesToEnable;
                    if (modes != null)
                    {
                        for (int i = 0; i < modes.Count; i++)
                        {
                            enable.Add((int)modes[i]);
                        }
                    }
                }
            }
        }
    }

    private static bool ComplementaryGating(GameObject subject, GameObject twin, out string detail)
    {
        var subjectEnable = new HashSet<int>();
        var subjectDisable = new HashSet<int>();
        var twinEnable = new HashSet<int>();
        var twinDisable = new HashSet<int>();
        GatingModes(subject, subjectEnable, subjectDisable);
        GatingModes(twin, twinEnable, twinDisable);

        if (twinEnable.Count > 0 && subjectDisable.Count > 0 && twinEnable.IsSubsetOf(subjectDisable))
        {
            detail = string.Empty;
            return true;
        }

        if (subjectEnable.Count > 0 && twinDisable.Count > 0 && subjectEnable.IsSubsetOf(twinDisable))
        {
            detail = string.Empty;
            return true;
        }

        if (subjectEnable.Count == 0 && subjectDisable.Count == 0)
        {
            detail = string.Empty;
            return true;
        }

        detail =
            $"its gating does not stand in for the subject's: subject enable=[{Modes(subjectEnable)}] " +
            $"disable=[{Modes(subjectDisable)}], twin enable=[{Modes(twinEnable)}] disable=[{Modes(twinDisable)}]";
        return false;
    }

    private static string Modes(HashSet<int> modes)
    {
        var names = new List<string>();
        foreach (int mode in modes)
        {
            names.Add(((Il2Cpp.ExperienceModeType)mode).ToString());
        }

        names.Sort();
        return string.Join(",", names);
    }

    private static Authored? FindGatedTwin(Authored subject, List<Authored> authored)
    {
        Vector3 position = subject.BreakDown.transform.position;
        foreach (var other in authored)
        {
            if (other.Id == subject.Id || other.Root.GetInstanceID() == subject.Root.GetInstanceID())
            {
                continue;
            }

            if (Vector3.Distance(position, other.BreakDown.transform.position) > TwinDistance ||
                !GatedOff(other.BreakDown.gameObject))
            {
                continue;
            }

            if (!ComplementaryGating(subject.Root, other.Root, out string detail))
            {
                ObjectSinkFixMod.Log.Msg(
                    $"{Marker} scene={_sceneSaveName} {subject.Root.name}: discarded co-located {other.Root.name} " +
                    $"as a twin, {detail}.");
                continue;
            }

            return other;
        }

        return null;
    }

    private static void EmptyRecordless()
    {
        foreach (var container in ToEmpty)
        {
            if (container == null)
            {
                continue;
            }

            if (!container.m_StartHasBeenCalled)
            {
                container.Start();
            }

            int rolled = container.m_GearToInstantiate.Count;
            container.m_GearToInstantiate.Clear();
            ObjectSinkFixMod.Log.Msg(
                $"{Marker} scene={_sceneSaveName} emptied {container.gameObject.name}: discarded {rolled} rolled item(s).");

            PresentAsSearched(container);
        }
    }

    private static void PresentAsSearched(Il2Cpp.Container container)
    {
        var safe = container.GetComponentInChildren<Il2Cpp.SafeCracking>(true);
        if (safe != null && !safe.m_Cracked)
        {
            safe.m_Cracked = true;
            ObjectSinkFixMod.Log.Msg(
                $"{Marker} scene={_sceneSaveName} unlocked {container.gameObject.name}: restored empty, so it comes back cracked.");
        }

        if (container.m_Inspected)
        {
            return;
        }

        container.m_Inspected = true;
        ObjectSinkFixMod.Log.Msg(
            $"{Marker} scene={_sceneSaveName} marked searched {container.gameObject.name}: restored empty, so it reads as already searched.");
    }

    private static string PlaceableGuid(GameObject root)
    {
        var placeable = root.GetComponent<Il2CppTLD.Placement.Placeable>();
        return placeable != null ? placeable.m_Guid : string.Empty;
    }

    private static string Key(string? guid) =>
        string.IsNullOrEmpty(guid) ? "-" : guid.Length >= 8 ? guid.Substring(0, 8) : guid;

    private sealed class ContainerRecords
    {
        private readonly Dictionary<string, (bool Inspected, bool HasContents)?> _byGuid = new();

        internal string? Failure { get; private set; }

        internal bool Has(string guid) => guid.Length > 0 && _byGuid.ContainsKey(guid);

        internal bool TryGet(string guid, out (bool Inspected, bool HasContents) record)
        {
            record = default;
            if (!_byGuid.TryGetValue(guid, out var value) || value == null)
            {
                return false;
            }

            record = value.Value;
            return true;
        }

        internal static ContainerRecords Load(string slotName, string sceneSaveName)
        {
            var records = new ContainerRecords();
            if (!Il2Cpp.SaveGameSlots.TryLoadDataFromSlot<Il2Cpp.SceneSaveGameFormat>(slotName, sceneSaveName, out var data) ||
                data == null)
            {
                records.Failure = "the slot has no data for this scene";
                return records;
            }

            string text = data.m_ContainerManagerSerialized;
            if (string.IsNullOrEmpty(text))
            {
                records.Failure = "the scene's container data is empty";
                return records;
            }

            using var document = System.Text.Json.JsonDocument.Parse(text);
            var list = document.RootElement;
            if (list.ValueKind == System.Text.Json.JsonValueKind.Object &&
                !list.TryGetProperty("m_SerializedContainers", out list))
            {
                records.Failure = "no m_SerializedContainers";
                return records;
            }

            if (list.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                records.Failure = $"m_SerializedContainers is {list.ValueKind}";
                return records;
            }

            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !entry.TryGetProperty("m_Guid", out var guid) || guid.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                records._byGuid[guid.GetString() ?? string.Empty] = ReadInner(entry);
            }

            return records;
        }

        private static (bool Inspected, bool HasContents)? ReadInner(System.Text.Json.JsonElement entry)
        {
            if (!entry.TryGetProperty("m_SearializedContainer", out var inner))
            {
                return null;
            }

            try
            {
                if (inner.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    using var innerDocument = System.Text.Json.JsonDocument.Parse(inner.GetString() ?? string.Empty);
                    return Read(innerDocument.RootElement);
                }

                return inner.ValueKind == System.Text.Json.JsonValueKind.Object ? Read(inner) : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static (bool Inspected, bool HasContents)? Read(System.Text.Json.JsonElement inner)
        {
            if (inner.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            bool inspected = inner.TryGetProperty("m_InspectedProxy", out var flag) &&
                             flag.ValueKind == System.Text.Json.JsonValueKind.True;
            bool contents = NonEmpty(inner, "m_SerializedItems") || NonEmpty(inner, "m_GearToInstantiateSerialized");
            return (inspected, contents);
        }

        private static bool NonEmpty(System.Text.Json.JsonElement inner, string name) =>
            inner.TryGetProperty(name, out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.Array &&
            value.GetArrayLength() > 0;
    }
}

internal static class RestorePass
{
    private const float LateralTolerance = 0.001f;
    private const float RotationToleranceDegrees = 0.1f;
    private const float MinimumDrop = 0.02f;

    internal static bool SameLocation(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        return a == b || a.StartsWith(b + "_", System.StringComparison.Ordinal) ||
               b.StartsWith(a + "_", System.StringComparison.Ordinal);
    }

    internal static void Run()
    {
        if (AuthoredPoseSnapshot.Count == 0)
        {
            return;
        }

        var placeables = UnityEngine.Object.FindObjectsOfType<Il2CppTLD.Placement.Placeable>();

        int restoredCount = 0;
        for (int index = 0; index < placeables.Length; index++)
        {
            if (placeables[index].gameObject.transform.root.name == "DesignPlaceable")
            {
                restoredCount++;
            }
        }

        if (restoredCount == 0)
        {
            return;
        }

        int repaired = 0;
        for (int index = 0; index < placeables.Length; index++)
        {
            var placeable = placeables[index];
            GameObject go = placeable.gameObject;

            if (go.transform.root.name != "DesignPlaceable")
            {
                continue;
            }

            string guid = placeable.m_Guid;
            if (string.IsNullOrEmpty(guid) || !AuthoredPoseSnapshot.TryGet(guid, out var authored))
            {
                continue;
            }

            Vector3 current = go.transform.position;
            float drop = authored.Position.y - current.y;
            if (drop <= MinimumDrop)
            {
                continue;
            }

            bool lateralMatch = Mathf.Abs(authored.Position.x - current.x) <= LateralTolerance &&
                                Mathf.Abs(authored.Position.z - current.z) <= LateralTolerance;
            bool rotationMatch =
                Quaternion.Angle(authored.Rotation, go.transform.rotation) <= RotationToleranceDegrees;

            if (!lateralMatch || !rotationMatch)
            {
                continue;
            }

            if (StickRegistry.IsRegistered(go) || StickRegistry.IsRegisteredInSubtree(go))
            {
                continue;
            }

            if (!SameLocation(authored.SceneName, go.scene.name))
            {
                continue;
            }

            ObjectSinkFixMod.Log.Msg(
                $"Restoring {go.name} in {go.scene.name}: was {drop:F2} m below its intended position." +
                (ObjectSinkFixMod.ApplyRepairs ? string.Empty : " (reporting only, not moved)"));

            if (ObjectSinkFixMod.ApplyRepairs)
            {
                go.transform.position = authored.Position;
                repaired++;
            }
        }

        if (repaired > 0)
        {
            ObjectSinkFixMod.Log.Msg($"Restored {repaired} displaced object(s).");
        }
    }
}

internal static class StickRegistry
{
    internal static bool IsRegistered(GameObject target)
    {
        if (target == null)
        {
            return false;
        }

        return target.GetComponent<Il2Cpp.StickToGround>() != null ||
               Il2Cpp.BreakDown.GetStickToGroundInDirectParent(target) != null;
    }

    internal static bool IsRegisteredInSubtree(GameObject root)
    {
        return root != null && root.GetComponentsInChildren<Il2Cpp.StickToGround>(true).Length > 0;
    }
}
