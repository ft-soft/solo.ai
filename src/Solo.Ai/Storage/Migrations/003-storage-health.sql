CREATE TABLE StorageHealth (
    Id INTEGER PRIMARY KEY CHECK (Id=1),
    Value INTEGER NOT NULL CHECK (Value IN (0, 1))
) STRICT;
INSERT INTO StorageHealth (Id, Value) VALUES (1, 0);
