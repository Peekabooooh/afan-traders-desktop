using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Makaretu.Dns;

namespace AfanTraders.Core;

/// <summary>
/// Office network sync – same protocol as the Mac app (macOS/Sources/AfanTraders/Sync/LANSync.swift),
/// so Mac and Windows computers in the office sync with each other.
/// Discovery: Bonjour/mDNS service "_afansync._tcp". Messages: 4-byte big-endian length + JSON.
/// The office code is proven with HMAC-SHA256 challenges in both directions and never sent.
/// </summary>
public sealed class LanSync : IDisposable
{
    public sealed class Peer
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "";
        public bool Connected { get; set; }
        public DateTime? LastSync { get; set; }
    }

    public const string ServiceType = "_afansync._tcp";
    public bool Enabled { get; private set; }
    public bool Listening => listener != null;
    public int? Port { get; private set; }
    public List<string> Problems { get; private set; } = new();
    public IReadOnlyCollection<Peer> Peers { get { lock (gate) return peers.Values.ToList(); } }
    public event Action? StatusChanged;

    public string DeviceId { get; }
    public string ComputerName { get; } = Environment.MachineName;

    readonly Store store;
    readonly ISecretStore secrets;
    readonly object gate = new();
    readonly Dictionary<string, Peer> peers = new();
    readonly List<Conn> servers = new();
    readonly Dictionary<string, Conn> clients = new();
    string officeCode;
    TcpListener? listener;
    MulticastService? mdns;
    ServiceDiscovery? sd;
    CancellationTokenSource? cts;
    Timer? retry;

    public LanSync(Store store, ISecretStore secrets, string deviceId)
    {
        this.store = store;
        this.secrets = secrets;
        DeviceId = deviceId;
        officeCode = Environment.GetEnvironmentVariable("AFAN_OFFICE_CODE") ?? secrets.Get("office_code") ?? "";
        store.LocalChange += LocalChanged;
        if (store.SyncValue("lan_enabled") == "1" && officeCode.Length > 0) Start();
    }

    public bool HasCode => officeCode.Length > 0;

    public string StatusText
    {
        get
        {
            if (!Enabled) return "Off";
            var on = Peers.Where(p => p.Connected).Select(p => p.Name).OrderBy(n => n).ToList();
            return on.Count == 0 ? "Looking for other computers…" : $"Connected to {on.Count} computer{(on.Count == 1 ? "" : "s")}: {string.Join(", ", on)}";
        }
    }

    public void TurnOn(string code)
    {
        if (code.T().Length < 4) return;
        officeCode = code.T();
        secrets.Set("office_code", officeCode);
        store.SetSyncValue("lan_enabled", "1");
        Stop();
        Start();
    }

    public void TurnOff()
    {
        store.SetSyncValue("lan_enabled", "0");
        Stop();
    }

    /// <summary>Self-test only: start with a code without saving it, and without mDNS.</summary>
    public void StartForTest(string code) { officeCode = code; Start(advertise: false); }

    public void Start(bool advertise = true)
    {
        if (officeCode.Length == 0) return;
        Enabled = true;
        cts = new CancellationTokenSource();
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, 0);
            listener.Server.DualMode = true;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoop(listener, cts.Token);
        }
        catch (Exception e) { Problems = new() { $"Can't listen for other computers: {e.Message}" }; }
        if (advertise && Port is { } port)
        {
            try
            {
                mdns = new MulticastService();
                sd = new ServiceDiscovery(mdns);
                sd.Advertise(new ServiceProfile($"AFAN-{DeviceId[..12]}", ServiceType, (ushort)port));
                mdns.AnswerReceived += OnMdnsAnswer;
                sd.ServiceInstanceDiscovered += (_, e) => { try { mdns?.SendQuery(e.ServiceInstanceName, type: DnsType.SRV); } catch { } };
                mdns.NetworkInterfaceDiscovered += (_, _) => { try { sd?.QueryServiceInstances(ServiceType); } catch { } };
                mdns.Start();
                sd.QueryServiceInstances(ServiceType);
            }
            catch (Exception e) { Problems = new() { $"Can't search the network: {e.Message}" }; }
        }
        retry = new Timer(_ =>
        {
            try
            {
                foreach (var c in Clients()) if (c.Authed) c.Pull();
                sd?.QueryServiceInstances(ServiceType);
            }
            catch { /* try again next time */ }
        }, null, 30_000, 30_000);
        StatusChanged?.Invoke();
    }

    void Stop()
    {
        Enabled = false;
        retry?.Dispose(); retry = null;
        cts?.Cancel();
        try { sd?.Unadvertise(); } catch { }
        sd?.Dispose(); sd = null;
        mdns?.Stop(); mdns?.Dispose(); mdns = null;
        listener?.Stop(); listener = null; Port = null;
        lock (gate)
        {
            foreach (var c in servers.Concat(clients.Values).ToList()) c.Close();
            servers.Clear(); clients.Clear();
            foreach (var p in peers.Values) p.Connected = false;
        }
        StatusChanged?.Invoke();
    }

    public void Dispose() { store.LocalChange -= LocalChanged; Stop(); }

    List<Conn> Clients() { lock (gate) return clients.Values.ToList(); }

    /// <summary>Tell connected computers that something changed here.</summary>
    void LocalChanged()
    {
        List<Conn> list;
        lock (gate) list = servers.Where(s => s.Authed).ToList();
        foreach (var s in list) s.Send(new JsonObject { ["type"] = "changed" });
    }

    // MARK: Discovery

    void OnMdnsAnswer(object? sender, MessageEventArgs e)
    {
        try { HandleAnswer(e); } catch { /* malformed network traffic – ignore */ }
    }

    void HandleAnswer(MessageEventArgs e)
    {
        var all = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToList();
        foreach (var srv in all.OfType<SRVRecord>().Where(r => r.Name.ToString().Contains(ServiceType)))
        {
            if (srv.Name.ToString().Contains(DeviceId[..12])) continue;   // ourselves
            var host = srv.Target;
            var addrs = all.OfType<AddressRecord>().Where(a => a.Name == host).Select(a => a.Address).ToList();
            if (addrs.Count == 0) { mdns?.SendQuery(host, type: DnsType.A); continue; }
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
            Connect(ip.ToString(), srv.Port);
        }
    }

    /// <summary>Connects to another computer (also used directly by the self-test).</summary>
    public void Connect(string host, int port)
    {
        var key = $"{host}:{port}";
        lock (gate) { if (!Enabled || clients.ContainsKey(key)) return; }
        var c = new Conn(this, client: true, key);
        lock (gate) clients[key] = c;
        _ = c.ConnectAndRun(host, port);
    }

    async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(ct); } catch { return; }
            var c = new Conn(this, client: false, "");
            lock (gate) servers.Add(c);
            _ = c.Run(tcp);
        }
    }

    internal string Mac(params string[] parts)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(officeCode));
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    internal JsonObject RowsReply(long after)
    {
        try
        {
            var (rows, more) = store.LanChanges(after, 400);
            var list = new JsonArray();
            foreach (var r in rows)
                list.Add(new JsonObject
                {
                    ["table"] = r.Table, ["uid"] = r.Uid, ["data"] = r.Data == null ? null : Convert.ToBase64String(r.Data),
                    ["updated_at"] = r.UpdatedAt, ["deleted"] = r.Deleted, ["seq"] = r.Seq,
                });
            return new JsonObject { ["type"] = "rows", ["rows"] = list, ["max"] = rows.Count > 0 ? rows[^1].Seq : after, ["more"] = more };
        }
        catch (Exception e) { return new JsonObject { ["type"] = "error", ["message"] = e.Message }; }
    }

    internal void Closed(Conn c)
    {
        lock (gate)
        {
            servers.Remove(c);
            if (c.Key.Length > 0 && clients.TryGetValue(c.Key, out var x) && x == c) clients.Remove(c.Key);
            if (c.PeerId != null && peers.TryGetValue(c.PeerId, out var p) && !clients.Values.Any(o => o.PeerId == c.PeerId && o.Authed)) p.Connected = false;
        }
        if (c.IsClient && c.SentAuth && !c.Authed && !c.Denied) { c.Denied = true; Denied(c.PeerName); }
        StatusChanged?.Invoke();
        // Try again shortly while the computer is still around.
        if (c.IsClient && !c.Denied && Enabled && c.Host != null && c.PeerId != DeviceId)
        {
            var (h, p) = (c.Host, c.RemotePort);
            _ = Task.Delay(5000).ContinueWith(_ => { if (Enabled) Connect(h, p); });
        }
    }

    internal void PeerConnected(string id, string name)
    {
        lock (gate)
        {
            if (!peers.TryGetValue(id, out var p)) peers[id] = p = new Peer { Id = id };
            p.Name = name; p.Connected = true;
        }
        Problems = new();
        StatusChanged?.Invoke();
    }

    internal void Denied(string name)
    {
        Problems = new() { $"The office code on “{name}” is different. Use the same code on every computer." };
        StatusChanged?.Invoke();
    }

    internal long Cursor(string peer) => long.TryParse(store.SyncValue($"lan_cursor_{peer}"), out var v) ? v : 0;

    /// <summary>Applies rows from another computer and moves our bookmark for it forward.</summary>
    internal (bool More, bool Ok) Apply(JsonNode msg, string peer)
    {
        var rows = new List<IncomingRow>();
        foreach (var r in msg["rows"]?.AsArray() ?? new JsonArray())
        {
            if (r?["table"]?.GetValue<string>() is not { } t || r["uid"]?.GetValue<string>() is not { } uid) continue;
            var data = r["data"] is JsonValue dv && dv.TryGetValue<string>(out var b64) ? Convert.FromBase64String(b64) : null;
            rows.Add(new IncomingRow(t, uid, data, Num(r["updated_at"]), r["deleted"]?.GetValue<bool>() ?? false, (long)Num(r["seq"])));
        }
        var max = (long)Num(msg["max"]);
        try
        {
            var cursor = max;
            if (rows.Count > 0)
            {
                var result = store.ApplyRemote(rows, fromLan: true);
                store.ReloadAfterSync();
                Problems = result.Problems;
                if (result.Applied > 0) LocalChanged();   // pass it on to the other computers
                // Something arrived before what it depends on: ask again once, then move on.
                if (result.Failed.Count > 0)
                {
                    var first = result.Failed.Min(f => f.Seq);
                    var key = $"lan_retry_{peer}";
                    if (store.SyncValue(key) != first.ToString(CultureInfo.InvariantCulture))
                    {
                        cursor = first - 1;
                        store.SetSyncValue(key, first.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            store.SetSyncValue($"lan_cursor_{peer}", cursor.ToString(CultureInfo.InvariantCulture));
            lock (gate) if (peers.TryGetValue(peer, out var p)) p.LastSync = DateTime.Now;
            StatusChanged?.Invoke();
            return (msg["more"]?.GetValue<bool>() ?? false, true);
        }
        catch (Exception e)
        {
            Problems = new() { e.Message };
            StatusChanged?.Invoke();
            return (false, false);
        }
    }

    static double Num(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : 0) : 0;

    /// <summary>One TCP connection – either side of the handshake.</summary>
    internal sealed class Conn
    {
        readonly LanSync owner;
        public readonly bool IsClient;
        public readonly string Key;
        public string? PeerId, Host;
        public string PeerName = "Computer";
        public int RemotePort;
        public bool Authed, SentAuth, Denied;
        readonly string nonce = Guid.NewGuid().ToString().ToUpperInvariant();
        string otherNonce = "";
        NetworkStream? stream;
        TcpClient? tcp;
        readonly SemaphoreSlim sendLock = new(1, 1);
        bool pulling, pullAgain, closed;

        public Conn(LanSync owner, bool client, string key) { this.owner = owner; IsClient = client; Key = key; }

        public async Task ConnectAndRun(string host, int port)
        {
            Host = host; RemotePort = port;
            var t = new TcpClient(AddressFamily.InterNetworkV6) { Client = { DualMode = true } };
            try
            {
                var addr = IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host))[0];
                if (addr.AddressFamily == AddressFamily.InterNetwork) addr = addr.MapToIPv6();
                await t.ConnectAsync(addr, port).WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch { t.Dispose(); Close(); return; }
            await Run(t);
        }

        public async Task Run(TcpClient t)
        {
            tcp = t;
            stream = t.GetStream();
            if (IsClient) Send(new JsonObject { ["type"] = "hello", ["nonce"] = nonce, ["device"] = owner.DeviceId, ["name"] = owner.ComputerName });
            try
            {
                var head = new byte[4];
                while (!closed)
                {
                    await stream.ReadExactlyAsync(head);
                    var n = BinaryPrimitives.ReadUInt32BigEndian(head);
                    if (n == 0 || n > 200_000_000) break;
                    var body = new byte[n];
                    await stream.ReadExactlyAsync(body);
                    if (JsonNode.Parse(body) is JsonObject msg) Handle(msg);
                }
            }
            catch { }
            Close();
        }

        public void Send(JsonObject obj)
        {
            if (closed || stream == null) return;
            var body = Encoding.UTF8.GetBytes(obj.ToJsonString());
            var frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
            body.CopyTo(frame, 4);
            _ = Task.Run(async () =>
            {
                await sendLock.WaitAsync();
                try { await stream.WriteAsync(frame); } catch { Close(); } finally { sendLock.Release(); }
            });
        }

        public void Pull()
        {
            if (!Authed || PeerId == null) return;
            lock (this) { if (pulling) { pullAgain = true; return; } pulling = true; }
            Send(new JsonObject { ["type"] = "pull", ["after"] = owner.Cursor(PeerId) });
        }

        void Handle(JsonObject m)
        {
            switch ((string?)m["type"])
            {
                // Server side
                case "hello" when !IsClient:
                    otherNonce = (string?)m["nonce"] ?? "";
                    Send(new JsonObject { ["type"] = "challenge", ["nonce"] = nonce, ["device"] = owner.DeviceId, ["name"] = owner.ComputerName });
                    break;
                case "auth" when !IsClient:
                    if ((string?)m["mac"] == owner.Mac("client", nonce, otherNonce))
                    {
                        Authed = true;
                        Send(new JsonObject { ["type"] = "ok", ["mac"] = owner.Mac("server", otherNonce, nonce) });
                    }
                    else
                    {
                        Send(new JsonObject { ["type"] = "denied" });
                        _ = Task.Delay(500).ContinueWith(_ => Close());   // let the reply arrive first
                    }
                    break;
                case "pull" when !IsClient:
                    if (!Authed) { Close(); return; }
                    Send(owner.RowsReply((long)Num(m["after"])));
                    break;

                // Client side
                case "challenge" when IsClient:
                    var id = (string?)m["device"] ?? "";
                    if (id == owner.DeviceId) { Close(); return; }   // that's us
                    PeerId = id;
                    PeerName = (string?)m["name"] ?? "Computer";
                    otherNonce = (string?)m["nonce"] ?? "";
                    SentAuth = true;
                    Send(new JsonObject { ["type"] = "auth", ["mac"] = owner.Mac("client", otherNonce, nonce) });
                    break;
                case "ok" when IsClient:
                    if ((string?)m["mac"] != owner.Mac("server", nonce, otherNonce) || PeerId == null) { Close(); return; }
                    Authed = true;
                    owner.PeerConnected(PeerId, PeerName);
                    Pull();
                    break;
                case "denied" when IsClient:
                    Denied = true;
                    owner.Denied(PeerName);
                    Close();
                    break;
                case "rows" when IsClient && PeerId != null:
                    lock (this) pulling = false;
                    var (more, ok) = owner.Apply(m, PeerId);
                    bool again;
                    lock (this) { again = pullAgain; pullAgain = false; }
                    if ((ok && more) || again) Pull();
                    break;
                case "changed" when IsClient:
                    Pull();
                    break;
            }
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { tcp?.Close(); } catch { }
            owner.Closed(this);
        }
    }
}
