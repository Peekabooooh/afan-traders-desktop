import Foundation

/// Database layout. The Windows app uses the exact same tables, so a database
/// file can be copied between the two apps.
enum Schema {
    static let version: Int64 = 4

    static func migrate(_ db: SQLiteDB) throws {
        let current = try db.query("PRAGMA user_version").first?.int("user_version") ?? 0
        guard current < version else { return }
        if current < 1 { try createV1(db) }
        if current < 2 {
            // Cost of each item at the time it was invoiced, for accurate profit reports.
            try db.exec("ALTER TABLE doc_lines ADD COLUMN cost REAL NOT NULL DEFAULT 0;")
            try db.exec("""
                UPDATE doc_lines SET cost = COALESCE((SELECT cost_price FROM items WHERE items.id = doc_lines.item_id), 0)
                WHERE doc_type = 'I' AND item_id IS NOT NULL;
                """)
        }
        if current < 3 { try addSync(db) }
        if current < 4 { try addOfficeSync(db) }
        try db.exec("PRAGMA user_version = \(version);")
    }

    static let syncedTables = ["items", "purchases", "quotations", "challans", "invoices"]

    /// v3: cloud sync. Every synced row gets a global id (uid), a change time and a
    /// "needs upload" flag. Stock becomes derived: stock_adjust + purchased − delivered,
    /// so stock changes made on two computers can never overwrite each other.
    private static func addSync(_ db: SQLiteDB) throws {
        for t in syncedTables {
            try db.exec("""
                ALTER TABLE \(t) ADD COLUMN uid TEXT;
                ALTER TABLE \(t) ADD COLUMN updated_at REAL NOT NULL DEFAULT 0;
                ALTER TABLE \(t) ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;
                """)
            for r in try db.query("SELECT id FROM \(t)") {
                try db.run("UPDATE \(t) SET uid = ?, updated_at = ? WHERE id = ?",
                           [UUID().uuidString.lowercased(), Date().timeIntervalSince1970, r.int("id")])
            }
            try db.exec("CREATE UNIQUE INDEX IF NOT EXISTS idx_\(t)_uid ON \(t)(uid);")
        }
        try db.exec("""
            ALTER TABLE items ADD COLUMN stock_adjust REAL NOT NULL DEFAULT 0;
            CREATE TABLE IF NOT EXISTS tombstones (tbl TEXT NOT NULL, uid TEXT NOT NULL, deleted_at REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_state (key TEXT PRIMARY KEY, value TEXT);
            """)
        // Keep today's stock exactly as it is: stock_adjust = stock − (purchased − delivered).
        try db.exec("""
            UPDATE items SET stock_adjust = stock
              - COALESCE((SELECT SUM(qty) FROM purchase_lines WHERE item_id = items.id), 0)
              + COALESCE((SELECT SUM(dl.qty) FROM doc_lines dl JOIN challans c ON c.id = dl.doc_id
                          WHERE dl.doc_type = 'D' AND c.status = 'approved' AND dl.item_id = items.id), 0);
            """)
    }

    /// v4: office network sync. Every change gets the next number from a local counter
    /// (lan_seq), so another computer can ask "what changed since #123?". Deletions are
    /// kept (tombstones) so they reach every computer, not only the cloud.
    private static func addOfficeSync(_ db: SQLiteDB) throws {
        try db.exec("""
            CREATE TABLE IF NOT EXISTS lan_clock (id INTEGER PRIMARY KEY CHECK (id = 1), n INTEGER NOT NULL);
            INSERT OR IGNORE INTO lan_clock (id, n) VALUES (1, 0);
            ALTER TABLE tombstones ADD COLUMN pushed INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE tombstones ADD COLUMN lan_seq INTEGER NOT NULL DEFAULT 0;
            """)
        // Number existing rows in dependency order, so a first sync never sees a
        // challan before its quotation.
        for t in syncedTables {
            try db.exec("ALTER TABLE \(t) ADD COLUMN lan_seq INTEGER NOT NULL DEFAULT 0;")
            for r in try db.query("SELECT id FROM \(t) ORDER BY id") {
                try db.exec("UPDATE lan_clock SET n = n + 1;")
                try db.run("UPDATE \(t) SET lan_seq = (SELECT n FROM lan_clock) WHERE id = ?", [r.int("id")])
            }
            try db.exec("CREATE INDEX IF NOT EXISTS idx_\(t)_lan ON \(t)(lan_seq);")
        }
    }

    static let recomputeStockSQL = """
        UPDATE items SET stock = stock_adjust
          + COALESCE((SELECT SUM(qty) FROM purchase_lines WHERE item_id = items.id), 0)
          - COALESCE((SELECT SUM(dl.qty) FROM doc_lines dl JOIN challans c ON c.id = dl.doc_id
                      WHERE dl.doc_type = 'D' AND c.status = 'approved' AND dl.item_id = items.id), 0);
        """

    private static func createV1(_ db: SQLiteDB) throws {
        try db.exec("""
        CREATE TABLE IF NOT EXISTS items (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            code        TEXT NOT NULL UNIQUE COLLATE NOCASE,
            name        TEXT NOT NULL,
            details     TEXT NOT NULL DEFAULT '',
            unit        TEXT NOT NULL DEFAULT 'Pcs',
            image       BLOB,
            cost_price  REAL NOT NULL DEFAULT 0,
            sale_price  REAL NOT NULL DEFAULT 0,
            stock       REAL NOT NULL DEFAULT 0,
            created_at  REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS purchases (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            number    TEXT NOT NULL DEFAULT '',
            supplier  TEXT NOT NULL DEFAULT '',
            date      REAL NOT NULL,
            notes     TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS purchase_lines (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            purchase_id  INTEGER NOT NULL REFERENCES purchases(id) ON DELETE CASCADE,
            item_id      INTEGER REFERENCES items(id) ON DELETE SET NULL,
            code         TEXT NOT NULL DEFAULT '',
            name         TEXT NOT NULL DEFAULT '',
            details      TEXT NOT NULL DEFAULT '',
            unit         TEXT NOT NULL DEFAULT 'Pcs',
            qty          REAL NOT NULL DEFAULT 0,
            cost         REAL NOT NULL DEFAULT 0,
            sale_price   REAL NOT NULL DEFAULT 0,
            position     INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS quotations (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            number            TEXT NOT NULL UNIQUE COLLATE NOCASE,
            po_number         TEXT NOT NULL DEFAULT '',
            customer_name     TEXT NOT NULL DEFAULT '',
            customer_address  TEXT NOT NULL DEFAULT '',
            customer_phone    TEXT NOT NULL DEFAULT '',
            date              REAL NOT NULL,
            tax_rate          REAL NOT NULL DEFAULT 18,
            notes             TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS challans (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            number            TEXT NOT NULL UNIQUE COLLATE NOCASE,
            quotation_id      INTEGER NOT NULL UNIQUE REFERENCES quotations(id),
            po_number         TEXT NOT NULL DEFAULT '',
            customer_name     TEXT NOT NULL DEFAULT '',
            customer_address  TEXT NOT NULL DEFAULT '',
            customer_phone    TEXT NOT NULL DEFAULT '',
            date              REAL NOT NULL,
            status            TEXT NOT NULL DEFAULT 'pending',
            approved_at       REAL,
            vehicle_no        TEXT NOT NULL DEFAULT '',
            driver_name       TEXT NOT NULL DEFAULT '',
            notes             TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS invoices (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            number            TEXT NOT NULL UNIQUE COLLATE NOCASE,
            quotation_id      INTEGER NOT NULL UNIQUE REFERENCES quotations(id),
            challan_id        INTEGER NOT NULL UNIQUE REFERENCES challans(id),
            po_number         TEXT NOT NULL DEFAULT '',
            customer_name     TEXT NOT NULL DEFAULT '',
            customer_address  TEXT NOT NULL DEFAULT '',
            customer_phone    TEXT NOT NULL DEFAULT '',
            date              REAL NOT NULL,
            tax_rate          REAL NOT NULL DEFAULT 18,
            notes             TEXT NOT NULL DEFAULT ''
        );

        -- Lines for quotations ('Q'), delivery challans ('D') and invoices ('I').
        CREATE TABLE IF NOT EXISTS doc_lines (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            doc_type   TEXT NOT NULL,
            doc_id     INTEGER NOT NULL,
            item_id    INTEGER,
            code       TEXT NOT NULL DEFAULT '',
            name       TEXT NOT NULL DEFAULT '',
            details    TEXT NOT NULL DEFAULT '',
            unit       TEXT NOT NULL DEFAULT 'Pcs',
            qty        REAL NOT NULL DEFAULT 0,
            rate       REAL NOT NULL DEFAULT 0,
            base_rate  REAL NOT NULL DEFAULT 0,
            position   INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_doc_lines_doc ON doc_lines(doc_type, doc_id);

        CREATE TABLE IF NOT EXISTS settings (
            key    TEXT PRIMARY KEY,
            value  TEXT,
            data   BLOB
        );
        """)
    }
}
