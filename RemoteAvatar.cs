using System;
using System.Collections.Generic;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class RemoteAvatar : IDisposable
{
    private GameObject _gameObject;
    private SpriteRenderer _renderer;
    private GameObject _nameObject;
    private TextMesh _nameText;
    private readonly Dictionary<uint, Sprite> _sprites = new();
    private Vector3 _target;
    private Vector3 _velocity;
    private float _targetRotation;
    private float _timeSinceSnapshot;
    private float _nameOffset = 0.9f;
    private bool _initialized;

    internal void Apply(PlayerSnapshot snapshot, PlayerCharacter localPlayer, string playerName)
    {
        if (_gameObject == null)
            Create(localPlayer, new Vector3(snapshot.X, snapshot.Y, snapshot.Z), playerName);

        _target = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
        _velocity = new Vector3(snapshot.VelocityX, snapshot.VelocityY, 0f);
        _targetRotation = snapshot.Rotation;
        _timeSinceSnapshot = 0f;
        _nameText.text = Protocol.NormalizePlayerName(playerName);
        if (snapshot.SpriteId != 0 && _sprites.TryGetValue(snapshot.SpriteId, out var sprite))
            _renderer.sprite = sprite;
        _renderer.flipX = snapshot.Flipped;

        if (!_initialized || (_gameObject.transform.position - _target).sqrMagnitude > 64f)
        {
            _gameObject.transform.position = _target;
            _gameObject.transform.rotation = Quaternion.Euler(0f, 0f, _targetRotation);
            _initialized = true;
        }
    }

    internal void Update(float deltaTime)
    {
        if (_gameObject == null)
            return;

        _timeSinceSnapshot = Mathf.Min(_timeSinceSnapshot + deltaTime, 0.15f);
        var predictedPosition = _target + _velocity * _timeSinceSnapshot;
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
        _initialized = false;
    }

    internal static SpriteRenderer FindPrimaryRenderer(PlayerCharacter player)
    {
        foreach (var candidate in player.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (candidate != null && candidate.sprite != null)
                return candidate;
        }
        return null;
    }

    private void Create(PlayerCharacter localPlayer, Vector3 position, string playerName)
    {
        var sourceRenderer = FindPrimaryRenderer(localPlayer);

        _gameObject = new GameObject("DTMP Remote Diver");
        _gameObject.transform.position = position;
        _renderer = _gameObject.AddComponent<SpriteRenderer>();
        _renderer.color = new Color(0.55f, 0.9f, 1f, 0.85f);

        if (sourceRenderer != null)
        {
            _renderer.sprite = sourceRenderer.sprite;
            _renderer.sortingLayerID = sourceRenderer.sortingLayerID;
            _renderer.sortingOrder = sourceRenderer.sortingOrder + 1;
            _gameObject.transform.localScale = sourceRenderer.transform.lossyScale;
            _nameOffset = Mathf.Max(0.35f, _renderer.bounds.extents.magnitude + 0.1f);

            var sourceTexture = sourceRenderer.sprite.texture;
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null)
                    continue;
                var id = Protocol.SceneId(sprite.name);
                if (sprite.texture == sourceTexture || !_sprites.ContainsKey(id))
                    _sprites[id] = sprite;
            }
        }

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

    public void Dispose() => Clear();
}
