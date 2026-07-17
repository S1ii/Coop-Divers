using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class RemoteDiverHitbox : IDisposable
{
    private enum GeometryKind
    {
        Unsupported,
        Box,
        Capsule,
        Circle
    }

    private const int SentinelHp = 1_000_000_000;
    private static readonly Dictionary<int, RemoteDiverHitbox> Instances = new();

    private readonly GameObject _gameObject;
    private readonly Collider2D _collider;
    private readonly DamageableSimpleObject _owner;
    private readonly Damageable _damageable;
    private readonly int _ownerInstanceId;
    private Func<AttackData, DefenseData, bool> _onDamage;
    private bool _armed;
    private bool _disposed;

    private RemoteDiverHitbox(
        GameObject gameObject,
        Collider2D collider,
        DamageableSimpleObject owner,
        Damageable damageable,
        Func<AttackData, DefenseData, bool> onDamage)
    {
        _gameObject = gameObject;
        _collider = collider;
        _owner = owner;
        _damageable = damageable;
        _onDamage = onDamage;
        _ownerInstanceId = owner.GetInstanceID();
        Instances.Add(_ownerInstanceId, this);
    }

    internal static void SelfTest()
    {
        if (SelectGeometry(true, false, false) != GeometryKind.Box ||
            SelectGeometry(false, true, false) != GeometryKind.Capsule ||
            SelectGeometry(false, false, true) != GeometryKind.Circle ||
            SelectGeometry(false, false, false) != GeometryKind.Unsupported ||
            !CanEnable(true, true, false, true) ||
            CanEnable(true, false, false, true) ||
            CanEnable(true, true, true, true) ||
            CanEnable(true, true, false, false))
            throw new InvalidOperationException("Remote diver hitbox lifecycle failed");
    }

    internal static bool TryCreate(
        Transform remoteRoot,
        PlayerCharacter localPlayer,
        Func<AttackData, DefenseData, bool> onDamage,
        out RemoteDiverHitbox hitbox)
    {
        hitbox = null;
        if (remoteRoot == null || localPlayer == null)
            return false;

        var templateDamageable = localPlayer.GetDamageable;
        var sourceCollider = templateDamageable?.HitCollider;
        if (sourceCollider == null)
            return false;

        GameObject child = null;
        try
        {
            child = new GameObject("DTMP Remote Diver Hitbox");
            child.SetActive(false);
            child.layer = sourceCollider.gameObject.layer;
            child.transform.SetParent(remoteRoot, false);
            CopyRelativeTransform(child.transform, sourceCollider.transform, localPlayer.transform);

            var collider = CopyCollider(child, sourceCollider);
            if (collider == null)
            {
                UnityEngine.Object.Destroy(child);
                return false;
            }

            var owner = child.AddComponent<DamageableSimpleObject>();
            var damageable = child.AddComponent<Damageable>();
            owner.IsResetHPOnEnable = false;
            owner.m_MaxHP = SentinelHp;
            owner.m_CurrHP = SentinelHp;

            var nativeOwner = owner.TryCast<IDamageable>();
            if (nativeOwner == null)
            {
                UnityEngine.Object.Destroy(child);
                return false;
            }

            damageable.Init(nativeOwner);
            damageable.HitCollider = collider;
            damageable.takeableAttackTypes = templateDamageable.takeableAttackTypes;
            damageable.OnceHitFlag = false;
            damageable.CanNotDamaged = false;
            damageable.IsDamageableEnable = false;
            collider.enabled = false;

            hitbox = new RemoteDiverHitbox(child, collider, owner, damageable, onDamage);
            return true;
        }
        catch
        {
            if (child != null)
                UnityEngine.Object.Destroy(child);
            return false;
        }
    }

    internal void SetCallback(Func<AttackData, DefenseData, bool> onDamage)
    {
        _onDamage = onDamage;
        if (onDamage == null)
            Disarm();
    }

    internal bool Arm()
    {
        if (!CanEnable(!_disposed, _gameObject != null, false, _onDamage != null))
            return false;

        try
        {
            _armed = true;
            _gameObject.SetActive(true);
            _collider.enabled = true;
            _damageable.EnableDamageable(true);
            return true;
        }
        catch
        {
            Disarm();
            return false;
        }
    }

    internal void Disarm()
    {
        _armed = false;
        if (_disposed)
            return;
        try
        {
            if (_gameObject.activeSelf)
                _damageable.EnableDamageable(false);
            _collider.enabled = false;
            _gameObject.SetActive(false);
        }
        catch
        {
            if (_gameObject != null)
                _gameObject.SetActive(false);
        }
    }

    internal void SetInvulnerable(bool invulnerable)
    {
        if (!_disposed)
            _damageable.CanNotDamaged = invulnerable;
    }

    internal static bool TryHandleDamage(
        DamageableSimpleObject owner,
        AttackData attackData,
        DefenseData defenseData,
        out bool result)
    {
        result = false;
        if (owner == null || !Instances.TryGetValue(owner.GetInstanceID(), out var hitbox))
            return false;

        result = hitbox.HandleDamage(attackData, defenseData);
        return true;
    }

    private bool HandleDamage(AttackData attackData, DefenseData defenseData)
    {
        if (!_armed || _disposed || _onDamage == null || attackData == null || defenseData == null)
            return false;
        if (FishReplicator.IsPlayerAttack(attackData.attackType))
            return false;
        try
        {
            return _onDamage(attackData, defenseData);
        }
        catch
        {
            return false;
        }
    }

    private static Collider2D CopyCollider(GameObject target, Collider2D source)
    {
        var sourceBox = source.TryCast<BoxCollider2D>();
        var sourceCapsule = source.TryCast<CapsuleCollider2D>();
        var sourceCircle = source.TryCast<CircleCollider2D>();
        Collider2D result;
        switch (SelectGeometry(sourceBox != null, sourceCapsule != null, sourceCircle != null))
        {
            case GeometryKind.Box:
                var box = target.AddComponent<BoxCollider2D>();
                box.size = sourceBox.size;
                box.edgeRadius = sourceBox.edgeRadius;
                result = box;
                break;
            case GeometryKind.Capsule:
                var capsule = target.AddComponent<CapsuleCollider2D>();
                capsule.size = sourceCapsule.size;
                capsule.direction = sourceCapsule.direction;
                result = capsule;
                break;
            case GeometryKind.Circle:
                var circle = target.AddComponent<CircleCollider2D>();
                circle.radius = sourceCircle.radius;
                result = circle;
                break;
            default:
                return null;
        }

        result.offset = source.offset;
        result.isTrigger = source.isTrigger;
        result.sharedMaterial = source.sharedMaterial;
        result.enabled = false;
        return result;
    }

    private static GeometryKind SelectGeometry(bool box, bool capsule, bool circle) =>
        box ? GeometryKind.Box :
        capsule ? GeometryKind.Capsule :
        circle ? GeometryKind.Circle : GeometryKind.Unsupported;

    private static void CopyRelativeTransform(
        Transform target,
        Transform source,
        Transform sourceRoot)
    {
        target.localPosition = sourceRoot.InverseTransformPoint(source.position);
        target.localRotation = Quaternion.Inverse(sourceRoot.rotation) * source.rotation;
        target.localScale = DivideScale(source.lossyScale, sourceRoot.lossyScale);
    }

    private static Vector3 DivideScale(Vector3 value, Vector3 divisor) => new(
        SafeDivide(value.x, divisor.x),
        SafeDivide(value.y, divisor.y),
        SafeDivide(value.z, divisor.z));

    private static float SafeDivide(float value, float divisor) =>
        Mathf.Abs(divisor) > 0.0001f ? value / divisor : value;

    private static bool CanEnable(bool ready, bool fresh, bool dead, bool explicitlyArmed) =>
        ready && fresh && !dead && explicitlyArmed;

    public void Dispose()
    {
        if (_disposed)
            return;
        Disarm();
        _disposed = true;
        if (Instances.TryGetValue(_ownerInstanceId, out var registered) && ReferenceEquals(registered, this))
            Instances.Remove(_ownerInstanceId);
        if (_gameObject != null)
            UnityEngine.Object.Destroy(_gameObject);
    }
}

[HarmonyPatch(typeof(DamageableSimpleObject), nameof(DamageableSimpleObject.OnTakeDamage))]
internal static class RemoteDiverHitboxDamagePatch
{
    private static bool Prefix(
        DamageableSimpleObject __instance,
        AttackData __0,
        DefenseData __1,
        ref bool __result)
    {
        if (!RemoteDiverHitbox.TryHandleDamage(__instance, __0, __1, out var result))
            return true;
        __result = result;
        return false;
    }
}
