using Microsoft.Data.Sqlite;
using Solo.Ai.Client;
using Solo.Ai.Storage;
using Xunit;
using static Solo.Ai.Tests.SqliteStorageFixture;

namespace Solo.Ai.Tests;

public sealed class SqliteStorageIntegrityTests
{
    [Fact]
    public void ApplyMigrationsIdempotentlyAndRejectFutureSchema()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        fixture.Database.Initialize();
        Assert.Equal((long)SqliteChatDatabase.SchemaVersion, fixture.Execute("PRAGMA user_version;"));
        Assert.Equal("wal", fixture.Execute("PRAGMA journal_mode;"));
        Assert.Equal(1L, fixture.Execute("PRAGMA foreign_keys;"));
        Assert.Equal(id, new SqliteChatStore(fixture.Reopen()).GetCurrentChat(Owner)!.ChatId);
        fixture.Execute("PRAGMA user_version=999;");
        Assert.Throws<InvalidOperationException>(() => fixture.Database.Initialize());
        Assert.Throws<InvalidOperationException>(() => fixture.Chats.GetCurrentChat(Owner));
        Assert.Equal(999L, fixture.Execute("PRAGMA user_version;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(":memory:")]
    [InlineData("file::memory:?cache=shared")]
    [InlineData("relative.db")]
    [InlineData("\\\\server\\share\\chat.db")]
    public void RejectMissingMemoryRelativeAndNetworkPaths(string path)
    {
        Assert.Throws<InvalidOperationException>(() => new SqliteChatDatabase(new() { Enabled = true, DatabasePath = path }));
    }

    [Fact]
    public void FailOnUnavailableDiskAndNeverRecreateAMissingDatabaseDuringUse()
    {
        using var fixture = new SqliteStorageFixture();
        var database = new SqliteChatDatabase(new()
        {
            Enabled = true, DatabasePath = Path.Combine(fixture.DatabasePath, "missing", "chat.db"),
        });
        Assert.Throws<SqliteException>(() => database.Initialize());
        File.Move(fixture.DatabasePath, fixture.DatabasePath + ".saved");
        Assert.Throws<SqliteException>(() => fixture.Chats.GetCurrentChat(Owner));
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public void DatabaseConstraintsEnforceOneChatForeignKeysMessageOrderAndOneActiveRun()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = fixture.Send(id);
        var ownerCollision = Assert.Throws<SqliteException>(() => fixture.Execute("""
            INSERT INTO Chat (Id, OwnerId, Title, CreatedAt) SELECT $id, OwnerId, Title, CreatedAt FROM Chat;
            """, ("$id", Guid.NewGuid())));
        Assert.Contains("Chat.OwnerId", ownerCollision.Message);
        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE Message SET OwnerId='other';"));
        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE GenerationRun SET UserMessageId=$id;", ("$id", Guid.NewGuid())));
        Assert.Throws<SqliteException>(() => fixture.Execute("""
            INSERT INTO Message (Id, ChatId, OwnerId, Sequence, Role, Text, CreatedAt)
            SELECT $id, ChatId, OwnerId, Sequence, Role, Text, CreatedAt FROM Message;
            """, ("$id", Guid.NewGuid())));
        var activeCollision = Assert.Throws<SqliteException>(() => fixture.Execute("""
            INSERT INTO GenerationRun (Id, OwnerId, ChatId, UserMessageId, RetryId, State, CreatedAt, DeadlineAt)
            SELECT $id, OwnerId, ChatId, UserMessageId, $retry, 'pending', CreatedAt, DeadlineAt FROM GenerationRun;
            """, ("$id", Guid.NewGuid()), ("$retry", Guid.NewGuid())));
        Assert.Contains("GenerationRun.ChatId", activeCollision.Message);
        Assert.Equal(run, fixture.Runs.GetRun(Owner, id, run.RunId));
        fixture.Chats.DeleteChat(Owner, id);
        Assert.Throws<SqliteException>(() => fixture.Execute("""
            INSERT INTO Chat (Id, OwnerId, Title, CreatedAt) VALUES ($id, 'owner', 'old', 1);
            """, ("$id", id)));
    }

    [Fact]
    public void FailedRunInsertRollsBackTheAcceptedMessageAndActivity()
    {
        using var fixture = new SqliteStorageFixture();
        var chat = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat;
        fixture.Execute("""
            CREATE TRIGGER FailRun BEFORE INSERT ON GenerationRun BEGIN SELECT RAISE(ABORT, 'injected'); END;
            """);
        Assert.Throws<SqliteException>(() => fixture.Send(chat.ChatId));
        Assert.Equal(chat, fixture.Chats.GetCurrentChat(Owner));
        Assert.Equal(0L, fixture.Execute("SELECT COUNT(*) FROM Message;"));
        Assert.Equal(0L, fixture.Execute("SELECT COUNT(*) FROM GenerationRun;"));
    }

    [Fact]
    public void DatabaseEnforcesOwnerScopedIdempotencyEvenForTerminalRuns()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var sent = fixture.Send(id);
        fixture.Fail(id, sent);
        Assert.Throws<SqliteException>(() => fixture.Execute("""
            INSERT INTO GenerationRun (Id, OwnerId, ChatId, UserMessageId, State, CreatedAt, DeadlineAt)
            SELECT $new, OwnerId, ChatId, UserMessageId, 'pending', CreatedAt, DeadlineAt FROM GenerationRun;
            """, ("$new", Guid.NewGuid())));
        var retried = fixture.Runs.RetryRun(Owner, id, sent.UserMessageId, new(2, Guid.NewGuid()));
        fixture.Fail(id, retried);
        Assert.Throws<SqliteException>(() => fixture.Execute("""
            INSERT INTO GenerationRun (Id, OwnerId, ChatId, UserMessageId, RetryId, State, CreatedAt, DeadlineAt)
            SELECT $new, OwnerId, ChatId, UserMessageId, RetryId, 'pending', CreatedAt, DeadlineAt
            FROM GenerationRun WHERE Id=$retry;
            """, ("$new", Guid.NewGuid()), ("$retry", retried.RunId)));
        Assert.Equal(2L, fixture.Execute("SELECT COUNT(*) FROM GenerationRun;"));
    }

    [Fact]
    public void FailedTombstoneWriteRollsBackDeletionAndPreservesHistory()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = fixture.Send(id);
        fixture.Execute("""
            CREATE TRIGGER FailDeletion BEFORE INSERT ON DeletedChat BEGIN SELECT RAISE(ABORT, 'injected'); END;
            """);
        Assert.Throws<SqliteException>(() => fixture.Chats.DeleteChat(Owner, id));
        Assert.Equal(id, fixture.Chats.GetCurrentChat(Owner)!.ChatId);
        Assert.Single(fixture.Chats.GetMessages(Owner, id).Messages);
        Assert.Equal(run, fixture.Runs.GetRun(Owner, id, run.RunId));
        Assert.Equal(0L, fixture.Execute("SELECT COUNT(*) FROM DeletedChat;"));
    }

    [Fact]
    public void FailedFinalizationRollsBackAssistantAndCanRetryOnlyTheDatabaseWrite()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = fixture.Send(id);
        fixture.Transitions.TryStartRun(Owner, id, run.RunId);
        var activity = fixture.Chats.GetChat(Owner, id).LastMessageAt;
        fixture.Execute("""
            CREATE TRIGGER FailFinish BEFORE UPDATE ON GenerationRun
            WHEN NEW.State='completed' BEGIN SELECT RAISE(ABORT, 'injected'); END;
            """);
        Assert.Throws<SqliteException>(() => fixture.Transitions.FinishRun(Owner, id, run.RunId, "completed", assistantText: "answer"));
        Assert.Equal("running", fixture.Runs.GetRun(Owner, id, run.RunId).State);
        Assert.Single(fixture.Chats.GetMessages(Owner, id).Messages);
        Assert.Equal(activity, fixture.Chats.GetChat(Owner, id).LastMessageAt);
        fixture.Execute("DROP TRIGGER FailFinish;");
        var completed = fixture.Transitions.FinishRun(Owner, id, run.RunId, "completed", assistantText: "answer");
        Assert.Equal(completed, fixture.Transitions.FinishRun(Owner, id, run.RunId, "completed", assistantText: "answer"));
        Assert.Equal(2, fixture.Chats.GetMessages(Owner, id).Messages.Count);
    }

    [Fact]
    public void ExpiredDeadlineRejectsDispatchAndLateSuccessfulText()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = fixture.Send(id);
        fixture.Execute("UPDATE GenerationRun SET DeadlineAt=CreatedAt+1;");
        Assert.False(fixture.Transitions.TryStartRun(Owner, id, run.RunId));
        fixture.Execute("UPDATE GenerationRun SET State='running', StartedAt=CreatedAt;");
        Assert.Equal("timed_out", fixture.Transitions.FinishRun(Owner, id, run.RunId, "completed", assistantText: "late").State);
        Assert.Single(fixture.Chats.GetMessages(Owner, id).Messages);
    }

    [Fact]
    public void RestoringOldSnapshotAlsoRestoresOldTombstonesAsDocumented()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        fixture.Send(id, "snapshot text");
        var backupPath = fixture.DatabasePath + ".backup";
        using (var source = fixture.OpenConnection())
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }
        fixture.Chats.DeleteChat(Owner, id);
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
        using (var target = fixture.OpenConnection())
        {
            source.Open();
            source.BackupDatabase(target);
        }
        Assert.Equal(id, fixture.Chats.GetCurrentChat(Owner)!.ChatId);
        Assert.Equal("snapshot text", Assert.Single(fixture.Chats.GetMessages(Owner, id).Messages).Text);
        Assert.Equal(0L, fixture.Execute("SELECT COUNT(*) FROM DeletedChat;"));
    }
}
