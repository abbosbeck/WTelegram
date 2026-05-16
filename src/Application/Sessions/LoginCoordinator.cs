using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Application.Sessions;

public sealed class LoginCoordinator
{
    private readonly ConcurrentDictionary<long, LoginSession> _sessions = new();
    private readonly ILogger<LoginCoordinator> _logger;

    public LoginCoordinator(ILogger<LoginCoordinator> logger) { _logger = logger; }

    public bool TryRegister(long userId, LoginSession session) =>
        _sessions.TryAdd(userId, session);

    public LoginSession? Get(long userId) =>
        _sessions.TryGetValue(userId, out var s) ? s : null;

    public void Complete(long userId)
    {
        if (_sessions.TryRemove(userId, out _))
            _logger.LogInformation("Login completed for user {UserId}", userId);
    }

    public void Cancel(long userId)
    {
        if (_sessions.TryRemove(userId, out var s))
        {
            s.Cancel();
            _logger.LogInformation("Login cancelled for user {UserId}", userId);
        }
    }
}
