using Npgsql;
using System.Data;

namespace ErpV2.Common;

/// <summary>
/// Factory for creating PostgreSQL connections.
/// Registered as singleton; call CreateConnection() per-request.
/// </summary>
public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}

public class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IDbConnection CreateConnection()
    {
        var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
