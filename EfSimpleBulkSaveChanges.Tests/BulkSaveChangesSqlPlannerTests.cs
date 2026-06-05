using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Microsoft.Extensions.Logging;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
public sealed class BulkSaveChangesSqlPlannerTests
{
    [TestMethod]
    public void CreatePlan_BuildsMultiRowInsertWithReturningGeneratedKey()
    {
        using var db = CreateContext();
        db.Users.AddRange(
            new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" },
            new User { FirstName = "Jane", LastName = "Smith", Email = "jane.smith@example.com" });

        var plan = CreatePlan(db);
        var command = AssertSingleCommand(plan);

        AssertSql(
            """
            INSERT INTO "users" ("email", "first_name", "last_name")
            VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)
            RETURNING "id";
            """,
            command.CommandText);
        CollectionAssert.AreEqual(
            new object?[] { "john.doe@example.com", "John", "Doe", "jane.smith@example.com", "Jane", "Smith" },
            command.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.AreEqual(2, command.GeneratedValueAssignments.Count);
        Assert.AreEqual("Id", command.GeneratedValueAssignments[0].Property.Name);
    }

    [TestMethod]
    public void CreatePlan_BuildsDerivedTableUpdateForModifiedRowsAndWritesAllUpdatableColumns()
    {
        using var db = CreateContext();
        var john = new User { Id = 1, FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        var jane = new User { Id = 2, FirstName = "Jane", LastName = "Smith", Email = "jane.smith@example.com" };
        db.AttachRange(john, jane);

        john.FirstName = "Johnny";
        jane.Email = "jane.updated@example.com";

        var plan = CreatePlan(db);
        var command = AssertSingleCommand(plan);

        AssertSql(
            """
            WITH source("id", "email", "first_name", "last_name") AS (
              VALUES (@p0, @p1, @p2, @p3), (@p4, @p5, @p6, @p7)
            )
            UPDATE "users" AS target SET
              "email" = source."email",
              "first_name" = source."first_name",
              "last_name" = source."last_name"
            FROM source
            WHERE target."id" = source."id";
            """,
            command.CommandText);
        CollectionAssert.AreEqual(
            new object?[] { 1, "john.doe@example.com", "Johnny", "Doe", 2, "jane.updated@example.com", "Jane", "Smith" },
            command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [TestMethod]
    public void CreatePlan_BuildsDeleteWithKeyInList()
    {
        using var db = CreateContext();
        var john = new User { Id = 1, FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        var jane = new User { Id = 2, FirstName = "Jane", LastName = "Smith", Email = "jane.smith@example.com" };
        db.AttachRange(john, jane);
        db.RemoveRange(john, jane);

        var plan = CreatePlan(db);
        var command = AssertSingleCommand(plan);

        AssertSql(
            """
            DELETE FROM "users" WHERE "id" IN (@p0, @p1);
            """,
            command.CommandText);
        CollectionAssert.AreEqual(new object?[] { 1, 2 }, command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [TestMethod]
    public void CreatePlan_BuildsInsertWithoutReturningForExplicitKeys()
    {
        var options = new DbContextOptionsBuilder<ExplicitKeyContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new ExplicitKeyContext(options);
        db.ExplicitKeyUsers.Add(new ExplicitKeyUser { Id = 100, Name = "John" });

        var command = AssertSingleCommand(CreatePlan(db));

        AssertSql(
            """
            INSERT INTO "explicit_key_users" ("id", "name")
            VALUES (@p0, @p1);
            """,
            command.CommandText);
        CollectionAssert.AreEqual(new object?[] { 100, "John" }, command.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.AreEqual(0, command.GeneratedValueAssignments.Count);
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_InsertsAddAndAddRangeRowsWithClientGeneratedOnAddKeys()
    {
        using var db = CreateHiLoContext();
        var singleUser = new HiLoUser { Name = "Single" };
        var rangeUsers = new[]
        {
            new HiLoUser { Name = "Range 1" },
            new HiLoUser { Name = "Range 2" }
        };
        db.Users.Add(singleUser);
        db.Users.AddRange(rangeUsers);

        var savedCount = await db.BulkSaveChangesAsync(
            _ => { },
            (_, plan, _) =>
            {
                var command = AssertSingleCommand(plan);
                AssertSql(
                    """
                    INSERT INTO "hilo_users" ("id", "name")
                    VALUES (@p0, @p1), (@p2, @p3), (@p4, @p5);
                    """,
                    command.CommandText);
                CollectionAssert.AreEqual(
                    new object?[] { singleUser.Id, "Single", rangeUsers[0].Id, "Range 1", rangeUsers[1].Id, "Range 2" },
                    command.Parameters.Select(parameter => parameter.Value).ToArray());
                Assert.AreEqual(0, command.GeneratedValueAssignments.Count);
                return Task.CompletedTask;
            });

        Assert.AreEqual(3, savedCount);
        Assert.AreNotEqual(0, singleUser.Id);
        Assert.IsTrue(rangeUsers.All(user => user.Id != 0));
        Assert.AreEqual(EntityState.Unchanged, db.Entry(singleUser).State);
        Assert.IsTrue(rangeUsers.All(user => db.Entry(user).State == EntityState.Unchanged));
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_UpdatesAddAndAddRangeEntitiesWithClientGeneratedOnAddKeys()
    {
        using var db = CreateHiLoContext();
        var singleUser = new HiLoUser { Name = "Single" };
        var rangeUsers = new[]
        {
            new HiLoUser { Name = "Range 1" },
            new HiLoUser { Name = "Range 2" }
        };
        db.Users.Add(singleUser);
        db.Users.AddRange(rangeUsers);
        db.ChangeTracker.AcceptAllChanges();

        singleUser.Name = "Single updated";
        rangeUsers[0].Name = "Range 1 updated";
        rangeUsers[1].Name = "Range 2 updated";

        var savedCount = await db.BulkSaveChangesAsync(
            _ => { },
            (_, plan, _) =>
            {
                var command = AssertSingleCommand(plan);
                AssertSql(
                    """
                    WITH source("id", "name") AS (
                      VALUES (@p0, @p1), (@p2, @p3), (@p4, @p5)
                    )
                    UPDATE "hilo_users" AS target SET
                      "name" = source."name"
                    FROM source
                    WHERE target."id" = source."id";
                    """,
                    command.CommandText);
                CollectionAssert.AreEqual(
                    new object?[]
                    {
                        singleUser.Id,
                        "Single updated",
                        rangeUsers[0].Id,
                        "Range 1 updated",
                        rangeUsers[1].Id,
                        "Range 2 updated"
                    },
                    command.Parameters.Select(parameter => parameter.Value).ToArray());
                return Task.CompletedTask;
            });

        Assert.AreEqual(3, savedCount);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(singleUser).State);
        Assert.IsTrue(rangeUsers.All(user => db.Entry(user).State == EntityState.Unchanged));
    }

    [TestMethod]
    public void CreatePlan_AppliesValueConvertersToParameters()
    {
        var options = new DbContextOptionsBuilder<ValueConverterContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new ValueConverterContext(options);
        db.Users.Add(new ConvertedUser { Id = 1, Status = UserStatus.Active });

        var command = AssertSingleCommand(CreatePlan(db));

        AssertSql(
            """
            INSERT INTO "converted_users" ("id", "status")
            VALUES (@p0, @p1);
            """,
            command.CommandText);
        CollectionAssert.AreEqual(new object?[] { 1, "active" }, command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [TestMethod]
    public void CreatePlan_AppliesValueConvertersToUpdateParameters()
    {
        var options = new DbContextOptionsBuilder<ValueConverterContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new ValueConverterContext(options);
        var user = new ConvertedUser { Id = 1, Status = UserStatus.Inactive };
        db.Attach(user);
        user.Status = UserStatus.Active;

        var command = AssertSingleCommand(CreatePlan(db));

        AssertSql(
            """
            WITH source("id", "status") AS (
              VALUES (@p0, @p1)
            )
            UPDATE "converted_users" AS target SET
              "status" = source."status"
            FROM source
            WHERE target."id" = source."id";
            """,
            command.CommandText);
        CollectionAssert.AreEqual(new object?[] { 1, "active" }, command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [TestMethod]
    public void CreatePlan_BuildsInsertUpdateAndDeleteCommandsForMixedChanges()
    {
        using var db = CreateContext();
        var updated = new User { Id = 10, FirstName = "Alice", LastName = "Jones", Email = "alice@example.com" };
        var deleted = new User { Id = 11, FirstName = "Bob", LastName = "Stone", Email = "bob@example.com" };
        db.AttachRange(updated, deleted);
        updated.LastName = "Johnson";
        db.Remove(deleted);
        db.Users.Add(new User { FirstName = "New", LastName = "User", Email = "new@example.com" });

        var plan = CreatePlan(db);

        Assert.AreEqual(3, plan.Commands.Count);
        AssertSql(
            """
            INSERT INTO "users" ("email", "first_name", "last_name")
            VALUES (@p0, @p1, @p2)
            RETURNING "id";
            """,
            plan.Commands[0].CommandText);
        AssertSql(
            """
            WITH source("id", "email", "first_name", "last_name") AS (
              VALUES (@p0, @p1, @p2, @p3)
            )
            UPDATE "users" AS target SET
              "email" = source."email",
              "first_name" = source."first_name",
              "last_name" = source."last_name"
            FROM source
            WHERE target."id" = source."id";
            """,
            plan.Commands[1].CommandText);
        AssertSql(
            """
            DELETE FROM "users" WHERE "id" IN (@p0);
            """,
            plan.Commands[2].CommandText);
        CollectionAssert.AreEqual(
            new object?[] { "new@example.com", "New", "User" },
            plan.Commands[0].Parameters.Select(parameter => parameter.Value).ToArray());
        CollectionAssert.AreEqual(
            new object?[] { 10, "alice@example.com", "Alice", "Johnson" },
            plan.Commands[1].Parameters.Select(parameter => parameter.Value).ToArray());
        CollectionAssert.AreEqual(
            new object?[] { 11 },
            plan.Commands[2].Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [TestMethod]
    public void CreatePlan_SplitsCommandsByBatchSize()
    {
        using var db = CreateContext();
        db.Users.AddRange(
            new User { FirstName = "One", LastName = "A", Email = "one@example.com" },
            new User { FirstName = "Two", LastName = "B", Email = "two@example.com" },
            new User { FirstName = "Three", LastName = "C", Email = "three@example.com" });

        var plan = CreatePlan(db, batchSize: 2);

        Assert.AreEqual(2, plan.Commands.Count);
        AssertSql(
            """
            INSERT INTO "users" ("email", "first_name", "last_name")
            VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)
            RETURNING "id";
            """,
            plan.Commands[0].CommandText);
        AssertSql(
            """
            INSERT INTO "users" ("email", "first_name", "last_name")
            VALUES (@p0, @p1, @p2)
            RETURNING "id";
            """,
            plan.Commands[1].CommandText);
        CollectionAssert.AreEqual(
            new object?[] { "one@example.com", "One", "A", "two@example.com", "Two", "B" },
            plan.Commands[0].Parameters.Select(parameter => parameter.Value).ToArray());
        CollectionAssert.AreEqual(
            new object?[] { "three@example.com", "Three", "C" },
            plan.Commands[1].Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [TestMethod]
    public void CreatePlan_SplitsInsertCommandsToStayUnderNpgsqlParameterLimit()
    {
        using var db = CreateContext();
        for (var index = 1; index <= 21_846; index++)
        {
            db.Users.Add(new User { FirstName = $"First {index}", LastName = $"Last {index}", Email = $"user-{index}@example.com" });
        }

        var plan = CreatePlan(db, batchSize: 30_000);

        Assert.AreEqual(2, plan.Commands.Count);
        Assert.AreEqual(65_535, plan.Commands[0].Parameters.Count);
        Assert.AreEqual(3, plan.Commands[1].Parameters.Count);
        Assert.IsTrue(plan.Commands.All(command => command.Parameters.Count <= 65_535));
    }

    [TestMethod]
    public void CreatePlan_SplitsUpdateCommandsToStayUnderNpgsqlParameterLimit()
    {
        using var db = CreateContext();
        for (var index = 1; index <= 16_384; index++)
        {
            var user = new User { Id = index, FirstName = $"First {index}", LastName = $"Last {index}", Email = $"user-{index}@example.com" };
            db.Attach(user);
            user.FirstName = $"Updated {index}";
        }

        var plan = CreatePlan(db, batchSize: 30_000);

        Assert.AreEqual(2, plan.Commands.Count);
        Assert.AreEqual(65_532, plan.Commands[0].Parameters.Count);
        Assert.AreEqual(4, plan.Commands[1].Parameters.Count);
        Assert.IsTrue(plan.Commands.All(command => command.Parameters.Count <= 65_535));
    }

    [TestMethod]
    public void CreatePlan_SplitsDeleteCommandsToStayUnderNpgsqlParameterLimit()
    {
        using var db = CreateContext();
        for (var index = 1; index <= 65_536; index++)
        {
            var user = new User { Id = index, FirstName = $"First {index}", LastName = $"Last {index}", Email = $"user-{index}@example.com" };
            db.Attach(user);
            db.Remove(user);
        }

        var plan = CreatePlan(db, batchSize: 70_000);

        Assert.AreEqual(2, plan.Commands.Count);
        Assert.AreEqual(65_535, plan.Commands[0].Parameters.Count);
        Assert.AreEqual(1, plan.Commands[1].Parameters.Count);
        Assert.IsTrue(plan.Commands.All(command => command.Parameters.Count <= 65_535));
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_LogsWarningWhenRequestedBatchSizeExceedsNpgsqlParameterLimit()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        using var db = CreateContext(loggerFactory);
        for (var index = 1; index <= 21_846; index++)
        {
            db.Users.Add(new User { FirstName = $"First {index}", LastName = $"Last {index}", Email = $"user-{index}@example.com" });
        }

        await db.BulkSaveChangesAsync(
            options => options.BatchSize = 30_000,
            (_, plan, _) =>
            {
                Assert.AreEqual(2, plan.Commands.Count);
                return Task.CompletedTask;
            });

        var warning = loggerProvider.Messages.Single(message => message.LogLevel == LogLevel.Warning);
        Assert.IsTrue(warning.Message.Contains("requested batch size 30000", StringComparison.Ordinal));
        Assert.IsTrue(warning.Message.Contains("effective batch size of 21845", StringComparison.Ordinal));
        Assert.IsTrue(warning.Message.Contains("Set BatchSize to 21845 or lower", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_DoesNotLogWarningWhenRequestedBatchSizeIsWithinNpgsqlParameterLimit()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        using var db = CreateContext(loggerFactory);
        for (var index = 1; index <= 100; index++)
        {
            db.Users.Add(new User { FirstName = $"First {index}", LastName = $"Last {index}", Email = $"user-{index}@example.com" });
        }

        await db.BulkSaveChangesAsync(
            options => options.BatchSize = 100,
            (_, _, _) => Task.CompletedTask);

        Assert.IsFalse(loggerProvider.Messages.Any(message => message.LogLevel == LogLevel.Warning));
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_ReturnsZeroAndDoesNotExecuteWhenNothingChanged()
    {
        using var db = CreateContext();
        var executed = false;

        var savedCount = await db.BulkSaveChangesAsync(
            _ => { },
            (_, _, _) =>
            {
                executed = true;
                return Task.CompletedTask;
            });

        Assert.AreEqual(0, savedCount);
        Assert.IsFalse(executed);
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_BatchSizeOverloadControlsBatchSplitting()
    {
        using var db = CreateContext();
        db.Users.AddRange(
            new User { FirstName = "One", LastName = "A", Email = "one@example.com" },
            new User { FirstName = "Two", LastName = "B", Email = "two@example.com" },
            new User { FirstName = "Three", LastName = "C", Email = "three@example.com" });

        var savedCount = await db.BulkSaveChangesAsync(
            options => options.BatchSize = 2,
            (_, plan, _) =>
            {
                Assert.AreEqual(2, plan.Commands.Count);
                return Task.CompletedTask;
            });

        Assert.AreEqual(3, savedCount);
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_ThrowsForInvalidBatchSize()
    {
        using var db = CreateContext();
        db.Users.Add(new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" });

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => db.BulkSaveChangesAsync(batchSize: 0));
    }

    [TestMethod]
    public void CreatePlan_UsesDbNullEquivalentParameterForNullValues()
    {
        using var db = CreateContext();
        db.Users.Add(new User { FirstName = "John", LastName = "Doe", Email = null });

        var command = AssertSingleCommand(CreatePlan(db));

        Assert.IsNull(command.Parameters[0].Value);
    }

    [TestMethod]
    public void CreatePlan_KeepsMaliciousLookingValuesAsParameters()
    {
        using var db = CreateContext();
        var maliciousValue = "x'); DROP TABLE users; --";
        db.Users.Add(new User { FirstName = maliciousValue, LastName = "Doe", Email = "john.doe@example.com" });

        var command = AssertSingleCommand(CreatePlan(db));

        AssertSql(
            """
            INSERT INTO "users" ("email", "first_name", "last_name")
            VALUES (@p0, @p1, @p2)
            RETURNING "id";
            """,
            command.CommandText);
        CollectionAssert.AreEqual(
            new object?[] { "john.doe@example.com", maliciousValue, "Doe" },
            command.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.IsFalse(command.CommandText.Contains(maliciousValue, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreatePlan_QuotesSchemaAndTableNames()
    {
        var options = new DbContextOptionsBuilder<SchemaContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new SchemaContext(options);
        db.Users.Add(new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" });

        var command = AssertSingleCommand(CreatePlan(db));

        AssertSql(
            """
            INSERT INTO "app"."users" ("email", "first_name", "last_name")
            VALUES (@p0, @p1, @p2)
            RETURNING "id";
            """,
            command.CommandText);
    }

    [TestMethod]
    public void CreatePlan_EscapesQuotesInSchemaTableAndColumnIdentifiers()
    {
        var options = new DbContextOptionsBuilder<QuotedIdentifierContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new QuotedIdentifierContext(options);
        db.Users.Add(new QuotedIdentifierUser { Name = "John" });

        var command = AssertSingleCommand(CreatePlan(db));

        AssertSql(
            """
            INSERT INTO "app""schema"."users""table" ("display""name")
            VALUES (@p0)
            RETURNING "id""column";
            """,
            command.CommandText);
        CollectionAssert.AreEqual(new object?[] { "John" }, command.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_ThrowsForNonRelationalProviderAndKeepsState()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase($"bulk-{Guid.NewGuid()}")
            .Options;

        await using var db = new TestDbContext(options);
        var user = new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        db.Users.Add(user);

        await Assert.ThrowsExceptionAsync<NotSupportedException>(() => db.BulkSaveChangesAsync());
        Assert.AreEqual(EntityState.Added, db.Entry(user).State);
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_AcceptsChangesAndPopulatesGeneratedIdsAfterSuccess()
    {
        using var db = CreateContext();
        var user = new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        db.Users.Add(user);

        var savedCount = await db.BulkSaveChangesAsync(
            _ => { },
            (_, plan, _) =>
            {
                var assignment = plan.Commands.Single().GeneratedValueAssignments.Single();
                assignment.Entry.Property(assignment.Property).CurrentValue = 42;
                return Task.CompletedTask;
            });

        Assert.AreEqual(1, savedCount);
        Assert.AreEqual(42, user.Id);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(user).State);
    }

    [TestMethod]
    public async Task BulkSaveChangesAsync_KeepsStatesWhenExecutionFails()
    {
        using var db = CreateContext();
        var user = new User { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        db.Users.Add(user);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => db.BulkSaveChangesAsync(
            _ => { },
            (_, _, _) => throw new InvalidOperationException("boom")));

        Assert.AreEqual(EntityState.Added, db.Entry(user).State);
    }

    [TestMethod]
    public void CreatePlan_ThrowsForCompositeKeys()
    {
        var options = new DbContextOptionsBuilder<CompositeKeyContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new CompositeKeyContext(options);
        db.CompositeKeyEntities.Add(new CompositeKeyEntity { TenantId = 1, Code = "A", Name = "Alpha" });

        var exception = Assert.ThrowsException<NotSupportedException>(() => CreatePlan(db));
        Assert.AreEqual("Entity type 'CompositeKeyEntity' must have a single-column primary key.", exception.Message);
    }

    [TestMethod]
    public void CreatePlan_ThrowsForConcurrencyTokens()
    {
        var options = new DbContextOptionsBuilder<ConcurrencyTokenContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new ConcurrencyTokenContext(options);
        db.Users.Add(new VersionedUser { Name = "John", Version = 1 });

        var exception = Assert.ThrowsException<NotSupportedException>(() => CreatePlan(db));
        Assert.AreEqual(
            "Entity type 'VersionedUser' has concurrency tokens. Concurrency tokens are not supported by BulkSaveChangesAsync.",
            exception.Message);
    }

    [TestMethod]
    public void CreatePlan_ThrowsForOwnedTypes()
    {
        var options = new DbContextOptionsBuilder<OwnedTypeContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new OwnedTypeContext(options);
        var user = new UserWithAddress { Name = "John", Address = new Address { City = "Austin" } };
        db.Users.Add(user);
        db.Entry(user.Address).State = EntityState.Added;

        var exception = Assert.ThrowsException<NotSupportedException>(() => CreatePlan(db));
        Assert.AreEqual(
            "Entity type 'Address' is owned. Owned entity types are not supported by BulkSaveChangesAsync.",
            exception.Message);
    }

    [TestMethod]
    public void CreatePlan_ThrowsForInheritanceMappings()
    {
        var options = new DbContextOptionsBuilder<InheritanceContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new InheritanceContext(options);
        db.People.Add(new Employee { Name = "John", Department = "Engineering" });

        var exception = Assert.ThrowsException<NotSupportedException>(() => CreatePlan(db));
        Assert.AreEqual(
            "Entity type 'Employee' uses inheritance. Inheritance mappings are not supported by BulkSaveChangesAsync.",
            exception.Message);
    }

    [TestMethod]
    public void CreatePlan_ThrowsForShadowPrimaryKeys()
    {
        var options = new DbContextOptionsBuilder<ShadowKeyContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new ShadowKeyContext(options);
        var user = new ShadowKeyUser { Name = "John" };
        db.Users.Add(user);
        db.Entry(user).Property("Id").CurrentValue = 1;

        var exception = Assert.ThrowsException<NotSupportedException>(() => CreatePlan(db));
        Assert.AreEqual(
            "Entity type 'ShadowKeyUser' uses a shadow primary key. Shadow keys are not supported by BulkSaveChangesAsync.",
            exception.Message);
    }

    [TestMethod]
    public void CreatePlan_ThrowsForEntitiesNotMappedToTable()
    {
        var options = new DbContextOptionsBuilder<KeylessViewContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        using var db = new KeylessViewContext(options);
        db.ViewRows.Add(new ViewRow { Id = 1, Name = "John" });

        var exception = Assert.ThrowsException<NotSupportedException>(() => CreatePlan(db));
        Assert.AreEqual("Entity type 'ViewRow' is not mapped to a table.", exception.Message);
    }

    private static BulkSaveChangesPlan CreatePlan(DbContext db, int batchSize = 10_000)
    {
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker
            .Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        return BulkSaveChangesSqlPlanner.CreatePlan(db, entries, new BulkSaveChangesOptions { BatchSize = batchSize });
    }

    private static BulkSaveChangesCommand AssertSingleCommand(BulkSaveChangesPlan plan)
    {
        Assert.AreEqual(1, plan.Commands.Count);
        return plan.Commands[0];
    }

    private static void AssertSql(string expected, string actual)
    {
        Assert.AreEqual(NormalizeSql(expected), NormalizeSql(actual));
    }

    private static string NormalizeSql(string sql)
    {
        return sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static TestDbContext CreateContext(ILoggerFactory? loggerFactory = null)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=localhost;Database=bulk");

        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }

        var options = builder.Options;

        return new TestDbContext(options);
    }

    private static HiLoContext CreateHiLoContext()
    {
        var options = new DbContextOptionsBuilder<HiLoContext>()
            .UseNpgsql("Host=localhost;Database=bulk")
            .Options;

        return new HiLoContext(options);
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<User> Users => Set<User>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(user => user.FirstName).HasColumnName("first_name");
                entity.Property(user => user.LastName).HasColumnName("last_name");
                entity.Property(user => user.Email).HasColumnName("email");
            });
        }
    }

    private sealed class CompositeKeyContext(DbContextOptions<CompositeKeyContext> options) : DbContext(options)
    {
        public DbSet<CompositeKeyEntity> CompositeKeyEntities => Set<CompositeKeyEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeKeyEntity>(entity =>
            {
                entity.ToTable("composite_key_entities");
                entity.HasKey(item => new { item.TenantId, item.Code });
            });
        }
    }

    private sealed class ExplicitKeyContext(DbContextOptions<ExplicitKeyContext> options) : DbContext(options)
    {
        public DbSet<ExplicitKeyUser> ExplicitKeyUsers => Set<ExplicitKeyUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ExplicitKeyUser>(entity =>
            {
                entity.ToTable("explicit_key_users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(user => user.Name).HasColumnName("name");
            });
        }
    }

    private sealed class ValueConverterContext(DbContextOptions<ValueConverterContext> options) : DbContext(options)
    {
        public DbSet<ConvertedUser> Users => Set<ConvertedUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConvertedUser>(entity =>
            {
                entity.ToTable("converted_users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(user => user.Status)
                    .HasColumnName("status")
                    .HasConversion(
                        status => status == UserStatus.Active ? "active" : "inactive",
                        value => value == "active" ? UserStatus.Active : UserStatus.Inactive);
            });
        }
    }

    private sealed class HiLoContext(DbContextOptions<HiLoContext> options) : DbContext(options)
    {
        public DbSet<HiLoUser> Users => Set<HiLoUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HiLoUser>(entity =>
            {
                entity.ToTable("hilo_users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id)
                    .HasColumnName("id")
                    .ValueGeneratedOnAdd()
                    .HasValueGenerator<PermanentIntValueGenerator>();
                entity.Property(user => user.Name).HasColumnName("name");
            });
        }
    }

    private sealed class SchemaContext(DbContextOptions<SchemaContext> options) : DbContext(options)
    {
        public DbSet<User> Users => Set<User>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users", "app");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(user => user.FirstName).HasColumnName("first_name");
                entity.Property(user => user.LastName).HasColumnName("last_name");
                entity.Property(user => user.Email).HasColumnName("email");
            });
        }
    }

    private sealed class QuotedIdentifierContext(DbContextOptions<QuotedIdentifierContext> options) : DbContext(options)
    {
        public DbSet<QuotedIdentifierUser> Users => Set<QuotedIdentifierUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<QuotedIdentifierUser>(entity =>
            {
                entity.ToTable("users\"table", "app\"schema");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnName("id\"column").ValueGeneratedOnAdd();
                entity.Property(user => user.Name).HasColumnName("display\"name");
            });
        }
    }

    private sealed class ConcurrencyTokenContext(DbContextOptions<ConcurrencyTokenContext> options) : DbContext(options)
    {
        public DbSet<VersionedUser> Users => Set<VersionedUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<VersionedUser>(entity =>
            {
                entity.ToTable("versioned_users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Version).IsConcurrencyToken();
            });
        }
    }

    private sealed class OwnedTypeContext(DbContextOptions<OwnedTypeContext> options) : DbContext(options)
    {
        public DbSet<UserWithAddress> Users => Set<UserWithAddress>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserWithAddress>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(user => user.Id);
                entity.OwnsOne(user => user.Address);
            });
        }
    }

    private sealed class InheritanceContext(DbContextOptions<InheritanceContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.ToTable("people");
                entity.HasKey(person => person.Id);
            });

            modelBuilder.Entity<Employee>();
        }
    }

    private sealed class ShadowKeyContext(DbContextOptions<ShadowKeyContext> options) : DbContext(options)
    {
        public DbSet<ShadowKeyUser> Users => Set<ShadowKeyUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ShadowKeyUser>(entity =>
            {
                entity.ToTable("shadow_key_users");
                entity.Property<int>("Id");
                entity.HasKey("Id");
                entity.Property(user => user.Name).HasColumnName("name");
            });
        }
    }

    private sealed class KeylessViewContext(DbContextOptions<KeylessViewContext> options) : DbContext(options)
    {
        public DbSet<ViewRow> ViewRows => Set<ViewRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ViewRow>(entity =>
            {
                entity.HasKey(row => row.Id);
                entity.ToView("view_rows");
            });
        }
    }

    private sealed class User
    {
        public int Id { get; set; }

        public required string FirstName { get; set; }

        public required string LastName { get; set; }

        public string? Email { get; set; }
    }

    private sealed class CompositeKeyEntity
    {
        public int TenantId { get; set; }

        public required string Code { get; set; }

        public required string Name { get; set; }
    }

    private sealed class ExplicitKeyUser
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }

    private sealed class ConvertedUser
    {
        public int Id { get; set; }

        public UserStatus Status { get; set; }
    }

    private sealed class HiLoUser
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }

    private sealed class PermanentIntValueGenerator : ValueGenerator<int>
    {
        private static int NextId = 1_000;

        public override bool GeneratesTemporaryValues => false;

        public override int Next(EntityEntry entry)
        {
            return Interlocked.Increment(ref NextId);
        }
    }

    private enum UserStatus
    {
        Inactive,
        Active
    }

    private sealed class VersionedUser
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        public int Version { get; set; }
    }

    private sealed class QuotedIdentifierUser
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }

    private sealed class UserWithAddress
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        public required Address Address { get; set; }
    }

    private sealed class Address
    {
        public required string City { get; set; }
    }

    private class Person
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }

    private sealed class Employee : Person
    {
        public required string Department { get; set; }
    }

    private sealed class ShadowKeyUser
    {
        public required string Name { get; set; }
    }

    private sealed class ViewRow
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<LogMessage> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            return new RecordingLogger(Messages);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<LogMessage> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(new LogMessage(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogMessage(LogLevel LogLevel, string Message);
}
