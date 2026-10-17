using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PocketDesk;

internal static class DbConvert
{
    public static string Date(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTime Date(object value) => value switch
    {
        DateTime dt => dt,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
        _ => DateTime.Now,
    };

    public static DateTime? NullableDate(object value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        var parsed = Date(value);
        return parsed;
    }

    public static string? Text(object value) => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
}

/// <summary>收纳盒持久化。</summary>
public static class BoxRepository
{
    private const string Columns =
        "id, name, type, target_path, appearance_json, window_json, z_order, is_pinned, is_visible, " +
        "sort_field, sort_desc, created_at";

    public static List<Box> GetAll()
    {
        var result = new List<Box>();
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM boxes ORDER BY z_order ASC, created_at ASC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public static Box? Get(string id)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM boxes WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static void Insert(Box box)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO boxes (id, name, type, target_path, appearance_json, window_json, z_order,
                               is_pinned, is_visible, sort_field, sort_desc, created_at, updated_at)
            VALUES ($id, $name, $type, $target, $appearance, $window, $z, $pinned, $visible,
                    $sort, $desc, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", box.Id);
        command.Parameters.AddWithValue("$name", box.Name);
        command.Parameters.AddWithValue("$type", (int)box.Type);
        command.Parameters.AddWithValue("$target", (object?)box.TargetPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$appearance", Json.Serialize(box.Appearance));
        command.Parameters.AddWithValue("$window", (object?)Json.Serialize(box.Placement) ?? DBNull.Value);
        command.Parameters.AddWithValue("$z", box.ZOrder);
        command.Parameters.AddWithValue("$pinned", box.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$visible", box.IsVisible ? 1 : 0);
        command.Parameters.AddWithValue("$sort", box.SortField);
        command.Parameters.AddWithValue("$desc", box.SortDescending ? 1 : 0);
        command.Parameters.AddWithValue("$created", DbConvert.Date(box.CreatedAt));
        command.Parameters.AddWithValue("$updated", DbConvert.Date(DateTime.Now));
        command.ExecuteNonQuery();
    }

    public static void Update(Box box)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE boxes SET name = $name, type = $type, target_path = $target,
                             appearance_json = $appearance, window_json = $window,
                             z_order = $z, is_pinned = $pinned, is_visible = $visible,
                             sort_field = $sort, sort_desc = $desc, updated_at = $updated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", box.Id);
        command.Parameters.AddWithValue("$name", box.Name);
        command.Parameters.AddWithValue("$type", (int)box.Type);
        command.Parameters.AddWithValue("$target", (object?)box.TargetPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$appearance", Json.Serialize(box.Appearance));
        command.Parameters.AddWithValue("$window", (object?)Json.Serialize(box.Placement) ?? DBNull.Value);
        command.Parameters.AddWithValue("$z", box.ZOrder);
        command.Parameters.AddWithValue("$pinned", box.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$visible", box.IsVisible ? 1 : 0);
        command.Parameters.AddWithValue("$sort", box.SortField);
        command.Parameters.AddWithValue("$desc", box.SortDescending ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DbConvert.Date(DateTime.Now));
        command.ExecuteNonQuery();
    }

    public static void Delete(string id)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM boxes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public static void SetName(string id, string name)
    {
        Execute("UPDATE boxes SET name = $v, updated_at = $u WHERE id = $id;",
            ("$id", id), ("$v", name), ("$u", DbConvert.Date(DateTime.Now)));
    }

    public static void SetVisibility(string id, bool visible)
    {
        Execute("UPDATE boxes SET is_visible = $v, updated_at = $u WHERE id = $id;",
            ("$id", id), ("$v", visible ? 1 : 0), ("$u", DbConvert.Date(DateTime.Now)));
    }

    public static void SetPinned(string id, bool pinned)
    {
        Execute("UPDATE boxes SET is_pinned = $v, updated_at = $u WHERE id = $id;",
            ("$id", id), ("$v", pinned ? 1 : 0), ("$u", DbConvert.Date(DateTime.Now)));
    }

    public static void SetSort(string id, string field, bool descending)
    {
        Execute("UPDATE boxes SET sort_field = $f, sort_desc = $d, updated_at = $u WHERE id = $id;",
            ("$id", id), ("$f", field), ("$d", descending ? 1 : 0), ("$u", DbConvert.Date(DateTime.Now)));
    }

    public static void SetPlacement(string id, WindowPlacement? placement)
    {
        Execute("UPDATE boxes SET window_json = $w, updated_at = $u WHERE id = $id;",
            ("$id", id),
            ("$w", (object?)Json.Serialize(placement) ?? DBNull.Value),
            ("$u", DbConvert.Date(DateTime.Now)));
    }

    public static void SetAppearance(string id, BoxAppearance appearance)
    {
        Execute("UPDATE boxes SET appearance_json = $a, updated_at = $u WHERE id = $id;",
            ("$id", id), ("$a", Json.Serialize(appearance)), ("$u", DbConvert.Date(DateTime.Now)));
    }

    public static int NextZOrder()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(z_order), -1) + 1 FROM boxes;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static void Reorder(IReadOnlyList<string> orderedIds)
    {
        using var connection = Database.Open();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE boxes SET z_order = $z WHERE id = $id;";
            command.Parameters.AddWithValue("$z", i);
            command.Parameters.AddWithValue("$id", orderedIds[i]);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static Box Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Type = (BoxType)reader.GetInt32(2),
        TargetPath = reader.IsDBNull(3) ? null : reader.GetString(3),
        Appearance = Json.Deserialize<BoxAppearance>(reader.IsDBNull(4) ? null : reader.GetString(4)) ?? new BoxAppearance(),
        Placement = reader.IsDBNull(5) ? null : Json.Deserialize<WindowPlacement>(reader.GetString(5)),
        ZOrder = reader.GetInt32(6),
        IsPinned = reader.GetInt32(7) != 0,
        IsVisible = reader.GetInt32(8) != 0,
        SortField = reader.GetString(9),
        SortDescending = reader.GetInt32(10) != 0,
        CreatedAt = DbConvert.Date(reader.GetValue(11)),
    };
}

/// <summary>收纳项持久化。</summary>
public static class ItemRepository
{
    private const string Columns =
        "id, box_id, kind, display_name, source_path, stored_path, icon_cache_key, sort_index, " +
        "added_at, original_box_id, is_link, modified_at, size_bytes, shortcut_target";

    public static List<BoxItem> GetByBox(string boxId)
    {
        var result = new List<BoxItem>();
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM items WHERE box_id = $box ORDER BY added_at ASC;";
        command.Parameters.AddWithValue("$box", boxId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public static BoxItem? Get(string id)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM items WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static void Insert(BoxItem item)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items (id, box_id, kind, display_name, source_path, stored_path, icon_cache_key,
                               sort_index, added_at, original_box_id, is_link, modified_at, size_bytes, shortcut_target)
            VALUES ($id, $box, $kind, $name, $source, $stored, $icon, $sort, $added, $original,
                    $link, $modified, $size, $shortcut);
            """;
        AddParameters(command, item);
        command.ExecuteNonQuery();
    }

    public static void Update(BoxItem item)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET box_id = $box, kind = $kind, display_name = $name, source_path = $source,
                             stored_path = $stored, icon_cache_key = $icon, sort_index = $sort,
                             added_at = $added, original_box_id = $original, is_link = $link,
                             modified_at = $modified, size_bytes = $size, shortcut_target = $shortcut
            WHERE id = $id;
            """;
        AddParameters(command, item);
        command.ExecuteNonQuery();
    }

    public static void Delete(string id)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public static void DeleteByBox(string boxId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM items WHERE box_id = $box;";
        command.Parameters.AddWithValue("$box", boxId);
        command.ExecuteNonQuery();
    }

    public static void MoveToBox(IReadOnlyCollection<string> itemIds, string boxId)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        using var connection = Database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in itemIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE items SET box_id = $box WHERE id = $id;";
            command.Parameters.AddWithValue("$box", boxId);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static int CountByBox(string boxId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE box_id = $box;";
        command.Parameters.AddWithValue("$box", boxId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static Dictionary<string, int> CountAll()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT box_id, COUNT(*) FROM items GROUP BY box_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    public static double MaxSortIndex(string boxId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sort_index), 0) FROM items WHERE box_id = $box;";
        command.Parameters.AddWithValue("$box", boxId);
        return Convert.ToDouble(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddParameters(SqliteCommand command, BoxItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$box", item.BoxId);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$name", item.DisplayName);
        command.Parameters.AddWithValue("$source", (object?)item.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$stored", (object?)item.StoredPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$icon", (object?)item.IconCacheKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$sort", item.SortIndex);
        command.Parameters.AddWithValue("$added", DbConvert.Date(item.AddedAt));
        command.Parameters.AddWithValue("$original", (object?)item.OriginalBoxId ?? DBNull.Value);
        command.Parameters.AddWithValue("$link", item.IsLink ? 1 : 0);
        command.Parameters.AddWithValue("$modified", item.ModifiedAt is { } m ? DbConvert.Date(m) : DBNull.Value);
        command.Parameters.AddWithValue("$size", item.Size);
        command.Parameters.AddWithValue("$shortcut", (object?)item.ShortcutTarget ?? DBNull.Value);
    }

    private static BoxItem Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        BoxId = reader.GetString(1),
        Kind = (ItemKind)reader.GetInt32(2),
        DisplayName = reader.GetString(3),
        SourcePath = reader.IsDBNull(4) ? null : reader.GetString(4),
        StoredPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        IconCacheKey = reader.IsDBNull(6) ? null : reader.GetString(6),
        SortIndex = reader.GetDouble(7),
        AddedAt = DbConvert.Date(reader.GetValue(8)),
        OriginalBoxId = reader.IsDBNull(9) ? null : reader.GetString(9),
        IsLink = reader.GetInt32(10) != 0,
        ModifiedAt = reader.IsDBNull(11) ? null : DbConvert.Date(reader.GetValue(11)),
        Size = reader.GetInt64(12),
        ShortcutTarget = reader.IsDBNull(13) ? null : reader.GetString(13),
    };
}

/// <summary>journal 持久化。</summary>
public static class JournalRepository
{
    public static void Add(JournalEntry entry)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO journal (id, op_type, box_id, item_id, from_path, to_path, state, created_at, completed_at)
            VALUES ($id, $op, $box, $item, $from, $to, $state, $created, $completed);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$op", (int)entry.OpType);
        command.Parameters.AddWithValue("$box", (object?)entry.BoxId ?? DBNull.Value);
        command.Parameters.AddWithValue("$item", (object?)entry.ItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)entry.FromPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)entry.ToPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$created", DbConvert.Date(entry.CreatedAt));
        command.Parameters.AddWithValue("$completed", entry.CompletedAt is { } c ? DbConvert.Date(c) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public static void SetState(string id, JournalState state)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE journal SET state = $state, completed_at = $completed WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$completed", DbConvert.Date(DateTime.Now));
        command.ExecuteNonQuery();
    }

    public static List<JournalEntry> GetPending()
    {
        var result = new List<JournalEntry>();
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, op_type, box_id, item_id, from_path, to_path, state, created_at, completed_at
            FROM journal WHERE state = 0 ORDER BY created_at ASC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new JournalEntry
            {
                Id = reader.GetString(0),
                OpType = (JournalOpType)reader.GetInt32(1),
                BoxId = reader.IsDBNull(2) ? null : reader.GetString(2),
                ItemId = reader.IsDBNull(3) ? null : reader.GetString(3),
                FromPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                ToPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                State = (JournalState)reader.GetInt32(6),
                CreatedAt = DbConvert.Date(reader.GetValue(7)),
                CompletedAt = reader.IsDBNull(8) ? null : DbConvert.Date(reader.GetValue(8)),
            });
        }

        return result;
    }

    public static void Cleanup(TimeSpan retention)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM journal WHERE state <> 0 AND completed_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", DbConvert.Date(DateTime.Now - retention));
        command.ExecuteNonQuery();
    }
}

/// <summary>键值设置持久化。</summary>
public static class SettingsRepository
{
    public static string? Get(string key)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public static void Set(string key, string value)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
