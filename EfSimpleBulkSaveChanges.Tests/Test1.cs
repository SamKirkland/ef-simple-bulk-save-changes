using EfSimpleBulkSaveChanges.Api.Data;
using EfSimpleBulkSaveChanges.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EfSimpleBulkSaveChanges.Tests;

[TestClass]
public sealed class TodoDbContextTests
{
    [TestMethod]
    public async Task SaveChangesAsync_PersistsTodoItem()
    {
        var options = new DbContextOptionsBuilder<TodoDbContext>()
            .UseInMemoryDatabase($"todos-{Guid.NewGuid()}")
            .Options;

        await using var db = new TodoDbContext(options);

        db.TodoItems.Add(new TodoItem { Title = "Build the API" });
        await db.SaveChangesAsync();

        var saved = await db.TodoItems.SingleAsync();

        Assert.AreEqual("Build the API", saved.Title);
        Assert.IsFalse(saved.IsComplete);
    }
}
