using System;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class RemoteAvatar : IDisposable
{
    private GameObject _gameObject;
    private SpriteRenderer _renderer;
    private SpriteRenderer _sourceRenderer;
    private Vector3 _target;
    private bool _flipped;

    internal void Apply(PlayerSnapshot snapshot, PlayerCharacter localPlayer)
    {
        if (_gameObject == null)
            Create(localPlayer, new Vector3(snapshot.X, snapshot.Y, snapshot.Z));

        _target = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
        _flipped = snapshot.Flipped;
    }

    internal void Update(float deltaTime)
    {
        if (_gameObject == null)
            return;

        _gameObject.transform.position = Vector3.Lerp(
            _gameObject.transform.position,
            _target,
            1f - Mathf.Exp(-15f * deltaTime));

        if (_sourceRenderer != null && _sourceRenderer.sprite != null)
        {
            _renderer.sprite = _sourceRenderer.sprite;
            _renderer.flipX = _flipped;
        }
    }

    internal void Clear()
    {
        if (_gameObject != null)
            UnityEngine.Object.Destroy(_gameObject);
        _gameObject = null;
        _renderer = null;
        _sourceRenderer = null;
    }

    private void Create(PlayerCharacter localPlayer, Vector3 position)
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
        }
    }

    public void Dispose() => Clear();
}
