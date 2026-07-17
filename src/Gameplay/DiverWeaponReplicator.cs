using System;

namespace DaveTheDiverMP;

internal sealed class DiverWeaponReplicator
{
    private readonly DiverWeaponAuthority _authority = new();
    private SessionRole _role;
    private uint _sceneId;
    private uint _sceneEpoch;
    private ulong _requestId;
    private bool _pending;
    private bool _hasObservation;
    private int _observedWeaponId;
    private int _observedAmmo;

    internal static void SelfTest()
    {
        if (NextRequestId(0) != 1 || NextRequestId(ulong.MaxValue) != 1)
            throw new InvalidOperationException("Diver weapon observation self-test failed");
    }

    internal void Clear()
    {
        _role = SessionRole.Offline;
        _sceneId = 0;
        _sceneEpoch = 0;
        _requestId = 0;
        _pending = false;
        _hasObservation = false;
        _observedWeaponId = 0;
        _observedAmmo = 0;
    }

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        PlayerCharacter player,
        RemoteAvatar remoteAvatar,
        bool remoteDead)
    {
        if (role == SessionRole.Offline || session == null || !session.Connected ||
            !session.SceneMatches(sceneId))
        {
            Clear();
            return;
        }

        var epoch = role == SessionRole.Host
            ? session.LocalSceneEpoch
            : session.RemoteSceneEpoch;
        if (_role != role || _sceneId != sceneId || _sceneEpoch != epoch)
            BeginWorld(role, sceneId, epoch);

        if (role == SessionRole.Host)
        {
            while (session.TryTakeDiverWeaponIntent(out var intent))
                ApplyHostIntent(session, remoteAvatar, remoteDead, intent);
            return;
        }

        while (session.TryTakeDiverWeaponResult(out var result))
            ApplyClientResult(player, result);
        ObserveClientGun(session, player);
    }

    private void BeginWorld(SessionRole role, uint sceneId, uint sceneEpoch)
    {
        Clear();
        _role = role;
        _sceneId = sceneId;
        _sceneEpoch = sceneEpoch;
        if (role == SessionRole.Host)
            _authority.Initialize(sceneId, sceneEpoch, 1, 0, 0, 0);
    }

    private void ApplyHostIntent(
        UdpSession session,
        RemoteAvatar remoteAvatar,
        bool remoteDead,
        DiverWeaponIntent intent)
    {
        DiverWeaponAuthorityResult decision;
        if (remoteDead)
        {
            decision = _authority.RejectRequest(intent.RequestId, DiverWeaponRejectReason.Dead);
        }
        else
        {
            decision = intent.Action switch
            {
                DiverWeaponAction.Equip when TryResolveMaxAmmo(intent.WeaponId, out var maxAmmo) =>
                    _authority.Equip(intent.RequestId, intent.WeaponId, maxAmmo, maxAmmo),
                DiverWeaponAction.Fire =>
                    _authority.Fire(intent.RequestId, intent.WeaponId, 1),
                DiverWeaponAction.Reload when TryResolveMaxAmmo(intent.WeaponId, out var maxAmmo) &&
                    _authority.Current.WeaponId == intent.WeaponId &&
                    _authority.Current.MaxAmmo == maxAmmo =>
                    _authority.Reload(intent.RequestId, intent.WeaponId, maxAmmo),
                DiverWeaponAction.Unequip => _authority.Unequip(intent.RequestId),
                _ => _authority.RejectRequest(
                    intent.RequestId, DiverWeaponRejectReason.Unsupported)
            };
        }

        var state = new DiverRuntimeState(
            decision.SceneId,
            decision.SceneEpoch,
            decision.Revision,
            DiverOwner.Client,
            remoteDead,
            DiverRuntimeFields.Weapon | DiverRuntimeFields.Ammo,
            DiverRuntimeFlags.None,
            0f,
            0f,
            0f,
            0f,
            0f,
            decision.WeaponId,
            decision.Ammo,
            decision.MaxAmmo);
        var result = new DiverWeaponResult(
            decision.CommitRevision,
            intent.RequestId,
            intent.Action,
            decision.Accepted,
            decision.Reason,
            decision.AppliedRounds,
            state);
        session.SendDiverWeaponResult(result);
        remoteAvatar?.ApplyRuntime(state);
    }

    private void ApplyClientResult(PlayerCharacter player, DiverWeaponResult result)
    {
        _pending = false;
        if (!result.Accepted && result.Action == DiverWeaponAction.Equip)
            _hasObservation = false;

        if (!TryGetActiveOrdinaryGun(player, out var gun, out var weaponId, out _, out _))
        {
            _hasObservation = result.State.WeaponId == 0;
            _observedWeaponId = 0;
            _observedAmmo = 0;
            return;
        }

        if (result.State.WeaponId == weaponId)
        {
            var ammo = Math.Clamp(result.State.Ammo, 0, gun.m_MaxAmmo);
            gun.ForceSetBulletCount(ammo);
            _hasObservation = true;
            _observedWeaponId = weaponId;
            _observedAmmo = ammo;
        }
    }

    private void ObserveClientGun(UdpSession session, PlayerCharacter player)
    {
        if (_pending || player == null)
            return;

        if (!TryGetActiveOrdinaryGun(player, out var gun, out var weaponId, out var ammo, out _))
        {
            if (_hasObservation && _observedWeaponId != 0 &&
                SendIntent(session, DiverWeaponAction.Unequip, 0))
            {
                _observedWeaponId = 0;
                _observedAmmo = 0;
            }
            _hasObservation = true;
            return;
        }

        if (!_hasObservation || _observedWeaponId != weaponId)
        {
            if (SendIntent(session, DiverWeaponAction.Equip, weaponId))
            {
                _hasObservation = true;
                _observedWeaponId = weaponId;
                _observedAmmo = ammo;
            }
            return;
        }
        if (ammo != _observedAmmo)
            gun.ForceSetBulletCount(_observedAmmo);
    }

    private bool SendIntent(UdpSession session, DiverWeaponAction action, int weaponId)
    {
        var requestId = NextRequestId(_requestId);
        if (!session.SendDiverWeaponIntent(new DiverWeaponIntent(
                _sceneId, _sceneEpoch, requestId, action, weaponId)))
            return false;
        _requestId = requestId;
        _pending = true;
        return true;
    }

    private static bool TryGetActiveOrdinaryGun(
        PlayerCharacter player,
        out GunWeaponHandler gun,
        out int weaponId,
        out int ammo,
        out int maxAmmo)
    {
        gun = null;
        weaponId = 0;
        ammo = 0;
        maxAmmo = 0;
        try
        {
            gun = player?.CurrentInstanceItemInventory?.gunHandler;
            if (gun == null || !gun.IsEnabled || gun.CurrentMetaGunSlot != null ||
                gun.GunSpec == null || gun.GunSpec.TID <= 0)
                return false;
            weaponId = gun.GunSpec.TID;
            ammo = gun.GetAmmo();
            maxAmmo = gun.m_MaxAmmo;
            return maxAmmo is >= 1 and <= 1_000_000 && ammo >= 0 && ammo <= maxAmmo;
        }
        catch
        {
            gun = null;
            weaponId = 0;
            ammo = 0;
            maxAmmo = 0;
            return false;
        }
    }

    private static bool TryResolveMaxAmmo(int weaponId, out int maxAmmo)
    {
        maxAmmo = 0;
        try
        {
            if (!ResourceManager.hasInstance || !ResourceManager.Instance.IsLoadedGunSpecData)
                return false;
            var spec = ResourceManager.Instance.GetGunSpecData(weaponId);
            if (spec == null || spec.TID != weaponId ||
                spec.AmmoCount is < 1 or > 1_000_000)
                return false;
            maxAmmo = spec.AmmoCount;
            return true;
        }
        catch
        {
            maxAmmo = 0;
            return false;
        }
    }

    private static ulong NextRequestId(ulong requestId) =>
        requestId == ulong.MaxValue ? 1 : requestId + 1;
}
