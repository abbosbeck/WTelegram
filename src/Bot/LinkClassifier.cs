using System.Text.RegularExpressions;

namespace Bot;

internal enum LinkKind
{
    Invalid,
    Telegram,
    Web,
}

internal readonly record struct LinkClassification(LinkKind Kind, string Normalized);

/// <summary>
/// Decides whether a user-provided link is a Telegram message link, a web URL
/// (yt-dlp territory), or neither. Used by the unified "Download by link" flow.
/// </summary>
internal static class LinkClassifier
{
    // https?://(t|telegram).me/[c/<num>/|<user>/]<id>[?...]
    private static readonly Regex TmeWithId = new(
        @"^(?:https?://)?(?:t|telegram)\.me/(?:c/\d+|[A-Za-z0-9_]+)/\d+(?:\?\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // @username/123
    private static readonly Regex AtUsernameMsg = new(
        @"^@(?<user>[A-Za-z0-9_]{4,32})/(?<id>\d+)$",
        RegexOptions.Compiled);

    // @username only (no message id)
    private static readonly Regex BareAtUsername = new(
        @"^@[A-Za-z0-9_]{4,32}$",
        RegexOptions.Compiled);

    // t.me/<user> or https://t.me/<user> — username only, no message id.
    private static readonly Regex TmeUserOnly = new(
        @"^(?:https?://)?(?:t|telegram)\.me/(?<user>[A-Za-z0-9_]{4,32})/?(?:\?\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Accepts the various ways a user might type a Telegram peer reference and
    /// returns a form the MTProto resolver understands:
    ///   "@user" / "user" / "t.me/user" / "https://t.me/user" → "@user"
    ///   "12345" → "12345"
    /// </summary>
    public static string NormalizePeerTarget(string input)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0) return s;
        var m = TmeUserOnly.Match(s);
        if (m.Success) return "@" + m.Groups["user"].Value;
        return s;
    }

    public static LinkClassification Classify(string input)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0) return new(LinkKind.Invalid, "");

        if (TmeWithId.IsMatch(s))
            return new(LinkKind.Telegram, NormalizeTmeUrl(s));

        var atMatch = AtUsernameMsg.Match(s);
        if (atMatch.Success)
        {
            var url = $"https://t.me/{atMatch.Groups["user"].Value}/{atMatch.Groups["id"].Value}";
            return new(LinkKind.Telegram, url);
        }

        // Bare @username (no message id) is ambiguous for this flow.
        // The user probably wanted "From chat" — treat as invalid here.
        if (BareAtUsername.IsMatch(s))
            return new(LinkKind.Invalid, s);

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new(LinkKind.Web, s);
        }

        return new(LinkKind.Invalid, s);
    }

    private static string NormalizeTmeUrl(string s)
    {
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        return "https://" + s;
    }
}
