using System.Globalization;

namespace AfanTraders.Core;

/// <summary>
/// All business rules – a line-for-line port of the Mac app's AppStore.
/// Thread-safe: every database access happens under one lock, so the sync
/// engines can call in from background threads. Lists are replaced, never mutated.
/// </summary>
public sealed partial class Store : IDisposable
{
    public IReadOnlyList<Item> Items { get; private set; } = Array.Empty<Item>();
    public IReadOnlyList<Purchase> Purchases { get; private set; } = Array.Empty<Purchase>();
    public IReadOnlyList<Quotation> Quotations { get; private set; } = Array.Empty<Quotation>();
    public IReadOnlyList<Challan> Challans { get; private set; } = Array.Empty<Challan>();
    public IReadOnlyList<Invoice> Invoices { get; private set; } = Array.Empty<Invoice>();
    public CompanySettings Settings { get; private set; } = new();

    public string DataFolder { get; }
    internal readonly Db Db;
    internal readonly object Gate = new();

    /// <summary>Data was reloaded (raised on any thread – UI must dispatch).</summary>
    public event Action? DataChanged;
    /// <summary>A change was made on this computer (sync engines listen).</summary>
    public event Action? LocalChange;

    public Store(string? folder = null)
    {
        DataFolder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFAN Traders");
        Directory.CreateDirectory(DataFolder);
        Db = new Db(Path.Combine(DataFolder, "afan-traders.sqlite"));
        Schema.Migrate(Db);
        Db.Exec(Schema.RecomputeStockSql);
        ReloadAll();
    }

    public void Dispose() => Db.Dispose();

    T Locked<T>(Func<T> f) { lock (Gate) return f(); }
    void Locked(Action f) { lock (Gate) f(); }

    void Changed()
    {
        DataChanged?.Invoke();
        LocalChange?.Invoke();
    }

    // MARK: Change tracking (sync)

    internal void Touch(string table, long id)
    {
        Db.Exec("UPDATE lan_clock SET n = n + 1;");
        Db.Run($"UPDATE {table} SET uid = COALESCE(uid, ?), updated_at = ?, dirty = 1, lan_seq = (SELECT n FROM lan_clock) WHERE id = ?",
               Guid.NewGuid().ToString(), Fmt.Unix(DateTime.Now), id);
    }

    internal void BumpLan(string table, string uid, bool dirty)
    {
        Db.Exec("UPDATE lan_clock SET n = n + 1;");
        Db.Run($"UPDATE {table} SET lan_seq = (SELECT n FROM lan_clock), dirty = CASE WHEN ? THEN 1 ELSE dirty END WHERE uid = ?", dirty, uid);
    }

    internal long NextLanSeq()
    {
        Db.Exec("UPDATE lan_clock SET n = n + 1;");
        return Db.Query("SELECT n FROM lan_clock").FirstOrDefault()?.Int("n") ?? 0;
    }

    internal void Tombstone(string table, long id)
    {
        var seq = NextLanSeq();
        Db.Run($"INSERT INTO tombstones (tbl, uid, deleted_at, pushed, lan_seq) SELECT ?, uid, ?, 0, ? FROM {table} WHERE id = ? AND uid IS NOT NULL",
               table, Fmt.Unix(DateTime.Now), seq, id);
    }

    internal void RecomputeStock() => Db.Exec(Schema.RecomputeStockSql);

    // MARK: Lookups

    public Item? ItemById(long? id) => id is { } i ? Items.FirstOrDefault(x => x.Id == i) : null;
    public Item? ItemByCode(string code) { var c = code.T(); return Items.FirstOrDefault(x => string.Equals(x.Code, c, StringComparison.OrdinalIgnoreCase)); }
    public Quotation? QuotationById(long id) => Quotations.FirstOrDefault(q => q.Id == id);
    public Challan? ChallanById(long id) => Challans.FirstOrDefault(c => c.Id == id);
    public Challan? ChallanForQuotation(long id) => id == 0 ? null : Challans.FirstOrDefault(c => c.QuotationId == id);
    public Invoice? InvoiceForQuotation(long id) => id == 0 ? null : Invoices.FirstOrDefault(i => i.QuotationId == id);
    public Invoice? InvoiceForChallan(long id) => id == 0 ? null : Invoices.FirstOrDefault(i => i.ChallanId == id);

    public OrderStage StageOf(Quotation q)
    {
        var c = ChallanForQuotation(q.Id);
        if (c == null) return OrderStage.NeedsChallan;
        if (InvoiceForQuotation(q.Id) != null) return OrderStage.Invoiced;
        return c.Status == ChallanStatus.Approved ? OrderStage.ReadyToInvoice : OrderStage.AwaitingApproval;
    }

    public IReadOnlyList<(string Name, string Address, string Phone)> KnownCustomers()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string, string)>();
        foreach (var q in Quotations)
            if (q.CustomerName.Length > 0 && seen.Add(q.CustomerName)) list.Add((q.CustomerName, q.CustomerAddress, q.CustomerPhone));
        return list;
    }

    // MARK: Numbers

    public static string NewNumber(string prefix, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var yy = (DateTime.Now.Year % 100).ToString("00");
        var p = prefix.T().Length == 0 ? "" : prefix.T() + "-";
        while (true)
        {
            var n = $"{p}{yy}-{Random.Shared.Next(10000, 100000)}";
            if (!taken.Contains(n)) return n;
        }
    }

    public string NewQuotationNumber() => NewNumber(Settings.QuotationPrefix, Quotations.Select(q => q.Number));
    public string NewChallanNumber() => NewNumber(Settings.ChallanPrefix, Challans.Select(c => c.Number));
    public string NewInvoiceNumber() => NewNumber(Settings.InvoicePrefix, Invoices.Select(i => i.Number));

    // MARK: Loading

    public void ReloadAll()
    {
        Locked(() =>
        {
            Settings = LoadSettings();
            Items = LoadItems();
            Purchases = LoadPurchases();
            Quotations = LoadQuotations();
            Challans = LoadChallans();
            Invoices = LoadInvoices();
        });
    }

    List<Item> LoadItems() => Db.Query("SELECT * FROM items ORDER BY code COLLATE NOCASE").Select(r => new Item
    {
        Id = r.Int("id"), Code = r.Str("code"), Name = r.Str("name"), Details = r.Str("details"), Unit = r.Str("unit"),
        Image = r.Data("image"), CostPrice = r.Dbl("cost_price"), SalePrice = r.Dbl("sale_price"), Stock = r.Dbl("stock"),
        CreatedAt = r.Date("created_at"),
    }).ToList();

    Dictionary<long, List<DocLine>> LoadLines(string type)
    {
        var map = new Dictionary<long, List<DocLine>>();
        foreach (var r in Db.Query("SELECT * FROM doc_lines WHERE doc_type = ? ORDER BY doc_id, position", type))
        {
            var l = new DocLine
            {
                ItemId = r.OptInt("item_id"), Code = r.Str("code"), Name = r.Str("name"), Details = r.Str("details"),
                Unit = r.Str("unit"), Qty = r.Dbl("qty"), Rate = r.Dbl("rate"), BaseRate = r.Dbl("base_rate"), Cost = r.Dbl("cost"),
            };
            var id = r.Int("doc_id");
            if (!map.TryGetValue(id, out var list)) map[id] = list = new();
            list.Add(l);
        }
        return map;
    }

    static IReadOnlyList<T> Get<T>(Dictionary<long, List<T>> m, long id) => m.TryGetValue(id, out var l) ? l : Array.Empty<T>();

    List<Purchase> LoadPurchases()
    {
        var lines = new Dictionary<long, List<PurchaseLine>>();
        foreach (var r in Db.Query("SELECT * FROM purchase_lines ORDER BY purchase_id, position"))
        {
            var id = r.Int("purchase_id");
            if (!lines.TryGetValue(id, out var list)) lines[id] = list = new();
            list.Add(new PurchaseLine
            {
                ItemId = r.OptInt("item_id"), Code = r.Str("code"), Name = r.Str("name"), Details = r.Str("details"),
                Unit = r.Str("unit"), Qty = r.Dbl("qty"), Cost = r.Dbl("cost"), SalePrice = r.Dbl("sale_price"),
            });
        }
        return Db.Query("SELECT * FROM purchases ORDER BY date DESC, id DESC").Select(r => new Purchase
        {
            Id = r.Int("id"), Number = r.Str("number"), Supplier = r.Str("supplier"), Date = r.Date("date"),
            Notes = r.Str("notes"), Lines = Get(lines, r.Int("id")),
        }).ToList();
    }

    List<Quotation> LoadQuotations()
    {
        var lines = LoadLines("Q");
        return Db.Query("SELECT * FROM quotations ORDER BY date DESC, id DESC").Select(r => new Quotation
        {
            Id = r.Int("id"), Number = r.Str("number"), PoNumber = r.Str("po_number"), CustomerName = r.Str("customer_name"),
            CustomerAddress = r.Str("customer_address"), CustomerPhone = r.Str("customer_phone"), Date = r.Date("date"),
            TaxRate = r.Dbl("tax_rate"), Notes = r.Str("notes"), Lines = Get(lines, r.Int("id")),
        }).ToList();
    }

    List<Challan> LoadChallans()
    {
        var lines = LoadLines("D");
        return Db.Query("SELECT * FROM challans ORDER BY date DESC, id DESC").Select(r => new Challan
        {
            Id = r.Int("id"), Number = r.Str("number"), QuotationId = r.Int("quotation_id"), PoNumber = r.Str("po_number"),
            CustomerName = r.Str("customer_name"), CustomerAddress = r.Str("customer_address"), CustomerPhone = r.Str("customer_phone"),
            Date = r.Date("date"), Status = r.Str("status") == "approved" ? ChallanStatus.Approved : ChallanStatus.Pending,
            ApprovedAt = r.OptDate("approved_at"), VehicleNo = r.Str("vehicle_no"), DriverName = r.Str("driver_name"),
            Notes = r.Str("notes"), Lines = Get(lines, r.Int("id")),
        }).ToList();
    }

    List<Invoice> LoadInvoices()
    {
        var lines = LoadLines("I");
        return Db.Query("SELECT * FROM invoices ORDER BY date DESC, id DESC").Select(r => new Invoice
        {
            Id = r.Int("id"), Number = r.Str("number"), QuotationId = r.Int("quotation_id"), ChallanId = r.Int("challan_id"),
            PoNumber = r.Str("po_number"), CustomerName = r.Str("customer_name"), CustomerAddress = r.Str("customer_address"),
            CustomerPhone = r.Str("customer_phone"), Date = r.Date("date"), TaxRate = r.Dbl("tax_rate"),
            Notes = r.Str("notes"), Lines = Get(lines, r.Int("id")),
        }).ToList();
    }

    void ReplaceLines(string type, long docId, IReadOnlyList<DocLine> lines)
    {
        Db.Run("DELETE FROM doc_lines WHERE doc_type = ? AND doc_id = ?", type, docId);
        for (int pos = 0; pos < lines.Count; pos++)
        {
            var l = lines[pos];
            Db.Run("""
                INSERT INTO doc_lines (doc_type, doc_id, item_id, code, name, details, unit, qty, rate, base_rate, cost, position)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
                """, type, docId, l.ItemId, l.Code, l.Name, l.Details, l.Unit, l.Qty, l.Rate, l.BaseRate, l.Cost, pos);
        }
    }

    // MARK: Items

    public Item SaveItem(Item input)
    {
        var item = input with { Code = input.Code.T(), Name = input.Name.T() };
        if (item.Code.Length == 0) throw new AppError("Item ID is required.");
        if (item.Name.Length == 0) throw new AppError("Item name is required.");
        if (Items.Any(x => x.Id != item.Id && string.Equals(x.Code, item.Code, StringComparison.OrdinalIgnoreCase)))
            throw new AppError($"Item ID \"{item.Code}\" is already used by another item.");
        Locked(() => Db.Transaction(() =>
        {
            if (item.Id == 0)
            {
                var id = Db.Run("""
                    INSERT INTO items (code, name, details, unit, image, cost_price, sale_price, stock, stock_adjust, created_at)
                    VALUES (?,?,?,?,?,?,?,?,?,?)
                    """, item.Code, item.Name, item.Details, item.Unit, item.Image, item.CostPrice, item.SalePrice, item.Stock, item.Stock, DateTime.Now);
                item = item with { Id = id };
            }
            else
            {
                // A typed-in stock count becomes the manual adjustment on top of purchases and deliveries.
                var moved = StockMovement(item.Id);
                Db.Run("UPDATE items SET code=?, name=?, details=?, unit=?, image=?, cost_price=?, sale_price=?, stock_adjust=? WHERE id=?",
                       item.Code, item.Name, item.Details, item.Unit, item.Image, item.CostPrice, item.SalePrice, item.Stock - moved, item.Id);
            }
            Touch("items", item.Id);
            RecomputeStock();
            Items = LoadItems();
        }));
        Changed();
        return item;
    }

    public void DeleteItem(Item item)
    {
        Locked(() => Db.Transaction(() =>
        {
            Tombstone("items", item.Id);
            Db.Run("DELETE FROM items WHERE id = ?", item.Id);
            Items = LoadItems();
        }));
        Changed();
    }

    double StockMovement(long itemId) => Db.Query("""
        SELECT COALESCE((SELECT SUM(qty) FROM purchase_lines WHERE item_id = ?), 0)
             - COALESCE((SELECT SUM(dl.qty) FROM doc_lines dl JOIN challans c ON c.id = dl.doc_id
                         WHERE dl.doc_type = 'D' AND c.status = 'approved' AND dl.item_id = ?), 0) AS m
        """, itemId, itemId).FirstOrDefault()?.Dbl("m") ?? 0;

    // MARK: Purchases (stock in)

    public void SavePurchase(Purchase input)
    {
        var p = input with { Number = input.Number.T() };
        if (p.Number.Length == 0) throw new AppError("Enter the purchase invoice number so this stock is linked to it.");
        var lines = p.Lines.Where(l => !l.IsBlank).ToList();
        if (lines.Count == 0) throw new AppError("Add at least one item.");
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Code.T().Length == 0) throw new AppError($"Row {i + 1} ({l.Name}) needs an Item ID.");
            if (l.Qty <= 0) throw new AppError($"Quantity for {l.Code} must be more than zero.");
            if (ItemById(l.ItemId) == null && ItemByCode(l.Code) == null && l.Name.T().Length == 0)
                throw new AppError($"Enter a name for the new item {l.Code}.");
        }
        Locked(() => Db.Transaction(() =>
        {
            var pid = p.Id;
            if (pid != 0)
            {
                Db.Run("DELETE FROM purchase_lines WHERE purchase_id = ?", pid);
                Db.Run("UPDATE purchases SET number=?, supplier=?, date=?, notes=? WHERE id=?", p.Number, p.Supplier.T(), p.Date, p.Notes, pid);
            }
            else
            {
                pid = Db.Run("INSERT INTO purchases (number, supplier, date, notes) VALUES (?,?,?,?)", p.Number, p.Supplier.T(), p.Date, p.Notes);
            }
            for (int pos = 0; pos < lines.Count; pos++)
            {
                var l = lines[pos];
                var itemId = AddStock(l);
                Db.Run("""
                    INSERT INTO purchase_lines (purchase_id, item_id, code, name, details, unit, qty, cost, sale_price, position)
                    VALUES (?,?,?,?,?,?,?,?,?,?)
                    """, pid, itemId, l.Code.T(), l.Name.T(), l.Details, l.Unit, l.Qty, l.Cost, l.SalePrice, pos);
                Touch("items", itemId);
            }
            Touch("purchases", pid);
            RecomputeStock();
            Items = LoadItems();
            Purchases = LoadPurchases();
        }));
        Changed();
    }

    long AddStock(PurchaseLine l)
    {
        long? existing = null;
        if (l.ItemId is { } id && Db.Query("SELECT id FROM items WHERE id = ?", id).Count > 0) existing = id;
        else existing = Db.Query("SELECT id FROM items WHERE code = ? COLLATE NOCASE", l.Code.T()).FirstOrDefault()?.Int("id");
        if (existing is { } e)
        {
            Db.Run("UPDATE items SET cost_price = ?, sale_price = CASE WHEN ? > 0 THEN ? ELSE sale_price END WHERE id = ?",
                   l.Cost, l.SalePrice, l.SalePrice, e);
            if (l.Image != null) Db.Run("UPDATE items SET image = ? WHERE id = ?", l.Image, e);
            return e;
        }
        return Db.Run("""
            INSERT INTO items (code, name, details, unit, image, cost_price, sale_price, stock, stock_adjust, created_at)
            VALUES (?,?,?,?,?,?,?,0,0,?)
            """, l.Code.T(), l.Name.T(), l.Details, l.Unit.T().Length == 0 ? "Pcs" : l.Unit.T(), l.Image, l.Cost, l.SalePrice, DateTime.Now);
    }

    public sealed record StockMove(DateTime Date, string Reference, string Party, double Qty, double? Cost);

    public IReadOnlyList<StockMove> StockHistory(Item item)
    {
        var moves = new List<StockMove>();
        foreach (var p in Purchases)
            foreach (var l in p.Lines.Where(l => l.ItemId == item.Id))
                moves.Add(new(p.Date, $"Purchase Inv. {p.Number}", p.Supplier, l.Qty, l.Cost));
        foreach (var c in Challans.Where(c => c.Status == ChallanStatus.Approved))
            foreach (var l in c.Lines.Where(l => l.ItemId == item.Id))
                moves.Add(new(c.ApprovedAt ?? c.Date, $"Delivery Challan {c.Number}", c.CustomerName, -l.Qty, null));
        return moves.OrderByDescending(m => m.Date).ToList();
    }

    public Purchase? LastPurchase(Item item) => Purchases.FirstOrDefault(p => p.Lines.Any(l => l.ItemId == item.Id));

    public void DeletePurchase(Purchase p)
    {
        Locked(() => Db.Transaction(() =>
        {
            Tombstone("purchases", p.Id);
            Db.Run("DELETE FROM purchase_lines WHERE purchase_id = ?", p.Id);
            Db.Run("DELETE FROM purchases WHERE id = ?", p.Id);
            RecomputeStock();
            Items = LoadItems();
            Purchases = LoadPurchases();
        }));
        Changed();
    }

    // MARK: Quotations

    public Quotation DraftQuotation() => new() { Number = NewQuotationNumber(), TaxRate = Settings.DefaultTaxRate };

    public Quotation SaveQuotation(Quotation input)
    {
        var q = input with { Number = input.Number.T(), PoNumber = input.PoNumber.T(), CustomerName = input.CustomerName.T() };
        if (q.Number.Length == 0) throw new AppError("Quotation number is required.");
        if (q.CustomerName.Length == 0) throw new AppError("Customer name is required.");
        if (q.Lines.Count == 0) throw new AppError("Add at least one item to the quotation.");
        if (Quotations.Any(x => x.Id != q.Id && string.Equals(x.Number, q.Number, StringComparison.OrdinalIgnoreCase)))
            throw new AppError($"Quotation number {q.Number} is already used.");
        var locked = ChallanForQuotation(q.Id) != null;
        Locked(() => Db.Transaction(() =>
        {
            object?[] args = { q.Number, q.PoNumber, q.CustomerName, q.CustomerAddress, q.CustomerPhone, q.Date, q.TaxRate, q.Notes };
            if (q.Id == 0)
            {
                q = q with { Id = Db.Run("INSERT INTO quotations (number, po_number, customer_name, customer_address, customer_phone, date, tax_rate, notes) VALUES (?,?,?,?,?,?,?,?)", args) };
            }
            else
            {
                Db.Run("UPDATE quotations SET number=?, po_number=?, customer_name=?, customer_address=?, customer_phone=?, date=?, tax_rate=?, notes=? WHERE id=?",
                       args.Append(q.Id).ToArray());
                // The PO number links all three documents.
                foreach (var r in Db.Query("SELECT id FROM challans WHERE quotation_id=? AND po_number<>?", q.Id, q.PoNumber))
                { Db.Run("UPDATE challans SET po_number=? WHERE id=?", q.PoNumber, r.Int("id")); Touch("challans", r.Int("id")); }
                foreach (var r in Db.Query("SELECT id FROM invoices WHERE quotation_id=? AND po_number<>?", q.Id, q.PoNumber))
                { Db.Run("UPDATE invoices SET po_number=? WHERE id=?", q.PoNumber, r.Int("id")); Touch("invoices", r.Int("id")); }
            }
            if (!locked) ReplaceLines("Q", q.Id, q.Lines);
            Touch("quotations", q.Id);
            Quotations = LoadQuotations();
            Challans = LoadChallans();
            Invoices = LoadInvoices();
        }));
        Changed();
        return q;
    }

    public void DeleteQuotation(Quotation q)
    {
        if (ChallanForQuotation(q.Id) != null) throw new AppError("This quotation has a delivery challan. Delete the challan first.");
        Locked(() => Db.Transaction(() =>
        {
            Tombstone("quotations", q.Id);
            Db.Run("DELETE FROM doc_lines WHERE doc_type='Q' AND doc_id=?", q.Id);
            Db.Run("DELETE FROM quotations WHERE id=?", q.Id);
            Quotations = LoadQuotations();
        }));
        Changed();
    }

    // MARK: Delivery challans

    public Challan DraftChallan(Quotation q) => new()
    {
        Number = NewChallanNumber(), QuotationId = q.Id, PoNumber = q.PoNumber, CustomerName = q.CustomerName,
        CustomerAddress = q.CustomerAddress, CustomerPhone = q.CustomerPhone, Lines = q.Lines,
    };

    public Challan SaveChallan(Challan input)
    {
        var c = input with { Number = input.Number.T() };
        if (c.Number.Length == 0) throw new AppError("Delivery challan number is required.");
        if (Challans.Any(x => x.Id != c.Id && string.Equals(x.Number, c.Number, StringComparison.OrdinalIgnoreCase)))
            throw new AppError($"Delivery challan number {c.Number} is already used.");
        Locked(() => Db.Transaction(() =>
        {
            if (c.Id == 0)
            {
                if (ChallanForQuotation(c.QuotationId) != null) throw new AppError("This quotation already has a delivery challan.");
                var id = Db.Run("""
                    INSERT INTO challans (number, quotation_id, po_number, customer_name, customer_address, customer_phone, date, status, vehicle_no, driver_name, notes)
                    VALUES (?,?,?,?,?,?,?,'pending',?,?,?)
                    """, c.Number, c.QuotationId, c.PoNumber, c.CustomerName, c.CustomerAddress, c.CustomerPhone, c.Date, c.VehicleNo, c.DriverName, c.Notes);
                c = c with { Id = id, Status = ChallanStatus.Pending };
                ReplaceLines("D", c.Id, c.Lines);
            }
            else
            {
                Db.Run("UPDATE challans SET number=?, customer_name=?, customer_address=?, customer_phone=?, date=?, vehicle_no=?, driver_name=?, notes=? WHERE id=?",
                       c.Number, c.CustomerName, c.CustomerAddress, c.CustomerPhone, c.Date, c.VehicleNo, c.DriverName, c.Notes, c.Id);
            }
            Touch("challans", c.Id);
            Challans = LoadChallans();
        }));
        Changed();
        return ChallanById(c.Id) ?? c;
    }

    public IReadOnlyList<string> Shortages(Challan c)
    {
        var need = new Dictionary<long, double>();
        foreach (var l in c.Lines) if (l.ItemId is { } id) need[id] = need.GetValueOrDefault(id) + l.Qty;
        return need.Select(kv => (ItemById(kv.Key), kv.Value)).Where(x => x.Item1 != null && x.Item1.Stock < x.Value)
            .Select(x => $"{x.Item1!.Code} – {x.Item1.Name}: need {Fmt.Qty(x.Value)}, in stock {Fmt.Qty(x.Item1.Stock)}")
            .OrderBy(s => s).ToList();
    }

    /// <summary>Approving a challan means the goods leave the warehouse, so stock goes down.</summary>
    public void SetApproved(Challan c, bool approved)
    {
        var current = ChallanById(c.Id) ?? throw new AppError("Save the delivery challan first.");
        var target = approved ? ChallanStatus.Approved : ChallanStatus.Pending;
        if (current.Status == target) return;
        if (!approved && InvoiceForChallan(c.Id) != null) throw new AppError("An invoice exists for this challan. Delete the invoice first.");
        Locked(() => Db.Transaction(() =>
        {
            Db.Run("UPDATE challans SET status=?, approved_at=? WHERE id=?", approved ? "approved" : "pending", approved ? DateTime.Now : null, c.Id);
            Touch("challans", c.Id);
            RecomputeStock();
            Items = LoadItems();
            Challans = LoadChallans();
        }));
        Changed();
    }

    public void DeleteChallan(Challan c)
    {
        if (InvoiceForChallan(c.Id) != null) throw new AppError("An invoice exists for this challan. Delete the invoice first.");
        Locked(() => Db.Transaction(() =>
        {
            Tombstone("challans", c.Id);
            Db.Run("DELETE FROM doc_lines WHERE doc_type='D' AND doc_id=?", c.Id);
            Db.Run("DELETE FROM challans WHERE id=?", c.Id);
            RecomputeStock();
            Items = LoadItems();
            Challans = LoadChallans();
        }));
        Changed();
    }

    // MARK: Invoices

    public Invoice DraftInvoice(Challan c)
    {
        if (c.Status != ChallanStatus.Approved) throw new AppError("Approve the delivery challan before creating the invoice.");
        if (InvoiceForChallan(c.Id) is { } existing) return existing;
        var q = QuotationById(c.QuotationId) ?? throw new AppError("The quotation for this challan was not found.");
        var lines = q.Lines.Select(l => l with { Key = Guid.NewGuid(), BaseRate = l.Rate, Cost = ItemById(l.ItemId)?.CostPrice ?? 0 }).ToList();
        return new Invoice
        {
            Number = NewInvoiceNumber(), QuotationId = q.Id, ChallanId = c.Id, PoNumber = q.PoNumber, CustomerName = q.CustomerName,
            CustomerAddress = q.CustomerAddress, CustomerPhone = q.CustomerPhone, TaxRate = q.TaxRate, Lines = lines,
        };
    }

    public Invoice SaveInvoice(Invoice input)
    {
        var inv = input with { Number = input.Number.T() };
        if (inv.Number.Length == 0) throw new AppError("Invoice number is required.");
        if (Invoices.Any(x => x.Id != inv.Id && string.Equals(x.Number, inv.Number, StringComparison.OrdinalIgnoreCase)))
            throw new AppError($"Invoice number {inv.Number} is already used.");
        Locked(() => Db.Transaction(() =>
        {
            if (inv.Id == 0)
            {
                if (InvoiceForChallan(inv.ChallanId) != null) throw new AppError("An invoice already exists for this delivery challan.");
                inv = inv with { Id = Db.Run("""
                    INSERT INTO invoices (number, quotation_id, challan_id, po_number, customer_name, customer_address, customer_phone, date, tax_rate, notes)
                    VALUES (?,?,?,?,?,?,?,?,?,?)
                    """, inv.Number, inv.QuotationId, inv.ChallanId, inv.PoNumber, inv.CustomerName, inv.CustomerAddress, inv.CustomerPhone, inv.Date, inv.TaxRate, inv.Notes) };
            }
            else
            {
                Db.Run("UPDATE invoices SET number=?, customer_name=?, customer_address=?, customer_phone=?, date=?, tax_rate=?, notes=? WHERE id=?",
                       inv.Number, inv.CustomerName, inv.CustomerAddress, inv.CustomerPhone, inv.Date, inv.TaxRate, inv.Notes, inv.Id);
            }
            ReplaceLines("I", inv.Id, inv.Lines);
            Touch("invoices", inv.Id);
            Invoices = LoadInvoices();
        }));
        Changed();
        return inv;
    }

    public void DeleteInvoice(Invoice inv)
    {
        Locked(() => Db.Transaction(() =>
        {
            Tombstone("invoices", inv.Id);
            Db.Run("DELETE FROM doc_lines WHERE doc_type='I' AND doc_id=?", inv.Id);
            Db.Run("DELETE FROM invoices WHERE id=?", inv.Id);
            Invoices = LoadInvoices();
        }));
        Changed();
    }

    public double Profit(IEnumerable<DocLine> lines) =>
        Fmt.R2(lines.Sum(l => ItemById(l.ItemId) is { } it ? (l.Rate - it.CostPrice) * l.Qty : 0));

    // MARK: Settings

    static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    CompanySettings LoadSettings()
    {
        var kv = Db.Query("SELECT * FROM settings").ToDictionary(r => r.Str("key"));
        var d = new CompanySettings();
        string Text(string k, string fallback) => kv.TryGetValue(k, out var r) ? r.Str("value") : fallback;
        double Number(string k, double fallback) => Fmt.Parse(Text(k, "")) ?? fallback;
        return new CompanySettings
        {
            CompanyName = Text("companyName", d.CompanyName), Tagline = Text("tagline", d.Tagline), Address = Text("address", d.Address),
            Phone = Text("phone", d.Phone), Email = Text("email", d.Email), Ntn = Text("ntn", d.Ntn), Strn = Text("strn", d.Strn),
            DefaultTaxRate = Number("defaultTaxRate", d.DefaultTaxRate), Currency = Text("currency", d.Currency),
            CurrencyWord = Text("currencyWord", d.CurrencyWord), QuotationPrefix = Text("quotationPrefix", d.QuotationPrefix),
            ChallanPrefix = Text("challanPrefix", d.ChallanPrefix), InvoicePrefix = Text("invoicePrefix", d.InvoicePrefix),
            InvoiceTitle = Text("invoiceTitle", d.InvoiceTitle), QuotationTerms = Text("quotationTerms", d.QuotationTerms),
            InvoiceTerms = Text("invoiceTerms", d.InvoiceTerms), LetterheadTop = Number("letterheadTop", d.LetterheadTop),
            LetterheadBottom = Number("letterheadBottom", d.LetterheadBottom),
            Logo = kv.TryGetValue("logo", out var lg) ? lg.Data("data") : null,
            Letterhead = kv.TryGetValue("letterhead", out var lh) ? lh.Data("data") : null,
        };
    }

    public void SaveSettings(CompanySettings s)
    {
        (string, string)[] values =
        {
            ("companyName", s.CompanyName), ("tagline", s.Tagline), ("address", s.Address), ("phone", s.Phone), ("email", s.Email),
            ("ntn", s.Ntn), ("strn", s.Strn), ("defaultTaxRate", Num(s.DefaultTaxRate)), ("currency", s.Currency),
            ("currencyWord", s.CurrencyWord), ("quotationPrefix", s.QuotationPrefix), ("challanPrefix", s.ChallanPrefix),
            ("invoicePrefix", s.InvoicePrefix), ("invoiceTitle", s.InvoiceTitle), ("quotationTerms", s.QuotationTerms),
            ("invoiceTerms", s.InvoiceTerms), ("letterheadTop", Num(s.LetterheadTop)), ("letterheadBottom", Num(s.LetterheadBottom)),
        };
        Locked(() => Db.Transaction(() =>
        {
            foreach (var (k, v) in values) Db.Run("INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)", k, v);
            Db.Run("INSERT OR REPLACE INTO settings (key, data) VALUES ('logo', ?)", s.Logo);
            Db.Run("INSERT OR REPLACE INTO settings (key, data) VALUES ('letterhead', ?)", s.Letterhead);
            SetSyncValue("settings_updated", Num(Fmt.Unix(DateTime.Now)));
            SetSyncValue("settings_dirty", "1");
            SetSyncValue("settings_lan_seq", NextLanSeq().ToString(CultureInfo.InvariantCulture));
            Settings = LoadSettings();
        }));
        Changed();
    }

    internal Dictionary<string, string> SettingsValues() =>
        Db.Query("SELECT key, value FROM settings WHERE value IS NOT NULL").ToDictionary(r => r.Str("key"), r => r.Str("value"));

    public string? SyncValue(string key) => Locked(() => Db.Query("SELECT value FROM sync_state WHERE key = ?", key).FirstOrDefault()?.Str("value"));

    public void SetSyncValue(string key, string? value) => Locked(() =>
    {
        if (value != null) Db.Run("INSERT OR REPLACE INTO sync_state (key, value) VALUES (?, ?)", key, value);
        else Db.Run("DELETE FROM sync_state WHERE key = ?", key);
    });

    public void ReloadAfterSync()
    {
        Locked(RecomputeStock);
        ReloadAll();
        DataChanged?.Invoke();
    }

    public void Backup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        Locked(() => Db.Run("VACUUM INTO ?", path));
    }
}
