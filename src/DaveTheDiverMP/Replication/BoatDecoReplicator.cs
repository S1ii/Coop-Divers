using BepInEx.Logging;
using DR.Save;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class BoatDecoReplicator
{
    private readonly ManualLogSource _log;
    private int _lastHostId = int.MinValue;
    private int _lastClientId = int.MinValue;
    private int? _pendingClientId;
    private float _nextRead;
    private bool _applyFailed;
    private bool _applying;

    internal BoatDecoReplicator(ManualLogSource log) => _log = log;

    internal void Update(SessionRole role, UdpSession session, float now)
    {
        if (session == null || !session.Connected)
        {
            Clear();
            return;
        }

        if (role == SessionRole.Host)
        {
            while (session.TryTakeBoatDecoState(out var requested))
            {
                if (!TryPersistHostId(requested.Id) || !TryApply(requested.Id))
                    continue;
                _lastHostId = requested.Id;
                session.SendBoatDecoState(requested);
            }
            if (now < _nextRead || !TryReadHostId(out var id))
                return;
            _nextRead = now + 0.25f;
            if (_lastHostId == id)
                return;
            _lastHostId = id;
            session.SendBoatDecoState(new BoatDecoState(id));
            return;
        }

        if (role != SessionRole.Client)
            return;
        while (session.TryTakeBoatDecoState(out var state))
            _pendingClientId = state.Id;
        if (!_pendingClientId.HasValue || UnityEngine.Object.FindFirstObjectByType<UpdateBoatDeco>() == null)
            return;
        if (TryApply(_pendingClientId.Value))
        {
            _lastClientId = _pendingClientId.Value;
            _pendingClientId = null;
            _applyFailed = false;
        }
    }

    internal void OnLocalChange(SessionRole role, UdpSession session, int id)
    {
        if (_applying || session == null || !session.Connected || id < 0)
            return;
        if (role == SessionRole.Host)
        {
            _lastHostId = id;
            session.SendBoatDecoState(new BoatDecoState(id));
        }
        else if (role == SessionRole.Client)
        {
            _lastClientId = id;
            session.SendBoatDecoState(new BoatDecoState(id));
        }
    }

    internal void Clear()
    {
        _lastHostId = int.MinValue;
        _lastClientId = int.MinValue;
        _pendingClientId = null;
        _nextRead = 0f;
        _applyFailed = false;
        _applying = false;
    }

    private static bool TryReadHostId(out int id)
    {
        id = 0;
        var save = SaveSystem.GetGameSave();
        if (save == null || save.BoatDecoSaveData == null)
            return false;
        id = save.BoatDecoSaveData.CurrentBoatDecoID;
        return true;
    }

    private bool TryPersistHostId(int id)
    {
        try
        {
            var save = SaveSystem.GetGameSave();
            if (save?.BoatDecoSaveData == null)
                return false;
            save.BoatDecoSaveData.CurrentBoatDecoID = id;
            return true;
        }
        catch (System.Exception exception)
        {
            _log.LogWarning($"Boat decoration save failed: {exception.Message}");
            return false;
        }
    }

    private bool TryApply(int id)
    {
        try
        {
            _applying = true;
            DREventManager.EventTrigger(new UpdateBoatDecoEvent { boatDecoID = id });
            return true;
        }
        catch (System.Exception exception)
        {
            if (!_applyFailed)
                _log.LogWarning($"Boat decoration apply failed: {exception.Message}");
            _applyFailed = true;
            return false;
        }
        finally
        {
            _applying = false;
        }
    }
}

[HarmonyPatch(typeof(UpdateBoatDeco), nameof(UpdateBoatDeco.UpdateDeco))]
internal static class BoatDecoChangePatch
{
    private static void Postfix(int __0) =>
        ProbeBehaviour.Instance?.ReportBoatDecoChange(__0);
}
