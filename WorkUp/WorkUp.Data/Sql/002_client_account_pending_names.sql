-- WorkUp schema, SRS v3.0 (T20, RF-25, D-18).
-- A self-registered account lives as *pending* until its email is verified: it has no ClientId yet, so it cannot
-- create a visible ficha. The two nullable columns keep the names of the future ficha in the meantime, which is why
-- they are cleared when the verification consumes the link and the ficha is created.
-- Both columns stay NULL for every account that does not come from self-registration.
ALTER TABLE ClientAccounts ADD PendingFirstName NVARCHAR(60) NULL;
GO
ALTER TABLE ClientAccounts ADD PendingLastName NVARCHAR(60) NULL;
GO
