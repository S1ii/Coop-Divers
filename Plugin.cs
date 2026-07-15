using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DR.AI;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaveTheDiverMP;

[BepInPlugin("dev.davethedivermp", "Dave the Diver Multiplayer", "0.11.0")]
public sealed class Plugin : BasePlugin
{
    private static ConfigEntry<SessionRole> _roleConfig;
    private static ConfigEntry<string> _addressConfig;
    private static ConfigEntry<int> _portConfig;
    private static ConfigEntry<string> _playerNameConfig;
    private static ConfigFile _config;

    public override void Load()
    {
        Protocol.SelfTest();
        LobbyInput.SelfTest();

        _config = Config;
        _roleConfig = Config.Bind("Network", "Role", SessionRole.Offline,
            "Offline, Host, or Client");
        _addressConfig = Config.Bind("Network", "Address", "127.0.0.1",
            "Host IPv4 address used by clients");
        _portConfig = Config.Bind("Network", "Port", 27777,
            new ConfigDescription("UDP listen port", new AcceptableValueRange<int>(1024, 65535)));
        _playerNameConfig = Config.Bind("Network", "PlayerName", string.Empty,
            "Name shown above your diver; blank uses the Steam name");

        ProbeBehaviour.Logger = Log;
        ProbeBehaviour.Role = _roleConfig.Value;
        ProbeBehaviour.Address = _addressConfig.Value;
        ProbeBehaviour.Port = _portConfig.Value;
        ProbeBehaviour.ConfiguredName = _playerNameConfig.Value;
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

    internal static void SaveNetworkSettings(
        SessionRole role,
        string address,
        int port,
        string playerName)
    {
        _roleConfig.Value = role;
        _addressConfig.Value = address;
        _portConfig.Value = port;
        _playerNameConfig.Value = playerName;
        _config.Save();
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
    private uint _buildId;
    private string _localName = "Diver";
    private PlayerCharacter _player;
    private SpriteRenderer _playerRenderer;
    private UdpSession _session;
    private FishReplicator _fishReplicator;
    private PickupReplicator _pickupReplicator;
    private SceneReplicator _sceneReplicator;
    private readonly RemoteAvatar _remoteAvatar = new();
    private bool _showLobby;
    private bool _cursorWasVisible;
    private CursorLockMode _cursorWasLocked;
    private string _lanAddress = "unknown";
    private DRInput.DRInputAsset.DRInputAssetEntry _lobbyInputEntry;
    private bool _ownsLobbyInputLock;
    private Rect _lobbyRect = new(20f, 20f, 420f, 280f);
    private string _lobbyAddress = "127.0.0.1";
    private string _lobbyPort = "27777";
    private string _lobbyName = string.Empty;
    private string _lobbyError = string.Empty;

    public ProbeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private void Start()
    {
        Instance = this;
        _buildId = Protocol.SceneId(
            $"{Application.buildGUID}|{Application.version}|{Application.unityVersion}");
        _fishReplicator = new FishReplicator(Logger);
        _pickupReplicator = new PickupReplicator(Logger);
        _sceneReplicator = new SceneReplicator(Logger);
        _lobbyAddress = Address;
        _lobbyPort = Port.ToString();
        _lobbyName = ConfiguredName;
        if (!SwitchSession(Role, Address, Port, ConfiguredName, false, out _lobbyError))
            SwitchSession(SessionRole.Offline, Address, Port, ConfiguredName, false, out _);
    }

    private void OnGUI()
    {
        var current = Event.current;
        if (current != null && current.type == EventType.KeyDown)
        {
            if (current.keyCode == KeyCode.F8)
            {
                SetLobbyVisible(!_showLobby);
                current.Use();
            }
            else if (_showLobby && current.keyCode == KeyCode.Escape)
            {
                SetLobbyVisible(false);
                current.Use();
            }
        }

        if (_showLobby)
        {
            if (!_ownsLobbyInputLock)
                AcquireLobbyInputLock();
            DrawLobbyPanel();
        }
    }

    private void Update()
    {
        var scene = SceneManager.GetActiveScene().name;
        if (scene != _scene)
        {
            if (_showLobby)
                SetLobbyVisible(false);
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
        _sceneReplicator?.Update(Role, _session);
        _fishReplicator?.Update(
            Role, _session, _sceneId, Time.realtimeSinceStartup, Time.unscaledDeltaTime, _player);
        _pickupReplicator?.Update(
            Role, _session, _sceneId, Time.realtimeSinceStartup, _player);

        while (_session != null && _session.TryTakeSnapshot(out var snapshot))
        {
            if (_player != null && _session.SceneMatches(_sceneId) && snapshot.SceneId == _sceneId)
                _remoteAvatar.Apply(snapshot, _player, _session.RemoteName);
            else
                _remoteAvatar.Clear();
        }
        _remoteAvatar.Update(Time.unscaledDeltaTime);

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
        if (_showLobby)
            SetLobbyVisible(false);
        else
            ReleaseLobbyInputLock();
        if (Instance == this)
            Instance = null;
        _session?.Dispose();
        _fishReplicator?.Clear();
        _pickupReplicator?.Clear();
        _sceneReplicator?.Clear();
        _remoteAvatar.Dispose();
    }

    private void DrawLobbyPanel()
    {
        var running = _session != null && _session.IsRunning;
        var connected = _session != null && _session.Connected;
        var peer = _session != null ? _session.RemoteName : "Diver";
        GUI.BeginGroup(_lobbyRect);
        GUI.Box(new Rect(0f, 0f, _lobbyRect.width, _lobbyRect.height), "Dave the Diver Multiplayer");
        GUI.Label(new Rect(16f, 30f, 388f, 24f),
            $"Status: {LobbyInput.Status(Role, running, connected, peer)}");
        if (Role == SessionRole.Host)
            GUI.Label(new Rect(16f, 54f, 388f, 24f), $"LAN address: {_lanAddress}");

        GUI.Label(new Rect(16f, 84f, 70f, 24f), "Name");
        _lobbyName = GUI.TextField(new Rect(90f, 84f, 314f, 24f), _lobbyName ?? string.Empty, 24);
        GUI.Label(new Rect(16f, 114f, 70f, 24f), "Host IP");
        _lobbyAddress = GUI.TextField(
            new Rect(90f, 114f, 314f, 24f), _lobbyAddress ?? string.Empty, 45);
        GUI.Label(new Rect(16f, 144f, 70f, 24f), "Port");
        _lobbyPort = GUI.TextField(new Rect(90f, 144f, 110f, 24f), _lobbyPort ?? string.Empty, 5);

        if (GUI.Button(new Rect(16f, 178f, 190f, 28f), "Host"))
            SwitchFromLobby(SessionRole.Host);
        if (GUI.Button(new Rect(214f, 178f, 190f, 28f), "Join"))
            SwitchFromLobby(SessionRole.Client);
        if (GUI.Button(new Rect(16f, 212f, 190f, 28f), "Disconnect / Offline"))
            SwitchSession(SessionRole.Offline, _lobbyAddress, Port, _lobbyName, true, out _lobbyError);

        if (!string.IsNullOrEmpty(_lobbyError))
            GUI.Label(new Rect(16f, 244f, 388f, 24f), _lobbyError);
        else
            GUI.Label(new Rect(16f, 244f, 190f, 24f), "F8: toggle   Esc: close");
        if (GUI.Button(new Rect(214f, 212f, 190f, 28f), "Close"))
            SetLobbyVisible(false);
        GUI.EndGroup();
    }

    private void SwitchFromLobby(SessionRole role)
    {
        if (!LobbyInput.TryValidate(
                role, _lobbyAddress, _lobbyPort,
                out var address, out var port, out _lobbyError))
            return;
        SwitchSession(role, address, port, _lobbyName, true, out _lobbyError);
    }

    private bool SwitchSession(
        SessionRole role,
        string address,
        int port,
        string configuredName,
        bool persist,
        out string error)
    {
        error = string.Empty;
        if (role != SessionRole.Offline &&
            !LobbyInput.TryValidate(role, address, port.ToString(), out address, out port, out error))
            return false;

        var localName = Plugin.ResolvePlayerName(configuredName);
        if (_session != null && role == Role && role != SessionRole.Offline &&
            _session.IsRunning && address == Address && port == Port && localName == _localName)
        {
            ConfiguredName = configuredName ?? string.Empty;
            SaveSelectedSettings(role, address, port, configuredName, persist, ref error);
            return true;
        }

        var previous = _session;
        var previousRunning = previous != null && previous.IsRunning;
        var disposedPrevious = previous != null && (!previousRunning ||
            Role == SessionRole.Host && role == SessionRole.Host && Port == port);
        if (disposedPrevious)
            previous.Dispose();

        var replacement = new UdpSession(Logger);
        replacement.Start(role, address, port, localName, _buildId);
        if (role != SessionRole.Offline && !replacement.IsRunning)
        {
            replacement.Dispose();
            if (previousRunning && !disposedPrevious)
            {
                error = "Network start failed; current session kept";
                return false;
            }

            previous?.Dispose();
            ClearReplicationState();
            _session = new UdpSession(Logger);
            _session.Start(SessionRole.Offline, address, port, localName, _buildId);
            _localName = localName;
            Role = SessionRole.Offline;
            Address = address;
            Port = port;
            ConfiguredName = configuredName ?? string.Empty;
            SaveSelectedSettings(Role, Address, Port, ConfiguredName, persist, ref error);
            error = "Network start failed; see BepInEx log";
            return false;
        }

        if (!disposedPrevious)
            previous?.Dispose();
        ClearReplicationState();
        _session = replacement;
        _localName = localName;
        Role = role;
        Address = address;
        Port = port;
        ConfiguredName = configuredName ?? string.Empty;
        _session.SetLocalScene(_sceneId);
        _nextSnapshot = 0f;
        SaveSelectedSettings(Role, Address, Port, ConfiguredName, persist, ref error);
        Logger.LogInfo($"Network identity: {_localName}; build={_buildId:X8}; role={Role}");
        return true;
    }

    private static void SaveSelectedSettings(
        SessionRole role,
        string address,
        int port,
        string configuredName,
        bool persist,
        ref string error)
    {
        if (!persist)
            return;
        try
        {
            Plugin.SaveNetworkSettings(role, address, port, configuredName ?? string.Empty);
        }
        catch (Exception exception)
        {
            error = "Connected, but config could not be saved";
            Logger.LogWarning($"Network config save failed: {exception.Message}");
        }
    }

    private void ClearReplicationState()
    {
        _remoteAvatar.Clear();
        _fishReplicator?.Clear();
        _pickupReplicator?.Clear();
        _sceneReplicator?.Clear();
    }

    private void SetLobbyVisible(bool visible)
    {
        if (_showLobby == visible)
            return;
        _showLobby = visible;
        if (visible)
        {
            AcquireLobbyInputLock();
            _lanAddress = LobbyInput.FindLanAddress();
            _cursorWasVisible = Cursor.visible;
            _cursorWasLocked = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            ReleaseLobbyInputLock();
            if (Cursor.visible)
                Cursor.visible = _cursorWasVisible;
            if (Cursor.lockState == CursorLockMode.None)
                Cursor.lockState = _cursorWasLocked;
        }
    }

    private void AcquireLobbyInputLock()
    {
        if (_ownsLobbyInputLock)
            return;
        try
        {
            var entry = DRInput.DRInputAsset.entryTemp;
            if (entry == null)
                return;
            entry.Lock();
            _lobbyInputEntry = entry;
            _ownsLobbyInputLock = true;
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Lobby input lock failed: {exception.Message}");
        }
    }

    private void ReleaseLobbyInputLock()
    {
        if (!_ownsLobbyInputLock)
            return;
        var entry = _lobbyInputEntry;
        _lobbyInputEntry = null;
        _ownsLobbyInputLock = false;
        try
        {
            entry?.UnLock();
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Lobby input unlock failed: {exception.Message}");
        }
    }

    internal void OnPickupDestroyed(PickupInstanceItem item)
    {
        if (Role == SessionRole.Host)
            _pickupReplicator?.OnHostDestroyed(_session, _sceneId, item);
    }

    internal bool OnPickupInteract(PickupInstanceItem item)
    {
        if (Role != SessionRole.Client)
            return true;
        _pickupReplicator?.RequestPickup(_session, _sceneId, item);
        return false;
    }

    internal bool AllowFishDamage(
        FishAISystem fish,
        int damage,
        EElement element,
        AttackType attackType)
    {
        if (Role != SessionRole.Client || _session == null ||
            !_session.SceneMatches(_sceneId) ||
            !FishReplicator.IsPlayerAttack(attackType))
            return true;
        return !(_fishReplicator?.RequestDamage(
            _session, _sceneId, fish, damage, element, attackType) ?? false);
    }

    internal bool AllowFishPickup(FishInteractionBody body)
    {
        if (Role != SessionRole.Client || _session == null ||
            !_session.SceneMatches(_sceneId) || body == null)
            return true;
        var fish = body.GetComponentInParent<FishAISystem>();
        if (fish == null)
            return true;
        _fishReplicator?.RequestPickup(_session, _sceneId, fish);
        return false;
    }

    internal void OnSceneTransition(
        string sceneName,
        SceneTransitionType transitionType,
        bool throughEmptyScene,
        bool initLoading,
        bool useStartTransition,
        bool useFinishTransition,
        bool unloadActiveScene,
        bool ignoreSameSceneCheck,
        bool isRetry,
        bool skipEmptySceneOptionIsUnloadAssets,
        bool firstFindSceneManagerInActiveScene)
    {
        if (Role != SessionRole.Host)
            return;
        _sceneReplicator?.OnHostTransition(
            _session,
            sceneName,
            transitionType,
            throughEmptyScene,
            initLoading,
            useStartTransition,
            useFinishTransition,
            unloadActiveScene,
            ignoreSameSceneCheck,
            isRetry,
            skipEmptySceneOptionIsUnloadAssets,
            firstFindSceneManagerInActiveScene);
    }
}
