# Shared platform
terraform {
  required_version = ">= 1.6.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.10"
    }
    random = {
      source  = "hashicorp/random"
      version = "3.6.3"
    }
  }

  backend "azurerm" {
    resource_group_name  = "rg-tfstate"
    storage_account_name = "sttfstate"
    container_name       = "tfstate"
    key                  = "platform.tfstate"
  }
}

provider "azurerm" {
  features {}
}

/* module "old" { source = "./old" } */

module "network" {
  source  = "Azure/network/azurerm"
  version = "5.3.0"
  resource_group_name = azurerm_resource_group.platform.name
}

module "aks" {
  source = "git::https://dev.azure.com/contoso/Platform/_git/tf-modules//aks?ref=v2.1.0"
  name   = "aks-platform"
}

module "naming" {
  source = "./modules/naming"
}

module "monitoring" {
  source = "git::https://github.com/contoso/tf-monitoring.git"
}

resource "azurerm_resource_group" "platform" {
  name     = "rg-platform"
  location = "westeurope"
}

resource "azurerm_key_vault" "platform" {
  name                = "kv-platform" # "inline comment with quotes"
  location            = azurerm_resource_group.platform.location
  resource_group_name = azurerm_resource_group.platform.name
  tenant_id           = var.tenant_id
  sku_name            = "standard"
}

resource "azurerm_servicebus_namespace" "platform" {
  name                = "sb-platform"
  location            = azurerm_resource_group.platform.location
  resource_group_name = azurerm_resource_group.platform.name
  sku                 = "Standard"
}

resource "azurerm_servicebus_queue" "orders" {
  name               = "orders-created"
  namespace_id       = azurerm_servicebus_namespace.platform.id
  max_delivery_count = 10
}
