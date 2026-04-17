using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

public class AccountController : Controller
{
    // GET: /Account/Login
    [HttpGet]
    public IActionResult Login()
    {
        return View();
    }

    // POST: /Account/Login
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Login(string username, string password)
    {
        // NOTE: placeholder authentication. Replace with Identity or a real check.
        if (string.IsNullOrWhiteSpace(username))
        {
            ModelState.AddModelError(string.Empty, "Username is required.");
            ViewData["Username"] = username;
            return View();
        }

        // Mark user as logged in using session (demo only).
        HttpContext.Session.SetString("IsLoggedIn", "true");
        HttpContext.Session.SetString("Username", username);

        // After login send the user to the Compiler.
        return RedirectToAction("Index", "Compiler");
    }

    // GET: /Account/Register
    [HttpGet]
    public IActionResult Register()
    {
        return View();
    }

    // POST: /Account/Register
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Register(string username, string password, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            ModelState.AddModelError(string.Empty, "Username is required.");
            ViewData["Username"] = username;
            return View();
        }

        if (password != confirmPassword)
        {
            ModelState.AddModelError(string.Empty, "Passwords do not match.");
            ViewData["Username"] = username;
            return View();
        }

        // Placeholder user creation: mark as logged in using session.
        HttpContext.Session.SetString("IsLoggedIn", "true");
        HttpContext.Session.SetString("Username", username);

        // After register send the user to the Compiler.
        return RedirectToAction("Index", "Compiler");
    }

    // POST: /Account/Logout
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Index", "Home");
    }
}