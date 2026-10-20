namespace Accel.Tests;

using Accel.App.Controls;
using Xunit;

/// <summary>
/// Pins the markdown pipeline behind the FILE/GIT preview: Markdig's default pipeline is CommonMark
/// only, so GitHub-style pipe tables used to render as literal "| a | b |" paragraphs.
/// </summary>
public class MarkdownPreviewRenderTests
{
    [Fact]
    public void PipeTable_RendersAsHtmlTable()
    {
        const string md = "| Name | Value |\n|------|:-----:|\n| a | 1 |\n| b | 2 |\n";

        string html = MarkdownPreviewView.RenderMarkdown(md);

        Assert.Contains("<table>", html);
        Assert.Contains("<th>Name</th>", html);
        Assert.Contains("<td>b</td>", html);
        Assert.Contains(">2</td>", html); // second column is centre-aligned, so the tag carries a style attribute
        Assert.DoesNotContain("|------|", html);
    }

    [Fact]
    public void TaskListAndStrikethrough_Render()
    {
        string html = MarkdownPreviewView.RenderMarkdown("- [x] done\n\n~~gone~~\n");

        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("<del>gone</del>", html);
    }
}
