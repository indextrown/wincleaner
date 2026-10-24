using System.Text;
using WinCleaner.Cleaning;
using WinCleaner.Core;

namespace WinCleaner.Tests;

public class RuleCatalogTests
{
    [Fact]
    public void Built_in_rules_load_with_unique_ids_and_low_risk()
    {
        var rules = RuleCatalog.LoadBuiltIn();

        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r =>
        {
            Assert.Equal(RiskLevel.Low, r.Risk);
            Assert.NotEmpty(r.Targets);
        });
    }

    [Fact]
    public void Every_built_in_folder_target_passes_the_guard()
    {
        var guard = PathGuard.ForCurrentUser();

        foreach (var target in RuleCatalog.LoadBuiltIn().SelectMany(r => r.Targets))
        {
            var isExactFile = !target.Recursive && !target.Pattern.Contains('*');
            if (isExactFile)
                continue;

            // 와일드카드 폴더는 대표 이름으로 바꿔 검사한다.
            var folder = Environment.ExpandEnvironmentVariables(target.Path).Replace("*", "Default");
            Assert.True(guard.IsAllowedRoot(folder, out var reason), $"{target.Path}: {reason}");
        }
    }

    [Fact]
    public void Parse_rejects_duplicate_ids()
    {
        const string json = """
            { "rules": [
              { "id": "a", "name": "A", "description": "", "category": "x", "targets": [ { "path": "C:\\a" } ] },
              { "id": "a", "name": "B", "description": "", "category": "x", "targets": [ { "path": "C:\\b" } ] }
            ] }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => RuleCatalog.Parse(stream));
    }
}
