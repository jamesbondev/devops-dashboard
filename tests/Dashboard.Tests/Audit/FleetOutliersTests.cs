using Dashboard.Core.Audit;
using Dashboard.Core.Scanners;
using Dashboard.Tests.Scanners;

namespace Dashboard.Tests.Audit;

public class FleetOutliersTests
{
    private static RepoFacts Repo(bool cpm, bool readme, bool nonRootDocker = true)
    {
        var f = new RepoFacts();
        f.Dotnet.CentralPackageManagement = cpm;
        f.Dotnet.Projects = [new DotnetProject { Name = "App", Kind = "Web", Nullable = true, CentralPackageManagement = cpm }];
        f.Docs.HasReadme = readme;
        f.Deploy.Dockerfiles = [new DockerfileFacts { Path = "Dockerfile", NonRootUser = nonRootDocker, BaseImagesPinned = true, MultiStage = true }];
        return f;
    }

    [Fact]
    public void Flags_repo_that_differs_from_a_clear_majority()
    {
        var fleet = new Dictionary<string, RepoFacts>
        {
            ["a"] = Repo(cpm: true, readme: true),
            ["b"] = Repo(cpm: true, readme: true),
            ["c"] = Repo(cpm: true, readme: true),
            ["d"] = Repo(cpm: false, readme: true, nonRootDocker: false),
        };
        var outliers = FleetOutliers.Compute(fleet);
        var cpm = Assert.Single(outliers, o => o.Fact == FleetOutliers.Facts.CentralPackageManagement);
        Assert.Equal("d", cpm.Repo);
        Assert.False(cpm.RepoValue);
        Assert.True(cpm.MajorityValue);
        Assert.Equal(0.75, cpm.MajorityShare);
        Assert.Equal(4, cpm.ApplicableRepos);
        Assert.Single(outliers, o => o.Fact == FleetOutliers.Facts.DockerfilesNonRoot && o.Repo == "d");
        Assert.DoesNotContain(outliers, o => o.Fact == FleetOutliers.Facts.HasReadme);   // everyone agrees
        // Every repo lacks CODEOWNERS: unanimous, so no outliers either.
        Assert.DoesNotContain(outliers, o => o.Fact == FleetOutliers.Facts.HasCodeowners);
    }

    [Fact]
    public void Requires_three_applicable_repos_and_majority_share()
    {
        var two = new Dictionary<string, RepoFacts> { ["a"] = Repo(true, true), ["b"] = Repo(false, false) };
        Assert.Empty(FleetOutliers.Compute(two));

        // 3 of 5 = 0.6 → meets the default threshold; with 0.7 it does not.
        var five = new Dictionary<string, RepoFacts>
        {
            ["a"] = Repo(true, true), ["b"] = Repo(true, true), ["c"] = Repo(true, true),
            ["d"] = Repo(false, true), ["e"] = Repo(false, true),
        };
        Assert.Equal(["d", "e"], FleetOutliers.Compute(five).Where(o => o.Fact == FleetOutliers.Facts.CentralPackageManagement).Select(o => o.Repo));
        Assert.DoesNotContain(FleetOutliers.Compute(five, 0.7), o => o.Fact == FleetOutliers.Facts.CentralPackageManagement);

        // Ties never produce outliers.
        var tie = new Dictionary<string, RepoFacts> { ["a"] = Repo(true, true), ["b"] = Repo(true, true), ["c"] = Repo(false, true), ["d"] = Repo(false, true) };
        Assert.DoesNotContain(FleetOutliers.Compute(tie, 0.5), o => o.Fact == FleetOutliers.Facts.CentralPackageManagement);
    }

    [Fact]
    public void Not_applicable_repos_are_excluded()
    {
        var infra = new RepoFacts();   // no .NET, no Dockerfiles
        var fleet = new Dictionary<string, RepoFacts>
        {
            ["a"] = Repo(true, true), ["b"] = Repo(true, true), ["c"] = Repo(false, true), ["infra"] = infra,
        };
        var cpm = FleetOutliers.Compute(fleet).Where(o => o.Fact == FleetOutliers.Facts.CentralPackageManagement).ToList();
        var o = Assert.Single(cpm);
        Assert.Equal("c", o.Repo);
        Assert.Equal(3, o.ApplicableRepos);
        // README applies to every repo: infra lacks one, 3 of 4 have it.
        Assert.Equal("infra", Assert.Single(FleetOutliers.Compute(fleet), x => x.Fact == FleetOutliers.Facts.HasReadme).Repo);
    }

    [Fact]
    public void Works_on_fixture_fleet()
    {
        var fleet = new Dictionary<string, RepoFacts>
        {
            ["orders"] = Fixtures.Facts(Fixtures.Orders),
            ["legacy"] = Fixtures.Facts(Fixtures.Legacy),
            ["infra"] = Fixtures.Facts(Fixtures.Infra),
        };
        var outliers = FleetOutliers.Compute(fleet);
        // README: orders + infra have one, legacy doesn't → 2/3 ≥ 0.6.
        var readme = Assert.Single(outliers, o => o.Fact == FleetOutliers.Facts.HasReadme);
        Assert.Equal("legacy", readme.Repo);
        Assert.Equal(3, readme.ApplicableRepos);
        // CPM is applicable to only two repos → skipped.
        Assert.DoesNotContain(outliers, o => o.Fact == FleetOutliers.Facts.CentralPackageManagement);
    }
}
