using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AfanTraders.Core;

// JSON shapes shared with the Mac app (macOS/Sources/AfanTraders/Sync/SyncRecords.swift).
// Property names are the JSON field names, so they stay snake_case.
#pragma warning disable IDE1006
public sealed record SyncLine(string? item_uid, string code, string name, string details, string unit,
                              double qty, double rate, double base_rate, double cost);
public sealed record SyncItem(string code, string name, string details, string unit, byte[]? image,
                              double cost_price, double sale_price, double stock_adjust, double created_at);
public sealed record SyncPurchaseLine(string? item_uid, string code, string name, string details, string unit,
                                      double qty, double cost, double sale_price);
public sealed record SyncPurchase(string number, string supplier, double date, string notes, List<SyncPurchaseLine> lines);
public sealed record SyncQuotation(string number, string po_number, string customer_name, string customer_address,
                                   string customer_phone, double date, double tax_rate, string notes, List<SyncLine> lines);
public sealed record SyncChallan(string number, string quotation_uid, string po_number, string customer_name,
                                 string customer_address, string customer_phone, double date, string status, double? approved_at,
                                 string vehicle_no, string driver_name, string notes, List<SyncLine> lines);
public sealed record SyncInvoice(string number, string quotation_uid, string challan_uid, string po_number, string customer_name,
                                 string customer_address, string customer_phone, double date, double tax_rate, string notes,
                                 List<SyncLine> lines);
public sealed record SyncSettings(Dictionary<string, string> values, byte[]? logo, byte[]? letterhead);
#pragma warning restore IDE1006

/// <summary>A local change waiting to be sent.</summary>
public sealed record OutgoingRow(string Table, long? LocalId, string Uid, double UpdatedAt, byte[]? Data, bool Deleted, long Seq = 0);

/// <summary>A row received from Supabase or another office computer.</summary>
public sealed record IncomingRow(string Table, string Uid, byte[]? Data, double UpdatedAt, bool Deleted, long Seq = 0);

public sealed record ApplyResult(List<string> Problems, List<IncomingRow> Failed, int Applied);

public sealed partial class Store
{
    public static readonly string[] SyncTables = { "items", "purchases", "quotations", "challans", "invoices", "settings" };
    public const string SettingsUid = "company-settings";

    internal static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);
    static double? ParseD(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    Dictionary<long, string> UidMap(string t) => Db.Query($"SELECT id, uid FROM {t} WHERE uid IS NOT NULL").ToDictionary(r => r.Int("id"), r => r.Str("uid"));
    Dictionary<string, long> IdMap(string t) => Db.Query($"SELECT id, uid FROM {t} WHERE uid IS NOT NULL").ToDictionary(r => r.Str("uid"), r => r.Int("id"));

    List<SyncLine> SyncLines(string type, long docId, Dictionary<long, string> itemUids) =>
        Db.Query("SELECT * FROM doc_lines WHERE doc_type=? AND doc_id=? ORDER BY position", type, docId).Select(r =>
            new SyncLine(r.OptInt("item_id") is { } i && itemUids.TryGetValue(i, out var u) ? u : null, r.Str("code"), r.Str("name"),
                         r.Str("details"), r.Str("unit"), r.Dbl("qty"), r.Dbl("rate"), r.Dbl("base_rate"), r.Dbl("cost"))).ToList();

    // MARK: Outgoing

    public int PendingCount() => Locked(() =>
    {
        long n = 0;
        foreach (var t in Schema.SyncedTables) n += Db.Query($"SELECT COUNT(*) AS n FROM {t} WHERE dirty = 1").First().Int("n");
        n += Db.Query("SELECT COUNT(*) AS n FROM tombstones WHERE pushed = 0").First().Int("n");
        if (SyncValue("settings_dirty") == "1") n++;
        return (int)n;
    });

    /// <summary>Changes waiting for the cloud.</summary>
    public List<OutgoingRow> PendingChanges() => Locked(() => ExportRows(null));

    /// <summary>Changes another office computer hasn't seen yet, oldest first.</summary>
    public (List<OutgoingRow> Rows, bool More) LanChanges(long after, int limit) => Locked(() =>
    {
        var all = ExportRows(after).OrderBy(r => r.Seq).ToList();
        return (all.Take(limit).ToList(), all.Count > limit);
    });

    List<OutgoingRow> ExportRows(long? lanAfter)
    {
        var filter = lanAfter == null ? "dirty = 1 AND uid IS NOT NULL" : $"lan_seq > {lanAfter} AND uid IS NOT NULL";
        var itemUids = UidMap("items");
        var quoteUids = UidMap("quotations");
        var challanUids = UidMap("challans");
        var output = new List<OutgoingRow>();
        void Add<T>(string table, Row r, T value) =>
            output.Add(new OutgoingRow(table, r.Int("id"), r.Str("uid"), r.Dbl("updated_at"), JsonSerializer.SerializeToUtf8Bytes(value, Json), false, r.Int("lan_seq")));

        foreach (var r in Db.Query($"SELECT * FROM items WHERE {filter}"))
            Add("items", r, new SyncItem(r.Str("code"), r.Str("name"), r.Str("details"), r.Str("unit"), r.Data("image"),
                                         r.Dbl("cost_price"), r.Dbl("sale_price"), r.Dbl("stock_adjust"), r.Dbl("created_at")));
        foreach (var r in Db.Query($"SELECT * FROM purchases WHERE {filter}"))
        {
            var lines = Db.Query("SELECT * FROM purchase_lines WHERE purchase_id = ? ORDER BY position", r.Int("id")).Select(l =>
                new SyncPurchaseLine(l.OptInt("item_id") is { } i && itemUids.TryGetValue(i, out var u) ? u : null, l.Str("code"), l.Str("name"),
                                     l.Str("details"), l.Str("unit"), l.Dbl("qty"), l.Dbl("cost"), l.Dbl("sale_price"))).ToList();
            Add("purchases", r, new SyncPurchase(r.Str("number"), r.Str("supplier"), r.Dbl("date"), r.Str("notes"), lines));
        }
        foreach (var r in Db.Query($"SELECT * FROM quotations WHERE {filter}"))
            Add("quotations", r, new SyncQuotation(r.Str("number"), r.Str("po_number"), r.Str("customer_name"), r.Str("customer_address"),
                r.Str("customer_phone"), r.Dbl("date"), r.Dbl("tax_rate"), r.Str("notes"), SyncLines("Q", r.Int("id"), itemUids)));
        foreach (var r in Db.Query($"SELECT * FROM challans WHERE {filter}"))
        {
            if (!quoteUids.TryGetValue(r.Int("quotation_id"), out var qUid)) continue;
            Add("challans", r, new SyncChallan(r.Str("number"), qUid, r.Str("po_number"), r.Str("customer_name"), r.Str("customer_address"),
                r.Str("customer_phone"), r.Dbl("date"), r.Str("status"), r.Has("approved_at") ? r.Dbl("approved_at") : null,
                r.Str("vehicle_no"), r.Str("driver_name"), r.Str("notes"), SyncLines("D", r.Int("id"), itemUids)));
        }
        foreach (var r in Db.Query($"SELECT * FROM invoices WHERE {filter}"))
        {
            if (!quoteUids.TryGetValue(r.Int("quotation_id"), out var qUid) || !challanUids.TryGetValue(r.Int("challan_id"), out var cUid)) continue;
            Add("invoices", r, new SyncInvoice(r.Str("number"), qUid, cUid, r.Str("po_number"), r.Str("customer_name"), r.Str("customer_address"),
                r.Str("customer_phone"), r.Dbl("date"), r.Dbl("tax_rate"), r.Str("notes"), SyncLines("I", r.Int("id"), itemUids)));
        }
        var settingsSeq = long.TryParse(SyncValue("settings_lan_seq"), out var ss) ? ss : 0;
        var wantSettings = lanAfter is { } after ? settingsSeq > after && SyncValue("settings_updated") != null : SyncValue("settings_dirty") == "1";
        if (wantSettings)
        {
            var s = Settings;
            output.Add(new OutgoingRow("settings", null, SettingsUid, ParseD(SyncValue("settings_updated")) ?? Fmt.Unix(DateTime.Now),
                JsonSerializer.SerializeToUtf8Bytes(new SyncSettings(SettingsValues(), s.Logo, s.Letterhead), Json), false, settingsSeq));
        }
        var tombFilter = lanAfter is { } a ? $"lan_seq > {a}" : "pushed = 0";
        foreach (var r in Db.Query($"SELECT * FROM tombstones WHERE {tombFilter}"))
            output.Add(new OutgoingRow(r.Str("tbl"), null, r.Str("uid"), r.Dbl("deleted_at"), null, true, r.Int("lan_seq")));
        return output;
    }

    /// <summary>Clears "needs upload" unless the row changed again while uploading.</summary>
    public void MarkPushed(IEnumerable<OutgoingRow> rows) => Locked(() => Db.Transaction(() =>
    {
        foreach (var r in rows)
        {
            if (r.Deleted) Db.Run("UPDATE tombstones SET pushed = 1 WHERE tbl = ? AND uid = ?", r.Table, r.Uid);
            else if (r.Table == "settings")
            {
                var stamp = SyncValue("settings_updated");
                if (stamp == null || ParseD(stamp) == r.UpdatedAt) SetSyncValue("settings_dirty", "0");
            }
            else if (r.LocalId is { } id) Db.Run($"UPDATE {r.Table} SET dirty = 0 WHERE id = ? AND updated_at = ?", id, r.UpdatedAt);
        }
    }));

    // MARK: Incoming

    static readonly string[] Order = { "items", "purchases", "quotations", "challans", "invoices", "settings" };

    /// <summary>Applies received rows. The newer edit wins.</summary>
    public ApplyResult ApplyRemote(IReadOnlyList<IncomingRow> rows, bool fromLan = false)
    {
        var problems = new List<string>();
        var failed = new List<IncomingRow>();
        int applied = 0;
        var upserts = rows.Where(r => !r.Deleted).OrderBy(r => Array.IndexOf(Order, r.Table)).ToList();
        var deletes = rows.Where(r => r.Deleted).OrderByDescending(r => Array.IndexOf(Order, r.Table)).ToList();
        Locked(() => Db.Transaction(() =>
        {
            foreach (var row in upserts)
            {
                Db.Exec("SAVEPOINT remote_row;");
                try
                {
                    if (ApplyUpsert(row))
                    {
                        applied++;
                        // Pass it on to other office computers; office-network changes are also queued for the cloud.
                        if (row.Table == "settings")
                        {
                            SetSyncValue("settings_lan_seq", Num(NextLanSeq()));
                            if (fromLan) SetSyncValue("settings_dirty", "1");
                        }
                        else BumpLan(row.Table, row.Uid, fromLan);
                    }
                    Db.Exec("RELEASE remote_row;");
                }
                catch (Exception e)
                {
                    try { Db.Exec("ROLLBACK TO remote_row;"); Db.Exec("RELEASE remote_row;"); } catch { }
                    problems.Add($"{row.Table} {row.Uid[..Math.Min(8, row.Uid.Length)]}: {e.Message}");
                    failed.Add(row);
                }
            }
            foreach (var row in deletes)
            {
                try { ApplyDelete(row, fromLan); }
                catch (Exception e) { problems.Add($"delete {row.Table} {row.Uid[..Math.Min(8, row.Uid.Length)]}: {e.Message}"); }
            }
            RecomputeStock();
        }));
        return new ApplyResult(problems, failed, applied + deletes.Count);
    }

    bool ApplyUpsert(IncomingRow row)
    {
        if (row.Data == null) return false;
        if (row.Table == "settings")
        {
            var localStamp = ParseD(SyncValue("settings_updated")) ?? 0;
            if (localStamp == row.UpdatedAt) return false;
            if (SyncValue("settings_dirty") == "1" && localStamp >= row.UpdatedAt) return false;
            var s = JsonSerializer.Deserialize<SyncSettings>(row.Data, Json)!;
            foreach (var (k, v) in s.values) Db.Run("INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)", k, v);
            Db.Run("INSERT OR REPLACE INTO settings (key, data) VALUES ('logo', ?)", s.logo);
            Db.Run("INSERT OR REPLACE INTO settings (key, data) VALUES ('letterhead', ?)", s.letterhead);
            SetSyncValue("settings_updated", row.UpdatedAt.ToString("R", CultureInfo.InvariantCulture));
            SetSyncValue("settings_dirty", "0");
            return true;
        }
        var local = Db.Query($"SELECT id, dirty, updated_at FROM {row.Table} WHERE uid = ?", row.Uid).FirstOrDefault();
        if (local != null && local.Dbl("updated_at") >= row.UpdatedAt) return false;   // same or newer version here
        long? id = local?.Int("id");
        switch (row.Table)
        {
            case "items": ApplyItem(JsonSerializer.Deserialize<SyncItem>(row.Data, Json)!, row, id); break;
            case "purchases": ApplyPurchase(JsonSerializer.Deserialize<SyncPurchase>(row.Data, Json)!, row, id); break;
            case "quotations": ApplyQuotation(JsonSerializer.Deserialize<SyncQuotation>(row.Data, Json)!, row, id); break;
            case "challans": ApplyChallan(JsonSerializer.Deserialize<SyncChallan>(row.Data, Json)!, row, id); break;
            case "invoices": ApplyInvoice(JsonSerializer.Deserialize<SyncInvoice>(row.Data, Json)!, row, id); break;
            default: return false;
        }
        return true;
    }

    /// <summary>Same Item ID / number made on two computers offline: the smaller uid keeps it, the other gets "-2".</summary>
    string ResolveUnique(string table, string column, string value, string uid)
    {
        var other = Db.Query($"SELECT id, uid FROM {table} WHERE {column} = ? COLLATE NOCASE AND uid <> ?", value, uid).FirstOrDefault();
        if (other == null) return value;
        if (string.CompareOrdinal(uid, other.Str("uid")) < 0)
        {
            Db.Run($"UPDATE {table} SET {column} = {column} || '-2' WHERE id = ?", other.Int("id"));
            Touch(table, other.Int("id"));
            return value;
        }
        return value + "-2";
    }

    void ApplyItem(SyncItem d, IncomingRow row, long? id)
    {
        var code = ResolveUnique("items", "code", d.code, row.Uid);
        if (id is { } i)
            Db.Run("UPDATE items SET code=?, name=?, details=?, unit=?, image=?, cost_price=?, sale_price=?, stock_adjust=?, updated_at=?, dirty=0 WHERE id=?",
                   code, d.name, d.details, d.unit, d.image, d.cost_price, d.sale_price, d.stock_adjust, row.UpdatedAt, i);
        else
            Db.Run("""
                INSERT INTO items (code, name, details, unit, image, cost_price, sale_price, stock, stock_adjust, created_at, uid, updated_at, dirty)
                VALUES (?,?,?,?,?,?,?,0,?,?,?,?,0)
                """, code, d.name, d.details, d.unit, d.image, d.cost_price, d.sale_price, d.stock_adjust, d.created_at, row.Uid, row.UpdatedAt);
    }

    void ApplyPurchase(SyncPurchase d, IncomingRow row, long? id)
    {
        var items = IdMap("items");
        long pid;
        if (id is { } i)
        {
            pid = i;
            Db.Run("UPDATE purchases SET number=?, supplier=?, date=?, notes=?, updated_at=?, dirty=0 WHERE id=?", d.number, d.supplier, d.date, d.notes, row.UpdatedAt, i);
            Db.Run("DELETE FROM purchase_lines WHERE purchase_id = ?", i);
        }
        else pid = Db.Run("INSERT INTO purchases (number, supplier, date, notes, uid, updated_at, dirty) VALUES (?,?,?,?,?,?,0)",
                          d.number, d.supplier, d.date, d.notes, row.Uid, row.UpdatedAt);
        for (int pos = 0; pos < d.lines.Count; pos++)
        {
            var l = d.lines[pos];
            Db.Run("""
                INSERT INTO purchase_lines (purchase_id, item_id, code, name, details, unit, qty, cost, sale_price, position)
                VALUES (?,?,?,?,?,?,?,?,?,?)
                """, pid, l.item_uid != null && items.TryGetValue(l.item_uid, out var iid) ? iid : null,
                   l.code, l.name, l.details, l.unit, l.qty, l.cost, l.sale_price, pos);
        }
    }

    void WriteLines(string type, long docId, List<SyncLine> lines)
    {
        var items = IdMap("items");
        Db.Run("DELETE FROM doc_lines WHERE doc_type = ? AND doc_id = ?", type, docId);
        for (int pos = 0; pos < lines.Count; pos++)
        {
            var l = lines[pos];
            Db.Run("""
                INSERT INTO doc_lines (doc_type, doc_id, item_id, code, name, details, unit, qty, rate, base_rate, cost, position)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
                """, type, docId, l.item_uid != null && items.TryGetValue(l.item_uid, out var iid) ? iid : null,
                   l.code, l.name, l.details, l.unit, l.qty, l.rate, l.base_rate, l.cost, pos);
        }
    }

    void ApplyQuotation(SyncQuotation d, IncomingRow row, long? id)
    {
        var number = ResolveUnique("quotations", "number", d.number, row.Uid);
        object?[] p = { number, d.po_number, d.customer_name, d.customer_address, d.customer_phone, d.date, d.tax_rate, d.notes, row.UpdatedAt };
        long qid;
        if (id is { } i)
        {
            qid = i;
            Db.Run("UPDATE quotations SET number=?, po_number=?, customer_name=?, customer_address=?, customer_phone=?, date=?, tax_rate=?, notes=?, updated_at=?, dirty=0 WHERE id=?",
                   p.Append(i).ToArray());
        }
        else qid = Db.Run("INSERT INTO quotations (number, po_number, customer_name, customer_address, customer_phone, date, tax_rate, notes, updated_at, uid, dirty) VALUES (?,?,?,?,?,?,?,?,?,?,0)",
                          p.Append(row.Uid).ToArray());
        WriteLines("Q", qid, d.lines);
    }

    void ApplyChallan(SyncChallan d, IncomingRow row, long? id)
    {
        if (!IdMap("quotations").TryGetValue(d.quotation_uid, out var qid)) throw new AppError("its quotation has not arrived yet");
        var number = ResolveUnique("challans", "number", d.number, row.Uid);
        object?[] p = { number, qid, d.po_number, d.customer_name, d.customer_address, d.customer_phone, d.date, d.status,
                        d.approved_at, d.vehicle_no, d.driver_name, d.notes, row.UpdatedAt };
        long cid;
        if (id is { } i)
        {
            cid = i;
            Db.Run("UPDATE challans SET number=?, quotation_id=?, po_number=?, customer_name=?, customer_address=?, customer_phone=?, date=?, status=?, approved_at=?, vehicle_no=?, driver_name=?, notes=?, updated_at=?, dirty=0 WHERE id=?",
                   p.Append(i).ToArray());
        }
        else cid = Db.Run("""
            INSERT INTO challans (number, quotation_id, po_number, customer_name, customer_address, customer_phone, date, status, approved_at, vehicle_no, driver_name, notes, updated_at, uid, dirty)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,0)
            """, p.Append(row.Uid).ToArray());
        WriteLines("D", cid, d.lines);
    }

    void ApplyInvoice(SyncInvoice d, IncomingRow row, long? id)
    {
        var quotes = IdMap("quotations");
        var challans = IdMap("challans");
        if (!quotes.TryGetValue(d.quotation_uid, out var qid) || !challans.TryGetValue(d.challan_uid, out var cid))
            throw new AppError("its quotation or delivery challan has not arrived yet");
        var number = ResolveUnique("invoices", "number", d.number, row.Uid);
        object?[] p = { number, qid, cid, d.po_number, d.customer_name, d.customer_address, d.customer_phone, d.date, d.tax_rate, d.notes, row.UpdatedAt };
        long iid;
        if (id is { } i)
        {
            iid = i;
            Db.Run("UPDATE invoices SET number=?, quotation_id=?, challan_id=?, po_number=?, customer_name=?, customer_address=?, customer_phone=?, date=?, tax_rate=?, notes=?, updated_at=?, dirty=0 WHERE id=?",
                   p.Append(i).ToArray());
        }
        else iid = Db.Run("INSERT INTO invoices (number, quotation_id, challan_id, po_number, customer_name, customer_address, customer_phone, date, tax_rate, notes, updated_at, uid, dirty) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,0)",
                          p.Append(row.Uid).ToArray());
        WriteLines("I", iid, d.lines);
    }

    void ApplyDelete(IncomingRow row, bool fromLan)
    {
        if (row.Table == "settings") return;
        if (Db.Query("SELECT 1 FROM tombstones WHERE tbl = ? AND uid = ?", row.Table, row.Uid).Count == 0)
            Db.Run("INSERT INTO tombstones (tbl, uid, deleted_at, pushed, lan_seq) VALUES (?,?,?,?,?)", row.Table, row.Uid, row.UpdatedAt, fromLan ? 0 : 1, NextLanSeq());
        var local = Db.Query($"SELECT id, dirty, updated_at FROM {row.Table} WHERE uid = ?", row.Uid).FirstOrDefault();
        if (local == null) return;
        if (local.Int("dirty") == 1 && local.Dbl("updated_at") > row.UpdatedAt) return;   // edited here after the delete
        var id = local.Int("id");
        switch (row.Table)
        {
            case "purchases": Db.Run("DELETE FROM purchase_lines WHERE purchase_id = ?", id); break;
            case "quotations": Db.Run("DELETE FROM doc_lines WHERE doc_type='Q' AND doc_id=?", id); break;
            case "challans": Db.Run("DELETE FROM doc_lines WHERE doc_type='D' AND doc_id=?", id); break;
            case "invoices": Db.Run("DELETE FROM doc_lines WHERE doc_type='I' AND doc_id=?", id); break;
        }
        Db.Run($"DELETE FROM {row.Table} WHERE id = ?", id);
    }
}
