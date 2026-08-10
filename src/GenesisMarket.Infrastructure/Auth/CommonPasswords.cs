using System.Reflection;

namespace GenesisMarket.Infrastructure.Auth;

/// <summary>
/// Блок-лист самых частых паролей. Список — встроенный ресурс
/// <c>common-passwords.txt</c> (по одному паролю в строке, регистронезависимо).
/// Регистрируется синглтоном: файл читается один раз.
/// </summary>
public sealed class CommonPasswords
{
    private readonly HashSet<string> _set;

    public CommonPasswords()
    {
        _set = Load();
    }

    public int Count => _set.Count;

    public bool Contains(string password) =>
        _set.Contains(password.Trim());

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(CommonPasswords).Assembly;
        var name = Array.Find(
            asm.GetManifestResourceNames(),
            n => n.EndsWith("common-passwords.txt", StringComparison.OrdinalIgnoreCase));

        if (name is null)
            return set;

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var pwd = line.Trim();
            if (pwd.Length == 0 || pwd.StartsWith('#'))
                continue;
            set.Add(pwd);
        }

        return set;
    }
}
