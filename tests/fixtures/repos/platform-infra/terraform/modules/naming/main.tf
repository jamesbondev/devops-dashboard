resource "random_string" "suffix" {
  length  = 4
  special = false
}

resource "azurerm_storage_account" "diag" {
  name                     = "stdiag${random_string.suffix.result}"
  resource_group_name      = "rg-platform"
  location                 = "westeurope"
  account_tier             = "Standard"
  account_replication_type = "LRS"
}
