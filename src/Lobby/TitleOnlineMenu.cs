using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Common.UI;
using DR.Save;
using DR.Title;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DaveTheDiverMP;

internal static class TitleOnlineMenu
{
    private const int Online = 100;
    private const int Host = 101;
    private const int Join = 102;
    private const int Back = 106;
    private const int Ready = 107;
    private const int Start = 108;
    private const string OnlineObjectName = "DaveTheDiverMP_Online";
    private const float NativePanelTimeout = 2f;
    private const float RowStartY = -32f;
    private const float RowStepY = 68f;
    private const float RowHeight = 56f;

    private enum OnlineText
    {
        Online,
        CreateLobby,
        Join,
        Name,
        HostIp,
        Port,
        Back,
        Host,
        PlayerTwo,
        Waiting,
        Ready,
        NotReady,
        Start,
        WaitingForReady,
        Connecting,
        WaitingForHostRoom,
        LocalNetwork,
        StartingCampaign
        ,HostDisconnected
    }

    // Korean, English, Japanese, Chinese, Chinese Traditional, French, Italian, German, Spanish, Portuguese, Russian.
    private static readonly string[][] Texts =
    {
        new[] { "온라인", "Online", "オンライン", "在线", "線上", "En ligne", "Online", "Online", "En línea", "Online", "Онлайн" },
        new[] { "로비 만들기", "Create lobby", "ロビーを作成", "创建大厅", "建立大廳", "Créer un salon", "Crea lobby", "Lobby erstellen", "Crear sala", "Criar sala", "Создать лобби" },
        new[] { "참가", "Join", "参加", "加入", "加入", "Rejoindre", "Unisciti", "Beitreten", "Unirse", "Entrar", "Подключиться" },
        new[] { "이름", "Name", "名前", "名称", "名稱", "Nom", "Nome", "Name", "Nombre", "Nome", "Имя" },
        new[] { "호스트 IP", "Host IP", "ホスト IP", "主机 IP", "主機 IP", "IP de l’hôte", "IP host", "Host-IP", "IP del anfitrión", "IP do anfitrião", "IP хоста" },
        new[] { "포트", "Port", "ポート", "端口", "連接埠", "Port", "Porta", "Port", "Puerto", "Porta", "Порт" },
        new[] { "뒤로", "Back", "戻る", "返回", "返回", "Retour", "Indietro", "Zurück", "Atrás", "Voltar", "Назад" },
        new[] { "호스트", "Host", "ホスト", "主机", "主機", "Hôte", "Host", "Host", "Anfitrión", "Anfitrião", "Хост" },
        new[] { "플레이어 2", "Player 2", "プレイヤー 2", "玩家 2", "玩家 2", "Joueur 2", "Giocatore 2", "Spieler 2", "Jugador 2", "Jogador 2", "Игрок 2" },
        new[] { "대기 중", "Waiting", "待機中", "等待中", "等待中", "En attente", "In attesa", "Wartet", "Esperando", "Aguardando", "Ожидание" },
        new[] { "준비됨", "Ready", "準備完了", "已准备", "已準備", "Prêt", "Pronto", "Bereit", "Listo", "Pronto", "Готов" },
        new[] { "준비 안 됨", "Not ready", "準備未完了", "未准备", "未準備", "Pas prêt", "Non pronto", "Nicht bereit", "No listo", "Não pronto", "Не готов" },
        new[] { "시작", "Start", "開始", "开始", "開始", "Démarrer", "Avvia", "Starten", "Iniciar", "Iniciar", "Начать" },
        new[] { "준비 대기 중", "Waiting for ready", "準備待ち", "等待准备", "等待準備", "En attente du prêt", "In attesa del pronto", "Warte auf Bereitschaft", "Esperando preparación", "Aguardando pronto", "Ожидание готовности" },
        new[] { "연결 중...", "Connecting...", "接続中...", "正在连接...", "連線中...", "Connexion...", "Connessione...", "Verbinden...", "Conectando...", "Conectando...", "Подключение..." },
        new[] { "호스트 방 대기 중", "Waiting for host room", "ホストのルームを待機中", "等待主机房间", "等待主機房間", "En attente du salon hôte", "In attesa della stanza host", "Warte auf Host-Lobby", "Esperando sala del anfitrión", "Aguardando sala do anfitrião", "Ожидание комнаты хоста" },
        new[] { "LAN", "LAN", "LAN", "局域网", "區域網路", "Réseau local", "Rete locale", "Lokales Netzwerk", "Red local", "Rede local", "Локальная сеть" },
        new[] { "캠페인을 시작하는 중...", "Starting campaign...", "キャンペーンを開始中...", "正在开始战役...", "正在開始戰役...", "Démarrage de la campagne...", "Avvio della campagna...", "Kampagne wird gestartet...", "Iniciando campaña...", "Iniciando campanha...", "Запуск кампании..." },
        new[] { "호스트와 연결이 끊겼습니다", "Host disconnected", "ホストとの接続が切れました", "主机已断开连接", "主機已中斷連線", "L’hôte s’est déconnecté", "L'host si è disconnesso", "Host getrennt", "El anfitrión se desconectó", "Anfitrião desconectado", "Хост отключился" }
    };

    private static readonly List<TitleMenuButton> TitleButtons = new();
    private static readonly List<TitleMenuButton> RoomButtons = new();
    private static readonly List<TitleMenuButton> RoomLabels = new();
    private static readonly List<GameObject> RoomFields = new();
    private static readonly Dictionary<int, TitleMenuButton> ActionButtons = new();
    private static readonly Dictionary<GameObject, bool> NativeContentStates = new();
    private static readonly Dictionary<Behaviour, bool> NativeControlStates = new();
    private static TitleManager _manager;
    private static TitleMenuButton _template;
    private static SettingAppPanel _nativePanel;
    private static RectTransform _roomContent;
    private static bool _titleFallback;
    private static TMP_Text _nativeHeader;
    private static string _nativeHeaderTitle = string.Empty;
    private static TMP_Text _nativeCloseLabel;
    private static string _nativeCloseTitle = string.Empty;
    private static Vector2 _nativeContentSize;
    private static bool _visible;
    private static bool _dirty;
    private static bool _clientReady;
    private static bool _remoteReady;
    private static bool _lastSentRemoteReady;
    private static bool _hostStateSent;
    private static bool _lastConnected;
    private static string _lastRemoteName = string.Empty;
    private static Languages _lastLanguage = Languages.Unknown;
    private static uint _roomId;
    private static uint _readySentForRoom;
    private static uint _readyRevision = 1;
    private static uint _lastRemoteReadyRevision;
    private static uint _stateRevision;
    private static uint _lastStateRevision;
    private static bool _clientWasConnected;
    private static string _name = string.Empty;
    private static string _address = "127.0.0.1";
    private static string _port = "27777";
    private static string _message = string.Empty;
    private static bool _openAfterHostLoss;
    private static bool _opening;
    private static bool _nativeSettingsRequested;
    private static float _nativePanelDeadline;
    private static bool _closing;

    internal static void RequestHostDisconnect()
    {
        _openAfterHostLoss = true;
        MarkDirty();
    }

    internal static void EnsureOnlineEntry(TitleManager manager)
    {
        if (manager == null || (_visible && manager == _manager))
            return;
        for (var index = 0; index < manager.buttons.Count; index++)
        {
            if (manager.buttons[index] != null && manager.buttons[index].gameObject.name == OnlineObjectName)
            {
                OpenAfterHostLoss(manager);
                return;
            }
        }
        if (manager.buttons.Count == 0)
            return;

        var sourceIndex = 0;
        var source = manager.buttons[sourceIndex];
        if (source == null)
            return;
        var gameObject = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
        var button = gameObject.GetComponent<TitleMenuButton>();
        if (button == null)
        {
            UnityEngine.Object.Destroy(gameObject);
            return;
        }

        gameObject.name = OnlineObjectName;
        button.buttonName = (ButtonName)Online;
        button.Init(manager);
        BindAction(button, manager, Online);
        SetLocalizedText(button, language => Text(OnlineText.Online, language));
        gameObject.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
        manager.buttons.Insert(sourceIndex + 1, button);
        ProbeBehaviour.Logger?.LogInfo("Title online entry added");
        OpenAfterHostLoss(manager);
    }

    internal static bool TryHandle(TitleManager manager, ButtonName action)
    {
        var code = (int)action;
        if (code == Online)
        {
            Open(manager);
            return true;
        }
        if (!_visible || manager != _manager || code is < Host or > Start)
            return false;

        var probe = ProbeBehaviour.Instance;
        if (probe == null)
            return true;

        switch (code)
        {
            case Host:
                if (!probe.SwitchTitleSession(SessionRole.Host, _address, _port, _name, out _message))
                    break;
                _roomId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                _remoteReady = false;
                _hostStateSent = false;
                _lastSentRemoteReady = false;
                _lastRemoteReadyRevision = 0;
                _stateRevision = 0;
                break;
            case Join:
                if (!probe.SwitchTitleSession(SessionRole.Client, _address, _port, _name, out _message))
                    break;
                _clientReady = false;
                _readyRevision = 1;
                _readySentForRoom = 0;
                _roomId = 0;
                _lastStateRevision = 0;
                _clientWasConnected = false;
                break;
            case Ready:
                _clientReady = !_clientReady;
                _readyRevision = NextRevision(_readyRevision);
                _readySentForRoom = 0;
                break;
            case Start:
                _message = Text(OnlineText.StartingCampaign, CurrentLanguage());
                MarkDirty();
                ProbeBehaviour.Logger?.LogInfo("Online room: host starts the native continue-game flow");
                manager.OnContinueGame();
                return true;
            case Back:
                probe.SwitchTitleSession(SessionRole.Offline, _address, _port, _name, out _);
                Close();
                return true;
        }

        MarkDirty();
        return true;
    }

    internal static bool TryHandleButton(TitleMenuButton button)
    {
        if (button == null || (int)button.buttonName is < Online or > Start)
            return false;
        var manager = _manager ?? UnityEngine.Object.FindFirstObjectByType<TitleManager>();
        return manager != null && TryHandle(manager, button.buttonName);
    }

    internal static void Tick(ProbeBehaviour probe)
    {
        if (_opening)
            TryFinishOpen();
        if (!_visible || _manager == null || probe == null)
            return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

        var session = probe._session;
        if (ProbeBehaviour.Role == SessionRole.Host && session != null)
        {
            if (_roomId == 0)
                _roomId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            while (session.TryTakeRoomReady(out var ready))
            {
                if (ready.RoomId == _roomId && IsNewer(ready.Revision, _lastRemoteReadyRevision))
                {
                    _lastRemoteReadyRevision = ready.Revision;
                    if (_remoteReady != ready.Ready)
                    {
                        _remoteReady = ready.Ready;
                        MarkDirty();
                    }
                }
            }
            if (session.Connected && _roomId != 0 &&
                (!_hostStateSent || _remoteReady != _lastSentRemoteReady))
            {
                _stateRevision = NextRevision(_stateRevision);
                session.SendRoomState(new RoomState(_roomId, _stateRevision, _remoteReady));
                _hostStateSent = true;
                _lastSentRemoteReady = _remoteReady;
            }
            if (!session.Connected)
            {
                if (_remoteReady)
                    MarkDirty();
                _remoteReady = false;
                _hostStateSent = false;
                _lastSentRemoteReady = false;
                _lastRemoteReadyRevision = 0;
            }
        }
        else if (ProbeBehaviour.Role == SessionRole.Client && session != null)
        {
            if (!session.Connected)
            {
                _clientWasConnected = false;
                _readySentForRoom = 0;
            }
            else if (!_clientWasConnected)
            {
                _clientWasConnected = true;
                _readySentForRoom = 0;
            }
            while (session.TryTakeRoomState(out var state))
            {
                if (state.RoomId != _roomId)
                {
                    _roomId = state.RoomId;
                    _lastStateRevision = 0;
                    _readySentForRoom = 0;
                    MarkDirty();
                }
                if (state.RoomId == _roomId && IsNewer(state.Revision, _lastStateRevision))
                {
                    _lastStateRevision = state.Revision;
                    if (_remoteReady != state.ClientReady)
                    {
                        _remoteReady = state.ClientReady;
                        MarkDirty();
                    }
                }
            }
            if (session.Connected && _roomId != 0 && _readySentForRoom != _roomId)
            {
                session.SendRoomReady(new RoomReady(_roomId, _readyRevision, _clientReady));
                _readySentForRoom = _roomId;
            }
        }

        var connected = session != null && session.Connected;
        var remoteName = session?.RemoteName ?? string.Empty;
        var language = CurrentLanguage();
        if (_lastConnected != connected || _lastRemoteName != remoteName || _lastLanguage != language)
        {
            _lastConnected = connected;
            _lastRemoteName = remoteName;
            _lastLanguage = language;
            MarkDirty();
        }
        if (_dirty)
            Rebuild(probe, session);
    }

    internal static void Reset(TitleManager manager)
    {
        if (manager == _manager)
            Close();
    }

    private static void Open(TitleManager manager)
    {
        if (manager == null || _visible || _opening)
            return;
        _manager = manager;
        _template = null;
        TitleButtons.Clear();
        for (var index = 0; index < manager.buttons.Count; index++)
        {
            var button = manager.buttons[index];
            if (button == null)
                continue;
            TitleButtons.Add(button);
            _template ??= button;
        }
        if (_template == null)
        {
            Close();
            return;
        }

        _opening = true;
        _nativeSettingsRequested = true;
        _nativePanelDeadline = Time.realtimeSinceStartup + NativePanelTimeout;
        try
        {
            manager.OnClickSetting();
        }
        catch (Exception exception)
        {
            ProbeBehaviour.Logger?.LogWarning($"Online room: settings open failed: {exception.Message}");
        }
        TryFinishOpen();
    }

    private static void TryFinishOpen()
    {
        if (!_opening || _manager == null)
            return;

        var source = FindSettingsPanel(_manager);
        if (source != null && source.gameObject.activeInHierarchy && source.scrollContent != null)
        {
            UseNativePanel(source);
            FinishOpen();
            return;
        }
        if (Time.realtimeSinceStartup < _nativePanelDeadline)
            return;
        ProbeBehaviour.Logger?.LogWarning("Online room: native settings panel did not open; using title fallback");
        if (CreateFallbackPanel())
            FinishOpen();
        else
            Close();
    }

    private static void FinishOpen()
    {
        if (_manager == null)
        {
            Close();
            return;
        }
        for (var index = 0; index < TitleButtons.Count; index++)
        {
            var button = TitleButtons[index];
            if (button != null)
                button.gameObject.SetActive(false);
        }

        var probe = ProbeBehaviour.Instance;
        _name = ProbeBehaviour.ConfiguredName;
        _address = string.IsNullOrWhiteSpace(ProbeBehaviour.Address) ? "127.0.0.1" : ProbeBehaviour.Address;
        _port = ProbeBehaviour.Port.ToString();
        _message = _openAfterHostLoss ? Text(OnlineText.HostDisconnected, CurrentLanguage()) : string.Empty;
        _openAfterHostLoss = false;
        if (probe != null && ProbeBehaviour.Role != SessionRole.Offline &&
            !probe.SwitchTitleSession(SessionRole.Offline, _address, _port, _name, out _message))
        {
            ProbeBehaviour.Logger?.LogWarning($"Online room could not stop session: {_message}");
        }
        _visible = true;
        _lastConnected = false;
        _lastRemoteName = string.Empty;
        _lastLanguage = Languages.Unknown;
        _opening = false;
        _manager.buttons.Clear();
        MarkDirty();
        Rebuild(probe, probe?._session);
        ProbeBehaviour.Logger?.LogInfo($"Title online room opened ({(_titleFallback ? "fallback" : "native settings")})");
    }

    private static void OpenAfterHostLoss(TitleManager manager)
    {
        if (_openAfterHostLoss && !_visible)
            Open(manager);
    }

    private static void UseNativePanel(SettingAppPanel panel)
    {
        _nativePanel = panel;
        _roomContent = panel.scrollContent;
        NativeContentStates.Clear();
        NativeControlStates.Clear();
        _nativeContentSize = _roomContent.sizeDelta;
        for (var index = 0; index < _roomContent.childCount; index++)
        {
            var content = _roomContent.GetChild(index).gameObject;
            NativeContentStates[content] = content.activeSelf;
            content.SetActive(false);
        }
        DisableNativeControl(panel);
        foreach (var layout in _roomContent.GetComponents<LayoutGroup>())
            DisableNativeControl(layout);
        foreach (var fitter in _roomContent.GetComponents<ContentSizeFitter>())
            DisableNativeControl(fitter);
        foreach (var scroll in panel.GetComponentsInChildren<ScrollRect>(true))
        {
            DisableNativeControl(scroll);
            HideNativeObject(scroll.horizontalScrollbar?.gameObject);
            HideNativeObject(scroll.verticalScrollbar?.gameObject);
        }
        HideNativeObject(panel.text?.gameObject);
        _roomContent.gameObject.SetActive(true);
        _roomContent.sizeDelta = new Vector2(_roomContent.sizeDelta.x, 460f);
        _nativeHeader = FindPanelHeader(panel);
        _nativeHeaderTitle = _nativeHeader?.text ?? string.Empty;
        _nativeCloseLabel = FindPanelCloseLabel(panel);
        _nativeCloseTitle = _nativeCloseLabel?.text ?? string.Empty;
        SetPanelTitle();
        _titleFallback = false;
    }

    private static bool CreateFallbackPanel()
    {
        _roomContent = _template?.transform.parent.GetComponent<RectTransform>();
        _titleFallback = _roomContent != null;
        return _titleFallback;
    }

    private static SettingAppPanel FindSettingsPanel(TitleManager manager)
    {
        if (manager.settingPopup != null)
            return manager.settingPopup;
        var panels = Resources.FindObjectsOfTypeAll<SettingAppPanel>();
        for (var index = 0; index < panels.Length; index++)
        {
            var panel = panels[index];
            if (panel != null && panel.scrollContent != null)
                return panel;
        }
        return null;
    }

    private static void Close()
    {
        if (_closing)
            return;
        _closing = true;
        DestroyRoom();
        RestoreNativePanel();
        _roomContent = null;
        _nativeHeader = null;
        _nativeHeaderTitle = string.Empty;
        _nativeCloseLabel = null;
        _nativeCloseTitle = string.Empty;
        _titleFallback = false;

        if (_manager != null)
        {
            _manager.buttons.Clear();
            for (var index = 0; index < TitleButtons.Count; index++)
            {
                var button = TitleButtons[index];
                if (button == null)
                    continue;
                button.gameObject.SetActive(true);
                _manager.buttons.Add(button);
            }
            _manager.m_MaxButtonCount = _manager.buttons.Count;
            _manager.m_Index = 0;
            if (_manager.buttons.Count > 0)
                _manager.buttons[0].SetFocus(true);
        }
        TitleButtons.Clear();
        _manager = null;
        _template = null;
        _visible = false;
        _dirty = false;
        _roomId = 0;
        _readySentForRoom = 0;
        _readyRevision = 1;
        _lastRemoteReadyRevision = 0;
        _stateRevision = 0;
        _lastStateRevision = 0;
        _clientWasConnected = false;
        _hostStateSent = false;
        _lastSentRemoteReady = false;
        _remoteReady = false;
        _clientReady = false;
        _lastConnected = false;
        _lastRemoteName = string.Empty;
        _lastLanguage = Languages.Unknown;
        _opening = false;
        _nativeSettingsRequested = false;
        _nativePanelDeadline = 0f;
        _closing = false;
    }

    private static void RestoreNativePanel()
    {
        if (_nativePanel != null)
        {
            foreach (var state in NativeContentStates)
            {
                if (state.Key != null)
                    state.Key.SetActive(state.Value);
            }
            if (_nativeHeader != null)
                _nativeHeader.text = _nativeHeaderTitle;
            if (_nativeCloseLabel != null)
                _nativeCloseLabel.text = _nativeCloseTitle;
            if (_roomContent != null)
                _roomContent.sizeDelta = _nativeContentSize;
            foreach (var state in NativeControlStates)
            {
                if (state.Key != null)
                    state.Key.enabled = state.Value;
            }
            if (_nativePanel.gameObject.activeInHierarchy)
                _nativePanel.Close();
        }
        else if (_nativeSettingsRequested)
        {
            var panel = FindSettingsPanel(_manager);
            if (panel != null && panel.gameObject.activeInHierarchy)
                panel.Close();
        }
        NativeContentStates.Clear();
        NativeControlStates.Clear();
        _nativePanel = null;
    }

    private static void HideNativeObject(GameObject gameObject)
    {
        if (gameObject == null)
            return;
        if (!NativeContentStates.ContainsKey(gameObject))
            NativeContentStates.Add(gameObject, gameObject.activeSelf);
        gameObject.SetActive(false);
    }

    private static TMP_Text FindPanelHeader(SettingAppPanel panel)
    {
        TMP_Text header = null;
        var highest = float.MinValue;
        foreach (var text in panel.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text == null || (_roomContent != null && text.transform.IsChildOf(_roomContent)))
                continue;
            var y = text.rectTransform.position.y;
            if (y <= highest)
                continue;
            highest = y;
            header = text;
        }
        return header;
    }

    private static TMP_Text FindPanelCloseLabel(SettingAppPanel panel)
    {
        TMP_Text label = null;
        var lowest = float.MaxValue;
        foreach (var text in panel.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text == null || text == _nativeHeader ||
                (_roomContent != null && text.transform.IsChildOf(_roomContent)))
                continue;
            var y = text.rectTransform.position.y;
            if (y >= lowest)
                continue;
            lowest = y;
            label = text;
        }
        return label;
    }

    private static void SetPanelTitle()
    {
        if (_nativeHeader != null)
            _nativeHeader.text = Text(OnlineText.Online, CurrentLanguage());
        if (_nativeCloseLabel != null)
            _nativeCloseLabel.text = Text(OnlineText.Back, CurrentLanguage());
    }

    internal static bool TryCloseNativePanel(SettingAppPanel panel)
    {
        if (_closing || !_visible || panel == null || panel != _nativePanel)
            return false;
        Close();
        return true;
    }

    private static void DisableNativeControl(Behaviour control)
    {
        if (control == null || NativeControlStates.ContainsKey(control))
            return;
        NativeControlStates[control] = control.enabled;
        control.enabled = false;
    }

    private static void Rebuild(ProbeBehaviour probe, UdpSession session)
    {
        if (_manager == null || _template == null || _roomContent == null)
            return;
        _dirty = false;
        DestroyRoom();
        _manager.buttons.Clear();
        SetPanelTitle();

        if (ProbeBehaviour.Role == SessionRole.Offline)
        {
            AddInput(OnlineText.Name, _name, 24, TMP_InputField.ContentType.Standard, value => _name = value);
            AddInput(OnlineText.HostIp, _address, 45, TMP_InputField.ContentType.Standard, value => _address = value);
            AddInput(OnlineText.Port, _port, 5, TMP_InputField.ContentType.IntegerNumber, value => _port = value);
            AddAction(language => Text(OnlineText.CreateLobby, language), Host);
            AddAction(language => Text(OnlineText.Join, language), Join);
            AddAction(language => Text(OnlineText.Back, language), Back);
        }
        else if (ProbeBehaviour.Role == SessionRole.Host)
        {
            AddLabel(language => Value(OnlineText.Host, DisplayName(), language));
            AddLabel(language => session != null && session.Connected
                ? $"{Value(OnlineText.PlayerTwo, session.RemoteName, language)} — {Text(_remoteReady ? OnlineText.Ready : OnlineText.NotReady, language)}"
                : Value(OnlineText.PlayerTwo, Text(OnlineText.Waiting, language), language));
            AddLabel(language => $"{Text(OnlineText.LocalNetwork, language)}: {probe?.TitleLanAddress ?? "unknown"}:{ProbeBehaviour.Port}");
            if (session != null && session.Connected && _remoteReady)
                AddAction(language => Text(OnlineText.Start, language), Start);
            else
                AddLabel(language => Text(OnlineText.WaitingForReady, language));
            AddAction(language => Text(OnlineText.Back, language), Back);
        }
        else
        {
            AddLabel(language => session != null && session.Connected
                ? Value(OnlineText.Host, session.RemoteName, language)
                : Value(OnlineText.Host, Text(OnlineText.Connecting, language), language));
            AddLabel(language => Value(OnlineText.PlayerTwo, DisplayName(), language));
            if (session != null && session.Connected && _roomId != 0)
                AddAction(language => Text(_clientReady ? OnlineText.NotReady : OnlineText.Ready, language), Ready);
            else
                AddLabel(language => Text(OnlineText.WaitingForHostRoom, language));
            AddAction(language => Text(OnlineText.Back, language), Back);
        }

        if (!string.IsNullOrEmpty(_message))
            AddLabel(_ => _message);
        _manager.m_MaxButtonCount = _manager.buttons.Count;
        _manager.m_Index = 0;
        if (_manager.buttons.Count > 0)
            _manager.buttons[0].SetFocus(true);
    }

    private static void AddAction(Func<Languages, string> text, int action)
    {
        var button = Create(text, action);
        if (button == null)
            return;
        RoomButtons.Add(button);
        ActionButtons[action] = button;
        _manager.buttons.Add(button);
        button.Init(_manager);
        BindAction(button, _manager, action);
    }

    private static void AddLabel(Func<Languages, string> text)
    {
        var label = Create(text, 0);
        if (label == null)
            return;
        RoomLabels.Add(label);
        label.enabled = false;
        var pointer = label.GetComponentInChildren<PointerEventComponent>(true);
        if (pointer != null)
            pointer.enabled = false;
    }

    private static void AddInput(
        OnlineText label,
        string value,
        int limit,
        TMP_InputField.ContentType contentType,
        Action<string> apply)
    {
        var row = CreateRow();
        if (row == null)
            return;
        var button = row.GetComponent<TitleMenuButton>();
        var labelText = button?.nameText?.text?.textTMProUGUI;
        if (button == null || labelText == null)
        {
            UnityEngine.Object.Destroy(row);
            return;
        }

        button.enabled = false;
        foreach (var pointer in row.GetComponentsInChildren<PointerEventComponent>(true))
            pointer.enabled = false;

        button.nameText.SetOverride((Func<string, string>)(_ => Text(label, CurrentLanguage())), true);
        labelText.text = Text(label, CurrentLanguage());
        labelText.alignment = TextAlignmentOptions.MidlineLeft;
        var labelRect = labelText.rectTransform;
        labelRect.anchorMin = new Vector2(0.06f, 0f);
        labelRect.anchorMax = new Vector2(0.4f, 1f);
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var fieldObject = UnityEngine.Object.Instantiate(labelText.gameObject, row.transform);
        fieldObject.name = "DaveTheDiverMP_Input";
        fieldObject.transform.SetParent(row.transform, false);
        var fieldRect = fieldObject.GetComponent<RectTransform>();
        fieldRect.anchorMin = new Vector2(0.42f, 0.16f);
        fieldRect.anchorMax = new Vector2(0.93f, 0.84f);
        fieldRect.offsetMin = Vector2.zero;
        fieldRect.offsetMax = Vector2.zero;
        var valueText = fieldObject.GetComponent<TextMeshProUGUI>();
        valueText.text = value ?? string.Empty;
        valueText.alignment = TextAlignmentOptions.MidlineLeft;
        valueText.raycastTarget = true;

        var input = fieldObject.AddComponent<TMP_InputField>();
        input.targetGraphic = valueText;
        input.textComponent = valueText;
        input.characterLimit = limit;
        input.contentType = contentType;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.text = value ?? string.Empty;
        input.onValueChanged.AddListener((UnityEngine.Events.UnityAction<string>)(changed => apply(changed)));
        BindInputPointer(fieldObject.AddComponent<PointerEventComponent>(), input);
        RoomFields.Add(row);
    }

    private static TitleMenuButton Create(Func<Languages, string> text, int action)
    {
        var clone = CreateRow();
        if (clone == null)
            return null;
        var button = clone.GetComponent<TitleMenuButton>();
        if (button == null)
        {
            UnityEngine.Object.Destroy(clone);
            return null;
        }
        button.buttonName = (ButtonName)action;
        SetLocalizedText(button, text);
        button.SetFocus(false);
        return button;
    }

    private static GameObject CreateRow()
    {
        var index = RoomButtons.Count + RoomLabels.Count + RoomFields.Count;
        if (index >= TitleButtons.Count)
            return null;
        var source = TitleButtons[index];
        if (source == null)
            return null;
        var clone = UnityEngine.Object.Instantiate(source.gameObject, _roomContent);
        clone.SetActive(true);
        clone.name = $"DaveTheDiverMP_Room_{index}";
        var rect = clone.GetComponent<RectTransform>();
        if (rect != null && !_titleFallback)
        {
            rect.anchorMin = new Vector2(0.08f, 1f);
            rect.anchorMax = new Vector2(0.92f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, RowStartY - index * RowStepY);
            rect.sizeDelta = new Vector2(0f, RowHeight);
        }
        return clone;
    }

    private static void DestroyRoom()
    {
        foreach (var button in RoomButtons)
        {
            if (button != null)
                UnityEngine.Object.Destroy(button.gameObject);
        }
        foreach (var label in RoomLabels)
        {
            if (label != null)
                UnityEngine.Object.Destroy(label.gameObject);
        }
        foreach (var field in RoomFields)
        {
            if (field != null)
                UnityEngine.Object.Destroy(field);
        }
        RoomButtons.Clear();
        RoomLabels.Clear();
        RoomFields.Clear();
        ActionButtons.Clear();
    }

    private static void SetLocalizedText(TitleMenuButton button, Func<Languages, string> text)
    {
        if (button == null || button.nameText == null)
            return;
        var label = button.nameText;
        label.SetOverride((Func<string, string>)(_ => text(label.currentLanguage)), true);
    }

    private static void BindAction(TitleMenuButton button, TitleManager manager, int action)
    {
        button.OnEventInvoke = new UnityEngine.Events.UnityEvent();
        button.OnEventInvoke.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            TryHandle(manager, (ButtonName)action);
        }));
        var pointers = button.GetComponentsInChildren<PointerEventComponent>(true);
        var bound = 0;
        foreach (var pointer in pointers)
        {
            if (pointer == null)
                continue;
            var graphic = pointer.GetComponent<Graphic>();
            if (graphic == null || !graphic.raycastTarget ||
                !pointer.gameObject.activeInHierarchy)
                continue;
            BindPointer(pointer, button);
            bound++;
        }
        if (bound == 0)
            BindPointer(button.gameObject.AddComponent<PointerEventComponent>(), button);
        ProbeBehaviour.Logger?.LogDebug($"Online button {action}: mouse targets={bound}");
    }

    private static void BindPointer(PointerEventComponent pointer, TitleMenuButton button)
    {
        if (pointer != null)
        {
            pointer.enabled = true;
            pointer.onClick = new UnityEngine.Events.UnityEvent();
            pointer.onClick.AddListener((UnityEngine.Events.UnityAction)button.Invoke);
        }
    }

    private static void BindInputPointer(PointerEventComponent pointer, TMP_InputField input)
    {
        if (pointer == null || input == null)
            return;
        pointer.enabled = true;
        pointer.onClick = new UnityEngine.Events.UnityEvent();
        pointer.onClick.AddListener((UnityEngine.Events.UnityAction)input.ActivateInputField);
    }

    private static void MarkDirty() => _dirty = true;

    private static Languages CurrentLanguage()
    {
        for (var index = 0; index < TitleButtons.Count; index++)
        {
            var button = TitleButtons[index];
            if (button != null && button.nameText != null)
                return button.nameText.currentLanguage;
        }
        return Languages.English;
    }

    private static string Text(OnlineText text, Languages language)
    {
        var languageIndex = language switch
        {
            Languages.Korean => 0,
            Languages.Japanese => 2,
            Languages.Chinese => 3,
            Languages.ChineseTraditional => 4,
            Languages.French => 5,
            Languages.Italian => 6,
            Languages.German => 7,
            Languages.Spanish => 8,
            Languages.Portuguese => 9,
            Languages.Russian => 10,
            _ => 1
        };
        return Texts[(int)text][languageIndex];
    }

    private static string Value(OnlineText text, string value, Languages language) =>
        $"{Text(text, language)}: {value}";

    private static uint NextRevision(uint revision) => revision == uint.MaxValue ? 1u : revision + 1u;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;

    private static string DisplayName() => Plugin.ResolvePlayerName(_name);
}

[HarmonyPatch(typeof(TitleManager), "RefreshButtonList")]
internal static class TitleOnlineEntryPatch
{
    private static void Postfix(TitleManager __instance) => TitleOnlineMenu.EnsureOnlineEntry(__instance);
}

[HarmonyPatch(typeof(TitleManager), nameof(TitleManager.OnSelect))]
internal static class TitleOnlineSelectPatch
{
    private static bool Prefix(TitleManager __instance, ButtonName name) =>
        !TitleOnlineMenu.TryHandle(__instance, name);
}

[HarmonyPatch(typeof(TitleMenuButton), nameof(TitleMenuButton.Invoke))]
internal static class TitleOnlineButtonInvokePatch
{
    private static bool Prefix(TitleMenuButton __instance) =>
        !TitleOnlineMenu.TryHandleButton(__instance);
}

[HarmonyPatch(typeof(SettingAppPanel), nameof(SettingAppPanel.Close))]
internal static class TitleOnlineSettingsClosePatch
{
    private static bool Prefix(SettingAppPanel __instance) =>
        !TitleOnlineMenu.TryCloseNativePanel(__instance);
}

[HarmonyPatch(typeof(TitleManager), "OnDestroy")]
internal static class TitleOnlineDestroyPatch
{
    private static void Prefix(TitleManager __instance) => TitleOnlineMenu.Reset(__instance);
}
