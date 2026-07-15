using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaveTheDiverMP;

[BepInPlugin("dev.davethedivermp", "Dave the Diver Multiplayer", "0.1.0")]
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

        ProbeBehaviour.Logger = Log;
        ProbeBehaviour.Role = role.Value;
        ProbeBehaviour.Address = address.Value;
        ProbeBehaviour.Port = port.Value;
        Log.LogInfo($"Probe loaded; Unity {Application.unityVersion}; Steam running: {SteamAPI.IsSteamRunning()}");
        AddComponent<ProbeBehaviour>();
    }
}

public sealed class ProbeBehaviour : MonoBehaviour
{
    internal static ManualLogSource Logger { get; set; }
    internal static SessionRole Role { get; set; }
    internal static string Address { get; set; } = string.Empty;
    internal static int Port { get; set; }

    private string _scene = string.Empty;
    private bool _playerPresent;
    private float _nextScan;
    private float _nextPositionLog;
    private float _nextSnapshot;
    private uint _sceneId;
    private PlayerCharacter _player;
    private UdpSession _session;
    private readonly RemoteAvatar _remoteAvatar = new();

    public ProbeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private void Start()
    {
        _session = new UdpSession(Logger);
        _session.Start(Role, Address, Port);
    }

    private void Update()
    {
        var scene = SceneManager.GetActiveScene().name;
        if (scene != _scene)
        {
            _scene = scene;
            _sceneId = Protocol.SceneId(scene);
            _player = null;
            _remoteAvatar.Clear();
            Logger.LogInfo($"Scene: {_scene}");
        }

        _session?.Update(Time.realtimeSinceStartup);

        while (_session != null && _session.TryTakeSnapshot(out var snapshot))
        {
            if (_player != null && snapshot.SceneId == _sceneId)
                _remoteAvatar.Apply(snapshot, _player);
            else
                _remoteAvatar.Clear();
        }
        _remoteAvatar.Update(Time.deltaTime);

        if (_session?.Connected == true && _player != null && Time.realtimeSinceStartup >= _nextSnapshot)
        {
            var position = _player.transform.position;
            var flipped = _player.Controller2D != null && _player.Controller2D.IsFliped();
            _session.SendSnapshot(new PlayerSnapshot(_sceneId, position.x, position.y, position.z, flipped));
            _nextSnapshot = Time.realtimeSinceStartup + 0.05f;
        }
        else if (_session?.Connected != true)
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
            return;
        }

        _player = player;

        if (!_playerPresent || Time.realtimeSinceStartup >= _nextPositionLog)
        {
            var position = player.transform.position;
            Logger.LogInfo($"PlayerCharacter: ({position.x:F2}, {position.y:F2}, {position.z:F2})");
            _nextPositionLog = Time.realtimeSinceStartup + 5f;
        }

        _playerPresent = true;
    }

    private void OnDestroy()
    {
        _session?.Dispose();
        _remoteAvatar.Dispose();
    }
}
