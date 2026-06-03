using EfSimpleBulkSaveChanges.Api.Data;
using EfSimpleBulkSaveChanges.Api.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddDbContext<TodoDbContext>(options =>
    options.UseInMemoryDatabase("Todos"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/todos", async (TodoDbContext db) =>
    await db.TodoItems
        .OrderBy(todo => todo.Id)
        .ToListAsync())
    .WithName("GetTodos");

app.MapGet("/todos/{id:int}", async (int id, TodoDbContext db) =>
{
    var todo = await db.TodoItems.FindAsync(id);

    return todo is null ? Results.NotFound() : Results.Ok(todo);
})
    .WithName("GetTodo");

app.MapPost("/todos", async (CreateTodoRequest request, TodoDbContext db) =>
{
    var todo = new TodoItem
    {
        Title = request.Title,
        IsComplete = false
    };

    db.TodoItems.Add(todo);
    await db.SaveChangesAsync();

    return Results.Created($"/todos/{todo.Id}", todo);
})
    .WithName("CreateTodo");

app.Run();
