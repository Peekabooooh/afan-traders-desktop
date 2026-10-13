using System.Globalization;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AfanTraders.Core;

/// <summary>
/// Offline-first sync with Supabase – port of the Mac SyncEngine. The local database is always
/// the working copy. When online: download newer rows, then upload local changes; a realtime
/// WebSocket triggers a sync the moment another computer saves something.
/// </summary>
public sealed class CloudSync : IDisposable
{
    public enum Phase { Off, Offline, Connecting, Syncing, Idle, Error }

    public sealed record Config(string Url = "", string AnonKey = "", string Email = "", bool Enabled = false);

    public Phase State { get; private set; } = Phase.Off;
    public string ErrorMessage { get; private set; } = "";
    public DateTime? LastSync { get; private set; }
    public int Pending { get; private set; }
    public bool Live { get; private set; }
    public bool Online { get; private set; } = true;
    public List<string> Problems { get; private set; } = new();
    public Config Settings { get; private set; } = new();
    public string DeviceId { get; }
    public event Action? StatusChanged;

    const string Prefix = "afan_";
    const int PageSize = 500;
    readonly Store store;
    readonly ISecretStore secrets;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    string? accessToken;
    DateTime tokenExpiry = DateTime.MinValue;
    ClientWebSocket? socket;
    CancellationTokenSource? socketCts;
    readonly Timer poll;
    CancellationTokenSource? debounce;
    int running;
    bool again;
    int refNo = 1;

    public CloudSync(Store store, ISecretStore secrets)
    {
        this.store = store;
        this.secrets = secrets;
        DeviceId = store.SyncValue("device_id") ?? Guid.NewGuid().ToString();
        store.SetSyncValue("device_id", DeviceId);
        if (store.SyncValue("config") is { } json)
            try { Settings = JsonSerializer.Deserialize<Config>(json) ?? new(); } catch { }
        if (double.TryParse(store.SyncValue("last_sync"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ls)) LastSync = Fmt.FromUnix(ls);
        Pending = store.PendingCount();
        Online = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        store.LocalChange += LocalChanged;
        poll = new Timer(_ => { try { SyncNow(); } catch { } }, null, 120_000, 120_000);
        if (Settings.Enabled) { State = Phase.Connecting; SyncNow(); }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        store.LocalChange -= LocalChanged;
        poll.Dispose();
        CloseSocket();
    }

    void Notify() => StatusChanged?.Invoke();

    public string StatusText => State switch
    {
        Phase.Off => "Not connected",
        Phase.Offline => Pending > 0 ? $"Offline · {Pending} change{(Pending == 1 ? "" : "s")} waiting" : "Offline · working locally",
        Phase.Connecting => "Connecting…",
        Phase.Syncing => "Syncing…",
        Phase.Idle => Pending > 0 ? $"{Pending} change{(Pending == 1 ? "" : "s")} waiting" : $"Synced {Ago(LastSync)}{(Live ? " · Live" : "")}",
        _ => $"Sync problem: {ErrorMessage}",
    };

    static string Ago(DateTime? t)
    {
        if (t is not { } d) return "just now";
        var s = (DateTime.Now - d).TotalSeconds;
        return s < 60 ? "just now" : s < 3600 ? $"{(int)(s / 60)} min ago" : s < 86400 ? $"{(int)(s / 3600)} h ago" : Fmt.Date(d);
    }

    // MARK: Connect / disconnect

    public async Task Connect(string url, string anonKey, string email, string password)
    {
        var u = url.T().TrimEnd('/');
        if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out _)) throw new AppError("The project URL doesn't look right.");
        Settings = new Config(u, anonKey.T(), email.T(), false);
        State = Phase.Connecting; Notify();
        try
        {
            await SignIn(password);
            await Request($"/rest/v1/{Prefix}items", query: new() { ["select"] = "uid", ["limit"] = "1" });
        }
        catch (Exception e) { State = Phase.Error; ErrorMessage = e.Message; Notify(); throw; }
        Settings = Settings with { Enabled = true };
        SaveConfig();
        if (store.SyncValue("settings_updated") != null) store.SetSyncValue("settings_dirty", "1");
        SyncNow();
    }

    public void Disconnect()
    {
        Settings = Settings with { Enabled = false };
        SaveConfig();
        secrets.Delete("refresh_token");
        accessToken = null;
        CloseSocket();
        State = Phase.Off;
        Notify();
    }

    void SaveConfig() => store.SetSyncValue("config", JsonSerializer.Serialize(Settings));

    void LocalChanged()
    {
        Pending = store.PendingCount();
        Notify();
        if (!Settings.Enabled) return;
        debounce?.Cancel();
        var c = debounce = new CancellationTokenSource();
        _ = Task.Delay(1500, c.Token).ContinueWith(t => { if (!t.IsCanceled) SyncNow(); });
    }

    void OnNetwork(object? s, NetworkAvailabilityEventArgs e)
    {
        Online = e.IsAvailable;
        if (!Settings.Enabled) return;
        if (Online) SyncNow();
        else { CloseSocket(); State = Phase.Offline; Notify(); }
    }

    public void SyncNow()
    {
        if (!Settings.Enabled) return;
        if (!Online) { State = Phase.Offline; Notify(); return; }
        if (Interlocked.Exchange(ref running, 1) == 1) { again = true; return; }
        State = Phase.Syncing; Notify();
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureToken();
                await Pull();
                await Push();
                LastSync = DateTime.Now;
                store.SetSyncValue("last_sync", Fmt.Unix(DateTime.Now).ToString("R", CultureInfo.InvariantCulture));
                State = Phase.Idle;
                if (socket == null) _ = ConnectRealtime();
            }
            catch (Exception e)
            {
                State = Online ? Phase.Error : Phase.Offline;
                ErrorMessage = e.Message;
            }
            Pending = store.PendingCount();
            Interlocked.Exchange(ref running, 0);
            Notify();
            if (again) { again = false; SyncNow(); }
        });
    }

    // MARK: Auth

    async Task SignIn(string password)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { email = Settings.Email, password });
        StoreTokens(await Request("/auth/v1/token", HttpMethod.Post, new() { ["grant_type"] = "password" }, body, auth: false));
    }

    async Task EnsureToken()
    {
        if (accessToken != null && tokenExpiry > DateTime.Now.AddSeconds(60)) return;
        var refresh = secrets.Get("refresh_token") ?? throw new AppError("Please sign in again in Settings → Cloud Sync.");
        try
        {
            StoreTokens(await Request("/auth/v1/token", HttpMethod.Post, new() { ["grant_type"] = "refresh_token" },
                                      JsonSerializer.SerializeToUtf8Bytes(new { refresh_token = refresh }), auth: false));
            SendAccessToken();
        }
        catch (AppError e) when (e.Message.Contains("Invalid Refresh Token") || e.Message.Contains("not found"))
        {
            throw new AppError("Session expired – please sign in again in Settings → Cloud Sync.");
        }
    }

    void StoreTokens(byte[] data)
    {
        var o = JsonNode.Parse(data) ?? throw new AppError("Unexpected sign-in response.");
        accessToken = (string?)o["access_token"] ?? throw new AppError("Unexpected sign-in response.");
        var refresh = (string?)o["refresh_token"] ?? throw new AppError("Unexpected sign-in response.");
        tokenExpiry = DateTime.Now.AddSeconds(o["expires_in"]?.GetValue<double>() ?? 3600);
        secrets.Set("refresh_token", refresh);
    }

    // MARK: Pull / push

    async Task Pull()
    {
        var rows = new List<IncomingRow>();
        var cursors = new Dictionary<string, string>();
        foreach (var table in Store.SyncTables)
        {
            var cursor = store.SyncValue($"cursor_{table}");
            while (true)
            {
                var q = new Dictionary<string, string> { ["select"] = "uid,data,updated_at,deleted,device,synced_at", ["order"] = "synced_at.asc", ["limit"] = PageSize.ToString() };
                if (cursor != null) q["synced_at"] = $"gt.{cursor}";
                var list = JsonNode.Parse(await Request($"/rest/v1/{Prefix}{table}", query: q))?.AsArray() ?? new JsonArray();
                foreach (var r in list)
                {
                    if ((string?)r?["uid"] is not { } uid) continue;
                    var data = r["data"] is JsonObject d ? Encoding.UTF8.GetBytes(d.ToJsonString()) : null;
                    rows.Add(new IncomingRow(table, uid, data, r["updated_at"]?.GetValue<double>() ?? 0, r["deleted"]?.GetValue<bool>() ?? false));
                    if ((string?)r["synced_at"] is { } s) cursor = s;
                }
                if (list.Count < PageSize) break;
            }
            if (cursor != null) cursors[table] = cursor;
        }
        if (rows.Count > 0)
        {
            var result = store.ApplyRemote(rows);
            Problems = result.Problems;
            store.ReloadAfterSync();
        }
        foreach (var (t, c) in cursors) store.SetSyncValue($"cursor_{t}", c);
    }

    async Task Push()
    {
        var rows = store.PendingChanges();
        if (rows.Count == 0) return;
        foreach (var table in Store.SyncTables)
        {
            var group = rows.Where(r => r.Table == table).ToList();
            for (int i = 0; i < group.Count; i += 100)
            {
                var batch = group.Skip(i).Take(100).ToList();
                var payload = new JsonArray();
                foreach (var r in batch)
                    payload.Add(new JsonObject
                    {
                        ["uid"] = r.Uid, ["data"] = r.Data == null ? null : JsonNode.Parse(r.Data), ["updated_at"] = r.UpdatedAt,
                        ["deleted"] = r.Deleted, ["device"] = DeviceId,
                    });
                await Request($"/rest/v1/{Prefix}{table}", HttpMethod.Post, new() { ["on_conflict"] = "uid" },
                              Encoding.UTF8.GetBytes(payload.ToJsonString()), headers: new() { ["Prefer"] = "resolution=merge-duplicates,return=minimal" });
                store.MarkPushed(batch);
            }
        }
    }

    // MARK: Realtime

    async Task ConnectRealtime()
    {
        if (!Settings.Enabled || !Online || socket != null || accessToken == null) return;
        var b = new UriBuilder(Settings.Url) { Scheme = Settings.Url.StartsWith("http:") ? "ws" : "wss", Path = "/realtime/v1/websocket", Query = $"apikey={Uri.EscapeDataString(Settings.AnonKey)}&vsn=1.0.0" };
        var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource();
        socket = ws; socketCts = cts;
        try
        {
            await ws.ConnectAsync(b.Uri, cts.Token);
            var changes = new JsonArray(Store.SyncTables.Select(t => (JsonNode)new JsonObject { ["event"] = "*", ["schema"] = "public", ["table"] = Prefix + t }).ToArray());
            await Send(new JsonObject
            {
                ["topic"] = "realtime:afan-sync", ["event"] = "phx_join", ["ref"] = NextRef(), ["join_ref"] = "1",
                ["payload"] = new JsonObject
                {
                    ["config"] = new JsonObject { ["postgres_changes"] = changes, ["broadcast"] = new JsonObject { ["self"] = false }, ["presence"] = new JsonObject { ["key"] = "" } },
                    ["access_token"] = accessToken,
                },
            });
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(25_000, cts.Token).ContinueWith(_ => { });
                    if (!cts.IsCancellationRequested) await Send(new JsonObject { ["topic"] = "phoenix", ["event"] = "heartbeat", ["payload"] = new JsonObject(), ["ref"] = NextRef() });
                }
            });
            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, cts.Token); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                Handle(ms.ToArray());
            }
        }
        catch { }
        if (socket == ws) { CloseSocket(); Notify(); }
        if (Settings.Enabled && Online)
        {
            await Task.Delay(5000);
            try { await EnsureToken(); } catch { }
            _ = ConnectRealtime();
        }
    }

    void Handle(byte[] text)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(text); } catch { return; }
        switch ((string?)msg?["event"])
        {
            case "phx_reply":
                if ((string?)msg["payload"]?["status"] == "ok" && (string?)msg["topic"] == "realtime:afan-sync") { Live = true; Notify(); }
                break;
            case "postgres_changes":
                if ((string?)msg["payload"]?["data"]?["record"]?["device"] == DeviceId) return;   // our own change
                debounce?.Cancel();
                var c = debounce = new CancellationTokenSource();
                _ = Task.Delay(600, c.Token).ContinueWith(t => { if (!t.IsCanceled) SyncNow(); });
                break;
            case "phx_error" or "phx_close":
                CloseSocket();
                break;
        }
    }

    void CloseSocket()
    {
        socketCts?.Cancel();
        try { socket?.Abort(); } catch { }
        socket = null;
        Live = false;
    }

    void SendAccessToken()
    {
        if (socket != null && accessToken != null)
            _ = Send(new JsonObject { ["topic"] = "realtime:afan-sync", ["event"] = "access_token", ["payload"] = new JsonObject { ["access_token"] = accessToken }, ["ref"] = NextRef() });
    }

    readonly SemaphoreSlim sendLock = new(1, 1);
    async Task Send(JsonObject obj)
    {
        if (socket is not { State: WebSocketState.Open } ws) return;
        await sendLock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(obj.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { sendLock.Release(); }
    }

    string NextRef() => Interlocked.Increment(ref refNo).ToString(CultureInfo.InvariantCulture);

    // MARK: HTTP

    async Task<byte[]> Request(string path, HttpMethod? method = null, Dictionary<string, string>? query = null, byte[]? body = null,
                               Dictionary<string, string>? headers = null, bool auth = true)
    {
        var qs = query == null ? "" : "?" + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        using var req = new HttpRequestMessage(method ?? HttpMethod.Get, Settings.Url + path + qs);
        req.Headers.Add("apikey", Settings.AnonKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth ? accessToken ?? Settings.AnonKey : Settings.AnonKey);
        foreach (var (k, v) in headers ?? new()) req.Headers.TryAddWithoutValidation(k, v);
        if (body != null) req.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        using var res = await http.SendAsync(req);
        var data = await res.Content.ReadAsByteArrayAsync();
        if (res.IsSuccessStatusCode) return data;
        string? msg = null;
        try { var o = JsonNode.Parse(data); msg = (string?)(o?["error_description"] ?? o?["msg"] ?? o?["message"] ?? o?["error"]); } catch { }
        var status = (int)res.StatusCode;
        if (status == 404 || (msg ?? "").Contains("does not exist") || (msg ?? "").Contains("schema cache"))
            throw new AppError("Cloud tables not found. Run the setup SQL in Supabase (Settings → Cloud Sync).");
        if (status == 401) accessToken = null;
        throw new AppError(msg ?? $"Server returned {status}.");
    }

    /// <summary>Same setup script as the Mac app.</summary>
    public const string SetupSql = """
        -- AFAN Traders cloud sync setup. Run once in Supabase → SQL Editor.
        create or replace function public.afan_touch() returns trigger language plpgsql as $$
        begin
          if tg_op = 'UPDATE' and new.updated_at < old.updated_at then
            return null;               -- an older edit never overwrites a newer one
          end if;
          new.synced_at := clock_timestamp();
          return new;
        end $$;

        do $$
        declare t text;
        begin
          foreach t in array array['afan_items','afan_purchases','afan_quotations','afan_challans','afan_invoices','afan_settings'] loop
            execute format('create table if not exists public.%I (
                uid text primary key,
                data jsonb,
                updated_at double precision not null,
                deleted boolean not null default false,
                device text,
                synced_at timestamptz not null default clock_timestamp())', t);
            execute format('create index if not exists %I on public.%I (synced_at)', t || '_synced_at', t);
            execute format('drop trigger if exists afan_touch on public.%I', t);
            execute format('create trigger afan_touch before insert or update on public.%I for each row execute function public.afan_touch()', t);
            execute format('alter table public.%I enable row level security', t);
            execute format('drop policy if exists afan_team on public.%I', t);
            execute format('create policy afan_team on public.%I for all to authenticated using (true) with check (true)', t);
            begin
              execute format('alter publication supabase_realtime add table public.%I', t);
            exception when duplicate_object then null;
            end;
          end loop;
        end $$;
        """;
}
