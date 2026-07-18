using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class BossReplicator
{
    private readonly record struct FamilyMap(
        string Root,
        string DamageEntry,
        string WeakPoint,
        string PhaseEntry,
        string CompletionEntry);

    // These are native entry points from the generated IL2CPP interop, not guessed aliases.
    // The map is deliberately also the allow-list: a family without a complete ABI stays host-only.
    private static readonly FamilyMap[] FamilyMaps =
    {
        new("BossGiantSquidController", "OnTakeDamage(AttackData,DefenseData)", "bossEyeDamageable", "currentBossState", "OnDie"),
        new("BossHermitCrabController", "OnTakeDamage(AttackData,DefenseData)", "BossControllerBase", "CheckAndChangeState", "OnDie"),
        new("BossWolffishController", "OnTakeDamage(AttackData,DefenseData)", "BossControllerBase", "currentBossState", "OnDie"),
        new("BossGoblinSharkController", "SABossControllerBase native damage", "native child damageables", "BossAngryCutSceneStart", "OnDie"),
        new("BossKronosaurus", "SABossControllerBase native damage", "native child damageables", "rock-breath actions", "OnDie"),
        new("SABossAnomalocaris", "OnTakeDamage(Int32,Vector3,Boolean)", "native collision target", "chasing/angry actions", "OnDie"),
        new("BossLuscaController", "SABossControllerBase native damage", "luscaWeakPoint", "electric-damage actions", "SABossControllerBase OnDie"),
        new("GodzillaSubmarineController", "OnTakeDamage(AttackData,DefenseData)", "m_Damageable", "torpedo state", "OnDie")
    };
    private sealed class ClientTarget
    {
        internal BossControllerBase Boss;
        internal Vector3 Position;
        internal uint Tick;
        internal bool WasEnabled;
        internal int OriginalHp;
        internal int OriginalMaxHp;
        internal int AuthoritativeHp;
        internal Vector3 OriginalPosition;
        internal Animator Animator;
        internal readonly List<BehaviourState> Behaviours = new();
        internal readonly List<Collider2DState> Colliders = new();
    }

    private readonly record struct BehaviourState(MonoBehaviour Behaviour, bool Enabled);
    private readonly record struct Collider2DState(Collider2D Collider, bool Enabled);

    private sealed class HostOnlyTarget
    {
        internal MonoBehaviour Boss;
        internal readonly List<BehaviourState> Behaviours = new();
        internal readonly List<Collider2DState> Colliders = new();
    }

    private readonly ManualLogSource _log;
    private readonly Dictionary<uint, BossControllerBase> _hostBosses = new();
    private readonly Dictionary<uint, ClientTarget> _clientTargets = new();
    private readonly Dictionary<uint, BossState> _lastHostStates = new();
    private readonly Dictionary<uint, float> _lastHostKeyframes = new();
    private readonly Dictionary<uint, BossState> _pendingClientStates = new();
    private readonly HashSet<int> _reportedUnsupportedFamilies = new();
    private readonly HashSet<uint> _reportedAmbiguousIds = new();
    private readonly Dictionary<int, HostOnlyTarget> _hostOnlyClientTargets = new();
    private readonly HashSet<int> _reportedHostOnlyFamilies = new();
    private float _nextScan;
    private float _nextHostOnlyScan;
    private float _nextSend;
    private uint _tick;
    private bool _applyingClientState;
    private uint _activeSceneId;
    private uint _activeSceneEpoch;
    private SessionRole _activeRole;

    internal BossReplicator(ManualLogSource log) => _log = log;

    internal static void SelfTest()
    {
        var unique = new Dictionary<uint, object>();
        var duplicates = new HashSet<uint>();
        var first = new object();
        var second = new object();
        AddUniqueId(unique, duplicates, 7, first);
        AddUniqueId(unique, duplicates, 8, second);
        AddUniqueId(unique, duplicates, 7, new object());
        AddUniqueId(unique, duplicates, 7, new object());
        AddUniqueId(unique, duplicates, 0, new object());
        if (unique.Count != 1 || !unique.TryGetValue(8, out var owner) ||
            !ReferenceEquals(owner, second) || !duplicates.SetEquals(new[] { 7u }) ||
            !IsSnapshotFamily("BossGiantSquidController") ||
            IsSnapshotFamily("BossGoblinSharkController") ||
            !TryGetFamilyMap("BossGiantSquidController", out var squid) ||
            squid.WeakPoint != "bossEyeDamageable" ||
            !TryGetFamilyMap("SABossAnomalocaris", out var anomalocaris) ||
            anomalocaris.DamageEntry != "OnTakeDamage(Int32,Vector3,Boolean)" ||
            !IsKnownHostOnlyFamily("BossGoblinSharkController") ||
            !IsKnownHostOnlyFamily("SABossEbirah") ||
            !IsKnownHostOnlyFamily("GodzillaSubmarineController") ||
            !IsKnownHostOnlyFamily("BossGreatWhiteSharkController") ||
            IsKnownHostOnlyFamily("UnknownBoss"))
            throw new InvalidOperationException("Boss unique-ID policy failed");
    }

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        float deltaTime)
    {
        if (session == null || !session.Connected || !session.SceneMatches(sceneId))
            return;

        var activeEpoch = role == SessionRole.Host
            ? session.LocalSceneEpoch
            : session.RemoteSceneEpoch;
        if (_activeSceneId != sceneId || _activeSceneEpoch != activeEpoch || _activeRole != role)
        {
            Clear();
            _activeSceneId = sceneId;
            _activeSceneEpoch = activeEpoch;
            _activeRole = role;
        }

        if (role == SessionRole.Host)
        {
            if (now >= _nextScan)
            {
                _nextScan = now + 0.25f;
                ScanHost(sceneId);
            }
            // Remote damage stays fail-closed until a native equipped-weapon ABI exists.
            while (session.TryTakeBossDamageRequest(out _))
            {
            }
            if (now >= _nextSend)
            {
                _nextSend = now + 0.1f;
                SendHostStates(session, sceneId, now);
            }
            return;
        }

        if (role != SessionRole.Client)
            return;
        while (session.TryTakeBossState(out var state))
        {
            if (state.SceneId != sceneId ||
                state.SceneEpoch != session.RemoteSceneEpoch)
                continue;
            if (_pendingClientStates.TryGetValue(state.BossId, out var pending) &&
                !IsNewer(state.Tick, pending.Tick))
                continue;
            if (_clientTargets.TryGetValue(state.BossId, out var target) &&
                !IsNewer(state.Tick, target.Tick))
                continue;
            _pendingClientStates[state.BossId] = state;
        }
        if (now >= _nextScan)
        {
            _nextScan = now + 0.1f;
            BindClientTargets(sceneId);
        }
        if (now >= _nextHostOnlyScan)
        {
            _nextHostOnlyScan = now + 1f;
            BindHostOnlyClientTargets();
        }
        ApplyClientStates(deltaTime);
    }

    internal bool AllowHpWrite(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        BossControllerBase boss,
        int hp)
    {
        if (_applyingClientState || role != SessionRole.Client || session == null ||
            !session.Connected || boss == null)
            return true;
        return false;
    }

    internal bool ObserveClientDamage(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        BossControllerBase boss,
        AttackData attack)
    {
        if (_applyingClientState || role != SessionRole.Client || session == null ||
            !session.Connected || boss == null || attack == null)
            return true;
        return false;
    }

    internal bool ObserveClientHarpoon(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        BossControllerBase boss,
        Vector3 hitPosition,
        int damage)
    {
        if (_applyingClientState || role != SessionRole.Client || session == null ||
            !session.Connected || boss == null)
            return true;
        return false;
    }

    internal bool AllowClientBossMutation(
        SessionRole role,
        UdpSession session,
        BossControllerBase boss)
    {
        if (role != SessionRole.Client || session == null || !session.Connected || boss == null)
            return true;
        return false;
    }

    internal bool AllowClientFamilyMutation(SessionRole role, UdpSession session, MonoBehaviour boss)
    {
        if (role != SessionRole.Client || session == null || !session.Connected || boss == null)
            return true;
        return false;
    }

    internal void ObserveNativeTransition(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        BossControllerBase boss)
    {
        if (role != SessionRole.Host || session == null || !session.Connected || boss == null ||
            !session.SceneMatches(sceneId) || !IsSnapshotFamily(boss.GetType().Name))
            return;
        ScanHost(sceneId);
        SendHostStates(session, sceneId, Time.unscaledTime, true);
    }

    internal void Clear()
    {
        _applyingClientState = true;
        try
        {
            foreach (var target in _clientTargets.Values)
                RestoreClientTarget(target);
            foreach (var target in _hostOnlyClientTargets.Values)
                RestoreHostOnlyTarget(target);
        }
        finally
        {
            _applyingClientState = false;
        }
        _hostBosses.Clear();
        _clientTargets.Clear();
        _lastHostStates.Clear();
        _lastHostKeyframes.Clear();
        _pendingClientStates.Clear();
        _reportedUnsupportedFamilies.Clear();
        _reportedAmbiguousIds.Clear();
        _hostOnlyClientTargets.Clear();
        _reportedHostOnlyFamilies.Clear();
        _nextScan = 0f;
        _nextHostOnlyScan = 0f;
        _nextSend = 0f;
        _tick = 0;
        _activeSceneId = 0;
        _activeSceneEpoch = 0;
        _activeRole = SessionRole.Offline;
    }

    private void ScanHost(uint sceneId)
    {
        var unique = new Dictionary<uint, BossControllerBase>();
        var duplicates = new HashSet<uint>();
        foreach (var boss in UnityEngine.Object.FindObjectsByType<BossControllerBase>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (boss == null || !boss.gameObject.scene.IsValid())
                continue;
            if (!IsSnapshotFamily(boss.GetType().Name))
            {
                ReportUnsupportedFamily(boss);
                continue;
            }
            AddUniqueId(unique, duplicates, WorldObjectId.For(boss, boss.fishID), boss);
        }
        foreach (var id in duplicates)
        {
            _lastHostStates.Remove(id);
            _lastHostKeyframes.Remove(id);
            ReportAmbiguousId(id);
        }
        _hostBosses.Clear();
        foreach (var pair in unique)
            _hostBosses.Add(pair.Key, pair.Value);
    }

    private void BindClientTargets(uint sceneId)
    {
        var unique = new Dictionary<uint, BossControllerBase>();
        var duplicates = new HashSet<uint>();
        foreach (var boss in UnityEngine.Object.FindObjectsByType<BossControllerBase>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (boss == null || !boss.gameObject.scene.IsValid())
                continue;
            if (!IsSnapshotFamily(boss.GetType().Name))
            {
                ReportUnsupportedFamily(boss);
                continue;
            }
            AddUniqueId(unique, duplicates, WorldObjectId.For(boss, boss.fishID), boss);
        }
        foreach (var id in duplicates)
        {
            RemoveClientTarget(id);
            _pendingClientStates.Remove(id);
            ReportAmbiguousId(id);
        }
        foreach (var pair in unique)
        {
            var id = pair.Key;
            var boss = pair.Value;
            if (_clientTargets.ContainsKey(id))
                continue;
            _clientTargets[id] = new ClientTarget
            {
                Boss = boss,
                Position = boss.transform.position,
                WasEnabled = boss.enabled,
                OriginalHp = boss.CurrentBossHP,
                OriginalMaxHp = boss.bossMaxHP,
                AuthoritativeHp = boss.CurrentBossHP,
                OriginalPosition = boss.transform.position,
                Animator = boss.GetComponentInChildren<Animator>(true)
            };
            SuppressClientSimulation(_clientTargets[id]);
            _log.LogInfo($"Network boss bound: {boss.GetType().Name}; id={id:X8}");
        }
    }

    private void RemoveClientTarget(uint id)
    {
        if (!_clientTargets.Remove(id, out var target) || target.Boss == null)
            return;
        RestoreClientTarget(target);
    }

    private void ReportAmbiguousId(uint id)
    {
        if (_reportedAmbiguousIds.Add(id))
            _log.LogWarning($"Network boss replication disabled for duplicate world ID: {id:X8}");
    }

    private void ReportUnsupportedFamily(BossControllerBase boss)
    {
        if (boss != null && _reportedUnsupportedFamilies.Add(boss.GetInstanceID()))
            _log.LogWarning(
                $"Network boss replication disabled for unsupported family: {boss.GetType().Name}");
    }

    private static bool IsSnapshotFamily(string typeName) =>
        typeName is "BossGiantSquidController" or "BossHermitCrabController" or
            "BossWolffishController";

    private static bool IsKnownHostOnlyFamily(string typeName) => typeName is
        "BossGoblinSharkController" or "BossKronosaurus" or "SABossAnomalocaris" or
        "BossLuscaController" or "BossMantisShrimpController" or
        "BossGiantGardonController" or "BossClioneController" or
        "BossJW2Controller" or "BossJW3Controller" or "SABossEbirah" or
        "HermitCrabController" or "BossGreatWhiteSharkController" or
        "BossHelicoprionController" or "GodzillaSubmarineController";

    private static bool TryGetFamilyMap(string typeName, out FamilyMap map)
    {
        foreach (var candidate in FamilyMaps)
            if (candidate.Root == typeName)
            {
                map = candidate;
                return true;
            }
        map = default;
        return false;
    }

    private static bool IsHostOnlyBoss(MonoBehaviour boss)
    {
        if (boss == null || IsSnapshotFamily(boss.GetType().Name))
            return false;
        if (boss is BossControllerBase || IsKnownHostOnlyFamily(boss.GetType().Name))
            return true;
        for (var type = boss.GetType().BaseType; type != null; type = type.BaseType)
            if (type.Name.StartsWith("SABossControllerBase`", StringComparison.Ordinal))
                return true;
        return false;
    }

    private void BindHostOnlyClientTargets()
    {
        // One-second all-MonoBehaviour scan covers unsupported boss roots;
        // replace with direct adapters only after a native ABI is proven.
        foreach (var boss in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (boss == null || !boss.gameObject.scene.IsValid() || !IsHostOnlyBoss(boss) ||
                _hostOnlyClientTargets.ContainsKey(boss.GetInstanceID()))
                continue;
            var target = new HostOnlyTarget { Boss = boss };
            SuppressClientSimulation(boss, target.Behaviours, target.Colliders);
            _hostOnlyClientTargets.Add(boss.GetInstanceID(), target);
            if (_reportedHostOnlyFamilies.Add(boss.GetInstanceID()))
                _log.LogWarning(
                    $"Network boss is host-only until its native damage ABI is proven: " +
                    boss.GetType().Name);
        }
    }

    private static void SuppressClientSimulation(ClientTarget target) =>
        SuppressClientSimulation(target.Boss, target.Behaviours, target.Colliders);

    private static void SuppressClientSimulation(
        BossControllerBase boss,
        List<BehaviourState> behaviours,
        List<Collider2DState> colliders) =>
        SuppressClientSimulation((MonoBehaviour)boss, behaviours, colliders);

    private static void SuppressClientSimulation(
        MonoBehaviour boss,
        List<BehaviourState> behaviours,
        List<Collider2DState> colliders)
    {
        foreach (var behaviour in boss.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null)
                continue;
            behaviours.Add(new BehaviourState(behaviour, behaviour.enabled));
            behaviour.enabled = false;
        }
        foreach (var collider in boss.GetComponentsInChildren<Collider2D>(true))
        {
            if (collider == null)
                continue;
            colliders.Add(new Collider2DState(collider, collider.enabled));
            collider.enabled = false;
        }
    }

    private void RestoreClientTarget(ClientTarget target)
    {
        if (target.Boss == null)
            return;
        var applying = _applyingClientState;
        _applyingClientState = true;
        try
        {
            RestoreSimulation(target.Behaviours, target.Colliders);
            target.Boss.bossMaxHP = target.OriginalMaxHp;
            target.Boss.CurrentBossHP = target.OriginalHp;
            target.Boss.transform.position = target.OriginalPosition;
        }
        finally
        {
            _applyingClientState = applying;
        }
    }

    private static void RestoreHostOnlyTarget(HostOnlyTarget target) =>
        RestoreSimulation(target.Behaviours, target.Colliders);

    private static void RestoreSimulation(
        List<BehaviourState> behaviours,
        List<Collider2DState> colliders)
    {
        foreach (var state in colliders)
            if (state.Collider != null)
                state.Collider.enabled = state.Enabled;
        foreach (var state in behaviours)
            if (state.Behaviour != null)
                state.Behaviour.enabled = state.Enabled;
    }

    private static void AddUniqueId<T>(
        IDictionary<uint, T> unique,
        ISet<uint> duplicates,
        uint id,
        T value)
    {
        if (id == 0 || duplicates.Contains(id))
            return;
        if (unique.ContainsKey(id))
        {
            unique.Remove(id);
            duplicates.Add(id);
            return;
        }
        unique.Add(id, value);
    }

    private void SendHostStates(UdpSession session, uint sceneId, float now, bool force = false)
    {
        _tick = _tick == uint.MaxValue ? 1 : _tick + 1;
        foreach (var pair in _hostBosses)
        {
            var boss = pair.Value;
            if (boss == null)
                continue;
            var position = boss.transform.position;
            var maxHp = Math.Max(1, boss.bossMaxHP);
            var hp = Math.Clamp(boss.CurrentBossHP, 0, maxHp);
            var animator = boss.GetComponentInChildren<Animator>(true);
            var animationHash = 0;
            var animationTime = 0f;
            if (animator != null && animator.layerCount > 0)
            {
                var animation = animator.IsInTransition(0)
                    ? animator.GetNextAnimatorStateInfo(0)
                    : animator.GetCurrentAnimatorStateInfo(0);
                animationHash = animation.fullPathHash;
                animationTime = Mathf.Repeat(animation.normalizedTime, 1f);
            }
            var state = new BossState(
                sceneId, session.LocalSceneEpoch, _tick, pair.Key,
                Math.Max(0, boss.fishID), hp, maxHp,
                position.x, position.y, position.z,
                animationHash, animationTime,
                PhaseFor(boss),
                hp == 0 || boss.IsDeadBoss() ? (byte)1 : (byte)0);
            _lastHostStates.TryGetValue(pair.Key, out var previous);
            _lastHostKeyframes.TryGetValue(pair.Key, out var lastKeyframe);
            if (!force && Same(previous, state) && now - lastKeyframe < 1f)
                continue;
            _lastHostStates[pair.Key] = state;
            _lastHostKeyframes[pair.Key] = now;
            session.SendBossState(state);
        }
    }

    private void ApplyClientStates(float deltaTime)
    {
        _applyingClientState = true;
        try
        {
            foreach (var pair in _pendingClientStates)
            {
                if (!_clientTargets.TryGetValue(pair.Key, out var target) || target.Boss == null)
                    continue;
                var state = pair.Value;
                target.Tick = state.Tick;
                target.Position = new Vector3(state.X, state.Y, state.Z);
                target.Boss.bossMaxHP = state.MaxHp;
                target.AuthoritativeHp = state.CurrentHp;
                target.Boss.CurrentBossHP = state.CurrentHp;
                ApplyAnimation(target.Animator, state.AnimationHash, state.AnimationTime);
            }
        }
        finally
        {
            _applyingClientState = false;
        }
        foreach (var id in _clientTargets.Keys)
            _pendingClientStates.Remove(id);

        var blend = Mathf.Clamp01(deltaTime * 12f);
        foreach (var target in _clientTargets.Values)
            if (target.Boss != null && target.Tick != 0)
                target.Boss.transform.position = Vector3.Lerp(
                    target.Boss.transform.position, target.Position, blend);
    }

    private static bool Same(BossState left, BossState right) =>
        left.SceneId == right.SceneId && left.SceneEpoch == right.SceneEpoch &&
        left.BossId == right.BossId && left.FishId == right.FishId &&
        left.CurrentHp == right.CurrentHp && left.MaxHp == right.MaxHp && left.Flags == right.Flags &&
        left.Phase == right.Phase &&
        left.AnimationHash == right.AnimationHash &&
        MathF.Abs(left.X - right.X) < 0.02f && MathF.Abs(left.Y - right.Y) < 0.02f &&
        MathF.Abs(left.Z - right.Z) < 0.02f;

    private static void ApplyAnimation(Animator animator, int hash, float time)
    {
        if (animator == null || hash == 0 || animator.layerCount == 0)
            return;
        var current = animator.GetCurrentAnimatorStateInfo(0);
        if (current.fullPathHash == hash &&
            AnimationDistance(Mathf.Repeat(current.normalizedTime, 1f), time) < 0.2f)
            return;
        animator.Play(hash, 0, time);
    }

    private static float AnimationDistance(float left, float right)
    {
        var difference = MathF.Abs(left - right);
        return MathF.Min(difference, 1f - difference);
    }

    private static bool IsNewer(uint value, uint previous) =>
        unchecked((int)(value - previous)) > 0;

    // Telemetry only: applying phase/death needs a family-specific native adapter.
    private static int PhaseFor(BossControllerBase boss) => boss switch
    {
        BossGiantSquidController squid => Math.Max(0, (int)squid.currentBossState),
        BossWolffishController wolffish => Math.Max(0, (int)wolffish.currentBossState),
        _ => 0
    };

}

[HarmonyPatch(typeof(BossControllerBase), nameof(BossControllerBase.CurrentBossHP), MethodType.Setter)]
internal static class BossHpAuthorityPatch
{
    private static bool Prefix(BossControllerBase __instance, int __0) =>
        ProbeBehaviour.Instance?.AllowBossHpWrite(__instance, __0) ?? true;
}

[HarmonyPatch(typeof(BossControllerBase), nameof(BossControllerBase.OnTakeDamage))]
internal static class BossDamageAuthorityPatch
{
    private static bool Prefix(BossControllerBase __instance, AttackData __0, ref bool __result)
    {
        if (ProbeBehaviour.Instance?.ObserveBossDamage(__instance, __0) ?? true)
            return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(BossControllerBase), nameof(BossControllerBase.OnReflectProjectile))]
internal static class BossReflectAuthorityPatch
{
    private static bool Prefix(BossControllerBase __instance, ref Vector2 __result)
    {
        if (ProbeBehaviour.Instance?.AllowBossMutation(__instance) ?? true)
            return true;
        __result = Vector2.zero;
        return false;
    }
}

[HarmonyPatch]
internal static class SnapshotBossDamagePatch
{
    // Generic family hooks are not ABI-safe on this game build; keep each family host-only
    // until its native callback contract has been verified.
    private static bool Prepare() => false;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in SnapshotTypes)
        {
            var method = AccessTools.Method(type, nameof(BossControllerBase.OnTakeDamage),
                new[] { typeof(AttackData), typeof(DefenseData) });
            if (method != null)
                yield return method;
        }
    }

    private static bool Prefix(BossControllerBase __instance, AttackData __0, ref bool __result)
    {
        if (ProbeBehaviour.Instance?.ObserveBossDamage(__instance, __0) ?? true)
            return true;
        __result = false;
        return false;
    }

    private static void Postfix(BossControllerBase __instance) =>
        ProbeBehaviour.Instance?.ObserveBossTransition(__instance);

    private static readonly Type[] SnapshotTypes =
    {
        typeof(BossGiantSquidController), typeof(BossHermitCrabController),
        typeof(BossWolffishController)
    };
}

[HarmonyPatch]
internal static class SnapshotBossTransitionPatch
{
    private static bool Prepare() => false;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in SnapshotTypes)
        foreach (var name in new[] { nameof(BossControllerBase.CheckAndChangeState), nameof(BossControllerBase.OnDie) })
        {
            var method = AccessTools.Method(type, name);
            if (method != null)
                yield return method;
        }
    }

    private static bool Prefix(BossControllerBase __instance) =>
        ProbeBehaviour.Instance?.AllowBossMutation(__instance) ?? true;

    private static void Postfix(BossControllerBase __instance) =>
        ProbeBehaviour.Instance?.ObserveBossTransition(__instance);

    private static readonly Type[] SnapshotTypes =
    {
        typeof(BossGiantSquidController), typeof(BossHermitCrabController),
        typeof(BossWolffishController)
    };
}

[HarmonyPatch]
internal static class KnownBossFamilyAuthorityPatch
{
    private static bool Prepare() => false;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in FamilyTypes)
        {
            var type = AccessTools.TypeByName(name);
            if (type == null)
                continue;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (method.Name is "OnTakeDamage" or "OnDie" or "CheckAndChangeState" or
                    "OnHitHarpoon" or "OnReflectProjectile")
                    yield return method;
        }
    }

    private static bool Prefix(MonoBehaviour __instance) =>
        ProbeBehaviour.Instance?.AllowBossFamilyMutation(__instance) ?? true;

    private static readonly string[] FamilyTypes =
    {
        "BossGoblinSharkController", "BossKronosaurus", "SABossAnomalocaris",
        "BossLuscaController", "BossMantisShrimpController", "BossGiantGardonController",
        "BossClioneController", "BossJW2Controller", "BossJW3Controller",
        "SABossEbirah", "BossGreatWhiteSharkController", "BossHelicoprionController",
        "GodzillaSubmarineController"
    };
}
