using System;
using System.Collections.Generic;
using UnityEngine;

namespace DaveTheDiverMP;

internal readonly record struct VisibleTransform(
    float Rotation,
    float ScaleX,
    float ScaleY,
    bool FlipX,
    bool FlipY);

internal sealed class RemoteAvatar : IDisposable
{
    private const float SnapshotFreshSeconds = 0.75f;
    private const float VisualFreshSeconds = 0.5f;

    internal static void SelfTest()
    {
        var angle = 40f * Mathf.Deg2Rad;
        var normal = NormalizeVisibleBasis(
            new Vector2(Mathf.Cos(angle) * 2f, Mathf.Sin(angle) * 2f),
            new Vector2(-Mathf.Sin(angle) * 3f, Mathf.Cos(angle) * 3f));
        var reflected = NormalizeVisibleBasis(
            new Vector2(-Mathf.Cos(angle) * 2f, Mathf.Sin(angle) * 2f),
            new Vector2(Mathf.Sin(angle) * 3f, Mathf.Cos(angle) * 3f));
        if (ApplyDepthBias(-0.05f) >= -0.05f || RemoteTint.a != 1f ||
            NameOffset(true, 0.75f, 2.5f) != 0.95f ||
            NameOffset(false, 0.75f, 2.5f) != 2.5f || normal.FlipX ||
            !IsFresh(0.75f, SnapshotFreshSeconds) ||
            IsFresh(0.751f, SnapshotFreshSeconds) ||
            Mathf.Abs(Mathf.DeltaAngle(normal.Rotation, 40f)) > 0.01f ||
            !reflected.FlipX ||
            Mathf.Abs(Mathf.DeltaAngle(reflected.Rotation, -40f)) > 0.01f)
            throw new InvalidOperationException("Remote avatar depth bias failed");
    }

    private static readonly Color RemoteTint = new(0.82f, 0.95f, 1f, 1f);

    private sealed class RemoteVisual
    {
        internal SpriteRenderer Renderer;
        internal Vector3 Offset;
        internal float Rotation;
    }

    private GameObject _gameObject;
    private SpriteRenderer _renderer;
    private readonly List<RemoteVisual> _visualRenderers = new();
    private GameObject _nameObject;
    private TextMesh _nameText;
    private readonly Dictionary<uint, Sprite> _sprites = new();
    private Vector3 _target;
    private Vector3 _velocity;
    private float _targetRotation;
    private float _timeSinceSnapshot;
    private float _timeSinceVisual;
    private float _nameOffset = 2.5f;
    private float _defaultNameOffset = 2.5f;
    private bool _isDive;
    private bool _initialized;
    private bool _hasVisualState;
    private bool _isRemoteDead;

    internal Transform Transform => _gameObject?.transform;
    internal Transform TargetTransform =>
        _gameObject != null && IsFresh(_timeSinceSnapshot, SnapshotFreshSeconds)
            ? _gameObject.transform
            : null;

    internal void Apply(
        PlayerSnapshot snapshot,
        SpriteRenderer localRenderer,
        string playerName,
        bool isDive)
    {
        if (_gameObject == null)
            Create(localRenderer, new Vector3(snapshot.X, snapshot.Y, ApplyDepthBias(snapshot.Z)), playerName);

        _target = new Vector3(snapshot.X, snapshot.Y, ApplyDepthBias(snapshot.Z));
        _velocity = new Vector3(snapshot.VelocityX, snapshot.VelocityY, 0f);
        _targetRotation = snapshot.Rotation;
        _timeSinceSnapshot = 0f;
        _isDive = isDive;
        _gameObject.SetActive(true);
        _nameObject.SetActive(true);
        _nameText.text = Protocol.NormalizePlayerName(playerName);
        if (snapshot.SpriteId != 0 && _sprites.TryGetValue(snapshot.SpriteId, out var sprite))
            _renderer.sprite = sprite;
        _renderer.flipX = snapshot.Flipped;
        _renderer.enabled = !_hasVisualState;
        _gameObject.transform.localScale = Vector3.one;
        if (!_hasVisualState)
            _renderer.transform.localScale = new Vector3(snapshot.ScaleX, snapshot.ScaleY, 1f);

        if (!_initialized || (_gameObject.transform.position - _target).sqrMagnitude > 64f)
        {
            _gameObject.transform.position = _target;
            _gameObject.transform.rotation = Quaternion.Euler(0f, 0f, _targetRotation);
            _initialized = true;
        }
    }

    internal void ApplyVisual(PlayerVisualState state, string playerName)
    {
        if (_gameObject == null)
            return;

        _timeSinceVisual = 0f;
        _hasVisualState = state.Sprites.Length > 0;
        _renderer.enabled = !_hasVisualState;
        _gameObject.transform.localScale = Vector3.one;
        _nameText.text = Protocol.NormalizePlayerName(playerName);
        while (_visualRenderers.Count < state.Sprites.Length)
            CreateVisualRenderer();

        var highestOrder = _renderer.sortingOrder;
        var highestTop = 0f;
        for (var index = 0; index < _visualRenderers.Count; index++)
        {
            var visual = _visualRenderers[index];
            var renderer = visual.Renderer;
            if (index >= state.Sprites.Length)
            {
                renderer.gameObject.SetActive(false);
                continue;
            }

            var sprite = state.Sprites[index];
            renderer.gameObject.SetActive(true);
            renderer.sprite = ResolveSprite(sprite.SpriteId);
            renderer.flipX = sprite.FlipX;
            renderer.flipY = sprite.FlipY;
            renderer.sortingLayerID = sprite.SortingLayerId;
            renderer.sortingOrder = sprite.SortingOrder;
            visual.Offset = new Vector3(sprite.OffsetX, sprite.OffsetY, sprite.OffsetZ);
            visual.Rotation = sprite.Rotation;
            renderer.transform.position = _gameObject.transform.position + visual.Offset;
            renderer.transform.rotation = Quaternion.Euler(0f, 0f, visual.Rotation);
            renderer.transform.localScale = new Vector3(sprite.ScaleX, sprite.ScaleY, 1f);
            highestOrder = Mathf.Max(highestOrder, sprite.SortingOrder);
            highestTop = Mathf.Max(
                highestTop, visual.Offset.y + renderer.bounds.extents.y);
        }
        _nameOffset = NameOffset(_isDive, highestTop, _defaultNameOffset);
        var nameRenderer = _nameObject.GetComponent<MeshRenderer>();
        nameRenderer.sortingOrder = highestOrder + 100;
    }

    internal void ApplyRuntime(DiverRuntimeState state)
    {
        _isRemoteDead = state.IsDead;
        ApplyTint();
    }

    internal static bool TryCaptureRuntimeState(
        uint sceneId,
        uint sceneEpoch,
        uint revision,
        PlayerCharacter player,
        out DiverRuntimeState state)
    {
        state = default;
        if (sceneId == 0 || sceneEpoch == 0 || revision == 0 || player == null)
            return false;
        try
        {
            var fields = DiverRuntimeFields.None;
            var flags = DiverRuntimeFlags.None;
            var oxygen = 0f;
            var maxOxygen = 0f;
            var breath = player.BreathHandler;
            if (breath != null)
            {
                if (!float.IsFinite(breath.HP) || !float.IsFinite(breath.MaxHP) ||
                    breath.MaxHP is <= 0f or > 1_000_000f ||
                    breath.HP < 0f || breath.HP > breath.MaxHP)
                    return false;
                fields |= DiverRuntimeFields.Oxygen;
                maxOxygen = breath.MaxHP;
                oxygen = breath.HP;
                if (breath.IsOxygenDepleting)
                    flags |= DiverRuntimeFlags.OxygenDepleting;
            }

            var cargoWeight = 0f;
            var cargo = LootBox.Instance;
            if (cargo != null)
            {
                if (!float.IsFinite(cargo.weight) || cargo.weight is < 0f or > 1_000_000f)
                    return false;
                fields |= DiverRuntimeFields.Cargo;
                cargoWeight = cargo.weight;
                if (cargo.isOverweightState)
                    flags |= DiverRuntimeFlags.Overweight;
            }
            if (player.IsImmuneDamage)
                flags |= DiverRuntimeFlags.Invulnerable;

            state = new DiverRuntimeState(
                sceneId, sceneEpoch, revision, DiverOwner.Host, player.IsDead(), fields, flags,
                0f, 0f, oxygen, maxOxygen, cargoWeight, 0, 0);
            return true;
        }
        catch
        {
            state = default;
            return false;
        }
    }

    internal void Update(float deltaTime)
    {
        if (_gameObject == null)
            return;

        _timeSinceSnapshot += deltaTime;
        _timeSinceVisual += deltaTime;
        if (!IsFresh(_timeSinceSnapshot, SnapshotFreshSeconds))
        {
            _gameObject.SetActive(false);
            _nameObject.SetActive(false);
            return;
        }
        if (!_gameObject.activeSelf)
        {
            _gameObject.SetActive(true);
            _nameObject.SetActive(true);
        }
        if (_hasVisualState && !IsFresh(_timeSinceVisual, VisualFreshSeconds))
        {
            _hasVisualState = false;
            _renderer.enabled = true;
            foreach (var visual in _visualRenderers)
                if (visual.Renderer != null)
                    visual.Renderer.gameObject.SetActive(false);
        }

        var predictedPosition = _target + _velocity * Mathf.Min(_timeSinceSnapshot, 0.15f);
        var positionBlend = 1f - Mathf.Exp(-18f * deltaTime);
        _gameObject.transform.position = Vector3.Lerp(
            _gameObject.transform.position,
            predictedPosition,
            positionBlend);
        var rotation = Mathf.LerpAngle(
            _gameObject.transform.eulerAngles.z,
            _targetRotation,
            1f - Mathf.Exp(-22f * deltaTime));
        _gameObject.transform.rotation = Quaternion.Euler(0f, 0f, rotation);

        foreach (var visual in _visualRenderers)
        {
            if (visual.Renderer == null || !visual.Renderer.gameObject.activeSelf)
                continue;
            visual.Renderer.transform.position = _gameObject.transform.position + visual.Offset;
            visual.Renderer.transform.rotation = Quaternion.Euler(0f, 0f, visual.Rotation);
        }

        _nameObject.transform.position = _gameObject.transform.position + Vector3.up * _nameOffset;
        _nameObject.transform.rotation = Quaternion.identity;
    }

    internal void Clear()
    {
        if (_gameObject != null)
            UnityEngine.Object.Destroy(_gameObject);
        if (_nameObject != null)
            UnityEngine.Object.Destroy(_nameObject);
        _gameObject = null;
        _renderer = null;
        _nameObject = null;
        _nameText = null;
        _sprites.Clear();
        _visualRenderers.Clear();
        _initialized = false;
        _hasVisualState = false;
        _isRemoteDead = false;
        _timeSinceSnapshot = 0f;
        _timeSinceVisual = 0f;
    }

    internal static SpriteRenderer FindPrimaryRenderer(Component player)
    {
        foreach (var candidate in player.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (candidate != null && candidate.sprite != null)
                return candidate;
        }
        return null;
    }

    internal static PlayerVisualState CaptureVisualState(
        uint sceneId, uint sceneEpoch, Component player)
    {
        var sprites = new List<VisualSprite>();
        var root = player.transform;
        var rangeAttack = player.GetComponent<PlayerCharacter>()?.RangeAttackArm;
        var attackRange = rangeAttack?.attackRangeObject?.transform;
        var harpoonAim = rangeAttack?.harpoonAimPoint;
        var gunAim = rangeAttack?.gunAimPoint?.transform;
        foreach (var renderer in player.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                renderer.sprite == null || IsUnder(renderer.transform, attackRange) ||
                IsUnder(renderer.transform, harpoonAim) || IsUnder(renderer.transform, gunAim))
                continue;
            var visible = CaptureVisibleTransform(renderer);
            sprites.Add(new VisualSprite(
                Protocol.SceneId(renderer.sprite.name),
                renderer.transform.position.x - root.position.x,
                renderer.transform.position.y - root.position.y,
                renderer.transform.position.z - root.position.z,
                visible.Rotation,
                visible.ScaleX, visible.ScaleY,
                renderer.sortingLayerID, renderer.sortingOrder,
                visible.FlipX, visible.FlipY));
            if (sprites.Count == Protocol.MaxVisualSprites)
                break;
        }
        return new PlayerVisualState(sceneId, sceneEpoch, sprites.ToArray());
    }

    internal static VisibleTransform CaptureVisibleTransform(SpriteRenderer renderer)
    {
        var right = renderer.transform.TransformVector(Vector3.right);
        var up = renderer.transform.TransformVector(Vector3.up);
        if (renderer.flipX)
            right = -right;
        if (renderer.flipY)
            up = -up;
        return NormalizeVisibleBasis(
            new Vector2(right.x, right.y), new Vector2(up.x, up.y));
    }

    private static VisibleTransform NormalizeVisibleBasis(Vector2 right, Vector2 up)
    {
        var scaleX = Mathf.Max(right.magnitude, 0.0001f);
        var scaleY = Mathf.Max(up.magnitude, 0.0001f);
        var flipX = right.x * up.y - right.y * up.x < 0f;
        var rotation = Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg + (flipX ? 180f : 0f);
        return new VisibleTransform(rotation, scaleX, scaleY, flipX, false);
    }

    private static bool IsUnder(Transform candidate, Transform root) =>
        root != null && (candidate == root || candidate.IsChildOf(root));

    private static float ApplyDepthBias(float z) => z - 0.01f;

    private static float NameOffset(bool isDive, float highestTop, float fallback) =>
        isDive ? Mathf.Max(0.65f, highestTop + 0.2f) : fallback;

    private static bool IsFresh(float age, float maxAge) =>
        age >= 0f && age <= maxAge;

    private void Create(SpriteRenderer sourceRenderer, Vector3 position, string playerName)
    {
        _gameObject = new GameObject("DTMP Remote Diver");
        _gameObject.transform.position = position;
        var baseVisual = new GameObject("DTMP Remote Base Visual");
        baseVisual.transform.SetParent(_gameObject.transform, false);
        _renderer = baseVisual.AddComponent<SpriteRenderer>();
        _renderer.color = CurrentTint;

        if (sourceRenderer != null)
        {
            _renderer.sprite = sourceRenderer.sprite;
            _renderer.sortingLayerID = sourceRenderer.sortingLayerID;
            _renderer.sortingOrder = sourceRenderer.sortingOrder + 1;
            _renderer.transform.localScale = new Vector3(
                Mathf.Abs(sourceRenderer.transform.lossyScale.x),
                Mathf.Abs(sourceRenderer.transform.lossyScale.y), 1f);
            var sourceRoot = FindPlayerRoot(sourceRenderer);
            if (sourceRoot != null)
                _renderer.transform.localPosition =
                    sourceRenderer.transform.position - sourceRoot.position;
            _defaultNameOffset = Mathf.Max(2.5f, _renderer.bounds.extents.y + 1.25f);
            _nameOffset = NameOffset(
                _isDive,
                _renderer.transform.localPosition.y + _renderer.bounds.extents.y,
                _defaultNameOffset);
        }
        CacheSprites();

        _nameObject = new GameObject("DTMP Remote Name");
        _nameText = _nameObject.AddComponent<TextMesh>();
        _nameText.text = Protocol.NormalizePlayerName(playerName);
        _nameText.anchor = TextAnchor.MiddleCenter;
        _nameText.alignment = TextAlignment.Center;
        _nameText.fontSize = 64;
        _nameText.characterSize = 0.025f;
        _nameText.color = Color.white;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null)
        {
            _nameText.font = font;
            _nameObject.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }
        var nameRenderer = _nameObject.GetComponent<MeshRenderer>();
        nameRenderer.sortingLayerID = _renderer.sortingLayerID;
        nameRenderer.sortingOrder = _renderer.sortingOrder + 100;
        _nameObject.transform.position = position + Vector3.up * _nameOffset;
    }

    private void CreateVisualRenderer()
    {
        var visual = new GameObject("DTMP Remote Visual");
        visual.transform.SetParent(_gameObject.transform, false);
        var renderer = visual.AddComponent<SpriteRenderer>();
        renderer.color = CurrentTint;
        _visualRenderers.Add(new RemoteVisual { Renderer = renderer });
    }

    private Color CurrentTint => _isRemoteDead
        ? new Color(0.55f, 0.62f, 0.68f, 0.75f)
        : RemoteTint;

    private void ApplyTint()
    {
        if (_renderer != null)
            _renderer.color = CurrentTint;
        foreach (var visual in _visualRenderers)
            if (visual.Renderer != null)
                visual.Renderer.color = CurrentTint;
    }

    private static Transform FindPlayerRoot(SpriteRenderer renderer)
    {
        Component player = renderer.GetComponentInParent<PlayerCharacter>();
        player ??= renderer.GetComponentInParent<LobbyPlayer>();
        player ??= renderer.GetComponentInParent<SushiBarPlayerHanlder>();
        return player != null ? player.transform : renderer.transform.parent;
    }

    private Sprite ResolveSprite(uint id)
    {
        if (_sprites.TryGetValue(id, out var sprite))
            return sprite;
        CacheSprites();
        return _sprites.TryGetValue(id, out sprite) ? sprite : null;
    }

    private void CacheSprites()
    {
        foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (sprite != null)
                _sprites.TryAdd(Protocol.SceneId(sprite.name), sprite);
        }
    }

    public void Dispose() => Clear();
}
