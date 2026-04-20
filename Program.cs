using webCompilerInterpreter.Services;
var builder= WebApplication.CreateBuilder(args);
// Register services
builder.Services.AddControllersWithViews();
// In-memory session store
builder.Services.AddDistributedMemoryCache();
// Configure session options and cookie settings
// Session timeout is set at 20 minutes of inactivity
builder.Services.AddSession
    (options => {
        options.IdleTimeout= TimeSpan.FromMinutes(20);
        options.Cookie.HttpOnly= true;
        options.Cookie.IsEssential= true;
    });
// Code execution service used in scoped mode: new instance per HTTP request
builder.Services.AddScoped<ICodeExecutionService, CodeExecutionService>();
// User account service used in singleton mode: one instance shares the file lock
// across all requests. IWebHostEnvironment is injected automatically.
builder.Services.AddSingleton<IUserAccountService, UserAccountService>();
// Build
var app= builder.Build();
// Ensure Accounts.json exists before accepting any requests
// Resolve the service and call EnsureStorageExistsAsync() once at startup.
// This creates an empty Accounts.json if the file doesn't exist yet, so
// the first Register/Login request never hits a missing-file error.
var accountService= app.Services.GetRequiredService<IUserAccountService>();
await accountService.EnsureStorageExistsAsync();
// Configure HTTP middleware pipeline 
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
// Redirect HTTP to HTTPS
app.UseHttpsRedirection();
// Serve static files (e.g. CSS, JS, images) from wwwroot
app.UseStaticFiles();
// Enable routing to controllers
app.UseRouting();
// Enable session state for storing user login info across requests
app.UseSession();
// Enable authorization middleware
app.UseAuthorization();
// Define the default route pattern for controller actions
app.MapControllerRoute(
    name:    "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();