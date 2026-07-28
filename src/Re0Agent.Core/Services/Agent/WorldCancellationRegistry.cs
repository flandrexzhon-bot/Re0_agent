using System.Collections.Concurrent;

namespace Re0Agent.Core.Services.Agent;

public sealed class WorldCancellationRegistry
{
    private readonly ConcurrentDictionary<int, CancellationDomains> domains = new();

    public CancellationToken BackgroundToken(int sessionId) => Get(sessionId).Background.Token;
    public CancellationToken ForegroundToken(int sessionId) => Get(sessionId).Foreground.Token;

    public void CancelForeground(int sessionId) => Get(sessionId).Foreground.Cancel();
    public void CancelBackground(int sessionId) => Get(sessionId).Background.Cancel();
    public void CancelSession(int sessionId)
    {
        var domain = Get(sessionId);
        domain.Foreground.Cancel();
        domain.Background.Cancel();
    }

    public void ResetForeground(int sessionId)
    {
        var domain = Get(sessionId);
        if (!domain.Foreground.IsCancellationRequested) return;
        domain.Foreground.Dispose();
        domain.Foreground = new CancellationTokenSource();
    }

    private CancellationDomains Get(int sessionId) => domains.GetOrAdd(sessionId, _ => new CancellationDomains());

    private sealed class CancellationDomains
    {
        public CancellationTokenSource Foreground { get; set; } = new();
        public CancellationTokenSource Background { get; } = new();
    }
}
