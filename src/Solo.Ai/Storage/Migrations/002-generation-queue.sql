-- Poll active states without scanning the entire retained run history.
CREATE INDEX GenerationQueue ON GenerationRun (State, Number);
