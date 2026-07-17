using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WeaponSystem;

namespace DaveTheDiverMP;

internal sealed class ProjectileVisualReplicator
{
    private const int MaxActiveProjectiles = 64;
    private const int MaxDrainingProjectiles = 64;
    private const int MaxParticleSystemsPerRemoteProjectile = 0;
    private const int MaxTrailsPerRemoteProjectile = 1;
    private const float MinTrailLifetime = 0.05f;
    private const float MaxTrailLifetime = 0.15f;
    private const float MaxTrailWidth = 0.035f;
    private const float MaxTrailVertexDistance = 0.1f;
    private const int MaxTrailEndVertices = 2;

    private sealed class RemoteProjectile
    {
        internal GameObject Root;
        internal SpriteRenderer Renderer;
        internal LineRenderer Rope;
        internal float LastSeen;
    }

    private readonly record struct NormalizedVisual(
        float Rotation, float ScaleX, float ScaleY, bool Flipped);

    private readonly Dictionary<int, Component> _local = new();
    private readonly Dictionary<int, RemoteProjectile> _remote = new();
    private readonly Dictionary<uint, Sprite> _sprites = new();
    private readonly Dictionary<uint, Component> _templates = new();
    private readonly List<int> _staleIds = new();
    private readonly Queue<GameObject> _draining = new();
    private Material _ropeMaterial;
    private float _nextSend;
    private bool _resourcesScanned;

    internal static void SelfTest()
    {
        if (AtCapacity(63, MaxActiveProjectiles) ||
            !AtCapacity(64, MaxActiveProjectiles) ||
            !AtCapacity(64, MaxDrainingProjectiles) ||
            MaxParticleSystemsPerRemoteProjectile != 0 ||
            MaxTrailsPerRemoteProjectile != 1 ||
            MinTrailLifetime <= 0f || MaxTrailLifetime < MinTrailLifetime ||
            MaxTrailLifetime > 0.15f || MaxTrailWidth <= 0f || MaxTrailWidth > 0.05f ||
            MaxTrailVertexDistance <= 0f || MaxTrailEndVertices < 0 ||
            MaxTrailEndVertices > 2)
            throw new InvalidOperationException("Projectile visual limit self-test failed");
        foreach (var rotation in new[] { 25f, 205f })
        foreach (var flip in new[] { false, true })
        foreach (var negativeScale in new[] { false, true })
        foreach (var scaleY in new[] { -3f, 3f })
        {
            var normalized = NormalizeVisual(
                rotation, negativeScale ? -2f : 2f, scaleY, flip);
            if (normalized.Rotation != rotation || normalized.ScaleX != 2f ||
                normalized.ScaleY != 3f || normalized.Flipped != (flip ^ negativeScale) ||
                GetRemoteScale(normalized) != new Vector3(2f, 3f, 1f))
                throw new InvalidOperationException("Projectile orientation self-test failed");
        }
    }

    internal void Register(Component projectile)
    {
        if (projectile != null)
        {
            _local[projectile.GetInstanceID()] = projectile;
            CacheTemplate(projectile);
        }
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
                DrainAndDestroy(pair.Value);
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
            if (remote.Root != null)
                UnityEngine.Object.Destroy(remote.Root);
        _remote.Clear();
        while (_draining.Count > 0)
        {
            var draining = _draining.Dequeue();
            if (draining != null)
                UnityEngine.Object.Destroy(draining);
        }
        if (_ropeMaterial != null)
            UnityEngine.Object.Destroy(_ropeMaterial);
        _ropeMaterial = null;
    }

    private void Apply(ProjectileVisualState state, float now)
    {
        if (!_remote.TryGetValue(state.Id, out var remote))
        {
            if (AtCapacity(_remote.Count, MaxActiveProjectiles))
                DrainOldestRemote();
            var visual = new GameObject("DTMP Remote Projectile");
            ApplyTransform(visual.transform, state);
            var createdRenderer = visual.AddComponent<SpriteRenderer>();
            createdRenderer.sprite = ResolveSprite(state.SpriteId);
            var hasNativeTrail = false;
            if (TryResolveTemplate(state.SpriteId, out var template, out var source))
            {
                TryCopy(() => createdRenderer.color = source.color);
                TryCopy(() => createdRenderer.sharedMaterial = source.sharedMaterial);
                hasNativeTrail = TryCreateNativeTrail(visual, template, source, state);
            }
            remote = new RemoteProjectile
            {
                Root = visual,
                Renderer = createdRenderer,
                Rope = CreateRope(visual)
            };
            if (!hasNativeTrail)
                CreateFallbackTrail(visual, createdRenderer);
            _remote.Add(state.Id, remote);
        }
        var renderer = remote.Renderer;
        renderer.sprite = ResolveSprite(state.SpriteId);
        renderer.flipX = state.Flipped;
        ApplySorting(remote.Root, renderer, state.SortingLayerId, state.SortingOrder);
        ApplyTransform(remote.Root.transform, state);
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

    private static bool TryCreateNativeTrail(
        GameObject owner, Component template, SpriteRenderer sourceRenderer,
        ProjectileVisualState state)
    {
        var source = template.GetComponentInChildren<TrailRenderer>(true);
        if (source == null)
            return false;
        var effect = CreateEffectObject(owner, sourceRenderer.transform, source.transform, "Trail");
        CopyTrailRenderer(source, effect.AddComponent<TrailRenderer>(), state,
            sourceRenderer.sortingOrder);
        return true;
    }

    private static GameObject CreateEffectObject(
        GameObject owner, Transform origin, Transform source, string name)
    {
        var effect = new GameObject(name);
        effect.transform.SetParent(owner.transform, false);
        effect.transform.localPosition = origin.InverseTransformPoint(source.position);
        effect.transform.localRotation = Quaternion.Inverse(origin.rotation) * source.rotation;
        return effect;
    }

    private static void CopyTrailRenderer(
        TrailRenderer source, TrailRenderer target,
        ProjectileVisualState state, int sourceOrder)
    {
        target.time = Mathf.Clamp(source.time, MinTrailLifetime, MaxTrailLifetime);
        target.minVertexDistance = Mathf.Clamp(source.minVertexDistance, 0.01f,
            MaxTrailVertexDistance);
        target.startWidth = MaxTrailWidth;
        target.endWidth = 0f;
        TryCopy(() => target.colorGradient = source.colorGradient);
        target.numCornerVertices = Mathf.Min(source.numCornerVertices, MaxTrailEndVertices);
        target.numCapVertices = Mathf.Min(source.numCapVertices, MaxTrailEndVertices);
        TryCopy(() => target.alignment = source.alignment);
        TryCopy(() => target.textureMode = source.textureMode);
        TryCopy(() => target.generateLightingData = source.generateLightingData);
        TryCopy(() => target.sharedMaterial = source.sharedMaterial);
        target.autodestruct = false;
        target.emitting = true;
        target.sortingLayerID = state.SortingLayerId;
        target.sortingOrder = source.sortingOrder - sourceOrder;
    }

    private static void ApplyTransform(Transform transform, ProjectileVisualState state)
    {
        transform.position = new Vector3(state.X, state.Y, state.Z);
        transform.rotation = Quaternion.Euler(0f, 0f, state.Rotation);
        transform.localScale = GetRemoteScale(new NormalizedVisual(
            state.Rotation, Mathf.Abs(state.ScaleX), state.ScaleY, state.Flipped));
    }

    private static void TryCopy(Action copy)
    {
        try
        {
            copy();
        }
        catch (Exception)
        {
            // Some IL2CPP Unity versions expose module properties that throw when unsupported.
        }
    }

    private static void ApplySorting(
        GameObject root, SpriteRenderer renderer, int layerId, int order)
    {
        var previousOrder = renderer.sortingOrder;
        foreach (var childRenderer in root.GetComponentsInChildren<Renderer>(true))
        {
            childRenderer.sortingLayerID = layerId;
            childRenderer.sortingOrder = order + childRenderer.sortingOrder - previousOrder;
        }
    }

    private void CreateFallbackTrail(GameObject owner, SpriteRenderer renderer)
    {
        var trail = owner.AddComponent<TrailRenderer>();
        trail.time = MaxTrailLifetime;
        trail.startWidth = MaxTrailWidth;
        trail.endWidth = 0f;
        trail.startColor = renderer.color;
        trail.endColor = new Color(renderer.color.r, renderer.color.g, renderer.color.b, 0f);
        trail.sharedMaterial = renderer.sharedMaterial != null ? renderer.sharedMaterial : _ropeMaterial;
        trail.emitting = true;
    }

    private void DrainOldestRemote()
    {
        var oldestId = 0;
        RemoteProjectile oldest = null;
        foreach (var pair in _remote)
            if (oldest == null || pair.Value.LastSeen < oldest.LastSeen)
            {
                oldestId = pair.Key;
                oldest = pair.Value;
            }
        if (oldest == null)
            return;
        _remote.Remove(oldestId);
        DrainAndDestroy(oldest);
    }

    private void DrainAndDestroy(RemoteProjectile remote)
    {
        if (remote.Root == null)
            return;
        remote.Renderer.enabled = false;
        remote.Rope.enabled = false;
        foreach (var trail in remote.Root.GetComponentsInChildren<TrailRenderer>(true))
            trail.emitting = false;
        while (_draining.Count > 0 && _draining.Peek() == null)
            _draining.Dequeue();
        if (AtCapacity(_draining.Count, MaxDrainingProjectiles))
        {
            var oldest = _draining.Dequeue();
            if (oldest != null)
                UnityEngine.Object.Destroy(oldest);
        }
        _draining.Enqueue(remote.Root);
        UnityEngine.Object.Destroy(remote.Root, 0.5f);
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
        EnsureResourceCache();
        return _sprites.TryGetValue(id, out var sprite) ? sprite : null;
    }

    private void CacheTemplate(Component projectile, bool overwrite = true)
    {
        var renderer = projectile.GetComponentInChildren<SpriteRenderer>(true);
        if (renderer != null && renderer.sprite != null)
        {
            var id = Protocol.SceneId(renderer.sprite.name);
            _sprites[id] = renderer.sprite;
            if (overwrite)
                _templates[id] = projectile;
            else
                _templates.TryAdd(id, projectile);
        }
    }

    private void EnsureResourceCache()
    {
        if (_resourcesScanned)
            return;
        _resourcesScanned = true;
        foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            if (sprite != null)
                _sprites.TryAdd(Protocol.SceneId(sprite.name), sprite);
        foreach (var projectile in Resources.FindObjectsOfTypeAll<Projectile>())
            if (projectile != null)
                CacheTemplate(projectile, false);
        foreach (var projectile in Resources.FindObjectsOfTypeAll<HarpoonProjectile>())
            if (projectile != null)
                CacheTemplate(projectile, false);
    }

    private bool TryResolveTemplate(
        uint spriteId, out Component template, out SpriteRenderer renderer)
    {
        EnsureResourceCache();
        if (_templates.TryGetValue(spriteId, out var cached) && cached != null &&
            TryGetTemplateRenderer(cached, spriteId, out renderer))
        {
            template = cached;
            return true;
        }
        foreach (var projectile in _local.Values)
            if (TryGetTemplateRenderer(projectile, spriteId, out renderer))
            {
                _templates[spriteId] = projectile;
                template = projectile;
                return true;
            }
        template = null;
        renderer = null;
        return false;
    }

    private static bool TryGetTemplateRenderer(
        Component projectile, uint spriteId, out SpriteRenderer renderer)
    {
        renderer = projectile != null
            ? projectile.GetComponentInChildren<SpriteRenderer>(true)
            : null;
        return renderer != null && renderer.sprite != null &&
            Protocol.SceneId(renderer.sprite.name) == spriteId;
    }

    private static NormalizedVisual NormalizeVisual(
        float worldRotation, float scaleX, float scaleY, bool flipped) =>
        new(worldRotation, Mathf.Abs(scaleX), Mathf.Abs(scaleY), flipped ^ (scaleX < 0f));

    private static Vector3 GetRemoteScale(NormalizedVisual visual) =>
        new(Mathf.Abs(visual.ScaleX), Mathf.Abs(visual.ScaleY), 1f);

    private static bool AtCapacity(int count, int capacity) => count >= capacity;

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
        var visual = NormalizeVisual(
            renderer.transform.eulerAngles.z, scale.x, scale.y, renderer.flipX);
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
            visual.Rotation, visual.ScaleX, visual.ScaleY,
            renderer.sortingLayerID, renderer.sortingOrder, visual.Flipped,
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
    private static void Prefix(HarpoonWeaponHandler __instance, bool isSuccess)
    {
        ProbeBehaviour.Instance?.TraceHarpoon("finish-recall-prefix", __instance, isSuccess);
        ProbeBehaviour.Instance?.BeginHarpoonRecall(isSuccess);
    }

    private static void Postfix(HarpoonWeaponHandler __instance, bool isSuccess)
    {
        ProbeBehaviour.Instance?.TraceHarpoon("finish-recall-postfix", __instance, isSuccess);
        ProbeBehaviour.Instance?.EndHarpoonRecall();
    }

    private static Exception Finalizer(Exception __exception)
    {
        ProbeBehaviour.Instance?.EndHarpoonRecall();
        return __exception;
    }
}

[HarmonyPatch(typeof(HarpoonWeaponHandler), nameof(HarpoonWeaponHandler.ResetWeaponResources))]
internal static class HarpoonResetTracePatch
{
    private static void Prefix(HarpoonWeaponHandler __instance)
    {
        ProbeBehaviour.Instance?.TraceHarpoon("reset-prefix", __instance);
        ProbeBehaviour.Instance?.OnHarpoonReset();
    }

    private static void Postfix(HarpoonWeaponHandler __instance) =>
        ProbeBehaviour.Instance?.TraceHarpoon("reset-postfix", __instance);
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch))]
internal static class WeaponProjectileVisualPatch
{
    private static void Postfix(Projectile __instance) =>
        ProbeBehaviour.Instance?.RegisterProjectile(__instance);
}
