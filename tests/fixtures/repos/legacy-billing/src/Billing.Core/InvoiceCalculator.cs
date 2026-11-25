namespace Billing.Core;

public class InvoiceCalculator
{
    public decimal Total(decimal net, decimal vat) => net * (1 + vat);
}
