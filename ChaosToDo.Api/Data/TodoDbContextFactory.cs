using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChaosToDo.Api.Data;

/// <summary>
/// Used only by `dotnet ef migrations add/remove` at design time. At runtime the
/// DbContext is configured by Aspire via <c>AddSqlServerDbContext</c> in Program.cs,
/// using the connection string Aspire injects for the "database" resource.
/// </summary>
public class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TodoDbContext>();
        optionsBuilder.UseSqlServer("Server=localhost;Database=ChaosToDo;TrustServerCertificate=True");
        return new TodoDbContext(optionsBuilder.Options);
    }
}
