using System.Text.Json;
using System.Text.Json.Serialization;
using WinCleaner.Core.Cleaning;

namespace WinCleaner.Cleaning;

public static class RuleCatalog
{
    private const string ResourceName = "cleanup-rules.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>프로그램에 포함된 기본 규칙으로 청소 규칙 목록을 만든다.</summary>
    public static IReadOnlyList<ICleanupRule> CreateBuiltInRules()
    {
        var guard = PathGuard.ForCurrentUser();
        var rules = LoadBuiltIn().Select(d => (ICleanupRule)new FileCleanupRule(d, guard)).ToList();

        // 파일 규칙으로 표현할 수 없는 항목
        rules.Insert(0, new RecycleBinRule());
        return rules;
    }

    public static IReadOnlyList<RuleDefinition> LoadBuiltIn()
    {
        using var stream = typeof(RuleCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("기본 청소 규칙을 찾을 수 없습니다.");
        return Parse(stream);
    }

    public static IReadOnlyList<RuleDefinition> Parse(Stream json)
    {
        var file = JsonSerializer.Deserialize<RuleFile>(json, JsonOptions)
            ?? throw new InvalidDataException("청소 규칙 파일이 비어 있습니다.");

        var duplicate = file.Rules.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"청소 규칙 ID가 중복됩니다: {duplicate.Key}");

        return file.Rules;
    }

    private sealed class RuleFile
    {
        public required IReadOnlyList<RuleDefinition> Rules { get; init; }
    }
}
