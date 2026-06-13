using Re0Agent.Core.Services.Agent;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Tests;

public sealed class CharacterNameResolverTests
{
    private static async Task<IReadOnlyList<CharacterNameResolver.AliasGroup>> LoadGroupsAsync()
    {
        var ragService = new BlackTeaRagService(new ChapterVariantRenderer());
        var resolver = new CharacterNameResolver(ragService);
        return await resolver.LoadGroupsAsync();
    }

    [Fact]
    public async Task ResolvesAliasToCanonicalName()
    {
        var groups = await LoadGroupsAsync();

        // 罗兹瓦尔 是 罗兹瓦尔·L·梅瑟斯 的别名（来自世界书 Keys）。
        Assert.Equal("罗兹瓦尔·L·梅瑟斯", CharacterNameResolver.ResolveCanonical(groups, "罗兹瓦尔"));
        // 贝蒂 / 贝阿特丽丝 都指向 碧翠丝。
        Assert.Equal("碧翠丝", CharacterNameResolver.ResolveCanonical(groups, "贝蒂"));
        Assert.Equal("碧翠丝", CharacterNameResolver.ResolveCanonical(groups, "贝阿特丽丝"));
    }

    [Fact]
    public async Task TreatsAliasAndFullNameAsSameCharacter()
    {
        var groups = await LoadGroupsAsync();

        Assert.True(CharacterNameResolver.IsSameCharacter(groups, "罗兹瓦尔", "罗兹瓦尔·L·梅瑟斯"));
        Assert.True(CharacterNameResolver.IsSameCharacter(groups, "贝蒂", "碧翠丝"));
        // 敬称应被剥离后仍能归一。
        Assert.True(CharacterNameResolver.IsSameCharacter(groups, "罗兹瓦尔大人", "罗兹瓦尔·L·梅瑟斯"));
    }

    [Fact]
    public async Task DistinguishesDifferentCharacters()
    {
        var groups = await LoadGroupsAsync();

        Assert.False(CharacterNameResolver.IsSameCharacter(groups, "罗兹瓦尔", "碧翠丝"));
        Assert.False(CharacterNameResolver.IsSameCharacter(groups, "雷姆", "拉姆"));
    }

    [Fact]
    public async Task ReturnsNullForUnknownName()
    {
        var groups = await LoadGroupsAsync();

        Assert.Null(CharacterNameResolver.ResolveCanonical(groups, "完全不存在的路人甲"));
    }
}
