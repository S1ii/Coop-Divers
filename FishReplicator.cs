using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DR.AI;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class FishReplicator
{
    private sealed class Target
    {
        internal FishAISystem Fish;
        internal Vector3 Position;
        internal float Rotation;
        internal float Hp;
        internal byte Flags;
        internal bool HasSnapshot;
    }

    private readonly ManualLogSource _log;
    private readonly Dictionary<int, FishAISystem> _hostFishById = new();
    private readonly Dictionary<int, Target> _targets = new();
    private readonly HashSet<int> _missedIds = new();
    private readonly HashSet<int> _removedIds = new();
    private float _nextHostScan;
    private float _nextClientScan;
    private float _nextSend;
    private int _lastHostCount = -1;
    private int _lastHostDuplicates = -1;
    private int _lastClientCount = -1;
    private int _lastClientDuplicates = -1;
    private int _lastClientMissing = -1;

    internal FishReplicator(ManualLogSource log) => _log = log;

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        float deltaTime,
        PlayerCharacter hostPlayer)
    {
        if (role == SessionRole.Host)
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            UpdateHost(session, sceneId, now, hostPlayer);
            return;
        }

        while (session.TryTakeFishDamageRequest(out _))
        {
        }
        while (session.TryTakeFishPickupRequest(out _))
        {
        }

        if (role != SessionRole.Client || !session.SceneMatches(sceneId))
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            _targets.Clear();
            return;
        }

        if (now >= _nextClientScan)
            RefreshClientFish(sceneId, now);

        while (session.TryTakeFishRemoved(out var removed))
        {
            if (removed.SceneId != sceneId || !_targets.TryGetValue(removed.Id, out var target))
            {
                if (removed.SceneId == sceneId)
                    _removedIds.Add(removed.Id);
                continue;
            }
            _removedIds.Add(removed.Id);
            if (target.Fish != null)
                target.Fish.DestroySelf();
            _targets.Remove(removed.Id);
        }

        while (session.TryTakeFishSnapshot(out var snapshot))
        {
            if (snapshot.SceneId != sceneId)
                continue;
            if (_removedIds.Contains(snapshot.Id))
                continue;
            if (!_targets.TryGetValue(snapshot.Id, out var target))
            {
                _missedIds.Add(snapshot.Id);
                continue;
            }

            target.Position = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
            target.Rotation = snapshot.Rotation;
            target.Hp = snapshot.Hp;
            target.Flags = snapshot.Flags;
            target.HasSnapshot = true;
            ApplyFlags(target);
        }

        var blend = 1f - Mathf.Exp(-14f * deltaTime);
        foreach (var target in _targets.Values)
        {
            if (!target.HasSnapshot || target.Fish == null)
                continue;
            // Soft authority keeps client attacks/animations alive;
            // disable client AI once host-side combat events are replicated.
            target.Fish.transform.position = Vector3.Lerp(
                target.Fish.transform.position, target.Position, blend);
            target.Fish.Rotation = Mathf.LerpAngle(
                target.Fish.Rotation, target.Rotation, blend);
            if (Mathf.Abs(target.Fish.HP - target.Hp) > 0.01f)
                target.Fish.SetHP(target.Hp);
        }
    }

    internal bool RequestDamage(
        UdpSession session,
        uint sceneId,
        FishAISystem fish,
        int damage,
        EElement element,
        AttackType attackType)
    {
        if (fish == null || damage <= 0 || !IsPlayerAttack(attackType))
            return false;
        session.SendFishDamageRequest(new FishDamageRequest(
            sceneId, NetworkId(sceneId, fish), Mathf.Clamp(damage, 1, 10_000),
            (int)element, (int)attackType));
        return true;
    }

    internal void RequestPickup(UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (fish != null)
            session.SendFishPickupRequest(new FishPickupRequest(sceneId, NetworkId(sceneId, fish)));
    }

    internal void Clear()
    {
        _hostFishById.Clear();
        _targets.Clear();
        _missedIds.Clear();
        _removedIds.Clear();
        _nextHostScan = 0f;
        _nextClientScan = 0f;
        _nextSend = 0f;
        _lastHostCount = -1;
        _lastHostDuplicates = -1;
        _lastClientCount = -1;
        _lastClientDuplicates = -1;
        _lastClientMissing = -1;
    }

    private void UpdateHost(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer)
    {
        if (!session.SceneMatches(sceneId))
        {
            while (session.TryTakeFishDamageRequest(out _))
            {
            }
            while (session.TryTakeFishPickupRequest(out _))
            {
            }
            return;
        }
        if (now >= _nextHostScan)
            RefreshHostFish(sceneId, now);

        while (session.TryTakeFishDamageRequest(out var damageRequest))
            ApplyHostDamage(session, sceneId, now, damageRequest);
        while (session.TryTakeFishPickupRequest(out var pickupRequest))
            ApplyHostPickup(session, sceneId, now, hostPlayer, pickupRequest);

        if (now < _nextSend)
            return;

        _nextSend = now + 0.2f;
        foreach (var pair in _hostFishById)
        {
            var fish = pair.Value;
            if (fish == null || !fish.gameObject.activeInHierarchy)
                continue;
            var position = fish.transform.position;
            byte flags = 0;
            if (fish.IsCorpse)
                flags |= 1;
            if (fish.IsFishCaptured)
                flags |= 2;
            if (fish.IsFishEnable)
                flags |= 4;
            session.SendFishSnapshot(new FishSnapshot(
                sceneId, pair.Key, position.x, position.y, position.z, fish.Rotation,
                Mathf.Max(0f, fish.HP), flags));
        }
    }

    private void RefreshHostFish(uint sceneId, float now)
    {
        _nextHostScan = now + 1f;
        _hostFishById.Clear();
        var duplicates = new HashSet<int>();
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(FindObjectsSortMode.None))
        {
            if (fish == null)
                continue;
            var networkId = NetworkId(sceneId, fish);
            if (!_hostFishById.TryAdd(networkId, fish))
                duplicates.Add(networkId);
        }
        foreach (var duplicate in duplicates)
            _hostFishById.Remove(duplicate);
        if (_lastHostCount != _hostFishById.Count || _lastHostDuplicates != duplicates.Count)
        {
            _log.LogInfo(
                $"Network fish host: {_hostFishById.Count} unique; {duplicates.Count} duplicate IDs");
            _lastHostCount = _hostFishById.Count;
            _lastHostDuplicates = duplicates.Count;
        }
    }

    private void ApplyHostDamage(
        UdpSession session,
        uint sceneId,
        float now,
        FishDamageRequest request)
    {
        if (request.SceneId != sceneId || !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
            return;
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        if (fish == null || fish.IsCorpse || fish.IsFishCaptured ||
            !Enum.IsDefined(typeof(EElement), request.Element) ||
            !IsPlayerAttack((AttackType)request.AttackType) ||
            !InRange(fish.transform.position, remotePlayer, 1600f))
        {
            _log.LogWarning($"Network fish damage rejected: id={request.Id}");
            return;
        }

        if ((AttackType)request.AttackType == AttackType.QTE_Damage)
            fish.SetHPDamageQTE(request.Damage, (EElement)request.Element);
        else
            fish.SetHPDamage(request.Damage, (EElement)request.Element, (AttackType)request.AttackType);
        _log.LogDebug($"Network fish damage: id={request.Id}; damage={request.Damage}; hp={fish.HP:F1}");
    }

    private void ApplyHostPickup(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        FishPickupRequest request)
    {
        if (request.SceneId != sceneId || hostPlayer == null ||
            !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
            return;
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        var body = fish != null ? fish.GetInteractionBody : null;
        if (fish == null || body == null || !body.IsEnableInteraction ||
            !InRange(fish.transform.position, remotePlayer, 16f))
        {
            _log.LogWarning($"Network fish pickup rejected: id={request.Id}");
            return;
        }

        body.SuccessInteract(hostPlayer);
        session.SendFishRemoved(new FishRemoved(sceneId, request.Id));
        _hostFishById.Remove(request.Id);
        _log.LogInfo($"Network fish pickup accepted: id={request.Id}");
    }

    private static bool TryGetRemotePlayer(
        UdpSession session,
        uint sceneId,
        float now,
        out PlayerSnapshot remotePlayer) =>
        session.TryGetFreshRemotePlayerSnapshot(now, 0.75f, out remotePlayer) &&
            remotePlayer.SceneId == sceneId;

    private static bool InRange(Vector3 position, PlayerSnapshot player, float maxSquaredDistance)
    {
        var dx = position.x - player.X;
        var dy = position.y - player.Y;
        return dx * dx + dy * dy <= maxSquaredDistance;
    }

    internal static bool IsPlayerAttack(AttackType attackType) =>
        attackType is AttackType.Player_All or AttackType.Player_Gun or
            AttackType.Player_Harpoon or AttackType.Player_Melee or
            AttackType.Player_Harpoon_Interaction or AttackType.QTE_Damage or
            AttackType.Player_SubHelper;

    private void RefreshClientFish(uint sceneId, float now)
    {
        _nextClientScan = now + 1f;
        var fishes = new Dictionary<int, FishAISystem>();
        var duplicates = new HashSet<int>();
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(FindObjectsSortMode.None))
        {
            if (fish == null)
                continue;
            var networkId = NetworkId(sceneId, fish);
            if (_removedIds.Contains(networkId))
                continue;
            if (!fishes.TryAdd(networkId, fish))
                duplicates.Add(networkId);
        }
        foreach (var duplicate in duplicates)
            fishes.Remove(duplicate);

        var nextTargets = new Dictionary<int, Target>(fishes.Count);
        foreach (var pair in fishes)
        {
            if (_targets.TryGetValue(pair.Key, out var target) && target.Fish == pair.Value)
                nextTargets.Add(pair.Key, target);
            else
                nextTargets.Add(pair.Key, new Target { Fish = pair.Value });
        }
        _targets.Clear();
        foreach (var pair in nextTargets)
            _targets.Add(pair.Key, pair.Value);

        if (_lastClientCount != _targets.Count ||
            _lastClientDuplicates != duplicates.Count || _lastClientMissing != _missedIds.Count)
        {
            _log.LogInfo(
                $"Network fish client: {_targets.Count} unique; {duplicates.Count} duplicate IDs; " +
                $"{_missedIds.Count} host IDs missing");
            _lastClientCount = _targets.Count;
            _lastClientDuplicates = duplicates.Count;
            _lastClientMissing = _missedIds.Count;
            _missedIds.Clear();
        }
    }

    private static void ApplyFlags(Target target)
    {
        var corpse = (target.Flags & 1) != 0;
        var captured = (target.Flags & 2) != 0;
        var enabled = (target.Flags & 4) != 0;
        if (target.Fish.IsCorpse != corpse)
            target.Fish.IsCorpse = corpse;
        if (target.Fish.IsFishCaptured != captured)
            target.Fish.IsFishCaptured = captured;
        if (target.Fish.IsFishEnable != enabled)
            target.Fish.IsFishEnable = enabled;
    }

    private static int NetworkId(uint sceneId, FishAISystem fish)
    {
        var initial = fish.InitialPosition;
        var hash = Mix(Mix(2166136261u, unchecked((int)sceneId)), fish.ID);
        hash = Mix(hash, Mathf.RoundToInt(initial.x * 100f));
        hash = Mix(hash, Mathf.RoundToInt(initial.y * 100f));
        for (var current = fish.transform; current != null; current = current.parent)
        {
            hash = Mix(hash, unchecked((int)Protocol.SceneId(current.name)));
            if (current.parent != null)
                hash = Mix(hash, current.GetSiblingIndex());
        }
        return unchecked((int)hash);
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

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetHPDamage))]
internal static class FishDamagePatch
{
    private static bool Prefix(
        FishAISystem __instance,
        int damage,
        EElement element,
        AttackType attackType)
    {
        var behaviour = ProbeBehaviour.Instance;
        return behaviour?.AllowFishDamage(__instance, damage, element, attackType) ?? true;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetHPDamageQTE))]
internal static class FishQteDamagePatch
{
    private static bool Prefix(FishAISystem __instance, int damage, EElement element)
    {
        var behaviour = ProbeBehaviour.Instance;
        return behaviour?.AllowFishDamage(
            __instance, damage, element, AttackType.QTE_Damage) ?? true;
    }
}

[HarmonyPatch(typeof(FishInteractionBody), nameof(FishInteractionBody.SuccessInteract))]
internal static class FishPickupPatch
{
    private static bool Prefix(FishInteractionBody __instance) =>
        ProbeBehaviour.Instance?.AllowFishPickup(__instance) ?? true;
}
