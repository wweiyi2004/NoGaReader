using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace NoGaReader.Services;

public sealed class SingleInstanceService : IDisposable
{
    public const string MutexName = "Local\\NoGaReader.SingleInstance.v1";
    public const string PipeName = "NoGaReader.OpenFile.v1";

    private readonly Mutex _mutex;
    private readonly bool _isPrimary;
    private CancellationTokenSource? _listenCancellation;
    private Task? _listenTask;
    private bool _disposed;

    private SingleInstanceService(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        _isPrimary = isPrimary;
    }

    public bool IsPrimaryInstance => _isPrimary;

    public event Action<string>? OpenPathRequested;

    public static SingleInstanceService Acquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        return new SingleInstanceService(mutex, createdNew);
    }

    public bool TrySignalExistingInstance(IEnumerable<string> args)
    {
        var payload = string.Join('\n', args
            .Select(arg => arg?.Trim())
            .Where(arg => !string.IsNullOrWhiteSpace(arg))!);
        if (string.IsNullOrWhiteSpace(payload))
        {
            payload = "__ACTIVATE__";
        }

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect(1500);
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartListening()
    {
        if (!_isPrimary || _listenTask is not null)
        {
            return;
        }

        _listenCancellation = new CancellationTokenSource();
        var token = _listenCancellation.Token;
        _listenTask = Task.Run(() => ListenLoopAsync(token), token);
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                var payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }

                var app = Application.Current;
                if (app is null)
                {
                    continue;
                }

                await app.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var line in payload.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (string.Equals(line, "__ACTIVATE__", StringComparison.Ordinal))
                        {
                            OpenPathRequested?.Invoke(string.Empty);
                            continue;
                        }

                        OpenPathRequested?.Invoke(line.Trim());
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        try
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                security);
        }
        catch
        {
            return new NamedPipeServerStream(
                PipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listenCancellation?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listenTask?.Wait(500);
        }
        catch
        {
            // ignore
        }

        _listenCancellation?.Dispose();
        try
        {
            if (_isPrimary)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // ignore
        }

        _mutex.Dispose();
    }
}
