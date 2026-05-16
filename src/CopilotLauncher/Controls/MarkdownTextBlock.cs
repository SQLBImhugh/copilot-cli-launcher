using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace CopilotLauncher.Controls
{
public sealed class MarkdownTextBlock : UserControl
{
    // Defaults used when no theme override is in play. ThemeManager populates
    // ContentControlThemeFontFamily on Application.Resources for our themed
    // body text and CliMonoFontFamily for code blocks; we look those up at
    // render time so the markdown follows the active palette.
    private static readonly FontFamily DefaultFontFamily = new("Segoe UI Variable Text");
    private static readonly FontFamily MonospaceFontFamily = new("Cascadia Mono, Consolas");

    private readonly RichTextBlock _richTextBlock;
    private string _sourceMarkdown = string.Empty;

    public MarkdownTextBlock()
    {
        _richTextBlock = new RichTextBlock
        {
            FontFamily = ResolveBodyFont(),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };

        Content = _richTextBlock;
    }

    private static FontFamily ResolveBodyFont()
    {
        var app = Application.Current;
        if (app?.Resources is null) return DefaultFontFamily;
        return app.Resources.TryGetValue("ContentControlThemeFontFamily", out var v) && v is FontFamily ff
            ? ff : DefaultFontFamily;
    }

    private static FontFamily ResolveCodeFont()
    {
        var app = Application.Current;
        if (app?.Resources is null) return MonospaceFontFamily;
        return app.Resources.TryGetValue("CliMonoFontFamily", out var v) && v is FontFamily ff
            ? ff : MonospaceFontFamily;
    }

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownTextBlock),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MarkdownTextBlock markdownTextBlock)
        {
            markdownTextBlock.RenderMarkdown(args.NewValue as string);
        }
    }

    private void RenderMarkdown(string? markdown)
    {
        _sourceMarkdown = markdown ?? string.Empty;

        try
        {
            Content = _richTextBlock;
            _richTextBlock.Blocks.Clear();
            // Re-resolve in case the user toggled themes after the control
            // was constructed (the resource value moves with the theme).
            _richTextBlock.FontFamily = ResolveBodyFont();

            if (string.IsNullOrWhiteSpace(_sourceMarkdown))
            {
                return;
            }

            var document = Markdig.Markdown.Parse(_sourceMarkdown);
            foreach (var block in document)
            {
                RenderBlock(block);
            }

            if (_richTextBlock.Blocks.Count == 0)
            {
                _richTextBlock.Blocks.Add(CreatePlainParagraph(_sourceMarkdown));
            }
        }
        catch
        {
            Content = new TextBlock
            {
                Text = _sourceMarkdown,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
        }
    }

    private void RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock headingBlock:
                _richTextBlock.Blocks.Add(CreateHeadingParagraph(headingBlock));
                break;
            case ParagraphBlock paragraphBlock:
                _richTextBlock.Blocks.Add(CreateParagraph(paragraphBlock));
                break;
            case FencedCodeBlock fencedCodeBlock:
                _richTextBlock.Blocks.Add(CreateCodeParagraph(fencedCodeBlock));
                break;
            case CodeBlock codeBlock:
                _richTextBlock.Blocks.Add(CreateCodeParagraph(codeBlock));
                break;
            case ListBlock listBlock:
                RenderListBlock(listBlock);
                break;
            case ThematicBreakBlock:
                _richTextBlock.Blocks.Add(CreateThematicBreakParagraph());
                break;
            default:
                var fallbackText = GetSourceText(block);
                if (!string.IsNullOrWhiteSpace(fallbackText))
                {
                    _richTextBlock.Blocks.Add(CreatePlainParagraph(fallbackText));
                }
                break;
        }
    }

    private Paragraph CreateHeadingParagraph(HeadingBlock headingBlock)
    {
        var paragraph = new Paragraph
        {
            FontSize = headingBlock.Level switch
            {
                <= 1 => 20,
                2 => 17,
                3 => 15,
                _ => 14
            },
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 4)
        };

        AppendInlines(paragraph.Inlines, headingBlock.Inline);
        EnsureParagraphHasText(paragraph, headingBlock);
        return paragraph;
    }

    private Paragraph CreateParagraph(ParagraphBlock paragraphBlock)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 6)
        };

        AppendInlines(paragraph.Inlines, paragraphBlock.Inline);
        EnsureParagraphHasText(paragraph, paragraphBlock);
        return paragraph;
    }

    private Paragraph CreateCodeParagraph(CodeBlock codeBlock)
    {
        var paragraph = new Paragraph
        {
            FontFamily = ResolveCodeFont(),
            Margin = new Thickness(8, 4, 0, 8)
        };

        paragraph.Inlines.Add(new Run
        {
            FontFamily = ResolveCodeFont(),
            Text = codeBlock.Lines.ToString() ?? string.Empty
        });

        return paragraph;
    }

    private Paragraph CreateThematicBreakParagraph()
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 8)
        };

        paragraph.Inlines.Add(new Run
        {
            Text = "─────"
        });

        return paragraph;
    }

    private Paragraph CreatePlainParagraph(string text)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 6)
        };

        paragraph.Inlines.Add(new Run
        {
            Text = text
        });

        return paragraph;
    }

    private void RenderListBlock(ListBlock listBlock)
    {
        foreach (var block in listBlock)
        {
            if (block is not ListItemBlock listItemBlock)
            {
                continue;
            }

            var renderedAny = false;
            foreach (var childBlock in listItemBlock)
            {
                Paragraph paragraph;
                if (childBlock is ParagraphBlock paragraphBlock)
                {
                    paragraph = new Paragraph
                    {
                        Margin = new Thickness(16, 0, 0, 4)
                    };

                    paragraph.Inlines.Add(new Run { Text = "•  " });
                    AppendInlines(paragraph.Inlines, paragraphBlock.Inline);
                    EnsureParagraphHasText(paragraph, paragraphBlock, "•  ");
                }
                else
                {
                    var fallbackText = GetSourceText(childBlock);
                    if (string.IsNullOrWhiteSpace(fallbackText))
                    {
                        continue;
                    }

                    paragraph = new Paragraph
                    {
                        Margin = new Thickness(16, 0, 0, 4)
                    };

                    paragraph.Inlines.Add(new Run { Text = $"•  {fallbackText}" });
                }

                _richTextBlock.Blocks.Add(paragraph);
                renderedAny = true;
            }

            if (!renderedAny)
            {
                _richTextBlock.Blocks.Add(CreatePlainParagraph("•"));
            }
        }
    }

    private void AppendInlines(InlineCollection inlines, ContainerInline? containerInline)
    {
        if (containerInline is null)
        {
            return;
        }

        for (var inline = containerInline.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literalInline:
                    inlines.Add(new Run
                    {
                        Text = literalInline.Content.ToString() ?? string.Empty
                    });
                    break;
                case LineBreakInline:
                    inlines.Add(new LineBreak());
                    break;
                case CodeInline codeInline:
                    inlines.Add(CreateCodeInline(codeInline));
                    break;
                case EmphasisInline emphasisInline:
                    Span emphasisSpan = emphasisInline.DelimiterCount >= 2
                        ? new Bold()
                        : new Italic();
                    AppendInlines(emphasisSpan.Inlines, emphasisInline);
                    inlines.Add(emphasisSpan);
                    break;
                case LinkInline linkInline:
                    var linkSpan = new Span();
                    AppendInlines(linkSpan.Inlines, linkInline);
                    if (linkSpan.Inlines.Count == 0 && !string.IsNullOrWhiteSpace(linkInline.Url))
                    {
                        linkSpan.Inlines.Add(new Run { Text = linkInline.Url });
                    }

                    inlines.Add(linkSpan);
                    break;
                case ContainerInline childContainerInline:
                    var span = new Span();
                    AppendInlines(span.Inlines, childContainerInline);
                    inlines.Add(span);
                    break;
                default:
                    var fallbackText = GetSourceText(inline);
                    if (!string.IsNullOrWhiteSpace(fallbackText))
                    {
                        inlines.Add(new Run { Text = fallbackText });
                    }
                    break;
            }
        }
    }

    private static Span CreateCodeInline(CodeInline codeInline)
    {
        var span = new Span
        {
            FontFamily = ResolveCodeFont()
        };

        span.Inlines.Add(new Run
        {
            FontFamily = ResolveCodeFont(),
            Text = codeInline.Content ?? string.Empty
        });

        return span;
    }

    private void EnsureParagraphHasText(Paragraph paragraph, MarkdownObject markdownObject, string prefix = "")
    {
        if (paragraph.Inlines.Count > 0)
        {
            return;
        }

        var fallbackText = prefix + GetSourceText(markdownObject);
        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            paragraph.Inlines.Add(new Run { Text = fallbackText });
        }
    }

    private string GetSourceText(MarkdownObject markdownObject)
    {
        if (string.IsNullOrEmpty(_sourceMarkdown))
        {
            return string.Empty;
        }

        var start = markdownObject.Span.Start;
        var end = markdownObject.Span.End;
        if (start < 0 || end < start)
        {
            return string.Empty;
        }

        end = Math.Min(end, _sourceMarkdown.Length - 1);
        if (end < start)
        {
            return string.Empty;
        }

        return _sourceMarkdown.Substring(start, end - start + 1);
    }
}

}

namespace Windows.UI.Text
{
    internal static class FontWeights
    {
        public static FontWeight Bold => new() { Weight = 700 };

        public static FontWeight SemiBold => new() { Weight = 600 };
    }
}
