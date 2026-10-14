-- WorkUp schema, SRS v3.0 (T23, RF-10/RF-17).
-- The outbox dispatcher needs three columns on Notifications, and every one of them is nullable so the seven producers
-- that predate T23 keep working untouched: they omit the columns, SQL leaves them NULL, and the effective due instant
-- of such a row is then its CreatedAt.
--   NextAttemptAt: earliest instant a retry may be attempted. NULL means the effective due instant is CreatedAt.
--   ClaimedAt:    when the current claim was taken. NULL unless the status is Dispatching or ReviewRequired.
--   DispatchKey:  claim token of the current dispatch. Every completion must match it, so a stale completion of a claim
--                 that was taken over writes nothing instead of overwriting the current owner.
-- ClaimedAt and DispatchKey are also the traceability a human needs to resolve a notice left Dispatching or moved to
-- ReviewRequired after an uncertain send; nothing reclaims those rows automatically.
ALTER TABLE Notifications ADD NextAttemptAt DATETIME2(0) NULL;
GO
ALTER TABLE Notifications ADD ClaimedAt DATETIME2(0) NULL;
GO
ALTER TABLE Notifications ADD DispatchKey UNIQUEIDENTIFIER NULL;
GO

-- Due scan of the dispatcher: it filters Status and orders by COALESCE(NextAttemptAt, CreatedAt), Id. The index keeps
-- the rows of one status contiguous and carries both candidate order keys plus the id tie-break. It is deliberately
-- not filtered on Status = 1: a filter would still be maintained on every status transition, and an unfiltered index
-- also serves the status queries of the T26 outbox viewer with no extra write path.
CREATE INDEX IX_Notifications_Due ON Notifications (Status, NextAttemptAt, CreatedAt, Id);
GO
