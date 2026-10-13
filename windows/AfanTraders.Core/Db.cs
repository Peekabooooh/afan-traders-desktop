using Microsoft.Data.Sqlite;

namespace AfanTraders.Core;

/// <summary>One result row. Values are long, double, string or byte[].</summary>
public sealed class Row
{
    readonly Dictionary<string, object?> cols;
    public Row(Dictionary<string, object?> cols) => this.cols = cols;

    object? Get(string k) => cols.TryGetValue(k, out var v) ? v : null;
    public bool Has(string k) => Get(k) != null;
    public string Str(string k) => Get(k) as string ?? "";
    public double Dbl(string k) => Get(k) switch { double d => d, long l => l, _ => 0 };
    public long Int(string k) => Get(k) switch { long l => l, double d => (long)d, _ => 0 };
    public long? OptInt(string k) => Has(k) ? Int(k) : null;
    public byte[]? Data(string k) => Get(k) as byte[] is { Length: > 0 } b ? b : null;
    public DateTime Date(string k) => Fmt.FromUnix(Dbl(k));
    public DateTime? OptDate(string k) => Has(k) ? Date(k) : null;
}

/// <summary>Thin SQLite wrapper. Dates are stored as Unix seconds, like the Mac app.</summary>
public sealed class Db : IDisposable
{
    readonly SqliteConnection conn;

    public Db(string path)
    {
        conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        Exec("PRAGMA foreign_keys = ON;");
        Exec("PRAGMA journal_mode = WAL;");
    }

    public void Dispose() => conn.Dispose();

    public void Exec(string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    SqliteCommand Prepare(string sql, object?[] args)
    {
        var cmd = conn.CreateCommand();
        // Positional "?" parameters (like the Mac app) become @p0, @p1, … (no SQL here has "?" inside quotes).
        var sb = new System.Text.StringBuilder(sql.Length + args.Length * 3);
        int next = 0;
        foreach (var ch in sql) { if (ch == '?') sb.Append("@p").Append(next++); else sb.Append(ch); }
        cmd.CommandText = sb.ToString();
        for (int i = 0; i < args.Length; i++)
        {
            object value = args[i] switch
            {
                null => DBNull.Value,
                bool b => b ? 1L : 0L,
                int n => (long)n,
                DateTime d => Fmt.Unix(d),
                byte[] { Length: 0 } => DBNull.Value,
                var v => v,
            };
            cmd.Parameters.Add(new SqliteParameter("@p" + i, value));
        }
        return cmd;
    }

    /// <summary>Runs a statement; returns the last inserted row id.</summary>
    public long Run(string sql, params object?[] args)
    {
        using var cmd = Prepare(sql, args);
        cmd.ExecuteNonQuery();
        using var id = conn.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid()";
        return (long)(id.ExecuteScalar() ?? 0L);
    }

    public List<Row> Query(string sql, params object?[] args)
    {
        using var cmd = Prepare(sql, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
        {
            var d = new Dictionary<string, object?>(r.FieldCount);
            for (int i = 0; i < r.FieldCount; i++)
                d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(new Row(d));
        }
        return rows;
    }

    public void Transaction(Action body)
    {
        Exec("BEGIN IMMEDIATE;");
        try { body(); Exec("COMMIT;"); }
        catch { try { Exec("ROLLBACK;"); } catch { } throw; }
    }
}
