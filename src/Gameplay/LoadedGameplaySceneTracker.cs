using System;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace DaveTheDiverMP;

internal sealed class LoadedGameplaySceneTracker
{
    private readonly Dictionary<int, string> _loaded = new();
    private readonly HashSet<int> _gameplay = new();

    internal int GameplaySceneCount => _gameplay.Count;
    internal bool AllowsUnsafeWorldReplication => _gameplay.Count <= 1;

    internal bool AllowsWorldScopedReplication(SessionRole role, bool connected) =>
        role == SessionRole.Offline || !connected || AllowsUnsafeWorldReplication;

    internal void Reset()
    {
        _loaded.Clear();
        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (scene.IsValid() && scene.isLoaded)
                _loaded[scene.handle] = scene.name;
        }
        Refresh();
    }

    internal void OnSceneLoaded(Scene scene, LoadSceneMode _) 
    {
        if (scene.IsValid() && scene.isLoaded)
            _loaded[scene.handle] = scene.name;
        Refresh();
    }

    internal void OnSceneUnloaded(Scene scene)
    {
        _loaded.Remove(scene.handle);
        _gameplay.Remove(scene.handle);
    }

    internal void Refresh()
    {
        _gameplay.Clear();
        foreach (var pair in _loaded)
        {
            var metadata = SceneMetadataResolver.Resolve(pair.Value);
            if (metadata.CanDive || metadata.IsSharedAction)
                _gameplay.Add(pair.Key);
        }
    }

    internal static void SelfTest()
    {
        var tracker = new LoadedGameplaySceneTracker();
        tracker.SetForTest(10, "DR_A01", true);
        if (!tracker.AllowsUnsafeWorldReplication || tracker.GameplaySceneCount != 1)
            throw new InvalidOperationException("Single gameplay scene was rejected");
        tracker.SetForTest(11, "DR_A02", true);
        if (tracker.AllowsUnsafeWorldReplication || tracker.GameplaySceneCount != 2)
            throw new InvalidOperationException("Additive gameplay scene was not rejected");
        if (tracker.AllowsWorldScopedReplication(SessionRole.Host, true) ||
            !tracker.AllowsWorldScopedReplication(SessionRole.Offline, true) ||
            !tracker.AllowsWorldScopedReplication(SessionRole.Client, false))
            throw new InvalidOperationException("World replication gate self-test failed");
        tracker.RemoveForTest(11);
        if (!tracker.AllowsUnsafeWorldReplication || tracker.GameplaySceneCount != 1)
            throw new InvalidOperationException("Gameplay scene unload did not restore the gate");
    }

    private void SetForTest(int handle, string name, bool gameplay)
    {
        _loaded[handle] = name;
        if (gameplay)
            _gameplay.Add(handle);
        else
            _gameplay.Remove(handle);
    }

    private void RemoveForTest(int handle)
    {
        _loaded.Remove(handle);
        _gameplay.Remove(handle);
    }
}
