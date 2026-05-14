using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace webCompilerInterpreter.Services
{
    // Data model for MongoDB
    public class UserAccount
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("username")]
        public string Username { get; set; } = string.Empty;

        [BsonElement("email")]
        public string Email { get; set; } = string.Empty;

        /// Base-64 encoded PBKDF2 hash of the password
        [BsonElement("passwordHash")]
        public string PasswordHash { get; set; } = string.Empty;

        /// Base-64 encoded random salt used when hashing this user's password
        [BsonElement("passwordSalt")]
        public string PasswordSalt { get; set; } = string.Empty;

        /// Track when the account was created
        [BsonElement("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // Service interface
    public interface IUserAccountService
    {
        /// Ensures the MongoDB collection exists with proper indexes
        Task EnsureStorageExistsAsync();

        /// Attempts to authenticate a user by username + password
        /// Returns the matching UserAccount on success or null if authentication fails
        Task<UserAccount?> AuthenticateAsync(string username, string password);

        /// Registers a new user
        /// Returns null on success, or an error message string if validation fails
        Task<string?> RegisterAsync(string username, string email, string password);
    }

    // MongoDB implementation
    public class UserAccountService : IUserAccountService
    {
        private readonly IMongoDatabase _database;
        private readonly IMongoCollection<UserAccount> _usersCollection;

        // PBKDF2 parameters
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const int Pbkdf2Iterations = 350_000;
        private const string CollectionName = "Users";

        public UserAccountService(IMongoDatabase database)
        {
            _database = database;
            _usersCollection = database.GetCollection<UserAccount>(CollectionName);
        }

        public async Task EnsureStorageExistsAsync()
        {
            try
            {
                // Check if collection exists by iterating the async cursor returned
                var collectionsCursor = await _database.ListCollectionNamesAsync();
                bool collectionExists = false;

                while (await collectionsCursor.MoveNextAsync())
                {
                    foreach (var name in collectionsCursor.Current)
                    {
                        if (string.Equals(name, CollectionName, StringComparison.Ordinal))
                        {
                            collectionExists = true;
                            break;
                        }
                    }
                    if (collectionExists) break;
                }

                if (!collectionExists)
                {
                    // Create the collection
                    await _database.CreateCollectionAsync(CollectionName);
                }

                // Create unique indexes for username and email
                // This prevents duplicate usernames and emails at the database level
                var indexModel = new CreateIndexModel<UserAccount>(
                    Builders<UserAccount>.IndexKeys.Ascending(u => u.Username),
                    new CreateIndexOptions { Unique = true }
                );

                var emailIndexModel = new CreateIndexModel<UserAccount>(
                    Builders<UserAccount>.IndexKeys.Ascending(u => u.Email),
                    new CreateIndexOptions { Unique = true }
                );

                try
                {
                    await _usersCollection.Indexes.CreateOneAsync(indexModel);
                    await _usersCollection.Indexes.CreateOneAsync(emailIndexModel);
                }
                catch (MongoCommandException ex) when (ex.Code == 85)
                {
                    // Index already exists
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to initialize MongoDB user collection", ex);
            }
        }

        public async Task<UserAccount?> AuthenticateAsync(string username, string password)
        {
            try
            {
                // Case-insensitive username lookup using MongoDB filter
                var filter = Builders<UserAccount>.Filter.Regex(
                    u => u.Username,
                    new BsonRegularExpression($"^{Regex.Escape(username)}$", "i")
                );

                var account = await _usersCollection.Find(filter).FirstOrDefaultAsync();

                if (account is null)
                    return null;

                // Verify the password matches
                bool passwordMatches = VerifyPassword(
                    password, account.PasswordHash, account.PasswordSalt
                );

                return passwordMatches ? account : null;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Authentication failed due to database error", ex);
            }
        }

        public async Task<string?> RegisterAsync(string username, string email, string password)
        {
            try
            {
                // Check for existing username (case-insensitive)
                var usernameTaken = await UsernameExistsAsync(username);
                if (usernameTaken)
                    return "Username is already taken.";

                // Check for existing email (case-insensitive)
                var emailTaken = await EmailExistsAsync(email);
                if (emailTaken)
                    return "An account with that email address already exists.";

                // Hash the password
                (string hash, string salt) = HashPassword(password);

                var newUser = new UserAccount
                {
                    Username = username,
                    Email = email,
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    CreatedAt = DateTime.UtcNow
                };

                // Insert into MongoDB
                await _usersCollection.InsertOneAsync(newUser);
                return null; // Success
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // MongoDB unique constraint violation
                // Determine if it was username or email
                if (ex.Message.Contains("username"))
                    return "Username is already taken.";
                if (ex.Message.Contains("email"))
                    return "An account with that email address already exists.";

                return "Registration failed: username or email already in use.";
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Registration failed due to database error", ex);
            }
        }

        // Private helper methods

        private async Task<bool> UsernameExistsAsync(string username)
        {
            var filter = Builders<UserAccount>.Filter.Regex(
                u => u.Username,
                new BsonRegularExpression($"^{Regex.Escape(username)}$", "i")
            );
            return await _usersCollection.Find(filter).AnyAsync();
        }

        private async Task<bool> EmailExistsAsync(string email)
        {
            var filter = Builders<UserAccount>.Filter.Regex(
                u => u.Email,
                new BsonRegularExpression($"^{Regex.Escape(email)}$", "i")
            );
            return await _usersCollection.Find(filter).AnyAsync();
        }

        // Password hashing
        private static (string hash, string salt) HashPassword(string plaintext)
        {
            byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);

            byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(plaintext),
                saltBytes,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                HashBytes
            );

            return (
                Convert.ToBase64String(hashBytes),
                Convert.ToBase64String(saltBytes)
            );
        }

        private static bool VerifyPassword(string plaintext, string hashB64, string saltB64)
        {
            byte[] saltBytes = Convert.FromBase64String(saltB64);
            byte[] storedHash = Convert.FromBase64String(hashB64);

            byte[] suppliedHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(plaintext),
                saltBytes,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                HashBytes
            );

            return CryptographicOperations.FixedTimeEquals(storedHash, suppliedHash);
        }
    }
}