using System;
using UnityEngine;

namespace DaveTheDiverMP;

internal static class ControllerScopePolicy
{
    private static readonly string[] UnsupportedControllerTypes =
    {
        "PlayerCharacter_Stealth",
        "DredgePlayerBoat",
        "BaconStoryPlayer",
        "BeatemUp.BeatPlayer",
        "JungleRpgPlayer",
        "GunnerVehicle",
        "GunnerVehicleChasing"
    };

    internal static void SelfTest()
    {
        if (!IsUnsupported("PlayerCharacter_Stealth") ||
            !IsUnsupported("BeatemUp.BeatPlayer") || !IsUnsupported("GunnerVehicle") ||
            IsUnsupported("PlayerCharacter"))
            throw new InvalidOperationException("Controller support policy self-test failed");
    }

    internal static Component FindActiveUnsupported()
    {
        // Fallback scan runs only while no supported player controller is bound.
        foreach (var candidate in UnityEngine.Object.FindObjectsOfType<Component>())
            if (candidate != null && candidate.gameObject?.activeInHierarchy == true &&
                (IsUnsupported(candidate.GetType().Name) ||
                 IsUnsupported(candidate.GetType().FullName)))
                return candidate;
        return null;
    }

    internal static bool IsUnsupported(string typeName) =>
        Array.IndexOf(UnsupportedControllerTypes, typeName) >= 0;
}
