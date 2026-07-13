using System.Globalization;
using System.Text;

namespace NimBus.WebApp.Services.ApplicationInsights;

internal static class KqlStringLiteral
{
    public static string Format(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var literal = new StringBuilder(value.Length + 2);
        literal.Append('\'');

        foreach (var character in value)
        {
            switch (character)
            {
                case '\'':
                    literal.Append("\\'");
                    break;
                case '\\':
                    literal.Append("\\\\");
                    break;
                case '\r':
                    literal.Append("\\r");
                    break;
                case '\n':
                    literal.Append("\\n");
                    break;
                case '\t':
                    literal.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        literal.Append("\\u");
                        literal.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        literal.Append(character);
                    }

                    break;
            }
        }

        literal.Append('\'');
        return literal.ToString();
    }
}
