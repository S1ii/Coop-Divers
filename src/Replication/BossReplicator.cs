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
        internal Vector3 OriginalPosition;
        internal Animator Animator;
    }

    private readonly ManualLogSource _log;
    private readonly Dictionary<uint, BossControllerBase> _hostBosses = new();
    private readonly Dictionary<uint, ClientTarget> _clientTargets = new();
    private readonly Dictionary<uint, BossState> _lastHostStates = new();
    private readonly Dictionary<uint, float> _lastHostKeyframes = new();
    private readonly Dictionary<uint, BossState> _pendingClientStates = new();
    private float _nextScan;
    private float _nextSend;
    private uint _tick;

    internal BossReplicator(ManualLogSource log) => _log = log;

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        float deltaTime)
    {
        if (session == null || !session.Connected || !session.SceneMatches(sceneId))
            return;

        if (role == SessionRole.Host)
        {
            if (now >= _nextScan)
            {
                _nextScan = now + 0.25f;
                ScanHost(sceneId);
            }
            while (session.TryTakeBossDamageRequest(out var request))
                ApplyHostDamage(session, sceneId, now, request);
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
            if (state.SceneId != sceneId)
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

    internal bool AllowDamage(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        BossControllerBase boss,
        AttackData attack)
    {
        if (role != SessionRole.Client || session == null || !session.Connected || boss == null)
            return true;
        try
        {
            if (attack != null && FishReplicator.IsPlayerAttack(attack.attackType))
            {
                var element = Math.Clamp((int)attack.element, 0, 32);
                session.SendBossDamageRequest(new BossDamageRequest(
                    sceneId,
                    WorldObjectId.For(sceneId, boss, boss.fishID),
                    Math.Clamp(attack.BuffedDamage, 1, 10_000),
                    element,
                    (int)attack.attackType));
            }
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network boss damage request failed: {exception.Message}");
        }
        return false;
    }

    internal void Clear()
    {
        foreach (var target in _clientTargets.Values)
            if (target.Boss != null)
            {
                target.Boss.enabled = target.WasEnabled;
                target.Boss.bossMaxHP = target.OriginalMaxHp;
                target.Boss.CurrentBossHP = target.OriginalHp;
                target.Boss.transform.position = target.OriginalPosition;
            }
        _hostBosses.Clear();
        _clientTargets.Clear();
        _lastHostStates.Clear();
        _lastHostKeyframes.Clear();
        _pendingClientStates.Clear();
        _nextScan = 0f;
        _nextSend = 0f;
        _tick = 0;
    }

    private void ScanHost(uint sceneId)
    {
        _hostBosses.Clear();
        foreach (var boss in UnityEngine.Object.FindObjectsByType<BossControllerBase>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (boss == null || !boss.gameObject.scene.IsValid())
                continue;
            _hostBosses[WorldObjectId.For(sceneId, boss, boss.fishID)] = boss;
        }
    }

    private void BindClientTargets(uint sceneId)
    {
        foreach (var boss in UnityEngine.Object.FindObjectsByType<BossControllerBase>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (boss == null || !boss.gameObject.scene.IsValid())
                continue;
            var id = WorldObjectId.For(sceneId, boss, boss.fishID);
            if (_clientTargets.ContainsKey(id))
                continue;
            _clientTargets[id] = new ClientTarget
            {
                Boss = boss,
                Position = boss.transform.position,
                WasEnabled = boss.enabled,
                OriginalHp = boss.CurrentBossHP,
                OriginalMaxHp = boss.bossMaxHP,
                OriginalPosition = boss.transform.position,
                Animator = boss.GetComponentInChildren<Animator>(true)
            };
            boss.enabled = false;
            _log.LogInfo($"Network boss bound: {boss.GetType().Name}; id={id:X8}");
        }
    }

    private void ApplyHostDamage(
        UdpSession session,
        uint sceneId,
        float now,
        BossDamageRequest request)
    {
        if (request.SceneId != sceneId || !_hostBosses.TryGetValue(request.BossId, out var boss) ||
            boss == null || !FishReplicator.IsPlayerAttack((AttackType)request.AttackType) ||
            !session.TryGetFreshRemotePlayerSnapshot(now, 0.75f, out var player) ||
            player.SceneId != sceneId || !InRange(boss.transform.position, player, 3600f))
            return;

        try
        {
            var current = Math.Max(0, boss.CurrentBossHP);
            if (current == 0 || boss.IsDeadBoss())
                return;
            var next = Math.Max(0, current - request.Damage);
            boss.CurrentBossHP = next;
            if (next == 0)
                boss.OnDie();
            _nextSend = 0f;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network boss damage apply failed: {exception.Message}");
        }
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
                sceneId, _tick, pair.Key, Math.Max(0, boss.fishID), hp, maxHp,
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
        foreach (var pair in _pendingClientStates)
        {
            if (!_clientTargets.TryGetValue(pair.Key, out var target) || target.Boss == null)
                continue;
            var state = pair.Value;
            target.Tick = state.Tick;
            target.Position = new Vector3(state.X, state.Y, state.Z);
            target.Boss.bossMaxHP = state.MaxHp;
            target.Boss.CurrentBossHP = state.CurrentHp;
            ApplyAnimation(target.Animator, state.AnimationHash, state.AnimationTime);
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

    private static bool InRange(Vector3 boss, PlayerSnapshot player, float maxSquaredDistance)
    {
        var dx = boss.x - player.X;
        var dy = boss.y - player.Y;
        return dx * dx + dy * dy <= maxSquaredDistance;
    }

    private static bool IsNewer(uint value, uint previous) =>
        unchecked((int)(value - previous)) > 0;
}

[HarmonyPatch]
internal static class BossDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(BossControllerBase).Assembly))
        {
            if (type != typeof(BossControllerBase) && !type.IsSubclassOf(typeof(BossControllerBase)))
                continue;
            var method = AccessTools.DeclaredMethod(
                type,
                nameof(BossControllerBase.OnTakeDamage),
                new[] { typeof(AttackData), typeof(DefenseData) });
            if (method != null)
                yield return method;
        }
    }

    private static bool Prefix(BossControllerBase __instance, AttackData __0) =>
        ProbeBehaviour.Instance?.AllowBossDamage(__instance, __0) ?? true;
}
