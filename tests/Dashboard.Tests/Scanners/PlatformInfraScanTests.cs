using Dashboard.Core.Scanners;

namespace Dashboard.Tests.Scanners;

/// <summary>Fixture (c): terraform + hiera infra repo.</summary>
public class PlatformInfraScanTests
{
    private static RepoFacts F => Fixtures.Facts(Fixtures.Infra);

    [Fact]
    public void Primary_language_is_terraform()
    {
        Assert.Equal("Terraform", F.Languages.Primary);
        Assert.Equal(3, F.Languages.FileCounts["Terraform"]);
        Assert.Equal(1, F.Languages.FileCounts["Puppet"]);
        Assert.Empty(F.Dotnet.Projects);
        Assert.Empty(F.Components);
    }

    [Fact]
    public void Terraform_versions_backend_and_providers()
    {
        var tf = F.Terraform;
        Assert.Equal(["terraform", "terraform/modules/naming"], tf.Directories);
        Assert.Equal(">= 1.6.0", tf.RequiredVersion);
        Assert.Equal("~> 4.10", tf.Providers["azurerm"]);
        Assert.Equal("3.6.3", tf.Providers["random"]);
        Assert.Equal("~> 4.10", tf.AzurermVersion);
        Assert.Equal("4.50.0", tf.LatestAzurermVersion);
        Assert.Equal("azurerm", tf.BackendType);
        Assert.True(tf.HasLockFile);
    }

    [Fact]
    public void Terraform_modules_and_pinning()
    {
        var m = F.Terraform.Modules.ToDictionary(x => x.Name);
        Assert.Equal(4, m.Count);                      // commented-out module "old" ignored
        Assert.True(m["network"].Pinned);
        Assert.Equal("5.3.0", m["network"].Version);
        Assert.Equal("Azure/network/azurerm", m["network"].Source);
        Assert.True(m["aks"].Pinned);                  // ?ref=
        Assert.Null(m["aks"].Version);
        Assert.True(m["naming"].Pinned);               // local path
        Assert.False(m["monitoring"].Pinned);          // git without ref
    }

    [Fact]
    public void Terraform_resource_types()
        => Assert.Equal(
            ["azurerm_key_vault", "azurerm_resource_group", "azurerm_servicebus_namespace", "azurerm_servicebus_queue", "azurerm_storage_account", "random_string"],
            F.Terraform.ResourceTypes);

    [Fact]
    public void Azure_services_from_terraform()
        => Assert.Equal(["Key Vault", "Service Bus", "Storage"], F.AzureServices);

    [Fact]
    public void Hiera_hierarchy_and_keys()
    {
        var h = F.Hiera;
        Assert.True(h.Present);
        Assert.Equal(["Per-node data", "Per-environment data", "Common data"], h.HierarchyLevels);
        Assert.Equal("puppet/hiera.yaml", h.Files[0]);
        Assert.Equal(4, h.Files.Count);
        Assert.Equal(10, h.KeyCount);
    }

    [Fact]
    public void Pipeline_extends_with_stages_inside_parameters_is_not_inline()
    {
        var p = Assert.Single(F.Pipelines);   // broken.yml is skipped with a warning
        Assert.Equal("pipelines/terraform.yml", p.Path);
        Assert.True(p.UsesExtends);
        Assert.Equal("templates/terraform.yml@templates", p.ExtendsTemplate);
        Assert.Equal("refs/heads/next", Assert.Single(p.RepositoryResources).Ref);
        Assert.Equal(0, p.InlineStages);
        Assert.Equal(["platform-prod"], p.Environments);
        Assert.Equal(["templates/terraform.yml@templates", "steps/terraform-apply.yml@templates"], p.TemplateReferences);
    }

    [Fact]
    public void Malformed_yaml_becomes_the_only_warning()
        => Assert.Contains("pipelines/broken.yml", Assert.Single(F.ScanWarnings), StringComparison.Ordinal);

    [Fact]
    public void Dead_letter_config_found_in_terraform()
    {
        Assert.True(F.Messaging.DeadLetterHandlingFound);
        Assert.False(F.Messaging.UsesServiceBus);
    }

    [Fact]
    public void Docs_readme_mentions_deploy()
    {
        Assert.True(F.Docs.HasReadme);
        Assert.True(F.Docs.ReadmeMentionsDeploy);
    }
}
