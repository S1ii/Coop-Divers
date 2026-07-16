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
    private float _nextVisual;
    private uint _sceneId;
    private uint _buildId;
    private string _localName = "Diver";
    private PlayerCharacter _player;
    private SpriteRenderer _playerRenderer;
    private LobbyPlayer _lobbyPlayer;
    private SpriteRenderer _lobbyRenderer;
    private SushiBarPlayerHanlder _sushiPlayer;
    private SpriteRenderer _sushiRenderer;
    internal UdpSession _session;
    private FishReplicator _fishReplicator;
    private PickupReplicator _pickupReplicator;
    private SceneReplicator _sceneReplicator;
    private MissionProgressReplicator _missionProgressReplicator;
    private ManagerEventReplicator _managerEventReplicator;
    private WorldStateReplicator _worldStateReplicator;
    private BossReplicator _bossReplicator;
    private IngredientsReplicator _ingredientsReplicator;
    private BoatDecoReplicator _boatDecoReplicator;
    private DiveCoordinator _diveCoordinator;
    private TravelCoordinator _travelCoordinator;
    private ProjectileVisualReplicator _projectileVisualReplicator;
    private RemoteCatchLedger _remoteCatchLedger;
    private SessionTrace _sessionTrace;
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
        Application.runInBackground = true;
        _buildId = Protocol.SceneId(
            $"{Application.buildGUID}|{Application.version}|{Application.unityVersion}|" +
            typeof(Plugin).Module.ModuleVersionId);
        _sessionTrace = new SessionTrace(Logger);
        _remoteCatchLedger = new RemoteCatchLedger(Logger);
        _fishReplicator = new FishReplicator(Logger, _sessionTrace);
        _pickupReplicator = new PickupReplicator(Logger, _remoteCatchLedger, _sessionTrace);
        _sceneReplicator = new SceneReplicator(Logger);
        _missionProgressReplicator = new MissionProgressReplicator(Logger, _sessionTrace);
        _managerEventReplicator = new ManagerEventReplicator(Logger);
        _worldStateReplicator = new WorldStateReplicator(Logger);
        _bossReplicator = new BossReplicator(Logger);
        _ingredientsReplicator = new IngredientsReplicator(Logger);
        _boatDecoReplicator = new BoatDecoReplicator(Logger);
        _diveCoordinator = new DiveCoordinator(Logger);
        _travelCoordinator = new TravelCoordinator(Logger);
        _projectileVisualReplicator = new ProjectileVisualReplicator();
        _lobbyAddress = Address;
        _lobbyPort = Port.ToString();
        _lobbyName = ConfiguredName;
        if (!SwitchSession(SessionRole.Offline, Address, Port, ConfiguredName, false, out _lobbyError))
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
            var wasDiveScene = IsDiveSceneName(_scene);
            if (_showLobby)
                SetLobbyVisible(false, false);
            _scene = scene;
            _sceneId = Protocol.SceneId(scene);
            if (!wasDiveScene && IsDiveSceneName(scene))
                _remoteCatchLedger?.BeginDive(_session, Time.realtimeSinceStartup);
            _player = null;
            _playerRenderer = null;
            _lobbyPlayer = null;
            _lobbyRenderer = null;
            _sushiPlayer = null;
            _sushiRenderer = null;
            _remoteAvatar.Clear();
            _fishReplicator?.Clear();
            _pickupReplicator?.Clear();
            _bossReplicator?.Clear();
            _boatDecoReplicator?.Clear();
            _diveCoordinator?.Reset();
            _travelCoordinator?.Reset();
            _projectileVisualReplicator?.Clear();
            _session?.SetLocalScene(_sceneId);
            if (Role == SessionRole.Host)
                _sceneReplicator?.OnHostObservedScene(_session, _scene);
            Logger.LogInfo($"Scene: {_scene}");
            _sessionTrace?.Write("SCENE", $"entered name={_scene} id={_sceneId:X8}");
        }

        _session?.Update(Time.realtimeSinceStartup);
        if (Role == SessionRole.Client && _session != null && _session.TryTakePeerLoss(out var peerLoss))
        {
            ReturnToOnlineRoom(peerLoss);
            return;
        }
        TitleOnlineMenu.Tick(this);
        _remoteCatchLedger?.Update(Role, _session, _scene, Time.realtimeSinceStartup);
        _managerEventReplicator?.Update(Role, _session, _sceneId, Time.realtimeSinceStartup);
        _missionProgressReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        _worldStateReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        _ingredientsReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        _boatDecoReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        _sceneReplicator?.Update(Role, _session);
        _diveCoordinator?.Update(
            Role, _session, Time.realtimeSinceStartup, _player, _remoteAvatar.Transform);
        _travelCoordinator?.Update(
            Role, _session,
            _diveCoordinator?.HostDead ?? false,
            _diveCoordinator?.ClientDead ?? false);
        _projectileVisualReplicator?.Update(_session, _sceneId, Time.realtimeSinceStartup);
        _fishReplicator?.Update(
            Role, _session, _sceneId, Time.realtimeSinceStartup, Time.unscaledDeltaTime,
            _player, _remoteAvatar.Transform);
        _bossReplicator?.Update(
            Role, _session, _sceneId, Time.realtimeSinceStartup, Time.unscaledDeltaTime);
        _pickupReplicator?.Update(
            Role, _session, _sceneId, Time.realtimeSinceStartup, _player);

        while (_session != null && _session.TryTakeSnapshot(out var snapshot))
        {
            var renderer = _playerRenderer ?? _lobbyRenderer ?? _sushiRenderer;
            if (renderer != null && _session.SceneMatches(_sceneId) && snapshot.SceneId == _sceneId)
                _remoteAvatar.Apply(snapshot, renderer, _session.RemoteName);
            else
                _remoteAvatar.Clear();
        }
        while (_session != null && _session.TryTakePlayerVisualState(out var visualState))
        {
            if (_session.SceneMatches(_sceneId) && visualState.SceneId == _sceneId)
                _remoteAvatar.ApplyVisual(visualState, _session.RemoteName);
        }
        _remoteAvatar.Update(Time.unscaledDeltaTime);

        if (_session != null && _session.SceneMatches(_sceneId) &&
            (_player != null || _lobbyPlayer != null || _sushiPlayer != null) &&
            Time.realtimeSinceStartup >= _nextSnapshot)
        {
            var transform = _player != null
                ? _player.transform
                : _lobbyPlayer != null ? _lobbyPlayer.transform : _sushiPlayer.transform;
            var controller = _player != null ? _player.Controller2D : null;
            var renderer = _player != null
                ? _playerRenderer ??= RemoteAvatar.FindPrimaryRenderer(_player)
                : _lobbyPlayer != null
                    ? _lobbyRenderer ??= _lobbyPlayer.m_Renderer ?? RemoteAvatar.FindPrimaryRenderer(_lobbyPlayer)
                    : _sushiRenderer ??= RemoteAvatar.FindPrimaryRenderer(_sushiPlayer);
            var position = transform.position;
            var velocity = controller != null ? controller.GetVelocity() : Vector2.zero;
            var rotation = renderer != null
                ? renderer.transform.eulerAngles.z
                : controller != null ? controller.GetRotation() : transform.eulerAngles.z;
            var flipped = renderer != null
                ? renderer.flipX
                : controller != null && controller.IsFliped();
            var spriteId = renderer != null && renderer.sprite != null
                ? Protocol.SceneId(renderer.sprite.name)
                : 0u;
            var visualScale = renderer != null ? renderer.transform.lossyScale : transform.lossyScale;
            flipped ^= visualScale.x < 0f;
            _session.SendSnapshot(new PlayerSnapshot(
                _sceneId,
                position.x,
                position.y,
                position.z,
                rotation,
                velocity.x,
                velocity.y,
                spriteId,
                Mathf.Abs(visualScale.x),
                Mathf.Abs(visualScale.y),
                flipped));
            _nextSnapshot = Time.realtimeSinceStartup + 0.05f;
        }
        else if (_session == null || !_session.SceneMatches(_sceneId))
        {
            _remoteAvatar.Clear();
        }

        var visualPlayer = (Component)_player ?? _sushiPlayer;
        if (_session != null && visualPlayer != null && _session.SceneMatches(_sceneId) &&
            Time.realtimeSinceStartup >= _nextVisual)
        {
            _session.SendPlayerVisualState(RemoteAvatar.CaptureVisualState(_sceneId, visualPlayer));
            _nextVisual = Time.realtimeSinceStartup + 0.1f;
        }

        if (Time.realtimeSinceStartup < _nextScan)
            return;

        _nextScan = Time.realtimeSinceStartup + 1f;

        var player = UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>();
        if (player == null)
        {
            _player = null;
            _playerRenderer = null;
            var lobbyPlayer = UnityEngine.Object.FindFirstObjectByType<LobbyPlayer>();
            if (lobbyPlayer != null)
            {
                if (_lobbyPlayer != lobbyPlayer)
                    _lobbyRenderer = lobbyPlayer.m_Renderer ?? RemoteAvatar.FindPrimaryRenderer(lobbyPlayer);
                _lobbyPlayer = lobbyPlayer;
                _sushiPlayer = null;
                _sushiRenderer = null;
                _playerPresent = true;
                return;
            }
            var sushiPlayer = UnityEngine.Object.FindFirstObjectByType<SushiBarPlayerHanlder>();
            if (sushiPlayer != null)
            {
                if (_sushiPlayer != sushiPlayer)
                    _sushiRenderer = RemoteAvatar.FindPrimaryRenderer(sushiPlayer);
                _sushiPlayer = sushiPlayer;
                _lobbyPlayer = null;
                _lobbyRenderer = null;
                _playerPresent = true;
                return;
            }
            if (_playerPresent)
                Logger.LogInfo("Player character left the scene");
            _playerPresent = false;
            _lobbyPlayer = null;
            _lobbyRenderer = null;
            _sushiPlayer = null;
            _sushiRenderer = null;
            return;
        }

        _lobbyPlayer = null;
        _lobbyRenderer = null;
        _sushiPlayer = null;
        _sushiRenderer = null;
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

    private void LateUpdate() => _travelCoordinator?.LateUpdate();

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
        _missionProgressReplicator?.Clear();
        _managerEventReplicator?.Clear();
        _worldStateReplicator?.Clear();
        _bossReplicator?.Clear();
        _ingredientsReplicator?.Clear();
        _boatDecoReplicator?.Clear();
        _projectileVisualReplicator?.Clear();
        _remoteCatchLedger?.Clear("plugin stopped");
        _remoteAvatar.Dispose();
        _sessionTrace?.Dispose();
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
        var ingredients = _ingredientsReplicator?.Status ?? "shared catch: unavailable";
        var remoteCatch = _remoteCatchLedger?.Status ?? "remote carry: unavailable";
        GUI.Label(new Rect(16f, 54f, 388f, 24f), Role == SessionRole.Host
            ? $"LAN: {_lanAddress} | {ingredients} | {remoteCatch}"
            : ingredients);

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
            ClearReplicationState(SessionRole.Offline);
            _session = new UdpSession(Logger);
            _session.Start(SessionRole.Offline, address, port, localName, _buildId);
            _localName = localName;
            Role = SessionRole.Offline;
            Address = address;
            Port = port;
            ConfiguredName = configuredName ?? string.Empty;
            SaveSelectedSettings(Role, Address, Port, ConfiguredName, persist, ref error);
            _sessionTrace?.SwitchRole(Role, _localName, _buildId);
            error = "Network start failed; see BepInEx log";
            return false;
        }

        if (!disposedPrevious)
            previous?.Dispose();
        ClearReplicationState(role);
        _session = replacement;
        _localName = localName;
        Role = role;
        Address = address;
        Port = port;
        ConfiguredName = configuredName ?? string.Empty;
        _session.SetLocalScene(_sceneId);
        _nextSnapshot = 0f;
        SaveSelectedSettings(Role, Address, Port, ConfiguredName, persist, ref error);
        _sessionTrace?.SwitchRole(Role, _localName, _buildId);
        Logger.LogInfo($"Network identity: {_localName}; build={_buildId:X8}; role={Role}");
        return true;
    }

    internal string TitlePlayerName => _localName;
    internal string TitleLanAddress => LobbyInput.FindLanAddress();

    internal bool SwitchTitleSession(
        SessionRole role,
        string address,
        string portText,
        string playerName,
        out string error)
    {
        if (role == SessionRole.Offline)
            return SwitchSession(SessionRole.Offline, address, Port, playerName, true, out error);
        if (!LobbyInput.TryValidate(role, address, portText, out var normalized, out var port, out error))
            return false;
        return SwitchSession(role, normalized, port, playerName, true, out error);
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

    private void ClearReplicationState(SessionRole nextRole)
    {
        if (Role != nextRole && (Role == SessionRole.Host || nextRole == SessionRole.Host))
            _remoteCatchLedger?.Clear("network authority changed");
        _remoteAvatar.Clear();
        _fishReplicator?.Clear();
        _pickupReplicator?.Clear();
        _sceneReplicator?.Clear();
        _missionProgressReplicator?.Clear();
        _managerEventReplicator?.Clear();
        _worldStateReplicator?.Clear();
        _bossReplicator?.Clear();
        _ingredientsReplicator?.Clear();
        _boatDecoReplicator?.Clear();
        _projectileVisualReplicator?.Clear();
    }

    private void SetLobbyVisible(bool visible, bool restoreCursor = true)
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
            if (restoreCursor)
            {
                if (Cursor.visible)
                    Cursor.visible = _cursorWasVisible;
                if (Cursor.lockState == CursorLockMode.None)
                    Cursor.lockState = _cursorWasLocked;
            }
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

    internal void RegisterProjectile(Component projectile) =>
        _projectileVisualReplicator?.Register(projectile);

    internal void ReportBoatDecoChange(int id) =>
        _boatDecoReplicator?.OnLocalChange(Role, _session, id);

    internal bool RequestMoveSceneTravel(Common.Contents.MoveSceneElement element)
    {
        var targetId = element == null ? 0 : TravelTargets.FromMoveScene(element.Scene);
        Action action = element == null ? null : element.OnClick;
        return _travelCoordinator?.Request(
            Role, _session, targetId, action,
            _diveCoordinator?.AnyPlayerDead ?? false,
            _diveCoordinator?.HostDead ?? false,
            element) ?? true;
    }

    internal bool RequestSushiBarReturn(SushiBarExitPanel panel) =>
        _travelCoordinator?.Request(
            Role, _session, TravelTargets.Lobby, panel.OnExecute,
            _diveCoordinator?.AnyPlayerDead ?? false,
            _diveCoordinator?.HostDead ?? false) ?? true;

    internal bool RequestFishFarmTravel(FishFarm.FishFarmManager manager, string methodName)
    {
        uint targetId;
        Action action;
        switch (methodName)
        {
            case nameof(FishFarm.FishFarmManager.GoToSushiBar):
                targetId = TravelTargets.SushiBar;
                action = manager.GoToSushiBar;
                break;
            case nameof(FishFarm.FishFarmManager.GoToFarm):
                targetId = TravelTargets.Farm;
                action = manager.GoToFarm;
                break;
            case nameof(FishFarm.FishFarmManager.GoToLobby):
                targetId = TravelTargets.Lobby;
                action = manager.GoToLobby;
                break;
            case nameof(FishFarm.FishFarmManager.GoToSushiBarBranch):
                targetId = TravelTargets.SushiBranch;
                action = manager.GoToSushiBarBranch;
                break;
            default:
                return true;
        }
        return _travelCoordinator?.Request(
            Role, _session, targetId, action,
            _diveCoordinator?.AnyPlayerDead ?? false,
            _diveCoordinator?.HostDead ?? false) ?? true;
    }

    internal bool RequestDredgeReturn(Dredge.DredgeManager manager) =>
        _travelCoordinator?.Request(
            Role, _session, TravelTargets.Lobby, manager.ReturnToLobby,
            _diveCoordinator?.AnyPlayerDead ?? false,
            _diveCoordinator?.HostDead ?? false) ?? true;

    internal bool RequestEscapeMirror(
        Interaction.Escape.EscapeMirror mirror,
        BaseCharacter player) =>
        _travelCoordinator?.Request(
            Role, _session, TravelTargets.Lobby,
            () => mirror.SuccessInteract(player),
            _diveCoordinator?.AnyPlayerDead ?? false,
            _diveCoordinator?.HostDead ?? false) ?? true;

    internal bool RequestJungleFastTravel(
        JDLC.FastTravelPanelController panel,
        string sceneName,
        SceneType sceneType,
        object location,
        SceneTransitionType transitionType) =>
        _travelCoordinator?.Request(
            Role, _session,
            TravelTargets.JungleFastTravel(sceneName, sceneType, Convert.ToInt32(location)),
            () => TravelCoordinator.ChangeJungleScene(panel, sceneName, sceneType, location, transitionType),
            _diveCoordinator?.AnyPlayerDead ?? false,
            _diveCoordinator?.HostDead ?? false,
            null,
            new TravelRoute(
                sceneName, (int)sceneType, Convert.ToInt32(location), (int)transitionType)) ?? true;

    internal bool AllowSceneTransition(string sceneName)
    {
        if (_sceneReplicator?.AllowTransition(Role) ?? true)
            return true;
        if (_travelCoordinator?.AllowClientSceneTransition() ?? false)
            return true;
        Logger.LogInfo($"Network: client scene transition blocked: {sceneName}");
        return false;
    }

    internal bool InterceptManagerEvent(
        ManagerDomain domain,
        ManagerAction action,
        int value,
        int context) =>
        _managerEventReplicator?.Intercept(Role, _session, domain, action, value, context) ?? true;

    internal bool BeginManagerEvent(
        ManagerDomain domain,
        ManagerAction action,
        int value,
        int context,
        out bool suppressNested)
    {
        if (_managerEventReplicator != null)
            return _managerEventReplicator.BeginIntercept(
                Role, _session, domain, action, value, context, out suppressNested);
        suppressNested = false;
        return true;
    }

    internal void EndManagerEvent(bool suppressNested) =>
        _managerEventReplicator?.EndIntercept(suppressNested);

    internal void PublishSushiResult() =>
        _managerEventReplicator?.PublishSushiResult(Role, _session);

    internal void ObserveTimeline(TimelineManager.TPlayState state, int tid, bool success)
    {
        if (success)
            _managerEventReplicator?.ObserveTimeline(Role, _session, state, tid);
    }

    internal bool AllowTimelineControl() =>
        _managerEventReplicator?.AllowTimelineControl(Role, _session) ?? true;

    private void ReturnToOnlineRoom(string reason)
    {
        Logger.LogWarning($"Network: {reason}; returning client to the Online room");
        var address = Address;
        var port = Port;
        var name = ConfiguredName;
        SwitchSession(SessionRole.Offline, address, port, name, false, out _);
        TitleOnlineMenu.RequestHostDisconnect();
        if (_scene == "DR_Title")
            return;
        var loader = UnityEngine.Object.FindFirstObjectByType<SceneLoader>();
        if (loader != null)
            loader.ChangeSceneAsync("DR_Title", SceneTransitionType.FadeOutIn);
    }

    internal void OnIngredientsChanged()
    {
        if (Role == SessionRole.Host)
            _ingredientsReplicator?.MarkDirty();
    }

    internal bool AllowPuzzleSave(PuzzleStateSaveObject saveObject, bool value) =>
        _worldStateReplicator?.AllowLocalSave(Role, _session, saveObject, value) ?? true;

    internal void OnPuzzleSaved(PuzzleStateSaveObject saveObject, bool value) =>
        _worldStateReplicator?.ObserveHostSave(Role, _session, saveObject, value);

    internal bool AllowBossHpWrite(BossControllerBase boss, int hp) =>
        _bossReplicator?.AllowHpWrite(Role, _session, _sceneId, boss, hp) ?? true;

    internal bool TryCaptureRemoteLoot(
        int itemId,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool updateMission) =>
        Role == SessionRole.Host &&
        (_remoteCatchLedger?.TryIntercept(
            itemId, count, bonusGrade, liftType, getTimes, updateMission) ?? false);

    internal void PrepareRemoteCatchResult()
    {
        if (Role == SessionRole.Host)
            _remoteCatchLedger?.PrepareResult(_session, false);
    }

    internal void PrepareDiveExitResult()
    {
        if (Role == SessionRole.Host)
            _remoteCatchLedger?.PrepareResult(_session, true);
    }

    internal void ReportClientLoot(
        int itemId,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        bool updateMission)
    {
        if (Role == SessionRole.Client && IsDiveScene())
            _remoteCatchLedger?.ReportClientLoot(
                _session, itemId, count, bonusGrade, liftType, updateMission);
    }

    internal void ClearRemoteCatch(string reason) => _remoteCatchLedger?.Clear(reason);

    internal void ClearCompletedRemoteCatch()
    {
        if (Role == SessionRole.Host)
            _remoteCatchLedger?.ClearCompleted();
    }

    internal bool OnPickupInteract(PickupInstanceItem item)
    {
        if (Role != SessionRole.Client)
            return true;
        return _pickupReplicator?.RequestPickup(_session, _sceneId, item) ?? false;
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
        if (_fishReplicator?.IsClientDamageScoped(fish) ?? false)
            return false;
        _fishReplicator?.RequestDamage(
            _session, _sceneId, fish, damage, element, attackType);
        return false;
    }

    internal bool BeginFishDamage(
        FishAISystem fish,
        AttackData attackData,
        out bool scoped)
    {
        scoped = false;
        if (Role != SessionRole.Client || _session == null ||
            !_session.SceneMatches(_sceneId))
            return true;
        scoped = _fishReplicator?.BeginClientDamage(
            _session, _sceneId, fish, attackData) ?? false;
        return scoped;
    }

    internal void EndFishDamage(FishAISystem fish, bool scoped)
    {
        if (scoped)
            _fishReplicator?.EndClientDamage(fish);
    }

    internal bool AllowFishTrueDamage(FishAISystem fish) =>
        Role != SessionRole.Client || _session == null ||
        !_session.SceneMatches(_sceneId) || fish == null;

    internal void OnFishCaptureWon(FishAISystem fish)
    {
        if (Role == SessionRole.Client && _session != null &&
            _session.SceneMatches(_sceneId))
            _fishReplicator?.RequestCapture(_session, _sceneId, fish);
    }

    internal bool AllowFishSimulation(FishAISystem fish) =>
        Role != SessionRole.Client || !(_fishReplicator?.IsClientProxy(fish) ?? false);

    internal bool AllowFishInteraction(FishInteractionBody body)
    {
        if (Role != SessionRole.Client || _session == null ||
            !_session.SceneMatches(_sceneId) || body == null)
            return true;
        var fish = body.GetComponentInParent<FishAISystem>();
        return fish != null && (_fishReplicator?.IsClientProxy(fish) ?? false);
    }

    internal bool AllowFishPickup(FishInteractionBody body, BaseCharacter character)
    {
        if (Role != SessionRole.Client || _session == null ||
            !_session.SceneMatches(_sceneId) || body == null)
            return true;
        var fish = body.GetComponentInParent<FishAISystem>();
        if (fish == null)
            return true;
        if (_fishReplicator?.ApplyingClientPickup ?? false)
            return true;
        if (!(_fishReplicator?.RequestPickup(_session, _sceneId, fish) ?? false))
            character?.SuccessInteraction();
        return false;
    }

    internal void OnFishPickupSucceeded(FishAISystem fish)
    {
        if (Role == SessionRole.Host && _session != null)
            _fishReplicator?.ObserveHostPickup(_session, _sceneId, fish);
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
        if (IsDiveScene() && sceneName.StartsWith("DR_Lobby", StringComparison.Ordinal))
            PrepareDiveExitResult();
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

    internal bool RequestDive(LobbyStartGamePanelUI panel, StartParameter startParameter) =>
        _diveCoordinator?.RequestDive(Role, _session, panel, startParameter) ?? true;

    internal void ObserveDivePanel(LobbyStartGamePanelUI panel) =>
        _diveCoordinator?.ObserveDivePanel(panel);

    internal bool RequestDiveExit(Common.SceneExitTrigger trigger)
    {
        if (!IsSharedActionScene())
            return true;
        return _diveCoordinator?.RequestExit(
            Role, _session, Time.realtimeSinceStartup, _player, trigger) ?? true;
    }

    internal bool RequestDiveLobbyExit(
        InGameManager manager,
        SceneTransitionColorType color,
        bool playerDead)
    {
        if (!IsSharedActionScene())
            return true;
        return _diveCoordinator?.RequestLobbyExit(
            Role, _session, Time.realtimeSinceStartup, _player,
            manager, color, playerDead) ?? true;
    }

    internal void ReportDiveLife(bool dead)
    {
        if (IsSharedActionScene())
            _diveCoordinator?.ReportLocalLife(Role, _session, dead);
    }

    internal bool ShouldSuppressClientDeathPopup() =>
        Role == SessionRole.Client && IsSharedActionScene() &&
        (_diveCoordinator?.IsClientSpectating ?? false);

    internal bool RequestDiveDeathReturn()
    {
        if (Role != SessionRole.Client || !IsSharedActionScene())
            return true;
        return _diveCoordinator?.RequestExit(
            Role, _session, Time.realtimeSinceStartup, _player, null) ?? true;
    }

    private bool IsDiveScene() => IsDiveSceneName(_scene);

    private bool IsSharedActionScene() => IsSharedActionSceneName(_scene);

    private static bool IsDiveSceneName(string sceneName) =>
        HasPrefix(sceneName,
            "A0", "B0", "C0", "Boss_", "ControlCenter_", "GlacialArea_",
            "GlacialPassage_", "MermanWarehouse", "SecretRoom_", "C00_",
            "Godzilla_Boss_", "Godzilla_underwater_", "DR_Jungle_Lake");

    private static bool IsSharedActionSceneName(string sceneName) =>
        IsDiveSceneName(sceneName) || HasPrefix(sceneName,
            "DR_Jungle_HollowEarth", "DR_Jungle_RPG_", "DR_Jungle_MiniGames_",
            "DR_Jungle_DaiMuDaimu", "DR_Jungle_Basilo_Inside",
            "Godzilla_Lobby_Fight");

    private static bool HasPrefix(string value, params string[] prefixes)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (var prefix in prefixes)
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }
}
