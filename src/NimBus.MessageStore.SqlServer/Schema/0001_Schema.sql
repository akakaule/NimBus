IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = '$schema$')
BEGIN
    EXEC('CREATE SCHEMA [$schema$]');
END
GO
