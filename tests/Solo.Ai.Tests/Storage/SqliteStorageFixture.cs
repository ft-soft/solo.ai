using Microsoft.Data.Sqlite;
using Solo.Ai.Client;
using Solo.Ai.Storage;
using Xunit;

namespace Solo.Ai.Tests;

internal sealed class SqliteStorageFixture : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "storage-tests", Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(directory, "chat.db");
    public SqliteChatDatabase Database { get; }
    public SqliteChatStore Chats { get; }
    public SqliteRunStore Runs { get; }
    public SqliteRunTransitions Transitions { get; }
    public const string Owner = "synthetic-alice";
    public const string OtherOwner = "synthetic-bob";

    public SqliteStorageFixture()
    {
        Directory.CreateDirectory(directory);
        Database = Reopen();
        Database.Initialize();
        Chats = new(Database);
        Runs = new(Database);
        Transitions = new(Database);
    }

    public SqliteChatDatabase Reopen() => new(new SqliteChatOptions { Enabled = true, DatabasePath = DatabasePath });

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, Pooling = false, Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        return connection;
    }

    public object? Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    public SoloAiRun Send(Guid chatId, string text = "hello") => Runs.SendMessage(Owner, chatId, new(2, Guid.NewGuid(), text));

    public SoloAiRun Fail(Guid chatId, SoloAiRun run, string code = "provider_error") =>
        Transitions.FinishRun(Owner, chatId, run.RunId, "failed", code);

    public SoloAiRun Complete(Guid chatId, SoloAiRun run, string text = "answer")
    {
        Assert.True(Transitions.TryStartRun(Owner, chatId, run.RunId));
        return Transitions.FinishRun(Owner, chatId, run.RunId, "completed", assistantText: text);
    }

    public static void AssertError(string code, Action action) => Assert.Equal(code, Assert.Throws<SoloAiExchangeException>(action).Code);

    public static async Task<T[]> Race<T>(Func<T> first, Func<T> second)
    {
        using var barrier = new Barrier(2);
        return await Task.WhenAll(new[] { first, second }.Select(action => Task.Factory.StartNew(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
            return action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(directory);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "storage-tests")) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
        Directory.Delete(path, recursive: true);
    }
}
