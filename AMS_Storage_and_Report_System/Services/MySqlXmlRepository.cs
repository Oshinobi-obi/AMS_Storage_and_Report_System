using Microsoft.AspNetCore.DataProtection.Repositories;
using MySqlConnector;
using System.Xml.Linq;

namespace AMS_Storage_and_Report_System.Services;

public class MySqlXmlRepository : IXmlRepository
{
    private readonly string _connectionString;

    public MySqlXmlRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();

        using var cmd = new MySqlCommand("SELECT xml_data FROM data_protection_keys", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();

        using var cmd = new MySqlCommand(
            "INSERT INTO data_protection_keys (friendly_name, xml_data) VALUES (@name, @xml)", conn);
        cmd.Parameters.AddWithValue("@name", friendlyName);
        cmd.Parameters.AddWithValue("@xml", element.ToString());
        cmd.ExecuteNonQuery();
    }
}