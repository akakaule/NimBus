IF OBJECT_ID(N'dbo.FailureClassifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FailureClassifications
    (
        FailureMessageId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        StateJson nvarchar(max) NOT NULL CHECK (ISJSON(StateJson) = 1),
        Version rowversion NOT NULL
    );
END;
