using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace webCompilerInterpreter.Services
{
    // Data model stored in the JSON file
    public class UserAccount
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;
        /// Base-64 encoded PBKDF2 hash of the password.
        [JsonPropertyName("passwordHash")]
        public string PasswordHash { get; set; } = string.Empty;
        /// Base-64 encoded random salt used when hashing this user's password. 
        /// Each user gets a unique salt.
        [JsonPropertyName("passwordSalt")]
        public string PasswordSalt { get; set; } = string.Empty;
    }

    // Service interface
    public interface IUserAccountService
    {
        /// Creates Accounts.json with an empty user list if the file does not yet exist.
        Task EnsureStorageExistsAsync();

        /// Attempts to authenticate a user by username + password
        /// Returns the matching UserAccount on success
        /// or null if the username does not exist or the password is wrong
        Task<UserAccount?> AuthenticateAsync(string username, string password);

        /// Registers a new user
        /// Returns null on success, or an error message 
        /// string if the username or e-mail is already taken
        Task<string?> RegisterAsync(string username, string email, string password);
    }

    // Concrete implementation
    public class UserAccountService : IUserAccountService
    {
        private readonly string _filePath;
        // Used to prevent concurrent file access from multiple HTTP requests
        private static readonly SemaphoreSlim _lock = new(1, 1);
        // PBKDF2 parameters
        private const int SaltBytes       = 16;
        private const int HashBytes       = 32;
        private const int Pbkdf2Iterations = 350_000;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true
        };
        // UserAccountService
        public UserAccountService(IWebHostEnvironment env)
        {
            _filePath = Path.Combine(env.ContentRootPath, "Accounts.json");
        }
        // EnsureStorageExistsAsync
        public async Task EnsureStorageExistsAsync()
        {
            // If the file already exists nothing to do
            if (File.Exists(_filePath)) return;
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(_filePath))
                {
                    string empty = JsonSerializer.Serialize
                        (Array.Empty<UserAccount>(), _jsonOpts);
                    // Write an empty array so the file is valid JSON from day one
                    await File.WriteAllTextAsync(_filePath, empty);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        // AuthenticateAsync
        public async Task<UserAccount?> AuthenticateAsync(string username, string password)
        {
            var accounts = await ReadAllAsync();

            // Username lookup is case-insensitive so "Admin" == "admin"
            var account = accounts.FirstOrDefault
                (a => string.Equals
                    (a.Username, username, StringComparison.OrdinalIgnoreCase)
                );
            if (account is null) return null;// username not found
            // Re-derive the hash from the supplied password + stored salt
            // and compare to what we stored at registration time
            bool passwordMatches = VerifyPassword
                (password, account.PasswordHash, account.PasswordSalt);
            return passwordMatches ? account : null;
        }
        // RegisterAsync
        public async Task<string?> RegisterAsync(string username, string email, string password)
        {
            await _lock.WaitAsync();
            try
            {
                var accounts = await ReadAllNoLockAsync();
                // Duplicate checks
                bool usernameTaken = accounts.Any
                    (a => string.Equals
                        (a.Username, username, StringComparison.OrdinalIgnoreCase)
                    );
                if (usernameTaken) return "Username is already taken.";
                bool emailTaken = accounts.Any
                    (a => string.Equals
                        (a.Email, email, StringComparison.OrdinalIgnoreCase)
                    );
                if (emailTaken) return "An account with that email address already exists.";
                // Hash the password
                (string hash, string salt) = HashPassword(password);
                accounts.Add(new UserAccount
                {
                    Username     = username,
                    Email        = email,
                    PasswordHash = hash,
                    PasswordSalt = salt
                });
                await WriteAllNoLockAsync(accounts);
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }
        // Private helpers
        /// Reads the JSON file and returns the list of accounts
        private async Task<List<UserAccount>> ReadAllAsync()
        {
            await _lock.WaitAsync();
            try
            {
                return await ReadAllNoLockAsync();
            }
            finally
            {
                _lock.Release();
            }
        }
        /// Same as ReadAllAsync but assumes the caller already holds the lock
        private async Task<List<UserAccount>> ReadAllNoLockAsync()
        {
            if (!File.Exists(_filePath)) return new List<UserAccount>();

            string json = await File.ReadAllTextAsync(_filePath);

            if (string.IsNullOrWhiteSpace(json)) return new List<UserAccount>();

            return JsonSerializer.Deserialize<List<UserAccount>>
                (json, _jsonOpts) ?? new List<UserAccount>();
        }
        /// Serialises the list back to the JSON file assumes the caller holds the lock
        private async Task WriteAllNoLockAsync(List<UserAccount> accounts)
        {
            string json = JsonSerializer.Serialize(accounts, _jsonOpts);
            await File.WriteAllTextAsync(_filePath, json);
        }

        // Password hashing
        /// Generates a fresh random salt, derives a PBKDF2 hash from given plaintext + salt
        /// and returns both as Base-64 strings ready to store in JSON
        private static (string hash, string salt) HashPassword(string plaintext)
        {
            byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);

            byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2
                (
                    Encoding.UTF8.GetBytes(plaintext),
                    saltBytes,
                    Pbkdf2Iterations,
                    HashAlgorithmName.SHA256,
                    HashBytes
                );
            return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
        }
        /// Re-derives the hash from plaintext and the stored saltB64 then compares it to the
        /// stored hashB64 using a constant-time compare to prevent timing attacks
        private static bool VerifyPassword(string plaintext, string hashB64, string saltB64)
        {
            byte[] saltBytes     = Convert.FromBase64String(saltB64);
            byte[] storedHash    = Convert.FromBase64String(hashB64);

            byte[] suppliedHash  = Rfc2898DeriveBytes.Pbkdf2
                (
                    Encoding.UTF8.GetBytes(plaintext),
                    saltBytes,
                    Pbkdf2Iterations,
                    HashAlgorithmName.SHA256,
                    HashBytes
                );
            // CryptographicOperations.FixedTimeEquals prevents timing attacks
            // where an attacker could measure how long the compare takes.
            return CryptographicOperations.FixedTimeEquals(storedHash, suppliedHash);
        }
    }
}
