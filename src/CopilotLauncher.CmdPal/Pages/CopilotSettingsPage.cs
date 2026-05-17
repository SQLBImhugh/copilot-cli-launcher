using System;
using System.Collections.Generic;
using System.Text.Json;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal.Pages;

/// <summary>
/// In-palette editor for the default flags applied by one-click session resume.
/// This mirrors the main app's Sessions Resume settings without opening WinUI.
/// </summary>
public sealed partial class CopilotSettingsPage : ContentPage
{
    private readonly ISettingsService _settings;

    public CopilotSettingsPage(ISettingsService settings)
    {
        _settings = settings;

        Name = "Settings";
        Title = "Copilot resume defaults";
        Icon = new IconInfo("\uE713");
    }

    public override IContent[] GetContent() =>
        new IContent[]
        {
            new ResumeDefaultsForm(_settings),
        };

    private sealed partial class ResumeDefaultsForm : FormContent
    {
        private readonly ISettingsService _settings;

        public ResumeDefaultsForm(ISettingsService settings)
        {
            _settings = settings;
            var resume = settings.Current.SessionsResume;

            DataJson = JsonSerializer.Serialize(new
            {
                enableAISummary = resume.EnableAISummary ? "true" : "false",
                enableAllowAll = resume.EnableAllowAll ? "true" : "false",
                extraCopilotArgs = resume.ExtraCopilotArgs ?? string.Empty,
            });
            StateJson = DataJson;
            TemplateJson = BuildTemplate(resume);
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            try
            {
                using var document = JsonDocument.Parse(inputs);
                var root = document.RootElement;
                var resume = _settings.Current.SessionsResume;

                resume.EnableAISummary = ReadBool(root, "enableAISummary");
                resume.EnableAllowAll = ReadBool(root, "enableAllowAll");
                resume.ExtraCopilotArgs = NullIfWhiteSpace(ReadString(root, "extraCopilotArgs"));

                _settings.Save();
                return CommandResult.GoBack();
            }
            catch
            {
                return CommandResult.KeepOpen();
            }
        }

        private static string BuildTemplate(SessionsResumeSettings resume)
        {
            var card = new Dictionary<string, object?>
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["type"] = "AdaptiveCard",
                ["version"] = "1.5",
                ["body"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock",
                        ["text"] = "Resume defaults",
                        ["weight"] = "Bolder",
                        ["wrap"] = true,
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "Input.Toggle",
                        ["id"] = "enableAISummary",
                        ["title"] = "AI summary on resume",
                        ["valueOn"] = "true",
                        ["valueOff"] = "false",
                        ["value"] = resume.EnableAISummary ? "true" : "false",
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "Input.Toggle",
                        ["id"] = "enableAllowAll",
                        ["title"] = "Pass --allow-all on resume",
                        ["valueOn"] = "true",
                        ["valueOff"] = "false",
                        ["value"] = resume.EnableAllowAll ? "true" : "false",
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "Input.Text",
                        ["id"] = "extraCopilotArgs",
                        ["label"] = "Extra copilot args on resume",
                        ["isMultiline"] = false,
                        ["value"] = resume.ExtraCopilotArgs ?? string.Empty,
                    },
                },
                ["actions"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "Action.Submit",
                        ["title"] = "Save",
                    },
                },
            };

            return JsonSerializer.Serialize(card);
        }

        private static bool ReadBool(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        private static string? ReadString(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : null;
        }

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
