using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Reflection;

namespace Reptar;

internal static class RowMapper
{
    public static Func<DbDataReader, T> Resolve<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        DbDataReader reader)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType.IsPrimitive || targetType == typeof(string) || targetType == typeof(decimal) ||
            targetType == typeof(Guid) || targetType == typeof(DateTime) || targetType == typeof(DateTimeOffset))
        {
            return readerInstance => readerInstance.IsDBNull(0)
                ? default!
                : (T)ConvertValue(readerInstance.GetValue(0), targetType)!;
        }

        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            throw new NotSupportedException(
                $"No source-generated row mapper registered for {typeof(T)}. " +
                "Use a [ReptarEntity] partial record with the Reptar source generator, " +
                "register an explicit EntityMapper<T>, or use QueryMappedAsync with an explicit mapper.");

        var constructors = typeof(T).GetConstructors();
        if (constructors.Length == 0)
        {
            return _ => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        }

        var constructor = constructors
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        var parameters = constructor.GetParameters();
        var columns = new (int Ordinal, ParameterInfo Parameter)[parameters.Length];

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var ordinal = -1;

            for (var column = 0; column < reader.FieldCount; column++)
            {
                var name = reader.GetName(column);

                if (string.Equals(name, parameter.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ToPascal(name), parameter.Name, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = column;
                    break;
                }
            }

            columns[index] = (ordinal, parameter);
        }

        return readerInstance =>
        {
            var arguments = new object?[parameters.Length];

            for (var index = 0; index < parameters.Length; index++)
            {
                var (ordinal, parameter) = columns[index];

                if (ordinal < 0 || readerInstance.IsDBNull(ordinal))
                {
                    arguments[index] = parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
                    continue;
                }

                arguments[index] = ConvertValue(
                    readerInstance.GetValue(ordinal),
                    parameter.ParameterType);
            }

            return (T)constructor.Invoke(arguments)!;
        };
    }

    public static string ToPascal(string name) =>
        string.Join(
            "",
            name.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    public static object? ConvertValue(object value, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (type == typeof(string))
            return value.ToString();

        if (type == typeof(int))
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);

        if (type == typeof(long))
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);

        if (type == typeof(short))
            return Convert.ToInt16(value, CultureInfo.InvariantCulture);

        if (type == typeof(double))
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);

        if (type == typeof(float))
            return Convert.ToSingle(value, CultureInfo.InvariantCulture);

        if (type == typeof(decimal))
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        if (type == typeof(bool))
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);

        if (type == typeof(byte[]))
            return value;

        if (type == typeof(Guid))
            return Guid.Parse(value.ToString()!);

        if (type == typeof(DateTime))
        {
            return value is DateTime dateTime
                ? dateTime
                : DateTime.Parse(
                    value.ToString()!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
        }

        if (type == typeof(DateTimeOffset))
        {
            return value is DateTimeOffset offset
                ? offset
                : DateTimeOffset.Parse(
                    value.ToString()!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
        }

        if (type.IsEnum)
            return Enum.Parse(type, value.ToString()!, ignoreCase: true);

        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }
}
