using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaveTheDiverMP;

[BepInPlugin("dev.davethedivermp", "Dave the Diver Multiplayer", "0.7.0")]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        Protocol.SelfTest();

        var role = Config.Bind("Network", "Role", SessionRole.Offline,
            "Offline, Host, or Client");
        var address = Config.Bind("Network", "Address", "127.0.0.1",
            "Host IPv4 address used by clients");
        var port = Config.Bind("Network", "Port", 27777,
            new ConfigDescription("UDP listen port", new AcceptableValueRange<int>(1024, 65535)));
        var playerName = Config.Bind("Network", "PlayerName", string.Empty,
            "Name shown above your diver; blank uses the Steam name");

        ProbeBehaviour.Logger = Log;
        ProbeBehaviour.Role = role.Value;
        ProbeBehaviour.Address = address.Value;
        ProbeBehaviour.Port = port.Value;
        ProbeBehaviour.ConfiguredName = playerName.Value;
        try
        {
            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, "dev.davethedivermp");
            Log.LogInfo("Gameplay patches active");
        }
        catch (Exception exception)
        {
            Log.LogError($"Gameplay patches failed: {exception.Message}");
        }
        Log.LogInfo($"Probe loaded; Unity {Application.unityVersion}; Steam running: {SteamAPI.IsSteamRunning()}");
        AddComponent<ProbeBehaviour>();
    }

    internal static string ResolvePlayerName(string configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName))
            return Protocol.NormalizePlayerName(configuredName);

        try
        {
            return Protocol.NormalizePlayerName(SteamFriends.GetPersonaName());
        }
        catch
        {
            return "Diver";
        }
    }
}

public sealed class ProbeBehaviour : MonoBehaviour
{
    internal static ProbeBehaviour Instance { get; private set; }
    internal static ManualLogSource Logger { get; set; }
    internal static SessionRole Role { get; set; }
    internal static string Address { get; set; } = string.Empty;
    internal static int Port { get; set; }
    internal static string ConfiguredName { get; set; } = string.Empty;

    private string _scene = string.Empty;
    private bool _playerPresent;
    private float _nextScan;
    private float _nextPositionLog;
    private float _nextSnapshot;
    private uint _sceneId;
    private PlayerCharacter _player;
    private SpriteRenderer _playerRenderer;
    private UdpSession _session;
    private FishReplicator _fishReplicator;
    private PickupReplicator _pickupReplicator;
    private readonly RemoteAvatar _remoteAvatar = new();

    public ProbeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private void Start()
    {
        Instance = this;
        var localName = Plugin.ResolvePlayerName(ConfiguredName);
        var buildId = Protocol.SceneId(
            $"{Application.buildGUID}|{Application.version}|{Application.unityVersion}");
        _session = new UdpSession(Logger);
        _fishReplicator = new FishReplicator(Logger);
        _pickupReplicator = new PickupReplicator(Logger);
        _session.Start(Role, Address, Port, localName, buildId);
        Logger.LogInfo($"Network identity: {localName}; build={buildId:X8}");
    }

    private void Update()
    {
        var scene = SceneManager.GetActiveScene().name;
        if (scene != _scene)
        {
            _scene = scene;
            _sceneId = Protocol.SceneId(scene);
            _player = null;
            _playerRenderer = null;
            _remoteAvatar.Clear();
            _fishReplicator?.Clear();
            _pickupReplicator?.Clear();
            _session?.SetLocalScene(_sceneId);
            Logger.LogInfo($"Scene: {_scene}");
        }

        _session?.Update(Time.realtimeSinceStartup);
        _fishReplicator?.Update(
            Role, _session, _sceneId, Time.realtimeSinceStartup, Time.deltaTime);
        _pickupReplicator?.Update(Role, _session, _sceneId, Time.realtimeSinceStartup);

        while (_session != null && _session.TryTakeSnapshot(out var snapshot))
        {
            if (_player != null && _session.SceneMatches(_sceneId) && snapshot.SceneId == _sceneId)
                _remoteAvatar.Apply(snapshot, _player, _session.RemoteName);
            else
                _remoteAvatar.Clear();
        }
        _remoteAvatar.Update(Time.deltaTime);

        if (_session != null && _session.SceneMatches(_sceneId) &&
            _player != null && Time.realtimeSinceStartup >= _nextSnapshot)
        {
            var position = _player.transform.position;
            var controller = _player.Controller2D;
            _playerRenderer ??= RemoteAvatar.FindPrimaryRenderer(_player);
            var velocity = controller != null ? controller.GetVelocity() : Vector2.zero;
            var rotation = _playerRenderer != null
                ? _playerRenderer.transform.eulerAngles.z
                : controller != null ? controller.GetRotation() : _player.transform.eulerAngles.z;
            var flipped = _playerRenderer != null
                ? _playerRenderer.flipX
                : controller != null && controller.IsFliped();
            var spriteId = _playerRenderer != null && _playerRenderer.sprite != null
                ? Protocol.SceneId(_playerRenderer.sprite.name)
                : 0u;
            var visualScale = _playerRenderer != null
                ? _playerRenderer.transform.lossyScale
                : _player.transform.lossyScale;
            _session.SendSnapshot(new PlayerSnapshot(
                _sceneId,
                position.x,
                position.y,
                position.z,
                rotation,
                velocity.x,
                velocity.y,
                spriteId,
                visualScale.x,
                visualScale.y,
                flipped));
            _nextSnapshot = Time.realtimeSinceStartup + 0.05f;
        }
        else if (_session == null || !_session.SceneMatches(_sceneId))
        {
            _remoteAvatar.Clear();
        }

        if (Time.realtimeSinceStartup < _nextScan)
            return;

        _nextScan = Time.realtimeSinceStartup + 1f;

        var player = UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>();
        if (player == null)
        {
            if (_playerPresent)
                Logger.LogInfo("PlayerCharacter left the scene");
            _playerPresent = false;
            _player = null;
            _playerRenderer = null;
            return;
        }

        if (_player != player)
            _playerRenderer = RemoteAvatar.FindPrimaryRenderer(player);
        _player = player;

        if (!_playerPresent || Time.realtimeSinceStartup >= _nextPositionLog)
        {
            var position = player.transform.position;
            var controller = player.Controller2D;
            var rotation = controller != null ? controller.GetRotation() : player.transform.eulerAngles.z;
            var velocity = controller != null ? controller.GetVelocity() : Vector2.zero;
            var visualRotation = _playerRenderer != null
                ? _playerRenderer.transform.eulerAngles.z
                : rotation;
            var spriteName = _playerRenderer != null && _playerRenderer.sprite != null
                ? _playerRenderer.sprite.name
                : "none";
            var visualScale = _playerRenderer != null
                ? _playerRenderer.transform.lossyScale
                : player.transform.lossyScale;
            Logger.LogInfo(
                $"PlayerCharacter: ({position.x:F2}, {position.y:F2}, {position.z:F2}); " +
                $"rotation={rotation:F1}/{visualRotation:F1}; velocity=({velocity.x:F2}, {velocity.y:F2}); " +
                $"scale=({visualScale.x:F2}, {visualScale.y:F2}); sprite={spriteName}");
            _nextPositionLog = Time.realtimeSinceStartup + 5f;
        }

        _playerPresent = true;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        _session?.Dispose();
        _fishReplicator?.Clear();
        _pickupReplicator?.Clear();
        _remoteAvatar.Dispose();
    }

    internal void OnPickupDestroyed(PickupInstanceItem item)
    {
        if (Role == SessionRole.Host)
            _pickupReplicator?.OnHostDestroyed(_session, _sceneId, item);
    }
}
