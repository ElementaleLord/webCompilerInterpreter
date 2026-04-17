using OnlineCompiler.Services;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddControllersWithViews();

// Add in-memory session store (for demo)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// We register our code execution service with a SCOPED lifetime
builder.Services.AddScoped<ICodeExecutionService, CodeExecutionService>();

var app = builder.Build();

// Configure the HTTP middleware pipeline

if (!app.Environment.IsDevelopment())
{
    // In production, catch unhandled exceptions and show /Home/Error.
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Enable session before auth/mvc
app.UseSession();

app.UseAuthorization();

// Maps URL pattern  /{controller}/{action}/{id?}
// The defaults mean http://localhost:PORT/ redirects to Home/Index.
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}"
);

app.Run();