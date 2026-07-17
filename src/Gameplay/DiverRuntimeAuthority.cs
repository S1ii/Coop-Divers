using System;
using System.Collections.Generic;

namespace DaveTheDiverMP;

internal readonly record struct DiverRuntimeAuthorityResult(
    uint SceneId,
    uint SceneEpoch,
    uint Revision,
    uint CommitRevision,
    float VitalCurrent,
    float VitalMax,
    bool IsDead,
    bool Changed,
    bool Died,
    bool Revived,
    bool Duplicate,
    float AppliedAmount)
{
    internal DiverOwner Owner => DiverOwner.Client;
    internal float Health => VitalCurrent;
    internal float MaxHealth => VitalMax;
    internal float Oxygen => VitalCurrent;
    internal float MaxOxygen => VitalMax;

    internal DiverRuntimeState ToRuntimeState(DiverRuntimeFlags flags = DiverRuntimeFlags.None) =>
        new(
            SceneId, SceneEpoch, Revision, DiverOwner.Client, IsDead,
            DiverRuntimeFields.Health | DiverRuntimeFields.Oxygen, flags,
            VitalCurrent, VitalMax, VitalCurrent, VitalMax, 0f, 0, 0);
}

internal sealed class DiverRuntimeAuthority
{
    private const int ProcessedDamageCapacity = 256;
    private readonly Queue<ulong> _processedDamageOrder = new();
    private readonly HashSet<ulong> _processedDamage = new();
    private uint _sceneId;
    private uint _sceneEpoch;
    private uint _revision;
    private uint _commitRevision;
    private float _vitalCurrent;
    private float _vitalMax;
    private bool _dead;
    private bool _initialized;

    internal DiverRuntimeAuthorityResult Current
    {
        get
        {
            EnsureInitialized();
            return Result();
        }
    }

    internal bool IsInitializedFor(uint sceneId, uint sceneEpoch) =>
        _initialized && _sceneId == sceneId && _sceneEpoch == sceneEpoch;

    internal bool IsInitialized => _initialized;

    internal void Initialize(
        uint sceneId,
        uint sceneEpoch,
        uint revision,
        float vitalCurrent,
        float vitalMax)
    {
        if (sceneId == 0)
            throw new ArgumentOutOfRangeException(nameof(sceneId));
        if (sceneEpoch == 0)
            throw new ArgumentOutOfRangeException(nameof(sceneEpoch));
        if (revision == 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        ValidateVital(vitalCurrent, vitalMax);

        _sceneId = sceneId;
        _sceneEpoch = sceneEpoch;
        _revision = revision;
        _commitRevision = 1;
        _vitalCurrent = vitalCurrent;
        _vitalMax = vitalMax;
        _dead = vitalCurrent == 0f;
        _initialized = true;
        _processedDamage.Clear();
        _processedDamageOrder.Clear();
    }

    internal DiverRuntimeAuthorityResult ApplyDamage(
        ulong eventId,
        float amount,
        bool invulnerable)
    {
        EnsureInitialized();
        if (eventId == 0)
            throw new ArgumentOutOfRangeException(nameof(eventId));
        ValidateAmount(amount);

        if (!RememberDamage(eventId))
            return Result(duplicate: true);
        if (invulnerable || _dead)
            return Result();

        var next = Math.Max(0f, _vitalCurrent - amount);
        if (next == _vitalCurrent)
            return Result();

        var applied = _vitalCurrent - next;
        _vitalCurrent = next;
        var died = next == 0f;
        _dead = died;
        AdvanceRevision();
        return Result(changed: true, died: died, appliedAmount: applied);
    }

    internal DiverRuntimeAuthorityResult ApplyHeal(float amount)
    {
        EnsureInitialized();
        ValidateAmount(amount);
        if (_dead)
            return Result();

        var next = Math.Min(_vitalMax, _vitalCurrent + amount);
        if (next == _vitalCurrent)
            return Result();

        var applied = next - _vitalCurrent;
        _vitalCurrent = next;
        AdvanceRevision();
        return Result(changed: true, appliedAmount: applied);
    }

    internal DiverRuntimeAuthorityResult ApplyRevive(float vitalCurrent)
    {
        EnsureInitialized();
        if (!float.IsFinite(vitalCurrent) || vitalCurrent <= 0f || vitalCurrent > _vitalMax)
            throw new ArgumentOutOfRangeException(nameof(vitalCurrent));
        if (!_dead)
            return Result();

        _vitalCurrent = vitalCurrent;
        _dead = false;
        AdvanceRevision();
        return Result(changed: true, revived: true, appliedAmount: vitalCurrent);
    }

    internal static void SelfTest()
    {
        var authority = new DiverRuntimeAuthority();
        authority.Initialize(10, 20, uint.MaxValue, 5f, 10f);

        var damaged = authority.ApplyDamage(1, 2f, false);
        if (!damaged.Changed || damaged.Died || damaged.Revision != 1 ||
            damaged.CommitRevision != 2 ||
            damaged.Owner != DiverOwner.Client || damaged.Health != 3f ||
            damaged.Oxygen != damaged.Health || damaged.MaxOxygen != damaged.MaxHealth ||
            damaged.AppliedAmount != 2f)
            throw new InvalidOperationException("Diver damage authority failed");

        var duplicate = authority.ApplyDamage(1, 2f, false);
        var blocked = authority.ApplyDamage(2, 2f, true);
        if (!duplicate.Duplicate || duplicate.Changed || blocked.Changed ||
            authority.Current.VitalCurrent != 3f)
            throw new InvalidOperationException("Diver duplicate/invulnerability guard failed");

        var died = authority.ApplyDamage(3, 3f, false);
        var deadAgain = authority.ApplyDamage(4, 1f, false);
        var healWhileDead = authority.ApplyHeal(5f);
        if (!died.Died || !died.IsDead || deadAgain.Died || deadAgain.Changed ||
            healWhileDead.Changed || !healWhileDead.IsDead)
            throw new InvalidOperationException("Diver death edge failed");

        var revived = authority.ApplyRevive(4f);
        var revivedAgain = authority.ApplyRevive(8f);
        if (!revived.Revived || revived.IsDead || revived.VitalCurrent != 4f ||
            revivedAgain.Changed || revivedAgain.Revived || revivedAgain.VitalCurrent != 4f)
            throw new InvalidOperationException("Diver revive edge failed");

        authority.Initialize(10, 21, 7, 5f, 10f);
        var nextEpoch = authority.ApplyDamage(1, 1f, false);
        if (nextEpoch.Duplicate || nextEpoch.SceneEpoch != 21 || nextEpoch.VitalCurrent != 4f)
            throw new InvalidOperationException("Diver scene epoch reset failed");

        authority.Initialize(10, 22, 8, 1000f, 1000f);
        for (ulong eventId = 1; eventId <= ProcessedDamageCapacity + 1UL; eventId++)
            authority.ApplyDamage(eventId, 1f, false);
        var evicted = authority.ApplyDamage(1, 1f, false);
        if (authority._processedDamage.Count != ProcessedDamageCapacity ||
            evicted.Duplicate || !evicted.Changed)
            throw new InvalidOperationException("Diver duplicate cache bound failed");
    }

    private bool RememberDamage(ulong eventId)
    {
        if (!_processedDamage.Add(eventId))
            return false;

        _processedDamageOrder.Enqueue(eventId);
        if (_processedDamageOrder.Count > ProcessedDamageCapacity)
            _processedDamage.Remove(_processedDamageOrder.Dequeue());
        return true;
    }

    private void AdvanceRevision()
    {
        _revision++;
        if (_revision == 0)
            _revision = 1;
        _commitRevision++;
        if (_commitRevision == 0)
            _commitRevision = 1;
    }

    private DiverRuntimeAuthorityResult Result(
        bool changed = false,
        bool died = false,
        bool revived = false,
        bool duplicate = false,
        float appliedAmount = 0f) =>
        new(
            _sceneId,
            _sceneEpoch,
            _revision,
            _commitRevision,
            _vitalCurrent,
            _vitalMax,
            _dead,
            changed,
            died,
            revived,
            duplicate,
            appliedAmount);

    private static void ValidateVital(float current, float max)
    {
        if (!float.IsFinite(current) || !float.IsFinite(max) ||
            max is <= 0f or > 1_000_000f || current < 0f || current > max)
            throw new ArgumentOutOfRangeException(nameof(current));
    }

    private static void ValidateAmount(float amount)
    {
        if (!float.IsFinite(amount) || amount is <= 0f or > 1_000_000f)
            throw new ArgumentOutOfRangeException(nameof(amount));
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Diver authority is not initialized");
    }
}
