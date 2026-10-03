using MySqlConnector;

namespace AMS_Storage_and_Report_System.Services;

/// Turns database errors from saving files (photos, signed RIS) into a message AMS can act on.
public static class StorageErrors
{
    public static string Explain(Exception ex, string what)
    {
        var root = ex.GetBaseException();
        var message = root.Message;

        if (message.Contains("max_allowed_packet", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Packet", StringComparison.OrdinalIgnoreCase) && message.Contains("too large", StringComparison.OrdinalIgnoreCase))
            return $"{what} is larger than the database server accepts (its max_allowed_packet setting). Try a smaller file, or ask the host to raise max_allowed_packet.";

        if (message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase))
            return $"{what} couldn't be saved because a database table is missing. Run the latest Database scripts (005 and 006) on this database.";

        if (root is MySqlException)
            return $"{what} couldn't be saved. The database said: {message}";

        return $"{what} couldn't be saved. Please try again. ({message})";
    }
}
