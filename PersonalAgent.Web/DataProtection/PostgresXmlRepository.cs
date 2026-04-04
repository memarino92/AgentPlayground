using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace PersonalAgent.Web.DataProtection;

internal class PostgresXmlRepository(string connectionString) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = new NpgsqlCommand("SELECT xml FROM app.data_protection_keys ORDER BY friendly_name", connection);
        using var reader = command.ExecuteReader();

        var elements = new List<XElement>();
        while (reader.Read())
        {
            var xml = reader.GetString(0);
            elements.Add(XElement.Parse(xml));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = new NpgsqlCommand("""
            INSERT INTO app.data_protection_keys (friendly_name, xml)
            VALUES (@friendly_name, @xml)
            ON CONFLICT (friendly_name) DO UPDATE SET xml = EXCLUDED.xml
            """, connection);

        command.Parameters.AddWithValue("friendly_name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
