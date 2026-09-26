using System;
using System.Text;

namespace FanslationStudio.Plugins.Support;

public static class ExceptionHelper
{
    /// <summary>
    /// Formats an exception including every inner exception. Needed because some exceptions
    /// (notably YamlDotNet's YamlException) override ToString() to only print their position and
    /// message, which hides the inner exception that actually explains the failure.
    /// </summary>
    public static string ToFullString(this Exception exception)
    {
        var builder = new StringBuilder();
        var depth = 0;

        for (var current = exception; current != null; current = current.InnerException, depth++)
        {
            if (depth > 0)
                builder.AppendLine().Append("  ---> ");

            builder.Append(current.GetType().FullName).Append(": ").Append(current.Message);

            if (!string.IsNullOrEmpty(current.StackTrace))
                builder.AppendLine().Append(current.StackTrace);
        }

        return builder.ToString();
    }
}
