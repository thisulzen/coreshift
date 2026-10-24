using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace CoreShift.Migrator;

static class Db
{
    // The tool logs in as coreshift, which can read legacy but not change it (db/03-access.sql). The password
    // comes from the environment, or from the .env file db/load-legacy.sh writes when it creates the login.
    public static SqlConnection Open()
    {
        var password = Environment.GetEnvironmentVariable("CORESHIFT_PASSWORD") ?? FromDotEnv("CORESHIFT_PASSWORD")
            ?? throw new InvalidOperationException("No CORESHIFT_PASSWORD found. Run db/load-legacy.sh first; it creates the login and .env.");
        var db = new SqlConnection($"Server=localhost,1433;Database=CoreShift;User Id=coreshift;Password={password};TrustServerCertificate=True");
        try { db.Open(); }
        catch (SqlException e)
        {
            throw new InvalidOperationException($"Can't connect to SQL Server on localhost:1433 (if it isn't running: `docker compose up -d`). {e.Message}", e);
        }
        return db;
    }

    static string? FromDotEnv(string key)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".env");
            if (File.Exists(file))
                return File.ReadLines(file).Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == key)?[1].Trim();
        }
        return null;
    }

    public static SqlCommand Cmd(SqlConnection db, string sql, SqlTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = new SqlCommand(sql, db, tx) { CommandTimeout = 900 };
        foreach (var (name, value) in args)
        {
            // Every text column here is VARCHAR or CHAR. Sent as NVARCHAR, AddWithValue's default for strings,
            // SQL Server converts the column instead, can't seek its index, and every cohort scanned whole tables.
            var p = cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            if (value is string) p.SqlDbType = SqlDbType.VarChar;
        }
        return cmd;
    }

    // Runs a script split on GO lines, the way sqlcmd does
    public static void RunScript(SqlConnection db, string sql)
    {
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) Cmd(db, batch).ExecuteNonQuery();
    }

    public static string Resource(string name)
    {
        using var stream = typeof(Db).Assembly.GetManifestResourceStream(name)!;
        return new StreamReader(stream).ReadToEnd();
    }

    // An empty DataTable shaped like a real table: types, lengths and nullability come from SQL Server
    public static DataTable Shape(SqlConnection db, string table, SqlTransaction tx)
    {
        var shape = new DataTable();
        using var adapter = new SqlDataAdapter(Cmd(db, $"SELECT TOP 0 * FROM {table}", tx));
        adapter.FillSchema(shape, SchemaType.Source);
        return shape;
    }

    // CheckConstraints, for the same reason as the legacy load: without it bulk copy skips foreign keys
    public static void BulkCopy(SqlConnection db, string table, DataTable rows, SqlTransaction tx)
    {
        using var bulk = new SqlBulkCopy(db, SqlBulkCopyOptions.CheckConstraints, tx) { DestinationTableName = table, BulkCopyTimeout = 900 };
        foreach (DataColumn c in rows.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        bulk.WriteToServer(rows);
    }
}
