-- ponytail: tombstones roll back with backups; use an independent log only if restore must retain later deletions.
CREATE TABLE DeletedChat (
    ChatId TEXT PRIMARY KEY NOT NULL,
    DeletedAt INTEGER NOT NULL
) STRICT;

CREATE TABLE Chat (
    Id TEXT PRIMARY KEY NOT NULL,
    OwnerId TEXT NOT NULL UNIQUE CHECK (length(OwnerId) > 0),
    Title TEXT NOT NULL CHECK (length(CAST(Title AS BLOB)) BETWEEN 1 AND 512),
    CreatedAt INTEGER NOT NULL,
    LastMessageAt INTEGER CHECK (LastMessageAt >= CreatedAt),
    UNIQUE (OwnerId, Id)
) STRICT;

CREATE TABLE Message (
    Id TEXT NOT NULL,
    ChatId TEXT NOT NULL,
    OwnerId TEXT NOT NULL,
    Sequence INTEGER NOT NULL CHECK (Sequence > 0),
    Role TEXT NOT NULL CHECK (Role IN ('user', 'assistant')),
    Text TEXT NOT NULL CHECK (length(CAST(Text AS BLOB)) BETWEEN 1 AND 65536),
    CreatedAt INTEGER NOT NULL,
    PRIMARY KEY (OwnerId, Id),
    UNIQUE (ChatId, Sequence),
    UNIQUE (OwnerId, ChatId, Id),
    FOREIGN KEY (OwnerId, ChatId) REFERENCES Chat (OwnerId, Id) ON DELETE CASCADE
) STRICT;

CREATE TABLE GenerationRun (
    Number INTEGER PRIMARY KEY AUTOINCREMENT,
    Id TEXT NOT NULL UNIQUE,
    OwnerId TEXT NOT NULL,
    ChatId TEXT NOT NULL,
    UserMessageId TEXT NOT NULL,
    RetryId TEXT,
    State TEXT NOT NULL CHECK (State IN ('pending', 'running', 'cancellation_requested',
        'completed', 'failed', 'timed_out', 'incomplete', 'cancelled', 'interrupted')),
    CreatedAt INTEGER NOT NULL,
    StartedAt INTEGER CHECK (StartedAt >= CreatedAt),
    FinishedAt INTEGER CHECK (FinishedAt >= COALESCE(StartedAt, CreatedAt)),
    DeadlineAt INTEGER NOT NULL CHECK (DeadlineAt > CreatedAt),
    OutcomeCode TEXT,
    AssistantMessageId TEXT CHECK (AssistantMessageId != UserMessageId),
    FOREIGN KEY (OwnerId, ChatId) REFERENCES Chat (OwnerId, Id) ON DELETE CASCADE,
    FOREIGN KEY (OwnerId, ChatId, UserMessageId) REFERENCES Message (OwnerId, ChatId, Id) ON DELETE CASCADE,
    FOREIGN KEY (OwnerId, ChatId, AssistantMessageId) REFERENCES Message (OwnerId, ChatId, Id) DEFERRABLE INITIALLY DEFERRED,
    CHECK ((State IN ('pending', 'running', 'cancellation_requested')) = (FinishedAt IS NULL)),
    CHECK ((State = 'completed') = (AssistantMessageId IS NOT NULL)),
    CHECK (State != 'pending' OR StartedAt IS NULL),
    CHECK (State NOT IN ('running', 'completed', 'incomplete', 'interrupted') OR StartedAt IS NOT NULL),
    CHECK (
        (State IN ('pending', 'running', 'cancellation_requested', 'completed') AND OutcomeCode IS NULL) OR
        (State = 'failed' AND OutcomeCode IS NOT NULL AND OutcomeCode IN ('configuration_failure',
            'dependency_authentication_failed', 'dependency_unavailable', 'provider_error',
            'transport_error', 'limit_exceeded', 'malformed_model_response')) OR
        (State IN ('timed_out', 'cancelled', 'interrupted') AND OutcomeCode IS NOT NULL AND OutcomeCode = State) OR
        (State = 'incomplete' AND OutcomeCode IS NOT NULL AND OutcomeCode = 'incomplete_response')
    )
) STRICT;

CREATE UNIQUE INDEX OneActiveRun ON GenerationRun (ChatId)
    WHERE State IN ('pending', 'running', 'cancellation_requested');
CREATE UNIQUE INDEX OriginalSend ON GenerationRun (OwnerId, UserMessageId) WHERE RetryId IS NULL;
CREATE UNIQUE INDEX RetryOperation ON GenerationRun (OwnerId, RetryId) WHERE RetryId IS NOT NULL;
CREATE UNIQUE INDEX AssistantRun ON GenerationRun (OwnerId, AssistantMessageId) WHERE AssistantMessageId IS NOT NULL;
CREATE INDEX ChatRuns ON GenerationRun (OwnerId, ChatId, Number);
CREATE INDEX MessageRuns ON GenerationRun (OwnerId, ChatId, UserMessageId, Number);

CREATE TRIGGER RejectDeletedChat BEFORE INSERT ON Chat
WHEN EXISTS (SELECT 1 FROM DeletedChat WHERE ChatId = NEW.Id)
BEGIN SELECT RAISE(ABORT, 'deleted_chat'); END;

CREATE TRIGGER RecordDeletedChat BEFORE DELETE ON Chat
BEGIN
    -- Server UTC in .NET ticks, matching the application's timestamps.
    INSERT INTO DeletedChat (ChatId, DeletedAt)
    VALUES (OLD.Id, CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER))
    ON CONFLICT (ChatId) DO NOTHING;
END;
