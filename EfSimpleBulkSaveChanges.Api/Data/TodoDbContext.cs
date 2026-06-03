using EfSimpleBulkSaveChanges.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EfSimpleBulkSaveChanges.Api.Data;

public sealed class TodoDbContext(DbContextOptions<TodoDbContext> options) : DbContext(options)
{
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();
}
