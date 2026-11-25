using System.Data.SqlClient;
using Dapper;

namespace Billing.Web.Repositories;

public class InvoiceRepository
{
    private const string ConnectionString = "Server=sql01;Database=Billing;Trusted_Connection=True";

    public async Task<IEnumerable<Invoice>> GetOpenAsync()
    {
        using var conn = new SqlConnection(ConnectionString);
        return await conn.QueryAsync<Invoice>("select * from Invoices where Paid = 0");
    }
}

public record Invoice(int Id, decimal Amount);
