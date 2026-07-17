using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class BossReplicator
{
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
    }

    private readonly ManualLogSource _log;
    private readonly Dictionary<uint, BossControllerBase> _hostBosses = new();
    private readonly Dictionary<uint, ClientTarget> _clientTargets = new();
    private readonly Dictionary<uint, BossState> _lastHostStates = new();
    private readonly Dictionary<uint, float> _lastHostKeyframes = new();
    private readonly Dictionary<uint, BossState> _pendingClientStates = new();
    private readonly HashSet<uint> _unsupportedClientDamage = new();
    private readonly HashSet<uint> _reportedAmbiguousIds = new();
    private float _nextScan;
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
            !ReferenceEquals(owner, second) || !duplicates.SetEquals(new[] { 7u }))
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
            while (session.TryTakeBossDamageRequest(out var request))
                RejectUnsupportedHostDamage(session, sceneId, request);
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
        foreach (var pair in _clientTargets)
        {
            var target = pair.Value;
            if (target.Boss != boss)
                continue;
            if (hp < target.AuthoritativeHp)
                session.SendBossDamageRequest(new BossDamageRequest(
                    sceneId, session.RemoteSceneEpoch, pair.Key,
                    Math.Clamp(target.AuthoritativeHp - hp, 1, 10_000),
                    0, (int)AttackType.Player_All));
            return false;
        }
        return true;
    }

    internal void Clear()
    {
        _applyingClientState = true;
        try
        {
            foreach (var target in _clientTargets.Values)
                if (target.Boss != null)
                {
                    target.Boss.enabled = target.WasEnabled;
                    target.Boss.bossMaxHP = target.OriginalMaxHp;
                    target.Boss.CurrentBossHP = target.OriginalHp;
                    target.Boss.transform.position = target.OriginalPosition;
                }
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
        _unsupportedClientDamage.Clear();
        _reportedAmbiguousIds.Clear();
        _nextScan = 0f;
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
            AddUniqueId(unique, duplicates, WorldObjectId.For(boss, boss.fishID), boss);
        }
        foreach (var id in duplicates)
        {
            _lastHostStates.Remove(id);
            _lastHostKeyframes.Remove(id);
            _unsupportedClientDamage.Remove(id);
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
            boss.enabled = false;
            _log.LogInfo($"Network boss bound: {boss.GetType().Name}; id={id:X8}");
        }
    }

    private void RemoveClientTarget(uint id)
    {
        if (!_clientTargets.Remove(id, out var target) || target.Boss == null)
            return;
        var applying = _applyingClientState;
        _applyingClientState = true;
        try
        {
            target.Boss.enabled = target.WasEnabled;
            target.Boss.bossMaxHP = target.OriginalMaxHp;
            target.Boss.CurrentBossHP = target.OriginalHp;
            target.Boss.transform.position = target.OriginalPosition;
        }
        finally
        {
            _applyingClientState = applying;
        }
    }

    private void ReportAmbiguousId(uint id)
    {
        if (_reportedAmbiguousIds.Add(id))
            _log.LogWarning($"Network boss replication disabled for duplicate world ID: {id:X8}");
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

    private void RejectUnsupportedHostDamage(
        UdpSession session,
        uint sceneId,
        BossDamageRequest request)
    {
        if (request.SceneId != sceneId || request.SceneEpoch != session.LocalSceneEpoch ||
            !_hostBosses.TryGetValue(request.BossId, out var boss) ||
            boss == null || !FishReplicator.IsPlayerAttack((AttackType)request.AttackType))
            return;
        if (_unsupportedClientDamage.Add(request.BossId))
            _log.LogWarning(
                $"Network boss damage rejected until a native family adapter exists: " +
                $"{boss.GetType().Name}; id={request.BossId:X8}");
    }

    private void SendHostStates(UdpSession session, uint sceneId, float now)
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
                hp == 0 || boss.IsDeadBoss() ? (byte)1 : (byte)0);
            _lastHostStates.TryGetValue(pair.Key, out var previous);
            _lastHostKeyframes.TryGetValue(pair.Key, out var lastKeyframe);
            if (Same(previous, state) && now - lastKeyframe < 1f)
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
}

[HarmonyPatch(typeof(BossControllerBase), nameof(BossControllerBase.CurrentBossHP), MethodType.Setter)]
internal static class BossHpAuthorityPatch
{
    private static bool Prefix(BossControllerBase __instance, int __0) =>
        ProbeBehaviour.Instance?.AllowBossHpWrite(__instance, __0) ?? true;
}
