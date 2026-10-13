namespace AfanTraders.Core;

/// <summary>
/// Database layout – identical to the Mac app (macOS/Sources/AfanTraders/Data/Schema.swift),
/// so a database file or backup can move between Mac and Windows.
/// </summary>
public static class Schema
{
    public const long Version = 4;
    public static readonly string[] SyncedTables = { "items", "purchases", "quotations", "challans", "invoices" };

    public static void Migrate(Db db)
    {
        var current = db.Query("PRAGMA user_version").FirstOrDefault()?.Int("user_version") ?? 0;
        if (current >= Version) return;
        if (current < 1) CreateV1(db);
        if (current < 2)
        {
            db.Exec("ALTER TABLE doc_lines ADD COLUMN cost REAL NOT NULL DEFAULT 0;");
            db.Exec("""
                UPDATE doc_lines SET cost = COALESCE((SELECT cost_price FROM items WHERE items.id = doc_lines.item_id), 0)
                WHERE doc_type = 'I' AND item_id IS NOT NULL;
                """);
        }
        if (current < 3) AddSync(db);
        if (current < 4) AddOfficeSync(db);
        db.Exec($"PRAGMA user_version = {Version};");
    }

    static void AddSync(Db db)
    {
        foreach (var t in SyncedTables)
        {
            db.Exec($"""
                ALTER TABLE {t} ADD COLUMN uid TEXT;
                ALTER TABLE {t} ADD COLUMN updated_at REAL NOT NULL DEFAULT 0;
                ALTER TABLE {t} ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;
                """);
            foreach (var r in db.Query($"SELECT id FROM {t}"))
                db.Run($"UPDATE {t} SET uid = ?, updated_at = ? WHERE id = ?", Guid.NewGuid().ToString(), Fmt.Unix(DateTime.Now), r.Int("id"));
            db.Exec($"CREATE UNIQUE INDEX IF NOT EXISTS idx_{t}_uid ON {t}(uid);");
        }
        db.Exec("""
            ALTER TABLE items ADD COLUMN stock_adjust REAL NOT NULL DEFAULT 0;
            CREATE TABLE IF NOT EXISTS tombstones (tbl TEXT NOT NULL, uid TEXT NOT NULL, deleted_at REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_state (key TEXT PRIMARY KEY, value TEXT);
            """);
        db.Exec("""
            UPDATE items SET stock_adjust = stock
              - COALESCE((SELECT SUM(qty) FROM purchase_lines WHERE item_id = items.id), 0)
              + COALESCE((SELECT SUM(dl.qty) FROM doc_lines dl JOIN challans c ON c.id = dl.doc_id
                          WHERE dl.doc_type = 'D' AND c.status = 'approved' AND dl.item_id = items.id), 0);
            """);
    }

    static void AddOfficeSync(Db db)
    {
        db.Exec("""
            CREATE TABLE IF NOT EXISTS lan_clock (id INTEGER PRIMARY KEY CHECK (id = 1), n INTEGER NOT NULL);
            INSERT OR IGNORE INTO lan_clock (id, n) VALUES (1, 0);
            ALTER TABLE tombstones ADD COLUMN pushed INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE tombstones ADD COLUMN lan_seq INTEGER NOT NULL DEFAULT 0;
            """);
        foreach (var t in SyncedTables)
        {
            db.Exec($"ALTER TABLE {t} ADD COLUMN lan_seq INTEGER NOT NULL DEFAULT 0;");
            foreach (var r in db.Query($"SELECT id FROM {t} ORDER BY id"))
            {
                db.Exec("UPDATE lan_clock SET n = n + 1;");
                db.Run($"UPDATE {t} SET lan_seq = (SELECT n FROM lan_clock) WHERE id = ?", r.Int("id"));
            }
            db.Exec($"CREATE INDEX IF NOT EXISTS idx_{t}_lan ON {t}(lan_seq);");
        }
    }

    public const string RecomputeStockSql = """
        UPDATE items SET stock = stock_adjust
          + COALESCE((SELECT SUM(qty) FROM purchase_lines WHERE item_id = items.id), 0)
          - COALESCE((SELECT SUM(dl.qty) FROM doc_lines dl JOIN challans c ON c.id = dl.doc_id
                      WHERE dl.doc_type = 'D' AND c.status = 'approved' AND dl.item_id = items.id), 0);
        """;

    static void CreateV1(Db db) => db.Exec("""
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
        """);
}
