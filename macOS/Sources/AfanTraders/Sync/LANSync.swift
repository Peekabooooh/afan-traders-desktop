import Foundation
import Network
import CryptoKit

/// Office network sync: computers on the same Wi-Fi / office network find each
/// other (Bonjour) and exchange changes directly. No internet, no cloud bill.
///
/// Every computer runs a small server and connects to every other one it finds.
/// It asks each one "what changed since #N?" and applies the answer with the same
/// newer-edit-wins rules as cloud sync. When something changes locally, it tells
/// connected computers immediately, so updates appear within a second.
/// A shared office code (checked with HMAC; never sent over the network) keeps
/// other devices on the network out.
@MainActor
final class LANSync: ObservableObject {
    struct Peer: Identifiable {
        let id: String
        var name: String
        var connected = false
        var lastSync: Date?
        var problem: String?
    }

    @Published private(set) var enabled = false
    @Published private(set) var peers: [String: Peer] = [:]
    @Published private(set) var problems: [String] = []
    @Published private(set) var listening = false

    static let serviceType = "_afansync._tcp"
    let deviceId: String
    let computerName: String
    private unowned let store: AppStore
    private var officeCode = ""
    private var listener: NWListener?
    private var browser: NWBrowser?
    private var servers: [ObjectIdentifier: ServerSide] = [:]
    private var clients: [String: ClientSide] = [:]      // by endpoint description
    private var retry: Timer?

    init(store: AppStore) {
        self.store = store
        deviceId = store.sync.deviceId
        computerName = Host.current().localizedName ?? "Mac"
        // AFAN_OFFICE_CODE is for testing two copies of the app on one Mac.
        officeCode = ProcessInfo.processInfo.environment["AFAN_OFFICE_CODE"] ?? Keychain.get(account: "office_code") ?? ""
        if store.syncValue("lan_enabled") == "1" && !officeCode.isEmpty { start() }
    }

    var hasCode: Bool { !officeCode.isEmpty }

    var statusText: String {
        guard enabled else { return "Off" }
        let on = peers.values.filter(\.connected)
        if on.isEmpty { return "Looking for other computers…" }
        return "Connected to \(on.count) computer\(on.count == 1 ? "" : "s"): \(on.map(\.name).sorted().joined(separator: ", "))"
    }

    // MARK: - On / off

    func turnOn(code: String) {
        let c = code.trimmed
        guard c.count >= 4 else { return }
        officeCode = c
        Keychain.set(c, account: "office_code")
        try? store.setSyncValue("lan_enabled", "1")
        stop()
        start()
    }

    func turnOff() {
        try? store.setSyncValue("lan_enabled", "0")
        stop()
    }

    /// Self-test only: start with a code without saving it, no Bonjour.
    func startForTest(code: String) {
        officeCode = code
        start(advertise: false)
    }

    /// `advertise: false` is used by the self-test (direct connections only).
    func start(advertise: Bool = true, port: NWEndpoint.Port = .any) {
        guard !officeCode.isEmpty else { return }
        enabled = true
        do {
            let l = try NWListener(using: .tcp, on: port)
            if advertise { l.service = NWListener.Service(name: "AFAN-\(deviceId.prefix(12))", type: Self.serviceType) }
            l.newConnectionHandler = { [weak self] conn in
                MainActor.assumeIsolated { self?.accept(conn) }
            }
            l.stateUpdateHandler = { [weak self] state in
                MainActor.assumeIsolated {
                    switch state {
                    case .ready: self?.listening = true
                    case .failed(let e): self?.listening = false; self?.problems = ["Can't listen for other computers: \(e)"]
                    default: break
                    }
                }
            }
            l.start(queue: .main)
            listener = l
        } catch {
            problems = ["Can't start office sync: \(error.localizedDescription)"]
        }
        if advertise { browse() }
        retry = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.pullAll() }
        }
    }

    private func stop() {
        enabled = false
        listening = false
        retry?.invalidate()
        browser?.cancel(); browser = nil
        listener?.cancel(); listener = nil
        for c in clients.values { c.wire.close() }
        for s in servers.values { s.wire.close() }
        clients = [:]; servers = [:]
        for k in peers.keys { peers[k]?.connected = false }
    }

    var port: UInt16? { listener?.port?.rawValue }

    /// Called by the store after every local change: tell connected computers.
    func localChanged() {
        for s in servers.values where s.authed { s.wire.send(["type": "changed"]) }
    }

    private func pullAll() {
        for c in clients.values where c.authed { c.pull() }
    }

    // MARK: - Discovery

    private func browse() {
        let b = NWBrowser(for: .bonjour(type: Self.serviceType, domain: nil), using: .tcp)
        b.browseResultsChangedHandler = { [weak self] results, _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                for r in results {
                    if case .service(let name, _, _, _) = r.endpoint, name.contains(self.deviceId.prefix(12)) { continue }  // ourselves
                    self.connect(to: r.endpoint)
                }
            }
        }
        b.start(queue: .main)
        browser = b
    }

    /// Opens a connection to another computer (also used directly by the self-test).
    func connect(to endpoint: NWEndpoint) {
        let key = "\(endpoint)"
        guard enabled, clients[key] == nil else { return }
        let client = ClientSide(owner: self, wire: Wire(NWConnection(to: endpoint, using: .tcp)), key: key)
        clients[key] = client
        client.start()
    }

    fileprivate func clientClosed(_ c: ClientSide) {
        clients[c.key] = nil
        if c.sentAuth && !c.authed && !c.denied {      // hung up during the code check
            c.denied = true
            peerDenied(name: c.peerName, key: c.key)
        }
        if let id = c.peerId { peers[id]?.connected = false }
        // Try again shortly; Bonjour keeps the computer in the list while it is on.
        let endpoint = c.wire.conn.endpoint
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.enabled, c.peerId != self.deviceId, !c.denied else { return }
                self.connect(to: endpoint)
            }
        }
    }

    // MARK: - Server side

    private func accept(_ conn: NWConnection) {
        let s = ServerSide(owner: self, wire: Wire(conn))
        servers[ObjectIdentifier(s)] = s
        s.start()
    }

    fileprivate func serverClosed(_ s: ServerSide) { servers[ObjectIdentifier(s)] = nil }

    fileprivate func mac(_ parts: String...) -> String {
        let key = SymmetricKey(data: Data(officeCode.utf8))
        let tag = HMAC<SHA256>.authenticationCode(for: Data(parts.joined(separator: "|").utf8), using: key)
        return Data(tag).base64EncodedString()
    }

    fileprivate func rowsReply(after: Int64) -> [String: Any] {
        do {
            let (rows, more) = try store.lanChanges(after: after, limit: 400)
            let list: [[String: Any]] = rows.map { r in
                ["table": r.table, "uid": r.uid, "data": r.data?.base64EncodedString() ?? NSNull(),
                 "updated_at": r.updatedAt, "deleted": r.deleted, "seq": r.seq]
            }
            return ["type": "rows", "rows": list, "max": rows.last?.seq ?? after, "more": more]
        } catch {
            return ["type": "error", "message": error.localizedDescription]
        }
    }

    // MARK: - Client side

    fileprivate func peerConnected(_ id: String, name: String) {
        var p = peers[id] ?? Peer(id: id, name: name)
        p.name = name
        p.connected = true
        p.problem = nil
        peers[id] = p
    }

    fileprivate func peerDenied(name: String, key: String) {
        problems = ["The office code on “\(name)” is different. Use the same code on every computer."]
    }

    /// Applies rows from another computer and moves our bookmark for it forward.
    fileprivate func apply(_ msg: [String: Any], from peer: String) -> (more: Bool, ok: Bool) {
        let list = (msg["rows"] as? [[String: Any]]) ?? []
        let rows: [IncomingRow] = list.compactMap { r in
            guard let t = r["table"] as? String, let uid = r["uid"] as? String else { return nil }
            return IncomingRow(table: t, uid: uid, data: (r["data"] as? String).flatMap { Data(base64Encoded: $0) },
                               updatedAt: (r["updated_at"] as? Double) ?? 0, deleted: (r["deleted"] as? Bool) ?? false,
                               seq: (r["seq"] as? NSNumber)?.int64Value ?? 0)
        }
        let max = (msg["max"] as? NSNumber)?.int64Value ?? 0
        do {
            var cursor = max
            if !rows.isEmpty {
                let result = try store.applyRemote(rows, fromLAN: true)
                store.reloadAfterSync()
                problems = result.problems
                if result.applied > 0 { localChanged() }   // pass it on to the other computers
                // If something arrived before what it depends on, ask for it again next time
                // (once – if it fails twice we move on so sync never gets stuck).
                if let firstFailed = result.failed.map(\.seq).min() {
                    let key = "lan_retry_\(peer)"
                    if store.syncValue(key) != String(firstFailed) {
                        cursor = firstFailed - 1
                        try store.setSyncValue(key, String(firstFailed))
                    }
                }
            }
            try store.setSyncValue("lan_cursor_\(peer)", String(cursor))
            peers[peer]?.lastSync = Date()
            return ((msg["more"] as? Bool) ?? false, true)
        } catch {
            problems = [error.localizedDescription]
            return (false, false)
        }
    }

    fileprivate func cursor(for peer: String) -> Int64 {
        Int64(store.syncValue("lan_cursor_\(peer)") ?? "") ?? 0
    }
}

// MARK: - Connections

/// Length-prefixed JSON messages over one TCP connection (callbacks on the main queue).
final class Wire {
    let conn: NWConnection
    var onReady: (() -> Void)?
    var onMessage: (([String: Any]) -> Void)?
    var onClose: (() -> Void)?
    private var closed = false

    init(_ conn: NWConnection) { self.conn = conn }

    func start() {
        conn.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready: self?.onReady?(); self?.read()
            case .failed, .cancelled: self?.close()
            default: break
            }
        }
        conn.start(queue: .main)
    }

    func send(_ obj: [String: Any]) {
        guard !closed, let body = try? JSONSerialization.data(withJSONObject: obj) else { return }
        var len = UInt32(body.count).bigEndian
        var frame = Data(bytes: &len, count: 4)
        frame.append(body)
        conn.send(content: frame, completion: .contentProcessed { _ in })
    }

    private func read() {
        conn.receive(minimumIncompleteLength: 4, maximumLength: 4) { [weak self] head, _, _, error in
            guard let self else { return }
            guard error == nil, let head, head.count == 4 else { self.close(); return }
            let n = Int(head.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }.bigEndian)
            guard n > 0, n < 200_000_000 else { self.close(); return }
            self.conn.receive(minimumIncompleteLength: n, maximumLength: n) { body, _, _, error in
                guard error == nil, let body, body.count == n,
                      let obj = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] else { self.close(); return }
                self.onMessage?(obj)
                self.read()
            }
        }
    }

    func close() {
        guard !closed else { return }
        closed = true
        conn.cancel()
        onClose?()
    }
}

/// Another computer connected to us: prove the office code, then answer "pull" requests.
@MainActor
private final class ServerSide {
    unowned let owner: LANSync
    let wire: Wire
    var authed = false
    private let nonce = UUID().uuidString
    private var clientNonce = ""

    init(owner: LANSync, wire: Wire) { self.owner = owner; self.wire = wire }

    func start() {
        wire.onMessage = { [weak self] m in MainActor.assumeIsolated { self?.handle(m) } }
        wire.onClose = { [weak self] in MainActor.assumeIsolated { if let self { self.owner.serverClosed(self) } } }
        wire.start()
    }

    private func handle(_ m: [String: Any]) {
        switch m["type"] as? String {
        case "hello":
            clientNonce = (m["nonce"] as? String) ?? ""
            wire.send(["type": "challenge", "nonce": nonce, "device": owner.deviceId, "name": owner.computerName])
        case "auth":
            if (m["mac"] as? String) == owner.mac("client", nonce, clientNonce) {
                authed = true
                wire.send(["type": "ok", "mac": owner.mac("server", clientNonce, nonce)])
            } else {
                wire.send(["type": "denied"])
                let w = wire
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { w.close() }   // let the reply arrive first
            }
        case "pull":
            guard authed else { wire.close(); return }
            wire.send(owner.rowsReply(after: (m["after"] as? NSNumber)?.int64Value ?? 0))
        default:
            break
        }
    }
}

/// We connected to another computer: prove the office code, then pull its changes.
@MainActor
private final class ClientSide {
    unowned let owner: LANSync
    let wire: Wire
    let key: String
    var peerId: String?
    var peerName = ""
    var authed = false
    var denied = false
    var sentAuth = false
    private let nonce = UUID().uuidString
    private var serverNonce = ""
    private var pulling = false
    private var pullAgain = false

    init(owner: LANSync, wire: Wire, key: String) { self.owner = owner; self.wire = wire; self.key = key }

    func start() {
        wire.onReady = { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.wire.send(["type": "hello", "nonce": self.nonce, "device": self.owner.deviceId, "name": self.owner.computerName])
            }
        }
        wire.onMessage = { [weak self] m in MainActor.assumeIsolated { self?.handle(m) } }
        wire.onClose = { [weak self] in MainActor.assumeIsolated { if let self { self.owner.clientClosed(self) } } }
        wire.start()
    }

    func pull() {
        guard authed, let peerId else { return }
        if pulling { pullAgain = true; return }
        pulling = true
        wire.send(["type": "pull", "after": owner.cursor(for: peerId)])
    }

    private func handle(_ m: [String: Any]) {
        switch m["type"] as? String {
        case "challenge":
            let id = (m["device"] as? String) ?? ""
            guard id != owner.deviceId else { wire.close(); return }      // that's us
            peerId = id
            peerName = (m["name"] as? String) ?? "Computer"
            serverNonce = (m["nonce"] as? String) ?? ""
            sentAuth = true
            wire.send(["type": "auth", "mac": owner.mac("client", serverNonce, nonce)])
        case "ok":
            guard (m["mac"] as? String) == owner.mac("server", nonce, serverNonce), let peerId else { wire.close(); return }
            authed = true
            owner.peerConnected(peerId, name: peerName)
            pull()
        case "denied":
            denied = true
            owner.peerDenied(name: peerName, key: key)
            wire.close()
        case "rows":
            guard let peerId else { return }
            pulling = false
            let r = owner.apply(m, from: peerId)
            if r.ok && r.more || pullAgain { pullAgain = false; pull() }
        case "changed":
            pull()
        default:
            break
        }
    }
}
