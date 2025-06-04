using Faxtract.Hubs;
using Faxtract.Interfaces;
using Faxtract.Models;
using Faxtract.Services;

namespace Faxtract;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddUserSecrets<Program>(optional: true);

        // Add services to the container.
        builder.Services.AddControllersWithViews();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IWorkProvider, MemoryWorkProvider>();
        builder.Services.AddSingleton<WorkProcessor>();
        builder.Services.AddSingleton<LlamaExecutor>();
        builder.Services.AddSingleton<StorageService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkProcessor>());

        var app = builder.Build();

        // Delete the state file if present, in case the model has changed.
        File.Delete(Path.Join(AppContext.BaseDirectory, app.Configuration["PrePromptFile"] ?? "preprompt.state"));

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseRouting();

        app.UseAuthorization();

        app.MapControllers();
        app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
        app.MapHub<WorkHub>("/workhub");

        app.Run();
    }
}
