using System.Text.RegularExpressions;
using TL;
using WTelegram;

namespace TelegramDownloader.Services;

/// <summary>
/// Parses t.me message links and fetches that single message.
/// Supports:
///   https://t.me/&lt;username&gt;/&lt;msgId&gt;
///   https://t.me/c/&lt;internalChannelId&gt;/&lt;msgId&gt;
/// </summary>
internal sealed partial class MessageLinkResolver
{
    private readonly Client _client;

    public MessageLinkResolver(Client client)
    {
        _client = client;
    }

    public async Task<(InputPeer Peer, long ChatId, Message Message)> ResolveAsync(string link, CancellationToken ct)
    {
        var publicMatch = PublicLinkRegex().Match(link);
        if (publicMatch.Success)
        {
            var username = publicMatch.Groups["user"].Value;
            var msgId = int.Parse(publicMatch.Groups["id"].Value);

            var resolved = await _client.Contacts_ResolveUsername(username).WaitAsync(ct);
            var peer = resolved.peer switch
            {
                PeerChannel c => resolved.chats[c.channel_id].ToInputPeer(),
                PeerChat g => resolved.chats[g.chat_id].ToInputPeer(),
                PeerUser u => resolved.users[u.user_id].ToInputPeer(),
                _ => throw new InvalidOperationException("Unknown peer type.")
            };

            var msg = await FetchMessageAsync(peer, msgId, ct);
            return (peer, PeerIdFromInput(peer), msg);
        }

        var privateMatch = PrivateLinkRegex().Match(link);
        if (privateMatch.Success)
        {
            long internalId = long.Parse(privateMatch.Groups["chan"].Value);
            int msgId = int.Parse(privateMatch.Groups["id"].Value);

            var dialogs = await _client.Messages_GetAllDialogs().WaitAsync(ct);
            if (!dialogs.chats.TryGetValue(internalId, out var chat))
                throw new InvalidOperationException(
                    $"Channel {internalId} not found in your dialogs. Join the chat first.");

            var peer = chat.ToInputPeer();
            var msg = await FetchMessageAsync(peer, msgId, ct);
            return (peer, internalId, msg);
        }

        throw new InvalidOperationException($"Not a valid t.me message link: {link}");
    }

    private async Task<Message> FetchMessageAsync(InputPeer peer, int msgId, CancellationToken ct)
    {
        Messages_MessagesBase result = peer is InputPeerChannel ipc
            ? await _client.Channels_GetMessages(new InputChannel(ipc.channel_id, ipc.access_hash), msgId).WaitAsync(ct)
            : await _client.Messages_GetMessages(msgId).WaitAsync(ct);

        var msg = result.Messages.OfType<Message>().FirstOrDefault(m => m.ID == msgId)
                  ?? throw new InvalidOperationException($"Message {msgId} not found or inaccessible.");
        return msg;
    }

    private static long PeerIdFromInput(InputPeer peer) => peer switch
    {
        InputPeerChannel c => c.channel_id,
        InputPeerChat c => c.chat_id,
        InputPeerUser u => u.user_id,
        _ => 0
    };

    [GeneratedRegex(@"^https?://t\.me/(?!c/)(?<user>[A-Za-z0-9_]+)/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PublicLinkRegex();

    [GeneratedRegex(@"^https?://t\.me/c/(?<chan>\d+)/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateLinkRegex();
}
