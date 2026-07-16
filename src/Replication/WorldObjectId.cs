using UnityEngine;

namespace DaveTheDiverMP;

internal static class WorldObjectId
{
    internal static uint For(uint sceneId, Component component, int discriminator = 0)
    {
        var hash = Mix(Mix(2166136261u, unchecked((int)sceneId)), discriminator);
        hash = Mix(hash, unchecked((int)Protocol.SceneId(component.GetType().FullName ?? component.name)));
        for (var current = component.transform; current != null; current = current.parent)
        {
            hash = Mix(hash, unchecked((int)Protocol.SceneId(current.name)));
            if (current.parent != null)
                hash = Mix(hash, current.GetSiblingIndex());
        }
        return hash == 0 ? 1u : hash;
    }

    private static uint Mix(uint hash, int value)
    {
        unchecked
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
