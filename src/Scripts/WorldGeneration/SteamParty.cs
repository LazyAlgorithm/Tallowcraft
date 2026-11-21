using System.Globalization;
using Tallow;

namespace WorldGeneration;

// The connect argument distinguishes Tallowcraft from other games using Spacewar.
internal sealed class SteamParty
{
    internal const string Switch = "--tallowcraft-join";
    internal static string Connect => $"{Switch} {Steam.UserId}";
    private readonly List<(ulong Id, string Name)> _friends = new();
    private readonly ulong[] _visible = new ulong[3];
    private int _page;
    private float _nextRefresh;
    private string _feedback = "";
    private float _feedbackTime;

    internal static bool TryHost(string connect, out ulong host)
    {
        host = 0;
        return connect.StartsWith(Switch + " ", StringComparison.Ordinal) &&
            ulong.TryParse(connect.AsSpan(Switch.Length + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out host) && host != 0 && host != Steam.UserId;
    }

    internal void Turn(int direction)
    {
        _page = Math.Clamp(_page + direction, 0, Math.Max(0, (_friends.Count - 1) / _visible.Length));
        _nextRefresh = 0;
    }

    internal void Refresh(UIDocument doc, bool invitations, float dt)
    {
        _feedbackTime -= dt;
        if ((_nextRefresh -= dt) > 0) return;
        _nextRefresh = 2f;
        _friends.Clear();
        if (Steam.IsReady)
            foreach (ulong id in Steam.Friends)
            {
                if (!Steam.FriendOnline(id)) continue;
                if (!invitations && (!TryHost(Steam.FriendPresence(id, "connect"), out ulong host) || host != id)) continue;
                _friends.Add((id, Steam.FriendName(id)));
            }
        _friends.Sort((a,b) => string.Compare(a.Name,b.Name,StringComparison.OrdinalIgnoreCase));
        _page = Math.Min(_page, Math.Max(0, (_friends.Count - 1) / _visible.Length));
        for (int slot = 0; slot < _visible.Length; slot++)
        {
            int index = _page * _visible.Length + slot;
            bool show = index < _friends.Count;
            _visible[slot] = show ? _friends[index].Id : 0;
            if (doc.Find("friend-" + slot) is not { } row) continue;
            row.Visible = show;
            if (show) row.Text = (invitations ? "Invite   " : "Join   ") + _friends[index].Name;
        }
        if (doc.Find("friends-prev") is { } prev) prev.Visible = _page > 0;
        if (doc.Find("friends-next") is { } next) next.Visible = (_page+1)*_visible.Length < _friends.Count;
        string status = !Steam.IsReady ? "Open Steam and sign in to play with friends." :
            _friends.Count == 0 ? (invitations ? "No friends online." : "No friends hosting Tallowcraft.") :
            $"{_friends.Count} {(invitations ? "online" : "hosting")}   ·   Page {_page+1}";
        doc.Find("friends-status")?.SetText(_feedbackTime > 0 ? _feedback : status);
    }

    internal ulong Choose(string id, bool invite)
    {
        if (!Steam.IsReady || !id.StartsWith("friend-", StringComparison.Ordinal) ||
            !int.TryParse(id.AsSpan(7), out int slot) || (uint)slot >= _visible.Length) return 0;
        ulong friend = _visible[slot];
        if (friend == 0) return 0;
        if (Steam.IsFriend(friend) && Steam.FriendOnline(friend))
        {
            if (!invite && TryHost(Steam.FriendPresence(friend, "connect"), out ulong host) && host == friend)
                return host;
            if (invite && Steam.Invite(friend, Connect))
            {
                Feedback($"Invitation sent to {Steam.FriendName(friend)}.");
                return 0;
            }
        }
        Feedback(invite ? "Steam could not send the invitation." : "That expedition is no longer available.");
        return 0;
    }

    internal void Feedback(string message)
    {
        _feedback = message;
        _feedbackTime = 5f;
        _nextRefresh = 0;
    }
}
