using System;
using System.Collections.Generic;

namespace DaveTheDiverMP;

internal readonly record struct DiverWeaponAuthorityResult(
    uint SceneId,
    uint SceneEpoch,
    uint Revision,
    uint CommitRevision,
    bool Accepted,
    DiverWeaponRejectReason Reason,
    bool Duplicate,
    bool Changed,
    int AppliedRounds,
    int WeaponId,
    int Ammo,
    int MaxAmmo);

internal sealed class DiverWeaponAuthority
{
    private const int ProcessedRequestCapacity = 256;

    private readonly record struct Decision(bool Accepted, DiverWeaponRejectReason Reason);

    private readonly Queue<ulong> _processedOrder = new();
    private readonly Dictionary<ulong, Decision> _processed = new();
    private uint _sceneId;
    private uint _sceneEpoch;
    private uint _revision;
    private uint _commitRevision;
    private int _weaponId;
    private int _ammo;
    private int _maxAmmo;
    private bool _initialized;

    internal DiverWeaponAuthorityResult Current
    {
        get
        {
            EnsureInitialized();
            return Result(true, DiverWeaponRejectReason.None);
        }
    }

    internal void Initialize(
        uint sceneId,
        uint sceneEpoch,
        uint revision,
        int weaponId,
        int ammo,
        int maxAmmo)
    {
        if (sceneId == 0)
            throw new ArgumentOutOfRangeException(nameof(sceneId));
        if (sceneEpoch == 0)
            throw new ArgumentOutOfRangeException(nameof(sceneEpoch));
        if (revision == 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (!IsValidWeaponState(weaponId, ammo, maxAmmo))
            throw new ArgumentOutOfRangeException(nameof(weaponId));

        _sceneId = sceneId;
        _sceneEpoch = sceneEpoch;
        _revision = revision;
        _commitRevision = 1;
        _weaponId = weaponId;
        _ammo = ammo;
        _maxAmmo = maxAmmo;
        _initialized = true;
        _processed.Clear();
        _processedOrder.Clear();
    }

    internal DiverWeaponAuthorityResult Equip(
        ulong requestId,
        int weaponId,
        int ammo,
        int maxAmmo)
    {
        EnsureInitialized();
        if (TryDuplicate(requestId, out var duplicate))
            return duplicate;
        if (requestId == 0 || weaponId <= 0 || !IsValidAmmo(ammo, maxAmmo))
            return Reject(requestId, DiverWeaponRejectReason.InvalidState);

        var changed = _weaponId != weaponId || _ammo != ammo || _maxAmmo != maxAmmo;
        if (changed)
        {
            _weaponId = weaponId;
            _ammo = ammo;
            _maxAmmo = maxAmmo;
            AdvanceRevision();
        }
        return Accept(requestId, changed);
    }

    internal DiverWeaponAuthorityResult Fire(ulong requestId, int weaponId, int rounds)
    {
        EnsureInitialized();
        if (TryDuplicate(requestId, out var duplicate))
            return duplicate;
        if (requestId == 0 || weaponId <= 0 || rounds is < 1 or > 64)
            return Reject(requestId, DiverWeaponRejectReason.InvalidState);
        if (weaponId != _weaponId)
            return Reject(requestId, DiverWeaponRejectReason.InvalidWeapon);
        if (_ammo < rounds)
            return Reject(requestId, DiverWeaponRejectReason.NoAmmo);

        _ammo -= rounds;
        AdvanceRevision();
        return Accept(requestId, changed: true, appliedRounds: rounds);
    }

    internal DiverWeaponAuthorityResult Reload(
        ulong requestId,
        int weaponId,
        int ammo,
        int maxAmmo)
    {
        EnsureInitialized();
        if (TryDuplicate(requestId, out var duplicate))
            return duplicate;
        if (requestId == 0 || weaponId <= 0 || !IsValidAmmo(ammo, maxAmmo))
            return Reject(requestId, DiverWeaponRejectReason.InvalidState);
        if (weaponId != _weaponId)
            return Reject(requestId, DiverWeaponRejectReason.InvalidWeapon);

        var appliedRounds = ammo - _ammo;
        if (appliedRounds <= 0)
            return Reject(requestId, DiverWeaponRejectReason.ReloadNotNeeded);

        var changed = _ammo != ammo || _maxAmmo != maxAmmo;
        if (changed)
        {
            _ammo = ammo;
            _maxAmmo = maxAmmo;
            AdvanceRevision();
        }
        return Accept(requestId, changed, appliedRounds);
    }

    internal DiverWeaponAuthorityResult Unequip(ulong requestId)
    {
        EnsureInitialized();
        if (TryDuplicate(requestId, out var duplicate))
            return duplicate;
        if (requestId == 0)
            return Reject(requestId, DiverWeaponRejectReason.InvalidState);

        var changed = _weaponId != 0;
        if (changed)
        {
            _weaponId = 0;
            _ammo = 0;
            _maxAmmo = 0;
            AdvanceRevision();
        }
        return Accept(requestId, changed);
    }

    internal static void SelfTest()
    {
        var authority = new DiverWeaponAuthority();
        authority.Initialize(10, 20, uint.MaxValue, 1001, 2, 6);

        var fired = authority.Fire(1, 1001, 2);
        var duplicate = authority.Fire(1, 1001, 2);
        if (!fired.Accepted || !fired.Changed || fired.AppliedRounds != 2 ||
            fired.Ammo != 0 || fired.Revision != 1 || fired.CommitRevision != 2 ||
            !duplicate.Accepted || duplicate.CommitRevision != 2 ||
            !duplicate.Duplicate || duplicate.Changed || duplicate.AppliedRounds != 0 ||
            duplicate.Ammo != 0)
            throw new InvalidOperationException("Weapon fire/dedupe failed");

        var empty = authority.Fire(2, 1001, 1);
        var wrong = authority.Fire(3, 1002, 1);
        var invalid = authority.Fire(4, 1001, 65);
        if (empty.Reason != DiverWeaponRejectReason.NoAmmo || empty.Changed ||
            empty.CommitRevision != 3 || empty.Revision != 1 ||
            wrong.Reason != DiverWeaponRejectReason.InvalidWeapon || wrong.Changed ||
            wrong.CommitRevision != 4 || wrong.Revision != 1 ||
            invalid.Reason != DiverWeaponRejectReason.InvalidState || invalid.Changed ||
            invalid.CommitRevision != 5 || invalid.Revision != 1)
            throw new InvalidOperationException("Weapon fire rejection failed");

        var reload = authority.Reload(5, 1001, 6, 6);
        var equip = authority.Equip(6, 2001, 3, 8);
        var unequip = authority.Unequip(7);
        if (!reload.Accepted || reload.AppliedRounds != 6 || reload.Ammo != 6 ||
            reload.MaxAmmo != 6 ||
            !equip.Accepted || equip.WeaponId != 2001 || equip.Ammo != 3 ||
            !unequip.Accepted || unequip.WeaponId != 0 || unequip.Ammo != 0 ||
            unequip.MaxAmmo != 0)
            throw new InvalidOperationException("Weapon reload/equip/unequip failed");

        var invalidEquip = authority.Equip(8, 2001, 9, 8);
        if (invalidEquip.Reason != DiverWeaponRejectReason.InvalidState ||
            invalidEquip.WeaponId != 0)
            throw new InvalidOperationException("Weapon ammo bounds failed");

        authority.Initialize(10, 21, 9, 1001, 2, 6);
        var nextEpoch = authority.Fire(1, 1001, 1);
        if (nextEpoch.Duplicate || nextEpoch.SceneEpoch != 21 || nextEpoch.Ammo != 1)
            throw new InvalidOperationException("Weapon epoch reset failed");

        authority.Initialize(10, 22, 10, 0, 0, 0);
        if (authority.Current.WeaponId != 0 || authority.Current.CommitRevision != 1)
            throw new InvalidOperationException("Weapon canonical unequipped failed");
        authority._commitRevision = uint.MaxValue;
        var wrappedCommit = authority.Fire(2, 1001, 1);
        if (wrappedCommit.CommitRevision != 1 || wrappedCommit.Accepted)
            throw new InvalidOperationException("Weapon commit revision wrap failed");

        authority.Initialize(10, 23, 10, 1001, 300, 300);
        for (ulong requestId = 1; requestId <= ProcessedRequestCapacity + 1UL; requestId++)
            authority.Fire(requestId, 1001, 1);
        var evicted = authority.Fire(1, 1001, 1);
        if (authority._processed.Count != ProcessedRequestCapacity ||
            evicted.Duplicate || !evicted.Accepted)
            throw new InvalidOperationException("Weapon dedupe bound failed");
    }

    private DiverWeaponAuthorityResult Accept(
        ulong requestId,
        bool changed,
        int appliedRounds = 0)
    {
        AdvanceCommit(requestId);
        Remember(requestId, new Decision(true, DiverWeaponRejectReason.None));
        return Result(true, DiverWeaponRejectReason.None, changed: changed,
            appliedRounds: appliedRounds);
    }

    private DiverWeaponAuthorityResult Reject(
        ulong requestId,
        DiverWeaponRejectReason reason)
    {
        AdvanceCommit(requestId);
        Remember(requestId, new Decision(false, reason));
        return Result(false, reason);
    }

    private bool TryDuplicate(ulong requestId, out DiverWeaponAuthorityResult result)
    {
        if (requestId != 0 && _processed.TryGetValue(requestId, out var decision))
        {
            result = Result(decision.Accepted, decision.Reason, duplicate: true);
            return true;
        }
        result = default;
        return false;
    }

    private void Remember(ulong requestId, Decision decision)
    {
        if (requestId == 0 || !_processed.TryAdd(requestId, decision))
            return;
        _processedOrder.Enqueue(requestId);
        if (_processedOrder.Count > ProcessedRequestCapacity)
            _processed.Remove(_processedOrder.Dequeue());
    }

    private void AdvanceRevision()
    {
        Advance(ref _revision);
    }

    private void AdvanceCommit(ulong requestId)
    {
        if (requestId != 0)
            Advance(ref _commitRevision);
    }

    private static void Advance(ref uint revision)
    {
        revision++;
        if (revision == 0)
            revision = 1;
    }

    private DiverWeaponAuthorityResult Result(
        bool accepted,
        DiverWeaponRejectReason reason,
        bool duplicate = false,
        bool changed = false,
        int appliedRounds = 0) =>
        new(
            _sceneId,
            _sceneEpoch,
            _revision,
            _commitRevision,
            accepted,
            reason,
            duplicate,
            changed,
            appliedRounds,
            _weaponId,
            _ammo,
            _maxAmmo);

    private static bool IsValidAmmo(int ammo, int maxAmmo) =>
        ammo >= 0 && maxAmmo >= 0 && ammo <= maxAmmo && maxAmmo <= 1_000_000;

    private static bool IsValidWeaponState(int weaponId, int ammo, int maxAmmo) =>
        weaponId == 0
            ? ammo == 0 && maxAmmo == 0
            : weaponId > 0 && IsValidAmmo(ammo, maxAmmo);

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Weapon authority is not initialized");
    }
}
