using System;
using BepInEx.Logging;
using UnityEngine;

namespace DaveTheDiverMP;

// Read-only discovery for the only remaining safe harpoon route: an engine-
// supplied inactive handler/pool.  It deliberately never creates or rewires a
// native component.
internal static class HarpoonRuntimeProbe
{
    private static uint _reportedScene;

    internal static void Clear() => _reportedScene = 0;

    internal static void ReportOnce(
        uint sceneId,
        PlayerCharacter player,
        ManualLogSource log,
        SessionTrace trace)
    {
        if (sceneId == 0 || _reportedScene == sceneId || player == null)
            return;
        _reportedScene = sceneId;

        var local = player.CurrentInstanceItemInventory?.harpoonHandler;
        Write(log, trace, "local", local);

        var count = 0;
        foreach (var handler in Resources.FindObjectsOfTypeAll<HarpoonWeaponHandler>())
        {
            if (handler == null || handler == local || count++ >= 24)
                continue;
            Write(log, trace, "candidate", handler);
        }
        log?.LogInfo($"HARPOON-PROBE scene={sceneId:X8} handlers={count} " +
                     "need=inactive-native-handler-without-host-owner");
    }

    private static void Write(
        ManualLogSource log,
        SessionTrace trace,
        string kind,
        HarpoonWeaponHandler handler)
    {
        if (handler == null)
        {
            Write(log, trace, $"kind={kind} handler=none");
            return;
        }
        try
        {
            var gameObject = handler.gameObject;
            var candidate =
                handler.handlerOwner == null && handler.harpoonProjectile != null &&
                handler.harpoonRope != null && handler.ProjectileAttachTransform != null;
            Write(log, trace,
                $"kind={kind} name={gameObject.name} scene={gameObject.scene.name} " +
                $"active={gameObject.activeInHierarchy} owner={handler.handlerOwner?.GetType().Name ?? "none"} " +
                $"projectile={(handler.harpoonProjectile != null)} rope={(handler.harpoonRope != null)} " +
                $"attach={(handler.ProjectileAttachTransform != null)} arm={(handler.RangeAttackArm != null)} " +
                $"candidate={candidate}");
        }
        catch (Exception exception)
        {
            Write(log, trace, $"kind={kind} read-failed={exception.GetType().Name}");
        }
    }

    private static void Write(ManualLogSource log, SessionTrace trace, string details)
    {
        log?.LogInfo($"HARPOON-PROBE {details}");
        trace?.Write("HARPOON-PROBE", details);
    }
}
