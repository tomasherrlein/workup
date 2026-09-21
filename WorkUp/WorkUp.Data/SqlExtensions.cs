using Microsoft.Data.SqlClient;

namespace WorkUp.Data;

/// <summary>Parameter and reader helpers shared by every repository.</summary>
public static class SqlExtensions
{
    /// <summary>
    /// Adds a parameter. Null becomes DBNull and enums become their TINYINT value. DateOnly and TimeOnly are supported
    /// natively by Microsoft.Data.SqlClient.
    /// </summary>
    public static SqlParameterCollection AddValue(this SqlParameterCollection parameters, string name, object? value)
    {
        object dbValue = value switch
        {
            null => DBNull.Value,
            Enum e => Convert.ToByte(e),
            _ => value
        };
        parameters.AddWithValue(name, dbValue);
        return parameters;
    }

    public static T Get<T>(this SqlDataReader reader, string column) =>
        reader.GetFieldValue<T>(reader.GetOrdinal(column));

    public static T? GetNullable<T>(this SqlDataReader reader, string column) where T : struct
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    public static string? GetNullableString(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>Reads a TINYINT column as an enum, including <see cref="DayOfWeek"/>.</summary>
    public static TEnum GetEnum<TEnum>(this SqlDataReader reader, string column) where TEnum : struct, Enum =>
        (TEnum)Enum.ToObject(typeof(TEnum), reader.GetByte(reader.GetOrdinal(column)));

    public static TEnum? GetNullableEnum<TEnum>(this SqlDataReader reader, string column) where TEnum : struct, Enum
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : (TEnum)Enum.ToObject(typeof(TEnum), reader.GetByte(ordinal));
    }
}
