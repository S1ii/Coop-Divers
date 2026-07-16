using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace DaveTheDiverMP;

internal sealed class SessionTrace : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualLogSource _log;
    private StreamWriter _writer;
    private SessionRole _role;
    private bool _hostOpened;
    private bool _clientOpened;
    private bool _reportedFailure;

    internal SessionTrace(ManualLogSource log) => _log = log;

    internal void SwitchRole(SessionRole role, string playerName, uint buildId)
    {
        lock (_gate)
        {
            CloseWriter();
            _role = role;
            _reportedFailure = false;
            if (role == SessionRole.Offline)
                return;

            try
            {
                var directory = Path.Combine(Paths.BepInExRootPath, "DTMP-logs");
                Directory.CreateDirectory(directory);
                var processId = Process.GetCurrentProcess().Id;
                var path = Path.Combine(directory, $"{role}-{processId}.log");
                var append = role == SessionRole.Host ? _hostOpened : _clientOpened;
                if (role == SessionRole.Host)
                    _hostOpened = true;
                else
                    _clientOpened = true;
                _writer = new StreamWriter(
                    new FileStream(path, append ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                WriteUnsafe("SESSION",
                    $"started player={Protocol.NormalizePlayerName(playerName)} " +
                    $"build={buildId:X8} pid={processId}");
                _log.LogInfo($"Network trace: {path}");
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    internal void Write(string category, string message)
    {
        lock (_gate)
        {
            if (_writer == null)
                return;
            try
            {
                WriteUnsafe(category, message);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
                CloseWriter();
            }
        }
    }

    private void WriteUnsafe(string category, string message) =>
        _writer.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [{_role}] [{category}] {message}");

    private void ReportFailure(Exception exception)
    {
        if (_reportedFailure)
            return;
        _reportedFailure = true;
        _log.LogWarning($"Network trace unavailable: {exception.Message}");
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
        }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_gate)
            CloseWriter();
    }
}
