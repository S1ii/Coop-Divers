using System;
using UnityEngine;

namespace DaveTheDiverMP;

// The host owns the client's oxygen/health.  The game exposes one native breath
// value for both, so this deliberately uses that single source of truth.
internal sealed class DiverVitalReplicator
{
    private readonly DiverRuntimeAuthority _authority = new();
    private ulong _eventId;
    private DiverRuntimeFlags _flags;
    internal bool ApplyingClientResult { get; private set; }

    internal static void SelfTest()
    {
        if (CauseFor(0f, true) != DiverVitalCause.OxygenDepleted ||
            CauseFor(1f, false) != DiverVitalCause.Damage ||
            !AcceptClientVital(4f, 5f) || AcceptClientVital(6f, 5f) ||
            NextEventId(ulong.MaxValue) != 1)
            throw new InvalidOperationException("Diver vital bridge self-test failed");
    }

    internal void Clear()
    {
        _eventId = 0;
        _flags = DiverRuntimeFlags.None;
        ApplyingClientResult = false;
    }

    internal void ObserveHostRuntime(UdpSession session, DiverRuntimeState state, RemoteAvatar avatar)
    {
        if (session == null || state.Owner != DiverOwner.Client ||
            (state.Fields & DiverRuntimeFields.Oxygen) == 0)
            return;

        if (!_authority.IsInitializedFor(state.SceneId, state.SceneEpoch))
        {
            _authority.Initialize(state.SceneId, state.SceneEpoch, state.Revision,
                state.Oxygen, state.MaxOxygen);
            avatar?.ApplyRuntime(state);
            _flags = state.Flags & ~DiverRuntimeFlags.Invulnerable;
            return;
        }

        // Runtime snapshots are observation-only.  In particular, a client cannot
        // grant itself immunity, heal, or revive by changing its local breath value.
        _flags = state.Flags & ~DiverRuntimeFlags.Invulnerable;

        var current = _authority.Current;
        if (!AcceptClientVital(state.Oxygen, current.Oxygen))
        {
            avatar?.ApplyRuntime(current.ToRuntimeState(_flags));
            return;
        }
        var eventId = NextEventId(_eventId);
        _eventId = eventId;
        DiverRuntimeAuthorityResult result;
        DiverVitalCause cause;
        if (state.Oxygen < current.Oxygen)
        {
            result = _authority.ApplyDamage(eventId, current.Oxygen - state.Oxygen,
                (state.Flags & DiverRuntimeFlags.Invulnerable) != 0);
            cause = CauseFor(state.Oxygen, result.Died);
        }
        else
        {
            avatar?.ApplyRuntime(state);
            return;
        }

        Publish(session, avatar, result, eventId, cause, _flags);
    }

    internal bool TryApplyHostDamage(
        UdpSession session,
        AttackData attack,
        RemoteAvatar avatar)
    {
        if (session == null || attack == null || !_authority.IsInitialized)
            return false;

        var buffs = attack.buff;
        if (!DiverEquipmentPolicy.CanApplyRemoteHit(
                attack.attackType, attack.knockBackForce, attack.freezeTime,
                buffs?.Length ?? 0))
            return false;

        var amount = attack.damage;
        if (amount <= 0)
            return false;
        var eventId = NextEventId(_eventId);
        _eventId = eventId;
        var result = _authority.ApplyDamage(eventId, amount,
            (_flags & DiverRuntimeFlags.Invulnerable) != 0 || _authority.Current.IsDead);
        if (!result.Changed)
            return false;
        Publish(session, avatar, result, eventId, DiverVitalCause.Damage, _flags);
        return true;
    }

    internal void ApplyClientResults(UdpSession session, uint sceneId, PlayerCharacter player)
    {
        while (session != null && session.TryTakeDiverVitalResult(out var result))
        {
            var state = result.State;
            if (player == null || state.Owner != DiverOwner.Client || state.SceneId != sceneId ||
                state.SceneEpoch != session.RemoteSceneEpoch)
                continue;
            try
            {
                ApplyingClientResult = true;
                var breath = player.BreathHandler;
                if (breath == null)
                    continue;
                breath.SetHP(state.Oxygen);
                if ((result.Edges & DiverVitalEdges.Died) != 0 && !player.IsDead())
                    player.OnDie();
                else if ((result.Edges & DiverVitalEdges.Revived) != 0 && player.IsDead())
                    player.OnRevive();
            }
            catch
            {
                // The next reliable result/snapshot retries the native presentation.
            }
            finally
            {
                ApplyingClientResult = false;
            }
        }
    }

    private void Publish(
        UdpSession session,
        RemoteAvatar avatar,
        DiverRuntimeAuthorityResult result,
        ulong eventId,
        DiverVitalCause cause,
        DiverRuntimeFlags flags)
    {
        if (!result.Changed)
            return;
        var edges = cause == DiverVitalCause.OxygenDepleted ? DiverVitalEdges.Died :
            result.Died ? DiverVitalEdges.Damaged | DiverVitalEdges.Died :
            result.Revived ? DiverVitalEdges.Revived :
            cause is DiverVitalCause.Heal or DiverVitalCause.OxygenRestore
                ? DiverVitalEdges.Healed : DiverVitalEdges.Damaged;
        var state = result.ToRuntimeState(flags);
        session.SendDiverVitalResult(new DiverVitalResult(
            result.CommitRevision, eventId, cause, edges, result.AppliedAmount, state));
        avatar?.ApplyRuntime(state);
    }

    private static DiverVitalCause CauseFor(float oxygen, bool died) =>
        died || oxygen == 0f ? DiverVitalCause.OxygenDepleted : DiverVitalCause.Damage;

    private static bool AcceptClientVital(float reported, float canonical) => reported <= canonical;

    private static ulong NextEventId(ulong value) => value == ulong.MaxValue ? 1 : value + 1;
}
