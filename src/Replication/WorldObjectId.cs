using UnityEngine;

namespace DaveTheDiverMP;

internal static class WorldObjectId
{
    internal static void SelfTest()
    {
        if (BeginHash(1, 7, "Game.Component") == BeginHash(2, 7, "Game.Component") ||
            BeginHash(1, 7, "Game.Component") == BeginHash(1, 8, "Game.Component"))
            throw new System.InvalidOperationException("World object fallback identity lost its scope");
    }

    internal static uint For(Component component, int discriminator = 0)
    {
        if (component == null || !component.gameObject.scene.IsValid())
            return 0;
        var sceneId = Protocol.SceneId(component.gameObject.scene.name);
        var hash = BeginHash(sceneId, discriminator, component.GetType().FullName ?? component.name);
        for (var current = component.transform; current != null; current = current.parent)
        {
            hash = Mix(hash, unchecked((int)Protocol.SceneId(current.name)));
            if (current.parent != null)
                hash = Mix(hash, current.GetSiblingIndex());
        }
        return hash == 0 ? 1u : hash;
    }

    private static uint BeginHash(uint sceneId, int discriminator, string componentType)
    {
        var hash = Mix(Mix(2166136261u, unchecked((int)sceneId)), discriminator);
        return Mix(hash, unchecked((int)Protocol.SceneId(componentType)));
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
