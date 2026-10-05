using FanslationStudio.Plugins.Support;

namespace FanslationStudio.Plugins.Tests
{
    public class PathPatternTests
    {
        [Theory]
        // Global resizer convention used by every shipped game
        [InlineData("/*", "Canvas/AreaUIPanel/HeroName", true)]
        [InlineData("/*", "Root", true)]
        // Leading slash = any depth
        [InlineData("/Title/Text", "UI/Layer_3/LibraryPanel/Panels/Legend/Title/Text", true)]
        [InlineData("/Title/Text", "Title/Text", true)]
        [InlineData("/Title/Text", "UI/SubTitle/Text", false)]
        // Wildcards span segments and partial names
        [InlineData("Canvas/AreaUIPanel/AreaUIBelow/AreaHeroScrollView/Viewport/Content/*/HeroName",
                    "Canvas/AreaUIPanel/AreaUIBelow/AreaHeroScrollView/Viewport/Content/HeroIconPrefab(Clone)/HeroName", true)]
        [InlineData("MainMap/*/Tooltip_*/*/Title", "MainMap/Root/Tooltip_Area/Content/Title", true)]
        [InlineData("UI/Layer_2/LoadGamePanel/Container/*/Slot/Header*", "UI/Layer_2/LoadGamePanel/Container/Slot1/Slot/HeaderText", true)]
        [InlineData("Canvas/PopRoot/CreateMenu/HeroBox/Radar/NameContainer/Text (TMP)*",
                    "Canvas/PopRoot/CreateMenu/HeroBox/Radar/NameContainer/Text (TMP) (3)", true)]
        // Regex metacharacters are literal
        [InlineData("[UI]/MainUI/Layer_*/ActionInfo/ContributionValue", "[UI]/MainUI/Layer_2/ActionInfo/ContributionValue", true)]
        [InlineData("[UI]/MainUI/Layer_*/ActionInfo/ContributionValue", "U/MainUI/Layer_2/ActionInfo/ContributionValue", false)]
        [InlineData("Canvas/a.b/*", "Canvas/axb/Text", false)]
        [InlineData("Canvas/a+b/*", "Canvas/a+b/Text", true)]
        [InlineData("[UI]/*/StatusPanel/Container/Buttons/LeftButtons/StatusButton_*/*/Text (TMP)",
                    "[UI]/MainUI/StatusPanel/Container/Buttons/LeftButtons/StatusButton_Items/Label/Text (TMP)", true)]
        // Anchored: no longer matches mid-path or children
        [InlineData("UI/Layer_1/Buttons/*/Text", "[UI]/MainUI/Layer_1/Buttons/B/Text", false)]
        [InlineData("Canvas/*/Title", "Canvas/Panel/Title/Child", false)]
        [InlineData("Canvas/*/Title*", "Canvas/Panel/Title/Child", true)]
        // Non-wildcard patterns are exact
        [InlineData("Canvas/Vertical/TitleText", "Canvas/Vertical/TitleText", true)]
        [InlineData("Canvas/Vertical/TitleText", "Canvas/Vertical/TitleText2", false)]
        public void IsMatch(string pattern, string path, bool expected)
        {
            Assert.Equal(expected, PathPattern.IsMatch(pattern, path));
        }

        [Theory]
        [InlineData("/*", true)]
        [InlineData("/Title", true)]
        [InlineData("Canvas/*/Title", true)]
        [InlineData("Canvas/Title", false)]
        public void IsWildcard(string pattern, bool expected)
        {
            Assert.Equal(expected, PathPattern.IsWildcard(pattern));
        }

        [Theory]
        [InlineData("Canvas/Vertical/TitleText", "TitleText")]
        [InlineData("/Title/Text", "Text")]
        [InlineData("/Title", "Title")]
        [InlineData("Canvas/*/Title", "Title")]
        [InlineData("A*/Title", "Title")]
        [InlineData("Root", "Root")]
        [InlineData("/*", null)]
        [InlineData("Canvas/*", null)]
        [InlineData("Canvas/*/Title*", null)]
        [InlineData("A*B", null)]
        public void LeafNameOf(string pattern, string? expected)
        {
            Assert.Equal(expected, PathPattern.LeafNameOf(pattern));
        }

        [Fact]
        public void NullsNeverMatch()
        {
            Assert.False(PathPattern.IsMatch(null!, "a"));
            Assert.False(PathPattern.IsMatch("a", null!));
        }
    }
}
