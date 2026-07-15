using System;
using BepInEx;
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
        ProbeBehaviour.Logger = Log;
        Log.LogInfo($"Probe loaded; Unity {Application.unityVersion}; Steam running: {SteamAPI.IsSteamRunning()}");
        AddComponent<ProbeBehaviour>();
    }
}

public sealed class ProbeBehaviour : MonoBehaviour
{
    internal static ManualLogSource Logger { get; set; }

    private string _scene = string.Empty;
    private bool _playerPresent;
    private float _nextScan;
    private float _nextPositionLog;

    public ProbeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < _nextScan)
            return;

        _nextScan = Time.realtimeSinceStartup + 1f;

        var scene = SceneManager.GetActiveScene().name;
        if (scene != _scene)
        {
            _scene = scene;
            Logger.LogInfo($"Scene: {_scene}");
        }

        var player = UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>();
        if (player == null)
        {
            if (_playerPresent)
                Logger.LogInfo("PlayerCharacter left the scene");
            _playerPresent = false;
            return;
        }

        if (!_playerPresent || Time.realtimeSinceStartup >= _nextPositionLog)
        {
            var position = player.transform.position;
            Logger.LogInfo($"PlayerCharacter: ({position.x:F2}, {position.y:F2}, {position.z:F2})");
            _nextPositionLog = Time.realtimeSinceStartup + 5f;
        }

        _playerPresent = true;
    }
}
