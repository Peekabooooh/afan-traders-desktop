import Foundation
import Network
import Security

/// Offline-first sync with Supabase.
///
/// The local SQLite database is always the working copy, so the app works with or
/// without internet. Each change is marked "dirty"; when online, the engine
/// downloads newer rows from Supabase, then uploads local changes. A realtime
/// WebSocket tells it the moment another computer saves something.
@MainActor
final class SyncEngine: ObservableObject {
    enum Phase: Equatable {
        case off, offline, connecting, syncing, idle
        case error(String)
    }

    struct Config: Codable, Equatable {
        var url = ""
        var anonKey = ""
        var email = ""
        var enabled = false
    }

    @Published private(set) var phase: Phase = .off
    @Published private(set) var lastSync: Date?
    @Published private(set) var pending = 0
    @Published private(set) var live = false
    @Published private(set) var online = true
    @Published private(set) var problems: [String] = []
    @Published private(set) var config = Config()

    let deviceId: String
    private unowned let store: AppStore
    private var accessToken: String?
    private var tokenExpiry = Date.distantPast
    private let monitor = NWPathMonitor()
    private var socket: URLSessionWebSocketTask?
    private var heartbeat: Timer?
    private var poll: Timer?
    private var debounce: Task<Void, Never>?
    private var reconnect: Task<Void, Never>?
    private var running = false
    private var again = false
    private var ref = 1

    private static let remotePrefix = "afan_"
    private static let pageSize = 500

    init(store: AppStore) {
        self.store = store
        if let id = store.syncValue("device_id") {
            deviceId = id
        } else {
            deviceId = UUID().uuidString.lowercased()
            try? store.setSyncValue("device_id", deviceId)
        }
        if let json = store.syncValue("config"), let data = json.data(using: .utf8),
           let c = try? JSONDecoder().decode(Config.self, from: data) {
            config = c
        }
        if let t = store.syncValue("last_sync"), let v = Double(t) { lastSync = Date(timeIntervalSince1970: v) }
        pending = store.pendingCount()

        monitor.pathUpdateHandler = { [weak self] path in
            let up = path.status == .satisfied
            Task { @MainActor in self?.networkChanged(up) }
        }
        monitor.start(queue: DispatchQueue(label: "afan.network"))
        poll = Timer.scheduledTimer(withTimeInterval: 120, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.syncNow() }
        }
        if config.enabled { phase = .connecting }
    }

    // MARK: - Public

    var statusText: String {
        switch phase {
        case .off: return "Not connected"
        case .offline: return pending > 0 ? "Offline · \(pending) change\(pending == 1 ? "" : "s") waiting" : "Offline · working locally"
        case .connecting: return "Connecting…"
        case .syncing: return "Syncing…"
        case .idle:
            let when = lastSync.map { $0.formatted(.relative(presentation: .named)) } ?? "just now"
            return pending > 0 ? "\(pending) change\(pending == 1 ? "" : "s") waiting" : "Synced \(when)\(live ? " · Live" : "")"
        case .error(let m): return "Sync problem: \(m)"
        }
    }

    /// Signs in and turns sync on.
    func connect(url: String, anonKey: String, email: String, password: String) async throws {
        var u = url.trimmed
        while u.hasSuffix("/") { u.removeLast() }
        if !u.lowercased().hasPrefix("http") { u = "https://" + u }
        guard URL(string: u)?.host != nil else { throw AppError("The project URL doesn't look right.") }
        config = Config(url: u, anonKey: anonKey.trimmed, email: email.trimmed, enabled: false)
        phase = .connecting
        do {
            try await signIn(password: password)
            // Make sure the cloud tables exist before turning sync on.
            _ = try await request("/rest/v1/\(Self.remotePrefix)items", query: [URLQueryItem(name: "select", value: "uid"),
                                                                               URLQueryItem(name: "limit", value: "1")])
        } catch {
            phase = .error(error.localizedDescription)
            throw error
        }
        config.enabled = true
        saveConfig()
        if store.syncValue("settings_updated") != nil { try? store.setSyncValue("settings_dirty", "1") }
        syncNow()
        connectRealtime()
    }

    func disconnect() {
        config.enabled = false
        saveConfig()
        Keychain.delete(account: "refresh_token")
        accessToken = nil
        closeSocket()
        phase = .off
    }

    /// Called by the store after every local change.
    func localChanged() {
        pending = store.pendingCount()
        guard config.enabled else { return }
        debounce?.cancel()
        debounce = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled else { return }
            self?.syncNow()
        }
    }

    func syncNow() {
        guard config.enabled else { return }
        guard online else { phase = .offline; return }
        if running { again = true; return }
        running = true
        phase = .syncing
        Task {
            do {
                try await ensureToken()
                try await pull()
                try await push()
                lastSync = Date()
                try? store.setSyncValue("last_sync", String(Date().timeIntervalSince1970))
                phase = .idle
                if socket == nil { connectRealtime() }
            } catch {
                phase = online ? .error(error.localizedDescription) : .offline
            }
            pending = store.pendingCount()
            running = false
            if again { again = false; syncNow() }
        }
    }

    // MARK: - Network state

    private func networkChanged(_ up: Bool) {
        online = up
        guard config.enabled else { return }
        if up {
            syncNow()
            connectRealtime()
        } else {
            closeSocket()
            phase = .offline
        }
    }

    // MARK: - Auth

    private func signIn(password: String) async throws {
        let body = try JSONSerialization.data(withJSONObject: ["email": config.email, "password": password])
        let data = try await request("/auth/v1/token", method: "POST", query: [URLQueryItem(name: "grant_type", value: "password")],
                                     body: body, auth: false)
        try storeTokens(data)
    }

    private func ensureToken() async throws {
        if accessToken != nil && tokenExpiry > Date().addingTimeInterval(60) { return }
        guard let refresh = Keychain.get(account: "refresh_token") else {
            throw AppError("Please sign in again in Settings → Cloud Sync.")
        }
        let body = try JSONSerialization.data(withJSONObject: ["refresh_token": refresh])
        do {
            let data = try await request("/auth/v1/token", method: "POST",
                                         query: [URLQueryItem(name: "grant_type", value: "refresh_token")], body: body, auth: false)
            try storeTokens(data)
            sendAccessToken()
        } catch let e as AppError where e.message.contains("Invalid Refresh Token") || e.message.contains("not found") {
            throw AppError("Session expired – please sign in again in Settings → Cloud Sync.")
        }
    }

    private func storeTokens(_ data: Data) throws {
        guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let access = obj["access_token"] as? String, let refresh = obj["refresh_token"] as? String else {
            throw AppError("Unexpected sign-in response.")
        }
        accessToken = access
        tokenExpiry = Date().addingTimeInterval((obj["expires_in"] as? Double) ?? 3600)
        Keychain.set(refresh, account: "refresh_token")
    }

    // MARK: - Pull / push

    private func pull() async throws {
        var rows: [IncomingRow] = []
        var cursors: [String: String] = [:]
        for table in AppStore.syncTables {
            var cursor = store.syncValue("cursor_\(table)")
            while true {
                var q = [URLQueryItem(name: "select", value: "uid,data,updated_at,deleted,device,synced_at"),
                         URLQueryItem(name: "order", value: "synced_at.asc"),
                         URLQueryItem(name: "limit", value: String(Self.pageSize))]
                if let cursor { q.append(URLQueryItem(name: "synced_at", value: "gt.\(cursor)")) }
                let data = try await request("/rest/v1/\(Self.remotePrefix)\(table)", query: q)
                guard let list = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { break }
                for r in list {
                    guard let uid = r["uid"] as? String else { continue }
                    let payload = (r["data"] as? [String: Any]).flatMap { try? JSONSerialization.data(withJSONObject: $0) }
                    rows.append(IncomingRow(table: table, uid: uid, data: payload,
                                            updatedAt: (r["updated_at"] as? Double) ?? 0,
                                            deleted: (r["deleted"] as? Bool) ?? false))
                    if let s = r["synced_at"] as? String { cursor = s }
                }
                if list.count < Self.pageSize { break }
            }
            if let cursor { cursors[table] = cursor }
        }
        if !rows.isEmpty {
            let result = try store.applyRemote(rows)
            problems = result.problems
            store.reloadAfterSync()
            if result.applied > 0 { store.lan?.localChanged() }   // let office computers know
        }
        for (t, c) in cursors { try store.setSyncValue("cursor_\(t)", c) }
    }

    private func push() async throws {
        let rows = try store.pendingChanges()
        guard !rows.isEmpty else { return }
        for table in AppStore.syncTables {
            let group = rows.filter { $0.table == table }
            for start in stride(from: 0, to: group.count, by: 100) {
                let batch = Array(group[start..<min(start + 100, group.count)])
                let payload: [[String: Any]] = batch.map { r in
                    [
                        "uid": r.uid,
                        "data": r.data.flatMap { try? JSONSerialization.jsonObject(with: $0) } ?? NSNull(),
                        "updated_at": r.updatedAt,
                        "deleted": r.deleted,
                        "device": deviceId,
                    ]
                }
                let body = try JSONSerialization.data(withJSONObject: payload)
                _ = try await request("/rest/v1/\(Self.remotePrefix)\(table)", method: "POST",
                                      query: [URLQueryItem(name: "on_conflict", value: "uid")], body: body,
                                      headers: ["Prefer": "resolution=merge-duplicates,return=minimal"])
                try store.markPushed(batch)
            }
        }
    }

    // MARK: - Realtime

    private func connectRealtime() {
        guard config.enabled, online, socket == nil, let token = accessToken,
              var comps = URLComponents(string: config.url) else { return }
        comps.scheme = comps.scheme == "http" ? "ws" : "wss"
        comps.path = "/realtime/v1/websocket"
        comps.queryItems = [URLQueryItem(name: "apikey", value: config.anonKey), URLQueryItem(name: "vsn", value: "1.0.0")]
        guard let url = comps.url else { return }
        let task = URLSession.shared.webSocketTask(with: url)
        socket = task
        task.resume()
        let changes = AppStore.syncTables.map { ["event": "*", "schema": "public", "table": Self.remotePrefix + $0] }
        send(["topic": "realtime:afan-sync", "event": "phx_join", "ref": nextRef(), "join_ref": "1",
              "payload": ["config": ["postgres_changes": changes, "broadcast": ["self": false], "presence": ["key": ""]],
                          "access_token": token]])
        receive(task)
        heartbeat?.invalidate()
        heartbeat = Timer.scheduledTimer(withTimeInterval: 25, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.send(["topic": "phoenix", "event": "heartbeat", "payload": [:], "ref": self.nextRef()])
            }
        }
    }

    private func receive(_ task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            Task { @MainActor in
                guard let self, self.socket === task else { return }
                switch result {
                case .failure:
                    self.socketDropped()
                case .success(let message):
                    if case .string(let text) = message { self.handle(text) }
                    self.receive(task)
                }
            }
        }
    }

    private func handle(_ text: String) {
        guard let data = text.data(using: .utf8),
              let msg = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let event = msg["event"] as? String else { return }
        switch event {
        case "phx_reply":
            if let payload = msg["payload"] as? [String: Any], (payload["status"] as? String) == "ok",
               (msg["topic"] as? String) == "realtime:afan-sync" {
                live = true
            }
        case "postgres_changes":
            let record = ((msg["payload"] as? [String: Any])?["data"] as? [String: Any])?["record"] as? [String: Any]
            if (record?["device"] as? String) == deviceId { return }   // our own change coming back
            debounce?.cancel()
            debounce = Task { [weak self] in
                try? await Task.sleep(nanoseconds: 600_000_000)
                guard !Task.isCancelled else { return }
                self?.syncNow()
            }
        case "phx_error", "phx_close":
            socketDropped()
        default:
            break
        }
    }

    private func socketDropped() {
        closeSocket()
        guard config.enabled, online else { return }
        reconnect?.cancel()
        reconnect = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            guard !Task.isCancelled, let self else { return }
            try? await self.ensureToken()
            self.connectRealtime()
        }
    }

    private func closeSocket() {
        heartbeat?.invalidate()
        heartbeat = nil
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        live = false
    }

    private func sendAccessToken() {
        guard socket != nil, let token = accessToken else { return }
        send(["topic": "realtime:afan-sync", "event": "access_token", "payload": ["access_token": token], "ref": nextRef()])
    }

    private func send(_ obj: [String: Any]) {
        guard let socket, let data = try? JSONSerialization.data(withJSONObject: obj),
              let text = String(data: data, encoding: .utf8) else { return }
        socket.send(.string(text)) { _ in }
    }

    private func nextRef() -> String { ref += 1; return String(ref) }

    // MARK: - HTTP

    private func saveConfig() {
        if let data = try? JSONEncoder().encode(config) { try? store.setSyncValue("config", String(data: data, encoding: .utf8)) }
    }

    private func request(_ path: String, method: String = "GET", query: [URLQueryItem] = [], body: Data? = nil,
                         headers: [String: String] = [:], auth: Bool = true) async throws -> Data {
        guard var comps = URLComponents(string: config.url + path) else { throw AppError("Bad project URL.") }
        if !query.isEmpty { comps.queryItems = query }
        // "+" in timestamps must be encoded, or the server reads it as a space.
        comps.percentEncodedQuery = comps.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        guard let url = comps.url else { throw AppError("Bad project URL.") }
        var req = URLRequest(url: url, timeoutInterval: 30)
        req.httpMethod = method
        req.httpBody = body
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue(config.anonKey, forHTTPHeaderField: "apikey")
        req.setValue("Bearer \(auth ? (accessToken ?? config.anonKey) : config.anonKey)", forHTTPHeaderField: "Authorization")
        for (k, v) in headers { req.setValue(v, forHTTPHeaderField: k) }
        let (data, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            let msg = (obj?["error_description"] ?? obj?["msg"] ?? obj?["message"] ?? obj?["error"]) as? String
            if status == 404 || (msg ?? "").contains("does not exist") || (msg ?? "").contains("schema cache") {
                throw AppError("Cloud tables not found. Run the setup SQL in Supabase (Settings → Cloud Sync).")
            }
            if status == 401 { accessToken = nil }
            throw AppError(msg ?? "Server returned \(status).")
        }
        return data
    }
}

/// Stores the sign-in token in the macOS Keychain.
enum Keychain {
    private static let service = "com.afantraders.sync"

    static func set(_ value: String, account: String) {
        delete(account: account)
        SecItemAdd([kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: account,
                    kSecValueData: Data(value.utf8)] as CFDictionary, nil)
    }

    static func get(account: String) -> String? {
        var out: AnyObject?
        let status = SecItemCopyMatching([kSecClass: kSecClassGenericPassword, kSecAttrService: service,
                                          kSecAttrAccount: account, kSecReturnData: true] as CFDictionary, &out)
        guard status == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func delete(account: String) {
        SecItemDelete([kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: account] as CFDictionary)
    }
}

enum SupabaseSetup {
    /// Run once in Supabase → SQL Editor. Creates the sync tables, keeps the newest
    /// version of every record, limits access to signed-in users and turns on realtime.
    static let sql = """
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
    """
}
