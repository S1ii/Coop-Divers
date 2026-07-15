using System;
using TMPro;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class RemoteAvatar : IDisposable
{
    private GameObject _gameObject;
    private SpriteRenderer _renderer;
    private SpriteRenderer _sourceRenderer;
    private GameObject _nameObject;
    private TextMeshPro _nameText;
    private Vector3 _target;
    private Vector3 _velocity;
    private float _targetRotation;
    private float _timeSinceSnapshot;
    private float _nameOffset = 0.9f;
    private bool _flipped;
    private bool _initialized;

    internal void Apply(PlayerSnapshot snapshot, PlayerCharacter localPlayer, string playerName)
    {
        if (_gameObject == null)
            Create(localPlayer, new Vector3(snapshot.X, snapshot.Y, snapshot.Z), playerName);

        _target = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
        _velocity = new Vector3(snapshot.VelocityX, snapshot.VelocityY, 0f);
        _targetRotation = snapshot.Rotation;
        _timeSinceSnapshot = 0f;
        _flipped = snapshot.Flipped;
        _nameText.text = Protocol.NormalizePlayerName(playerName);

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

        if (_sourceRenderer != null && _sourceRenderer.sprite != null)
        {
            _renderer.sprite = _sourceRenderer.sprite;
            _renderer.flipX = _flipped;
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
        _sourceRenderer = null;
        _nameObject = null;
        _nameText = null;
        _initialized = false;
    }

    private void Create(PlayerCharacter localPlayer, Vector3 position, string playerName)
    {
        foreach (var candidate in localPlayer.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (candidate != null && candidate.sprite != null)
            {
                _sourceRenderer = candidate;
                break;
            }
        }

        _gameObject = new GameObject("DTMP Remote Diver");
        _gameObject.transform.position = position;
        _renderer = _gameObject.AddComponent<SpriteRenderer>();
        _renderer.color = new Color(0.55f, 0.9f, 1f, 0.85f);

        if (_sourceRenderer != null)
        {
            _renderer.sprite = _sourceRenderer.sprite;
            _renderer.sortingLayerID = _sourceRenderer.sortingLayerID;
            _renderer.sortingOrder = _sourceRenderer.sortingOrder + 1;
            _gameObject.transform.localScale = _sourceRenderer.transform.lossyScale;
            _nameOffset = Mathf.Max(0.7f, _renderer.bounds.extents.magnitude + 0.3f);
        }

        _nameObject = new GameObject("DTMP Remote Name");
        _nameText = _nameObject.AddComponent<TextMeshPro>();
        _nameText.text = Protocol.NormalizePlayerName(playerName);
        _nameText.alignment = TextAlignmentOptions.Center;
        _nameText.fontSize = 4f;
        _nameText.color = Color.white;
        _nameText.outlineColor = new Color32(0, 0, 0, 230);
        _nameText.outlineWidth = 0.22f;
        foreach (var existingText in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (existingText != null && existingText.font != null)
            {
                _nameText.font = existingText.font;
                break;
            }
        }
        _nameText.sortingLayerID = _renderer.sortingLayerID;
        _nameText.sortingOrder = _renderer.sortingOrder + 100;
        _nameText.rectTransform.sizeDelta = new Vector2(20f, 4f);
        _nameObject.transform.localScale = Vector3.one * 0.1f;
        _nameObject.transform.position = position + Vector3.up * _nameOffset;
    }

    public void Dispose() => Clear();
}
