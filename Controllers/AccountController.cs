using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using webCompilerInterpreter.Services;

namespace webCompilerInterpreter.Controllers
{
    public class AccountController : Controller
    {
        private readonly IUserAccountService _accounts;
        // IUserAccountService is injected by the DI container
        // (registered in Program.cs as a singleton).
        public AccountController(IUserAccountService accounts)
        {
            _accounts = accounts;
        }

        // GET /Account/Login
        [HttpGet]
        public IActionResult Login() => View();

        // POST /Account/Login
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string username, string password)
        {
            // Preserve given form vars for error cases 
            ViewData["Username"] = username;

            // Null checks
            if (string.IsNullOrWhiteSpace(username))
                ModelState.AddModelError(string.Empty, "Username is required.");

            if (string.IsNullOrWhiteSpace(password))
                ModelState.AddModelError(string.Empty, "Password is required.");

            if (!ModelState.IsValid)
                return View();

            // Authenticate with Accounts.json
            var account = await _accounts.AuthenticateAsync(username, password);

            if (account is null)
            {// didnt find a match
                ModelState.AddModelError(string.Empty, "Invalid username or password.");
                return View();
            }

            // Login success
            HttpContext.Session.SetString("IsLoggedIn", "true");
            HttpContext.Session.SetString("Username", account.Username);
            HttpContext.Session.SetString("Email", account.Email);

            return RedirectToAction("Index", "Compiler");
        }
        // GET /Account/Register
        [HttpGet]
        public IActionResult Register() => View();
        // swap to the register page

        // POST /Account/Register
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(
            string username, string email,
            string password, string confirmPassword)
        {
            // Preserve given form vars for error cases
            ViewData["Username"] = username;
            ViewData["Email"]    = email;

            // Null checks and some baseline validation
            if (string.IsNullOrWhiteSpace(username))
                ModelState.AddModelError(string.Empty, "Username is required.");
            else if (username.Length < 3)
                ModelState.AddModelError
                     (string.Empty, "Username must be at least 3 characters.");
            else if (username.Length > 32)
                ModelState.AddModelError
                    (string.Empty, "Username must be 32 characters or fewer.");

            if (string.IsNullOrWhiteSpace(email))
                ModelState.AddModelError(string.Empty, "Email address is required.");
            else if (!IsValidEmail(email))
                ModelState.AddModelError
                    (string.Empty, "Please enter a valid email address.");

            if (string.IsNullOrWhiteSpace(password))
                ModelState.AddModelError(string.Empty, "Password is required.");
            else if (password.Length < 8)
                ModelState.AddModelError(string.Empty,
                    "Password must be at least 8 characters.");

            if (!string.IsNullOrEmpty(password) && password != confirmPassword)
                ModelState.AddModelError(string.Empty, "Passwords do not match.");

            if (!ModelState.IsValid)
                return View();
            // Attempt to register
            string? error = await _accounts.RegisterAsync(username, email, password);
            if (error is not null)
            {
                ModelState.AddModelError(string.Empty, error);
                return View();
            }
            // Registration success
            HttpContext.Session.SetString("IsLoggedIn", "true");
            HttpContext.Session.SetString("Username", username);
            HttpContext.Session.SetString("Email", email);
            return RedirectToAction("Index", "Compiler");
        }
        // POST /Account/Logout
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }
        private static bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }
    }
}
