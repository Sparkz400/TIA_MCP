using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

/// <summary>
/// Copies the Host's log records to the client as notifications/message once it sends logging/setLevel; stderr keeps every record.
/// Only Information and above are forwarded, without exception details: SDK debug and trace records and exception messages
/// can carry request and result payloads.
/// Native worker output stays on stderr: it is free text the Host did not write and cannot vouch for.
/// </summary>
internal sealed class ClientLogging : ILoggerProvider
{
    private ILoggerProvider? _client;
    [ThreadStatic] private static bool _forwarding;

    // The SDK stores the level and filters by it; this handler only learns which session to send to.
    internal ValueTask<EmptyResult> SetLevel(RequestContext<SetLevelRequestParams> context, CancellationToken cancellationToken)
    {
        Interlocked.CompareExchange(ref _client, context.Server.AsClientLoggerProvider(), null);
        return ValueTask.FromResult(new EmptyResult());
    }

    public ILogger CreateLogger(string categoryName) => new Forwarder(this, categoryName);
    public void Dispose() => _client?.Dispose();

    private sealed class Forwarder(ClientLogging owner, string category) : ILogger
    {
        private ILogger? _target;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None && owner._client is not null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // A record raised while sending a record is not sent again, so a failing transport cannot recurse.
            if (!IsEnabled(logLevel) || _forwarding) return;
            _forwarding = true;
            try { (_target ??= owner._client!.CreateLogger(category)).Log(logLevel, eventId, state, null, (value, _) => formatter(value, null)); }
            finally { _forwarding = false; }
        }
    }
}
