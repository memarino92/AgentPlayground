using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using PersonalAgent.Mobile.Services;
using PersonalAgent.Presentation;

namespace PersonalAgent.Mobile;

public partial class MainPage
{
    private static readonly MarkdownPipeline MessagePipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    private void OnMessageTextChanged(object? Sender, EventArgs Args)
    {
        if (Sender is not Label label) return;
        label.FormattedText = null;
        label.Text = null;
        if (label.BindingContext is not NativeChatMessage message) return;
        if (message.IsUser) label.Text = message.Content;
        else label.FormattedText = FormatReply(message.Content);
    }

    private FormattedString FormatReply(string Text)
    {
        var result = new FormattedString();
        void Add(string value, FontAttributes style = FontAttributes.None, bool code = false, string? url = null)
        {
            if (value.Length == 0) return;
            var span = new Span { Text = value, FontAttributes = style };
            if (code) span.FontFamily = "monospace";
            if (url is not null && LinkAction(url) is { } open)
            {
                span.TextDecorations = TextDecorations.Underline;
                span.SetAppThemeColor(Span.TextColorProperty, Color.FromArgb("#116454"), Color.FromArgb("#98DBC8"));
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) =>
                {
                    try { await open(); }
                    catch (Exception) { await DisplayAlertAsync("Link unavailable", "This link could not be opened.", "OK"); }
                };
                span.GestureRecognizers.Add(tap);
            }
            result.Spans.Add(span);
        }

        void Inline(Markdig.Syntax.Inlines.Inline item, FontAttributes style, string? url)
        {
            switch (item)
            {
                case LiteralInline literal: Add(literal.Content.ToString(), style, url: url); break;
                case CodeInline code: Add(code.Content, style, true, url); break;
                case LineBreakInline: Add("\n"); break;
                case HtmlEntityInline entity: Add(entity.Transcoded.ToString(), style, url: url); break;
                case AutolinkInline link: Add(link.Url, style, url: link.Url); break;
                case ContainerInline group:
                    if (group is EmphasisInline emphasis)
                    {
                        if (emphasis.DelimiterCount >= 2) style |= FontAttributes.Bold;
                        if (emphasis.DelimiterCount % 2 == 1) style |= FontAttributes.Italic;
                    }
                    if (group is LinkInline linkGroup && !linkGroup.IsImage) url = linkGroup.Url;
                    foreach (var child in group) Inline(child, style, url);
                    break;
            }
        }

        void BlockContent(Block block)
        {
            switch (block)
            {
                case ListBlock list:
                    var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var item in list)
                    {
                        if (item != list[0]) Add("\n");
                        Add(list.IsOrdered ? $"{number++}. " : "• ");
                        BlockContent(item);
                    }
                    break;
                case ContainerBlock container:
                    foreach (var child in container)
                    {
                        if (child != container[0]) Add("\n\n");
                        BlockContent(child);
                    }
                    break;
                case CodeBlock code: Add(code.Lines.ToString(), code: true); break;
                case LeafBlock { Inline: { } inline }: Inline(inline, block is HeadingBlock ? FontAttributes.Bold : FontAttributes.None, null); break;
                case ThematicBreakBlock: Add("—"); break;
            }
        }

        BlockContent(Markdown.Parse(Text, MessagePipeline));
        return result;
    }

    private Func<Task>? LinkAction(string Url)
    {
        if (EvidenceCitation.FromUrl(Url, _chat.Profile) is { } citation && citation.ProfileId == _chat.Profile)
            return () => Navigation.PushAsync(new WorkspacePage(_chat, "Recording evidence", citation.UploadId, StartMs: citation.StartMs));
        var screen = Url.Split('?', '#')[0] switch
        {
            "/coding-jobs" when _chat.IsOwner => "Coding jobs",
            "/jobs" => "Scheduled jobs",
            "/automations" => "Automations",
            "/coach-transcripts" or "/coach-checkins/admin" => "Coach recordings",
            "/admin/settings" when _chat.IsOwner => "Administration",
            _ => null
        };
        if (screen is not null) return () => Navigation.PushAsync(new WorkspacePage(_chat, screen));
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            return () => Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
        return null;
    }
}
