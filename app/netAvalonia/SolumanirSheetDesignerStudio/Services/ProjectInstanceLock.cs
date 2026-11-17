using System.IO.Pipes;
using System.Text;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Settings;

namespace SolumanirSheetDesignerStudio.App.Services;

/// <summary>
/// One window per project file: the first instance that opens a project holds a lock file and listens on a named
/// pipe, both derived from the file's path; a second instance asked to open the same file tells the first to come
/// to the front instead.
/// </summary>
public sealed class ProjectInstanceLock : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private FileStream? _lockFile;

    private ProjectInstanceLock() { }

    private static string PipeName(string projectPath) => "ssds-" + PathUtil.Key(projectPath);

    /// <summary>Returns null when another instance already has the project open (and has been asked to activate).</summary>
    public static ProjectInstanceLock? TryAcquire(string projectPath, Action activate)
    {
        string lockPath = Path.Combine(AppPaths.LocksDir, PathUtil.Key(projectPath) + ".lock");
        FileStream? lockFile = null;
        try
        {
            Directory.CreateDirectory(AppPaths.LocksDir);
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // Held by another instance — unless the lock file is not even there, in which case the folder itself is
            // the problem and single-instance checking is simply unavailable.
            if (SignalExisting(projectPath) || File.Exists(lockPath)) return null;
        }
        catch (UnauthorizedAccessException)
        {
            // cannot lock: behave as if single-instance checking is unavailable
        }

        var instance = new ProjectInstanceLock { _lockFile = lockFile };
        _ = instance.ListenAsync(PipeName(projectPath), activate);
        return instance;
    }

    private static bool SignalExisting(string projectPath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(projectPath), PipeDirection.Out);
            client.Connect(800);
            client.Write(Encoding.ASCII.GetBytes("activate"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false; // the other instance is busy or gone
        }
    }

    private async Task ListenAsync(string pipeName, Action activate)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token);
                var buffer = new byte[16];
                _ = await server.ReadAsync(buffer, _cts.Token);
                activate();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _lockFile?.Dispose();
        _lockFile = null;
    }
}
