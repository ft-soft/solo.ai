using Microsoft.Data.Sqlite;

namespace Solo.Ai.Storage;

/// <summary>One local file, explicit startup migrations, fresh connections for short transactions.</summary>
public sealed class SqliteChatDatabase
{
    private static readonly string[] Migrations = ["001-initial.sql", "002-generation-queue.sql", "003-storage-health.sql"];
    private readonly string connectionString;
    public TimeSpan RunTimeout { get; }
    public static int SchemaVersion => Migrations.Length;

    // Held by the host until disposal, including worker shutdown. Never delete a live lock file.
    public FileStream AcquireHostLock() => new(new SqliteConnectionStringBuilder(connectionString).DataSource + ".lock",
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public void CheckHealth()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A real committed write detects read-only/full storage; SELECT 1 cannot do that.
        command.CommandText = "UPDATE StorageHealth SET Value=1-Value WHERE Id=1;";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Invalid storage health record.");
        transaction.Commit();
    }

    public SqliteChatDatabase(SqliteChatOptions options)
    {
        if (!options.IsValid()) throw new InvalidOperationException("Invalid SoloAiStorage configuration.");
        RunTimeout = options.RunTimeout;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(options.DatabasePath),
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = options.BusyTimeoutSeconds,
        }.ToString();
    }

    // Called before readiness/worker. Missing directories and disk errors fail startup.
    public void Initialize()
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadWriteCreate };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite WAL is required.");
        command.CommandText = "PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction(deferred: false);
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version < 0 || version > SchemaVersion)
            throw new InvalidOperationException("Unsupported SQLite schema version.");
        for (var index = version; index < Migrations.Length; index++)
        {
            using var stream = typeof(SqliteChatDatabase).Assembly.GetManifestResourceStream(
                $"Solo.Ai.Storage.Migrations.{Migrations[index]}")!;
            using var reader = new StreamReader(stream);
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            command.CommandText = $"PRAGMA user_version={index + 1};";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(command.ExecuteScalar()) != SchemaVersion)
                throw new InvalidOperationException("SQLite migrations are required before use.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
