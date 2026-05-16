using WTelegram;

namespace Application.Sessions;

/// <summary>
/// A login flow being driven interactively (by the console today, by the bot tomorrow).
/// Each state is exposed as a TaskCompletionSource so the driver can asynchronously feed
/// the phone / code / 2FA password whenever the user provides it.
/// </summary>
public sealed class LoginSession
{
    public long UserId { get; }
    public Client? Client { get; }

    private readonly TaskCompletionSource<string> _phoneTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _codeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _passwordTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LoginSession(long userId, Client? client = null)
    {
        UserId = userId;
        Client = client;
    }

    public void SubmitPhone(string phone) => _phoneTcs.TrySetResult(phone);
    public void SubmitCode(string code) => _codeTcs.TrySetResult(code);
    public void SubmitPassword(string password) => _passwordTcs.TrySetResult(password);

    public bool IsPhoneAwaited => !_phoneTcs.Task.IsCompleted;
    public bool IsCodeAwaited => _phoneTcs.Task.IsCompleted && !_codeTcs.Task.IsCompleted;
    public bool IsPasswordAwaited => _codeTcs.Task.IsCompleted && !_passwordTcs.Task.IsCompleted;

    public Task<string> AwaitPhoneAsync() => _phoneTcs.Task;
    public Task<string> AwaitCodeAsync() => _codeTcs.Task;
    public Task<string> AwaitPasswordAsync() => _passwordTcs.Task;

    public void Cancel()
    {
        _phoneTcs.TrySetCanceled();
        _codeTcs.TrySetCanceled();
        _passwordTcs.TrySetCanceled();
    }
}
