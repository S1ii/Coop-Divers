using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DR.AI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Steamworks;
using UnityEngine;
using UnityEngine.Events;
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
    private int _sceneHandle = int.MinValue;
    private bool _playerPresent;
    private float _nextScan;
    private float _nextPositionLog;
    private float _nextSnapshot;
    private float _nextVisual;
    private float _nextRuntimeState;
    private uint _runtimeRevision;
    private uint _sceneId;
    private ResolvedSceneMetadata _sceneMetadata;
    private float _nextSceneMetadataRefresh;
    private float _sceneMetadataRefreshUntil;
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
    private NpcInteractionCoordinator _npcInteractionCoordinator;
    private WorldStateReplicator _worldStateReplicator;
    private BossReplicator _bossReplicator;
    private IngredientsReplicator _ingredientsReplicator;
    private BoatDecoReplicator _boatDecoReplicator;
    private DiveCoordinator _diveCoordinator;
    private TravelCoordinator _travelCoordinator;
    private ProjectileVisualReplicator _projectileVisualReplicator;
    private DiverWeaponReplicator _diverWeaponReplicator;
    private RemoteCatchLedger _remoteCatchLedger;
    private SessionTrace _sessionTrace;
    private readonly LoadedGameplaySceneTracker _loadedGameplayScenes = new();
    private UnityAction<Scene, LoadSceneMode> _sceneLoadedHandler;
    private UnityAction<Scene> _sceneUnloadedHandler;
    private bool _unsafeWorldReplicationBlocked;
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
    private float _hostAuthorityRefreshAt;
    private bool _wasSessionConnected;
    private int _remoteMissionApplyDepth;
    private int _clientPresentationLifecycleDepth;
    private bool _restoringClientOriginals;

    public ProbeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    internal static void SelfTest()
    {
        if (!ShouldEmitSessionDesyncDump(
                SessionRole.Host, true, true, 7, 11, 13) ||
            ShouldEmitSessionDesyncDump(
                SessionRole.Client, true, true, 7, 11, 13) ||
            ShouldEmitSessionDesyncDump(
                SessionRole.Host, false, true, 7, 11, 13) ||
            ShouldEmitSessionDesyncDump(
                SessionRole.Host, true, false, 7, 11, 13) ||
            ShouldEmitSessionDesyncDump(
                SessionRole.Host, true, true, 0, 11, 13) ||
            ShouldEmitSessionDesyncDump(
                SessionRole.Host, true, true, 7, 0, 13) ||
            ShouldEmitSessionDesyncDump(
                SessionRole.Host, true, true, 7, 11, 0))
            throw new InvalidOperationException("Session desync dump gate self-test failed");
    }

    private void Start()
    {
        Instance = this;
        Application.runInBackground = true;
        _buildId = Protocol.SceneId(
            $"{Application.buildGUID}|{Application.version}|{Application.unityVersion}|" +
            typeof(Plugin).Module.ModuleVersionId);
        _sessionTrace = new SessionTrace(Logger);
        _remoteCatchLedger = new RemoteCatchLedger(Logger, _sessionTrace);
        _fishReplicator = new FishReplicator(Logger, _sessionTrace, _remoteCatchLedger);
        _remoteCatchLedger.SetRemoteLootEvidence(request =>
            _fishReplicator?.TryCaptureRemoteLoot(
                _session, _sceneId, request, _remoteCatchLedger) ?? false);
        _pickupReplicator = new PickupReplicator(Logger, _sessionTrace, _remoteCatchLedger);
        _sceneReplicator = new SceneReplicator(Logger);
        _missionProgressReplicator = new MissionProgressReplicator(Logger, _sessionTrace);
        _managerEventReplicator = new ManagerEventReplicator(Logger);
        _npcInteractionCoordinator = new NpcInteractionCoordinator(Logger);
        _worldStateReplicator = new WorldStateReplicator(Logger);
        _bossReplicator = new BossReplicator(Logger);
        _ingredientsReplicator = new IngredientsReplicator(Logger);
        _boatDecoReplicator = new BoatDecoReplicator(Logger);
        _diveCoordinator = new DiveCoordinator(Logger);
        _travelCoordinator = new TravelCoordinator(Logger);
        _projectileVisualReplicator = new ProjectileVisualReplicator();
        _diverWeaponReplicator = new DiverWeaponReplicator();
        _lobbyAddress = Address;
        _lobbyPort = Port.ToString();
        _lobbyName = ConfiguredName;
        _sceneLoadedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene, LoadSceneMode>>(
            (Action<Scene, LoadSceneMode>)OnUnitySceneLoaded);
        _sceneUnloadedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene>>(
            (Action<Scene>)OnUnitySceneUnloaded);
        SceneManager.sceneLoaded += _sceneLoadedHandler;
        SceneManager.sceneUnloaded += _sceneUnloadedHandler;
        _loadedGameplayScenes.Reset();
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
        var activeScene = SceneManager.GetActiveScene();
        var scene = activeScene.name;
        if (scene != _scene || activeScene.handle != _sceneHandle)
        {
            var wasDiveScene = _sceneMetadata.CanDive;
            _managerEventReplicator?.OnSceneChanged();
            _npcInteractionCoordinator?.Clear(Role, _session);
            if (_showLobby)
                SetLobbyVisible(false, false);
            _scene = scene;
            _sceneHandle = activeScene.handle;
            _sceneId = Protocol.SceneId(scene);
            _sceneMetadata = SceneMetadataResolver.Resolve(scene);
            _nextSceneMetadataRefresh = _sceneMetadata.HasNativeSceneType
                ? 0f
                : Time.realtimeSinceStartup + 0.1f;
            _sceneMetadataRefreshUntil = Time.realtimeSinceStartup + 10f;
            if (!wasDiveScene && _sceneMetadata.CanDive)
                _remoteCatchLedger?.BeginDive(_session, Time.realtimeSinceStartup);
            _player = null;
            _playerRenderer = null;
            _lobbyPlayer = null;
            _lobbyRenderer = null;
            _sushiPlayer = null;
            _sushiRenderer = null;
            _remoteAvatar.Clear();
            _nextRuntimeState = 0f;
            _runtimeRevision = 0;
            _fishReplicator?.Clear();
            _pickupReplicator?.Clear();
            _bossReplicator?.Clear();
            _boatDecoReplicator?.Clear();
            _diveCoordinator?.Reset();
            _travelCoordinator?.Reset();
            _projectileVisualReplicator?.Clear();
            _diverWeaponReplicator?.Clear();
            _session?.SetLocalScene(_sceneId);
            if (Role == SessionRole.Host)
                FishSpawnSeedCoordinator.ActivateLocalScene(
                    _sceneId, _session?.LocalSceneEpoch ?? 0);
            else if (Role == SessionRole.Client)
                FishSpawnSeedCoordinator.ActivateStagedScene(_sceneId);
            if (Role == SessionRole.Host)
                _sceneReplicator?.OnHostObservedScene(_session, _scene);
            LogSceneMetadata("entered");
            _hostAuthorityRefreshAt = Role == SessionRole.Host
                ? Time.realtimeSinceStartup + 2f
                : 0f;
        }
        else if (_nextSceneMetadataRefresh > 0f &&
                 Time.realtimeSinceStartup >= _nextSceneMetadataRefresh)
        {
            var refreshed = SceneMetadataResolver.Resolve(_scene);
            var merged = SceneMetadataResolver.MergeRefresh(_sceneMetadata, refreshed);
            if (!SceneMetadataResolver.Matches(_sceneMetadata, merged))
            {
                var becameDive = !_sceneMetadata.CanDive && merged.CanDive;
                _sceneMetadata = merged;
                if (becameDive)
                    _remoteCatchLedger?.BeginDive(_session, Time.realtimeSinceStartup);
                LogSceneMetadata(merged.HasNativeSceneType
                    ? "refreshed-native"
                    : "refreshed-partial");
            }

            if (merged.HasNativeSceneType)
                _nextSceneMetadataRefresh = 0f;
            else
            {
                _nextSceneMetadataRefresh = Time.realtimeSinceStartup +
                    (Time.realtimeSinceStartup < _sceneMetadataRefreshUntil ? 0.25f : 2f);
            }
        }

        _session?.Update(Time.realtimeSinceStartup);
        var sessionConnected = _session?.Connected == true;
        if (ManagerEventReplicator.ShouldScheduleReconnectKeyframe(
                Role, _wasSessionConnected, sessionConnected))
        {
            _hostAuthorityRefreshAt = Time.realtimeSinceStartup + 2f;
            _sessionTrace?.Write("AUTH-KEYFRAME", "reason=rejoin-await-scene-settle missions=1 story=1 day=1");
        }
        _wasSessionConnected = sessionConnected;
        FishSpawnSeedCoordinator.Update(Role, _session);
        if (Role == SessionRole.Client && _session != null && _session.TryTakePeerLoss(out var peerLoss))
        {
            ReturnToOnlineRoom(peerLoss);
            return;
        }
        UpdateUnsafeWorldReplicationGate();
        TitleOnlineMenu.Tick(this);
        _remoteCatchLedger?.Update(
            Role, _session, _scene, _sceneMetadata.SceneType == SceneType.lobby,
            Time.realtimeSinceStartup);
        _managerEventReplicator?.Update(Role, _session, _sceneId, Time.realtimeSinceStartup);
        _npcInteractionCoordinator?.Update(Role, _session, _sceneId, Time.realtimeSinceStartup);
        _missionProgressReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        if (!UnsafeWorldReplicationBlocked)
            _worldStateReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        _ingredientsReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        _boatDecoReplicator?.Update(Role, _session, Time.realtimeSinceStartup);
        _sceneReplicator?.Update(Role, _session);
        _diveCoordinator?.Update(
            Role, _session, Time.realtimeSinceStartup, _player, _remoteAvatar.TargetTransform);
        _travelCoordinator?.Update(
            Role, _session,
            _diveCoordinator?.HostDead ?? false,
            _diveCoordinator?.ClientDead ?? false);
        _diverWeaponReplicator?.Update(
            Role, _session, _sceneId, _player, _remoteAvatar,
            Role == SessionRole.Host && (_diveCoordinator?.ClientDead ?? false));
        _projectileVisualReplicator?.Update(_session, _sceneId, Time.realtimeSinceStartup);
        if (!UnsafeWorldReplicationBlocked)
        {
            _fishReplicator?.Update(
                Role, _session, _sceneId, Time.realtimeSinceStartup, Time.unscaledDeltaTime,
                _player, _remoteAvatar.TargetTransform);
        }
        if (!UnsafeWorldReplicationBlocked)
        {
            _bossReplicator?.Update(
                Role, _session, _sceneId, Time.realtimeSinceStartup, Time.unscaledDeltaTime);
            _pickupReplicator?.Update(
                Role, _session, _sceneId, Time.realtimeSinceStartup, _player);
        }
        if (ManagerEventReplicator.ShouldForceSceneEntryKeyframe(
                Role,
                _session?.SceneMatches(_sceneId) == true,
                _hostAuthorityRefreshAt,
                Time.realtimeSinceStartup))
        {
            _hostAuthorityRefreshAt = 0f;
            _missionProgressReplicator?.ForceHostKeyframe();
            _managerEventReplicator?.ForceHostKeyframe();
            _sessionTrace?.Write("AUTH-KEYFRAME", "reason=scene-or-rejoin-settled missions=1 story=1 day=1");
            WriteSessionDesyncDump(_session);
        }

        while (_session != null && _session.TryTakeSnapshot(out var snapshot))
        {
            var renderer = _playerRenderer ?? _lobbyRenderer ?? _sushiRenderer;
            if (renderer != null && _session.SceneMatches(_sceneId) && snapshot.SceneId == _sceneId)
                _remoteAvatar.Apply(snapshot, renderer, _session.RemoteName, IsDiveScene());
            else
                _remoteAvatar.Clear();
        }
        while (_session != null && _session.TryTakePlayerVisualState(out var visualState))
        {
            if (_session.SceneMatches(_sceneId) && visualState.SceneId == _sceneId &&
                visualState.SceneEpoch == _session.RemoteSceneEpoch)
                _remoteAvatar.ApplyVisual(visualState, _session.RemoteName);
        }
        while (_session != null && _session.TryTakeDiverRuntimeState(out var runtimeState))
            if (runtimeState.Owner == DiverOwner.Host)
                _remoteAvatar.ApplyRuntime(runtimeState);
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
            var visible = renderer != null
                ? RemoteAvatar.CaptureVisibleTransform(renderer)
                : new VisibleTransform(
                    controller != null ? controller.GetRotation() : transform.eulerAngles.z,
                    Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y),
                    controller != null && controller.IsFliped(), false);
            var spriteId = renderer != null && renderer.sprite != null
                ? Protocol.SceneId(renderer.sprite.name)
                : 0u;
            _session.SendSnapshot(new PlayerSnapshot(
                _sceneId,
                _session.LocalSceneEpoch,
                position.x,
                position.y,
                position.z,
                visible.Rotation,
                velocity.x,
                velocity.y,
                spriteId,
                visible.ScaleX,
                visible.ScaleY,
                visible.FlipX));
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
            _session.SendPlayerVisualState(RemoteAvatar.CaptureVisualState(
                _sceneId, _session.LocalSceneEpoch, visualPlayer));
            _nextVisual = Time.realtimeSinceStartup + 0.1f;
        }

        if (Role == SessionRole.Host && _session != null && _player != null &&
            _session.SceneMatches(_sceneId) && Time.realtimeSinceStartup >= _nextRuntimeState)
        {
            _runtimeRevision = _runtimeRevision == uint.MaxValue ? 1 : _runtimeRevision + 1;
            if (RemoteAvatar.TryCaptureRuntimeState(
                    _sceneId, _session.LocalSceneEpoch, _runtimeRevision, _player,
                    out var runtimeState))
                _session.SendDiverRuntimeState(runtimeState);
            _nextRuntimeState = Time.realtimeSinceStartup + 0.2f;
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
            _remoteAvatar.Clear();
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

    private void LateUpdate()
    {
        _travelCoordinator?.LateUpdate();
        _fishReplicator?.LateUpdate();
    }

    private void OnDestroy()
    {
        if (_sceneLoadedHandler != null)
            SceneManager.sceneLoaded -= _sceneLoadedHandler;
        if (_sceneUnloadedHandler != null)
            SceneManager.sceneUnloaded -= _sceneUnloadedHandler;
        if (_showLobby)
            SetLobbyVisible(false);
        else
            ReleaseLobbyInputLock();
        _npcInteractionCoordinator?.Clear(Role, _session);
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
        _diverWeaponReplicator?.Clear();
        FishSpawnSeedCoordinator.Clear();
        _remoteCatchLedger?.Clear("plugin stopped");
        _remoteAvatar.Dispose();
        _sessionTrace?.Dispose();
        if (Instance == this)
            Instance = null;
    }

    private bool UnsafeWorldReplicationBlocked =>
        !_loadedGameplayScenes.AllowsWorldScopedReplication(
            Role, _session?.Connected == true);

    private void WriteSessionDesyncDump(UdpSession session)
    {
        if (session == null || !ShouldEmitSessionDesyncDump(
                Role, session.Connected, session.SceneMatches(_sceneId),
                _sceneId, session.LocalSceneEpoch, session.RemoteSceneEpoch))
            return;

        var commits = session.LastDiverCommitDiagnostics;
        var details =
            $"scope=session connection={session.ConnectionId:X16} " +
            $"scene={_sceneId:X8} localEpoch={session.LocalSceneEpoch} " +
            $"remoteEpoch={session.RemoteSceneEpoch} pendingReliable={session.PendingReliableCount} " +
            $"reliableBacklog={session.ReliableBacklogCount} " +
            $"diver=hostRev:{commits.HostRuntimeRevision},clientRev:{commits.ClientRuntimeRevision}," +
            $"weaponCommit:{commits.WeaponCommitRevision},vitalCommit:{commits.VitalCommitRevision} " +
            $"packets={FormatPacketReceiveDiagnostics(session.PacketReceiveDiagnostics)} " +
            "open=owner-map,manager-leases,entity-counts,save-commits";
        Logger?.LogWarning($"DESYNC-DUMP {details}");
        _sessionTrace?.Write("DESYNC-DUMP", details);
    }

    private static bool ShouldEmitSessionDesyncDump(
        SessionRole role,
        bool connected,
        bool scenesMatch,
        uint sceneId,
        uint localEpoch,
        uint remoteEpoch) =>
        role == SessionRole.Host && connected && scenesMatch && sceneId != 0 &&
        localEpoch != 0 && remoteEpoch != 0;

    private static string FormatPacketReceiveDiagnostics(PacketReceiveDiagnostics[] diagnostics)
    {
        if (diagnostics == null || diagnostics.Length == 0)
            return "none";
        var entries = new List<string>(diagnostics.Length);
        foreach (var entry in diagnostics)
            entries.Add($"{entry.Type}:{entry.Received}/{entry.Dropped}/{entry.QueueOverflows}");
        return string.Join(',', entries);
    }

    private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _loadedGameplayScenes.OnSceneLoaded(scene, mode);
        UpdateUnsafeWorldReplicationGate();
    }

    private void OnUnitySceneUnloaded(Scene scene)
    {
        _loadedGameplayScenes.OnSceneUnloaded(scene);
        UpdateUnsafeWorldReplicationGate();
    }

    private void UpdateUnsafeWorldReplicationGate()
    {
        _loadedGameplayScenes.Refresh();
        var blocked = UnsafeWorldReplicationBlocked;
        if (blocked == _unsafeWorldReplicationBlocked)
        {
            if (blocked)
                DrainBlockedWorldPackets();
            return;
        }

        _unsafeWorldReplicationBlocked = blocked;
        if (!blocked)
        {
            _fishReplicator?.Clear();
            _pickupReplicator?.Clear();
            _bossReplicator?.Clear();
            _worldStateReplicator?.Clear();
            Logger?.LogInfo("Network world replication restored: one gameplay scene loaded");
            return;
        }

        _fishReplicator?.Clear();
        _pickupReplicator?.Clear();
        _bossReplicator?.Clear();
        Logger?.LogWarning(
            $"Network world replication disabled: {_loadedGameplayScenes.GameplaySceneCount} " +
            "gameplay scenes are loaded; leave the additive route to resume replication");
        DrainBlockedWorldPackets();
    }

    private void DrainBlockedWorldPackets()
    {
        if (_session == null)
            return;
        while (_session.TryTakePickupRequest(out _))
        {
        }
        while (_session.TryTakePickupResult(out _))
        {
        }
        while (_session.TryTakePickupRemoved(out _))
        {
        }
        while (_session.TryTakeBossDamageRequest(out _))
        {
        }
        while (_session.TryTakeBossState(out _))
        {
        }
        while (_session.TryTakeWorldFlagRequest(out _))
        {
        }
        while (_session.TryTakeWorldFlagState(out _))
        {
        }
        while (_session.TryTakeFishSnapshot(out _))
        {
        }
        while (_session.TryTakeFishRemoved(out _))
        {
        }
        while (_session.TryTakeFishPickupResult(out _))
        {
        }
        while (_session.TryTakeFishManifest(out _))
        {
        }
        while (_session.TryTakeFishManifestState(out _))
        {
        }
        while (_session.TryTakeFishLifecycle(out _))
        {
        }
        while (_session.TryTakeFishActionRequest(out _))
        {
        }
        while (_session.TryTakeFishActionAck(out _))
        {
        }
        while (_session.TryTakeFishLootGrant(out _))
        {
        }
        while (_session.TryTakeFishLootComplete(out _))
        {
        }
        while (_session.TryTakeFishDamageRequest(out _))
        {
        }
        while (_session.TryTakeFishPickupRequest(out _))
        {
        }
        while (_session.TryTakeFishHookPose(out _))
        {
        }
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
        if (role != SessionRole.Offline && MultiplayerSaveSync.NormalSaveRestartRequired)
        {
            error = "Restart the game before starting another online session";
            return false;
        }
        if (role != SessionRole.Offline &&
            !LobbyInput.TryValidate(role, address, port.ToString(), out address, out port, out error))
            return false;
        var restoreOriginalProfile =
            MultiplayerSaveSync.RequiresOriginalProfileRestore(Role, role);

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
        var disposedPrevious = previous != null && !restoreOriginalProfile &&
            (!previousRunning ||
                Role == SessionRole.Host && role == SessionRole.Host && Port == port);
        if (disposedPrevious)
        {
            _npcInteractionCoordinator?.Clear(Role, previous);
            previous.Dispose();
        }

        var replacement = new UdpSession(Logger);
        replacement.Start(role, address, port, localName, _buildId);
        if (role != SessionRole.Offline && !replacement.IsRunning)
        {
            replacement.Dispose();
            if (restoreOriginalProfile &&
                !MultiplayerSaveSync.TryRestoreOriginalProfile(Logger))
            {
                error = MultiplayerSaveSync.Status;
                return false;
            }
            if (previousRunning && !disposedPrevious)
            {
                error = "Network start failed; current session kept";
                return false;
            }

            _npcInteractionCoordinator?.Clear(Role, previous);
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

        if (restoreOriginalProfile &&
            !MultiplayerSaveSync.TryRestoreOriginalProfile(Logger))
        {
            replacement.Dispose();
            error = MultiplayerSaveSync.Status;
            return false;
        }

        if (!disposedPrevious)
        {
            _npcInteractionCoordinator?.Clear(Role, previous);
            previous?.Dispose();
        }
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
    internal bool IsApplyingRemoteMission => _remoteMissionApplyDepth > 0;
    internal bool IsCompletingClientPresentation => _clientPresentationLifecycleDepth > 0;
    internal bool IsApplyingNpcGrant => _npcInteractionCoordinator?.IsApplyingGrant == true;
    internal bool HasActiveNpcLease => _npcInteractionCoordinator?.HasActiveLease == true;

    internal void BeginRemoteMissionApply() => _remoteMissionApplyDepth++;

    internal void EndRemoteMissionApply()
    {
        if (_remoteMissionApplyDepth > 0)
            _remoteMissionApplyDepth--;
    }

    internal void BeginClientPresentationLifecycle() => _clientPresentationLifecycleDepth++;

    internal void EndClientPresentationLifecycle()
    {
        if (_clientPresentationLifecycleDepth > 0)
            _clientPresentationLifecycleDepth--;
    }

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
        if (Role != nextRole)
            _remoteCatchLedger?.Clear("network authority changed");
        _wasSessionConnected = false;
        _hostAuthorityRefreshAt = 0f;
        _remoteAvatar.Clear();
        _fishReplicator?.Clear();
        _pickupReplicator?.Clear();
        _sceneReplicator?.Clear();
        _diveCoordinator?.Reset();
        _travelCoordinator?.Reset();
        _missionProgressReplicator?.Clear();
        _managerEventReplicator?.Clear(Role == SessionRole.Host && nextRole == SessionRole.Host);
        _worldStateReplicator?.Clear();
        _bossReplicator?.Clear();
        _ingredientsReplicator?.Clear();
        _boatDecoReplicator?.Clear();
        _projectileVisualReplicator?.Clear();
        _diverWeaponReplicator?.Clear();
        FishSpawnSeedCoordinator.Clear();
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
        if (!UnsafeWorldReplicationBlocked &&
            Role is SessionRole.Host or SessionRole.Client)
            _pickupReplicator?.OnDestroyed(Role, _session, _sceneId, item);
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
        SceneConnectLocationID location,
        SceneTransitionType transitionType) =>
        _travelCoordinator?.Request(
            Role, _session,
            TravelTargets.JungleFastTravel(sceneName, sceneType, (int)location),
            () => TravelCoordinator.ChangeJungleScene(panel, sceneName, sceneType, location, transitionType),
            _diveCoordinator?.AnyPlayerDead ?? false,
            _diveCoordinator?.HostDead ?? false,
            null,
            new TravelRoute(
                sceneName, (int)sceneType, (int)location, (int)transitionType)) ?? true;

    internal bool AllowSceneTransition(string sceneName)
    {
        if (_sceneReplicator?.AllowTransition(Role, sceneName, Time.realtimeSinceStartup) ?? true)
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

    internal bool AllowScenarioControl() =>
        _managerEventReplicator?.AllowScenarioControl(Role, _session) ?? true;

    internal bool AllowDialogueAdvance() =>
        _managerEventReplicator?.AllowDialogueAdvance(Role, _session) ?? true;

    internal bool AllowDialogueChoice() =>
        _managerEventReplicator?.AllowDialogueChoice(Role, _session) ?? true;

    internal bool TryRestoreClientOriginals()
    {
        if (_restoringClientOriginals)
            return true;
        _restoringClientOriginals = true;
        try
        {
            return (_managerEventReplicator?.TryRestoreClientOriginals() ?? true) &
                (_missionProgressReplicator?.TryRestoreClientOriginals() ?? true);
        }
        finally
        {
            _restoringClientOriginals = false;
        }
    }

    internal bool InterceptPhoneCall(int tid, Il2CppSystem.Action<bool> callback) =>
        _managerEventReplicator?.InterceptPhoneCall(Role, _session, tid, callback) ?? true;

    internal bool InterceptPhoneAnswer() =>
        _managerEventReplicator?.InterceptPhoneAnswer(Role, _session) ?? true;

    internal bool AllowScenarioTerminal() =>
        _managerEventReplicator?.AllowScenarioTerminal(Role, _session) ?? true;

    internal bool AllowDialogueTerminal() =>
        _managerEventReplicator?.AllowDialogueTerminal(Role, _session) ?? true;

    internal bool InterceptScenarioStart(
        string bundleId,
        bool isBranch,
        ScenarioBranchData branchData,
        Il2CppSystem.Collections.Generic.List<string> arguments,
        Il2CppSystem.Action<bool> callback,
        bool useButton,
        bool showCurtain,
        bool ignorePlaying) =>
        _managerEventReplicator?.InterceptScenarioStart(
            Role, _session, bundleId, isBranch, branchData, arguments, callback,
            useButton, showCurtain, ignorePlaying) ?? true;

    internal bool InterceptDialogueStart(
        string bundleId,
        ManagerEventReplicator.DialogueStartKind kind,
        Il2CppSystem.Collections.Generic.List<string> arguments,
        Il2CppSystem.Collections.Generic.List<DR.GameData.DialogueEntry> entries,
        ScenarioButtonInfo buttonInfo,
        Il2CppSystem.Action<bool> callback,
        Il2CppSystem.Action<int> choiceCallback,
        bool useButton,
        bool showCurtain) =>
        _managerEventReplicator?.InterceptDialogueStart(
            Role, _session, bundleId, kind, arguments, entries, buttonInfo,
            callback, choiceCallback, useButton, showCurtain) ?? true;

    internal bool InterceptScenarioNode(ScenarioManager manager, DRSequence sequence) =>
        _managerEventReplicator?.InterceptScenarioNode(
            Role, _session, manager, sequence) ?? true;

    internal void ObserveScenarioStarted(ScenarioManager manager, string bundleId) =>
        _managerEventReplicator?.ObserveScenarioStarted(
            Role, _session, manager, bundleId);

    internal void ObserveScenarioFinished() =>
        _managerEventReplicator?.ObserveScenarioFinished(Role, _session);

    internal void ObserveScenarioFinished(bool result) =>
        _managerEventReplicator?.ObserveScenarioFinished(Role, _session, result);

    internal Il2CppSystem.Action<bool> WrapScenarioFinish(
        string bundleId, Il2CppSystem.Action<bool> callback) =>
        _managerEventReplicator?.WrapScenarioFinish(
            Role, _session, bundleId, callback) ?? callback;

    internal void ObserveDialogueStarted(string bundleId) =>
        _managerEventReplicator?.ObserveDialogueStarted(Role, _session, bundleId);

    internal void ObserveDialogueNode(DialogueManager manager, DialogueInfo info) =>
        _managerEventReplicator?.ObserveDialogueNode(Role, _session, manager, info);

    internal void ObserveDialogueFinished()
    {
        _managerEventReplicator?.ObserveDialogueFinished(Role, _session);
        _npcInteractionCoordinator?.Release(Role, _session);
    }

    internal Il2CppSystem.Action<bool> WrapDialogueFinish(
        string bundleId, Il2CppSystem.Action<bool> callback) =>
        _managerEventReplicator?.WrapDialogueFinish(
            Role, _session, bundleId, callback) ?? callback;

    internal void PublishSushiResult() =>
        _managerEventReplicator?.PublishSushiResult(Role, _session);

    internal void ObserveReward(Reward reward) =>
        _managerEventReplicator?.ObserveReward(Role, _session, reward);

    internal bool InterceptPlayerGoods(GoodsType type, int value) =>
        _managerEventReplicator?.InterceptPlayerGoods(Role, _session, type, value) ?? true;

    internal bool InterceptSushiInteraction(StaffDave staff, SushiBarInteraction interaction) =>
        _managerEventReplicator?.InterceptSushiInteraction(
            Role, _session, staff, interaction) ?? true;

    internal bool InterceptSushiClean(SushiBarTrashTrigger trigger, int gold) =>
        _managerEventReplicator?.InterceptSushiClean(Role, _session, trigger, gold) ?? true;

    internal bool InterceptSushiWasabi(SushiBar.Place place, int count) =>
        _managerEventReplicator?.InterceptSushiWasabi(Role, _session, place, count) ?? true;

    internal bool InterceptTimelinePlay(
        int tid,
        Il2CppSystem.Action onStart,
        Il2CppSystem.Action<bool> onFinished,
        bool applyOffset,
        Il2CppSystem.Nullable<Vector3> customPos) =>
        _managerEventReplicator?.InterceptTimelinePlay(
            Role, _session, tid, onStart, onFinished, applyOffset, customPos) ?? true;

    internal bool InterceptTimelineController(
        TimelineController controller,
        Il2CppSystem.Action onStart,
        Il2CppSystem.Action<bool> onFinished,
        int id) =>
        _managerEventReplicator?.InterceptTimelineController(
            Role, _session, controller, onStart, onFinished, id) ?? true;

    internal void ObserveTimeline(TimelineManager.TPlayState state, int tid, bool success) =>
        _managerEventReplicator?.ObserveTimeline(Role, _session, state, tid, success);

    internal bool AllowTimelineTerminalControl() =>
        _managerEventReplicator?.AllowTimelineTerminalControl(Role, _session) ?? true;

    internal bool RequestNpcTalk(Common.Contents.TalkTarget target) =>
        _npcInteractionCoordinator?.RequestTalk(Role, _session, _sceneId, target) ?? true;

    internal bool RequestNpcTalkMenu(Common.Contents.TalkNPCMenu menu) =>
        _npcInteractionCoordinator?.RequestTalkMenu(
            Role, _session, _sceneId, menu) ?? true;

    internal bool RequestMissionNpcTalk(MissionTargetNPCController npc) =>
        _npcInteractionCoordinator?.RequestMissionTalk(
            Role, _session, _sceneId, npc) ?? true;

    private void ReturnToOnlineRoom(string reason)
    {
        Logger.LogWarning($"Network: {reason}; returning client to the Online room");
        var address = Address;
        var port = Port;
        var name = ConfiguredName;
        SwitchSession(SessionRole.Offline, address, port, name, false, out _);
        TitleOnlineMenu.RequestHostDisconnect();
        if (_sceneMetadata.SceneType == SceneType.title)
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
        UnsafeWorldReplicationBlocked
            ? false
            : _worldStateReplicator?.AllowLocalSave(Role, _session, saveObject, value) ?? true;

    internal void OnPuzzleSaved(PuzzleStateSaveObject saveObject, bool value)
    {
        if (!UnsafeWorldReplicationBlocked)
            _worldStateReplicator?.ObserveHostSave(Role, _session, saveObject, value);
    }

    internal bool AllowBossHpWrite(BossControllerBase boss, int hp) =>
        UnsafeWorldReplicationBlocked
            ? false
            : _bossReplicator?.AllowHpWrite(Role, _session, _sceneId, boss, hp) ?? true;

    internal bool TryCaptureRemoteLoot(
        int itemId,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool updateMission) =>
        IsCompletingClientPresentation || Role == SessionRole.Host &&
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
        if (Role is SessionRole.Host or SessionRole.Client && IsDiveScene())
            _remoteCatchLedger?.ReportClientLoot(
                _session, itemId, count, bonusGrade, liftType, updateMission);
    }

    internal void ClearRemoteCatch(string reason)
    {
        if (Role == SessionRole.Host && reason == "dive aborted" &&
            (_diveCoordinator?.HostDead ?? false) && !(_diveCoordinator?.ClientDead ?? false))
            return;
        _remoteCatchLedger?.Clear(reason);
    }

    internal void ClearCompletedRemoteCatch()
    {
        if (Role == SessionRole.Host)
            _remoteCatchLedger?.ClearCompleted();
    }

    internal bool OnPickupInteract(PickupInstanceItem item, BaseCharacter character)
    {
        if (UnsafeWorldReplicationBlocked)
            return false;
        if (Role != SessionRole.Client)
            return true;
        if (!(_pickupReplicator?.RequestPickup(_session, _sceneId, item) ?? false))
            character?.SuccessInteraction();
        return false;
    }

    internal void EndClientLootSource() => _remoteCatchLedger?.EndClientLootSource();

    internal bool BeginClientFishLootSource(FishAISystem fish)
    {
        if (Role == SessionRole.Client)
            _remoteCatchLedger?.BeginClientFishSource();
        return Role == SessionRole.Client &&
            (_fishReplicator?.BeginPendingClientFishLootScope(fish) ?? false);
    }

    internal void EndClientFishLootSource(bool pendingScope)
    {
        if (pendingScope)
            _fishReplicator?.EndPendingClientFishLootScope();
        EndClientLootSource();
    }

    internal bool ShouldSuppressPendingClientFishLoot(int itemId, int count)
    {
        if (Role != SessionRole.Client ||
            _fishReplicator?.ShouldSuppressPendingClientFishLoot(itemId) != true)
            return false;
        _sessionTrace?.Write("FISH-LOOT-DEFER", $"item={itemId} count={count}");
        return true;
    }

    internal bool AllowFishDamage(
        FishAISystem fish,
        int damage,
        EElement element,
        AttackType attackType) =>
        Role != SessionRole.Client || _fishReplicator?.ShouldAllowClientDamageWrite(fish) != false;

    internal void ObserveFishDamage(FishAISystem fish, AttackData attackData)
    {
        if (Role != SessionRole.Client || _fishReplicator?.ClientAuthorityActive != true ||
            _fishReplicator.ApplyingClientState || !_fishReplicator.IsClientProxy(fish))
            return;
        _fishReplicator.ObserveClientDamage(_session, _sceneId, fish, attackData);
    }

    internal bool AllowFishTrueDamage(FishAISystem fish) =>
        Role != SessionRole.Client ||
        _fishReplicator?.ShouldAllowDirectClientProxyWrite(fish) != false;

    internal bool AllowFishCaptureWon(FishAISystem fish)
    {
        if (IsCompletingClientPresentation)
            return true;
        if (Role == SessionRole.Client &&
            _fishReplicator?.SuppressingNativeRecallOutcome == true)
            return false;
        if (Role != SessionRole.Client ||
            _fishReplicator?.ClientAuthorityActive != true)
            return true;
        return _fishReplicator.RequestClientCapture(_session, _sceneId, fish);
    }

    internal void RefreshMissionAfterNativeChange()
    {
        if (Role == SessionRole.Host)
            _missionProgressReplicator?.RefreshAfterNativeMissionChange();
    }

    internal void OnFishHooked(FishAISystem fish)
    {
        if (Role == SessionRole.Client && _fishReplicator?.ClientAuthorityActive == true)
            _fishReplicator.ObserveClientHook(_session, _sceneId, fish);
    }

    internal void OnFishHookEnded(FishAISystem fish)
    {
        if (Role == SessionRole.Client && _fishReplicator?.ClientAuthorityActive == true)
            _fishReplicator.ObserveClientRelease(_session, _sceneId, fish);
    }

    internal void BeginHarpoonRecall(bool isSuccess)
    {
        if (Role == SessionRole.Client)
            _fishReplicator?.BeginClientHarpoonRecall(_session, _sceneId, isSuccess);
    }

    internal void EndHarpoonRecall()
    {
        if (Role == SessionRole.Client)
            _fishReplicator?.EndClientHarpoonRecall();
    }

    internal void OnHarpoonReset()
    {
        if (Role == SessionRole.Client)
            _fishReplicator?.ResetClientHarpoon(_session, _sceneId);
    }

    internal void ApplyFishAuthoritativeState(FishAISystem fish)
    {
        if (Role == SessionRole.Client && _fishReplicator?.ClientAuthorityActive == true)
        {
            _fishReplicator.PublishClientHookPose(
                _session, _sceneId, fish, Time.realtimeSinceStartup);
            _fishReplicator.ApplyClientAuthoritativeAfterPresentation(fish);
        }
    }

    internal bool AllowFishSimulation(FishAISystem fish) =>
        Role != SessionRole.Client ||
        _fishReplicator?.ShouldAllowClientSimulation(fish) != false;

    internal bool AllowFishRemoval(FishAISystem fish)
    {
        if (Role == SessionRole.Host && _remoteCatchLedger?.AllowFishRemoval(fish) == false)
            return false;
        return _fishReplicator?.IsClientProxy(fish) != true ||
            !IsCompletingClientPresentation &&
            _fishReplicator?.SuppressingNativeRecallOutcome != true &&
            _fishReplicator?.HasPendingClientCapture(fish) != true;
    }

    internal bool AllowFishInteraction(FishInteractionBody body, bool nativeAvailable) =>
        Role == SessionRole.Client && _fishReplicator?.ClientAuthorityActive == true
            ? _fishReplicator.CanClientInteract(body, nativeAvailable)
            : nativeAvailable;

    internal bool AllowFishPickup(FishInteractionBody body, BaseCharacter character)
    {
        if (IsCompletingClientPresentation || Role != SessionRole.Client ||
            _fishReplicator?.ClientAuthorityActive != true)
            return true;
        var fish = body?.GetComponentInParent<FishAISystem>();
        if (!_fishReplicator.IsClientProxy(fish))
            return true;
        if (!_fishReplicator.RequestPickup(_session, _sceneId, fish))
            character?.SuccessInteraction();
        return false;
    }

    internal void TraceFishPickup(string stage, FishAISystem fish, FishInteractionBody body, bool? result = null)
    {
        if (!IsDiveScene())
            return;
        fish ??= body?.GetComponentInParent<FishAISystem>();
        body ??= fish?.GetInteractionBody;
        _sessionTrace?.Write("FISH-PICKUP",
            $"stage={stage} role={Role} type={fish?.FishDataTID ?? 0} " +
            $"hp={fish?.HP ?? -1f:F1} corpse={fish?.IsCorpse ?? false} " +
            $"captured={fish?.IsFishCaptured ?? false} body={body != null} " +
            $"enabled={body?.IsEnableInteraction ?? false} interaction={body?.InteractionType} " +
            $"result={(result.HasValue ? result.Value.ToString() : "-")}");
    }

    internal void TraceAuthorityRejected(string domain, string method, string owner, string reason) =>
        _sessionTrace?.Write("AUTH-REJECT",
            $"domain={domain} method={method} owner={owner} reason={reason}");

    internal void TracePlayerInteraction(string stage, PlayerCharacter player)
    {
        if (!IsDiveScene())
            return;
        var current = player?.CurrentInteractionObject;
        _sessionTrace?.Write("PLAYER-INTERACT",
            $"stage={stage} role={Role} current={current?.GetType().FullName ?? "null"} " +
            $"isAnim={player?.IsInteractionAnim ?? false}");
    }

    internal void TraceLootEntry(string source, int itemId, int count, LootBox.AutoLiftedType liftType, bool result)
    {
        if (!IsDiveScene())
            return;
        var lootBox = LootBox.Instance;
        _sessionTrace?.Write("LOOT-ENTRY",
            $"source={source} role={Role} item={itemId} count={count} lift={liftType} " +
            $"result={result} weight={lootBox?.weight ?? -1f:F2} " +
            $"max={lootBox?.weightMax ?? -1f:F2} overweight={lootBox?.isOverweightState ?? false}");
    }

    internal void TraceOverload(int itemId, bool nativeResult, bool correctedResult)
    {
        if (!IsDiveScene())
            return;
        var lootBox = LootBox.Instance;
        _sessionTrace?.Write("OVERLOAD-CHECK",
            $"role={Role} item={itemId} native={nativeResult} result={correctedResult} " +
            $"weight={lootBox?.weight ?? -1f:F2} max={lootBox?.weightMax ?? -1f:F2} " +
            $"overweight={lootBox?.isOverweightState ?? false}");
    }

    internal void ApplyClientCargoState(LootBox lootBox)
    {
        if (Role == SessionRole.Client && IsDiveScene())
            _remoteCatchLedger?.ApplyClientCargoState(
                lootBox, Role, _sceneId, _session?.RemoteSceneEpoch ?? 0);
    }

    internal void TraceHarpoon(string stage, HarpoonWeaponHandler handler, bool? success = null)
    {
        if (!IsDiveScene())
            return;
        var projectile = handler?.harpoonProjectile;
        _sessionTrace?.Write("HARPOON",
            $"stage={stage} role={Role} state={handler?.GetActionState} " +
            $"success={(success.HasValue ? success.Value.ToString() : "-")} " +
            $"projectile={projectile != null} active={projectile?.gameObject.activeInHierarchy ?? false} " +
            $"hooked={projectile?.IsHookedSomeThing ?? false} " +
            $"hookedObject={projectile?.HookedObject?.GetType().FullName ?? "null"}");
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
        if (IsDiveScene() && SceneMetadataResolver.Resolve(sceneName).SceneType == SceneType.lobby)
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

    internal void BeginClientNativeDiveTransition(string sceneName) =>
        _sceneReplicator?.BeginClientNativeDiveTransition(sceneName, Time.realtimeSinceStartup);

    internal void CancelClientNativeDiveTransition() =>
        _sceneReplicator?.CancelClientNativeDiveTransition();

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

    internal bool RequestEscapePod(PlayerCharacter player) =>
        _diveCoordinator?.RequestEscapePod(Role, _session, player) ?? true;

    internal void ReportDiveLife(bool dead)
    {
        if (IsSharedActionScene())
            _diveCoordinator?.ReportLocalLife(Role, _session, dead);
    }

    internal bool RequestDiveDeathReturn()
    {
        if (Role != SessionRole.Client || !IsSharedActionScene())
            return true;
        return _diveCoordinator?.RequestExit(
            Role, _session, Time.realtimeSinceStartup, _player, null) ?? true;
    }

    private bool IsDiveScene() => _sceneMetadata.CanDive;

    private bool IsSharedActionScene() => _sceneMetadata.IsSharedAction;

    private void LogSceneMetadata(string stage)
    {
        var details =
            $"stage={stage} name={_scene} id={_sceneId:X8} type={_sceneMetadata.SceneType} " +
            $"dive={_sceneMetadata.CanDive} shared={_sceneMetadata.IsSharedAction} " +
            $"additive={_sceneMetadata.IsAdditive} route={_sceneMetadata.RouteIdentity} " +
            $"source={_sceneMetadata.Source}";
        Logger.LogInfo($"Scene metadata: {details}");
        _sessionTrace?.Write("SCENE", details);
    }
}
