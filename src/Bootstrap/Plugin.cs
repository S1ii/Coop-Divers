using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Steamworks;
using UnityEngine;

namespace DaveTheDiverMP;

[BepInPlugin("dev.davethedivermp", "Dave the Diver Multiplayer", "0.16.0")]
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
        ClassInjector.RegisterTypeInIl2Cpp<ProbeBehaviour>();
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
