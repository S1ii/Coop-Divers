using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WeaponSystem;

namespace DaveTheDiverMP;

internal sealed class ProjectileVisualReplicator
{
    private sealed class RemoteProjectile
    {
        internal SpriteRenderer Renderer;
        internal LineRenderer Rope;
        internal float LastSeen;
    }

    private readonly Dictionary<int, Component> _local = new();
    private readonly Dictionary<int, RemoteProjectile> _remote = new();
    private readonly Dictionary<uint, Sprite> _sprites = new();
    private readonly List<int> _staleIds = new();
    private Material _ropeMaterial;
    private float _nextSend;

    internal void Register(Component projectile)
    {
        if (projectile != null)
            _local[projectile.GetInstanceID()] = projectile;
    }

    internal void Update(UdpSession session, uint sceneId, float now)
    {
        if (session == null)
            return;

        if (session.SceneMatches(sceneId))
        {
            if (now >= _nextSend)
            {
                _nextSend = now + 1f / 30f;
                _staleIds.Clear();
                foreach (var pair in _local)
                {
                    if (!TryCapture(sceneId, pair.Key, pair.Value, out var state))
                    {
                        _staleIds.Add(pair.Key);
                        continue;
                    }
                    session.SendProjectileVisualState(state);
                }
                foreach (var id in _staleIds)
                    _local.Remove(id);
            }

            while (session.TryTakeProjectileVisualState(out var state))
            {
                if (state.SceneId == sceneId)
                    Apply(state, now);
            }
        }

        _staleIds.Clear();
        foreach (var pair in _remote)
        {
            if (now - pair.Value.LastSeen > 0.35f)
            {
                UnityEngine.Object.Destroy(pair.Value.Renderer.gameObject);
                _staleIds.Add(pair.Key);
            }
        }
        foreach (var id in _staleIds)
            _remote.Remove(id);
    }

    internal void Clear()
    {
        _local.Clear();
        _nextSend = 0f;
        _staleIds.Clear();
        foreach (var remote in _remote.Values)
            if (remote.Renderer != null)
                UnityEngine.Object.Destroy(remote.Renderer.gameObject);
        _remote.Clear();
        _sprites.Clear();
        if (_ropeMaterial != null)
            UnityEngine.Object.Destroy(_ropeMaterial);
        _ropeMaterial = null;
    }

    private void Apply(ProjectileVisualState state, float now)
    {
        if (!_remote.TryGetValue(state.Id, out var remote))
        {
            var visual = new GameObject("DTMP Remote Projectile");
            remote = new RemoteProjectile
            {
                Renderer = visual.AddComponent<SpriteRenderer>(),
                Rope = CreateRope(visual)
            };
            _remote.Add(state.Id, remote);
        }
        var renderer = remote.Renderer;
        renderer.sprite = ResolveSprite(state.SpriteId);
        renderer.flipX = state.Flipped;
        renderer.sortingLayerID = state.SortingLayerId;
        renderer.sortingOrder = state.SortingOrder;
        renderer.transform.position = new Vector3(state.X, state.Y, state.Z);
        renderer.transform.rotation = Quaternion.Euler(0f, 0f, state.Rotation);
        renderer.transform.localScale = new Vector3(state.ScaleX, state.ScaleY, 1f);
        remote.Rope.enabled = state.HasRope;
        if (state.HasRope)
        {
            remote.Rope.sortingLayerID = state.SortingLayerId;
            remote.Rope.sortingOrder = state.SortingOrder - 1;
            remote.Rope.SetPosition(0, new Vector3(
                state.RopeStartX, state.RopeStartY, state.RopeStartZ));
            remote.Rope.SetPosition(1, new Vector3(
                state.RopeEndX, state.RopeEndY, state.RopeEndZ));
        }
        remote.LastSeen = now;
    }

    private LineRenderer CreateRope(GameObject owner)
    {
        var rope = owner.AddComponent<LineRenderer>();
        rope.positionCount = 2;
        rope.useWorldSpace = true;
        rope.startWidth = 0.018f;
        rope.endWidth = 0.012f;
        rope.startColor = new Color(0.08f, 0.25f, 0.38f, 0.9f);
        rope.endColor = rope.startColor;
        if (_ropeMaterial == null)
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader != null)
                _ropeMaterial = new Material(shader);
        }
        if (_ropeMaterial != null)
            rope.sharedMaterial = _ropeMaterial;
        rope.enabled = false;
        return rope;
    }

    private Sprite ResolveSprite(uint id)
    {
        if (_sprites.TryGetValue(id, out var sprite))
            return sprite;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<Sprite>())
            if (candidate != null)
                _sprites.TryAdd(Protocol.SceneId(candidate.name), candidate);
        return _sprites.TryGetValue(id, out sprite) ? sprite : null;
    }

    private static bool TryCapture(
        uint sceneId,
        int id,
        Component projectile,
        out ProjectileVisualState state)
    {
        state = default;
        if (projectile == null || !projectile.gameObject.activeInHierarchy)
            return false;
        var renderer = projectile.GetComponentInChildren<SpriteRenderer>(true);
        if (renderer == null || !renderer.enabled || renderer.sprite == null)
            return false;
        var scale = renderer.transform.lossyScale;
        var ropeStart = Vector3.zero;
        var ropeEnd = Vector3.zero;
        var hasRope = false;
        if (projectile is HarpoonProjectile harpoon)
        {
            var handler = harpoon.m_HarpoonHandler;
            var rope = handler?.harpoonRope;
            hasRope = rope != null && rope.m_IsShowLine;
            if (hasRope)
            {
                ropeStart = handler.ProjectileAttachTransform != null
                    ? handler.ProjectileAttachTransform.position
                    : harpoon.Owner != null ? harpoon.Owner.position : renderer.transform.position;
                ropeEnd = renderer.transform.position;
            }
        }
        state = new ProjectileVisualState(
            sceneId, id, Protocol.SceneId(renderer.sprite.name),
            renderer.transform.position.x, renderer.transform.position.y, renderer.transform.position.z,
            renderer.transform.eulerAngles.z, Mathf.Abs(scale.x), Mathf.Abs(scale.y),
            renderer.sortingLayerID, renderer.sortingOrder, renderer.flipX ^ (scale.x < 0f),
            hasRope, ropeStart.x, ropeStart.y, ropeStart.z, ropeEnd.x, ropeEnd.y, ropeEnd.z);
        return true;
    }
}

[HarmonyPatch(typeof(HarpoonProjectile), nameof(HarpoonProjectile.Fire))]
internal static class HarpoonProjectileVisualPatch
{
    private static void Postfix(HarpoonProjectile __instance) =>
        ProbeBehaviour.Instance?.RegisterProjectile(__instance);
}

[HarmonyPatch(typeof(HarpoonWeaponHandler), nameof(HarpoonWeaponHandler.ChangeState))]
internal static class HarpoonStateTracePatch
{
    private static void Prefix(
        HarpoonWeaponHandler __instance,
        HarpoonWeaponHandler.HarpoonActionState state)
    {
        ProbeBehaviour.Instance?.TraceHarpoon($"state-prefix->{state}", __instance);
    }

    private static void Postfix(
        HarpoonWeaponHandler __instance,
        HarpoonWeaponHandler.HarpoonActionState state)
    {
        ProbeBehaviour.Instance?.TraceHarpoon($"state-postfix->{state}", __instance);
    }
}

[HarmonyPatch(typeof(HarpoonWeaponHandler), nameof(HarpoonWeaponHandler.FinishRecallHarpoon))]
internal static class HarpoonFinishRecallTracePatch
{
    private static void Prefix(HarpoonWeaponHandler __instance, bool isSuccess) =>
        ProbeBehaviour.Instance?.TraceHarpoon("finish-recall-prefix", __instance, isSuccess);

    private static void Postfix(HarpoonWeaponHandler __instance, bool isSuccess) =>
        ProbeBehaviour.Instance?.TraceHarpoon("finish-recall-postfix", __instance, isSuccess);
}

[HarmonyPatch(typeof(HarpoonWeaponHandler), nameof(HarpoonWeaponHandler.ResetWeaponResources))]
internal static class HarpoonResetTracePatch
{
    private static void Prefix(HarpoonWeaponHandler __instance) =>
        ProbeBehaviour.Instance?.TraceHarpoon("reset-prefix", __instance);

    private static void Postfix(HarpoonWeaponHandler __instance) =>
        ProbeBehaviour.Instance?.TraceHarpoon("reset-postfix", __instance);
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch))]
internal static class WeaponProjectileVisualPatch
{
    private static void Postfix(Projectile __instance) =>
        ProbeBehaviour.Instance?.RegisterProjectile(__instance);
}
