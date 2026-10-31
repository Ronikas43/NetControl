using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>SQLite persistence for devices, history, security events and schedules.</summary>
public class Database
{
    private readonly string _connString;

    public Database(string dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NetControl");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "netcontrol.db");
        }
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS devices (
    mac TEXT PRIMARY KEY,
    ip TEXT, hostname TEXT, vendor TEXT, category INTEGER DEFAULT 0,
    custom_name TEXT, trusted INTEGER DEFAULT 0, blocked INTEGER DEFAULT 0,
    bandwidth_limited INTEGER DEFAULT 0, notes TEXT DEFAULT '',
    first_seen TEXT, last_seen TEXT, online INTEGER DEFAULT 0,
    is_local INTEGER DEFAULT 0, is_gateway INTEGER DEFAULT 0, known INTEGER DEFAULT 0,
    os_guess TEXT DEFAULT '', model TEXT DEFAULT '', confidence INTEGER DEFAULT 0, sources TEXT DEFAULT ''
);
CREATE TABLE IF NOT EXISTS history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    mac TEXT, ip TEXT, type TEXT, message TEXT
);
CREATE TABLE IF NOT EXISTS security_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    severity TEXT, type TEXT, mac TEXT, ip TEXT, message TEXT
);
CREATE TABLE IF NOT EXISTS schedules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT, mac TEXT, action TEXT, days TEXT,
    start_minutes INTEGER, end_minutes INTEGER,
    enabled INTEGER DEFAULT 1, currently_applied INTEGER DEFAULT 0, last_result TEXT DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_history_ts ON history(timestamp);
CREATE INDEX IF NOT EXISTS idx_history_mac ON history(mac);
CREATE INDEX IF NOT EXISTS idx_sec_ts ON security_events(timestamp);
";
        cmd.ExecuteNonQuery();
        Migrate(c);
    }

    /// <summary>Adds columns that older databases don't have yet (idempotent).</summary>
    private static void Migrate(SqliteConnection c)
    {
        string[] statements =
        {
            "ALTER TABLE devices ADD COLUMN os_guess TEXT DEFAULT ''",
            "ALTER TABLE devices ADD COLUMN model TEXT DEFAULT ''",
            "ALTER TABLE devices ADD COLUMN confidence INTEGER DEFAULT 0",
            "ALTER TABLE devices ADD COLUMN sources TEXT DEFAULT ''"
        };
        foreach (var sql in statements)
        {
            try
            {
                using var m = c.CreateCommand();
                m.CommandText = sql;
                m.ExecuteNonQuery();
            }
            catch (SqliteException) { /* column already exists */ }
        }
    }

    public int Execute(string sql, Action<SqliteCommand> bind = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        return cmd.ExecuteNonQuery();
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, Action<SqliteCommand> bind = null)
    {
        var list = new List<T>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public long InsertAndGetId(string sql, Action<SqliteCommand> bind = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql + "; SELECT last_insert_rowid();";
        bind?.Invoke(cmd);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---------- Devices ----------

    public void UpsertDevice(Device d)
    {
        Execute(@"INSERT INTO devices(mac, ip, hostname, vendor, category, custom_name, trusted, blocked,
                      bandwidth_limited, notes, first_seen, last_seen, online, is_local, is_gateway, known,
                      os_guess, model, confidence, sources)
                  VALUES(@mac,@ip,@hn,@vendor,@cat,@cname,@trusted,@blocked,@bw,@notes,@fs,@ls,@on,@local,@gw,@known,@os,@model,@conf,@sources)
                  ON CONFLICT(mac) DO UPDATE SET
                      ip=@ip, hostname=CASE WHEN @hn<>'' THEN @hn ELSE hostname END,
                      vendor=CASE WHEN @vendor<>'' THEN @vendor ELSE vendor END,
                      category=@cat, custom_name=@cname, trusted=@trusted, blocked=@blocked,
                      bandwidth_limited=@bw, notes=@notes, last_seen=@ls, online=@on,
                      is_local=@local, is_gateway=@gw, known=@known,
                      os_guess=@os, model=CASE WHEN @model<>'' THEN @model ELSE model END,
                      confidence=@conf, sources=CASE WHEN @sources<>'' THEN @sources ELSE sources END",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@mac", d.Mac);
                cmd.Parameters.AddWithValue("@ip", d.Ip);
                cmd.Parameters.AddWithValue("@hn", d.Hostname ?? "");
                cmd.Parameters.AddWithValue("@vendor", d.Vendor ?? "");
                cmd.Parameters.AddWithValue("@cat", (int)d.Category);
                cmd.Parameters.AddWithValue("@cname", d.CustomName ?? "");
                cmd.Parameters.AddWithValue("@trusted", d.Trusted ? 1 : 0);
                cmd.Parameters.AddWithValue("@blocked", d.Blocked ? 1 : 0);
                cmd.Parameters.AddWithValue("@bw", d.BandwidthLimited ? 1 : 0);
                cmd.Parameters.AddWithValue("@notes", d.Notes ?? "");
                cmd.Parameters.AddWithValue("@fs", d.FirstSeen.ToString("O"));
                cmd.Parameters.AddWithValue("@ls", d.LastSeen.ToString("O"));
                cmd.Parameters.AddWithValue("@on", d.Online ? 1 : 0);
                cmd.Parameters.AddWithValue("@local", d.IsLocal ? 1 : 0);
                cmd.Parameters.AddWithValue("@gw", d.IsGateway ? 1 : 0);
                cmd.Parameters.AddWithValue("@known", d.Known ? 1 : 0);
                cmd.Parameters.AddWithValue("@os", d.OsGuess ?? "");
                cmd.Parameters.AddWithValue("@model", d.Model ?? "");
                cmd.Parameters.AddWithValue("@conf", d.Confidence);
                cmd.Parameters.AddWithValue("@sources", d.Sources ?? "");
            });
    }

    public List<Device> LoadDevices()
    {
        return Query("SELECT * FROM devices", r => new Device
        {
            Mac = r.GetString(0),
            Ip = r.IsDBNull(1) ? "" : r.GetString(1),
            Hostname = r.IsDBNull(2) ? "" : r.GetString(2),
            Vendor = r.IsDBNull(3) ? "" : r.GetString(3),
            Category = (DeviceCategory)r.GetInt32(4),
            CustomName = r.IsDBNull(5) ? "" : r.GetString(5),
            Trusted = r.GetInt32(6) != 0,
            Blocked = r.GetInt32(7) != 0,
            BandwidthLimited = r.GetInt32(8) != 0,
            Notes = r.IsDBNull(9) ? "" : r.GetString(9),
            FirstSeen = ParseDate(r.IsDBNull(10) ? null : r.GetString(10)),
            LastSeen = ParseDate(r.IsDBNull(11) ? null : r.GetString(11)),
            Online = r.GetInt32(12) != 0,
            IsLocal = r.GetInt32(13) != 0,
            IsGateway = r.GetInt32(14) != 0,
            Known = r.GetInt32(15) != 0,
            OsGuess = r.FieldCount > 16 && !r.IsDBNull(16) ? r.GetString(16) : "",
            Model = r.FieldCount > 17 && !r.IsDBNull(17) ? r.GetString(17) : "",
            Confidence = r.FieldCount > 18 && !r.IsDBNull(18) ? r.GetInt32(18) : 0,
            Sources = r.FieldCount > 19 && !r.IsDBNull(19) ? r.GetString(19) : ""
        });
    }

    private static DateTime ParseDate(string s) =>
        string.IsNullOrEmpty(s) ? DateTime.Now : (DateTime.TryParse(s, out var d) ? d : DateTime.Now);

    // ---------- History ----------

    public void AddHistory(HistoryEvent e)
    {
        Execute(@"INSERT INTO history(timestamp, mac, ip, type, message) VALUES(@ts,@mac,@ip,@type,@msg)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@ts", e.Timestamp.ToString("O"));
                cmd.Parameters.AddWithValue("@mac", e.Mac ?? "");
                cmd.Parameters.AddWithValue("@ip", e.Ip ?? "");
                cmd.Parameters.AddWithValue("@type", e.Type ?? "");
                cmd.Parameters.AddWithValue("@msg", e.Message ?? "");
            });
    }

    public List<HistoryEvent> QueryHistory(string searchText, string type, string mac, DateTime? from, DateTime? to, int limit = 500)
    {
        var where = new List<string>();
        var binds = new List<SqliteParameter>();
        // Built inline below with parameters only.
        return QueryHistoryFiltered(searchText, type, mac, from, to, limit);
    }

    private List<HistoryEvent> QueryHistoryFiltered(string searchText, string type, string mac, DateTime? from, DateTime? to, int limit)
    {
        var conds = new List<string>();
        if (!string.IsNullOrWhiteSpace(searchText)) conds.Add("(message LIKE @q OR mac LIKE @q OR ip LIKE @q)");
        if (!string.IsNullOrWhiteSpace(type) && type != "All") conds.Add("type = @type");
        if (!string.IsNullOrWhiteSpace(mac) && mac != "*") conds.Add("mac = @mac");
        if (from.HasValue) conds.Add("timestamp >= @from");
        if (to.HasValue) conds.Add("timestamp <= @to");
        string whereSql = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";

        return Query($"SELECT * FROM history {whereSql} ORDER BY timestamp DESC LIMIT {limit}", r => new HistoryEvent
        {
            Id = r.GetInt64(0),
            Timestamp = ParseDate(r.GetString(1)),
            Mac = r.IsDBNull(2) ? "" : r.GetString(2),
            Ip = r.IsDBNull(3) ? "" : r.GetString(3),
            Type = r.IsDBNull(4) ? "" : r.GetString(4),
            Message = r.IsDBNull(5) ? "" : r.GetString(5)
        }, cmd =>
        {
            if (!string.IsNullOrWhiteSpace(searchText)) cmd.Parameters.AddWithValue("@q", $"%{searchText}%");
            if (!string.IsNullOrWhiteSpace(type) && type != "All") cmd.Parameters.AddWithValue("@type", type);
            if (!string.IsNullOrWhiteSpace(mac) && mac != "*") cmd.Parameters.AddWithValue("@mac", mac);
            if (from.HasValue) cmd.Parameters.AddWithValue("@from", from.Value.ToString("O"));
            if (to.HasValue) cmd.Parameters.AddWithValue("@to", to.Value.ToString("O"));
        });
    }

    public void ClearHistory()
    {
        Execute("DELETE FROM history");
        Execute("DELETE FROM security_events");
    }

    public void PruneHistory(int retentionDays)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays).ToString("O");
        Execute("DELETE FROM history WHERE timestamp < @c", c => c.Parameters.AddWithValue("@c", cutoff));
        Execute("DELETE FROM security_events WHERE timestamp < @c", c => c.Parameters.AddWithValue("@c", cutoff));
    }

    public List<HistoryEvent> LoadRecentHistory(int limit)
    {
        return Query($"SELECT * FROM history ORDER BY timestamp DESC LIMIT {limit}", r => new HistoryEvent
        {
            Id = r.GetInt64(0),
            Timestamp = ParseDate(r.GetString(1)),
            Mac = r.IsDBNull(2) ? "" : r.GetString(2),
            Ip = r.IsDBNull(3) ? "" : r.GetString(3),
            Type = r.IsDBNull(4) ? "" : r.GetString(4),
            Message = r.IsDBNull(5) ? "" : r.GetString(5)
        });
    }

    // ---------- Security events ----------

    public void AddSecurityEvent(SecurityEvent e)
    {
        Execute(@"INSERT INTO security_events(timestamp, severity, type, mac, ip, message)
                  VALUES(@ts,@sev,@type,@mac,@ip,@msg)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@ts", e.Timestamp.ToString("O"));
                cmd.Parameters.AddWithValue("@sev", e.Severity ?? "Info");
                cmd.Parameters.AddWithValue("@type", e.Type ?? "");
                cmd.Parameters.AddWithValue("@mac", e.Mac ?? "");
                cmd.Parameters.AddWithValue("@ip", e.Ip ?? "");
                cmd.Parameters.AddWithValue("@msg", e.Message ?? "");
            });
    }

    public List<SecurityEvent> LoadSecurityEvents(int limit = 300)
    {
        return Query($"SELECT * FROM security_events ORDER BY timestamp DESC LIMIT {limit}", r => new SecurityEvent
        {
            Id = r.GetInt64(0),
            Timestamp = ParseDate(r.GetString(1)),
            Severity = r.IsDBNull(2) ? "Info" : r.GetString(2),
            Type = r.IsDBNull(3) ? "" : r.GetString(3),
            Mac = r.IsDBNull(4) ? "" : r.GetString(4),
            Ip = r.IsDBNull(5) ? "" : r.GetString(5),
            Message = r.IsDBNull(6) ? "" : r.GetString(6)
        });
    }

    // ---------- Schedules ----------

    public long AddSchedule(Schedule s)
    {
        return InsertAndGetId(@"INSERT INTO schedules(name, mac, action, days, start_minutes, end_minutes, enabled, currently_applied, last_result)
                                VALUES(@name,@mac,@action,@days,@sm,@em,@en,@ap,@lr)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@name", s.Name);
                cmd.Parameters.AddWithValue("@mac", s.Mac);
                cmd.Parameters.AddWithValue("@action", s.Action);
                cmd.Parameters.AddWithValue("@days", string.Join("", Array.ConvertAll(s.Days, d => d ? "1" : "0")));
                cmd.Parameters.AddWithValue("@sm", s.StartMinutes);
                cmd.Parameters.AddWithValue("@em", s.EndMinutes);
                cmd.Parameters.AddWithValue("@en", s.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("@ap", 0);
                cmd.Parameters.AddWithValue("@lr", "");
            });
    }

    public void UpdateSchedule(Schedule s)
    {
        Execute(@"UPDATE schedules SET name=@name, mac=@mac, action=@action, days=@days,
                  start_minutes=@sm, end_minutes=@em, enabled=@en, currently_applied=@ap, last_result=@lr
                  WHERE id=@id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@id", s.Id);
                cmd.Parameters.AddWithValue("@name", s.Name);
                cmd.Parameters.AddWithValue("@mac", s.Mac);
                cmd.Parameters.AddWithValue("@action", s.Action);
                cmd.Parameters.AddWithValue("@days", string.Join("", Array.ConvertAll(s.Days, d => d ? "1" : "0")));
                cmd.Parameters.AddWithValue("@sm", s.StartMinutes);
                cmd.Parameters.AddWithValue("@em", s.EndMinutes);
                cmd.Parameters.AddWithValue("@en", s.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("@ap", s.CurrentlyApplied ? 1 : 0);
                cmd.Parameters.AddWithValue("@lr", s.LastResult ?? "");
            });
    }

    public void DeleteSchedule(long id) => Execute("DELETE FROM schedules WHERE id=@id",
        c => c.Parameters.AddWithValue("@id", id));

    public List<Schedule> LoadSchedules()
    {
        return Query("SELECT id, name, mac, action, days, start_minutes, end_minutes, enabled, currently_applied, last_result FROM schedules ORDER BY id", r =>
        {
            var daysStr = r.IsDBNull(4) ? "0000000" : r.GetString(4);
            var days = new bool[7];
            for (int i = 0; i < 7 && i < daysStr.Length; i++) days[i] = daysStr[i] == '1';
            return new Schedule
            {
                Id = r.GetInt64(0),
                Name = r.IsDBNull(1) ? "" : r.GetString(1),
                Mac = r.IsDBNull(2) ? "" : r.GetString(2),
                Action = r.IsDBNull(3) || r.GetString(3) != "Allow" ? "Block" : "Allow",
                Days = days,
                StartMinutes = r.GetInt32(5),
                EndMinutes = r.GetInt32(6),
                Enabled = r.GetInt32(7) != 0,
                CurrentlyApplied = r.GetInt32(8) != 0,
                LastResult = r.IsDBNull(9) ? "" : r.GetString(9)
            };
        });
    }
}
