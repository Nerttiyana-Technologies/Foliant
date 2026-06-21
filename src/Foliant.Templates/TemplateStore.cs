using System.Text.Json;
using System.Text.Json.Serialization;
using Foliant;
using Microsoft.Data.Sqlite;

namespace Foliant.Templates;

/// <summary>
/// SQLite-backed store of registered form templates. Each <see cref="FormLayout"/> is persisted with its
/// geometric coordinates (serialized to JSON) plus a fingerprint column used to recognize uploads. Embedded
/// and portable — the database is a single file that ships with the product; no server.
/// </summary>
public sealed class TemplateStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },   // FormElementKind as "Checkbox"/"Text"
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteConnection _connection;

    /// <param name="databasePath">Path to the SQLite file (created if absent). Use ":memory:" for tests.</param>
    public TemplateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        // Pooling=false so disposing the store releases the file handle immediately — otherwise the pooled
        // connection keeps the .db open and a later delete/replace fails on Windows (file-locked).
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS templates (" +
            "  template_id TEXT PRIMARY KEY," +
            "  name        TEXT NOT NULL," +
            "  fingerprint TEXT," +
            "  layout_json TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts or replaces a template, keyed by <see cref="FormLayout.TemplateId"/>.</summary>
    public void Save(FormLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT OR REPLACE INTO templates (template_id, name, fingerprint, layout_json) " +
            "VALUES ($id, $name, $fp, $json);";
        cmd.Parameters.AddWithValue("$id", layout.TemplateId);
        cmd.Parameters.AddWithValue("$name", layout.Name);
        cmd.Parameters.AddWithValue("$fp", (object?)layout.Fingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(layout, Json));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Loads a template by id, or null when absent.</summary>
    public FormLayout? Get(string templateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT layout_json FROM templates WHERE template_id = $id;";
        cmd.Parameters.AddWithValue("$id", templateId);
        return cmd.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<FormLayout>(json, Json)
            : null;
    }

    /// <summary>All registered templates — the candidate set an upload is matched against.</summary>
    public IReadOnlyList<FormLayout> All()
    {
        var result = new List<FormLayout>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT layout_json FROM templates;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (JsonSerializer.Deserialize<FormLayout>(reader.GetString(0), Json) is { } layout)
                result.Add(layout);
        return result;
    }

    /// <summary>Removes a template by id; true when a row was deleted.</summary>
    public bool Delete(string templateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM templates WHERE template_id = $id;";
        cmd.Parameters.AddWithValue("$id", templateId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void Dispose() => _connection.Dispose();
}
