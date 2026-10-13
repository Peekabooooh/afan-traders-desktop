import Foundation
import SQLite3

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

struct AppError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

/// One result row. Values are Int64, Double, String or Data.
struct Row {
    let columns: [String: Any]

    func string(_ key: String) -> String { columns[key] as? String ?? "" }

    func double(_ key: String) -> Double {
        if let d = columns[key] as? Double { return d }
        if let i = columns[key] as? Int64 { return Double(i) }
        return 0
    }

    func int(_ key: String) -> Int64 {
        if let i = columns[key] as? Int64 { return i }
        if let d = columns[key] as? Double { return Int64(d) }
        return 0
    }

    func optInt(_ key: String) -> Int64? {
        columns[key] == nil ? nil : int(key)
    }

    func data(_ key: String) -> Data? { columns[key] as? Data }

    func date(_ key: String) -> Date { Date(timeIntervalSince1970: double(key)) }

    func optDate(_ key: String) -> Date? {
        columns[key] == nil ? nil : date(key)
    }
}

/// Minimal wrapper over the system SQLite library.
final class SQLiteDB {
    private var db: OpaquePointer?

    init(path: String) throws {
        if sqlite3_open(path, &db) != SQLITE_OK {
            throw AppError("Cannot open database at \(path)")
        }
        try exec("PRAGMA foreign_keys = ON;")
        try exec("PRAGMA journal_mode = WAL;")
    }

    deinit { sqlite3_close(db) }

    private var lastError: String { String(cString: sqlite3_errmsg(db)) }

    func exec(_ sql: String) throws {
        var err: UnsafeMutablePointer<CChar>?
        if sqlite3_exec(db, sql, nil, nil, &err) != SQLITE_OK {
            let message = err.map { String(cString: $0) } ?? lastError
            sqlite3_free(err)
            throw AppError(message)
        }
    }

    private func prepare(_ sql: String, _ params: [Any?]) throws -> OpaquePointer {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let s = stmt else {
            throw AppError(lastError)
        }
        for (i, param) in params.enumerated() {
            let idx = Int32(i + 1)
            guard let value = param else {
                sqlite3_bind_null(s, idx)
                continue
            }
            switch value {
            case let v as Int: sqlite3_bind_int64(s, idx, Int64(v))
            case let v as Int64: sqlite3_bind_int64(s, idx, v)
            case let v as Double: sqlite3_bind_double(s, idx, v)
            case let v as Bool: sqlite3_bind_int64(s, idx, v ? 1 : 0)
            case let v as String: sqlite3_bind_text(s, idx, v, -1, SQLITE_TRANSIENT)
            case let v as Date: sqlite3_bind_double(s, idx, v.timeIntervalSince1970)
            case let v as Data:
                if v.isEmpty {
                    sqlite3_bind_zeroblob(s, idx, 0)
                } else {
                    _ = v.withUnsafeBytes { buf in
                        sqlite3_bind_blob(s, idx, buf.baseAddress, Int32(v.count), SQLITE_TRANSIENT)
                    }
                }
            default: sqlite3_bind_null(s, idx)
            }
        }
        return s
    }

    /// Runs a statement and returns the last inserted row id.
    @discardableResult
    func run(_ sql: String, _ params: [Any?] = []) throws -> Int64 {
        let s = try prepare(sql, params)
        defer { sqlite3_finalize(s) }
        let rc = sqlite3_step(s)
        guard rc == SQLITE_DONE || rc == SQLITE_ROW else { throw AppError(lastError) }
        return sqlite3_last_insert_rowid(db)
    }

    func query(_ sql: String, _ params: [Any?] = []) throws -> [Row] {
        let s = try prepare(sql, params)
        defer { sqlite3_finalize(s) }
        var rows: [Row] = []
        while true {
            let rc = sqlite3_step(s)
            if rc == SQLITE_DONE { break }
            guard rc == SQLITE_ROW else { throw AppError(lastError) }
            var dict: [String: Any] = [:]
            for c in 0..<sqlite3_column_count(s) {
                let name = String(cString: sqlite3_column_name(s, c))
                switch sqlite3_column_type(s, c) {
                case SQLITE_INTEGER:
                    dict[name] = sqlite3_column_int64(s, c)
                case SQLITE_FLOAT:
                    dict[name] = sqlite3_column_double(s, c)
                case SQLITE3_TEXT:
                    if let t = sqlite3_column_text(s, c) { dict[name] = String(cString: t) }
                case SQLITE_BLOB:
                    let n = Int(sqlite3_column_bytes(s, c))
                    if let p = sqlite3_column_blob(s, c), n > 0 {
                        dict[name] = Data(bytes: p, count: n)
                    }
                default:
                    break
                }
            }
            rows.append(Row(columns: dict))
        }
        return rows
    }

    func transaction(_ body: () throws -> Void) throws {
        try exec("BEGIN IMMEDIATE;")
        do {
            try body()
            try exec("COMMIT;")
        } catch {
            try? exec("ROLLBACK;")
            throw error
        }
    }
}
