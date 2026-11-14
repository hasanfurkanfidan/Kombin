using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;

namespace Kombin.Api.Features.Admin;

/// <summary>
/// <c>dotnet run --project src/Kombin.Api -- hash-password [password]</c>: prints the PBKDF2 hash to put in <c>Admin:Users</c>
/// (<c>ADMIN_PASSWORD_HASH</c> in compose). Without an argument the password is read from standard input (no echo on a terminal), so it
/// does not end up in the shell history. Runs before the host is built: no configuration, database or network is touched.
/// </summary>
internal static class AdminHashCommand
{
    public const string Name = "hash-password";

    public const int MinimumLength = 12;

    public static bool IsRequested(string[] args) => args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);

    public static int Run(string[] args, TextWriter output, TextWriter error, Func<string?> readPassword)
    {
        var password = args.Length > 1 ? args[1] : readPassword();

        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
        {
            error.WriteLine($"The password must be at least {MinimumLength} characters long.");
            return 1;
        }

        output.WriteLine(Hash(password));
        return 0;
    }

    public static string Hash(string password) => new PasswordHasher<User>().HashPassword(new User(), password);

    /// <summary>Reads a line from the console without echoing it, or from standard input when it is redirected.</summary>
    public static string? ReadFromConsole()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write("Password: ");
        var buffer = new System.Text.StringBuilder();

        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return buffer.ToString();
    }
}
