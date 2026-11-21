using Tallow;

namespace WorldGeneration;

public sealed class ExpeditionMenu : Script
{
    public const string World = "Scenes/Main.tscene";
    public const string Title = "Scenes/Expedition.tscene";
    public bool ConnectionScreen;
    private readonly SteamParty _friends = new();
    private static bool _launchHandled;
    private bool _loading;
    private bool _published;
    private bool _friendsOpen;
    private float _elapsed;
    private string _lastStatus = "";
    private GameObject? _hud;

    public override void Start()
    {
        Cursor.Locked = false;
        if (ConnectionScreen)
        {
            if (!Network.InSession(Scene)) Prefab.Instantiate(Scene, "Prefabs/Player.tprefab");
            return;
        }
        Steam.Start();
        if (_launchHandled) return;
        _launchHandled = true;
        if (CommandLine.Has("--host") || CommandLine.Has("--server") || CommandLine.Has("--connect"))
        {
            SceneManager.Load(World);
            return;
        }
        if (CommandLine.Get(SteamParty.Switch) is string arg &&
            SteamParty.TryHost($"{SteamParty.Switch} {arg}", out ulong host)) Join(host);
    }

    public override void LocalUpdate(float deltaTime)
    {
        var doc = GetComponent<UIDocument>();
        if (Steam.IsReady)
        {
            if (!Network.InSession(Scene)) Steam.Update();
            if (!_loading && Steam.TakeJoinRequest(out string invite) && SteamParty.TryHost(invite, out ulong host))
            {
                Join(host);
                return;
            }
        }
        if (!ConnectionScreen)
        {
            if (doc.Find("friend-list") is { } friends) friends.Visible = _friendsOpen;
            if (doc.Find("launch-options") is { } options) options.Visible = !_friendsOpen;
            if (_friendsOpen) _friends.Refresh(doc, false, deltaTime);
            return;
        }
        if (_hud is not { IsValid: true }) _hud = Scene.Find("Tallowcraft HUD (local)");
        bool playing = _hud is { IsValid: true } && (!Network.InSession(Scene) || Network.IsConnected(Scene));
        if (doc.Find("connection") is not { } panel) return;
        if (panel.Visible == playing) panel.Visible = !playing;
        if (playing)
        {
            if (!_published && Network.InSession(Scene) && Scene.IsServer && Steam.IsReady)
            {
                Steam.SetPresence("status", "Hosting a Tallowcraft expedition");
                _published = Steam.SetPresence("connect", SteamParty.Connect);
            }
            _elapsed = 0;
            return;
        }
        Cursor.Locked = false;
        _elapsed += deltaTime;
        string status = _elapsed > 12 ? "Still waiting for Steam. Check that the host is online." : "Connecting to the expedition…";
        if (status != _lastStatus)
        {
            doc.Find("progress")?.SetText(status);
            _lastStatus = status;
        }
    }

    public void OnSolo()
    {
        if (_loading) return;
        _loading = true;
        Network.Leave(Scene);
        SceneManager.Load(World);
    }

    public void OnHost()
    {
        if (_loading) return;
        if (!Steam.Start())
        {
            GetComponent<UIDocument>().Find("launch-note")?.SetText("Open Steam and sign in to host a multiplayer world.");
            return;
        }
        _loading = true;
        Audio.PlayOneShot2D("Audio/tap.wav", .5f);
        Network.Host();
        SceneManager.Load(World);
    }

    private void Join(ulong host)
    {
        if (_loading || !Steam.IsReady) return;
        _loading = true;
        if (Network.InSession(Scene)) Network.Leave(Scene);
        Audio.PlayOneShot2D("Audio/tap.wav", .5f);
        Network.Join(host.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SceneManager.Load(World);
    }

    public void OnFriends()
    {
        _friendsOpen = !_friendsOpen;
        if (_friendsOpen) Steam.Start();
    }
    public void OnFriend(string id)
    {
        if (_friends.Choose(id, false) is ulong host && host != 0) Join(host);
    }
    public void OnPrevious() => _friends.Turn(-1);
    public void OnNext() => _friends.Turn(1);

    public void OnBack()
    {
        Network.Leave(Scene);
        SceneManager.Load(Title);
    }
    public override void OnDestroy()
    {
        if (_published) Steam.ClearPresence();
    }
    public void OnQuit() => Application.Quit();
}
