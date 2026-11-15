using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.Tests.ViewModels;

public class NewsItemViewModelTest
{
    [TestCase("<h2>What is new</h2><p>Faster   loading &amp; fixes.</p>\n<p>More <a href=\"x\">here</a>.</p>", "Faster loading & fixes. More here.")]
    [TestCase("<p>One line</p>", "One line")]
    [TestCase("", "")]
    public void ToExcerpt_LeavesPlainTextWithoutHeadings(string html, string expected)
    {
        Assert.That(NewsItemViewModel.ToExcerpt(html), Is.EqualTo(expected));
    }
}
