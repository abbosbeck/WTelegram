using Microsoft.Extensions.Options;
using TelegramDownloader.Ui;

namespace TelegramDownloader.Configuration;

internal sealed class WTelegramConfigProvider
{
    private readonly TelegramOptions _options;
    private readonly IConsolePrompt _prompt;

    public WTelegramConfigProvider(IOptions<TelegramOptions> options, IConsolePrompt prompt)
    {
        _options = options.Value;
        _prompt = prompt;

        if (_options.ApiId == 0 || string.IsNullOrWhiteSpace(_options.ApiHash))
        {
            throw new InvalidOperationException(
                "Telegram:ApiId / Telegram:ApiHash are not configured. " +
                "Set them via user-secrets, environment variables (Telegram__ApiId, Telegram__ApiHash) " +
                "or appsettings.Local.json.");
        }
    }

    public string? Provide(string what) => what switch
    {
        "api_id" => _options.ApiId.ToString(),
        "api_hash" => _options.ApiHash,
        "phone_number" => _prompt.Ask("Phone number (international format, e.g. +998901234567): "),
        "verification_code" => _prompt.Ask("Verification code from Telegram: "),
        "password" => _prompt.Ask("2FA password (if enabled): "),
        "session_pathname" => _options.ResolvedSessionPathname,
        _ => null
    };
}
