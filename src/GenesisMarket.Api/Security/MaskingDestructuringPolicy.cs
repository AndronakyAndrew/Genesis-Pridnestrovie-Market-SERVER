using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace GenesisMarket.Api.Security;

/// <summary>
/// Политика деструктуризации Serilog: если структуру логируют целиком (<c>{@obj}</c>),
/// значения чувствительных свойств (<c>password</c>, <c>token</c>, <c>refreshToken</c>,
/// <c>phone</c>, <c>email</c> и производные) заменяются на <c>***</c>. Защита в глубину:
/// даже если где-то залогируют DTO логина/регистрации целиком, секреты не утекут.
/// Применяется только к типам самого приложения, чтобы не влиять на чужие объекты.
/// </summary>
public sealed class MaskingDestructuringPolicy : IDestructuringPolicy
{
    private const string Mask = "***";

    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "currentpassword", "newpassword",
        "token", "accesstoken", "refreshtoken",
        "phone", "phonee164",
        "email",
        "passwordhash", "securitystamp", "key", "secret", "secretkey",
    };

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        result = null;
        var type = value.GetType();

        // Только наши типы: не вмешиваемся в деструктуризацию объектов фреймворка/библиотек.
        if (type.Namespace is null || !type.Namespace.StartsWith("GenesisMarket", StringComparison.Ordinal))
            return false;

        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

        var masked = new List<LogEventProperty>();
        foreach (var property in properties)
        {
            object? propertyValue = Sensitive.Contains(property.Name) ? Mask : SafeGet(property, value);
            masked.Add(new LogEventProperty(
                property.Name,
                propertyValueFactory.CreatePropertyValue(propertyValue, destructureObjects: true)));
        }

        result = new StructureValue(masked, type.Name);
        return true;
    }

    private static object? SafeGet(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}
